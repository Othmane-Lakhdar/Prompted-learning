using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using PromptedLearning.Core;

namespace PromptedLearning.Managed;

/// <summary>Captured-source Roslyn adapter for the frozen C# pilot; source semantics only.</summary>
public sealed class ManagedPilotAdapter
{
    public const string AdapterName = "prompted-learning-roslyn-adapter";
    public const string AdapterVersion = "1";
    public const string RoslynVersion = "4.14.0.0";

    public static IReadOnlyList<AnalysisToolRequest> GetAnalysisTools()
    {
        if (typeof(Compilation).Assembly.GetName().Version?.ToString() != RoslynVersion ||
            typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() != RoslynVersion)
            throw new NotSupportedException("The managed adapter requires Roslyn 4.14.0.");
        return new[] { ("analysis:managed-adapter", typeof(ManagedPilotAdapter).Assembly.Location),
            ("analysis:roslyn-common", typeof(Compilation).Assembly.Location),
            ("analysis:roslyn-csharp", typeof(CSharpCompilation).Assembly.Location) }
            .Select(value => new AnalysisToolRequest(value.Item1, value.Item2,
                FileVersionInfo.GetVersionInfo(value.Item2).ProductVersion ?? throw new InvalidDataException("Analysis tool has no product version."))).ToArray();
    }

    public static CompilerProducer ProducerFromEvaluation(MsBuildEvaluation evaluation)
    {
        var adapter = ExactlyOne(evaluation.Tools.Where(tool => tool.Descriptor.GetProperty("name").GetString() == "analysis:managed-adapter"),
            "Expected exactly one declared managed adapter tool.");
        return new(AdapterName, AdapterVersion, RoslynVersion, adapter.Descriptor.GetProperty("content_fingerprint").GetProperty("value").GetString()!);
    }

    public static string SemanticInputFingerprint(ManagedAnalysisRequest request)
        => CanonicalJson.Sha256(CanonicalJson.Serialize(new object[] { "managed-semantic-input/1", new
        {
            snapshot_id = request.Capture.SnapshotId, context_id = request.ContextId, unit_id = request.UnitId,
            capture_index_fingerprint = request.Capture.IndexFingerprint, producer = request.Producer,
            files = request.Sources.Select(source =>
            {
                var file = SourceFile(request, source.FileVersionId);
                return new { key = source.FileKey, file_version_id = source.FileVersionId, logical_path = file.LogicalPath,
                    original_sha256 = file.ContentHash, byte_length = file.ByteLength, encoding = file.Encoding.Name, bom_bytes = file.Encoding.BomBytes };
            }).ToArray(),
            requested_features = new[] { "source_semantics" }, expected_consumed_file_keys = request.ExpectedConsumedFileKeys
        } }));

    public async Task<SemanticUnitResult> AnalyzeAsync(ManagedAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        if (request.TimeoutMs <= 0 || request.MaxSourceBytes <= 0 || request.MaxSourceBytes > 1048576)
            throw new ArgumentOutOfRangeException(nameof(request));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.TimeoutMs);
        var token = deadline.Token;
        var builder = new ManagedFactsBuilder(request);
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateScope(request);
            await VerifyToolsAsync(request, token);
            await using var archive = await SourceArchive.OpenAsync(request.Capture.ArchiveRoot, request.Capture.IndexFingerprint, token);
            if (archive.Index.GetProperty("snapshot_id").GetString() != request.Capture.SnapshotId)
                throw new InvalidDataException("Managed capture scope differs from its verified index.");
            var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp12, DocumentationMode.Parse,
                preprocessorSymbols: request.Evaluation.Properties["DefineConstants"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var trees = new List<SyntaxTree>();
            long totalBytes = 0;
            foreach (var source in request.Sources)
            {
                token.ThrowIfCancellationRequested();
                var file = SourceFile(request, source.FileVersionId);
                var length = file.ByteLength ?? throw new InvalidDataException("Source byte length is unavailable.");
                if (length > request.MaxSourceBytes - totalBytes) throw new InvalidDataException("managed_source_byte_limit");
                totalBytes += length;
                var read = await archive.ReadBytesAsync(file.FileVersionId, 0, length, request.MaxSourceBytes, token);
                var decoded = SourceDecoder.Decode(read.Bytes);
                if (decoded.Status != "valid" || decoded.Source is null) throw new InvalidDataException("Managed input cannot be decoded strictly.");
                var tree = CSharpSyntaxTree.ParseText(SourceText.From(decoded.Source.Text, Encoding.UTF8), parseOptions,
                    file.LogicalPath, cancellationToken: token);
                trees.Add(tree);
                builder.AddSource(source.FileKey, file, tree, decoded.Source);
            }
            var references = new List<MetadataReference>();
            foreach (var provider in request.Evaluation.Providers)
            {
                token.ThrowIfCancellationRequested();
                var bytes = await ReadVerifiedAsync(provider.PhysicalPath, provider.Descriptor.GetProperty("content_fingerprint"), token);
                references.Add(MetadataReference.CreateFromImage(ImmutableArray.CreateRange(bytes), filePath:
                    provider.Descriptor.GetProperty("root_key").GetString() + "/" + provider.Descriptor.GetProperty("relative_location").GetString()));
            }
            var properties = request.Evaluation.Properties;
            var compilation = CSharpCompilation.Create(properties["AssemblyName"], trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    checkOverflow: properties["CheckForOverflowUnderflow"] == "true", allowUnsafe: false, platform: Platform.X64,
                    warningLevel: int.Parse(properties["WarningLevel"], CultureInfo.InvariantCulture),
                    nullableContextOptions: NullableContextOptions.Enable, deterministic: properties["Deterministic"] == "true"));
            if (!compilation.SyntaxTrees.SequenceEqual(trees) || !compilation.References.SequenceEqual(references) ||
                !request.Sources.Select(source => source.FileKey).SequenceEqual(request.ExpectedConsumedFileKeys))
                throw new InvalidDataException("Managed declared input set differs from final compiler inputs.");
            var diagnostics = compilation.GetDiagnostics(token);
            builder.AddDiagnostics(diagnostics);
            if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                return Result(request, "failed", "compiler_errors", builder.Finish("failed", "compiler_errors"), []);
            builder.Extract(compilation, token);
            return Result(request, builder.IsPartial ? "partial" : "complete", builder.IsPartial ? "unsupported_semantics" : null,
                builder.Finish(builder.IsPartial ? "partial" : "complete", builder.IsPartial ? "unsupported_semantics" : null),
                request.Sources.Select(source => source.FileKey).ToArray());
        }
        catch (OperationCanceledException)
        {
            var reason = cancellationToken.IsCancellationRequested ? "cancelled" : "managed_time_limit";
            return Result(request, "cancelled", reason, builder.Failure(reason), []);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or BadImageFormatException or NotSupportedException)
        {
            return Result(request, "failed", "input_validation_failed", builder.Failure(error.Message), []);
        }
    }

    private static SemanticUnitResult Result(ManagedAnalysisRequest request, string status, string? reason, JsonElement facts, IReadOnlyList<string> consumed)
        => new(request.Capture.SnapshotId, request.ContextId, request.UnitId, request.Capture.IndexFingerprint,
            request.SemanticInputFingerprint, request.Producer, status, reason, facts, consumed);

    private static void ValidateScope(ManagedAnalysisRequest request)
    {
        if (request.Evaluation.Language != "csharp") throw new NotSupportedException("Managed adapter requires the C# pilot context.");
        PilotInputGuard.ValidateCapture(request.Evaluation, request.Capture.Inputs);
        if (request.Sources.Count != 2 || request.Sources.Select(source => source.FileKey).Distinct(StringComparer.Ordinal).Count() != 2 ||
            request.Sources.Any(source => !System.Text.RegularExpressions.Regex.IsMatch(source.FileKey, "^file:[A-Za-z0-9_./-]+$") ||
                System.Text.RegularExpressions.Regex.IsMatch(source.FileKey, "^file:[0-9a-f]{64}$")))
            throw new InvalidDataException("Managed source keys are invalid or duplicated.");
        var files = request.Sources.Select(source => SourceFile(request, source.FileVersionId)).ToArray();
        var selected = request.Evaluation.Items.Where(item => item.Kind == "Compile").Select(item => Path.GetFullPath(item.Metadata["FullPath"]));
        if (!selected.SequenceEqual(files.Select(file => Path.GetFullPath(file.OriginalPath)), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Managed source inputs differ from evaluated compile order.");
        var projectFile = ExactlyOne(request.Capture.Inputs.Files.Where(file => Path.GetFullPath(file.OriginalPath).Equals(Path.GetFullPath(request.Evaluation.ProjectPath), StringComparison.OrdinalIgnoreCase)),
            "Expected exactly one captured managed project.");
        var project = StableId.Create("project/1", new { snapshot_id = request.Capture.SnapshotId,
            definition_file_version_id = projectFile.FileVersionId, system_kind = "msbuild", logical_project_key = projectFile.LogicalPath });
        var context = MsBuildEvaluator.ContextComponents(request.Evaluation, request.Capture.SnapshotId, project);
        if (request.ContextId != StableId.Create("build_context/1", context)) throw new InvalidDataException("Managed context identity mismatch.");
        var inputs = files.Select(file => (object)new { role = "source", file_version_id = file.FileVersionId, provider = (object?)null })
            .Concat(request.Evaluation.Providers.Select(provider => (object)new { role = "reference", file_version_id = (string?)null, provider = provider.Descriptor })).ToArray();
        var unit = StableId.Create("compilation_unit/1", new { context_id = request.ContextId, language = "csharp", inputs,
            output_identity = request.Evaluation.Properties["AssemblyName"] });
        if (request.UnitId != unit || request.SemanticInputFingerprint != SemanticInputFingerprint(request))
            throw new InvalidDataException("Managed unit or semantic input fingerprint mismatch.");
        if (!request.ExpectedConsumedFileKeys.SequenceEqual(request.Sources.Select(source => source.FileKey)))
            throw new InvalidDataException("Managed expected consumed input order mismatch.");
        if (request.Producer != ProducerFromEvaluation(request.Evaluation)) throw new InvalidDataException("Managed producer mismatch.");
    }

    private static async Task VerifyToolsAsync(ManagedAnalysisRequest request, CancellationToken token)
    {
        foreach (var required in GetAnalysisTools())
        {
            var tool = ExactlyOne(request.Evaluation.Tools.Where(tool => tool.Descriptor.GetProperty("name").GetString() == required.Name),
                "Expected exactly one declared analysis tool: " + required.Name);
            if (!Path.GetFullPath(tool.PhysicalPath).Equals(Path.GetFullPath(required.PhysicalPath), StringComparison.OrdinalIgnoreCase)
                || tool.Descriptor.GetProperty("version").GetString() != required.ExpectedVersion)
                throw new InvalidDataException("Loaded analysis tool differs from evaluated tool identity.");
            _ = await ReadVerifiedAsync(tool.PhysicalPath, tool.Descriptor.GetProperty("content_fingerprint"), token);
        }
    }

    private static CapturedFile SourceFile(ManagedAnalysisRequest request, string version)
        => ExactlyOne(request.Capture.Inputs.Files.Where(file => file.FileVersionId == version), "Managed source version is missing or ambiguous.");

    private static T ExactlyOne<T>(IEnumerable<T> values, string reason)
    {
        var matches = values.Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException(reason);
    }

    private static async Task<byte[]> ReadVerifiedAsync(string path, JsonElement fingerprint, CancellationToken token)
    {
        if (fingerprint.GetProperty("state").GetString() != "known") throw new InvalidDataException("Compiler provider/tool fingerprint is unavailable.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length > 67108864) throw new InvalidDataException("Compiler provider/tool exceeds the pilot byte bound.");
        using var bytes = new MemoryStream();
        await file.CopyToAsync(bytes, token);
        var result = bytes.ToArray();
        if (CanonicalJson.Sha256(result) != fingerprint.GetProperty("value").GetString()) throw new InvalidDataException("Compiler provider/tool bytes changed after evaluation.");
        return result;
    }
}

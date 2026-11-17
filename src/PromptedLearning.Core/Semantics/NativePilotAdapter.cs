using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace PromptedLearning.Core;

public sealed record NativePilotAnalysis(ValidatedSemanticUnit Unit, JsonElement UnitIdentityInput,
    JsonElement DiscoveryRequest, JsonElement DiscoveryResult, JsonElement FinalRequest, JsonElement FinalResult);

/// <summary>Frozen MSBuild pilot lowering and captured-input closure; no working-source fallback.</summary>
public sealed class NativePilotAdapter
{
    public static IReadOnlyList<AnalysisToolRequest> GetAnalysisTools(string worker)
    {
        var host = typeof(NativePilotAdapter).Assembly.Location;
        if (FileVersionInfo.GetVersionInfo(worker).ProductVersion != "1") throw new InvalidDataException("Unrecognized native worker version.");
        return [new("analysis:native-worker", Path.GetFullPath(worker), "1"),
            new("analysis:native-host", host, FileVersionInfo.GetVersionInfo(host).ProductVersion ?? throw new InvalidDataException("Native host version unavailable."))];
    }

    public async Task<NativePilotAnalysis> AnalyzeAsync(MsBuildEvaluation evaluation, SealedCapture capture,
        string contextId, CancellationToken cancellationToken = default)
    {
        if (evaluation.Language != "cpp") throw new NotSupportedException("Native adapter requires the frozen C++ pilot.");
        PilotInputGuard.ValidateCapture(evaluation, capture.Inputs);
        var project = One(capture.Inputs.Files.Where(f => SamePath(f.OriginalPath, evaluation.ProjectPath)), "Captured native project");
        var projectId = StableId.Create("project/1", new { snapshot_id = capture.SnapshotId, definition_file_version_id = project.FileVersionId,
            system_kind = "msbuild", logical_project_key = project.LogicalPath });
        if (contextId != StableId.Create("build_context/1", MsBuildEvaluator.ContextComponents(evaluation, capture.SnapshotId, projectId)))
            throw new InvalidDataException("Native context does not bind evaluated inputs and tools.");
        var worker = One(evaluation.Tools.Where(t => t.Descriptor.GetProperty("name").GetString() == "analysis:native-worker"), "Native worker tool");
        var host = One(evaluation.Tools.Where(t => t.Descriptor.GetProperty("name").GetString() == "analysis:native-host"), "Native host tool");
        if (!SamePath(host.PhysicalPath, typeof(NativePilotAdapter).Assembly.Location)) throw new InvalidDataException("Native host assembly differs from evaluated tool.");
        foreach (var tool in evaluation.Tools) await VerifyTool(tool, cancellationToken);
        var mainItem = One(evaluation.Items.Where(i => i.Kind == "ClCompile"), "Native main item");
        var main = One(capture.Inputs.Files.Where(f => SamePath(f.OriginalPath, mainItem.Metadata["FullPath"])), "Native main capture");
        var headers = evaluation.Items.Where(i => i.Kind == "ClInclude").Select(i => One(capture.Inputs.Files.Where(f => SamePath(f.OriginalPath, i.Metadata["FullPath"])), "Native header capture")).ToArray();
        var selected = new[] { main }.Concat(headers).ToArray();
        if (selected.Select(f => f.FileVersionId).Distinct(StringComparer.Ordinal).Count() != selected.Length || selected.Any(f => f.RootName != main.RootName))
            throw new InvalidDataException("Native pilot inputs duplicate identities or cross capture roots.");
        await using var archive = await SourceArchive.OpenAsync(capture.ArchiveRoot, capture.IndexFingerprint, cancellationToken);
        if (archive.Index.GetProperty("snapshot_id").GetString() != capture.SnapshotId) throw new InvalidDataException("Native capture snapshot mismatch.");
        var files = new List<CompilerInputFile>(); long total = 0;
        foreach (var file in selected)
        {
            if (file.ByteLength is null || file.ByteLength > 1048576 - total) throw new InvalidDataException("Native pilot source budget exceeded.");
            total += file.ByteLength.Value;
            var read = await archive.ReadBytesAsync(file.FileVersionId, 0, file.ByteLength.Value, 1048576, cancellationToken);
            files.Add(new("file:native/" + files.Count, file.FileVersionId, file.LogicalPath, file.ContentHash!, read.Bytes, file.Encoding));
        }
        var producer = new CompilerProducer("prompted-learning-clang-worker", "1", "19.1.7", worker.Descriptor.GetProperty("content_fingerprint").GetProperty("value").GetString()!);
        var output = evaluation.Properties["TargetName"];
        JsonElement Identity(IReadOnlyList<string> keys)
        {
            var inputs = keys.Select(key => new { role = key == files[0].Key ? "main" : "included_header", file_version_id = files.Single(f => f.Key == key).FileVersionId, provider = (object?)null }).ToArray();
            return StableId.Envelope("compilation_unit/1", new { context_id = contextId, language = "cpp", inputs, output_identity = output });
        }
        var provisional = Identity([files[0].Key]);
        var provisionalId = StableId.Create("compilation_unit/1", provisional.GetProperty("components"));
        var discoveryRequest = Request("discovery", provisionalId, [files[0].Key]);
        var process = new NativeWorkerHost();
        var discovery = (await process.RunAsync(worker.PhysicalPath, discoveryRequest, cancellationToken)).WireResult;
        if (discovery.GetProperty("discovery_requests").GetArrayLength() != 0)
            throw new InvalidDataException("native_capture_expansion_required: return to T02 and seal a new snapshot; provisional facts were not accepted.");
        if (discovery.GetProperty("status").GetString() != "complete") throw new InvalidDataException("Native discovery did not complete.");
        var discovered = discovery.GetProperty("consumed_file_keys").EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal);
        if (!discovered.Contains(files[0].Key)) throw new InvalidDataException("Native discovery did not consume its main source.");
        // Stable main-first evaluated order; inclusion is established by the actual compiler, not ClInclude membership.
        var consumed = files.Where(f => discovered.Contains(f.Key)).Select(f => f.Key).ToArray();
        var identity = Identity(consumed); var unitId = StableId.Create("compilation_unit/1", identity.GetProperty("components"));
        var finalRequest = Request("final", unitId, consumed);
        var final = (await process.RunAsync(worker.PhysicalPath, finalRequest, cancellationToken)).WireResult;
        var result = NativeWorkerWire.ToSemanticResult(finalRequest, final);
        var binding = new SemanticUnitBinding("cpp", output, capture.SnapshotId, contextId, unitId, capture.IndexFingerprint,
            finalRequest.GetProperty("semantic_input_fingerprint").GetString()!, producer, files.AsReadOnly(), consumed,
            MaxRecords: 10000, MaxOperations: 10000, MaxResultBytes: 4194304);
        var validated = SemanticResultValidator.Validate(result, binding);
        return new(validated, identity, discoveryRequest, discovery, finalRequest, final);

        JsonElement Request(string phase, string unit, IReadOnlyList<string> expected)
        {
            var options = new List<object>();
            foreach (var literal in new[] { "-std=c++17", "-fms-extensions", "-fms-compatibility", "-fms-compatibility-version=19.44", "-DNDEBUG" }) options.Add(new { kind = "literal", value = literal });
            options.Add(new { kind = "path", option = "-I", root_key = main.RootName, relative_path = "" });
            var request = CompilerFacts.Element(new
            {
                schema = "native-worker-request/1", request_id = phase + ":" + unit, request_fingerprint = new string('0', 64),
                snapshot_id = capture.SnapshotId, context_id = contextId, unit_id = unit, capture_index_fingerprint = capture.IndexFingerprint,
                semantic_input_fingerprint = new string('0', 64), worker = new { name = producer.Name, version = producer.Version, clang_version = producer.CompilerVersion, binary_sha256 = producer.BinarySha256 },
                target = new { triple = "x86_64-pc-windows-msvc", language_standard = "c++17", ms_compatibility_version = "19.44" }, main_file_key = files[0].Key,
                files = files.Select(f => new { key = f.Key, file_version_id = f.FileVersionId, logical_path = f.LogicalPath, original_sha256 = f.OriginalSha256,
                    byte_length = f.Bytes.LongLength, encoding = f.Encoding.Name, bom_bytes = f.Encoding.BomBytes, bytes_base64 = Convert.ToBase64String(f.Bytes) }).ToArray(), options,
                path_bindings = new[] { new { root_key = main.RootName, role = "captured_input", physical_path = Path.GetDirectoryName(main.OriginalPath)!, logical_prefix = main.RootName } },
                budgets = new { time_ms = 30000, memory_bytes = 1073741824, max_input_bytes = 1048576, max_result_bytes = 4194304, max_records = 10000, max_operations = 10000 },
                requested_features = new[] { "source_semantics" }, phase, expected_consumed_file_keys = expected
            });
            var stamped = NativeWorkerWire.Stamp(request); ContractCatalog.Default.Validate("native-worker-request", stamped); return stamped;
        }
    }

    private static async Task VerifyTool(EvaluatedTool tool, CancellationToken cancellation)
    {
        var fingerprint = tool.Descriptor.GetProperty("content_fingerprint");
        if (fingerprint.GetProperty("state").GetString() != "known") throw new InvalidDataException("Native tool fingerprint is unavailable.");
        await using var input = new FileStream(tool.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellation)) != fingerprint.GetProperty("value").GetString())
            throw new InvalidDataException("Native tool changed after context evaluation.");
    }
    private static T One<T>(IEnumerable<T> values, string label) { var items = values.ToArray(); return items.Length == 1 ? items[0] : throw new InvalidDataException(label + " is missing or ambiguous."); }
    private static bool SamePath(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

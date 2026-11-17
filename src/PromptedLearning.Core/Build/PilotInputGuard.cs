using System.Security.Cryptography;
using System.Text.Json;

namespace PromptedLearning.Core;

/// <summary>Acceptance boundary for the two frozen M1 inputs, not general project support.</summary>
public static class PilotInputGuard
{
    private static readonly Lazy<JsonDocument> Manifest = new(() =>
    {
        using var stream = typeof(PilotInputGuard).Assembly.GetManifestResourceStream("PromptedLearning.Core.pilot.json")
            ?? throw new InvalidDataException("The frozen pilot manifest is not embedded.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return CanonicalJson.Parse(memory.ToArray());
    });
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static async Task ValidateFilesAsync(string projectPath, string configuration, string platform,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = Path.GetFullPath(projectPath);
        var context = Context(project);
        if (configuration != Text(context, "configuration") || platform != Text(context, "platform"))
            throw new NotSupportedException("Only the frozen Release/x64 pilot configuration is supported.");
        var relativeProject = Text(context, "project");
        var prefix = relativeProject[..(relativeProject.LastIndexOf('/') + 1)];
        var files = Manifest.Value.RootElement.GetProperty("files").EnumerateArray()
            .Where(file => Text(file, "path").StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (files.Length != 3) throw new InvalidDataException("The pilot contract must contain exactly three files per project.");
        foreach (var expected in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Text(expected, "path")[prefix.Length..];
            if (relative.Contains('/')) throw new InvalidDataException("The frozen pilot input is not a sibling file.");
            var path = Path.Combine(Path.GetDirectoryName(project)!, relative);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expected.GetProperty("bytes").GetInt64() ||
                Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)) != Text(expected, "sha256"))
                throw new InvalidDataException("Input differs from the frozen pilot: " + relative);
        }
    }

    public static void ValidateEvaluation(MsBuildEvaluation evaluation)
    {
        var context = Context(evaluation.ProjectPath);
        var language = Text(context, "language");
        if (evaluation.Language != language) throw new InvalidDataException("Evaluated language differs from the pilot.");
        var expectedProject = Manifest.Value.RootElement.GetProperty("files").EnumerateArray()
            .Single(file => Text(file, "path") == Text(context, "project"));
        if (evaluation.ProjectHash != Text(expectedProject, "sha256")) throw new InvalidDataException("Evaluated project fingerprint differs from the pilot.");
        Exact(evaluation.Properties, "Configuration", Text(context, "configuration"));
        Exact(evaluation.Properties, "Platform", Text(context, "platform"));
        Selected(evaluation, language == "csharp" ? "Compile" : "ClCompile", context.GetProperty("selected_compile_files"));
        if (language == "csharp") ValidateManaged(evaluation, context);
        else ValidateNative(evaluation, context);
    }

    /// <summary>Binds evaluated pilot assumptions to captured versions, without rereading working files.</summary>
    public static void ValidateCapture(MsBuildEvaluation evaluation, CapturedInputs capture)
    {
        ValidateEvaluation(evaluation);
        var context = Context(evaluation.ProjectPath);
        var relativeProject = Text(context, "project");
        var prefix = relativeProject[..(relativeProject.LastIndexOf('/') + 1)];
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(evaluation.ProjectPath))!;
        foreach (var expected in Manifest.Value.RootElement.GetProperty("files").EnumerateArray()
                     .Where(file => Text(file, "path").StartsWith(prefix, StringComparison.Ordinal)))
        {
            var expectedPath = Path.Combine(projectDirectory, Text(expected, "path")[prefix.Length..]);
            var matches = capture.Files.Where(file => Path.GetFullPath(file.OriginalPath).Equals(expectedPath, PathComparison)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Frozen pilot input is missing or ambiguous in captured source.");
            var file = matches[0];
            if (file.CollectionStatus != "available" || file.ContentHash != Text(expected, "sha256") ||
                file.ByteLength != expected.GetProperty("bytes").GetInt64() || file.CollectionReason is not null || file.Origin != "written")
                throw new InvalidDataException("Captured input differs from the frozen pilot: " + file.LogicalPath);
            var root = capture.Roots.SingleOrDefault(root => root.Id == file.RootId);
            if (root is null || root.RootKind != "physical" || root.OriginalPath is null || root.LogicalName != file.RootName ||
                file.LogicalPath != file.RootName + "/" + file.RelativePath ||
                !Path.GetFullPath(Path.Combine(root.OriginalPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar))).Equals(expectedPath, PathComparison))
                throw new InvalidDataException("Captured pilot path is not bound to its declared source root.");
            VerifyIdentity(capture, file.Id, "source_file/1", new { snapshot_id = capture.SnapshotId, root_key = file.RootName, relative_path = file.RelativePath });
            VerifyIdentity(capture, file.FileVersionId, "file_version/1", new { file_id = file.Id, content_hash = file.ContentHash,
                availability = file.CollectionStatus, unavailable_reason = file.CollectionReason });
            var blobs = capture.Blobs.Where(blob => blob.Hash == file.ContentHash).ToArray();
            if (blobs.Length != 1 || blobs[0].ByteLength != file.ByteLength)
                throw new InvalidDataException("Captured pilot version lacks matching blob evidence.");
        }
    }

    private static void VerifyIdentity(CapturedInputs capture, string id, string domain, object components)
    {
        if (!capture.IdentityInputs.TryGetValue(id, out var envelope)) throw new InvalidDataException("Captured pilot identity evidence is missing.");
        StableId.Verify(id, envelope);
        if (!CanonicalJson.Serialize(envelope).AsSpan().SequenceEqual(CanonicalJson.Serialize(StableId.Envelope(domain, components))))
            throw new InvalidDataException("Captured pilot identity differs from its descriptor.");
    }

    private static void ValidateManaged(MsBuildEvaluation evaluation, JsonElement context)
    {
        var properties = evaluation.Properties;
        foreach (var (key, expected) in new[] { ("TargetFramework", Text(context, "target_framework")),
            ("LangVersion", Text(context, "language_version")), ("NETCoreSdkVersion", Text(context, "sdk")),
            ("PlatformTarget", "x64"), ("AssemblyName", Text(context, "assembly")), ("Nullable", "enable"),
            ("ImplicitUsings", "disable"), ("GenerateAssemblyInfo", "false"), ("GenerateTargetFrameworkAttribute", "false"),
            ("AllowUnsafeBlocks", "false"), ("Optimize", "true") }) Exact(properties, key, expected);
        foreach (var key in new[] { "RuntimeIdentifier", "PathMap", "Features", "CscToolPath", "CscToolExe" }) Exact(properties, key, "");
        // An explicitly observed empty value selects the compiler's ordinary unchecked default.
        OneOf(properties, "CheckForOverflowUnderflow", "", "false");
        OneOf(properties, "OutputType", "Library", "library");
        foreach (var kind in new[] { "ReferencePath", "Analyzer", "AdditionalFiles", "EmbeddedResource", "ProjectReference", "PackageReference" }) Queried(evaluation, kind);
        foreach (var kind in new[] { "AdditionalFiles", "EmbeddedResource", "ProjectReference", "PackageReference" })
            if (evaluation.Items.Any(item => item.Kind == kind)) throw new NotSupportedException("Unexpected pilot input family: " + kind);
        ValidateAnalyzers(evaluation, Text(context, "sdk"));
    }

    private static void ValidateNative(MsBuildEvaluation evaluation, JsonElement context)
    {
        Selected(evaluation, "ClInclude", context.GetProperty("internal_includes"));
        var properties = evaluation.Properties;
        Exact(properties, "PlatformToolset", Text(context, "toolset"));
        Exact(properties, "VCToolsVersion", "14.44.35207");
        Exact(properties, "WindowsTargetPlatformVersion", Text(context, "windows_sdk"));
        Exact(properties, "ConfigurationType", "StaticLibrary");
        Exact(properties, "TargetName", "Pilot");
        Exact(properties, "CLToolPath", "");
        Exact(properties, "CLToolExe", "");
        OneOf(properties, "PreferredToolArchitecture", "", "x86");
        var compiler = evaluation.Tools.SingleOrDefault(tool => tool.Descriptor.TryGetProperty("name", out var name) && name.GetString() == "compiler:cl.exe");
        if (compiler is null || Text(compiler.Descriptor, "version") != "19.44.35215.0" ||
            !compiler.Descriptor.TryGetProperty("content_fingerprint", out var compilerFingerprint) ||
            Text(compilerFingerprint, "state") != "known" || !IsHash(Text(compilerFingerprint, "value")))
            throw new NotSupportedException("Native compiler evidence differs from the frozen pilot.");
        var source = evaluation.Items.Single(item => item.Kind == "ClCompile");
        foreach (var (key, value) in new[] { ("LanguageStandard", "stdcpp17"), ("PrecompiledHeader", "NotUsing"),
            ("RuntimeLibrary", "MultiThreadedDll"), ("CallingConvention", "Cdecl"), ("ExceptionHandling", "Sync"),
            ("TreatWChar_tAsBuiltInType", "true"), ("CompileAs", "Default"), ("ConformanceMode", "Default"),
            ("DisableLanguageExtensions", "false"), ("UndefineAllPreprocessorDefinitions", "false"), ("FloatingPointModel", "Precise"),
            ("StructMemberAlignment", "Default"), ("ForceConformanceInForLoopScope", "true"), ("IgnoreStandardIncludePath", "false") }) Exact(source.Metadata, key, value);
        foreach (var key in new[] { "ForcedIncludeFiles", "ForcedUsingFiles", "AdditionalIncludeDirectories" }) Exact(source.Metadata, key, "");
        var definitions = Required(source.Metadata, "PreprocessorDefinitions").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!definitions.SequenceEqual(new[] { "NDEBUG" }, StringComparer.Ordinal)) throw new NotSupportedException("Native definitions differ from the frozen pilot.");
        var options = Required(source.Metadata, "AdditionalOptions").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!options.SequenceEqual(new[] { "/utf-8", "/Zc:__cplusplus" }, StringComparer.Ordinal))
            throw new NotSupportedException("Native additional options differ from the frozen pilot.");
    }

    private static void Selected(MsBuildEvaluation evaluation, string kind, JsonElement expectedPaths)
    {
        Queried(evaluation, kind);
        var expected = expectedPaths.EnumerateArray().Select(path => Path.GetFileName(path.GetString()!)).ToHashSet(StringComparer.Ordinal);
        var items = evaluation.Items.Where(item => item.Kind == kind).ToArray();
        if (items.Length != expected.Count || !items.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new NotSupportedException("Evaluated " + kind + " set differs from the frozen pilot.");
        foreach (var item in items)
        {
            var fullPath = Path.GetFullPath(Required(item.Metadata, "FullPath"));
            var expectedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(evaluation.ProjectPath)!, item.Identity));
            if (!fullPath.Equals(expectedPath, PathComparison)) throw new NotSupportedException("Selected input is outside its frozen sibling location.");
        }
    }

    private static void ValidateAnalyzers(MsBuildEvaluation evaluation, string sdk)
    {
        var references = evaluation.Items.Where(item => item.Kind == "ReferencePath").ToArray();
        if (references.Length == 0 || references.Length != evaluation.Providers.Count)
            throw new InvalidDataException("Reference provider evidence is incomplete.");
        string? dotnetRoot = null;
        foreach (var reference in references)
        {
            Exact(reference.Metadata, "NuGetPackageId", "Microsoft.NETCore.App.Ref");
            Exact(reference.Metadata, "NuGetPackageVersion", "8.0.19");
            var path = Path.GetFullPath(Required(reference.Metadata, "FullPath"));
            var suffix = Path.Combine("packs", "Microsoft.NETCore.App.Ref", "8.0.19", "ref", "net8.0", Path.GetFileName(path));
            if (!path.EndsWith(Path.DirectorySeparatorChar + suffix, PathComparison)) throw new NotSupportedException("Framework reference is outside the pinned pack layout.");
            var root = path[..^suffix.Length].TrimEnd(Path.DirectorySeparatorChar);
            if (dotnetRoot is not null && !root.Equals(dotnetRoot, PathComparison)) throw new NotSupportedException("Framework references use conflicting installations.");
            dotnetRoot = root;
        }
        var sdkNames = new HashSet<string>(StringComparer.Ordinal) { "Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll", "Microsoft.CodeAnalysis.NetAnalyzers.dll" };
        var frameworkNames = new HashSet<string>(StringComparer.Ordinal) { "Microsoft.Interop.ComInterfaceGenerator.dll", "Microsoft.Interop.JavaScript.JSImportGenerator.dll",
            "Microsoft.Interop.LibraryImportGenerator.dll", "Microsoft.Interop.SourceGeneration.dll", "System.Text.Json.SourceGeneration.dll", "System.Text.RegularExpressions.Generator.dll" };
        foreach (var analyzer in evaluation.Items.Where(item => item.Kind == "Analyzer"))
        {
            var path = Path.GetFullPath(Required(analyzer.Metadata, "FullPath"));
            var name = Path.GetFileName(path);
            var expected = sdkNames.Contains(name) ? Path.Combine(dotnetRoot!, "sdk", sdk, "Sdks", "Microsoft.NET.Sdk", "analyzers", name)
                : frameworkNames.Contains(name) ? Path.Combine(dotnetRoot!, "packs", "Microsoft.NETCore.App.Ref", "8.0.19", "analyzers", "dotnet", "cs", name) : null;
            if (expected is null || !path.Equals(expected, PathComparison)) throw new NotSupportedException("Local/custom analyzer is outside the frozen pilot tools.");
            var tool = evaluation.Tools.SingleOrDefault(tool => Path.GetFullPath(tool.PhysicalPath).Equals(path, PathComparison));
            if (tool is null || !tool.Descriptor.TryGetProperty("content_fingerprint", out var fingerprint) ||
                Text(fingerprint, "state") != "known" || !IsHash(Text(fingerprint, "value")))
                throw new InvalidDataException("Selected analyzer has no measured tool fingerprint.");
        }
    }

    private static JsonElement Context(string projectPath)
    {
        var name = Path.GetFileName(projectPath);
        var matches = Manifest.Value.RootElement.GetProperty("contexts").EnumerateArray()
            .Where(context => Path.GetFileName(Text(context, "project")) == name).ToArray();
        return matches.Length == 1 ? matches[0] : throw new NotSupportedException("Project is outside the frozen M1 pilot.");
    }
    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()
        ?? throw new InvalidDataException("Null pilot contract field: " + property);
    private static string Required(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value)
        ? value : throw new InvalidDataException("Evaluated evidence is missing: " + key);
    private static void Exact(IReadOnlyDictionary<string, string> values, string key, string expected)
    {
        if (!string.Equals(Required(values, key), expected, StringComparison.Ordinal)) throw new NotSupportedException("Nonbaseline pilot setting: " + key);
    }
    private static void OneOf(IReadOnlyDictionary<string, string> values, string key, params string[] expected)
    {
        if (!expected.Contains(Required(values, key), StringComparer.Ordinal)) throw new NotSupportedException("Nonbaseline pilot setting: " + key);
    }
    private static void Queried(MsBuildEvaluation evaluation, string kind)
    {
        if (!evaluation.EvaluatedItemKinds.Contains(kind)) throw new InvalidDataException("Item family was not evaluated: " + kind);
    }
    private static bool IsHash(string value) => value.Length == 64 && value.All(character => "0123456789abcdef".Contains(character));
}

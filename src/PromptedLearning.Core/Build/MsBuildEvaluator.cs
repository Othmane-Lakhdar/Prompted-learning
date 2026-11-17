using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptedLearning.Core;

public sealed record MsBuildRequest(string ExecutablePath, string ProjectPath, string Configuration,
    string Platform, string IntermediateDirectory, string OutputDirectory, int TimeoutMs = 60000,
    int MaxOutputBytes = 16 * 1024 * 1024, IReadOnlyList<AnalysisToolRequest>? AnalysisTools = null);
public sealed record AnalysisToolRequest(string Name, string PhysicalPath, string ExpectedVersion);
public sealed record EvaluatedItem(string Kind, string Identity, IReadOnlyDictionary<string, string> Metadata);
public sealed record EvaluatedProvider(string PhysicalPath, JsonElement Descriptor);
public sealed record EvaluatedTool(string PhysicalPath, JsonElement Descriptor);
public sealed record MsBuildEvaluation(string ProjectPath, string ProjectHash, string Language,
    IReadOnlyDictionary<string, string> Properties, IReadOnlyList<EvaluatedItem> Items,
    IReadOnlyList<EvaluatedProvider> Providers, JsonElement ToolDescriptor, string ToolPath,
    JsonElement NormalizedOptions, string RawOutputHash, long ElapsedMs)
{
    public IReadOnlyList<EvaluatedTool> Tools { get; init; } = Array.Empty<EvaluatedTool>();
    public IReadOnlySet<string> EvaluatedItemKinds { get; init; } = new HashSet<string>();
    public string BuildDefinitionFingerprint { get; init; } = "";
    public string PreprocessedPath { get; init; } = "";
}

/// <summary>Evaluated input/context acquisition. ResolveReferences is not a compiled-artifact observation.</summary>
public sealed class MsBuildEvaluator
{
    public async Task<MsBuildEvaluation> EvaluateAsync(MsBuildRequest request, CancellationToken cancellationToken = default)
    {
        if (request.TimeoutMs <= 0 || request.MaxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        var project = Path.GetFullPath(request.ProjectPath);
        await PilotInputGuard.ValidateFilesAsync(project, request.Configuration, request.Platform, cancellationToken);
        var language = Path.GetExtension(project) switch { ".csproj" => "csharp", ".vcxproj" => "cpp", _ => throw new NotSupportedException("Only the declared MSBuild project types are supported.") };
        if (language == "cpp" && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CL")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("_CL_"))))
            throw new NotSupportedException("The frozen native pilot does not accept compiler-injected CL/_CL_ environment options.");
        var before = await HashFileAsync(project, cancellationToken);
        Directory.CreateDirectory(request.IntermediateDirectory); Directory.CreateDirectory(request.OutputDirectory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.TimeoutMs);
        var watch = Stopwatch.StartNew();
        var start = new ProcessStartInfo(Path.GetFullPath(request.ExecutablePath))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(project)!
        };
        var environment = start.Environment.ToArray(); // Freeze inherited values; never write them to reports.
        foreach (var arg in new[] { project, "-nologo", "-v:quiet", "-p:Configuration=" + request.Configuration, "-p:Platform=" + request.Platform }) start.ArgumentList.Add(arg);
        if (language == "csharp")
        {
            start.ArgumentList.Add("-p:BaseIntermediateOutputPath=" + DirectoryPath(request.IntermediateDirectory));
            start.ArgumentList.Add("-p:OutputPath=" + DirectoryPath(request.OutputDirectory));
        }
        else
        {
            start.ArgumentList.Add("-p:IntDir=" + DirectoryPath(request.IntermediateDirectory));
            start.ArgumentList.Add("-p:OutDir=" + DirectoryPath(request.OutputDirectory));
        }
        if (language == "csharp") await RunAsync(start, ["-target:Restore"], request.MaxOutputBytes, deadline.Token);
        var preprocessing = await RunAsync(start, ["-preprocess"], request.MaxOutputBytes, deadline.Token);
        var definitions = await MsBuildDefinitions.FingerprintAsync(preprocessing, request, deadline.Token);
        var output = await RunAsync(start, language == "csharp" ? ["-target:ResolveReferences",
            "-getProperty:TargetFramework,LangVersion,Configuration,Platform,PlatformTarget,AssemblyName,NETCoreSdkVersion,MSBuildVersion,DefineConstants,AllowUnsafeBlocks,Optimize,Nullable,RuntimeIdentifier,CheckForOverflowUnderflow,GenerateAssemblyInfo,GenerateTargetFrameworkAttribute,ImplicitUsings,PathMap,Features,OutputType,CodePage,WarningLevel,EnableNETAnalyzers,RunAnalyzersDuringBuild,GenerateDocumentationFile,Deterministic,DebugType,NoStdLib,NoConfig,RoslynTargetsPath,CscToolPath,CscToolExe",
            "-getItem:Compile,ReferencePath,Analyzer,AdditionalFiles,EmbeddedResource,ProjectReference,PackageReference"] : [
            "-getProperty:Configuration,Platform,PlatformToolset,VCToolsVersion,VCToolsInstallDir,WindowsTargetPlatformVersion,MSBuildVersion,ConfigurationType,TargetName,IncludePath,CLToolPath,CLToolExe,PreferredToolArchitecture",
            "-getItem:ClCompile,ClInclude"], request.MaxOutputBytes, deadline.Token);
        var afterPreprocessing = await RunAsync(start, ["-preprocess"], request.MaxOutputBytes, deadline.Token);
        if (definitions != await MsBuildDefinitions.FingerprintAsync(afterPreprocessing, request, deadline.Token))
            throw new IOException("Imported definitions changed during context acquisition.");
        var preprocessingPath = Path.Combine(request.IntermediateDirectory, "context.preprocessed.xml");
        await File.WriteAllTextAsync(preprocessingPath, afterPreprocessing, deadline.Token);
        if (before != await HashFileAsync(project, deadline.Token)) throw new IOException("Project changed during evaluation.");
        using var document = CanonicalJson.Parse(Encoding.UTF8.GetBytes(output.TrimStart('\ufeff')));
        var properties = document.RootElement.GetProperty("Properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var items = document.RootElement.GetProperty("Items").EnumerateObject().SelectMany(group => group.Value.EnumerateArray().Select(item =>
        {
            var metadata = item.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
            // Missing MSBuild item metadata evaluates to the empty string, unlike an unqueried item group.
            if (group.Name == "ClCompile") foreach (var key in new[] { "ForcedIncludeFiles", "ForcedUsingFiles", "AdditionalIncludeDirectories" }) metadata.TryAdd(key, "");
            return new EvaluatedItem(group.Name, metadata["Identity"], new ReadOnlyDictionary<string, string>(metadata));
        })).ToArray();
        var providers = new List<EvaluatedProvider>();
        foreach (var reference in items.Where(i => i.Kind == "ReferencePath"))
        {
            var fullPath = Path.GetFullPath(reference.Metadata["FullPath"]);
            var name = reference.Metadata["AssemblyName"];
            var version = reference.Metadata.GetValueOrDefault("NuGetPackageVersion", "");
            var package = reference.Metadata.GetValueOrDefault("NuGetPackageId", "");
            if (string.IsNullOrEmpty(package) || string.IsNullOrEmpty(version)) throw new NotSupportedException("Reference provider without explicit package/version needs a declared normalization rule.");
            var descriptor = Element(new
            {
                kind = "assembly", logical_name = name,
                version = new { state = "known", value = version, reason = (string?)null },
                content_fingerprint = new { state = "known", value = await HashFileAsync(fullPath, deadline.Token), reason = (string?)null },
                root_key = "package:" + package + ":" + version,
                relative_location = Path.GetFileName(fullPath)
            });
            providers.Add(new EvaluatedProvider(fullPath, descriptor));
        }
        var tool = Element(new { name = "MSBuild", version = properties["MSBuildVersion"], content_fingerprint = new { state = "known", value = await HashFileAsync(start.FileName, deadline.Token), reason = (string?)null } });
        var tools = new List<EvaluatedTool> { new(start.FileName, tool) };
        var compilerFolder = language == "csharp" ? properties["RoslynTargetsPath"] : Path.Combine(properties["VCToolsInstallDir"], "bin", "HostX86", "x64");
        var compilerNames = language == "csharp" ? new[] { "csc.exe", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll" } : new[] { "cl.exe", "c1xx.dll", "c2.dll" };
        foreach (var name in compilerNames) tools.Add(await ToolAsync("compiler:" + name, Path.Combine(compilerFolder, name), null, deadline.Token));
        foreach (var analyzer in items.Where(i => i.Kind == "Analyzer").OrderBy(i => i.Metadata["FullPath"], CanonicalJson.Utf8Comparer))
            tools.Add(await ToolAsync("analyzer:" + Path.GetFileName(analyzer.Metadata["FullPath"]), analyzer.Metadata["FullPath"], null, deadline.Token));
        foreach (var analysisTool in request.AnalysisTools ?? [])
            tools.Add(await ToolAsync(analysisTool.Name, analysisTool.PhysicalPath, analysisTool.ExpectedVersion, deadline.Token));
        var options = new List<object>();
        options.Add(new { kind = "literal", value = "evaluated_definition_fingerprint=" + definitions });
        if (language == "csharp")
        {
            foreach (var name in new[] { "TargetFramework", "LangVersion", "PlatformTarget", "DefineConstants", "AllowUnsafeBlocks", "Optimize", "Nullable", "NETCoreSdkVersion", "RuntimeIdentifier", "CheckForOverflowUnderflow", "GenerateAssemblyInfo", "GenerateTargetFrameworkAttribute", "ImplicitUsings", "PathMap", "Features", "OutputType", "CodePage", "WarningLevel", "EnableNETAnalyzers", "RunAnalyzersDuringBuild", "GenerateDocumentationFile", "Deterministic", "DebugType", "NoStdLib", "NoConfig" })
                options.Add(new { kind = "literal", value = name + "=" + properties[name] });
        }
        else
        {
            foreach (var name in new[] { "PlatformToolset", "VCToolsVersion", "WindowsTargetPlatformVersion", "ConfigurationType" })
                options.Add(new { kind = "literal", value = name + "=" + properties[name] });
            foreach (var source in items.Where(i => i.Kind == "ClCompile").OrderBy(i => i.Identity, CanonicalJson.Utf8Comparer))
                foreach (var name in new[] { "LanguageStandard", "PreprocessorDefinitions", "AdditionalOptions", "CallingConvention", "RuntimeLibrary", "Optimization", "ExceptionHandling", "TreatWChar_tAsBuiltInType", "ConformanceMode", "DisableLanguageExtensions", "CompileAs", "ForcedIncludeFiles", "ForcedUsingFiles", "PrecompiledHeader", "AdditionalIncludeDirectories", "UndefineAllPreprocessorDefinitions", "FloatingPointModel", "StructMemberAlignment", "ForceConformanceInForLoopScope", "IgnoreStandardIncludePath" })
                    options.Add(new { kind = "literal", value = source.Identity + ":" + name + "=" + source.Metadata.GetValueOrDefault(name, "") });
            foreach (var include in properties["IncludePath"].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = Path.GetFullPath(include); var roots = new[] {
                    (Path: Path.GetFullPath(Path.Combine(Path.GetDirectoryName(start.FileName)!, "../../..")), Key: "visual-studio"),
                    (Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits"), Key: "windows-kits") };
                var root = roots.FirstOrDefault(r => path.StartsWith(r.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                if (root.Path is null) throw new NotSupportedException("Unrecognized native include root.");
                options.Add(new { kind = "path", option = "IncludePath", root_key = root.Key, relative_path = Path.GetRelativePath(root.Path, path).Replace('\\', '/') });
            }
        }
        await PilotInputGuard.ValidateFilesAsync(project, request.Configuration, request.Platform, deadline.Token);
        var currentEnvironment = Environment.GetEnvironmentVariables();
        if (currentEnvironment.Count != environment.Length || environment.Any(pair => (string?)currentEnvironment[pair.Key] != pair.Value))
            throw new IOException("Process environment changed during context acquisition.");
        var evaluation = new MsBuildEvaluation(project, before, language, new ReadOnlyDictionary<string, string>(properties), items, providers,
            tool, start.FileName, Element(options), CanonicalJson.Sha256(Encoding.UTF8.GetBytes(output)), watch.ElapsedMilliseconds)
        { Tools = tools, EvaluatedItemKinds = document.RootElement.GetProperty("Items").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal), BuildDefinitionFingerprint = definitions, PreprocessedPath = preprocessingPath };
        PilotInputGuard.ValidateEvaluation(evaluation);
        return evaluation;
    }

    public static JsonElement ContextComponents(MsBuildEvaluation evaluation, string snapshotId, string projectId)
    {
        var props = evaluation.Properties;
        var native = evaluation.Items.FirstOrDefault(i => i.Kind == "ClCompile");
        var version = evaluation.Language == "csharp" ? props["LangVersion"] : native?.Metadata.GetValueOrDefault("LanguageStandard") switch
        {
            "stdcpp17" => "c++17", _ => throw new NotSupportedException("Native context is outside the M1 C++17 pilot.")
        };
        var result = Element(new
        {
            snapshot_id = snapshotId, project_id = projectId, solution_mapping = (object?)null,
            configuration = props["Configuration"], platform = props["Platform"], architecture = props["Platform"],
            target_framework = evaluation.Language == "csharp" ? props["TargetFramework"] : null, runtime_identifier = (string?)null,
            language_version = version, output_identity = evaluation.Language == "csharp" ? props["AssemblyName"] : props["TargetName"],
            options = evaluation.NormalizedOptions, tools = evaluation.Tools.Select(t => t.Descriptor).ToArray(), providers = evaluation.Providers.Select(p => p.Descriptor).ToArray(),
            generated_inputs = Array.Empty<object>(), path_semantics_policy = "logical_paths_no_path_sensitive_constructs",
            path_sensitive_values = Array.Empty<object>()
        });
        _ = StableId.Envelope("build_context/1", result);
        return result;
    }

    private static string DirectoryPath(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/') + "/";
    private static JsonElement Element(object value) { using var doc = CanonicalJson.Parse(CanonicalJson.Serialize(value)); return doc.RootElement.Clone(); }
    private static async Task<EvaluatedTool> ToolAsync(string name, string path, string? expectedVersion, CancellationToken token)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        var version = name == "compiler:cl.exe" ? info.FileVersion : info.ProductVersion ?? info.FileVersion;
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("Selected tool has no version metadata.");
        if (expectedVersion is not null && version != expectedVersion) throw new NotSupportedException("Analysis tool version does not match the declared request: " + name);
        return new EvaluatedTool(path, Element(new { name, version, content_fingerprint = new { state = "known", value = await HashFileAsync(path, token), reason = (string?)null } }));
    }
    private static async Task<string> RunAsync(ProcessStartInfo template, IReadOnlyList<string> extra, int maximumBytes, CancellationToken token)
    {
        var start = new ProcessStartInfo(template.FileName) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = template.WorkingDirectory };
        start.Environment.Clear();
        foreach (var pair in template.Environment) start.Environment.Add(pair.Key, pair.Value);
        foreach (var arg in template.ArgumentList.Concat(extra)) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("MSBuild did not start.");
        var stdout = ReadBoundedAsync(process.StandardOutput, maximumBytes, token);
        var stderr = ReadBoundedAsync(process.StandardError, maximumBytes, token);
        try
        {
            var pending = new List<Task> { process.WaitForExitAsync(token), stdout, stderr };
            while (pending.Count != 0) { var done = await Task.WhenAny(pending); await done; pending.Remove(done); }
            var output = await stdout; var error = await stderr;
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error)) throw new IOException($"MSBuild acquisition failed ({process.ExitCode}): {error}\n{output}");
            return output;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch { /* Preserve the original failure. */ }
            throw;
        }
    }
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumBytes, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; long bytes = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) return text.ToString();
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (bytes > maximumBytes) throw new IOException("MSBuild output exceeds the configured byte limit.");
            text.Append(buffer, 0, read);
        }
    }
}

using System.Text.Json;
using PromptedLearning.Core;

namespace PromptedLearning.Tests;

public static class PilotGuardTests
{
    private static string Project(TestContext context, string language) => Path.Combine(context.ProjectRoot,
        "tests", "fixtures", "pilot", language, language == "csharp" ? "Pilot.csproj" : "Pilot.vcxproj");
    private static string CopyPilot(TestContext context, string language)
    {
        var source = Path.GetDirectoryName(Project(context, language))!;
        var destination = Path.Combine(context.ScratchRoot, "pilot-guard-" + Guid.NewGuid().ToString("N"), "relocated");
        Directory.CreateDirectory(destination);
        foreach (var name in language == "csharp" ? new[] { "Pilot.csproj", "Arithmetic.cs", "Arithmetic.More.cs" }
                     : new[] { "Pilot.vcxproj", "Arithmetic.cpp", "Arithmetic.hpp" })
            File.Copy(Path.Combine(source, name), Path.Combine(destination, name));
        return Path.Combine(destination, Path.GetFileName(Project(context, language)));
    }
    private static EvaluatedItem Item(string kind, string identity, string fullPath, IReadOnlyDictionary<string, string>? extra = null)
    {
        var metadata = new Dictionary<string, string>(extra ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        { ["Identity"] = identity, ["FullPath"] = fullPath };
        return new(kind, identity, metadata);
    }
    // These are deliberately synthetic evaluation envelopes. ContextTests exercises real MSBuild.
    private static MsBuildEvaluation Baseline(TestContext context, bool managed, string? projectOverride = null)
    {
        var project = projectOverride ?? Project(context, managed ? "csharp" : "native");
        var directory = Path.GetDirectoryName(project)!;
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["Configuration"] = "Release", ["Platform"] = "x64" };
        var items = new List<EvaluatedItem>();
        var providers = new List<EvaluatedProvider>();
        var groups = managed ? new[] { "Compile", "ReferencePath", "Analyzer", "AdditionalFiles", "EmbeddedResource", "ProjectReference", "PackageReference" }
            : new[] { "ClCompile", "ClInclude" };
        if (managed)
        {
            foreach (var (key, value) in new[] { ("TargetFramework", "net8.0"), ("LangVersion", "12.0"), ("NETCoreSdkVersion", "9.0.304"),
                ("PlatformTarget", "x64"), ("AssemblyName", "Pilot"), ("Nullable", "enable"), ("ImplicitUsings", "disable"),
                ("GenerateAssemblyInfo", "false"), ("GenerateTargetFrameworkAttribute", "false"), ("AllowUnsafeBlocks", "false"),
                ("Optimize", "true"), ("RuntimeIdentifier", ""), ("PathMap", ""), ("Features", ""), ("CscToolPath", ""),
                ("CscToolExe", ""), ("CheckForOverflowUnderflow", "false"), ("OutputType", "Library") }) properties[key] = value;
            foreach (var name in new[] { "Arithmetic.cs", "Arithmetic.More.cs" }) items.Add(Item("Compile", name, Path.Combine(directory, name)));
            var reference = Path.Combine(context.ScratchRoot, "synthetic-dotnet", "packs", "Microsoft.NETCore.App.Ref", "8.0.19", "ref", "net8.0", "System.Runtime.dll");
            items.Add(Item("ReferencePath", reference, reference, new Dictionary<string, string> { ["NuGetPackageId"] = "Microsoft.NETCore.App.Ref", ["NuGetPackageVersion"] = "8.0.19" }));
            providers.Add(new(reference, JsonSerializer.SerializeToElement(new { })));
        }
        else
        {
            foreach (var (key, value) in new[] { ("PlatformToolset", "v143"), ("WindowsTargetPlatformVersion", "10.0.26100.0"),
                ("ConfigurationType", "StaticLibrary"), ("TargetName", "Pilot"), ("CLToolPath", ""), ("CLToolExe", ""), ("PreferredToolArchitecture", ""),
                ("VCToolsVersion", "14.44.35207") }) properties[key] = value;
            var metadata = new Dictionary<string, string> { ["LanguageStandard"] = "stdcpp17", ["PrecompiledHeader"] = "NotUsing",
                ["RuntimeLibrary"] = "MultiThreadedDll", ["CallingConvention"] = "Cdecl", ["ExceptionHandling"] = "Sync",
                ["TreatWChar_tAsBuiltInType"] = "true", ["CompileAs"] = "Default", ["ForcedIncludeFiles"] = "", ["ForcedUsingFiles"] = "",
                ["AdditionalIncludeDirectories"] = "", ["PreprocessorDefinitions"] = "NDEBUG;", ["AdditionalOptions"] = "/utf-8 /Zc:__cplusplus ",
                ["ConformanceMode"] = "Default", ["DisableLanguageExtensions"] = "false", ["UndefineAllPreprocessorDefinitions"] = "false",
                ["FloatingPointModel"] = "Precise", ["StructMemberAlignment"] = "Default", ["ForceConformanceInForLoopScope"] = "true",
                ["IgnoreStandardIncludePath"] = "false" };
            items.Add(Item("ClCompile", "Arithmetic.cpp", Path.Combine(directory, "Arithmetic.cpp"), metadata));
            items.Add(Item("ClInclude", "Arithmetic.hpp", Path.Combine(directory, "Arithmetic.hpp")));
        }
        return new(project, CanonicalJson.Sha256(File.ReadAllBytes(project)), managed ? "csharp" : "cpp", properties, items, providers,
            JsonSerializer.SerializeToElement(new { }), "synthetic-tool", JsonSerializer.SerializeToElement(Array.Empty<object>()), new string('a', 64), 0)
        { EvaluatedItemKinds = groups.ToHashSet(StringComparer.Ordinal), Tools = managed ? [] : [new EvaluatedTool("synthetic-cl.exe",
            JsonSerializer.SerializeToElement(new { name = "compiler:cl.exe", version = "19.44.35215.0", content_fingerprint = new { state = "known", value = new string('a', 64) } }))] };
    }
    private static MsBuildEvaluation Property(MsBuildEvaluation value, string key, string setting)
    {
        var properties = value.Properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        properties[key] = setting;
        return value with { Properties = properties };
    }
    private static MsBuildEvaluation Metadata(MsBuildEvaluation value, string key, string? setting)
    {
        var source = value.Items.Single(item => item.Kind == "ClCompile");
        var metadata = source.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (setting is null) metadata.Remove(key); else metadata[key] = setting;
        return value with { Items = value.Items.Select(item => item == source ? item with { Metadata = metadata } : item).ToArray() };
    }

    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        yield return new("pilot_guard.frozen_inputs_and_relocation", async () =>
        {
            foreach (var language in new[] { "csharp", "native" })
            {
                await PilotInputGuard.ValidateFilesAsync(Project(context, language), "Release", "x64");
                await PilotInputGuard.ValidateFilesAsync(CopyPilot(context, language), "Release", "x64");
            }
        });
        yield return new("pilot_guard.changed_source_and_project_rejected_without_msbuild", async () =>
        {
            foreach (var language in new[] { "csharp", "native" })
            {
                var project = CopyPilot(context, language);
                var source = Path.Combine(Path.GetDirectoryName(project)!, language == "csharp" ? "Arithmetic.cs" : "Arithmetic.cpp");
                var original = await File.ReadAllBytesAsync(source);
                await File.AppendAllTextAsync(source, " ");
                await Check.ThrowsAsync<InvalidDataException>(() => PilotInputGuard.ValidateFilesAsync(project, "Release", "x64"));
                await File.WriteAllBytesAsync(source, original);
                await File.AppendAllTextAsync(project, "<!-- changed -->");
                await Check.ThrowsAsync<InvalidDataException>(() => PilotInputGuard.ValidateFilesAsync(project, "Release", "x64"));
            }
        });
        yield return new("pilot_guard.unknown_configuration_and_project_rejected_without_msbuild", async () =>
        {
            await Check.ThrowsAsync<NotSupportedException>(() => PilotInputGuard.ValidateFilesAsync(Project(context, "csharp"), "Debug", "x64"));
            await Check.ThrowsAsync<NotSupportedException>(() => PilotInputGuard.ValidateFilesAsync(Project(context, "native"), "Release", "Win32"));
            await Check.ThrowsAsync<NotSupportedException>(() => PilotInputGuard.ValidateFilesAsync(Path.Combine(context.ScratchRoot, "Other.csproj"), "Release", "x64"));
        });
        yield return new("pilot_guard.pre_cancelled_validation_does_not_read", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => PilotInputGuard.ValidateFilesAsync("missing", "Release", "x64", cancellation.Token));
        });
        yield return new("pilot_guard.captured_versions_bind_evaluation_without_working_file_reads", async () =>
        {
            foreach (var managed in new[] { true, false })
            {
                var project = CopyPilot(context, managed ? "csharp" : "native");
                var evaluation = Baseline(context, managed, project);
                var names = managed ? new[] { "Pilot.csproj", "Arithmetic.cs", "Arithmetic.More.cs" }
                    : new[] { "Pilot.vcxproj", "Arithmetic.cpp", "Arithmetic.hpp" };
                var capture = await new CaptureService().CaptureAsync(new("guard-tests", [new("pilot", Path.GetDirectoryName(project))],
                    names.Select(name => new CaptureInput("pilot", name)).ToArray()), Path.Combine(Path.GetDirectoryName(project)!, "archive"));
                PilotInputGuard.ValidateCapture(evaluation, capture);
                var source = capture.Files.Single(file => file.RelativePath == names[1]);
                await File.AppendAllTextAsync(source.OriginalPath, " // changed after capture");
                PilotInputGuard.ValidateCapture(evaluation, capture);
                var changed = await new CaptureService().CaptureAsync(new("guard-tests", [new("pilot", Path.GetDirectoryName(project))],
                    names.Select(name => new CaptureInput("pilot", name)).ToArray()), Path.Combine(Path.GetDirectoryName(project)!, "changed-archive"));
                Check.Throws<InvalidDataException>(() => PilotInputGuard.ValidateCapture(evaluation, changed));
                foreach (var bad in new[] { source with { ContentHash = new string('b', 64) }, source with { ByteLength = source.ByteLength + 1 },
                    source with { CollectionStatus = "unavailable" }, source with { FileVersionId = "file_version:" + new string('c', 64) },
                    source with { RelativePath = "other.cs" } })
                    Check.Throws<InvalidDataException>(() => PilotInputGuard.ValidateCapture(evaluation,
                        capture with { Files = capture.Files.Select(file => file.Id == source.Id ? bad : file).ToArray() }));
            }
        });
        yield return TestCase.Sync("pilot_guard.synthetic_selected_sets_and_absence_evidence", () =>
        {
            foreach (var managed in new[] { true, false })
            {
                var baseline = Baseline(context, managed);
                PilotInputGuard.ValidateEvaluation(baseline);
                var kind = managed ? "Compile" : "ClInclude";
                var extra = Item(kind, "Generated.cs", Path.Combine(context.ScratchRoot, "Generated.cs"));
                Check.Throws<NotSupportedException>(() => PilotInputGuard.ValidateEvaluation(baseline with { Items = baseline.Items.Append(extra).ToArray() }));
                Check.Throws<InvalidDataException>(() => PilotInputGuard.ValidateEvaluation(baseline with { EvaluatedItemKinds = new HashSet<string>() }));
                Check.Throws<InvalidDataException>(() => PilotInputGuard.ValidateEvaluation(baseline with { ProjectHash = new string('f', 64) }));
            }
        });
        yield return TestCase.Sync("pilot_guard.synthetic_managed_runtime_generated_and_path_features", () =>
        {
            var baseline = Baseline(context, true);
            foreach (var (key, value) in new[] { ("RuntimeIdentifier", "win-x64"), ("GenerateAssemblyInfo", "true"),
                ("GenerateTargetFrameworkAttribute", "true"), ("ImplicitUsings", "enable"), ("CheckForOverflowUnderflow", "true"),
                ("PathMap", "physical=logical"), ("CscToolPath", "custom"), ("Features", "preview") })
                Check.Throws<NotSupportedException>(() => PilotInputGuard.ValidateEvaluation(Property(baseline, key, value)));
            var missing = baseline.Properties.Where(pair => pair.Key != "RuntimeIdentifier").ToDictionary(pair => pair.Key, pair => pair.Value);
            Check.Throws<InvalidDataException>(() => PilotInputGuard.ValidateEvaluation(baseline with { Properties = missing }));
            var groups = baseline.EvaluatedItemKinds.Where(kind => kind != "Analyzer").ToHashSet();
            Check.Throws<InvalidDataException>(() => PilotInputGuard.ValidateEvaluation(baseline with { EvaluatedItemKinds = groups }));
            Check.Throws<NotSupportedException>(() => PilotInputGuard.ValidateEvaluation(baseline with { Items = baseline.Items.Append(Item("AdditionalFiles", "extra", "extra")).ToArray() }));
        });
        yield return TestCase.Sync("pilot_guard.synthetic_native_forced_input_and_pch_rejection", () =>
        {
            var baseline = Baseline(context, false);
            foreach (var (key, value) in new[] { ("ForcedIncludeFiles", "hidden.hpp"), ("ForcedUsingFiles", "hidden.dll"),
                ("AdditionalIncludeDirectories", "outside"), ("PrecompiledHeader", "Use"), ("AdditionalOptions", "/utf-8 /Zc:__cplusplus /FC"),
                ("RuntimeLibrary", "MultiThreadedDebugDll"), ("ConformanceMode", "true"), ("DisableLanguageExtensions", "true"),
                ("UndefineAllPreprocessorDefinitions", "true"), ("FloatingPointModel", "Fast"), ("StructMemberAlignment", "4Bytes"),
                ("ForceConformanceInForLoopScope", "false"), ("IgnoreStandardIncludePath", "true") })
                Check.Throws<NotSupportedException>(() => PilotInputGuard.ValidateEvaluation(Metadata(baseline, key, value)));
            Check.Throws<InvalidDataException>(() => PilotInputGuard.ValidateEvaluation(Metadata(baseline, "ForcedIncludeFiles", null)));
            Check.Throws<NotSupportedException>(() => PilotInputGuard.ValidateEvaluation(Property(baseline, "PreferredToolArchitecture", "x64")));
            Check.Throws<NotSupportedException>(() => PilotInputGuard.ValidateEvaluation(Property(baseline, "CLToolExe", "other.exe")));
            Check.Throws<NotSupportedException>(() => PilotInputGuard.ValidateEvaluation(Property(baseline, "VCToolsVersion", "14.45.0")));
            Check.Throws<NotSupportedException>(() => PilotInputGuard.ValidateEvaluation(baseline with { Tools = [] }));
        });
        yield return TestCase.Sync("pilot_guard.synthetic_analyzer_location_and_fingerprint", () =>
        {
            var baseline = Baseline(context, true);
            var allowed = Path.Combine(context.ScratchRoot, "synthetic-dotnet", "sdk", "9.0.304", "Sdks", "Microsoft.NET.Sdk", "analyzers", "Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll");
            var analyzer = Item("Analyzer", allowed, allowed);
            var tool = new EvaluatedTool(allowed, JsonSerializer.SerializeToElement(new { content_fingerprint = new { state = "known", value = new string('a', 64) } }));
            var supported = baseline with { Items = baseline.Items.Append(analyzer).ToArray(), Tools = [tool] };
            PilotInputGuard.ValidateEvaluation(supported);
            Check.Throws<InvalidDataException>(() => PilotInputGuard.ValidateEvaluation(supported with { Tools = [] }));
            var localPath = Path.Combine(Path.GetDirectoryName(baseline.ProjectPath)!, Path.GetFileName(allowed));
            Check.Throws<NotSupportedException>(() => PilotInputGuard.ValidateEvaluation(baseline with { Items = baseline.Items.Append(Item("Analyzer", localPath, localPath)).ToArray() }));
        });
    }
}

using System.Text.Json;
using PromptedLearning.Core;

namespace PromptedLearning.Tests;

public static class ContextTests
{
    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        yield return new TestCase("evaluated pilot contexts and provider fingerprints", async () =>
        {
            var msbuild = Environment.GetEnvironmentVariable("PL_MSBUILD") ?? @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe";
            if (!File.Exists(msbuild)) throw new FileNotFoundException("Set PL_MSBUILD to the declared MSBuild executable.", msbuild);
            var evaluator = new MsBuildEvaluator();
            foreach (var language in new[] { "csharp", "native" })
            {
                var folder = Path.Combine(context.ScratchRoot, "context-" + language, Guid.NewGuid().ToString("N"));
                var project = Path.Combine(context.ProjectRoot, "tests", "fixtures", "pilot", language, language == "csharp" ? "Pilot.csproj" : "Pilot.vcxproj");
                var result = await evaluator.EvaluateAsync(new MsBuildRequest(msbuild, project, "Release", "x64", Path.Combine(folder, "obj"), Path.Combine(folder, "bin")));
                Check.Equal("Release", result.Properties["Configuration"]); Check.Equal("x64", result.Properties["Platform"]);
                Check.Equal(CanonicalJson.Sha256(await File.ReadAllBytesAsync(project)), result.ProjectHash);
                Check.Equal(64, result.BuildDefinitionFingerprint.Length);
                Check.True(File.Exists(result.PreprocessedPath), "Retain preprocessed input audit evidence.");
                Check.True(result.Tools.Any(t => t.Descriptor.GetProperty("name").GetString() == (language == "csharp" ? "compiler:csc.exe" : "compiler:cl.exe")));
                foreach (var tool in result.Tools)
                    Check.Equal(CanonicalJson.Sha256(await File.ReadAllBytesAsync(tool.PhysicalPath)), tool.Descriptor.GetProperty("content_fingerprint").GetProperty("value").GetString());
                if (language == "csharp")
                {
                    Check.Equal("9.0.304", result.Properties["NETCoreSdkVersion"]);
                    Check.Equal("net8.0", result.Properties["TargetFramework"]); Check.Equal("12.0", result.Properties["LangVersion"]);
                    Check.Equal("false", result.Properties["CheckForOverflowUnderflow"]);
                    Check.Equal(2, result.Items.Count(i => i.Kind == "Compile")); Check.Equal(163, result.Providers.Count);
                    Check.True(result.Items.Where(i => i.Kind == "ReferencePath").Select(i => i.Metadata["FullPath"]).SequenceEqual(result.Providers.Select(p => p.PhysicalPath)), "Preserve evaluated reference order.");
                    foreach (var provider in result.Providers)
                    {
                        Check.Equal("8.0.19", provider.Descriptor.GetProperty("version").GetProperty("value").GetString());
                        Check.Equal(CanonicalJson.Sha256(await File.ReadAllBytesAsync(provider.PhysicalPath)), provider.Descriptor.GetProperty("content_fingerprint").GetProperty("value").GetString());
                    }
                }
                else
                {
                    Check.Equal("14.44.35207", result.Properties["VCToolsVersion"]);
                    Check.Equal("10.0.26100.0", result.Properties["WindowsTargetPlatformVersion"]);
                    Check.Equal(1, result.Items.Count(i => i.Kind == "ClCompile")); Check.Equal(1, result.Items.Count(i => i.Kind == "ClInclude"));
                    Check.Equal("stdcpp17", result.Items.Single(i => i.Kind == "ClCompile").Metadata["LanguageStandard"]);
                }
                var components = MsBuildEvaluator.ContextComponents(result, "snapshot:" + new string('0', 64), "project:" + new string('1', 64));
                var id = StableId.Create("build_context/1", components);
                StableId.Verify(id, StableId.Envelope("build_context/1", components));
                Check.True(!components.GetRawText().Contains("C:\\", StringComparison.OrdinalIgnoreCase), "Physical locators must not enter normalized pilot identity.");
                var report = new { result.Language, result.ProjectHash, result.Properties, result.BuildDefinitionFingerprint, result.PreprocessedPath, selected_items = result.Items.Where(i => i.Kind != "ReferencePath").Select(i => new { i.Kind, i.Identity }), provider_count = result.Providers.Count, context_id = id, components, result.RawOutputHash, result.ElapsedMs, state = "evaluated_not_observed_build" };
                await File.WriteAllTextAsync(Path.Combine(folder, "context.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                // Changing only physical checkout/output locations must preserve semantic identity.
                var relocated = Path.Combine(folder, "relocated"); Directory.CreateDirectory(relocated);
                foreach (var input in Directory.GetFiles(Path.GetDirectoryName(project)!))
                    if (Path.GetExtension(input) is ".cs" or ".csproj" or ".cpp" or ".hpp" or ".vcxproj") File.Copy(input, Path.Combine(relocated, Path.GetFileName(input)));
                var second = await evaluator.EvaluateAsync(new MsBuildRequest(msbuild, Path.Combine(relocated, Path.GetFileName(project)), "Release", "x64", Path.Combine(folder, "obj2"), Path.Combine(folder, "bin2")));
                Check.Equal(result.BuildDefinitionFingerprint, second.BuildDefinitionFingerprint);
                Check.Equal(id, StableId.Create("build_context/1", MsBuildEvaluator.ContextComponents(second, "snapshot:" + new string('0', 64), "project:" + new string('1', 64))));
            }
        });
        yield return new TestCase("context acquisition honors pre-cancellation", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var evaluator = new MsBuildEvaluator();
            await Check.ThrowsAsync<OperationCanceledException>(() => evaluator.EvaluateAsync(new MsBuildRequest("not-launched", Path.Combine(context.ProjectRoot, "tests/fixtures/pilot/csharp/Pilot.csproj"), "Release", "x64", context.ScratchRoot, context.ScratchRoot), cancellation.Token));
        });
        yield return new TestCase("context acquisition bounds preprocessing output", async () =>
        {
            var msbuild = Environment.GetEnvironmentVariable("PL_MSBUILD") ?? @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe";
            var scratch = Path.Combine(context.ScratchRoot, "output-bound", Guid.NewGuid().ToString("N"));
            await Check.ThrowsAsync<IOException>(() => new MsBuildEvaluator().EvaluateAsync(new MsBuildRequest(msbuild,
                Path.Combine(context.ProjectRoot, "tests/fixtures/pilot/native/Pilot.vcxproj"), "Release", "x64", Path.Combine(scratch, "obj"), Path.Combine(scratch, "bin"), MaxOutputBytes: 64)));
        });
        yield return new TestCase("unrecognized local imports cannot silently change pilot semantics", async () =>
        {
            var msbuild = Environment.GetEnvironmentVariable("PL_MSBUILD") ?? @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe";
            var scratch = Path.Combine(context.ScratchRoot, "local-import", Guid.NewGuid().ToString("N"));
            var projectFolder = Path.Combine(scratch, "project"); Directory.CreateDirectory(projectFolder);
            foreach (var name in new[] { "Pilot.csproj", "Arithmetic.cs", "Arithmetic.More.cs" })
                File.Copy(Path.Combine(context.ProjectRoot, "tests/fixtures/pilot/csharp", name), Path.Combine(projectFolder, name));
            await File.WriteAllTextAsync(Path.Combine(projectFolder, "Directory.Build.props"),
                "<Project><PropertyGroup><CheckForOverflowUnderflow>true</CheckForOverflowUnderflow></PropertyGroup></Project>");
            await Check.ThrowsAsync<NotSupportedException>(() => new MsBuildEvaluator().EvaluateAsync(new MsBuildRequest(msbuild,
                Path.Combine(projectFolder, "Pilot.csproj"), "Release", "x64", Path.Combine(scratch, "obj"), Path.Combine(scratch, "bin"))));
        });
        yield return new TestCase("native injected environment options are rejected", async () =>
        {
            foreach (var name in new[] { "CL", "_CL_" })
            {
                var prior = Environment.GetEnvironmentVariable(name);
                try
                {
                    Environment.SetEnvironmentVariable(name, "/DUNDECLARED_OPTION");
                    await Check.ThrowsAsync<NotSupportedException>(() => new MsBuildEvaluator().EvaluateAsync(new MsBuildRequest("not-launched",
                        Path.Combine(context.ProjectRoot, "tests/fixtures/pilot/native/Pilot.vcxproj"), "Release", "x64", context.ScratchRoot, context.ScratchRoot)));
                }
                finally { Environment.SetEnvironmentVariable(name, prior); }
            }
        });
    }
}

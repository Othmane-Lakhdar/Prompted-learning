using System.Text;
using System.Text.Json;
using PromptedLearning.Core;
using PromptedLearning.Storage;

namespace PromptedLearning.Tests;

public static class ContextStorageTests
{
    private sealed record Inputs(CapturedInputs Capture, MsBuildEvaluation Managed, MsBuildEvaluation Native, string Directory);

    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        var inputs = new Lazy<Task<Inputs>>(() => Acquire(context));
        yield return new("Actual evaluated contexts preserve tools, providers, ordered options and managed inputs", async () =>
        {
            var input = await inputs.Value;
            using var store = CanonicalStore.Create(Path.Combine(input.Directory, "index.sqlite"));
            ImportedContext managed; ImportedContext native;
            using (var write = store.BeginWrite())
            {
                write.ImportCapturedSources(input.Capture, DateTimeOffset.UnixEpoch);
                managed = write.ImportEvaluatedContext(input.Managed, input.Capture);
                native = write.ImportEvaluatedContext(input.Native, input.Capture);
                var validation = write.CommitFoundation(); Check.True(validation.IsValid);
            }
            Check.Equal(2, store.ReadRows("project").Count); Check.Equal(2, store.ReadRows("build_context").Count);
            Check.Equal(163, managed.DependencyIds.Count); Check.Equal(0, native.DependencyIds.Count);
            Check.Equal(1, managed.UnitIds.Count); Check.Equal("known", managed.UnitCollectionState);
            Check.Equal(0, native.UnitIds.Count); Check.Equal("not_collected", native.UnitCollectionState);
            Check.Equal("native_input_discovery_not_run", native.UnitCollectionReason);
            Check.Equal(1, store.ReadRows("compilation_unit").Count);
            Check.True(store.ReadRows("compilation_unit").All(row => Text(row, "status") == "not_attempted"));
            Check.True(store.ReadRows("unit_input").All(row => row["observed_consumed"] is null));
            Check.True(store.ReadRows("field_state").Any(row => Text(row, "record_id") == native.ContextId
                && Text(row, "field_path") == "/compilation_unit/@collection/compilation_units"
                && Text(row, "state") == "not_collected" && Text(row, "reason") == "native_input_discovery_not_run"));

            var unitRows = store.ReadRows("unit_input").Where(row => Text(row, "unit_id") == managed.UnitIds.Single()).ToArray();
            var orderedSources = unitRows.Where(row => Text(row, "role") == "source").OrderBy(row => (long)row["ordinal"]!).ToArray();
            var expectedSources = input.Managed.Items.Where(item => item.Kind == "Compile").ToArray();
            Check.Equal(expectedSources.Length, orderedSources.Length);
            for (var index = 0; index < expectedSources.Length; index++)
            {
                var source = Captured(input.Capture, expectedSources[index].Metadata["FullPath"]);
                Check.Equal(source.FileVersionId, Text(orderedSources[index], "file_version_id"));
            }
            Check.Equal("Arithmetic.cs", Path.GetFileName(expectedSources[0].Metadata["FullPath"]));
            Check.Equal("Arithmetic.More.cs", Path.GetFileName(expectedSources[1].Metadata["FullPath"]));
            var references = unitRows.Where(row => Text(row, "role") == "reference").OrderBy(row => (long)row["ordinal"]!).ToArray();
            var dependencies = store.ReadRows("dependency").ToDictionary(row => Text(row, "id")!, StringComparer.Ordinal);
            Check.Equal(input.Managed.Providers.Count, references.Length);
            for (var index = 0; index < references.Length; index++)
            {
                var provider = input.Managed.Providers[index].Descriptor;
                var dependency = dependencies[Text(references[index], "dependency_id")!];
                Check.Equal(provider.GetProperty("logical_name").GetString(), Text(dependency, "selected_name"));
                Check.Equal(provider.GetProperty("content_fingerprint").GetProperty("value").GetString(), Text(dependency, "content_hash"));
                Check.Equal("8.0.19", Text(dependency, "selected_version"));
                Check.True(Text(dependency, "selected_path")!.StartsWith("<package:", StringComparison.Ordinal));
                Check.True(dependency["direct"] is null, "Resolved reference does not prove dependency directness.");
                Check.Equal(managed.ContextId, Text(dependency, "context_id"));
            }
            foreach (var pair in new[] { (input.Managed, managed), (input.Native, native) })
            {
                var options = store.ReadRows("context_setting").Where(row => Text(row, "context_id") == pair.Item2.ContextId
                    && Text(row, "category") == "compiler_option").OrderBy(row => (long)row["ordinal"]!).ToArray();
                var expected = pair.Item1.NormalizedOptions.EnumerateArray().ToArray();
                Check.Equal(expected.Length, options.Length);
                for (var index = 0; index < expected.Length; index++)
                {
                    var option = expected[index]; var path = option.GetProperty("kind").GetString() == "path";
                    Check.Equal(path ? option.GetProperty("option").GetString() : "", Text(options[index], "name"));
                    Check.Equal(path ? "<" + option.GetProperty("root_key").GetString() + ">/" + option.GetProperty("relative_path").GetString()
                        : option.GetProperty("value").GetString(), Text(options[index], "value"));
                }
                Check.Equal(pair.Item1.Tools.Count, pair.Item2.ToolIds.Count);
            }
            foreach (var tool in store.ReadRows("tool"))
            {
                var name = Text(tool, "name")!;
                if (name.StartsWith("analyzer:", StringComparison.Ordinal)) Check.Equal("analyzer", Text(tool, "role"));
                if (name.StartsWith("compiler:", StringComparison.Ordinal)) Check.Equal("compiler", Text(tool, "role"));
            }
            Check.Equal(1, store.ReadRows("tool").Count(row => Text(row, "name") == "MSBuild"));
            Check.Equal(0, store.ReadRows("operation").Count); Check.Equal(0, store.ReadRows("build_run").Count);
            var memberships = input.Capture.Files.ToDictionary(file => file.LogicalPath,
                file => (IReadOnlyList<ContextMembership>)[new(file.RootName == "pilot-csharp" ? managed.ContextId : native.ContextId)], StringComparer.Ordinal);
            var sealedCapture = await new CaptureService().SealAsync(input.Capture,
                new(input.Capture.SnapshotId, [managed.ContextId, native.ContextId], memberships));
            Check.Equal(input.Capture.SnapshotId, sealedCapture.SnapshotId);
            await File.WriteAllTextAsync(Path.Combine(input.Directory, "context-storage-evidence.json"), JsonSerializer.Serialize(new
            {
                state = "collecting_foundation_only", managed, native, source_files = input.Capture.Files.Count,
                tools = store.ReadRows("tool").Count, dependencies = dependencies.Count,
                managed_source_order = expectedSources.Select(item => item.Identity).ToArray(),
                managed_reference_order = input.Managed.Providers.Select(provider => provider.Descriptor.GetProperty("logical_name").GetString()).ToArray(),
                sealedCapture.IndexFingerprint, sealedCapture.ManifestHash,
                native_unit_state = "not_collected_until_native_input_discovery", operation_records = 0, observed_build_runs = 0
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        });
        yield return new("Repeated context import reuses stable records without operational output hash drift", async () =>
        {
            var input = await inputs.Value; using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite();
            write.ImportCapturedSources(input.Capture, DateTimeOffset.UnixEpoch);
            var first = write.ImportEvaluatedContext(input.Managed, input.Capture);
            var records = store.ReadRows("record").Count;
            var second = write.ImportEvaluatedContext(input.Managed with { RawOutputHash = new string('f', 64) }, input.Capture);
            Check.Equal(first.ContextId, second.ContextId); Check.Equal(records, store.ReadRows("record").Count);
            Check.True(write.CommitFoundation().IsValid);
        });
        yield return new("Missing reference descriptors cannot become a known empty reference set", async () =>
        {
            var input = await inputs.Value; using var store = CanonicalStore.CreateInMemory();
            using (var write = store.BeginWrite())
            {
                write.ImportCapturedSources(input.Capture, DateTimeOffset.UnixEpoch);
                Check.Throws<InvalidDataException>(() => write.ImportEvaluatedContext(input.Managed with { Providers = [] }, input.Capture));
                Check.Throws<InvalidOperationException>(() => write.CommitFoundation());
            }
            Check.Equal(0, store.ReadRows("record").Count);
        });
        yield return new("Unqueried item categories cannot be interpreted as complete context inputs", async () =>
        {
            var input = await inputs.Value; using var store = CanonicalStore.CreateInMemory();
            using (var write = store.BeginWrite())
            {
                write.ImportCapturedSources(input.Capture, DateTimeOffset.UnixEpoch);
                Check.Throws<InvalidDataException>(() => write.ImportEvaluatedContext(input.Managed with { EvaluatedItemKinds = new HashSet<string>() }, input.Capture));
            }
            Check.Equal(0, store.ReadRows("record").Count);
        });
        yield return new("Captured source mutation after evaluation rejects context import and rolls back", async () =>
        {
            var directory = NewDirectory(context, "changed-after-evaluation"); var copied = Path.Combine(directory, "csharp"); Directory.CreateDirectory(copied);
            foreach (var name in new[] { "Pilot.csproj", "Arithmetic.cs", "Arithmetic.More.cs" })
                File.Copy(Path.Combine(context.ProjectRoot, "tests", "fixtures", "pilot", "csharp", name), Path.Combine(copied, name));
            var evaluation = await Evaluate(Path.Combine(copied, "Pilot.csproj"), directory);
            await File.AppendAllTextAsync(Path.Combine(copied, "Arithmetic.cs"), "\n// changed after the evaluation checkpoint\n", new UTF8Encoding(false));
            var capture = await new CaptureService().CaptureAsync(new("context-mutation-test", [new("pilot-csharp", copied)],
                [new("pilot-csharp", "Pilot.csproj", Role: "build_definition"), new("pilot-csharp", "Arithmetic.cs", Language: "csharp"),
                 new("pilot-csharp", "Arithmetic.More.cs", Language: "csharp")]), Path.Combine(directory, "sources"));
            using var store = CanonicalStore.CreateInMemory();
            using (var write = store.BeginWrite())
            {
                write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch);
                Check.Throws<InvalidDataException>(() => write.ImportEvaluatedContext(evaluation, capture));
                Check.Equal(0, store.ReadRows("build_context").Count);
                Check.Throws<InvalidOperationException>(() => write.CommitFoundation());
            }
            Check.Equal(0, store.ReadRows("record").Count);
        });
    }

    private static async Task<Inputs> Acquire(TestContext context)
    {
        var directory = NewDirectory(context, "evaluated");
        var managedRoot = Path.Combine(context.ProjectRoot, "tests", "fixtures", "pilot", "csharp");
        var nativeRoot = Path.Combine(context.ProjectRoot, "tests", "fixtures", "pilot", "native");
        var managed = await Evaluate(Path.Combine(managedRoot, "Pilot.csproj"), Path.Combine(directory, "managed"));
        var native = await Evaluate(Path.Combine(nativeRoot, "Pilot.vcxproj"), Path.Combine(directory, "native"));
        var capture = await new CaptureService().CaptureAsync(new("context-storage-pilot",
            [new("pilot-csharp", managedRoot), new("pilot-native", nativeRoot)],
            [new("pilot-csharp", "Pilot.csproj", Role: "build_definition"), new("pilot-csharp", "Arithmetic.cs", Language: "csharp"),
             new("pilot-csharp", "Arithmetic.More.cs", Language: "csharp"), new("pilot-native", "Pilot.vcxproj", Role: "build_definition"),
             new("pilot-native", "Arithmetic.cpp", Language: "cpp"), new("pilot-native", "Arithmetic.hpp", Role: "header", Language: "cpp")]),
            Path.Combine(directory, "sources"));
        return new(capture, managed, native, directory);
    }
    private static Task<MsBuildEvaluation> Evaluate(string project, string directory)
    {
        var msbuild = Environment.GetEnvironmentVariable("PL_MSBUILD")
            ?? @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe";
        return new MsBuildEvaluator().EvaluateAsync(new(msbuild, project, "Release", "x64", Path.Combine(directory, "obj"), Path.Combine(directory, "bin")));
    }
    private static CapturedFile Captured(CapturedInputs capture, string path)
        => capture.Files.Single(file => string.Equals(Path.GetFullPath(file.OriginalPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    private static string NewDirectory(TestContext context, string label)
    { var path = Path.Combine(context.ScratchRoot, "context-storage", label + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static string? Text(IReadOnlyDictionary<string, object?> row, string column) => row[column] as string;
}

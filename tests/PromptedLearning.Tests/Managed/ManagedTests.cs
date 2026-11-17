using System.Text.Json;
using PromptedLearning.Core;
using PromptedLearning.Managed;

namespace PromptedLearning.Tests;

public static class ManagedTests
{
    public sealed record Fixture(ManagedAnalysisRequest Request, string Folder);
    private static JsonElement[] Rows(SemanticUnitResult result, string family) => result.Facts.GetProperty(family).EnumerateArray().ToArray();
    private static string Text(JsonElement row, string field) => row.GetProperty(field).GetString()!;
    public static async Task<Fixture> Acquire(TestContext context)
    {
        var folder = Path.Combine(context.ScratchRoot, "managed-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(folder, "relocated"); Directory.CreateDirectory(input);
        foreach (var name in new[] { "Pilot.csproj", "Arithmetic.cs", "Arithmetic.More.cs" })
            File.Copy(Path.Combine(context.ProjectRoot, "tests", "fixtures", "pilot", "csharp", name), Path.Combine(input, name));
        var msbuild = Environment.GetEnvironmentVariable("PL_MSBUILD") ?? @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe";
        var evaluation = await new MsBuildEvaluator().EvaluateAsync(new(msbuild, Path.Combine(input, "Pilot.csproj"), "Release", "x64",
            Path.Combine(folder, "obj"), Path.Combine(folder, "bin"), AnalysisTools: ManagedPilotAdapter.GetAnalysisTools()));
        var capture = await new CaptureService().CaptureAsync(new("managed-pilot-test", [new("pilot-csharp", input)],
            [new("pilot-csharp", "Pilot.csproj", "build_definition", "unknown"), new("pilot-csharp", "Arithmetic.cs", Language: "csharp"),
             new("pilot-csharp", "Arithmetic.More.cs", Language: "csharp")]), Path.Combine(folder, "sources"));
        var projectFile = capture.Files.Single(file => file.RelativePath == "Pilot.csproj");
        var projectId = StableId.Create("project/1", new { snapshot_id = capture.SnapshotId, definition_file_version_id = projectFile.FileVersionId,
            system_kind = "msbuild", logical_project_key = projectFile.LogicalPath });
        var contextId = StableId.Create("build_context/1", MsBuildEvaluator.ContextComponents(evaluation, capture.SnapshotId, projectId));
        var files = evaluation.Items.Where(item => item.Kind == "Compile").Select(item => capture.Files.Single(file =>
            file.OriginalPath.Equals(item.Metadata["FullPath"], StringComparison.OrdinalIgnoreCase))).ToArray();
        var unitInputs = files.Select(file => (object)new { role = "source", file_version_id = file.FileVersionId, provider = (object?)null })
            .Concat(evaluation.Providers.Select(provider => (object)new { role = "reference", file_version_id = (string?)null, provider = provider.Descriptor })).ToArray();
        var unit = StableId.Create("compilation_unit/1", new { context_id = contextId, language = "csharp", inputs = unitInputs, output_identity = "Pilot" });
        var sealedCapture = await new CaptureService().SealAsync(capture, new(capture.SnapshotId, [contextId],
            capture.Files.ToDictionary(file => file.LogicalPath, _ => (IReadOnlyList<ContextMembership>)[new(contextId)])));
        var sources = files.Select((file, ordinal) => new ManagedSourceInput("file:managed/" + ordinal, file.FileVersionId)).ToArray();
        var request = new ManagedAnalysisRequest(evaluation, sealedCapture, contextId, unit, "", ManagedPilotAdapter.ProducerFromEvaluation(evaluation),
            sources, sources.Select(source => source.FileKey).ToArray());
        return new(request with { SemanticInputFingerprint = ManagedPilotAdapter.SemanticInputFingerprint(request) }, folder);
    }

    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        var fixture = new Lazy<Task<Fixture>>(() => Acquire(context));
        var success = new Lazy<Task<SemanticUnitResult>>(async () => await new ManagedPilotAdapter().AnalyzeAsync((await fixture.Value).Request));
        yield return new("managed.real_symbols_partial_type_overloads_and_declared_owners", async () =>
        {
            var result = await success.Value;
            CompilerFacts.ValidateSchema(result);
            Check.Equal("complete", result.Status);
            Check.Equal("4.14.0.0", result.Producer.CompilerVersion);
            var entities = Rows(result, "entities");
            var type = entities.Single(row => Text(row, "entity_kind") == "class");
            Check.Equal("T:Pilot.Arithmetic", Text(type, "compiler_key"));
            Check.Equal(2, Rows(result, "appearances").Count(row => Text(row, "entity_key") == Text(type, "key")));
            var methods = entities.Where(row => Text(row, "entity_kind") == "method").ToArray();
            Check.Equal(4, methods.Length); Check.Equal(4, Rows(result, "signatures").Length);
            Check.True(methods.All(row => Text(row, "owner_entity_key") == Text(type, "key")));
            var adds = methods.Where(row => Text(row, "written_name") == "Add").ToArray();
            Check.Equal(2, adds.Length); Check.True(Text(adds[0], "compiler_key") != Text(adds[1], "compiler_key"));
            Check.True(methods.Any(row => Text(row, "compiler_key") == "M:Pilot.Arithmetic.Add(System.Int32,System.Int32)"));
            Check.True(methods.Any(row => Text(row, "compiler_key") == "M:Pilot.Arithmetic.Add(System.Int64,System.Int64)"));
            Check.Equal(6, entities.Count(row => Text(row, "entity_kind") == "parameter"));
            Check.Equal(1, entities.Count(row => Text(row, "entity_kind") == "local_variable"));
            Check.True(entities.All(row => new[] { "namespace", "class", "method", "parameter", "local_variable" }.Contains(Text(row, "entity_kind"))),
                "Every emitted pilot entity kind must use the frozen shared vocabulary.");
            Check.True(entities.All(row => row.GetProperty("linkage").ValueKind == JsonValueKind.Null), "C# linkage is inapplicable.");
            foreach (var entity in entities.Where(row => Text(row, "entity_kind") is "namespace" or "parameter" or "local_variable"))
                Check.Equal(JsonValueKind.Null, entity.GetProperty("access").ValueKind);
            Check.True(methods.All(row => Text(row, "access") == "public"));
            Check.Equal("public", Text(type, "access"));
            var provider = type.GetProperty("compiler_type_key").GetProperty("provider");
            Check.Equal("known", Text(provider.GetProperty("version"), "state"));
            Check.Equal("0.0.0.0", Text(provider.GetProperty("version"), "value"));
            Check.Equal("not_collected", Text(provider.GetProperty("content_fingerprint"), "state"));
            Check.Equal("output_not_emitted", Text(provider.GetProperty("content_fingerprint"), "reason"));
            Check.True(Rows(result, "signatures").All(row => row.GetProperty("compiler_signature_key").GetProperty("language_linkage").ValueKind == JsonValueKind.Null));
            var modifiers = Rows(result, "entity_modifiers").Where(row => Text(row, "entity_key") == Text(type, "key")).Select(row => Text(row, "modifier"));
            Check.True(modifiers.SequenceEqual(new[] { "public", "static", "partial" }));
        });
        yield return new("managed.choose_exact_independent_operation_oracle_and_byte_spans", async () =>
        {
            var result = await success.Value; var input = await fixture.Value;
            var choose = Rows(result, "entities").Single(row => Text(row, "entity_kind") == "method" && Text(row, "written_name") == "Choose");
            var body = Rows(result, "bodies").Single(row => Text(row, "owner_entity_key") == Text(choose, "key"));
            var operations = Rows(result, "operations").Where(row => Text(row, "body_key") == Text(body, "key")).ToArray();
            Check.Equal(22, operations.Length); Check.Equal(0, operations.Count(row => row.GetProperty("implicit").GetBoolean()));
            using var oracle = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(context.ProjectRoot, "docs", "m0", "evidence", "roslyn-pilot-operation-oracle.json")));
            ManagedChooseOracle.Assert(result, oracle.RootElement);
            var source = input.Request.Capture.Inputs.Files.Single(file => file.RelativePath == "Arithmetic.cs");
            await using var archive = await SourceArchive.OpenAsync(input.Request.Capture.ArchiveRoot, input.Request.Capture.IndexFingerprint);
            var bytes = await archive.ReadBytesAsync(source.FileVersionId, 0, source.ByteLength!.Value, 1048576);
            var decoded = SourceDecoder.Decode(bytes.Bytes).Source!;
            var spans = Rows(result, "spans").ToDictionary(row => Text(row, "key"));
            var expected = oracle.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
            for (var index = 0; index < expected.Length; index++)
            {
                var node = operations[index]; var reference = expected[index]; var span = spans[Text(node, "span_key")];
                Check.Equal(Text(reference, "kind"), Text(node, "compiler_kind"));
                Check.Equal(Text(reference, "path"), string.Join('/', node.GetProperty("structural_path").EnumerateArray().Select(value => value.GetInt32())));
                Check.Equal(decoded.ByteOffset(reference.GetProperty("start_utf16").GetInt32()), span.GetProperty("start_byte").GetInt64());
                Check.Equal(decoded.ByteOffset(reference.GetProperty("end_utf16").GetInt32()), span.GetProperty("end_byte").GetInt64());
            }
            var call = operations.Single(row => Text(row, "operation_kind") == "invocation");
            var target = Rows(result, "entities").Single(row => Text(row, "key") == Text(call, "referenced_entity_key"));
            Check.Equal("M:Pilot.Arithmetic.Add(System.Int32,System.Int32)", Text(target, "compiler_key"));
            var arguments = call.GetProperty("operands").EnumerateArray().ToArray();
            Check.Equal(2, arguments.Length); Check.True(arguments.All(row => row.GetProperty("parameter_entity_key").ValueKind == JsonValueKind.String));
            Check.Equal("call", Text(call, "semantic_role"));
            Check.Equal("value", Text(call, "child_role"));
            var byPath = operations.ToDictionary(row => string.Join('/', row.GetProperty("structural_path").EnumerateArray().Select(value => value.GetInt32())));
            foreach (var path in new[] { "0/1/0/0", "0/1/1/0/1/0/0", "0/2/0" })
                Check.Equal("read", Text(byPath[path], "semantic_role"));
            foreach (var path in new[] { "0/1/1/0/0", "0/1/2/0/0" })
            {
                Check.Equal("destination", Text(byPath[path], "semantic_role"));
                Check.Equal("destination", Text(byPath[path], "child_role"));
            }
            Check.Equal("left", Text(byPath["0/1/0/0"], "child_role"));
            Check.Equal("value", Text(byPath["0/1/1/0/1/0/0"], "child_role"));
            Check.Equal("return", Text(byPath["0/2/0"], "child_role"));
            foreach (var literal in Rows(result, "operations").Where(row => Text(row, "operation_kind") == "literal"))
            {
                Check.Equal("signed_integer", Text(literal.GetProperty("constant"), "kind"));
                Check.True(new[] { "0", "1" }.Contains(Text(literal.GetProperty("constant"), "value")));
            }
        });
        yield return new("managed.coverage_absence_and_later_flow_boundaries", async () =>
        {
            var result = await success.Value;
            Check.Equal(0, Rows(result, "control_flow").Length); Check.Equal(0, Rows(result, "annotations").Length); Check.Equal(0, Rows(result, "preprocessing").Length);
            var coverage = Rows(result, "coverage"); Check.Equal(18, coverage.Length);
            foreach (var row in coverage)
            {
                var later = Text(row, "feature") is "control_flow" or "storage_events";
                Check.Equal(later ? "not_attempted" : "complete", Text(row, "status"));
                if (later) Check.Equal("not_requested", Text(row, "reason"));
            }
            Check.Equal(0, Rows(result, "diagnostics").Count(row => Text(row, "severity") == "error"));
            Check.Equal(0, Rows(result, "external_symbols").Length);
            Check.True(result.ConsumedFileKeys.SequenceEqual((await fixture.Value).Request.ExpectedConsumedFileKeys));
        });
        yield return new("managed.all_bodies_operand_role_ordinals_and_implicit_origin_preimages", async () =>
        {
            var result = await success.Value; var request = (await fixture.Value).Request;
            var operations = Rows(result, "operations"); var spans = Rows(result, "spans").ToDictionary(row => Text(row, "key"));
            foreach (var operation in operations)
            {
                foreach (var group in operation.GetProperty("operands").EnumerateArray().GroupBy(row => Text(row, "role")))
                    Check.True(group.Select(row => row.GetProperty("ordinal").GetInt32()).SequenceEqual(Enumerable.Range(0, group.Count())),
                        "Operand ordinals are contiguous within each semantic role, independently of source child ordinals.");
                var children = operations.Where(row => row.GetProperty("parent_operation_key").GetString() == Text(operation, "key")).ToArray();
                Check.True(children.Select(row => row.GetProperty("child_ordinal").GetInt32()).SequenceEqual(Enumerable.Range(0, children.Length)));
            }
            var fileVersions = request.Sources.ToDictionary(source => source.FileKey, source => source.FileVersionId);
            var implicitOperations = operations.Where(row => row.GetProperty("implicit").GetBoolean()).ToArray();
            Check.True(implicitOperations.Length > 0, "Expression-bodied methods retain their implicit semantic wrappers.");
            Check.Equal(3, implicitOperations.Select(row => Text(row, "body_key")).Distinct().Count());
            foreach (var operation in implicitOperations)
            {
                var span = spans[Text(operation, "span_key")]; var cause = spans[Text(operation, "cause_span_key")];
                Check.Equal("implicit_cause", Text(span, "origin_kind"));
                Check.Equal(Text(cause, "key"), Text(span, "cause_span_key"));
                var stack = span.GetProperty("origin_stack").EnumerateArray().ToArray(); Check.Equal(1, stack.Length);
                var frame = stack[0]; Check.Equal("written", Text(frame, "origin_kind"));
                var expectedCause = new { file_version_id = fileVersions[Text(frame, "file_key")],
                    start_byte = frame.GetProperty("start_byte").GetInt64(), end_byte = frame.GetProperty("end_byte").GetInt64(),
                    origin_kind = Text(frame, "origin_kind"), origin_stack = Array.Empty<object>() };
                var actualCause = new { file_version_id = fileVersions[Text(cause, "file_key")],
                    start_byte = cause.GetProperty("start_byte").GetInt64(), end_byte = cause.GetProperty("end_byte").GetInt64(),
                    origin_kind = Text(cause, "origin_kind"), origin_stack = Array.Empty<object>() };
                Check.Equal(0, cause.GetProperty("origin_stack").GetArrayLength());
                Check.Equal(StableId.Create("source_span/1", expectedCause), StableId.Create("source_span/1", actualCause));
            }
        });
        yield return new("managed.working_source_mutation_cannot_change_captured_semantics", async () =>
        {
            var input = await fixture.Value; var previous = await success.Value;
            await File.AppendAllTextAsync(input.Request.Capture.Inputs.Files.Single(file => file.RelativePath == "Arithmetic.cs").OriginalPath, " invalid working source");
            var after = await new ManagedPilotAdapter().AnalyzeAsync(input.Request);
            Check.Equal("complete", after.Status);
            Check.True(CanonicalJson.Serialize(previous.Facts).SequenceEqual(CanonicalJson.Serialize(after.Facts)));
        });
        yield return new("managed.mismatched_scope_and_consumed_inputs_fail_without_success", async () =>
        {
            var request = (await fixture.Value).Request;
            foreach (var bad in new[] { request with { ContextId = "build_context:" + new string('f', 64) },
                request with { SemanticInputFingerprint = new string('f', 64) }, request with { ExpectedConsumedFileKeys = [request.Sources[0].FileKey] },
                request with { Sources = [request.Sources[0], request.Sources[0]] },
                request with { Sources = [request.Sources[0] with { FileVersionId = "file_version:" + new string('f', 64) }, request.Sources[1]] },
                request with { Sources = [request.Sources[0] with { FileKey = "file:" + new string('a', 64) }, request.Sources[1]] } })
            {
                var result = await new ManagedPilotAdapter().AnalyzeAsync(bad);
                Check.Equal("failed", result.Status); Check.Equal(0, Rows(result, "entities").Length);
            }
        });
        yield return new("managed.missing_duplicate_mismatched_tools_and_unsupported_settings_fail_explicitly", async () =>
        {
            var request = (await fixture.Value).Request;
            var tool = request.Evaluation.Tools.Single(value => Text(value.Descriptor, "name") == "analysis:managed-adapter");
            var missing = request.Evaluation with { Tools = request.Evaluation.Tools.Where(value => value != tool).ToArray() };
            var duplicate = request.Evaluation with { Tools = request.Evaluation.Tools.Append(tool).ToArray() };
            Check.Throws<InvalidDataException>(() => ManagedPilotAdapter.ProducerFromEvaluation(missing));
            Check.Throws<InvalidDataException>(() => ManagedPilotAdapter.ProducerFromEvaluation(duplicate));
            var mismatch = request.Evaluation with { Tools = request.Evaluation.Tools.Select(value => value == tool
                ? value with { PhysicalPath = Path.Combine(context.ScratchRoot, "unexpected-adapter.dll") } : value).ToArray() };
            var properties = request.Evaluation.Properties.ToDictionary(pair => pair.Key, pair => pair.Value);
            properties["RuntimeIdentifier"] = "win-x64";
            foreach (var evaluation in new[] { missing, duplicate, mismatch, request.Evaluation with { Properties = properties } })
            {
                var result = await new ManagedPilotAdapter().AnalyzeAsync(request with { Evaluation = evaluation });
                Check.Equal("failed", result.Status); Check.Equal(0, Rows(result, "entities").Length);
                Check.Equal(0, result.ConsumedFileKeys.Count);
            }
        });
        yield return new("managed.reference_bytes_reverified_and_precancel_explicit", async () =>
        {
            var input = await fixture.Value; var request = input.Request;
            var badPath = Path.Combine(input.Folder, "changed-reference.dll"); await File.WriteAllTextAsync(badPath, "not the evaluated bytes");
            var providers = request.Evaluation.Providers.ToArray(); providers[0] = providers[0] with { PhysicalPath = badPath };
            var failed = await new ManagedPilotAdapter().AnalyzeAsync(request with { Evaluation = request.Evaluation with { Providers = providers } });
            Check.Equal("failed", failed.Status); Check.Equal(0, Rows(failed, "operations").Length);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var result = await new ManagedPilotAdapter().AnalyzeAsync(request, cancelled.Token);
            Check.Equal("cancelled", result.Status); Check.Equal("cancelled", result.StopReason);
            Check.Equal(0, Rows(result, "entities").Length);
        });
    }
}

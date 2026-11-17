using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PromptedLearning.Core;

namespace PromptedLearning.Tests;

public static class NativeHostTests
{
    public sealed record Fixture(MsBuildEvaluation Evaluation, SealedCapture Capture, string ContextId, string Folder);
    public static async Task<Fixture> Acquire(TestContext context)
    {
        var folder = Path.Combine(context.ScratchRoot, "native-host-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(folder, "relocated"); Directory.CreateDirectory(input);
        foreach (var name in new[] { "Pilot.vcxproj", "Arithmetic.cpp", "Arithmetic.hpp" })
            File.Copy(Path.Combine(context.ProjectRoot, "tests/fixtures/pilot/native", name), Path.Combine(input, name));
        var msbuild = Environment.GetEnvironmentVariable("PL_MSBUILD") ?? @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe";
        var evaluation = await new MsBuildEvaluator().EvaluateAsync(new(msbuild, Path.Combine(input, "Pilot.vcxproj"), "Release", "x64",
            Path.Combine(folder, "obj"), Path.Combine(folder, "bin"), AnalysisTools: NativePilotAdapter.GetAnalysisTools(NativeRequestFixture.Executable)));
        var capture = await new CaptureService().CaptureAsync(new("native-host-pilot", [new("native", input)],
            [new("native", "Pilot.vcxproj", "build_definition"), new("native", "Arithmetic.cpp", Language: "cpp"), new("native", "Arithmetic.hpp", "header", "cpp")]), Path.Combine(folder, "sources"));
        var project = capture.Files.Single(f => f.RelativePath == "Pilot.vcxproj");
        var projectId = StableId.Create("project/1", new { snapshot_id = capture.SnapshotId, definition_file_version_id = project.FileVersionId,
            system_kind = "msbuild", logical_project_key = project.LogicalPath });
        var contextId = StableId.Create("build_context/1", MsBuildEvaluator.ContextComponents(evaluation, capture.SnapshotId, projectId));
        var sealedCapture = await new CaptureService().SealAsync(capture, new(capture.SnapshotId, [contextId],
            capture.Files.ToDictionary(f => f.LogicalPath, _ => (IReadOnlyList<ContextMembership>)[new(contextId)])));
        return new(evaluation, sealedCapture, contextId, folder);
    }

    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        var fixture = new Lazy<Task<Fixture>>(() => Acquire(context));
        var successful = new Lazy<Task<NativePilotAnalysis>>(async () =>
        {
            var input = await fixture.Value;
            // Remove working sources after capture: production host must use the immutable archive only.
            foreach (var name in new[] { "Arithmetic.cpp", "Arithmetic.hpp" })
                await File.WriteAllTextAsync(Path.Combine(input.Folder, "relocated", name), "working files changed after capture");
            return await new NativePilotAdapter().AnalyzeAsync(input.Evaluation, input.Capture, input.ContextId);
        });
        yield return new("Native host discovers captured closure and final identity before semantic acceptance", async () =>
        {
            var analysis = await successful.Value;
            Check.Equal("complete", analysis.Unit.Result.Status);
            Check.True(analysis.DiscoveryRequest.GetProperty("unit_id").GetString() != analysis.FinalRequest.GetProperty("unit_id").GetString(), "Consumed header changes provisional unit identity.");
            Check.Equal(2, analysis.Unit.Binding.ExpectedConsumedFileKeys.Count);
            var inputs = analysis.UnitIdentityInput.GetProperty("components").GetProperty("inputs");
            Check.Equal("main", inputs[0].GetProperty("role").GetString()); Check.Equal("included_header", inputs[1].GetProperty("role").GetString());
            StableId.Verify(analysis.Unit.Result.UnitId, analysis.UnitIdentityInput);
            Check.Equal(4, analysis.Unit.Result.Facts.GetProperty("bodies").GetArrayLength());
            await File.WriteAllBytesAsync(Path.Combine(context.ScratchRoot, "native-host-final-request.json"), CanonicalJson.Serialize(analysis.FinalRequest, true));
            await File.WriteAllBytesAsync(Path.Combine(context.ScratchRoot, "native-host-final-result.json"), CanonicalJson.Serialize(analysis.FinalResult, true));
            await File.WriteAllBytesAsync(Path.Combine(context.ScratchRoot, "native-host-unit-identity.json"), CanonicalJson.Serialize(analysis.UnitIdentityInput, true));
        });
        yield return new("Native host rejects invalid response framing, echoes and provisional payloads", async () =>
        {
            var analysis = await successful.Value; var bytes = CanonicalJson.Serialize(analysis.FinalResult, true);
            foreach (var corrupt in new[] { bytes[..^1], bytes.Concat(bytes).ToArray(), Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\n", "\r\n", StringComparison.Ordinal)) })
                Check.Throws<InvalidDataException>(() => NativeWorkerWire.ValidateResponse(analysis.FinalRequest, corrupt));
            foreach (var name in new[] { "request_id", "request_fingerprint", "semantic_input_fingerprint", "snapshot_id", "context_id", "unit_id" })
            {
                var node = JsonNode.Parse(analysis.FinalResult.GetRawText())!.AsObject();
                node[name] = name.EndsWith("fingerprint", StringComparison.Ordinal) ? new string('0', 64) : name == "request_id" ? "wrong" : "scope:" + new string('0', 64);
                Check.Throws<InvalidDataException>(() => NativeWorkerWire.ValidateResponse(analysis.FinalRequest, Bytes(node)));
            }
            Check.Throws<InvalidDataException>(() => NativeWorkerWire.ToSemanticResult(analysis.DiscoveryRequest, analysis.DiscoveryResult));
            var provisional = JsonNode.Parse(analysis.DiscoveryResult.GetRawText())!.AsObject();
            provisional["operations"] = JsonNode.Parse(analysis.FinalResult.GetProperty("operations").GetRawText());
            Check.Throws<InvalidDataException>(() => NativeWorkerWire.ValidateResponse(analysis.DiscoveryRequest, Bytes(provisional)));
        });
        yield return new("Native host validates binary hash before execution and honors cancellation", async () =>
        {
            var analysis = await successful.Value;
            var node = JsonNode.Parse(analysis.FinalRequest.GetRawText())!.AsObject(); node["worker"]!["binary_sha256"] = new string('0', 64);
            using var parsed = CanonicalJson.Parse(Bytes(node)); var bad = NativeWorkerWire.Stamp(parsed.RootElement);
            await Check.ThrowsAsync<InvalidDataException>(() => new NativeWorkerHost().RunAsync(NativeRequestFixture.Executable, bad));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => new NativeWorkerHost().RunAsync(NativeRequestFixture.Executable, analysis.FinalRequest, cancelled.Token));
        });
        yield return new("Native discovery preserves only located diagnostic evidence for missing capture", async () =>
        {
            var node = NativeRequestFixture.Create(context, "discovery"); node["files"]!.AsArray().RemoveAt(1);
            node["expected_consumed_file_keys"] = new JsonArray("file:main"); NativeRequestFixture.Fingerprint(node);
            using var request = CanonicalJson.Parse(Bytes(node));
            var execution = await new NativeWorkerHost().RunAsync(NativeRequestFixture.Executable, request.RootElement);
            Check.True(execution.WireResult.GetProperty("discovery_requests").GetArrayLength() > 0);
            Check.True(execution.WireResult.GetProperty("diagnostics").GetArrayLength() > 0);
            Check.True(execution.WireResult.GetProperty("spans").GetArrayLength() > 0, "Missing include diagnostics retain exact captured location.");
            var result = JsonNode.Parse(execution.WireResult.GetRawText())!.AsObject();
            var unrelated = result["spans"]![0]!.DeepClone(); unrelated["key"] = "span:unrelated";
            result["spans"]!.AsArray().Add(unrelated);
            Check.Throws<InvalidDataException>(() => NativeWorkerWire.ValidateResponse(request.RootElement, Bytes(result)));
        });
    }
    private static byte[] Bytes(JsonNode node)
    {
        using var parsed = CanonicalJson.Parse(Encoding.UTF8.GetBytes(node.ToJsonString())); return CanonicalJson.Serialize(parsed.RootElement, true);
    }
}

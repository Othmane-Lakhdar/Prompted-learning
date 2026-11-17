using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PromptedLearning.Core;

namespace PromptedLearning.Tests;

public static class NativeTests
{
    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        yield return new("Captured native pilot preserves compiler facts and original UTF8 spans", async () =>
        {
            var request = NativeRequestFixture.Create(context, "final");
            var run = await NativeRequestFixture.Run(request);
            Check.Equal(0, run.ExitCode, run.Error);
            using var document = CanonicalJson.Parse(run.Output);
            var result = document.RootElement;
            ContractCatalog.Default.Validate("native-worker-result", result);
            Check.Equal("complete", result.GetProperty("status").GetString(), run.Error + result.GetRawText());
            var entities = Rows(result, "entities");
            Check.True(entities.All(row => row.GetProperty("access").ValueKind == JsonValueKind.Null), "Native free declarations have inapplicable access.");
            var functions = entities.Where(row => Text(row, "entity_kind") == "function").ToArray();
            Check.Equal(4, functions.Length);
            Check.Equal(4, functions.Select(row => Text(row, "compiler_key")).Distinct().Count());
            Check.Equal(6, entities.Count(row => Text(row, "entity_kind") == "parameter"));
            Check.Equal(1, entities.Count(row => Text(row, "entity_kind") == "local_variable"));
            var choose = functions.Single(row => Text(row, "written_name") == "Choose");
            var body = Rows(result, "bodies").Single(row => Text(row, "owner_entity_key") == Text(choose, "key"));
            var operations = Rows(result, "operations").Where(row => Text(row, "body_key") == Text(body, "key")).ToArray();
            Check.Equal(22, operations.Length);
            Check.Equal(4, operations.Count(row => Text(row, "compiler_kind") == "ImplicitCastExpr"));
            Check.Equal(3, operations.Count(row => Text(row, "operator_kind") == "LValueToRValue"));
            Check.Equal(1, operations.Count(row => Text(row, "operator_kind") == "FunctionToPointerDecay"));
            Check.Equal(1, operations.Count(row => Text(row, "compiler_kind") == "VarDecl"));
            NativeChooseOracle.Assert(context, result, body, operations);
            Check.Equal(2, operations.Count(row => Text(row, "semantic_role") == "destination"));
            var call = operations.Single(row => Text(row, "operation_kind") == "invocation");
            Check.Equal("call", Text(call, "semantic_role"));
            var target = functions.Single(row => Text(row, "key") == Text(call, "referenced_entity_key"));
            Check.Equal("Add", Text(target, "written_name"));
            Check.Equal("int", target.GetProperty("compiler_signature_key").GetProperty("parameters")[0].GetProperty("type").GetProperty("name").GetString());
            foreach (var function in functions)
            {
                var appearances = Rows(result, "appearances").Where(row => Text(row, "entity_key") == Text(function, "key")).ToArray();
                Check.Equal(1, appearances.Count(row => Text(row, "kind") == "declaration"));
                Check.Equal(1, appearances.Count(row => Text(row, "kind") == "definition"));
            }
            foreach (var parameter in entities.Where(row => Text(row, "entity_kind") == "parameter"))
                Check.Equal(2, Rows(result, "appearances").Count(row => Text(row, "entity_key") == Text(parameter, "key") && Text(row, "kind") == "declaration"));
            var source = File.ReadAllBytes(Path.Combine(context.ProjectRoot, "tests/fixtures/pilot/native/Arithmetic.cpp"));
            var returnOp = operations.Single(row => Text(row, "compiler_kind") == "ReturnStmt");
            var span = Rows(result, "spans").Single(row => Text(row, "key") == Text(returnOp, "span_key"));
            var spanText = Encoding.UTF8.GetString(source.AsSpan(span.GetProperty("start_byte").GetInt32(), span.GetProperty("end_byte").GetInt32() - span.GetProperty("start_byte").GetInt32()));
            Check.Equal("return result", spanText);
            var pp = Rows(result, "preprocessing");
            Check.Equal(1, pp.Count(row => Text(row, "event_kind") == "include" && Text(row, "resolved_file_key") == "file:header"));
            Check.Equal(1, pp.Count(row => Text(row, "event_kind") == "pragma" && Text(row, "written_name") == "once"));
            Check.Equal(2, Rows(result, "annotations").Count(row => Text(row, "written_name") == "noinline" && row.GetProperty("attribute_type_key").ValueKind == JsonValueKind.Null));
            Check.Equal(1, Rows(result, "entity_modifiers").Count(row => Text(row, "modifier") == "noinline"));
            Check.Equal(0, Rows(result, "control_flow").Length);
            Check.True(Rows(result, "coverage").Where(row => Text(row, "feature") is "control_flow" or "storage_events")
                .All(row => Text(row, "status") == "not_attempted" && Text(row, "reason") == "not_requested"));
            var second = await NativeRequestFixture.Run(request);
            Check.True(run.Output.AsSpan().SequenceEqual(second.Output), "Same captured request must produce byte-identical deterministic facts.");
            await File.WriteAllBytesAsync(Path.Combine(context.ScratchRoot, "native-pilot-request.json"), CanonicalJson.Serialize(JsonSerializer.SerializeToElement(request), true));
            await File.WriteAllBytesAsync(Path.Combine(context.ScratchRoot, "native-pilot-result.json"), run.Output);
            await File.WriteAllTextAsync(Path.Combine(context.ScratchRoot, "native-worker-version.json"), JsonSerializer.Serialize(new
            {
                binary_sha256 = CanonicalJson.Sha256(File.ReadAllBytes(NativeRequestFixture.Executable)),
                product_version = FileVersionInfo.GetVersionInfo(NativeRequestFixture.Executable).ProductVersion,
                clang_version = "19.1.7"
            }));
        });

        yield return new("Native discovery uses only captured buffers and final input set is exact", async () =>
        {
            var missing = NativeRequestFixture.Create(context, "discovery");
            missing["files"]!.AsArray().RemoveAt(1);
            missing["expected_consumed_file_keys"] = new JsonArray("file:main");
            NativeRequestFixture.Fingerprint(missing);
            var discoveryRun = await NativeRequestFixture.Run(missing, Path.Combine(context.ProjectRoot, "tests/fixtures/pilot/native"));
            Check.Equal(0, discoveryRun.ExitCode, discoveryRun.Error);
            using var missingResult = CanonicalJson.Parse(discoveryRun.Output);
            Check.True(Rows(missingResult.RootElement, "discovery_requests").Length > 0, discoveryRun.Error + missingResult.RootElement.GetRawText());
            Check.Equal(0, Rows(missingResult.RootElement, "entities").Length);
            var diagnosticSpans = Rows(missingResult.RootElement, "diagnostics").Select(row => Text(row, "span_key")).Where(key => key is not null).ToHashSet();
            Check.True(Rows(missingResult.RootElement, "spans").All(row => diagnosticSpans.Contains(Text(row, "key"))),
                "Missing-include discovery retains only its located diagnostic evidence.");
            var discovery = NativeRequestFixture.Create(context, "discovery");
            var discovered = await NativeRequestFixture.Run(discovery);
            Check.Equal(0, discovered.ExitCode, discovered.Error);
            using var discoveredResult = CanonicalJson.Parse(discovered.Output);
            Check.Equal(2, discoveredResult.RootElement.GetProperty("consumed_file_keys").GetArrayLength());
            Check.Equal(0, Rows(discoveredResult.RootElement, "operations").Length);
            Check.Equal(0, Rows(discoveredResult.RootElement, "spans").Length);
            Check.Equal(0, Rows(discoveredResult.RootElement, "preprocessing").Length);
            var wrongSet = NativeRequestFixture.Create(context, "final");
            wrongSet["expected_consumed_file_keys"] = new JsonArray("file:main");
            NativeRequestFixture.Fingerprint(wrongSet);
            var mismatch = await NativeRequestFixture.Run(wrongSet);
            Check.Equal(0, mismatch.ExitCode, mismatch.Error);
            using var mismatchResult = CanonicalJson.Parse(mismatch.Output);
            Check.Equal("failed", Text(mismatchResult.RootElement, "status"));
            Check.Equal("consumed_input_set_mismatch", Text(mismatchResult.RootElement, "stop_reason"));
            Check.Equal(0, Rows(mismatchResult.RootElement, "entities").Length);
            Check.Equal(0, Rows(mismatchResult.RootElement, "spans").Length);
        });

        yield return new("Native rejects corrupt framing, duplicate keys, number coercion and unknown properties", async () =>
        {
            var good = Encoding.UTF8.GetString(CanonicalJson.Serialize(JsonSerializer.SerializeToElement(NativeRequestFixture.Create(context, "final")), true));
            var cases = new[]
            {
                good.TrimEnd('\n'), good + "{}\n", good.Replace("\n", "\r\n", StringComparison.Ordinal),
                good.Replace("\"time_ms\":30000", "\"time_ms\":30000,\"time_ms\":30000", StringComparison.Ordinal),
                good.Replace("\"time_ms\":30000", "\"time_ms\":3e4", StringComparison.Ordinal),
                good.Replace("\"time_ms\":30000", "\"time_ms\":9223372036854775808", StringComparison.Ordinal),
                good.Insert(1, "\"unknown\":true,")
            };
            foreach (var invalid in cases)
            {
                Check.True(invalid != good, "Negative mutation must change the request.");
                var run = await NativeRequestFixture.RunBytes(Encoding.UTF8.GetBytes(invalid));
                Check.True(run.ExitCode != 0, "Malformed request accepted: " + invalid[..Math.Min(160, invalid.Length)]);
                Check.Equal(0, run.Output.Length);
            }
        });

        yield return new("Native rejects mismatched bytes, identities, unsupported options and budgets atomically", async () =>
        {
            Action<JsonObject>[] mutations =
            [
                request => request["worker"]!["binary_sha256"] = new string('0', 64),
                request => request["files"]![0]!["original_sha256"] = new string('0', 64),
                request => request["files"]![0]!["byte_length"] = 1,
                request => request["files"]![0]!["bytes_base64"] = "!!!!",
                request => request["target"]!["ms_compatibility_version"] = "19.43",
                request => request["options"]!.AsArray().Add(new JsonObject { ["kind"] = "literal", ["value"] = "-includeC:/uncaptured.h" }),
                request => request["requested_features"]!.AsArray().Add("cfg"),
                request => request["files"]![0]!["logical_path"] = "../escape.cpp",
                request => request["budgets"]!["max_input_bytes"] = 1,
                request => request["budgets"]!["max_operations"] = 1,
                request => request["budgets"]!["max_records"] = 1,
                request => request["budgets"]!["max_result_bytes"] = 1
            ];
            foreach (var mutation in mutations)
            {
                var request = NativeRequestFixture.Create(context, "final"); mutation(request); NativeRequestFixture.Fingerprint(request);
                var run = await NativeRequestFixture.Run(request);
                Check.True(run.ExitCode != 0, "Invalid input was accepted."); Check.Equal(0, run.Output.Length);
            }
            var badHash = NativeRequestFixture.Create(context, "final"); badHash["request_fingerprint"] = new string('0', 64);
            Check.True((await NativeRequestFixture.Run(badHash)).ExitCode != 0);
        });
    }

    private static JsonElement[] Rows(JsonElement root, string name) => root.GetProperty(name).EnumerateArray().ToArray();
    private static string? Text(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetString();
}

public static class NativeRequestFixture
{
    public static string Executable => Environment.GetEnvironmentVariable("PL_NATIVE_WORKER") is { Length: > 0 } path && File.Exists(path)
        ? Path.GetFullPath(path) : throw new InvalidOperationException("PL_NATIVE_WORKER must point to the built pinned native worker; not run is not a pass.");

    public static JsonObject Create(TestContext context, string phase)
    {
        var files = new JsonArray();
        foreach (var (key, name) in new[] { ("file:main", "Arithmetic.cpp"), ("file:header", "Arithmetic.hpp") })
        {
            var bytes = File.ReadAllBytes(Path.Combine(context.ProjectRoot, "tests/fixtures/pilot/native", name));
            files.Add(new JsonObject
            {
                ["key"] = key, ["file_version_id"] = "file_version:" + CanonicalJson.Sha256(bytes), ["logical_path"] = "native/" + name,
                ["original_sha256"] = CanonicalJson.Sha256(bytes), ["byte_length"] = bytes.Length, ["encoding"] = "utf-8", ["bom_bytes"] = 0,
                ["bytes_base64"] = Convert.ToBase64String(bytes)
            });
        }
        var options = new JsonArray();
        foreach (var literal in new[] { "-std=c++17", "-fms-extensions", "-fms-compatibility", "-fms-compatibility-version=19.44", "-DNDEBUG" })
            options.Add(new JsonObject { ["kind"] = "literal", ["value"] = literal });
        options.Add(new JsonObject { ["kind"] = "path", ["option"] = "-I", ["root_key"] = "native", ["relative_path"] = "" });
        var request = new JsonObject
        {
            ["schema"] = "native-worker-request/1", ["request_id"] = "native-pilot-test", ["request_fingerprint"] = new string('0',64),
            ["snapshot_id"] = "snapshot:" + new string('1',64), ["context_id"] = "context:" + new string('2',64), ["unit_id"] = "compilation_unit:" + new string('3',64),
            ["capture_index_fingerprint"] = new string('4',64), ["semantic_input_fingerprint"] = new string('0',64),
            ["worker"] = new JsonObject { ["name"] = "prompted-learning-clang-worker", ["version"] = "1", ["clang_version"] = "19.1.7", ["binary_sha256"] = CanonicalJson.Sha256(File.ReadAllBytes(Executable)) },
            ["target"] = new JsonObject { ["triple"] = "x86_64-pc-windows-msvc", ["language_standard"] = "c++17", ["ms_compatibility_version"] = "19.44" },
            ["main_file_key"] = "file:main", ["files"] = files, ["options"] = options,
            ["path_bindings"] = new JsonArray(new JsonObject { ["root_key"] = "native", ["role"] = "captured_input", ["physical_path"] = "C:/deliberately-unavailable-native-inputs", ["logical_prefix"] = "native" }),
            ["budgets"] = new JsonObject { ["time_ms"] = 30000, ["memory_bytes"] = 1073741824, ["max_input_bytes"] = 1048576, ["max_result_bytes"] = 4194304, ["max_records"] = 10000, ["max_operations"] = 10000 },
            ["requested_features"] = new JsonArray("source_semantics"), ["phase"] = phase, ["expected_consumed_file_keys"] = new JsonArray("file:main", "file:header")
        };
        Fingerprint(request); return request;
    }

    public static void Fingerprint(JsonObject request)
    {
        var semantic = new JsonObject();
        foreach (var name in new[] { "snapshot_id", "context_id", "unit_id", "capture_index_fingerprint", "worker", "target", "main_file_key", "files", "options", "path_bindings", "requested_features", "expected_consumed_file_keys" })
            semantic[name] = request[name]!.DeepClone();
        foreach (var file in semantic["files"]!.AsArray()) file!.AsObject().Remove("bytes_base64");
        foreach (var binding in semantic["path_bindings"]!.AsArray()) binding!.AsObject().Remove("physical_path");
        request["semantic_input_fingerprint"] = CanonicalJson.Sha256(CanonicalJson.Serialize(JsonSerializer.SerializeToElement(new JsonArray("native-worker-semantic-input/1", semantic))));
        var unhashed = request.DeepClone().AsObject(); unhashed.Remove("request_fingerprint");
        request["request_fingerprint"] = CanonicalJson.Sha256(CanonicalJson.Serialize(JsonSerializer.SerializeToElement(unhashed)));
    }

    public static Task<NativeRun> Run(JsonObject request, string? workingDirectory = null) => RunBytes(CanonicalJson.Serialize(JsonSerializer.SerializeToElement(request), true), workingDirectory);
    public static async Task<NativeRun> RunBytes(byte[] bytes, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(Executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Native worker launch failed.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var output = ReadBounded(process.StandardOutput.BaseStream, 8 * 1024 * 1024, deadline.Token);
        var error = ReadBounded(process.StandardError.BaseStream, 128 * 1024, deadline.Token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(bytes, deadline.Token); process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            return new NativeRun(process.ExitCode, await output, Encoding.UTF8.GetString(await error));
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
    private static async Task<byte[]> ReadBounded(Stream source, int maximum, CancellationToken cancellation)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await source.ReadAsync(buffer, cancellation); if (count == 0) break; if (output.Length + count > maximum) throw new InvalidDataException("Native test IPC output exceeded limit."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
}
public sealed record NativeRun(int ExitCode, byte[] Output, string Error);

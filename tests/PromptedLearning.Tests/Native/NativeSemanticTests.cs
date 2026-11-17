using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PromptedLearning.Core;

namespace PromptedLearning.Tests;

public static class NativeSemanticTests
{
    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        Task<BoundNative>? actual = null;
        Task<BoundNative> Actual() => actual ??= ReadActual(context);

        yield return new("Shared semantic validator accepts the actual captured native result", async () =>
        {
            var native = await Actual();
            var accepted = SemanticResultValidator.Validate(native.Result, native.Binding);
            Check.Equal("complete", accepted.Result.Status);
            Check.Equal(42, accepted.Records.Values.Count(row => row.TryGetProperty("compiler_kind", out _)));
            Check.Equal(2, accepted.Result.ConsumedFileKeys.Count);
            await File.WriteAllBytesAsync(Path.Combine(context.ScratchRoot, "validated-native-unit.json"), CanonicalJson.Serialize(accepted.Result, true));
        });

        yield return new("Shared validator rejects native typed-reference, ownership and tree corruption", async () =>
        {
            var native = await Actual();
            _ = SemanticResultValidator.Validate(native.Result, native.Binding);
            Action<JsonObject>[] changes =
            [
                facts => facts["entities"]!.AsArray().Add(facts["entities"]![0]!.DeepClone()),
                facts => facts["operations"]![0]!["referenced_entity_key"] = "entity:missing",
                facts => facts["types"]![0]!["named_entity_key"] = "span:0",
                facts => facts["operations"]![1]!["parent_operation_key"] = facts["operations"]![1]!["key"]!.DeepClone(),
                facts => facts["operations"]![1]!["body_key"] = facts["bodies"]![1]!["key"]!.DeepClone(),
                facts => facts["bodies"]![0]!["root_operation_key"] = facts["operations"]![1]!["key"]!.DeepClone(),
                facts => facts["operations"]![1]!["child_ordinal"] = 7,
                facts => facts["operations"]![1]!["structural_path"] = new JsonArray(0, 99),
                facts => facts["signatures"]![0]!["parameters"]![0]!["entity_key"] = facts["entities"]![0]!["key"]!.DeepClone(),
                facts => facts["signatures"]![0]!["parameters"]![0]!["ordinal"] = 8,
                facts => facts["signatures"]![0]!["return_type_key"] = "type:missing",
                facts => facts["annotations"]![0]!["attribute_type_key"] = "type:0",
                facts => facts["preprocessing"]![0]!["resolved_file_key"] = "file:uncaptured"
            ];
            foreach (var change in changes) Reject(native, change);
        });

        yield return new("Shared validator rejects native original-byte, origin and provider contradictions", async () =>
        {
            var native = await Actual();
            _ = SemanticResultValidator.Validate(native.Result, native.Binding);
            Reject(native, facts => facts["spans"]![0]!["end_byte"] = 1000000);
            var main = native.Binding.Files.Single(file => file.Key == "file:main");
            var accent = main.Bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("é"));
            Check.True(accent >= 0);
            Reject(native, facts =>
            {
                var span = facts["spans"]!.AsArray().First(row => row!["file_key"]!.GetValue<string>() == "file:main")!;
                span["start_byte"] = accent + 1; span["end_byte"] = accent + 2;
            });
            Reject(native, facts =>
            {
                var caused = facts["spans"]!.AsArray().First(row => row!["cause_span_key"] is not null)!;
                caused["origin_stack"] = new JsonArray();
            });
            Reject(native, facts =>
            {
                var caused = facts["spans"]!.AsArray().First(row => row!["cause_span_key"] is not null)!;
                caused["origin_stack"]![0]!["start_byte"] = 0;
            });
            Reject(native, facts => facts["types"]!.AsArray().Add(ProviderType("wrong-output", false)));
            Reject(native, facts => facts["types"]!.AsArray().Add(ProviderType(native.Binding.OutputIdentity, true)));
            var differentBytes = main.Bytes.ToArray(); differentBytes[0] ^= 1;
            var changedBinding = native.Binding with { Files = native.Binding.Files.Select(file => file.Key == main.Key ? file with { Bytes = differentBytes } : file).ToArray() };
            Reject(() => SemanticResultValidator.Validate(native.Result, changedBinding));
        });

        yield return new("Shared validator rejects native scope, consumed-set, completeness and M1B violations", async () =>
        {
            var native = await Actual();
            _ = SemanticResultValidator.Validate(native.Result, native.Binding);
            Reject(() => SemanticResultValidator.Validate(native.Result with { UnitId = "compilation_unit:" + new string('a', 64) }, native.Binding));
            Reject(() => SemanticResultValidator.Validate(native.Result with { Producer = native.Result.Producer with { BinarySha256 = new string('0', 64) } }, native.Binding));
            Reject(() => SemanticResultValidator.Validate(native.Result with { ConsumedFileKeys = ["file:main"] }, native.Binding));
            Reject(() => SemanticResultValidator.Validate(native.Result with { ConsumedFileKeys = ["file:main", "file:header", "file:header"] }, native.Binding));
            Reject(() => SemanticResultValidator.Validate(native.Result, native.Binding with { MaxOperations = 1 }));
            Reject(native, facts => facts["coverage"]!.AsArray().RemoveAt(0));
            Reject(native, facts => facts["coverage"]!.AsArray().Add(facts["coverage"]![0]!.DeepClone()));
            Reject(native, facts => { facts["coverage"]![0]!["status"] = "partial"; facts["coverage"]![0]!["reason"] = "mutated_partial"; });
            Reject(native, facts =>
            {
                var row = facts["coverage"]!.AsArray().First(item => item!["feature"]!.GetValue<string>() == "control_flow")!;
                row["status"] = "complete"; row["reason"] = null;
            });
            Reject(native, facts => facts["discovery_requests"]!.AsArray().Add(new JsonObject
            {
                ["including_file_key"] = "file:main", ["written_include"] = "missing.hpp", ["logical_candidate_path"] = "native/missing.hpp", ["reason"] = "include_not_captured"
            }));
            Reject(native, facts => facts["control_flow"]!.AsArray().Add(new JsonObject
            {
                ["key"] = "cfg:forbidden", ["body_key"] = facts["bodies"]![0]!["key"]!.DeepClone(), ["status"] = "not_attempted", ["reason"] = "not_requested",
                ["entry_block_key"] = null, ["exit_block_key"] = null, ["blocks"] = new JsonArray(), ["edges"] = new JsonArray()
            }));
            Reject(native, facts =>
            {
                var constant = facts["operations"]!.AsArray().First(row => row!["constant"] is not null)!["constant"]!;
                constant["kind"] = "integer";
            });
        });
    }

    private static async Task<BoundNative> ReadActual(TestContext context)
    {
        var request = NativeRequestFixture.Create(context, "final");
        var run = await NativeRequestFixture.Run(request);
        Check.Equal(0, run.ExitCode, run.Error);
        using var document = CanonicalJson.Parse(run.Output);
        var root = document.RootElement;
        ContractCatalog.Default.Validate("native-worker-result", root);
        var producer = new CompilerProducer(root.GetProperty("worker").GetProperty("name").GetString()!, root.GetProperty("worker").GetProperty("version").GetString()!,
            root.GetProperty("worker").GetProperty("clang_version").GetString()!, root.GetProperty("worker").GetProperty("binary_sha256").GetString()!);
        string Text(string name) => root.GetProperty(name).GetString()!;
        var facts = CompilerFacts.Element(CompilerFacts.ArrayNames.ToDictionary(name => name, name => root.GetProperty(name).Clone(), StringComparer.Ordinal));
        var result = new SemanticUnitResult(Text("snapshot_id"), Text("context_id"), Text("unit_id"), request["capture_index_fingerprint"]!.GetValue<string>(),
            Text("semantic_input_fingerprint"), producer, Text("status"), root.GetProperty("stop_reason").GetString(), facts,
            root.GetProperty("consumed_file_keys").EnumerateArray().Select(key => key.GetString()!).ToArray());
        var files = request["files"]!.AsArray().Select(row => new CompilerInputFile(row!["key"]!.GetValue<string>(), row["file_version_id"]!.GetValue<string>(),
            row["logical_path"]!.GetValue<string>(), row["original_sha256"]!.GetValue<string>(), Convert.FromBase64String(row["bytes_base64"]!.GetValue<string>()),
            new SourceEncoding("known", "utf-8", 0, null))).ToArray();
        var binding = new SemanticUnitBinding("cpp", "Pilot", result.SnapshotId, result.ContextId, result.UnitId, result.CaptureIndexFingerprint,
            result.SemanticInputFingerprint, producer, files, ["file:main", "file:header"]);
        return new(result, binding);
    }

    private static JsonObject ProviderType(string output, bool invalidState) => new()
    {
        ["key"] = "type:provider-probe", ["named_entity_key"] = null, ["element_type_key"] = null, ["argument_type_keys"] = new JsonArray(),
        ["compiler_type_key"] = new JsonObject
        {
            ["tag"] = "named", ["compiler_key"] = "probe:Type", ["arguments"] = new JsonArray(),
            ["provider"] = new JsonObject
            {
                ["kind"] = "internal_output", ["logical_name"] = output,
                ["version"] = new JsonObject { ["state"] = "not_applicable", ["value"] = null, ["reason"] = "source_output" },
                ["content_fingerprint"] = new JsonObject { ["state"] = "not_collected", ["value"] = invalidState ? "invented" : null, ["reason"] = "source_only" },
                ["root_key"] = "native", ["relative_location"] = "Pilot.lib"
            }
        }
    };

    private static void Reject(BoundNative native, Action<JsonObject> change)
    {
        var facts = JsonNode.Parse(native.Result.Facts.GetRawText())!.AsObject(); change(facts);
        var changed = native.Result with { Facts = JsonSerializer.SerializeToElement(facts) };
        Reject(() => SemanticResultValidator.Validate(changed, native.Binding));
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (ContractException) { return; }
        throw new Exception("Malformed native result was accepted by the shared semantic validator.");
    }
    private sealed record BoundNative(SemanticUnitResult Result, SemanticUnitBinding Binding);
}

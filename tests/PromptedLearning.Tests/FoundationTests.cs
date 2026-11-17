using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PromptedLearning.Core;

namespace PromptedLearning.Tests;

public static class FoundationTests
{
    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        yield return TestCase.Sync("canonical exact UTF8, escapes and row LF", () =>
        {
            var value = new Dictionary<string, object?> { ["z"] = "café 🚀/\b\f\n\r\t\0", ["a"] = long.MinValue, ["t"] = true, ["n"] = null };
            var actual = Encoding.UTF8.GetString(CanonicalJson.Serialize(value, true));
            Check.Equal("{\"a\":-9223372036854775808,\"n\":null,\"t\":true,\"z\":\"café 🚀/\\b\\f\\n\\r\\t\\u0000\"}\n", actual);
            Check.True(!CanonicalJson.Serialize(value).AsSpan().StartsWith(new byte[] { 239, 187, 191 }), "No BOM is permitted.");
        });
        yield return TestCase.Sync("canonical keys use UTF8 order rather than UTF16 ordinal", () =>
        {
            var value = new Dictionary<string, object?> { ["\U00010000"] = 1, ["\ue000"] = 2 };
            Check.Equal("{\"\ue000\":2,\"\U00010000\":1}", Encoding.UTF8.GetString(CanonicalJson.Serialize(value)));
        });
        yield return TestCase.Sync("reject duplicate keys and non-Int64 semantic numbers", () =>
        {
            foreach (var text in new[] { "{\"a\":1,\"a\":2}", "1.5", "1.0", "1e0", "9223372036854775808", "-9223372036854775809" })
                Check.Throws<JsonException>(() => CanonicalJson.Parse(Encoding.UTF8.GetBytes(text)).Dispose());
            Check.Throws<JsonException>(() => CanonicalJson.Serialize(1.0));
            Check.Throws<JsonException>(() => CanonicalJson.Serialize(1m));
            Check.Throws<JsonException>(() => CanonicalJson.Serialize(DateTime.UtcNow));
        });
        yield return TestCase.Sync("reject malformed UTF16 before framework replacement", () =>
        {
            Check.Throws<JsonException>(() => CanonicalJson.Serialize("\ud800"));
            Check.Throws<JsonException>(() => CanonicalJson.Serialize('\ud800'));
            Check.Throws<JsonException>(() => CanonicalJson.Serialize(new { nested = new[] { "ok", "\udc00" } }));
            Check.Throws<JsonException>(() => CanonicalJson.Serialize(new Dictionary<string, object?> { ["\ud800"] = 1 }));
            Check.Throws<JsonException>(() => StableId.Envelope("source_file/1", new { snapshot_id = "snapshot:" + new string('0', 64), root_key = "r", relative_path = "\ud800" }));
        });
        yield return TestCase.Sync("independent Python SHA256 identity vectors", () =>
        {
            // Expected literals were computed independently using Python json(sort_keys=True)
            // and hashlib over the documented primitive preimages, before this test ran.
            var root = new { root_key = "r", boundary = "internal", exclusions = Array.Empty<object>() };
            var components = new
            {
                repository_identity = "fixture", roots = new[] { root },
                files = new[] { new { root_key = "r", relative_path = "a.cs", content_hash = new string('0', 64), availability = "available", origin = "written" } }
            };
            const string snapshot = "snapshot:25ae03db243cd8244ff430af756989b6b60e6912e5d86d3b711190d7a82a0d16";
            Check.Equal(snapshot, StableId.Create("snapshot/1", components));
            Check.Equal("source_file:05c584f32779d7f7457548cbd0070ecfce1a075bcbf3e2f35d68a2315c5ae836", StableId.Create("source_file/1", new { snapshot_id = snapshot, root_key = "r", relative_path = "a.cs" }));
            Check.Equal("source_root:d982ec581ec81b849ca34a09babe4846d6ec20c9d341356dc337d70be604a09e", StableId.Create("source_root/1", new { snapshot_id = snapshot, root }));
            var proof = StableId.Envelope("snapshot/1", components);
            StableId.Verify(snapshot, proof);
            Check.Throws<InvalidDataException>(() => StableId.Verify("snapshot:" + new string('0', 64), proof));
        });
        yield return TestCase.Sync("full frozen catalog loads; identity tuple has exact positions", () =>
        {
            var catalog = ContractCatalog.Default;
            var components = new
            {
                family = "appearance", scope = new { snapshot_id = "snapshot:" + new string('0', 64), context_id = "build_context:" + new string('1', 64), context_independent = false },
                producer_contract = "fixture/1", producer_version = "1", input_fingerprint = new string('2', 64),
                local_key = new { rule = "appearance-local-v1", parts = new object?[] { "entity:key", "span:key", "definition", 0L } }
            };
            var envelope = StableId.Envelope("appearance/1", components);
            catalog.Validate("identity-input", envelope);
            var changed = JsonNode.Parse(envelope.GetRawText())!;
            changed["components"]!["local_key"]!["parts"]![0] = new JsonObject { ["hidden"] = "payload" };
            using var invalid = CanonicalJson.Parse(Encoding.UTF8.GetBytes(changed.ToJsonString()));
            Check.Throws<ContractException>(() => catalog.Validate("identity-input", invalid.RootElement));
        });
        yield return TestCase.Sync("Draft2020 prefixItems applies items only after tuple prefix", () =>
        {
            var schema = "{\"$defs\":{\"tuple\":{\"type\":\"array\",\"prefixItems\":[{\"type\":\"string\"}],\"items\":{\"type\":\"integer\"}}}}";
            var catalog = new ContractCatalog(Encoding.UTF8.GetBytes(schema));
            using var valid = CanonicalJson.Parse("[\"a\",2]"u8); catalog.Validate("tuple", valid.RootElement);
            using var invalid = CanonicalJson.Parse("[\"a\",\"b\"]"u8); Check.Throws<ContractException>(() => catalog.Validate("tuple", invalid.RootElement));
        });
        yield return TestCase.Sync("frozen contains rule requires CFG for storage events", () =>
        {
            // Use the exact frozen policy subschema, without inventing a weaker analogue.
            var rule = ContractCatalog.Default.Root.GetProperty("$defs").GetProperty("native-worker-request").GetProperty("allOf")[0];
            var schema = "{\"$defs\":{\"selection\":" + rule.GetRawText() + "}}";
            var catalog = new ContractCatalog(Encoding.UTF8.GetBytes(schema));
            using var valid = CanonicalJson.Parse("{\"requested_features\":[\"source_semantics\",\"cfg\",\"storage_events\"]}"u8);
            catalog.Validate("selection", valid.RootElement);
            using var invalid = CanonicalJson.Parse("{\"requested_features\":[\"source_semantics\",\"storage_events\"]}"u8);
            Check.Throws<ContractException>(() => catalog.Validate("selection", invalid.RootElement));
        });
        yield return TestCase.Sync("catalog rejects unimplemented validation keywords", () =>
            Check.Throws<ContractException>(() => new ContractCatalog("{\"$defs\":{\"x\":{\"minProperties\":1}}}"u8)));
        yield return TestCase.Sync("A001 unknown preparation hash is permitted only before success", () =>
        {
            foreach (var outcome in new[] { "failed", "cancelled", "passed" })
            {
                var value = new { schema = "source-archive-preparation/1", capture_index_fingerprint = new string('1', 64),
                    source_manifest_hash = (string?)null, mode = "windows_pinned_read_handles_v1", max_open_blobs = 4096,
                    max_prepare_bytes = 8589934592L, max_prepare_time_ms = 120000, hash_buffer_bytes = 65536,
                    verified_bytes = 0, unique_blobs = 0, peak_open_handles = 0, elapsed_ms = 0, outcome,
                    reason = outcome == "passed" ? null : "early failure before trusted index" };
                using var doc = CanonicalJson.Parse(CanonicalJson.Serialize(value));
                if (outcome == "passed") Check.Throws<ContractException>(() => ContractCatalog.Default.Validate("source-archive-preparation", doc.RootElement));
                else ContractCatalog.Default.Validate("source-archive-preparation", doc.RootElement);
            }
        });
    }
}

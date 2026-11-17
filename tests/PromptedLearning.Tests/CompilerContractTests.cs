using System.Text.Json.Nodes;
using System.Text;
using PromptedLearning.Core;

namespace PromptedLearning.Tests;

public static class CompilerContractTests
{
    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        var result = new SemanticUnitResult("snapshot:" + new string('0', 64), "build_context:" + new string('1', 64),
            "compilation_unit:" + new string('2', 64), new string('3', 64), new string('4', 64),
            new("prompted-learning-roslyn-adapter", "1", "4.14.0.0", new string('5', 64)),
            "failed", "parse_failed", CompilerFacts.Empty(), []);
        yield return TestCase.Sync("honest managed metadata validates without Clang fields", () => CompilerFacts.ValidateSchema(result));
        yield return TestCase.Sync("common carrier rejects fabricated Clang metadata and unknown fields", () =>
        {
            var node = JsonNode.Parse(CompilerFacts.Element(result).GetRawText())!;
            node["producer"]!["clang_version"] = "not-a-managed-compiler";
            using var doc = CanonicalJson.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()));
            Check.Throws<ContractException>(() => ContractCatalog.Default.Validate("semantic-unit-result", doc.RootElement));
        });
        yield return TestCase.Sync("common carrier retains exact native array types and typed references", () =>
        {
            var node = JsonNode.Parse(CompilerFacts.Element(result).GetRawText())!;
            node["facts"]!["spans"] = new JsonArray(new JsonObject { ["key"] = "entity:wrong_family" });
            using var doc = CanonicalJson.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()));
            Check.Throws<ContractException>(() => ContractCatalog.Default.Validate("semantic-unit-result", doc.RootElement));
        });
        yield return TestCase.Sync("common outcomes require truthful failure reasons", () =>
        {
            Check.Throws<ContractException>(() => CompilerFacts.ValidateSchema(result with { StopReason = null }));
            Check.Throws<ContractException>(() => CompilerFacts.ValidateSchema(result with { Status = "complete" }));
            CompilerFacts.ValidateSchema(result with { Status = "complete", StopReason = null }); // Schema only; semantic emptiness is checked separately.
        });
        yield return TestCase.Sync("A003 abstract function signature identity is additive and finite", () =>
        {
            var signature = CompilerFacts.Element(new { kind = "function", name = "", generic_arity = 0,
                parameters = Array.Empty<object>(), return_type = new { tag = "builtin", name = "int" }, return_passing = "value",
                qualifiers = Array.Empty<string>(), calling_convention = "cdecl", language_linkage = "C++", constraints = Array.Empty<string>() });
            var typeComponents = CompilerFacts.Element(new { context_id = result.ContextId, language = "cpp", compiler_type_key = new { tag = "function", signature } });
            var typeId = StableId.Create("type_node/1", typeComponents);
            var components = CompilerFacts.Element(new { function_type_id = typeId, compiler_signature_key = signature });
            var id = StableId.Create("signature/1", components);
            StableId.Verify(id, StableId.Envelope("signature/1", components));
            var old = CompilerFacts.Element(new { callable_entity_id = "entity:" + new string('6', 64), compiler_signature_key = signature });
            Check.Equal("signature:" + CanonicalJson.Sha256(CanonicalJson.Serialize(new object[] { "signature/1", old })), StableId.Create("signature/1", old));
            Check.Throws<ContractException>(() => StableId.Envelope("signature/1", new { function_type_id = typeId, callable_entity_id = "entity:" + new string('6', 64), compiler_signature_key = signature }));
            Check.Throws<ContractException>(() => StableId.Envelope("type_node/1", new { context_id = result.ContextId, language = "cpp", compiler_type_key = new { tag = "function", signature }, function_signature_id = id }));
        });
    }
}

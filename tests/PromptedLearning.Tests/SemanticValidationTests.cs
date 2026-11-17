using System.Text.Json;
using System.Text.Json.Nodes;
using PromptedLearning.Core;
using PromptedLearning.Managed;

namespace PromptedLearning.Tests;

public static class SemanticValidationTests
{
    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        var fixture = new Lazy<Task<ManagedTests.Fixture>>(() => ManagedTests.Acquire(context));
        var result = new Lazy<Task<SemanticUnitResult>>(async () => await new ManagedPilotAdapter().AnalyzeAsync((await fixture.Value).Request));
        var binding = new Lazy<Task<SemanticUnitBinding>>(async () => await Bind((await fixture.Value).Request));
        yield return new("Shared acceptance validates actual managed compiler output", async () =>
        {
            var accepted = SemanticResultValidator.Validate(await result.Value, await binding.Value);
            Check.True(accepted.Records.Count > 0);
        });
        yield return new("Shared acceptance rejects contradictory source, ownership, graph, signatures and coverage", async () =>
        {
            var good = await result.Value; var scope = await binding.Value;
            SemanticResultValidator.Validate(good, scope);
            Action<JsonObject>[] mutations =
            [
                f => f["spans"]![0]!["end_byte"] = long.MaxValue,
                f => f["entities"]![0]!["linkage"] = "no_linkage",
                f => f["types"]!.AsArray().First(n => n!["compiler_type_key"]!["tag"]!.GetValue<string>() == "named")!["compiler_type_key"]!["provider"]!["version"]!["value"] = "99.0.0.0",
                f => f["signatures"]!.AsArray().Clear(),
                f => f["operations"]![0]!["representation"] = "syntax",
                f => f["coverage"]!.AsArray().RemoveAt(0),
                f => f["entities"]!.AsArray().Add(f["entities"]![0]!.DeepClone()),
                f => f["operations"]!.AsArray().First(n => n!["parent_operation_key"] is not null)!["parent_operation_key"] = "operation:absent",
                f => f["operations"]!.AsArray().First(n => n!["operation_kind"]!.GetValue<string>() == "reference")!["semantic_role"] = "value",
                f => f["operations"]!.AsArray().First(n => n!["constant"] is not null)!["constant"]!["value"] = "01",
                f =>
                {
                    var wrappers = f["operations"]!.AsArray().Where(n => n!["operation_kind"]!.GetValue<string>() == "argument").ToArray();
                    wrappers[0]!["operands"]![0]!["parameter_entity_key"] = wrappers[1]!["operands"]![0]!["parameter_entity_key"]!.DeepClone();
                },
                f =>
                {
                    var operands = f["operations"]!.AsArray().First(n => n!["operation_kind"]!.GetValue<string>() == "invocation")!["operands"]!.AsArray();
                    var first = operands[0]!["parameter_entity_key"]!.DeepClone();
                    operands[0]!["parameter_entity_key"] = operands[1]!["parameter_entity_key"]!.DeepClone(); operands[1]!["parameter_entity_key"] = first;
                },
                f =>
                {
                    var ops = f["operations"]!.AsArray(); var source = ops.First(n => n!["operands"]!.AsArray().Count > 0)!;
                    var target = ops.First(n => n!["body_key"]!.GetValue<string>() != source["body_key"]!.GetValue<string>())!;
                    source["operands"]![0]!["target_key"] = target["key"]!.DeepClone();
                },
                f =>
                {
                    var op = f["operations"]!.AsArray().First(n => n!["operation_kind"]!.GetValue<string>() == "reference")!;
                    var bodies = f["bodies"]!.AsArray(); var owner = bodies.Single(b => b!["key"]!.GetValue<string>() == op["body_key"]!.GetValue<string>())!["owner_entity_key"]!.GetValue<string>();
                    var parameter = f["entities"]!.AsArray().First(e => e!["entity_kind"]!.GetValue<string>() == "parameter" && e["owner_entity_key"]!.GetValue<string>() != owner)!;
                    op["referenced_entity_key"] = parameter["key"]!.DeepClone();
                }
            ];
            foreach (var mutation in mutations)
            {
                var facts = JsonNode.Parse(good.Facts.GetRawText())!.AsObject(); mutation(facts);
                using var parsed = CanonicalJson.Parse(System.Text.Encoding.UTF8.GetBytes(facts.ToJsonString()));
                Check.Throws<InvalidDataException>(() => SemanticResultValidator.Validate(good with { Facts = parsed.RootElement.Clone() }, scope));
            }
            Check.Throws<InvalidDataException>(() => SemanticResultValidator.Validate(good with { ConsumedFileKeys = [] }, scope));
            Check.Throws<InvalidDataException>(() => SemanticResultValidator.Validate(good, scope with { MaxRecords = 1 }));
            Check.Throws<InvalidDataException>(() => SemanticResultValidator.Validate(good, scope with { SemanticInputFingerprint = new string('0', 64) }));
        });
    }

    public static Task<SemanticUnitBinding> Bind(ManagedAnalysisRequest request) => ManagedPilotBinding.CreateAsync(request);
}

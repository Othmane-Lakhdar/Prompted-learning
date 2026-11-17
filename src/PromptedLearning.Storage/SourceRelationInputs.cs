using System.Text;
using System.Text.Json;
using PromptedLearning.Core;
using static PromptedLearning.Storage.SourceCallModel;

namespace PromptedLearning.Storage;

internal static class SourceRelationInputs
{
    // Only M1B source inputs enter this recipe. Relation output, derived coverage and field-state transitions never enter it.
    public static string Fingerprint(IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> rows, string unitId, string normalizerTool)
    {
        var unit = One(rows["compilation_unit"].Where(row => Text(row, "id") == unitId), "Relation input unit is absent.");
        var bodies = rows["body"].Where(row => Text(row, "unit_id") == unitId).ToArray();
        var bodyIds = bodies.Select(row => Text(row, "id")).ToHashSet();
        var operations = rows["operation"].Where(row => bodyIds.Contains(Text(row, "body_id"))).ToArray();
        var operationIds = operations.Select(row => Text(row, "id")).ToHashSet();
        var targetIds = operations.Where(row => Text(row, "operation_kind") == "invocation").Select(row => Text(row, "referenced_entity_id")).Where(id => id is not null).ToHashSet();
        var details = rows["entity_detail"].Where(row => targetIds.Contains(Text(row, "entity_id"))).ToArray();
        var signatureIds = details.Select(row => Text(row, "signature_id")).ToHashSet();
        var parameters = rows["parameter"].Where(row => signatureIds.Contains(Text(row, "signature_id"))).ToArray();
        var entityIds = targetIds.Concat(bodies.Select(row => Text(row, "owner_entity_id"))).Concat(parameters.Select(row => Text(row, "entity_id")))
            .Concat(rows["entity"].Where(row => targetIds.Contains(Text(row, "id"))).Select(row => Text(row, "containing_entity_id"))).ToHashSet();
        var evidenceIds = rows["record_evidence"].Where(row => Text(row, "record_id") == unitId && Text(row, "role") is "compiler_source_semantics" or "compiler_consumed_inputs")
            .Select(row => Text(row, "evidence_id")).ToHashSet();
        var compilerEvidence = rows["evidence"].Where(row => evidenceIds.Contains(Text(row, "id")) && Text(row, "detail") == "compiler_symbol").ToArray();
        var compilerIds = compilerEvidence.Select(row => Text(row, "id")).ToHashSet();
        object Ordered(IEnumerable<IReadOnlyDictionary<string, object?>> values) => values.OrderBy(row => Encoding.UTF8.GetString(CanonicalJson.Serialize(row)), CanonicalJson.Utf8Comparer).ToArray();
        return CanonicalJson.Sha256(CanonicalJson.Serialize(new object[] { "source-relations-input/1", new
        {
            unit_id = unitId, normalizer_tool_id = normalizerTool, policy = "source-relations/1", version = "1",
            compilation_unit = unit,
            unit_identity = Ordered(rows["record_extension"].Where(row => Text(row, "record_id") == unitId && Text(row, "schema_name") == "identity-input")),
            compiler_evidence = Ordered(compilerEvidence),
            compiler_evidence_identity = Ordered(rows["record_extension"].Where(row => compilerIds.Contains(Text(row, "record_id")) && Text(row, "schema_name") == "identity-input")),
            bodies = Ordered(bodies), operations = Ordered(operations),
            operation_children = Ordered(rows["operation_child"].Where(row => operationIds.Contains(Text(row, "parent_id")))),
            operation_operands = Ordered(rows["operation_operand"].Where(row => operationIds.Contains(Text(row, "operation_id")))),
            entities = Ordered(rows["entity"].Where(row => entityIds.Contains(Text(row, "id")))),
            modifiers = Ordered(rows["entity_modifier"].Where(row => entityIds.Contains(Text(row, "entity_id")))),
            entity_details = Ordered(details), signatures = Ordered(rows["signature"].Where(row => signatureIds.Contains(Text(row, "id")))),
            parameters = Ordered(parameters),
            source_body_coverage = Ordered(rows["coverage"].Where(row => bodyIds.Contains(Text(row, "subject_id")) && Text(row, "feature") == "operations"))
        } }));
    }
}

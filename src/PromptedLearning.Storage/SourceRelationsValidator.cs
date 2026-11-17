using System.Text.Json;

namespace PromptedLearning.Storage;

internal sealed partial class FoundationValidator
{
    private void ValidateSourceRelationRows()
    {
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, envelope) in identities.Where(pair => pair.Value.GetProperty("components").TryGetProperty("producer_contract", out var contract)
            && contract.GetString() == "source-relations/1")) Guard(payload[id].Table, id, "source_relation_provenance", () =>
        {
            var components = envelope.GetProperty("components");
            Require(components.GetProperty("producer_version").GetString() == "1", "Unregistered source normalization version.");
            var evidence = payload[id].Table == "evidence" ? payload[id].Row : payload[SourceCallModel.One(rows["record_evidence"].Where(row => Text(row, "record_id") == id
                && Text(row, "role") == "source_call_normalization"), "Output lacks unique normalization provenance.")["evidence_id"]!.ToString()!].Row;
            Require(Text(evidence, "kind") == "derivation" && evidence["detail"] is null, "Normalizer must not impersonate the compiler or embed a result dump.");
            var evidenceIdentity = identities[Text(evidence, "id")!].GetProperty("components");
            Require(evidenceIdentity.GetProperty("producer_contract").GetString() == "source-relations/1" && evidenceIdentity.GetProperty("producer_version").GetString() == "1",
                "Normalization evidence uses an unregistered producer contract.");
            var locator = Text(evidence, "locator")!;
            Require(locator.StartsWith("record:compilation_unit:", StringComparison.Ordinal), "Normalization evidence must locate its unit header.");
            var unitId = locator[7..]; var toolId = Text(evidence, "tool_id")!;
            Require(payload.TryGetValue(toolId, out var tool) && tool.Table == "tool" && Text(tool.Row, "name") == "PromptedLearning.Storage.SourceRelations"
                && Text(tool.Row, "role") == "source_normalizer" && tool.Row["content_hash"] is string, "Normalizer lacks its actual independently hashed tool descriptor.");
            Require(!rows["context_tool"].Any(row => Text(row, "tool_id") == toolId), "Normalizer must not mutate frozen compiler context inputs.");
            var cacheKey = unitId + "/" + toolId;
            if (!fingerprints.TryGetValue(cacheKey, out var fingerprint)) fingerprints[cacheKey] = fingerprint = SourceRelationInputs.Fingerprint(rows, unitId, toolId);
            Require(components.GetProperty("input_fingerprint").GetString() == fingerprint, "Normalization input fingerprint no longer matches the consumed source rows/tool.");
            Require(Text(registry[id], "context_id") == Text(registry[unitId], "context_id") && Text(registry[id], "snapshot_id") == Text(registry[unitId], "snapshot_id"), "Normalization provenance crosses its source unit scope.");
            if (payload[id].Table != "evidence") Require(rows["record_evidence"].Any(link => Text(link, "record_id") == id && Text(link, "role") == "compiler_call_target"
                && rows["record_evidence"].Any(source => Text(source, "record_id") == unitId && Equals(source["evidence_id"], link["evidence_id"])
                    && Text(source, "role") is "compiler_source_semantics" or "compiler_consumed_inputs")), "Derived source fact lost its original compiler evidence.");
        });
        foreach (var site in rows["call_site"]) Guard("call_site", Text(site, "id"), "source_call", () =>
        {
            var id = Text(site, "id")!;
            Require(identities[id].GetProperty("components").GetProperty("producer_contract").GetString() == "source-relations/1", "Call site uses an unregistered normalization contract.");
            Require(site["operation_id"] is string operationId && payload.TryGetValue(operationId, out var operation) && operation.Table == "operation", "Call site requires a source operation.");
            var source = payload[Text(site, "operation_id")!].Row;
            Require(Text(source, "operation_kind") == "invocation", "Call site operation is not an invocation.");
            var expected = SourceCallModel.Read(rows, source, out var unsupported);
            Require(expected is not null, "Call site claims unsupported direct dispatch: " + unsupported);
            Require(rows["coverage"].Count(row => Text(row, "subject_id") == expected!.BodyId && Text(row, "feature") == "direct_calls" && row["span_id"] is null) == 1,
                "Call site lacks its explicit source scan coverage summary.");
            Require(Text(site, "caller_id") == expected!.CallerId && Text(site, "selected_declaration_id") == expected.TargetId, "Call site caller/selected target differs from source semantics.");
            Require(Text(site, "dispatch") == "direct" && Text(site, "candidate_completeness") == "not_analyzed"
                && Text(site, "remainder_reason") == "runtime_candidates_not_analyzed" && site["receiver_id"] is null && site["analysis_result_id"] is null,
                "M1C0 must not invent dispatch receivers or runtime target analysis.");
            var parts = identities[id].GetProperty("components").GetProperty("local_key").GetProperty("parts");
            Require(parts[2].GetString() == "call", "Unregistered source call role.");
            var occurrence = payload[Text(site, "occurrence_id")!];
            Require(occurrence.Table == "occurrence" && Text(occurrence.Row, "call_site_id") == id
                && Text(occurrence.Row, "operation_id") == expected.OperationId && Text(occurrence.Row, "enclosing_id") == expected.CallerId
                && Text(occurrence.Row, "span_id") == expected.SpanId && Text(occurrence.Row, "cause_span_id") == expected.CauseSpanId,
                "Call occurrence ownership, reciprocal reference or exact origin differs.");
            Require(identities[Text(occurrence.Row, "id")!].GetProperty("components").GetProperty("local_key").GetProperty("parts")[4].GetInt64() == 0,
                "Each pilot invocation has one source occurrence ordinal zero.");
            var targets = rows["call_target"].Where(row => Text(row, "call_site_id") == id).ToArray();
            Require(targets.Length == 1 && Text(targets[0], "target_id") == expected.TargetId && Text(targets[0], "target_kind") == "selected_declaration"
                && Text(targets[0], "meaning") == "resolved_reference", "Selected declaration must remain distinct from uncomputed runtime candidates.");
            Require(payload.TryGetValue(Text(targets[0], "evidence_id")!, out var targetEvidence) && targetEvidence.Table == "evidence"
                && Text(targetEvidence.Row, "kind") == "compiler", "Selected declaration lacks compiler provenance.");
            var sourceUnit = Text(payload[expected.BodyId].Row, "unit_id");
            Require(rows["record_evidence"].Any(link => Text(link, "record_id") == sourceUnit && Equals(link["evidence_id"], targets[0]["evidence_id"])
                && Text(link, "role") is "compiler_source_semantics" or "compiler_consumed_inputs"), "Selected target compiler provenance belongs to another unit.");
            var arguments = rows["call_argument"].Where(row => Text(row, "call_site_id") == id).OrderBy(row => Number(row, "ordinal")).ToArray();
            var occurrenceArguments = rows["occurrence_argument"].Where(row => Text(row, "occurrence_id") == Text(occurrence.Row, "id")).OrderBy(row => Number(row, "ordinal")).ToArray();
            Require(arguments.Length == expected.Arguments.Count && occurrenceArguments.Length == expected.Arguments.Count, "Call argument collections are incomplete or duplicated.");
            for (var index = 0; index < arguments.Length; index++)
            {
                var argument = expected.Arguments[index]; var actual = arguments[index]; var occurrenceArgument = occurrenceArguments[index];
                Require(Number(actual, "ordinal") == argument.Ordinal && Text(actual, "expression_id") == argument.ExpressionId
                    && Text(actual, "parameter_id") == argument.ParameterId && Text(actual, "passing_mode") == argument.PassingMode, "Argument expression/parameter/passing differs from selected signature.");
                Require(Number(occurrenceArgument, "ordinal") == argument.Ordinal && Text(occurrenceArgument, "argument_kind") == "value"
                    && Text(occurrenceArgument, "target_id") == argument.ExpressionId, "Occurrence argument differs from call argument.");
            }
            var links = rows["relationship_occurrence"].Where(row => Text(row, "occurrence_id") == Text(occurrence.Row, "id")).ToArray();
            Require(links.Length == 1, "Source call occurrence must back exactly one direct relationship.");
            var relationship = payload[Text(links[0], "relationship_id")!];
            Require(relationship.Table == "relationship" && Text(relationship.Row, "source_id") == expected.CallerId
                && Text(relationship.Row, "destination_id") == expected.TargetId, "Direct relationship endpoints differ from compiler selection.");
            Require(RelationState(id, "call_target", "call_target") == "unknown", "Runtime target collection is uncomputed.");
            Require(RelationState(expected.CallerId, "relationship", "outgoing_relationships") == "unknown"
                && RelationState(expected.TargetId, "relationship", "incoming_relationships") == "unknown"
                && RelationState(expected.TargetId, "relationship", "incoming_relationships:direct_call") == "unknown"
                && RelationState(expected.CallerId, "occurrence", "occurrences") == "unknown", "Direct-call-only normalization cannot establish broad relationship/occurrence or whole-context incoming completeness.");
        });
        foreach (var occurrence in rows["occurrence"]) Guard("occurrence", Text(occurrence, "id"), "source_occurrence", () =>
        {
            Require(identities[Text(occurrence, "id")!].GetProperty("components").GetProperty("producer_contract").GetString() == "source-relations/1", "Occurrence uses an unregistered normalization contract.");
            Require(occurrence["call_site_id"] is string site && payload.TryGetValue(site, out var target) && target.Table == "call_site"
                && Text(target.Row, "occurrence_id") == Text(occurrence, "id"), "M1C0 occurrence lacks its reciprocal call site.");
            Require(occurrence["condition_text"] is null, "Path conditions are outside direct source normalization.");
        });
        foreach (var relationship in rows["relationship"]) Guard("relationship", Text(relationship, "id"), "source_relationship", () =>
        {
            Require(identities[Text(relationship, "id")!].GetProperty("components").GetProperty("producer_contract").GetString() == "source-relations/1", "Relationship uses an unregistered normalization contract.");
            Require(Text(relationship, "kind") == "direct_call" && Text(relationship, "role") == "call" && Text(relationship, "resolution") == "resolved"
                && Text(relationship, "meaning") == "resolved_reference" && relationship["analysis_result_id"] is null, "M1C0 only accepts compiler-selected direct call relationships.");
            Require(rows["relationship_occurrence"].Any(row => Text(row, "relationship_id") == Text(relationship, "id")), "A direct relationship has no source occurrence.");
            var callerBodies = rows["body"].Where(row => Text(row, "owner_entity_id") == Text(relationship, "source_id")).ToArray();
            var complete = callerBodies.Length > 0 && callerBodies.All(body => rows["coverage"].Any(row => Equals(row["subject_id"], body["id"])
                && Text(row, "feature") == "direct_calls" && row["span_id"] is null && Text(row, "status") == "complete"));
            Require(RelationState(Text(relationship, "id")!, "relationship_occurrence", "relationship_occurrence") == (complete ? "known" : "unknown"),
                "Relationship occurrence completeness exceeds the owning source scan.");
        });
        foreach (var state in rows["field_state"].Where(row => Text(row, "field_path") == "/call_site/@collection/calls" && Text(row, "state") is "known" or "unknown"))
            Guard("field_state", Text(state, "record_id"), "source_call_coverage", () =>
            {
                var bodies = rows["body"].Where(row => Text(row, "owner_entity_id") == Text(state, "record_id")).ToArray();
                Require(bodies.Length > 0 && bodies.All(body => rows["coverage"].Count(row => Equals(row["subject_id"], body["id"])
                    && Text(row, "feature") == "direct_calls" && row["span_id"] is null) == 1), "Call collection knowledge requires explicit scan coverage; absent rows never prove empty.");
            });
        foreach (var coverage in rows["coverage"].Where(row => Text(row, "feature") == "direct_calls"))
            Guard("coverage", Text(coverage, "id"), "source_call_coverage", () =>
                Require(identities[Text(coverage, "id")!].GetProperty("components").GetProperty("producer_contract").GetString() == "source-relations/1", "Call scan coverage uses an unregistered normalization contract."));
        foreach (var coverage in rows["coverage"].Where(row => Text(row, "feature") == "direct_calls" && row["span_id"] is null))
            Guard("coverage", Text(coverage, "id"), "source_call_coverage", () =>
            {
                var bodyId = Text(coverage, "subject_id")!;
                Require(payload.TryGetValue(bodyId, out var body) && body.Table == "body", "Direct call scan coverage must address a body.");
                var complete = Text(body.Row, "status") == "complete";
                var expectedOperations = new HashSet<string>();
                foreach (var operation in rows["operation"].Where(row => Text(row, "body_id") == bodyId && Text(row, "operation_kind") == "invocation"))
                {
                    var expected = SourceCallModel.Read(rows, operation, out var unsupported);
                    if (expected is not null) expectedOperations.Add(expected.OperationId);
                    else
                    {
                        complete = false;
                        Require(rows["coverage"].Any(row => Text(row, "subject_id") == bodyId && Text(row, "feature") == "direct_calls"
                            && Equals(row["span_id"], operation["span_id"]) && Text(row, "status") == "partial" && Text(row, "reason") == unsupported), "Unsupported call loses its located reason.");
                    }
                }
                var actual = rows["call_site"].Where(row => Text(row, "caller_id") == Text(body.Row, "owner_entity_id"))
                    .Where(row => row["operation_id"] is string operation && payload.TryGetValue(operation, out var source) && Text(source.Row, "body_id") == bodyId).ToArray();
                Require(actual.Length == expectedOperations.Count && actual.Select(row => Text(row, "operation_id")!).ToHashSet().SetEquals(expectedOperations), "Source call scan lost or duplicated a compiler-established invocation.");
                Require(Text(coverage, "status") == (complete ? "complete" : "partial"), "Call scan completeness exceeds its source evidence.");
                var caller = Text(body.Row, "owner_entity_id")!;
                Require(RelationState(caller, "call_site", "calls") == (complete ? "known" : "unknown")
                    && RelationState(caller, "relationship", "outgoing_relationships:direct_call") == (complete ? "known" : "unknown"), "Caller collection knowledge differs from its actual scan outcome.");
            });
    }

    private string? RelationState(string owner, string table, string role) => rows["field_state"].SingleOrDefault(row => Text(row, "record_id") == owner
        && Text(row, "field_path") == "/" + table + "/@collection/" + role) is { } state ? Text(state, "state") : null;
}

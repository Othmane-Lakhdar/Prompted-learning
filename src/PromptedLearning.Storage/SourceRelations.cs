using System.Text;
using System.Text.Json;
using PromptedLearning.Core;
using static PromptedLearning.Storage.SourceCallModel;

namespace PromptedLearning.Storage;

public sealed record DirectSourceRelationsResult(string UnitId, string InputFingerprint, IReadOnlyList<string> CallSiteIds, string Status);

/// <summary>Normalizes compiler-established direct source calls. It performs no runtime-target or flow analysis.</summary>
public static class SourceRelations
{
    public static DirectSourceRelationsResult NormalizeDirectSourceRelations(this CanonicalWrite write, string unitId, CancellationToken cancellationToken = default)
        => write.ImportOperation(() => new Normalizer(write, unitId, cancellationToken).Run());

    private sealed class Normalizer(CanonicalWrite write, string unitId, CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> rows = new(StringComparer.Ordinal);
        private string snapshot = "", context = "", inputFingerprint = "", producer = "", evidence = "", compilerEvidence = "";
        private IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(string table)
        {
            if (!rows.TryGetValue(table, out var values)) rows[table] = values = write.ReadRows(table);
            return values;
        }

        public DirectSourceRelationsResult Run()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = write.ValidateSourceRelations(cancellationToken);
            if (!validation.IsValid) throw new SourceRelationsValidationException(validation);
            foreach (var table in write.Schema.Tables.Keys) Rows(table);
            var unit = One(Rows("compilation_unit").Where(row => Text(row, "id") == unitId), "Requested canonical unit is absent.");
            Require(Text(unit, "status") is "complete" or "partial", "Source relations require an accepted compiler outcome.");
            var registry = One(Rows("record").Where(row => Text(row, "id") == unitId), "Unit registry is absent.");
            snapshot = Text(registry, "snapshot_id")!; context = Text(registry, "context_id")!;
            var sourceEvidenceIds = Rows("record_evidence").Where(row => Text(row, "record_id") == unitId && Text(row, "role") is "compiler_source_semantics" or "compiler_consumed_inputs")
                .Select(row => Text(row, "evidence_id")).ToHashSet(StringComparer.Ordinal);
            var sourceEvidence = One(Rows("evidence").Where(row => sourceEvidenceIds.Contains(Text(row, "id")) && Text(row, "detail") == "compiler_symbol"), "Accepted compiler provenance is absent or ambiguous.");
            compilerEvidence = Text(sourceEvidence, "id")!;
            producer = NormalizerTool();
            inputFingerprint = SourceRelationInputs.Fingerprint(rows, unitId, producer);
            evidence = Primary("evidence", ["derivation", null, producer, null, null, "record:" + unitId, CanonicalJson.Sha256(CanonicalJson.Serialize((object?)null))],
                Row(("kind", "derivation"), ("tool_id", producer), ("locator", "record:" + unitId)), link: false);
            var sites = new List<string>(); var partial = Text(unit, "status") != "complete";
            foreach (var body in Rows("body").Where(row => Text(row, "unit_id") == unitId).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bodyId = Text(body, "id")!; var caller = Text(body, "owner_entity_id")!;
                var complete = Text(body, "status") == "complete";
                var reason = complete ? null : Rows("coverage").FirstOrDefault(row => Text(row, "subject_id") == bodyId && Text(row, "feature") == "operations") is { } sourceCoverage
                    ? Text(sourceCoverage, "reason") ?? "source_body_incomplete" : "source_body_incomplete";
                foreach (var operation in Rows("operation").Where(row => Text(row, "body_id") == bodyId && Text(row, "operation_kind") == "invocation").ToArray())
                {
                    var call = SourceCallModel.Read(rows, operation, out var unsupported);
                    if (call is null)
                    {
                        complete = false; reason ??= unsupported;
                        Coverage(bodyId, Text(operation, "span_id"), "partial", unsupported);
                    }
                    else sites.Add(Emit(call));
                }
                partial |= !complete;
                Coverage(bodyId, null, complete ? "complete" : "partial", reason);
                Collection(caller, "call_site", "calls", complete ? "known" : "unknown", reason);
                Collection(caller, "relationship", "outgoing_relationships:direct_call", complete ? "known" : "unknown", reason);
                foreach (var relationship in Rows("relationship").Where(row => Text(row, "source_id") == caller && Text(row, "kind") == "direct_call").ToArray())
                    Collection(Text(relationship, "id")!, "relationship_occurrence", "relationship_occurrence", complete ? "known" : "unknown", reason);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(unitId, inputFingerprint, sites.AsReadOnly(), partial ? "partial" : "complete");
        }

        private string Emit(SourceCall call)
        {
            var site = Id("call_site", [call.CallerId, call.OperationId, "call"]);
            var occurrence = Primary("occurrence", [call.SpanId, call.CauseSpanId, call.CallerId, call.OperationId, 0L],
                Row(("span_id", call.SpanId), ("cause_span_id", call.CauseSpanId), ("enclosing_id", call.CallerId), ("operation_id", call.OperationId), ("call_site_id", site)), call.SpanId,
                missing: new() { ["condition_text"] = ("not_collected", "path_condition_not_collected") });
            Require(Primary("call_site", [call.CallerId, call.OperationId, "call"], Row(("caller_id", call.CallerId), ("operation_id", call.OperationId),
                ("occurrence_id", occurrence), ("selected_declaration_id", call.TargetId), ("dispatch", "direct"), ("candidate_completeness", "not_analyzed"),
                ("remainder_reason", "runtime_candidates_not_analyzed")), call.SpanId) == site, "Call site identity drifted.");
            var relationship = Primary("relationship", [call.CallerId, "direct_call", call.TargetId, "call", "resolved", null, "resolved_reference"],
                Row(("source_id", call.CallerId), ("kind", "direct_call"), ("destination_id", call.TargetId), ("role", "call"), ("resolution", "resolved"), ("meaning", "resolved_reference")));
            Insert("relationship_occurrence", Row(("relationship_id", relationship), ("occurrence_id", occurrence)));
            Insert("call_target", Row(("call_site_id", site), ("target_id", call.TargetId), ("target_kind", "selected_declaration"), ("meaning", "resolved_reference"), ("evidence_id", compilerEvidence)));
            foreach (var argument in call.Arguments)
            {
                Insert("call_argument", Row(("call_site_id", site), ("ordinal", argument.Ordinal), ("expression_id", argument.ExpressionId), ("parameter_id", argument.ParameterId), ("passing_mode", argument.PassingMode)));
                Insert("occurrence_argument", Row(("occurrence_id", occurrence), ("ordinal", argument.Ordinal), ("argument_kind", "value"), ("target_id", argument.ExpressionId)));
            }
            Collection(site, "call_target", "call_target", "unknown", "runtime_candidates_not_analyzed");
            Collection(site, "call_argument", "call_argument", "known");
            Collection(occurrence, "occurrence_argument", "occurrence_argument", "known");
            Collection(call.CallerId, "relationship", "outgoing_relationships", "unknown", "other_relationship_kinds_not_collected");
            Collection(call.TargetId, "relationship", "incoming_relationships", "unknown", "other_relationship_kinds_not_collected");
            Collection(call.TargetId, "relationship", "incoming_relationships:direct_call", "unknown", "context_call_scan_not_complete");
            Collection(call.CallerId, "occurrence", "occurrences", "unknown", "other_occurrence_kinds_not_collected");
            return site;
        }

        private void Coverage(string body, string? span, string status, string? reason)
        {
            Primary("coverage", [producer, body, "direct_calls", span], Row(("producer_id", producer), ("subject_id", body), ("feature", "direct_calls"),
                ("span_id", span), ("status", status), ("reason", reason), ("unresolved_count", status == "complete" ? 0L : null)), span,
                missing: status == "complete" ? null : new() { ["unresolved_count"] = ("unknown", reason ?? "source_body_incomplete") });
            Collection(body, "coverage", "coverage:direct_calls", "known");
        }
        private JsonElement Envelope(string family, object?[] parts) => StableId.Envelope(family + "/1", new
        {
            family, scope = new { snapshot_id = snapshot, context_id = context, context_independent = false },
            producer_contract = "source-relations/1", producer_version = "1", input_fingerprint = inputFingerprint,
            local_key = new { rule = family + "-local-v1", parts }
        });
        private string Id(string table, object?[] parts) => StableId.Create(table + "/1", Envelope(table, parts).GetProperty("components"));
        private string Primary(string table, object?[] parts, Dictionary<string, object?> row, string? span = null, bool link = true,
            Dictionary<string, (string State, string Reason)>? missing = null)
        {
            var envelope = Envelope(table, parts); var id = StableId.Create(table + "/1", envelope.GetProperty("components"));
            var section = StorageSchema.Dictionary.GetProperty("tables").EnumerateArray().Single(value => value.GetProperty("table").GetString() == table).GetProperty("section").GetString();
            Insert("record", Row(("id", id), ("kind", table), ("section", section), ("snapshot_id", snapshot), ("context_id", context), ("context_independent", 0L), ("origin_span_id", span)));
            row["id"] = id;
            foreach (var column in write.Schema.Tables[table].Columns.Values) row.TryAdd(column.Name, null);
            Insert(table, row);
            foreach (var column in write.Schema.Tables[table].Columns.Values.Where(column => row[column.Name] is null && !column.Required && column.KeyOrdinal == 0))
            {
                var state = missing is not null && missing.TryGetValue(column.Name, out var explicitState) ? explicitState : ("not_applicable", "inactive_variant");
                var key = write.Schema.Tables[table].PrimaryKey.Select(name => row[name]).ToArray();
                State(id, "/" + table + "/" + Key(key) + "/" + column.Name, state.Item1, state.Item2);
            }
            Insert("record_extension", Row(("record_id", id), ("schema_name", "identity-input"), ("schema_version", 1L), ("payload_json", Encoding.UTF8.GetString(CanonicalJson.Serialize(envelope)))));
            Collection(id, "record_extension", "record_extension", "known");
            if (link)
            {
                Insert("record_evidence", Row(("record_id", id), ("evidence_id", evidence), ("role", "source_call_normalization")));
                Insert("record_evidence", Row(("record_id", id), ("evidence_id", compilerEvidence), ("role", "compiler_call_target")));
            }
            return id;
        }
        private string NormalizerTool()
        {
            var assembly = typeof(SourceRelations).Assembly;
            var descriptor = new { name = "PromptedLearning.Storage.SourceRelations", version = assembly.GetName().Version!.ToString(),
                content_fingerprint = new { state = "known", value = CanonicalJson.Sha256(File.ReadAllBytes(assembly.Location)), reason = (string?)null } };
            var envelope = StableId.Envelope("tool/1", new { snapshot_id = snapshot, descriptor });
            var id = StableId.Create("tool/1", envelope.GetProperty("components"));
            Insert("record", Row(("id", id), ("kind", "tool"), ("section", "BDB.2"), ("snapshot_id", snapshot), ("context_id", null), ("context_independent", 1L), ("origin_span_id", null)));
            Insert("tool", Row(("id", id), ("role", "source_normalizer"), ("name", descriptor.name), ("version", descriptor.version), ("path", null), ("content_hash", descriptor.content_fingerprint.value), ("toolset", null)));
            Insert("record_extension", Row(("record_id", id), ("schema_name", "identity-input"), ("schema_version", 1L), ("payload_json", Encoding.UTF8.GetString(CanonicalJson.Serialize(envelope)))));
            // Tool provenance is context-independent and must not inherit a contextual state evidence link.
            Insert("field_state", Row(("record_id", id), ("field_path", "/record_extension/@collection/record_extension"), ("state", "known"), ("reason", null), ("evidence_id", null)));
            foreach (var column in new[] { "path", "toolset" }) Insert("field_state", Row(("record_id", id), ("field_path", "/tool/" + Key([id]) + "/" + column),
                ("state", column == "path" ? "not_collected" : "not_applicable"), ("reason", column == "path" ? "physical_tool_path_not_collected" : "normalizer_has_no_toolset"), ("evidence_id", null)));
            return id;
        }
        private void Collection(string owner, string table, string role, string state, string? reason = null)
            => State(owner, "/" + table + "/@collection/" + role, state, reason);
        private void State(string owner, string path, string state, string? reason)
        {
            var prior = Rows("field_state").SingleOrDefault(row => Text(row, "record_id") == owner && Text(row, "field_path") == path);
            var row = Row(("record_id", owner), ("field_path", path), ("state", state), ("reason", reason), ("evidence_id", evidence.Length == 0 ? null : evidence));
            if (prior is null) Insert("field_state", row);
            else if (!row.All(pair => Equals(prior[pair.Key], pair.Value)))
            {
                Require(Text(prior, "state") == "not_collected", "Conflicting retained source-relation knowledge state.");
                write.UpdateCollecting("field_state", prior, Row(("state", state), ("reason", reason), ("evidence_id", row["evidence_id"])));
                rows.Remove("field_state"); Rows("field_state");
            }
        }
        private void Insert(string table, Dictionary<string, object?> row)
        {
            var keys = write.Schema.Tables[table].PrimaryKey;
            var prior = Rows(table).SingleOrDefault(value => keys.All(key => Equals(value[key], row[key])));
            if (prior is not null) Require(row.All(pair => Equals(prior[pair.Key], pair.Value)), "Conflicting canonical " + table + " fact.");
            else { write.Insert(table, row); rows[table] = Rows(table).Append(row).ToArray(); }
        }
        private static Dictionary<string, object?> Row(params (string Name, object? Value)[] values) => values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        private static string Key(object?[] values) => Convert.ToBase64String(CanonicalJson.Serialize(values)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

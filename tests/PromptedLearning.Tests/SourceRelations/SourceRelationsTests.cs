using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PromptedLearning.Core;
using PromptedLearning.Managed;
using PromptedLearning.Storage;

namespace PromptedLearning.Tests;

public static class SourceRelationsTests
{
    private sealed record Input(MsBuildEvaluation Evaluation, SealedCapture Capture, ValidatedSemanticUnit Unit, JsonElement Identity);
    private static async Task<Input> Acquire(TestContext context, bool native)
    {
        if (native)
        {
            var fixture = await NativeHostTests.Acquire(context);
            var result = await new NativePilotAdapter().AnalyzeAsync(fixture.Evaluation, fixture.Capture, fixture.ContextId);
            return new(fixture.Evaluation, fixture.Capture, result.Unit, result.UnitIdentityInput);
        }
        var managed = await ManagedTests.Acquire(context); var request = managed.Request;
        var resultManaged = await new ManagedPilotAdapter().AnalyzeAsync(request);
        var unit = SemanticResultValidator.Validate(resultManaged, await ManagedPilotBinding.CreateAsync(request));
        var inputs = request.Sources.Select(source => (object)new { role = "source", file_version_id = source.FileVersionId, provider = (object?)null })
            .Concat(request.Evaluation.Providers.Select(provider => (object)new { role = "reference", file_version_id = (string?)null, provider = provider.Descriptor })).ToArray();
        return new(request.Evaluation, request.Capture, unit, StableId.Envelope("compilation_unit/1", new { context_id = request.ContextId, language = "csharp", inputs, output_identity = "Pilot" }));
    }
    private static CanonicalStore Store(Input input, string? path = null)
    {
        var store = path is null ? CanonicalStore.CreateInMemory() : CanonicalStore.Create(path);
        using (var write = store.BeginWrite())
        { write.ImportCapturedSources(input.Capture.Inputs, DateTimeOffset.UnixEpoch); write.ImportEvaluatedContext(input.Evaluation, input.Capture.Inputs); write.CommitFoundation(); }
        using (var write = store.BeginWrite()) { write.ImportSemanticUnit(input.Unit, input.Identity); write.CommitSemanticCollection(); }
        return store;
    }
    private static DirectSourceRelationsResult Normalize(CanonicalStore store, string unit)
    {
        using var write = store.BeginWrite(); var result = write.NormalizeDirectSourceRelations(unit); write.CommitSourceRelationsCollection(); return result;
    }
    private static string? Text(IReadOnlyDictionary<string, object?> row, string column) => row[column] as string;
    private static string? State(CanonicalStore store, string owner, string path) => Text(store.ReadRows("field_state").Single(row => Text(row, "record_id") == owner && Text(row, "field_path") == path), "state");

    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        var managed = new Lazy<Task<Input>>(() => Acquire(context, false)); var native = new Lazy<Task<Input>>(() => Acquire(context, true));
        foreach (var language in new[] { "csharp", "cpp" })
        {
            var selected = language;
            yield return new("source_relations.actual_" + selected + "_direct_call_and_exact_arguments", async () =>
            {
                var input = await (selected == "cpp" ? native.Value : managed.Value);
                using var store = Store(input); var result = Normalize(store, input.Unit.Binding.UnitId);
                Check.Equal("complete", result.Status); Check.Equal(1, result.CallSiteIds.Count);
                var site = store.ReadRows("call_site").Single(); var occurrence = store.ReadRows("occurrence").Single(); var relationship = store.ReadRows("relationship").Single();
                var caller = store.ReadRows("entity").Single(row => Equals(row["id"], site["caller_id"]));
                var target = store.ReadRows("entity").Single(row => Equals(row["id"], site["selected_declaration_id"]));
                Check.Equal("Choose", Text(caller, "written_name")); Check.Equal("Add", Text(target, "written_name"));
                Check.Equal("direct", Text(site, "dispatch")); Check.Equal("not_analyzed", Text(site, "candidate_completeness"));
                Check.Equal("runtime_candidates_not_analyzed", Text(site, "remainder_reason"));
                Check.Equal(site["id"], occurrence["call_site_id"]); Check.Equal(occurrence["id"], site["occurrence_id"]);
                Check.Equal("direct_call", Text(relationship, "kind")); Check.Equal("resolved_reference", Text(relationship, "meaning"));
                Check.Equal(caller["id"], relationship["source_id"]); Check.Equal(target["id"], relationship["destination_id"]);
                var selectedTarget = store.ReadRows("call_target").Single(); Check.Equal("selected_declaration", Text(selectedTarget, "target_kind"));
                Check.Equal("compiler", Text(store.ReadRows("evidence").Single(row => Equals(row["id"], selectedTarget["evidence_id"])), "kind"));
                var arguments = store.ReadRows("call_argument").OrderBy(row => (long)row["ordinal"]!).ToArray(); Check.Equal(2, arguments.Length);
                var first = store.ReadRows("operation").Single(row => Equals(row["id"], arguments[0]["expression_id"]));
                var second = store.ReadRows("operation").Single(row => Equals(row["id"], arguments[1]["expression_id"]));
                Check.Equal(selected == "cpp" ? "conversion" : "reference", Text(first, "operation_kind"));
                var reference = first;
                if (selected == "cpp")
                {
                    Check.Equal("ImplicitCastExpr", Text(first, "compiler_kind"));
                    var child = store.ReadRows("operation_child").Single(row => Equals(row["parent_id"], first["id"]));
                    reference = store.ReadRows("operation").Single(row => Equals(row["id"], child["child_id"]));
                    Check.Equal("reference", Text(reference, "operation_kind"));
                }
                Check.Equal("value", Text(store.ReadRows("entity").Single(row => Equals(row["id"], reference["referenced_entity_id"])), "written_name"));
                Check.Equal("literal", Text(second, "operation_kind")); Check.Equal("1", Text(second, "constant_value"));
                foreach (var argument in arguments)
                {
                    Check.Equal("value", Text(argument, "passing_mode"));
                    var parameter = store.ReadRows("entity").Single(row => Equals(row["id"], argument["parameter_id"])); Check.Equal(target["id"], parameter["containing_entity_id"]);
                    var detail = store.ReadRows("entity_detail").Single(row => Equals(row["entity_id"], parameter["id"])); Check.Equal(argument["ordinal"], detail["parameter_position"]);
                    Check.Equal(selected == "cpp" ? "int" : "System.Int32", Text(store.ReadRows("type_node").Single(row => Equals(row["id"], detail["type_id"])), "builtin_name"));
                }
                Check.Equal(2, store.ReadRows("occurrence_argument").Count);
                Check.Equal("unknown", State(store, Text(site, "id")!, "/call_target/@collection/call_target"));
                Check.Equal("unknown", State(store, Text(caller, "id")!, "/relationship/@collection/outgoing_relationships"));
                Check.Equal("known", State(store, Text(caller, "id")!, "/relationship/@collection/outgoing_relationships:direct_call"));
                Check.Equal("unknown", State(store, Text(target, "id")!, "/relationship/@collection/incoming_relationships:direct_call"));
                foreach (var body in store.ReadRows("body")) Check.Equal("known", State(store, Text(body, "owner_entity_id")!, "/call_site/@collection/calls"));
                Check.Equal(4, store.ReadRows("coverage").Count(row => Text(row, "feature") == "direct_calls" && Text(row, "status") == "complete"));
                Check.Equal(0, store.ReadRows("analysis_run").Count); Check.Equal(0, store.ReadRows("cfg_block").Count);
                var tool = store.ReadRows("tool").Single(row => Text(row, "role") == "source_normalizer");
                Check.Equal(CanonicalJson.Sha256(await File.ReadAllBytesAsync(typeof(SourceRelations).Assembly.Location)), Text(tool, "content_hash"));
                Check.True(!store.ReadRows("context_tool").Any(row => Equals(row["tool_id"], tool["id"])));
                Check.True(store.ReadRows("evidence").Any(row => Text(row, "kind") == "derivation" && row["detail"] is null && Equals(row["tool_id"], tool["id"])));
                var count = store.ReadRows("record").Count; var again = Normalize(store, input.Unit.Binding.UnitId);
                Check.Equal(count, store.ReadRows("record").Count); Check.Equal(result.InputFingerprint, again.InputFingerprint); Check.Equal(result.CallSiteIds[0], again.CallSiteIds[0]);
                await File.WriteAllBytesAsync(Path.Combine(context.ScratchRoot, "source-relations-" + selected + ".json"), CanonicalJson.Serialize(new
                { result.UnitId, result.InputFingerprint, result.CallSiteIds, result.Status, normalizer_tool_id = tool["id"], binary_sha256 = tool["content_hash"] }, true));
            });
        }
        yield return new("source_relations_prior_gate_and_unknown_unit_roll_back", async () =>
        {
            var input = await native.Value; var path = Path.Combine(context.ScratchRoot, "source-relation-gates.sqlite"); using var store = Store(input, path);
            foreach (var foundation in new[] { true, false }) using (var write = store.BeginWrite())
            {
                write.NormalizeDirectSourceRelations(input.Unit.Binding.UnitId);
                if (foundation) Check.Throws<FoundationValidationException>(() => write.CommitFoundation());
                else Check.Throws<SemanticCollectionValidationException>(() => write.CommitSemanticCollection());
            }
            Check.Equal(0, store.ReadRows("call_site").Count); Check.Equal(0, store.ReadRows("tool").Count(row => Text(row, "role") == "source_normalizer"));
            using (var write = store.BeginWrite()) Check.Throws<InvalidDataException>(() => write.NormalizeDirectSourceRelations("compilation_unit:" + new string('0', 64)));
            Check.Equal(0, store.ReadRows("relationship").Count); Check.True(store.ValidateSemanticCollection().IsValid);
            var unused = store.ReadRows("entity").Single(row => Text(row, "written_name") == "Unused");
            using var raw = new SqliteConnection("Data Source=" + path + ";Pooling=False"); raw.Open(); using var command = raw.CreateCommand();
            command.CommandText = "UPDATE field_state SET state='known' WHERE record_id=$id AND field_path='/call_site/@collection/calls'"; command.Parameters.AddWithValue("$id", unused["id"]); command.ExecuteNonQuery();
            try { Check.True(store.ValidateSourceRelationsCollection().Issues.Any(issue => issue.Code == "source_call_coverage"), "Zero call rows must not establish an unscanned known-empty collection."); }
            finally { command.CommandText = "UPDATE field_state SET state='not_collected' WHERE record_id=$id AND field_path='/call_site/@collection/calls'"; command.ExecuteNonQuery(); }
        });
        yield return new("source_relations_partial_and_unsupported_calls_preserve_located_uncertainty", async () =>
        {
            var input = await managed.Value;
            foreach (var variant in new[] { "partial", "unsupported", "nonstatic" })
            {
                var facts = JsonNode.Parse(input.Unit.Result.Facts.GetRawText())!.AsObject();
                var call = facts["operations"]!.AsArray().Single(row => row!["operation_kind"]!.GetValue<string>() == "invocation")!;
                var body = facts["bodies"]!.AsArray().Single(row => row!["key"]!.GetValue<string>() == call["body_key"]!.GetValue<string>())!;
                body["status"] = "partial"; body["reason"] = "fixture_partial_source_body";
                if (variant == "unsupported") { call["supported"] = false; call["unsupported_reason"] = "fixture_unsupported_call"; }
                if (variant == "nonstatic")
                {
                    var modifiers = facts["entity_modifiers"]!.AsArray();
                    var modifier = modifiers.Single(row => row!["entity_key"]!.GetValue<string>() == call["referenced_entity_key"]!.GetValue<string>() && row["modifier"]!.GetValue<string>() == "static");
                    modifiers.Remove(modifier);
                }
                var span = facts["spans"]!.AsArray().Single(row => row!["key"]!.GetValue<string>() == call["span_key"]!.GetValue<string>())!;
                var coverage = facts["coverage"]!.AsArray().Single(row => row!["file_key"]!.GetValue<string>() == span["file_key"]!.GetValue<string>() && row["feature"]!.GetValue<string>() == "operations")!;
                coverage["status"] = "partial"; coverage["reason"] = "fixture_partial_source_body"; coverage["unparsed_span_keys"] = new JsonArray(call["span_key"]!.GetValue<string>());
                using var document = CanonicalJson.Parse(Encoding.UTF8.GetBytes(facts.ToJsonString()));
                var accepted = SemanticResultValidator.Validate(input.Unit.Result with { Status = "partial", StopReason = "fixture_partial_source_body", Facts = document.RootElement.Clone() }, input.Unit.Binding);
                var path = Path.Combine(context.ScratchRoot, "partial-relations-" + variant + ".sqlite");
                using var store = Store(input with { Unit = accepted }, path); var result = Normalize(store, input.Unit.Binding.UnitId);
                Check.Equal("partial", result.Status); Check.Equal(variant == "partial" ? 1 : 0, store.ReadRows("call_site").Count);
                var choose = store.ReadRows("entity").Single(row => Text(row, "written_name") == "Choose");
                Check.Equal("unknown", State(store, Text(choose, "id")!, "/call_site/@collection/calls"));
                Check.True(store.ReadRows("coverage").Any(row => Text(row, "feature") == "direct_calls" && Text(row, "status") == "partial" && row["unresolved_count"] is null));
                if (variant != "partial") Check.True(store.ReadRows("coverage").Any(row => Text(row, "feature") == "direct_calls"
                    && Text(row, "reason") == (variant == "unsupported" ? "fixture_unsupported_call" : "nonstatic_call_dispatch_not_supported") && row["span_id"] is string));
                if (variant == "partial")
                {
                    var relationship = store.ReadRows("relationship").Single();
                    Check.Equal("unknown", State(store, Text(relationship, "id")!, "/relationship_occurrence/@collection/relationship_occurrence"));
                    using var raw = new SqliteConnection("Data Source=" + path + ";Pooling=False"); raw.Open(); using var command = raw.CreateCommand();
                    command.CommandText = "UPDATE field_state SET state='known' WHERE record_id=$id AND field_path='/relationship_occurrence/@collection/relationship_occurrence'";
                    command.Parameters.AddWithValue("$id", relationship["id"]); command.ExecuteNonQuery();
                    try { Check.True(store.ValidateSourceRelationsCollection().Issues.Any(issue => issue.Code == "source_relationship")); }
                    finally { command.CommandText = "UPDATE field_state SET state='unknown' WHERE record_id=$id AND field_path='/relationship_occurrence/@collection/relationship_occurrence'"; command.ExecuteNonQuery(); }
                }
                var count = store.ReadRows("record").Count; var repeated = Normalize(store, input.Unit.Binding.UnitId);
                Check.Equal(count, store.ReadRows("record").Count); Check.Equal(result.InputFingerprint, repeated.InputFingerprint);
            }
        });
        yield return new("source_relations_persisted_corruption_and_runtime_candidates_are_rejected", async () =>
        {
            var input = await native.Value; var path = Path.Combine(context.ScratchRoot, "source-relations-corruption.sqlite");
            using var store = Store(input, path); Normalize(store, input.Unit.Binding.UnitId);
            var site = store.ReadRows("call_site").Single(); var target = store.ReadRows("call_target").Single();
            using (var write = store.BeginWrite())
            {
                write.Insert("call_target", new Dictionary<string, object?> { ["call_site_id"] = site["id"], ["target_id"] = site["selected_declaration_id"], ["target_kind"] = "runtime_candidate", ["meaning"] = "may", ["evidence_id"] = target["evidence_id"] });
                Check.Throws<SourceRelationsValidationException>(() => write.CommitSourceRelationsCollection());
            }
            Check.Equal(1, store.ReadRows("call_target").Count);
            using var raw = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); raw.Open();
            void Mutate(string table, string column, string where, object original, object replacement, string code)
            {
                using var command = raw.CreateCommand(); command.CommandText = "UPDATE " + table + " SET " + column + "=$value WHERE " + where;
                var value = command.Parameters.AddWithValue("$value", replacement); command.ExecuteNonQuery();
                try { Check.True(store.ValidateSourceRelationsCollection().Issues.Any(issue => issue.Code == code), table + "." + column + " mutation escaped."); }
                finally { value.Value = original; command.ExecuteNonQuery(); }
                Check.True(store.ValidateSourceRelationsCollection().IsValid);
            }
            var arguments = store.ReadRows("call_argument").OrderBy(row => (long)row["ordinal"]!).ToArray();
            Mutate("call_argument", "parameter_id", "ordinal=0", arguments[0]["parameter_id"]!, arguments[1]["parameter_id"]!, "source_call");
            var wrong = store.ReadRows("entity").First(row => Text(row, "written_name") == "Unused");
            Mutate("call_site", "selected_declaration_id", "1=1", site["selected_declaration_id"]!, wrong["id"]!, "source_call");
            var occurrence = store.ReadRows("occurrence").Single();
            var otherSpan = store.ReadRows("source_span").First(row => !Equals(row["id"], occurrence["span_id"]));
            Mutate("occurrence", "span_id", "1=1", occurrence["span_id"]!, otherSpan["id"]!, "source_call");
            var literal = store.ReadRows("operation").First(row => Text(row, "constant_value") == "1");
            Mutate("operation", "constant_value", "id='" + Text(literal, "id") + "'", "1", "2", "source_relation_provenance");
            var tool = store.ReadRows("tool").Single(row => Text(row, "role") == "source_normalizer");
            Mutate("tool", "content_hash", "role='source_normalizer'", tool["content_hash"]!, new string('0', 64), "identity_columns");
            foreach (var (owner, field) in new[] { (site["caller_id"], "/relationship/@collection/outgoing_relationships"),
                (site["caller_id"], "/occurrence/@collection/occurrences"), (site["selected_declaration_id"], "/relationship/@collection/incoming_relationships"),
                (site["selected_declaration_id"], "/relationship/@collection/incoming_relationships:direct_call") })
                Mutate("field_state", "state", "record_id='" + owner + "' AND field_path='" + field + "'", "unknown", "known", "source_call");
            var summary = store.ReadRows("coverage").First(row => Text(row, "feature") == "direct_calls" && row["span_id"] is null);
            using (var command = raw.CreateCommand())
            {
                command.CommandText = "DELETE FROM coverage WHERE id=$id"; command.Parameters.AddWithValue("$id", summary["id"]); command.ExecuteNonQuery();
                try { Check.True(store.ValidateSourceRelationsCollection().Issues.Any(issue => issue.Code == "source_call_coverage"), "Deleting a summary must remove any known-empty claim."); }
                finally
                {
                    command.Parameters.Clear(); var columns = summary.Keys.ToArray();
                    command.CommandText = "INSERT INTO coverage(" + string.Join(",", columns) + ") VALUES(" + string.Join(",", columns.Select((_, i) => "$v" + i)) + ")";
                    for (var index = 0; index < columns.Length; index++) command.Parameters.AddWithValue("$v" + index, summary[columns[index]] ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
            }
            Check.True(store.ValidateSourceRelationsCollection().IsValid);
        });
    }
}

using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PromptedLearning.Core;
using PromptedLearning.Managed;
using PromptedLearning.Storage;

namespace PromptedLearning.Tests;

public static class SemanticStorageTests
{
    private sealed record Inputs(MsBuildEvaluation Evaluation, SealedCapture Capture, ValidatedSemanticUnit Unit, JsonElement UnitIdentity);
    private static async Task<Inputs> Managed(TestContext context)
    {
        var fixture = await ManagedTests.Acquire(context); var request = fixture.Request;
        var result = await new ManagedPilotAdapter().AnalyzeAsync(request);
        var binding = await ManagedPilotBinding.CreateAsync(request);
        var validated = SemanticResultValidator.Validate(result, binding);
        var inputs = request.Sources.Select(source => (object)new { role = "source", file_version_id = source.FileVersionId, provider = (object?)null })
            .Concat(request.Evaluation.Providers.Select(provider => (object)new { role = "reference", file_version_id = (string?)null, provider = provider.Descriptor })).ToArray();
        return new(request.Evaluation, request.Capture, validated, StableId.Envelope("compilation_unit/1",
            new { context_id = request.ContextId, language = "csharp", inputs, output_identity = "Pilot" }));
    }
    private static async Task<Inputs> Native(TestContext context)
    {
        var fixture = await NativeHostTests.Acquire(context);
        var analysis = await new NativePilotAdapter().AnalyzeAsync(fixture.Evaluation, fixture.Capture, fixture.ContextId);
        return new(fixture.Evaluation, fixture.Capture, analysis.Unit, analysis.UnitIdentityInput);
    }
    private static CanonicalStore Foundation(Inputs input, string? path = null)
    {
        var store = path is null ? CanonicalStore.CreateInMemory() : CanonicalStore.Create(path);
        using var write = store.BeginWrite();
        write.ImportCapturedSources(input.Capture.Inputs, DateTimeOffset.UnixEpoch);
        write.ImportEvaluatedContext(input.Evaluation, input.Capture.Inputs);
        write.CommitFoundation(); return store;
    }
    private static string? Text(IReadOnlyDictionary<string, object?> row, string column) => row[column] as string;
    private static ImportedSemanticUnit Import(CanonicalStore store, Inputs input)
    {
        using var write = store.BeginWrite(); var imported = write.ImportSemanticUnit(input.Unit, input.UnitIdentity);
        write.CommitSemanticCollection(); return imported;
    }

    private static void RejectNullWithState(CanonicalStore store, SqliteConnection raw, string table, string column,
        string owner, Dictionary<string, object?> key, object original, string issueCode)
    {
        var encoded = Convert.ToBase64String(CanonicalJson.Serialize(key.Values.ToArray())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var fieldPath = "/" + table + "/" + encoded + "/" + column;
        using var state = raw.CreateCommand();
        state.CommandText = "INSERT INTO field_state(record_id,field_path,state,reason,evidence_id) VALUES ($owner,$path,'not_applicable','inactive_variant',NULL)";
        state.Parameters.AddWithValue("$owner", owner); state.Parameters.AddWithValue("$path", fieldPath);
        state.ExecuteNonQuery();
        using var mutation = raw.CreateCommand();
        mutation.CommandText = "UPDATE " + table + " SET " + column + "=$value WHERE " + string.Join(" AND ", key.Keys.Select((name, index) => name + "=$k" + index));
        var value = mutation.Parameters.AddWithValue("$value", DBNull.Value); var ordinal = 0;
        foreach (var component in key.Values) mutation.Parameters.AddWithValue("$k" + ordinal++, component ?? DBNull.Value);
        mutation.ExecuteNonQuery();
        try { Check.True(store.ValidateSemanticCollection().Issues.Any(issue => issue.Code == issueCode && issue.RecordId == owner), table + "." + column + " must remain required even with an inapplicable state."); }
        finally
        {
            value.Value = original; mutation.ExecuteNonQuery();
            state.CommandText = "DELETE FROM field_state WHERE record_id=$owner AND field_path=$path"; state.ExecuteNonQuery();
        }
        Check.True(store.ValidateSemanticCollection().IsValid, "Null mutation must be restored before the next negative.");
    }

    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        var managed = new Lazy<Task<Inputs>>(() => Managed(context));
        var native = new Lazy<Task<Inputs>>(() => Native(context));
        yield return new("semantic_storage_historical_schema_rejected_without_migration", async () =>
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = context.ProjectRoot, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("show"); start.ArgumentList.Add("da35e5da506d08680f73de48408ba2b2c68b1d07:docs/m0/schema.sql");
            using var git = Process.Start(start) ?? throw new InvalidOperationException("Cannot read pinned historical schema.");
            var output = git.StandardOutput.ReadToEndAsync(); var error = git.StandardError.ReadToEndAsync();
            await git.WaitForExitAsync(); var historical = await output;
            Check.Equal(0, git.ExitCode, await error);
            var folder = Path.Combine(context.ScratchRoot, "historical-schema-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "schema.sql"), historical, new UTF8Encoding(false));
            var path = Path.Combine(folder, "collecting.sqlite");
            using (var raw = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                raw.Open(); using var command = raw.CreateCommand(); command.CommandText = historical; command.ExecuteNonQuery();
            }
            var before = CanonicalJson.Sha256(await File.ReadAllBytesAsync(path));
            var rejection = Check.Throws<InvalidDataException>(() => CanonicalStore.Open(path));
            Check.True(rejection.Message.Contains("fresh collecting generation", StringComparison.Ordinal));
            Check.Equal(before, CanonicalJson.Sha256(await File.ReadAllBytesAsync(path)), "Historical database must remain unchanged.");
        });
        foreach (var language in new[] { "csharp", "cpp" })
        {
            var selected = language;
            yield return new("semantic_storage.actual_" + selected + "_facts_commit_and_reconcile", async () =>
            {
                var input = await (selected == "csharp" ? managed.Value : native.Value);
                using var store = Foundation(input); var imported = Import(store, input);
                Check.Equal(CanonicalJson.Sha256(CanonicalJson.Serialize(input.Unit.Result)), imported.ResultFingerprint);
                await File.WriteAllBytesAsync(Path.Combine(context.ScratchRoot, "accepted-result-" + selected + ".json"), CanonicalJson.Serialize(new
                { language = selected, input.Unit.Result.UnitId, input.Unit.Result.SemanticInputFingerprint, imported.ResultFingerprint }, trailingNewline: true));
                Check.True(store.ValidateSemanticCollection().IsValid);
                Check.True(!store.ValidateFoundation().IsValid, "M1A validation must still reject semantic producer rows.");
                Check.Equal(4, store.ReadRows("body").Count);
                Check.Equal(6, store.ReadRows("entity").Count(row => Text(row, "entity_kind") == "parameter"));
                var choose = store.ReadRows("entity").Single(row => Text(row, "written_name") == "Choose");
                var body = store.ReadRows("body").Single(row => Text(row, "owner_entity_id") == Text(choose, "id"));
                var operations = store.ReadRows("operation").Where(row => Text(row, "body_id") == Text(body, "id")).ToArray(); Check.Equal(22, operations.Length);
                var call = operations.Single(row => Text(row, "operation_kind") == "invocation");
                Check.Equal("Add", Text(store.ReadRows("entity").Single(row => Text(row, "id") == Text(call, "referenced_entity_id")), "written_name"));
                Check.Equal(0, store.ReadRows("cfg_block").Count); Check.Equal(0, store.ReadRows("analysis_result").Count);
                Check.True(store.ReadRows("unit_input").All(row => Equals(row["observed_consumed"], 1L)));
                Check.Equal("complete", Text(store.ReadRows("compilation_unit").Single(), "status"));
                Check.True(store.ReadRows("coverage").Where(row => Text(row, "feature") is "control_flow" or "storage_events").All(row => Text(row, "status") == "not_attempted"));
                var count = store.ReadRows("record").Count;
                var repeated = Import(store, input); Check.Equal(count, store.ReadRows("record").Count);
                Check.Equal(imported.LocalIds.Count, repeated.LocalIds.Count);
                if (selected == "cpp")
                {
                    var functions = store.ReadRows("type_node").Where(row => Text(row, "type_kind") == "function").ToArray(); Check.True(functions.Length > 0);
                    foreach (var function in functions)
                    {
                        var signature = store.ReadRows("signature").Single(row => Text(row, "id") == Text(function, "function_signature_id"));
                        Check.Equal(Text(function, "id"), Text(signature, "callable_id")); Check.Equal("function_type", Text(signature, "callable_kind"));
                        Check.Equal("absent", Text(signature, "body_state"));
                        Check.True(store.ReadRows("parameter").Where(row => Text(row, "signature_id") == Text(signature, "id")).All(row => row["entity_id"] is null && row["name"] is null));
                    }
                    Check.True(store.ReadRows("type_node").Any(row => Text(row, "type_kind") == "pointer" && functions.Any(function => Text(function, "id") == Text(row, "element_type_id"))));
                    var unused = store.ReadRows("entity").Single(row => Text(row, "written_name") == "Unused");
                    Check.Equal(Text(store.ReadRows("entity_detail").Single(row => Text(row, "entity_id") == Text(choose, "id")), "type_id"),
                        Text(store.ReadRows("entity_detail").Single(row => Text(row, "entity_id") == Text(unused, "id")), "type_id"));
                    Check.Equal(2, store.ReadRows("annotation").Count); Check.Equal(2, store.ReadRows("preprocessing").Count);
                    Check.Equal(1, store.ReadRows("unit_input").Count(row => Text(row, "role") == "included_header"));
                }
            });
        }
        yield return new("semantic_storage.foundation_gate_and_conflicting_reimport_roll_back", async () =>
        {
            var input = await managed.Value;
            using (var store = Foundation(input))
            {
                using var write = store.BeginWrite(); write.ImportSemanticUnit(input.Unit, input.UnitIdentity);
                Check.Throws<FoundationValidationException>(() => write.CommitFoundation());
                Check.Equal(0, store.ReadRows("entity").Count);
                Check.Equal("not_attempted", Text(store.ReadRows("compilation_unit").Single(), "status"));
                Check.True(store.ReadRows("unit_input").All(row => row["observed_consumed"] is null));
            }
            using (var store = Foundation(input))
            {
                Import(store, input); var before = store.ReadRows("record").Count;
                var node = JsonNode.Parse(input.Unit.Result.Facts.GetRawText())!.AsObject();
                node["entities"]!.AsArray()[0]!["qualified_name"] = "contradictory retained name";
                using var document = CanonicalJson.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()));
                var result = input.Unit.Result with { Facts = document.RootElement.Clone() };
                var validShape = SemanticResultValidator.Validate(result, input.Unit.Binding);
                using var write = store.BeginWrite();
                Check.Throws<InvalidDataException>(() => write.ImportSemanticUnit(validShape, input.UnitIdentity));
                Check.Throws<InvalidOperationException>(() => write.CommitSemanticCollection());
                write.Dispose(); Check.Equal(before, store.ReadRows("record").Count);
            }
        });
        yield return new("semantic_storage_mutated_validation_token_and_wrong_unit_cannot_publish", async () =>
        {
            var input = await managed.Value;
            using var store = Foundation(input);
            using (var write = store.BeginWrite())
            {
                var wrong = StableId.Envelope("compilation_unit/1", new { context_id = input.Unit.Binding.ContextId, language = "csharp",
                    inputs = Array.Empty<object>(), output_identity = "Pilot" });
                Check.Throws<InvalidDataException>(() => write.ImportSemanticUnit(input.Unit, wrong));
            }
            Check.Equal(0, store.ReadRows("entity").Count);
            var bytes = input.Unit.Binding.Files[0].Bytes; var original = bytes[0]; bytes[0] ^= 1;
            try
            {
                using var write = store.BeginWrite(); Check.Throws<InvalidDataException>(() => write.ImportSemanticUnit(input.Unit, input.UnitIdentity));
            }
            finally { bytes[0] = original; }
            Check.Equal(0, store.ReadRows("entity").Count);
        });
        yield return new("semantic_storage_partial_results_preserve_located_reasons_and_unknown_collections", async () =>
        {
            var input = await managed.Value;
            var facts = JsonNode.Parse(input.Unit.Result.Facts.GetRawText())!.AsObject();
            var body = facts["bodies"]!.AsArray()[0]!; var spanKey = body["span_key"]!.GetValue<string>();
            var span = facts["spans"]!.AsArray().Single(span => span!["key"]!.GetValue<string>() == spanKey)!;
            var fileKey = span["file_key"]!.GetValue<string>();
            body["status"] = "partial"; body["reason"] = "fixture_unsupported_body_region";
            var coverage = facts["coverage"]!.AsArray().Single(row => row!["file_key"]!.GetValue<string>() == fileKey && row["feature"]!.GetValue<string>() == "operations")!;
            coverage["status"] = "partial"; coverage["reason"] = "fixture_unparsed_region"; coverage["unparsed_span_keys"] = new JsonArray(spanKey);
            using var document = CanonicalJson.Parse(Encoding.UTF8.GetBytes(facts.ToJsonString()));
            var result = input.Unit.Result with { Status = "partial", StopReason = "fixture_partial", Facts = document.RootElement.Clone() };
            var accepted = SemanticResultValidator.Validate(result, input.Unit.Binding);
            using var store = Foundation(input);
            var imported = Import(store, input with { Unit = accepted });
            Check.Equal(CanonicalJson.Sha256(CanonicalJson.Serialize(result)), imported.ResultFingerprint);
            Check.True(imported.ResultFingerprint != CanonicalJson.Sha256(CanonicalJson.Serialize(input.Unit.Result)));
            await File.WriteAllBytesAsync(Path.Combine(context.ScratchRoot, "accepted-result-partial.json"), CanonicalJson.Serialize(new
            { status = result.Status, result.UnitId, result.SemanticInputFingerprint, imported.ResultFingerprint }, trailingNewline: true));
            var canonicalSpan = imported.LocalIds[spanKey]; var canonicalBody = imported.LocalIds[body["key"]!.GetValue<string>()];
            Check.True(store.ReadRows("coverage").Any(row => Text(row, "span_id") == canonicalSpan && Text(row, "reason") == "fixture_unparsed_region"));
            Check.True(store.ReadRows("coverage").Any(row => Text(row, "subject_id") == canonicalBody && Text(row, "reason") == "fixture_unsupported_body_region"));
            Check.True(store.ReadRows("field_state").Any(row => Text(row, "record_id") == canonicalBody && Text(row, "field_path") == "/operation/@collection/operations:semantic"
                && Text(row, "state") == "unknown" && Text(row, "reason") == "fixture_unsupported_body_region"));
            Check.True(store.ReadRows("coverage").Where(row => Text(row, "status") == "partial").All(row => row["unresolved_count"] is null));
        });
        yield return new("semantic_storage_low_level_operand_and_signature_corruption_is_rejected", async () =>
        {
            var input = await native.Value;
            var path = Path.Combine(context.ScratchRoot, "semantic-corruption-" + Guid.NewGuid().ToString("N") + ".sqlite");
            using var store = Foundation(input, path); Import(store, input);
            var operation = store.ReadRows("operation").First(); var body = store.ReadRows("body").First();
            var before = store.ReadRows("operation_operand").Count;
            using (var write = store.BeginWrite())
            {
                write.Insert("operation_operand", new Dictionary<string, object?> { ["operation_id"] = operation["id"], ["role"] = "capture", ["ordinal"] = 0L,
                    ["target_id"] = body["id"], ["parameter_id"] = null });
                write.SetFieldState(Text(operation, "id")!, "operation_operand", [operation["id"], "capture", 0L], "parameter_id", "not_applicable", "inactive_variant");
                var error = Check.Throws<SemanticCollectionValidationException>(() => write.CommitSemanticCollection());
                Check.True(error.Result.Issues.Any(issue => issue.Code == "operand_semantics"));
            }
            Check.Equal(before, store.ReadRows("operation_operand").Count);
            var functions = store.ReadRows("type_node").Where(row => Text(row, "type_kind") == "function").ToArray();
            var functionSignature = store.ReadRows("signature").Single(row => Text(row, "id") == Text(functions[0], "function_signature_id"));
            var entity = store.ReadRows("entity").First(row => Text(row, "entity_kind") == "function");
            using var raw = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); raw.Open();
            void Mutate(string table, string column, string keyColumn, object key, object? replacement, string expectedCode, string expectedId)
            {
                using var command = raw.CreateCommand();
                command.CommandText = "SELECT " + column + " FROM " + table + " WHERE " + keyColumn + "=$id";
                command.Parameters.AddWithValue("$id", key); var original = command.ExecuteScalar();
                command.CommandText = "UPDATE " + table + " SET " + column + "=$value WHERE " + keyColumn + "=$id";
                var value = command.Parameters.AddWithValue("$value", replacement ?? DBNull.Value); command.ExecuteNonQuery();
                try
                {
                    Check.True(store.ValidateSemanticCollection().Issues.Any(issue => issue.Code == expectedCode && issue.RecordId == expectedId), table + "." + column + " mutation was not diagnosed.");
                }
                finally { value.Value = original ?? DBNull.Value; command.ExecuteNonQuery(); }
                Check.True(store.ValidateSemanticCollection().IsValid, "Mutation must be restored before the next independent negative.");
            }
            var signatureId = Text(functionSignature, "id")!;
            Mutate("signature", "callable_id", "id", signatureId, entity["id"], "identity_columns", signatureId);
            var builtin = store.ReadRows("type_node").First(row => Text(row, "type_kind") == "builtin");
            Mutate("signature", "callable_id", "id", signatureId, builtin["id"], "identity_columns", signatureId);
            var otherReturn = store.ReadRows("type_node").First(row => Text(row, "type_kind") == "builtin" && !Equals(row["id"], functionSignature["return_type_id"]));
            Mutate("signature", "return_type_id", "id", signatureId, otherReturn["id"], "identity_columns", signatureId);
            Mutate("type_node", "function_signature_id", "id", functions[0]["id"]!, functions[1]["function_signature_id"], "identity_columns", Text(functions[0], "id")!);
            var abstractParameter = store.ReadRows("parameter").First(row => Text(row, "signature_id") == signatureId);
            var declaredParameter = store.ReadRows("entity").First(row => Text(row, "entity_kind") == "parameter");
            Mutate("parameter", "entity_id", "id", abstractParameter["id"]!, declaredParameter["id"], "identity_columns", signatureId);
            var callableDetail = store.ReadRows("entity_detail").First(row => row["signature_id"] is string);
            var otherSignature = store.ReadRows("signature").First(row => Text(row, "callable_kind") != "function_type" && !Equals(row["id"], callableDetail["signature_id"]));
            Mutate("entity_detail", "signature_id", "entity_id", callableDetail["entity_id"]!, otherSignature["id"], "entity_semantics", Text(callableDetail, "entity_id")!);
            using (var command = raw.CreateCommand())
            {
                command.CommandText = "INSERT INTO signature_qualifier(signature_id,ordinal,qualifier) VALUES ($id,0,'const')";
                command.Parameters.AddWithValue("$id", signatureId); command.ExecuteNonQuery();
                try { Check.True(store.ValidateSemanticCollection().Issues.Any(issue => issue.Code == "identity_columns" && issue.RecordId == signatureId)); }
                finally { command.CommandText = "DELETE FROM signature_qualifier WHERE signature_id=$id AND ordinal=0"; command.ExecuteNonQuery(); }
            }
            Check.True(store.ValidateSemanticCollection().IsValid);
            var call = store.ReadRows("operation").First(row => Text(row, "operation_kind") == "invocation");
            var mapped = store.ReadRows("operation_operand").Where(row => Text(row, "operation_id") == Text(call, "id") && row["parameter_id"] is string).OrderBy(row => (long)row["ordinal"]!).ToArray();
            using (var command = raw.CreateCommand())
            {
                command.CommandText = "UPDATE operation_operand SET parameter_id=$parameter WHERE operation_id=$id AND role='argument' AND ordinal=0";
                command.Parameters.AddWithValue("$id", call["id"]); var value = command.Parameters.AddWithValue("$parameter", mapped[1]["parameter_id"]); command.ExecuteNonQuery();
                try { Check.True(store.ValidateSemanticCollection().Issues.Any(issue => issue.Code == "operand_semantics" && issue.RecordId == Text(call, "id"))); }
                finally { value.Value = mapped[0]["parameter_id"]!; command.ExecuteNonQuery(); }
            }
            Check.True(store.ValidateSemanticCollection().IsValid);
        });
        yield return new("semantic_storage_required_links_reject_null_even_with_inapplicable_state", async () =>
        {
            foreach (var input in new[] { await managed.Value, await native.Value })
            {
                var path = Path.Combine(context.ScratchRoot, "required-links-" + Guid.NewGuid().ToString("N") + ".sqlite");
                using var store = Foundation(input, path); Import(store, input);
                using var raw = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); raw.Open();
                var detail = store.ReadRows("entity_detail").First(row => row["signature_id"] is string);
                RejectNullWithState(store, raw, "entity_detail", "signature_id", Text(detail, "entity_id")!,
                    new() { ["entity_id"] = detail["entity_id"] }, detail["signature_id"]!, "entity_semantics");
                foreach (var kind in input.Unit.Binding.Language == "csharp" ? new[] { "invocation", "argument" } : new[] { "invocation" })
                {
                    var operation = store.ReadRows("operation").First(row => Text(row, "operation_kind") == kind);
                    var operand = store.ReadRows("operation_operand").First(row => Text(row, "operation_id") == Text(operation, "id") && row["parameter_id"] is string);
                    RejectNullWithState(store, raw, "operation_operand", "parameter_id", Text(operation, "id")!,
                        new() { ["operation_id"] = operation["id"], ["role"] = operand["role"], ["ordinal"] = operand["ordinal"] }, operand["parameter_id"]!, "operand_semantics");
                }
            }
        });
        yield return new("semantic_storage_reference_appearance_maps_to_unit_use", async () =>
        {
            var input = await managed.Value;
            var facts = JsonNode.Parse(input.Unit.Result.Facts.GetRawText())!.AsObject();
            var appearance = facts["appearances"]!.AsArray()[0]!; appearance["kind"] = "reference";
            var entityKey = appearance["entity_key"]!.GetValue<string>();
            using var document = CanonicalJson.Parse(Encoding.UTF8.GetBytes(facts.ToJsonString()));
            var accepted = SemanticResultValidator.Validate(input.Unit.Result with { Facts = document.RootElement.Clone() }, input.Unit.Binding);
            using var store = Foundation(input); var imported = Import(store, input with { Unit = accepted });
            Check.True(store.ReadRows("entity_unit").Any(row => Text(row, "entity_id") == imported.LocalIds[entityKey] && Text(row, "role") == "use"));
        });
    }
}

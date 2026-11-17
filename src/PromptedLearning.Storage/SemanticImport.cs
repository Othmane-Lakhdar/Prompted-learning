using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using PromptedLearning.Core;

namespace PromptedLearning.Storage;

public sealed record ImportedSemanticUnit(string UnitId, IReadOnlyDictionary<string, string> LocalIds, string ResultFingerprint);

/// <summary>Maps accepted source semantics into a collecting transaction. No analysis or finalization runs here.</summary>
public static partial class SemanticImport
{
    public static ImportedSemanticUnit ImportSemanticUnit(this CanonicalWrite write, ValidatedSemanticUnit unit, JsonElement unitIdentityInput)
        => write.ImportOperation(() => new Importer(write, unit, unitIdentityInput).Run());

    private sealed partial class Importer(CanonicalWrite write, ValidatedSemanticUnit accepted, JsonElement unitIdentityInput)
    {
        private readonly SemanticUnitResult result = accepted.Result;
        private readonly SemanticUnitBinding binding = accepted.Binding;
        private readonly Dictionary<string, string> ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> identityComponents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, IReadOnlyDictionary<string, object?>>> existing = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> primitiveTypes = new(StringComparer.Ordinal);
        private string compilerEvidence = "";
        private string captureEvidence = "";
        private string producerId = "";
        private IEnumerable<JsonElement> Facts(string name) => result.Facts.GetProperty(name).EnumerateArray();
        private JsonElement Fact(string key) => accepted.Records[key];
        private string? Ref(JsonElement row, string name) => Text(row, name) is { } key ? ids[key] : null;

        public ImportedSemanticUnit Run()
        {
            _ = SemanticResultValidator.Validate(result, binding); // Recheck a public binding before accepting its contents into a transaction.
            foreach (var file in binding.Files)
            {
                var version = One(write.ReadRows("file_version").Where(row => Text(row, "id") == file.FileVersionId), "Bound version is absent from canonical source.");
                Require(Text(version, "content_hash") == file.OriginalSha256 && Number(version, "byte_length") == file.Bytes.LongLength,
                    "Bound bytes differ from canonical capture.");
                ids.Add(file.Key, Text(version, "file_id")!);
            }
            var context = One(write.ReadRows("build_context").Where(row => Text(row, "id") == binding.ContextId), "Semantic context has not been imported.");
            Require(Text(context, "output_identity") == binding.OutputIdentity, "Semantic output differs from evaluated context.");
            var contextRecord = One(write.ReadRows("record").Where(row => Text(row, "id") == binding.ContextId), "Context registry is absent.");
            Require(Text(contextRecord, "snapshot_id") == binding.SnapshotId, "Semantic context belongs to another snapshot.");
            var tools = write.ReadRows("context_tool").Where(row => Text(row, "context_id") == binding.ContextId).Select(row => Text(row, "tool_id")).ToHashSet();
            producerId = Text(One(write.ReadRows("tool").Where(row => tools.Contains(Text(row, "id")) && Text(row, "content_hash") == result.Producer.BinarySha256),
                "Semantic producer binary is not uniquely declared in the context."), "id")!;
            captureEvidence = Text(One(write.ReadRows("evidence").Where(row => Text(row, "detail") == "source_snapshot_capture" && Text(row, "locator") == "capture:" + binding.SnapshotId),
                "Source capture evidence is absent."), "id")!;
            compilerEvidence = Evidence("compiler_symbol");
            ImportUnit();
            foreach (var span in Facts("spans")) Span(Text(span, "key")!);
            foreach (var entity in Facts("entities")) EntityId(Text(entity, "key")!);
            foreach (var type in Facts("types"))
                ids[Text(type, "key")!] = PrimitiveType(type.GetProperty("compiler_type_key"), Ref(type, "named_entity_key"));
            ImportEntities();
            ImportSignatures();
            ImportAppearancesAndBodies();
            ImportOperations();
            ImportAnnotationsAndPreprocessing();
            ImportCoverageAndDiagnostics();
            CompleteCollections();
            return new(binding.UnitId, new ReadOnlyDictionary<string, string>(ids), CanonicalJson.Sha256(CanonicalJson.Serialize(result)));
        }

        private string Evidence(string detail)
        {
            var locator = "record:" + binding.UnitId;
            var components = Generic("evidence", ["compiler", null, producerId, null, null, locator, CanonicalJson.Sha256(CanonicalJson.Serialize(detail))]);
            return Primary("evidence", components, Row(("kind", "compiler"), ("tool_id", producerId), ("locator", locator), ("detail", detail)),
                null, linkEvidence: false);
        }

        private JsonElement Generic(string family, object?[] parts) => CompilerFacts.Element(new
        {
            family, scope = new { snapshot_id = binding.SnapshotId, context_id = binding.ContextId, context_independent = false },
            producer_contract = "semantic-unit-result/1", producer_version = result.Producer.Version,
            input_fingerprint = result.SemanticInputFingerprint, local_key = new { rule = family + "-local-v1", parts }
        });

        private string Primary(string table, object components, Dictionary<string, object?> values, string? origin = null,
            bool independent = false, bool linkEvidence = true, IReadOnlyDictionary<string, (string State, string Reason)>? nullStates = null)
        {
            var envelope = StableId.Envelope(table + "/1", components);
            var id = StableId.Create(table + "/1", envelope.GetProperty("components"));
            identityComponents[id] = envelope.GetProperty("components").Clone();
            var section = StorageSchema.Dictionary.GetProperty("tables").EnumerateArray().Single(row => Text(row, "table") == table).GetProperty("section").GetString();
            Insert("record", Row(("id", id), ("kind", table), ("section", section), ("snapshot_id", binding.SnapshotId),
                ("context_id", independent ? null : binding.ContextId), ("context_independent", independent), ("origin_span_id", origin)));
            values["id"] = id;
            InsertWithStates(table, values, id, nullStates);
            Insert("record_extension", Row(("record_id", id), ("schema_name", "identity-input"), ("schema_version", 1L),
                ("payload_json", Encoding.UTF8.GetString(CanonicalJson.Serialize(envelope)))));
            Collection(id, "record_extension", "record_extension", "known");
            if (linkEvidence) Insert("record_evidence", Row(("record_id", id), ("evidence_id", independent ? captureEvidence : compilerEvidence), ("role", "compiler_source_semantics")));
            return id;
        }

        private void InsertWithStates(string table, Dictionary<string, object?> row, string owner,
            IReadOnlyDictionary<string, (string State, string Reason)>? nullStates = null)
        {
            foreach (var column in write.Schema.Tables[table].Columns.Values)
                if (!row.ContainsKey(column.Name)) row[column.Name] = null;
            Insert(table, row);
            foreach (var column in write.Schema.Tables[table].Columns.Values.Where(column => row[column.Name] is null && !column.Required && column.KeyOrdinal == 0))
            {
                var state = nullStates is not null && nullStates.TryGetValue(column.Name, out var explicitState)
                    ? explicitState : ("not_collected", "m1b_field_not_collected");
                ScalarState(owner, table, write.Schema.Tables[table].PrimaryKey.Select(key => row[key]).ToArray(), column.Name, state.Item1, state.Item2);
            }
        }

        private void ScalarState(string owner, string table, object?[] key, string column, string state, string? reason)
            => Insert("field_state", Row(("record_id", owner), ("field_path", "/" + table + "/" + Key(key) + "/" + column),
                ("state", state), ("reason", reason), ("evidence_id", null)));
        private void Collection(string owner, string table, string role, string state, string? reason = null)
            => Insert("field_state", Row(("record_id", owner), ("field_path", "/" + table + "/@collection/" + role.Replace("~", "~0").Replace("/", "~1")),
                ("state", state), ("reason", reason), ("evidence_id", null)));

        private void Insert(string table, Dictionary<string, object?> row)
        {
            var definition = write.Schema.Tables[table];
            if (!existing.TryGetValue(table, out var index)) existing[table] = index = write.ReadRows(table)
                .ToDictionary(prior => Key(definition.PrimaryKey.Select(column => prior[column]).ToArray()), StringComparer.Ordinal);
            var key = Key(definition.PrimaryKey.Select(column => Sql(row[column])).ToArray());
            if (index.TryGetValue(key, out var prior))
            {
                Require(row.All(pair => Equals(prior[pair.Key], Sql(pair.Value))), "Conflicting retained " + table + " facts for one canonical identity.");
                return;
            }
            write.Insert(table, row);
            index[key] = row.ToDictionary(pair => pair.Key, pair => Sql(pair.Value), StringComparer.Ordinal);
        }
        private void Transition(string table, IReadOnlyDictionary<string, object?> prior, Dictionary<string, object?> changes)
        {
            if (changes.All(pair => Equals(prior[pair.Key], Sql(pair.Value)))) return;
            write.UpdateCollecting(table, prior, changes.ToDictionary(pair => pair.Key, pair => Sql(pair.Value)));
            existing.Remove(table);
        }
        private static object? Sql(object? value) => value switch { bool boolean => boolean ? 1L : 0L, int number => (long)number, _ => value };
        private static Dictionary<string, object?> Row(params (string Name, object? Value)[] values) => values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
        private static string Key(object?[] values) => Convert.ToBase64String(CanonicalJson.Serialize(values)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static string? Text(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).GetString();
        private static string? Text(IReadOnlyDictionary<string, object?> row, string name) => row[name] as string;
        private static long Number(IReadOnlyDictionary<string, object?> row, string name) => Convert.ToInt64(row[name], System.Globalization.CultureInfo.InvariantCulture);
        private static T One<T>(IEnumerable<T> values, string reason) { var matches = values.Take(2).ToArray(); return matches.Length == 1 ? matches[0] : throw new InvalidDataException(reason); }
        private static void Require(bool valid, string reason) { if (!valid) throw new InvalidDataException(reason); }
        private static Dictionary<string, (string State, string Reason)> Inapplicable(params string[] columns)
            => columns.ToDictionary(column => column, _ => ("not_applicable", "inactive_variant"), StringComparer.Ordinal);
    }
}

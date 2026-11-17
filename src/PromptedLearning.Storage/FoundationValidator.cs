using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PromptedLearning.Core;

namespace PromptedLearning.Storage;

internal sealed partial class FoundationValidator(CanonicalStore store, SqliteConnection connection, SqliteTransaction? transaction, bool semanticCollection = false, bool sourceRelations = false)
{
    private static readonly HashSet<string> FoundationTables = new(StringComparer.Ordinal)
    {
        "schema_info", "dataset", "snapshot", "record", "field_state", "record_extension", "evidence", "record_evidence",
        "source_root", "exclusion", "source_blob", "source_file", "file_version", "file_path", "source_span",
        "project", "project_language", "solution", "solution_project", "configuration_mapping", "build_context",
        "context_setting", "context_input", "context_tool", "tool", "compilation_unit", "unit_input", "dependency"
    };
    private static readonly string[] SemanticTables = ["entity", "entity_unit", "entity_modifier", "appearance", "type_node", "type_argument", "type_qualifier",
        "signature", "signature_qualifier", "parameter", "entity_detail", "member_relation", "body", "operation", "operation_child", "operation_operand",
        "annotation", "annotation_argument", "preprocessing", "external_symbol", "unresolved_reference", "coverage", "diagnostic"];
    private static readonly string[] SourceRelationTables = ["relationship", "occurrence", "relationship_occurrence", "occurrence_argument", "call_site", "call_target", "call_argument"];
    private readonly HashSet<string> SupportedTables = FoundationTables.Concat(semanticCollection ? SemanticTables : []).Concat(sourceRelations ? SourceRelationTables : []).ToHashSet(StringComparer.Ordinal);
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9_-]*:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private readonly List<StorageIssue> issues = [];
    private readonly Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Table, IReadOnlyDictionary<string, object?> Row)> payload = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> registry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> tableContracts = StorageSchema.Dictionary.GetProperty("tables").EnumerateArray()
        .ToDictionary(x => x.GetProperty("table").GetString()!, x => x, StringComparer.Ordinal);
    private readonly JsonElement[] collections = StorageSchema.Dictionary.GetProperty("collections").EnumerateArray().ToArray();

    public FoundationValidationResult Validate(CancellationToken cancellationToken)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "PRAGMA foreign_keys";
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1) Add("foreign_keys_disabled", "record", null, "Foreign keys are disabled.");
            command.CommandText = "PRAGMA foreign_key_check";
            using var reader = command.ExecuteReader();
            while (reader.Read()) Add("foreign_key", reader.GetString(0), null, "Foreign key target missing: " + reader.GetString(2));
        }
        foreach (var table in store.Schema.Tables.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows[table] = store.ReadRows(table, transaction);
            if (rows[table].Count > 0 && !SupportedTables.Contains(table))
                Add("producer_not_in_m1a", table, null, "M1A validation does not accept this producer's records.");
        }
        foreach (var row in rows["dataset"])
            if (Text(row, "state") != "collecting") Add("not_collecting", "dataset", Text(row, "id"), "M1A cannot finalize or accept a finalized dataset.");
        foreach (var row in rows["record"]) registry.Add(Text(row, "id")!, row);
        foreach (var table in store.Schema.PrimaryTables)
            foreach (var row in rows[table])
            {
                var id = Text(row, "id")!;
                if (!payload.TryAdd(id, (table, row))) Add("multiple_payloads", table, id, "One ID has multiple primary typed payloads.");
            }
        ValidateRegistry();
        foreach (var row in rows["field_state"]) ValidateState(row);
        ValidateExtensions();
        foreach (var pair in payload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SupportedTables.Contains(pair.Value.Table)) continue;
            if (!identities.TryGetValue(pair.Key, out var envelope)) Add("identity_missing", pair.Value.Table, pair.Key, "Required identity-input/1 is absent.");
            else Guard(pair.Value.Table, pair.Key, "identity_columns", () => ValidateIdentity(pair.Key, pair.Value.Table, pair.Value.Row, envelope));
        }
        ValidateIdentityGraph();
        ValidateSources();
        ValidateStatesAndScopes();
        ValidateOrdering();
        if (semanticCollection) ValidateSemanticRows();
        if (sourceRelations) ValidateSourceRelationRows();
        return new(issues.AsReadOnly(), payload.Count);
    }

    private void ValidateRegistry()
    {
        foreach (var (id, record) in registry)
        {
            if (!IdPattern.IsMatch(id)) Add("invalid_id", "record", id, "ID is not family:full-lowercase-SHA256.");
            if (!payload.TryGetValue(id, out var target)) { Add("payload_missing", "record", id, "Registry record has no typed payload; canonical anchors are not allowed."); continue; }
            if (Text(record, "kind") != target.Table) Add("record_kind", "record", id, "Registry kind differs from typed family.");
            var section = tableContracts[target.Table].GetProperty("section").GetString();
            if (Text(record, "section") != section) Add("record_section", "record", id, "Registry section differs from typed family.");
            if (target.Table == "snapshot" && (Text(record, "snapshot_id") != id || Number(record, "context_independent") != 1 || record["context_id"] is not null))
                Add("snapshot_registry_pair", "snapshot", id, "Snapshot requires its independent same-ID registry pair.");
            if (target.Table == "build_context" && Text(record, "context_id") != id)
                Add("context_registry_scope", "build_context", id, "A context record must carry its own context ID.");
            if (target.Row.TryGetValue("context_id", out var context) && context is not null && !Equals(context, record["context_id"]))
                Add("context_mismatch", target.Table, id, "Typed and registry context differ.");
            if (target.Table is "snapshot" or "source_root" or "source_file" or "file_version" or "source_span" or "project" or "solution" or "tool" or "exclusion" or "configuration_mapping")
                if (Number(record, "context_independent") != 1) Add("foundation_scope", target.Table, id, "This foundation identity is context independent.");
        }
        foreach (var (id, target) in payload)
            if (!registry.ContainsKey(id)) Add("registry_missing", target.Table, id, "Typed payload has no registry row.");
    }

    private void ValidateExtensions()
    {
        foreach (var row in rows["record_extension"])
        {
            var id = Text(row, "record_id")!;
            Guard("record_extension", id, "extension_invalid", () =>
            {
                if (semanticCollection && Text(row, "schema_name") == "cpp-attribute" && Number(row, "schema_version") == 1)
                {
                    using var attribute = CanonicalJson.Parse(Encoding.UTF8.GetBytes(Text(row, "payload_json")!));
                    ContractCatalog.Default.Validate("cpp-attribute", attribute.RootElement);
                    Require(payload.TryGetValue(id, out var annotation) && annotation.Table == "annotation", "C++ attribute extension requires an annotation owner.");
                    return;
                }
                if (Text(row, "schema_name") != "identity-input" || Number(row, "schema_version") != 1)
                    throw new InvalidDataException("Extension is not registered for an M1A foundation owner.");
                using var document = CanonicalJson.Parse(Encoding.UTF8.GetBytes(Text(row, "payload_json")!));
                StableId.Verify(id, document.RootElement);
                if (!payload.TryGetValue(id, out var owner)) throw new InvalidDataException("Extension owner lacks typed payload.");
                if (document.RootElement.GetProperty("domain").GetString() != owner.Table + "/1") throw new InvalidDataException("Identity domain does not match typed owner.");
                identities.Add(id, document.RootElement.Clone());
            });
        }
    }

    private void ValidateIdentity(string id, string table, IReadOnlyDictionary<string, object?> row, JsonElement envelope)
    {
        var c = envelope.GetProperty("components");
        var scope = registry.GetValueOrDefault(id);
        void Same(string component, string column) => Require(JsonSame(c.GetProperty(component), row[column]), component + " differs from " + table + "." + column);
        if (c.TryGetProperty("snapshot_id", out var snapshot)) Require(scope is not null && JsonSame(snapshot, scope["snapshot_id"]), "Identity snapshot differs from registry.");
        if (c.TryGetProperty("context_id", out var context)) Require(scope is not null && JsonSame(context, scope["context_id"]), "Identity context differs from registry.");
        switch (table)
        {
            case "snapshot":
                Same("repository_identity", "repository_identity");
                var rootComponents = rows["source_root"].Where(r => Scope(r, "snapshot_id") == id)
                    .Select(r => identities.TryGetValue(Text(r, "id")!, out var e) ? e.GetProperty("components").GetProperty("root") : default)
                    .Where(e => e.ValueKind != JsonValueKind.Undefined).OrderBy(e => e.GetProperty("root_key").GetString(), CanonicalJson.Utf8Comparer).ToArray();
                Require(CanonicalJson.Serialize(c.GetProperty("roots")).SequenceEqual(CanonicalJson.Serialize(rootComponents)), "Snapshot root descriptors differ from retained roots.");
                var files = new List<object>();
                foreach (var file in rows["source_file"].Where(r => Scope(r, "snapshot_id") == id).OrderBy(r => RootName(r), CanonicalJson.Utf8Comparer).ThenBy(r => Text(r, "relative_path"), CanonicalJson.Utf8Comparer))
                {
                    var versions = rows["file_version"].Where(v => Text(v, "file_id") == Text(file, "id")).ToArray();
                    Require(versions.Length == 1, "A captured snapshot file needs exactly one immutable version.");
                    files.Add(new { root_key = RootName(file), relative_path = Text(file, "relative_path"), content_hash = Text(versions[0], "content_hash"), availability = Number(versions[0], "available") == 1 ? "available" : "unavailable", origin = Text(file, "origin") });
                }
                Require(CanonicalJson.Serialize(c.GetProperty("files")).SequenceEqual(CanonicalJson.Serialize(files)), "Snapshot file preimage differs from retained captures.");
                break;
            case "source_root":
                var root = c.GetProperty("root");
                Require(JsonSame(root.GetProperty("root_key"), row["logical_name"]) && JsonSame(root.GetProperty("boundary"), row["boundary"]), "Root descriptor differs from typed root.");
                Require(Text(row, "policy") == Encoding.UTF8.GetString(CanonicalJson.Serialize(root)), "Retained root policy differs from its exact declared descriptor.");
                var exclusions = rows["exclusion"].Where(e => Text(e, "root_id") == id).Select(e => new { rule = Text(e, "rule"), reason = Text(e, "reason") })
                    .OrderBy(e => e.rule, CanonicalJson.Utf8Comparer).ThenBy(e => e.reason, CanonicalJson.Utf8Comparer).ToArray();
                var declaredExclusions = root.GetProperty("exclusions").EnumerateArray()
                    .OrderBy(e => e.GetProperty("rule").GetString(), CanonicalJson.Utf8Comparer).ThenBy(e => e.GetProperty("reason").GetString(), CanonicalJson.Utf8Comparer).ToArray();
                Require(CanonicalJson.Serialize(declaredExclusions).SequenceEqual(CanonicalJson.Serialize(exclusions)), "Root exclusions differ from captured policy.");
                break;
            case "source_file": Same("relative_path", "relative_path"); Require(c.GetProperty("root_key").GetString() == RootName(row), "File logical root differs."); break;
            case "file_version":
                Same("file_id", "file_id"); Same("content_hash", "content_hash");
                Require(c.GetProperty("availability").GetString() == (Number(row, "available") == 1 ? "available" : "unavailable"), "Version availability differs.");
                if (Number(row, "available") == 1) Require(c.GetProperty("unavailable_reason").ValueKind == JsonValueKind.Null, "Available version has unavailable reason.");
                else Require(c.GetProperty("unavailable_reason").ValueKind == JsonValueKind.String, "Unavailable version lacks a reason.");
                break;
            case "source_span":
                foreach (var name in new[] { "file_version_id", "start_byte", "end_byte", "origin_kind" }) Same(name, name);
                var stack = c.GetProperty("origin_stack").EnumerateArray().ToArray();
                if (stack.Length == 0) Require(row["cause_span_id"] is null, "Empty origin stack has a cause link.");
                else
                {
                    var first = stack[0];
                    var cause = new { file_version_id = first.GetProperty("file_version_id").GetString(), start_byte = first.GetProperty("start_byte").GetInt64(), end_byte = first.GetProperty("end_byte").GetInt64(), origin_kind = first.GetProperty("origin_kind").GetString(), origin_stack = stack.Skip(1).ToArray() };
                    Require(Text(row, "cause_span_id") == StableId.Create("source_span/1", cause), "Cause does not match origin-stack suffix.");
                }
                break;
            case "project": case "solution":
                var definition = c.GetProperty("definition_file_version_id").GetString()!;
                Require(payload.TryGetValue(definition, out var version) && version.Table == "file_version" && Equals(version.Row["file_id"], row["definition_file_id"]), "Definition version does not resolve to the declared project/solution file.");
                if (table == "project") Same("system_kind", "system_kind");
                break;
            case "tool":
                var descriptor = c.GetProperty("descriptor");
                Require(JsonSame(descriptor.GetProperty("name"), row["name"]) && JsonSame(descriptor.GetProperty("version"), row["version"]), "Tool descriptor name/version differs.");
                ValidateKnowledgeValue(descriptor.GetProperty("content_fingerprint"), row["content_hash"]);
                break;
            case "build_context":
                foreach (var name in new[] { "project_id", "configuration", "platform", "architecture", "target_framework", "runtime_identifier", "language_version", "output_identity" }) Same(name, name);
                ValidateContext(id, row, c);
                break;
            case "compilation_unit":
                foreach (var name in new[] { "language", "output_identity" }) Same(name, name);
                Require(Text(row, "effective_fingerprint") == CanonicalJson.Sha256(CanonicalJson.Serialize(c)), "Unit effective fingerprint differs from its primitive identity components.");
                var expected = c.GetProperty("inputs").EnumerateArray().ToArray();
                var actual = rows["unit_input"].Where(x => Text(x, "unit_id") == id).ToArray();
                Require(expected.Length == actual.Length, "Unit input count differs from its frozen preimage.");
                foreach (var group in expected.GroupBy(e => e.GetProperty("role").GetString()))
                {
                    var selected = actual.Where(x => Text(x, "role") == group.Key).OrderBy(x => Number(x, "ordinal")).ToArray();
                    Require(selected.Length == group.Count(), "Unit input role count differs.");
                    foreach (var pair in group.Zip(selected))
                    {
                        Require(JsonSame(pair.First.GetProperty("file_version_id"), pair.Second["file_version_id"]), "Unit file input differs.");
                        var provider = pair.First.GetProperty("provider");
                        if (provider.ValueKind == JsonValueKind.Null) Require(pair.Second["dependency_id"] is null, "File input unexpectedly names a provider dependency.");
                        else
                        {
                            Require(payload.TryGetValue(Text(pair.Second, "dependency_id") ?? "", out var dependency) && dependency.Table == "dependency", "Provider input lacks a retained dependency.");
                            ValidateProvider(provider, dependency.Row);
                        }
                    }
                }
                if (Text(row, "language") == "cpp") Require(actual.Count(x => Text(x, "role") == "main" && Equals(x["file_version_id"], row["main_file_version_id"])) == 1, "Native unit must name exactly its main input.");
                break;
            default:
                if (!semanticCollection || !ValidateSemanticIdentity(id, table, row, c)) ValidateGenericIdentity(id, table, row, c);
                break;
        }
    }

    private void ValidateContext(string id, IReadOnlyDictionary<string, object?> row, JsonElement components)
    {
        Require(Text(row, "effective_fingerprint") == CanonicalJson.Sha256(CanonicalJson.Serialize(components)), "Context effective fingerprint differs from primitive identity components.");
        var mapping = components.GetProperty("solution_mapping");
        if (mapping.ValueKind == JsonValueKind.Null) Require(row["solution_id"] is null && row["configuration_mapping_id"] is null, "Context has an unexpected solution mapping.");
        else
        {
            Require(JsonSame(mapping.GetProperty("solution_id"), row["solution_id"]), "Context solution differs.");
            Require(payload.TryGetValue(Text(row, "configuration_mapping_id") ?? "", out var actual) && actual.Table == "configuration_mapping", "Context mapping is absent.");
            foreach (var property in mapping.EnumerateObject()) Require(JsonSame(property.Value, actual.Row[property.Name]), "Solution mapping differs: " + property.Name);
        }
        var expectedOptions = components.GetProperty("options").EnumerateArray().ToArray();
        var options = rows["context_setting"].Where(x => Text(x, "context_id") == id && Text(x, "category") == "compiler_option").OrderBy(x => Number(x, "ordinal")).ToArray();
        Require(expectedOptions.Length == options.Length, "Effective compiler-option count differs.");
        foreach (var pair in expectedOptions.Zip(options))
        {
            var path = pair.First.GetProperty("kind").GetString() == "path";
            var name = path ? pair.First.GetProperty("option").GetString() : "";
            var value = path ? "<" + pair.First.GetProperty("root_key").GetString() + ">/" + pair.First.GetProperty("relative_path").GetString() : pair.First.GetProperty("value").GetString();
            Require(Text(pair.Second, "name") == name && Text(pair.Second, "value") == value, "Effective compiler option differs from normalized descriptor.");
        }
        var tools = rows["context_tool"].Where(x => Text(x, "context_id") == id).Select(x => Text(x, "tool_id")!).Distinct(StringComparer.Ordinal).ToArray();
        var descriptors = components.GetProperty("tools").EnumerateArray().ToArray();
        Require(tools.Length == descriptors.Length, "Context tool descriptor count differs.");
        foreach (var tool in tools)
        {
            Require(identities.TryGetValue(tool, out var identity), "Context tool identity is absent.");
            Require(descriptors.Any(x => CanonicalJson.Serialize(x).SequenceEqual(CanonicalJson.Serialize(identity.GetProperty("components").GetProperty("descriptor")))), "Context tool descriptor differs.");
        }
    }

    private void ValidateGenericIdentity(string id, string table, IReadOnlyDictionary<string, object?> row, JsonElement components)
    {
        var scope = components.GetProperty("scope");
        Require(registry.TryGetValue(id, out var record), "Generic identity lacks registry scope.");
        Require(JsonSame(scope.GetProperty("snapshot_id"), record!["snapshot_id"]) && JsonSame(scope.GetProperty("context_id"), record["context_id"])
            && scope.GetProperty("context_independent").GetBoolean() == (Number(record, "context_independent") == 1), "Generic identity scope differs.");
        var recipe = ContractCatalog.Default.Root.GetProperty("x-identity-recipes").GetProperty(table + "/1");
        var names = recipe.GetProperty("local_key_parts").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var parts = components.GetProperty("local_key").GetProperty("parts").EnumerateArray().ToArray();
        for (var i = 0; i < names.Length; i++)
        {
            if (row.TryGetValue(names[i], out var value)) Require(JsonSame(parts[i], value), "Generic local key differs from " + names[i]);
            else if (names[i].StartsWith("sha256(", StringComparison.Ordinal) && names[i].EndsWith(')') && row.TryGetValue(names[i][7..^1], out value))
                Require(parts[i].GetString() == CanonicalJson.Sha256(CanonicalJson.Serialize(value)), "Generic identity content hash differs.");
        }
    }

    private void ValidateState(IReadOnlyDictionary<string, object?> row)
    {
        var owner = Text(row, "record_id")!; var path = Text(row, "field_path")!;
        Guard("field_state", owner, "field_state_path", () =>
        {
            var parts = path.Split('/'); Require(parts.Length == 4 && parts[0] == "", "Field path must have three exact components.");
            var table = parts[1]; Require(store.Schema.Tables.TryGetValue(table, out var definition), "Unknown field-state table.");
            Require(registry.ContainsKey(owner), "Field-state owner is absent.");
            if (parts[2] == "@collection")
            {
                var role = parts[3].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                var roleParts = role.Split(':');
                var candidates = collections.Where(c => c.GetProperty("table").GetString() == table && c.GetProperty("role").GetString() == roleParts[0]).ToArray();
                Require(candidates.Length == 1, "Unknown collection role.");
                var collection = candidates[0];
                var discriminators = collection.GetProperty("discriminator_columns").EnumerateArray().Select(x => x.GetString()!).ToArray();
                Require(roleParts.Length == 1 || roleParts.Length == discriminators.Length + 1, "Wrong collection discriminator arity.");
                var matching = rows[table].Where(r => CollectionOwner(collection, r) == owner);
                if (roleParts.Length > 1) for (var i = 0; i < discriminators.Length; i++)
                {
                    var column = discriminators[i]; var value = Uri.UnescapeDataString(roleParts[i + 1]);
                    matching = matching.Where(r => Convert.ToString(r[column], System.Globalization.CultureInfo.InvariantCulture) == value);
                }
                Require(CollectionAcceptsOwner(collection, owner), "Collection state owner has the wrong typed family.");
                if (Text(row, "state") is "not_applicable" or "not_collected") Require(!matching.Any(), "Absent collection state contradicts retained rows.");
            }
            else
            {
                var bytes = Convert.FromBase64String(parts[2].Replace('-', '+').Replace('_', '/') + new string('=', (4 - parts[2].Length % 4) % 4));
                using var keyDocument = CanonicalJson.Parse(bytes); var key = keyDocument.RootElement;
                Require(key.ValueKind == JsonValueKind.Array && key.GetArrayLength() == definition!.PrimaryKey.Count, "Field path has wrong primary key.");
                Require(Base64Key(key) == parts[2], "Field path key is not canonical base64url JSON.");
                Require(definition!.Columns.ContainsKey(parts[3]), "Unknown field-state column.");
                var target = rows[table].SingleOrDefault(r => definition!.PrimaryKey.Select((name, i) => JsonSame(key[i], r[name])).All(x => x));
                Require(target is not null, "Field-state addressed row is absent.");
                Require(Owner(table, target!) == owner, "Field-state ownership mismatch.");
                var value = target![parts[3]];
                Require(value is null ? Text(row, "state") != "known" : Text(row, "state") == "known", "Field knowledge contradicts scalar value.");
            }
            states.Add(owner + "\0" + path, row);
        });
    }

    private void ValidateStatesAndScopes()
    {
        foreach (var table in SupportedTables)
        foreach (var row in rows[table])
        {
            var owner = Owner(table, row);
            if (owner is null || table is "field_state" or "record_extension") continue;
            foreach (var column in store.Schema.Tables[table].Columns.Values.Where(c => !c.Required && c.KeyOrdinal == 0 && row[c.Name] is null))
            {
                if (StructuralNull(table, row, column.Name)) continue;
                var key = Base64Key(store.Schema.Tables[table].PrimaryKey.Select(k => row[k]).ToArray());
                if (!states.ContainsKey(owner + "\0/" + table + "/" + key + "/" + column.Name)) Add("missing_field_state", table, owner, "Unexplained NULL: " + column.Name);
            }
            if (!registry.TryGetValue(owner, out var sourceScope)) continue;
            foreach (var fk in store.Schema.Tables[table].ForeignKeys.Where(f => f.Table is not "snapshot" && f.Column != "id"))
            {
                if (row[fk.Column] is not string targetId || !registry.TryGetValue(targetId, out var targetScope)) continue;
                if (!Equals(sourceScope["snapshot_id"], targetScope["snapshot_id"])) Add("cross_snapshot_reference", table, owner, "Reference crosses snapshot: " + fk.Column);
                if (targetScope["context_id"] is not null && !Equals(sourceScope["context_id"], targetScope["context_id"])) Add("cross_context_reference", table, owner, "Reference crosses context: " + fk.Column);
            }
        }
        var detailCodes = ContractCatalog.Default.Root.GetProperty("x-provenance-policy").GetProperty("evidence_detail_allowed").EnumerateArray().ToArray();
        foreach (var evidence in rows["evidence"])
            if (!detailCodes.Any(x => JsonSame(x, evidence["detail"]))) Add("unregistered_evidence_detail", "evidence", Text(evidence, "id"), "Evidence detail is not a registered provenance code.");
    }

    private bool StructuralNull(string table, IReadOnlyDictionary<string, object?> row, string column)
    {
        if (table == "record" && (column == "context_id" && Number(row, "context_independent") == 1 || column == "origin_span_id")) return true;
        if (table == "source_file" && column == "generation_id" && Text(row, "origin") != "generated") return true;
        if (table == "source_span" && column == "cause_span_id" && identities.TryGetValue(Text(row, "id")!, out var spanIdentity) && spanIdentity.GetProperty("components").GetProperty("origin_stack").GetArrayLength() == 0) return true;
        if (table == "compilation_unit" && column == "main_file_version_id" && Text(row, "language") == "csharp") return true;
        if (table == "unit_input" && (column == "file_version_id" && row["dependency_id"] is not null || column == "dependency_id" && row["file_version_id"] is not null)) return true;
        if (table == "build_context" && column is "solution_id" or "configuration_mapping_id" && identities.TryGetValue(Text(row, "id")!, out var contextIdentity) && contextIdentity.GetProperty("components").GetProperty("solution_mapping").ValueKind == JsonValueKind.Null) return true;
        return false;
    }

    private void ValidateSources()
    {
        foreach (var blob in rows["source_blob"])
        {
            var hash = Text(blob, "hash")!;
            if (!HashPattern.IsMatch(hash) || Text(blob, "storage_key") != "sha256/" + hash[..Math.Min(2, hash.Length)] + "/" + hash)
                Add("blob_storage_key", "source_blob", hash, "Blob key must use its full lowercase hash relative to the sources root.");
        }
        foreach (var rootGroup in rows["source_root"].GroupBy(r => Scope(r, "snapshot_id")))
            if (rootGroup.Select(r => Text(r, "logical_name")).Distinct(StringComparer.Ordinal).Count() != rootGroup.Count()) Add("duplicate_logical_root", "source_root", null, "Logical root names must be unique within a snapshot.");
        foreach (var file in rows["source_file"])
        {
            var path = Text(file, "relative_path")!;
            if (!ValidRelativePath(path)) Add("invalid_relative_path", "source_file", Text(file, "id"), "File path is not a normalized relative logical path.");
            if (Text(file, "name") != path.Split('/')[^1] || Text(file, "extension") != Path.GetExtension(path)) Add("file_name", "source_file", Text(file, "id"), "File name/extension differs from its path.");
        }
        if (OperatingSystem.IsWindows())
            foreach (var group in rows["source_file"].GroupBy(r => Text(r, "root_id")))
                if (group.Select(r => Text(r, "relative_path")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Count()) Add("windows_path_collision", "source_file", null, "Case-colliding file identities require one file plus aliases.");
        foreach (var version in rows["file_version"])
        {
            var blob = rows["source_blob"].SingleOrDefault(b => Equals(b["hash"], version["content_hash"]));
            if (Number(version, "available") == 1 && (blob is null || !Equals(blob["byte_length"], version["byte_length"]))) Add("file_blob_length", "file_version", Text(version, "id"), "Available version does not match its blob length.");
            if (Text(version, "encoding") is { } encoding && encoding is not ("utf-8" or "utf-16le" or "utf-16be")) Add("unsupported_encoding_claim", "file_version", Text(version, "id"), "Unknown encodings must use explicit knowledge state rather than a supported claim.");
            if (Text(version, "encoding") is "utf-16le" or "utf-16be" && version["bom_bytes"] is not null && Number(version, "bom_bytes") != 2) Add("encoding_bom", "file_version", Text(version, "id"), "UTF-16 pilot text requires its two-byte BOM.");
            if (Text(version, "encoding") == "utf-8" && version["bom_bytes"] is not null && Number(version, "bom_bytes") is not (0 or 3)) Add("encoding_bom", "file_version", Text(version, "id"), "UTF-8 BOM length must be zero or three.");
        }
        foreach (var span in rows["source_span"])
        {
            var version = rows["file_version"].SingleOrDefault(v => Equals(v["id"], span["file_version_id"]));
            if (version is null || Number(version, "available") != 1 || Number(span, "end_byte") > Number(version, "byte_length")) Add("span_out_of_bounds", "source_span", Text(span, "id"), "Span must be inside an available immutable file version.");
        }
    }

    private void ValidateOrdering()
    {
        foreach (var collection in collections.Where(c => SupportedTables.Contains(c.GetProperty("table").GetString()!)))
        {
            var order = collection.GetProperty("ordering_contract");
            if (order.GetProperty("kind").GetString() != "ordered") continue;
            var table = collection.GetProperty("table").GetString()!;
            var partitions = order.GetProperty("partition_columns").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var index = order.GetProperty("index_column").GetString()!;
            foreach (var group in rows[table].GroupBy(r => Base64Key(partitions.Select(p => r[p]).ToArray())))
                if (!group.Select(r => Number(r, index)).Order().SequenceEqual(Enumerable.Range(0, group.Count()).Select(n => (long)n))) Add("noncontiguous_order", table, null, "Ordered children must be zero-based and contiguous in their complete partition.");
        }
    }

    private void ValidateIdentityGraph()
    {
        var graph = identities.ToDictionary(x => x.Key, x => IdentityReferences(x.Value.GetProperty("components")).ToArray(), StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal); var done = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (done.Contains(id)) return;
            if (!active.Add(id)) { Add("identity_cycle", "record_extension", id, "Identity-input dependencies contain a cycle."); return; }
            foreach (var target in graph[id])
            {
                if (!graph.ContainsKey(target)) Add("identity_reference_missing", "record_extension", id, "Identity preimage reference is absent: " + target);
                else Visit(target);
            }
            active.Remove(id); done.Add(id);
        }
        foreach (var id in graph.Keys) Visit(id);
    }
    private static IEnumerable<string> IdentityReferences(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.EndsWith("_id", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.String) yield return property.Value.GetString()!;
                else foreach (var reference in IdentityReferences(property.Value)) yield return reference;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) foreach (var reference in IdentityReferences(item)) yield return reference;
    }
    private string? Owner(string table, IReadOnlyDictionary<string, object?> row)
    {
        if (table == "record" || store.Schema.PrimaryTables.Contains(table)) return Text(row, "id");
        if (!tableContracts[table].GetProperty("field_state_owner").TryGetProperty("column", out var column)) return null;
        return row.TryGetValue(column.GetString()!, out var value) ? value as string : null;
    }
    private string? CollectionOwner(JsonElement collection, IReadOnlyDictionary<string, object?> row)
    {
        var owner = collection.GetProperty("owner_column").GetString()!;
        return owner == "record.snapshot_id" ? Scope(row, "snapshot_id") : Text(row, owner);
    }
    private bool CollectionAcceptsOwner(JsonElement collection, string owner)
    {
        var table = collection.GetProperty("table").GetString()!; var column = collection.GetProperty("owner_column").GetString()!;
        if (!payload.TryGetValue(owner, out var target)) return false;
        if (column == "record.snapshot_id") return target.Table == "snapshot";
        var reference = store.Schema.Tables[table].ForeignKeys.FirstOrDefault(x => x.Column == column);
        return reference is not null && (reference.Table == "record" || reference.Table == target.Table);
    }
    private string? Scope(IReadOnlyDictionary<string, object?> row, string column) => registry.TryGetValue(Text(row, "id")!, out var record) ? Text(record, column) : null;
    private string? RootName(IReadOnlyDictionary<string, object?> row) => payload.TryGetValue(Text(row, "root_id") ?? "", out var root) ? Text(root.Row, "logical_name") : null;
    private static bool ValidRelativePath(string path) => path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') && path.Split('/').All(x => x is not ("" or "." or ".."));
    private static void ValidateKnowledgeValue(JsonElement state, object? actual)
    {
        if (state.GetProperty("state").GetString() == "known") Require(actual is not null && state.GetProperty("value").ValueKind != JsonValueKind.Null
            && state.GetProperty("reason").ValueKind == JsonValueKind.Null && JsonSame(state.GetProperty("value"), actual), "Known descriptor value differs or is absent.");
        else Require(actual is null && state.GetProperty("value").ValueKind == JsonValueKind.Null && state.GetProperty("reason").ValueKind == JsonValueKind.String, "Unavailable descriptor state is unexplained or contradicts a value.");
    }
    private static void ValidateProvider(JsonElement descriptor, IReadOnlyDictionary<string, object?> dependency)
    {
        var kind = descriptor.GetProperty("kind").GetString();
        if (kind == "header_library") kind = "header";
        Require(Text(dependency, "provider_kind") == kind && JsonSame(descriptor.GetProperty("logical_name"), dependency["selected_name"]), "Provider kind/name differs from dependency.");
        ValidateKnowledgeValue(descriptor.GetProperty("version"), dependency["selected_version"]);
        ValidateKnowledgeValue(descriptor.GetProperty("content_fingerprint"), dependency["content_hash"]);
        Require(Text(dependency, "selected_path") == "<" + descriptor.GetProperty("root_key").GetString() + ">/" + descriptor.GetProperty("relative_location").GetString(), "Provider location differs from normalized descriptor.");
    }
    private static bool JsonSame(JsonElement expected, object? value) => CanonicalJson.Serialize(expected).SequenceEqual(CanonicalJson.Serialize(value));
    private static string Base64Key(object key) => Convert.ToBase64String(CanonicalJson.Serialize(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string? Text(IReadOnlyDictionary<string, object?> row, string column) => row.TryGetValue(column, out var value) ? value as string : null;
    private static long Number(IReadOnlyDictionary<string, object?> row, string column) => row.TryGetValue(column, out var value) && value is not null ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) : -1;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private void Add(string code, string table, string? id, string message) => issues.Add(new(code, table, id, message));
    private void Guard(string table, string? id, string code, Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or ContractException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { Add(code, table, id, error.Message); }
    }
}

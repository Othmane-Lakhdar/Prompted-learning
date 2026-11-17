using System.Text.Json;
using PromptedLearning.Core;

namespace PromptedLearning.Storage;

public static partial class SemanticImport
{
    private sealed partial class Importer
    {
        private void ImportUnit()
        {
            StableId.Verify(binding.UnitId, unitIdentityInput);
            Require(Text(unitIdentityInput, "domain") == "compilation_unit/1", "Expected a compilation unit identity envelope.");
            var components = unitIdentityInput.GetProperty("components");
            Require(Text(components, "context_id") == binding.ContextId && Text(components, "language") == binding.Language
                && Text(components, "output_identity") == binding.OutputIdentity, "Unit identity scope differs from accepted facts.");
            var inputs = components.GetProperty("inputs").EnumerateArray().ToArray();
            var files = inputs.Where(input => Text(input, "file_version_id") is not null).ToArray();
            var expectedVersions = binding.ExpectedConsumedFileKeys.Select(key => binding.Files.Single(file => file.Key == key).FileVersionId).ToHashSet(StringComparer.Ordinal);
            Require(files.Select(file => Text(file, "file_version_id")!).ToHashSet(StringComparer.Ordinal).SetEquals(expectedVersions)
                && files.Select(file => Text(file, "file_version_id")).Distinct().Count() == files.Length,
                "Final unit identity does not match actual consumed source versions.");
            var main = binding.Language == "cpp" ? Text(One(files.Where(file => Text(file, "role") == "main"), "Native final unit requires exactly one main source."), "file_version_id") : null;
            Require(binding.Language == "cpp" || files.All(file => Text(file, "role") is "source" or "generated"), "Managed unit has a native input role.");
            var prior = write.ReadRows("compilation_unit").SingleOrDefault(row => Text(row, "id") == binding.UnitId);
            if (prior is null)
                Require(Primary("compilation_unit", components, Row(("context_id", binding.ContextId), ("language", binding.Language),
                    ("main_file_version_id", main), ("output_identity", binding.OutputIdentity),
                    ("effective_fingerprint", CanonicalJson.Sha256(CanonicalJson.Serialize(components))), ("status", result.Status)),
                    nullStates: Inapplicable("main_file_version_id")) == binding.UnitId, "Unit identity changed while importing.");
            else
            {
                var envelope = One(write.ReadRows("record_extension").Where(row => Text(row, "record_id") == binding.UnitId && Text(row, "schema_name") == "identity-input"), "Existing unit identity is missing.");
                using var document = CanonicalJson.Parse(System.Text.Encoding.UTF8.GetBytes(Text(envelope, "payload_json")!));
                Require(CanonicalJson.Serialize(document.RootElement).SequenceEqual(CanonicalJson.Serialize(unitIdentityInput)), "Existing unit identity differs from final input closure.");
                Require(Text(prior, "status") is "not_attempted" || Text(prior, "status") == result.Status, "Conflicting unit compiler outcome.");
                Transition("compilation_unit", prior, Row(("status", result.Status)));
                Insert("record_evidence", Row(("record_id", binding.UnitId), ("evidence_id", compilerEvidence), ("role", "compiler_consumed_inputs")));
            }
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var input in inputs)
            {
                var role = Text(input, "role")!; var ordinal = ordinals.GetValueOrDefault(role); ordinals[role] = ordinal + 1;
                string? dependency = null;
                if (input.GetProperty("provider").ValueKind != JsonValueKind.Null)
                {
                    var primitive = input.GetProperty("provider");
                    dependency = Text(One(write.ReadRows("dependency").Where(row => Text(row, "context_id") == binding.ContextId
                        && Text(row, "selected_name") == Text(primitive, "logical_name")
                        && Text(row, "content_hash") == Text(primitive.GetProperty("content_fingerprint"), "value")), "Unit provider dependency is missing or ambiguous."), "id");
                }
                var old = write.ReadRows("unit_input").SingleOrDefault(row => Text(row, "unit_id") == binding.UnitId && Text(row, "role") == role && Number(row, "ordinal") == ordinal);
                if (old is null) Insert("unit_input", Row(("unit_id", binding.UnitId), ("role", role), ("ordinal", ordinal),
                    ("file_version_id", Text(input, "file_version_id")), ("dependency_id", dependency), ("observed_consumed", true), ("evidence_id", compilerEvidence)));
                else
                {
                    Require(Text(old, "file_version_id") == Text(input, "file_version_id") && Text(old, "dependency_id") == dependency, "Existing unit input contradicts final declared set.");
                    Require(old["observed_consumed"] is null || Number(old, "observed_consumed") == 1, "Conflicting input consumption observation.");
                    Transition("unit_input", old, Row(("observed_consumed", 1L)));
                    var path = "/unit_input/" + Key([binding.UnitId, role, ordinal]) + "/observed_consumed";
                    var oldState = write.ReadRows("field_state").SingleOrDefault(row => Text(row, "record_id") == binding.UnitId && Text(row, "field_path") == path);
                    if (oldState is not null) Transition("field_state", oldState, Row(("state", "known"), ("reason", null), ("evidence_id", compilerEvidence)));
                }
                Collection(binding.UnitId, "unit_input", "unit_input:" + role, "known");
            }
            var contextState = write.ReadRows("field_state").SingleOrDefault(row => Text(row, "record_id") == binding.ContextId && Text(row, "field_path") == "/compilation_unit/@collection/compilation_units");
            if (contextState is not null && Text(contextState, "state") != "known")
                Transition("field_state", contextState, Row(("state", "known"), ("reason", null), ("evidence_id", compilerEvidence)));
        }

        private string Span(string key)
        {
            if (ids.TryGetValue(key, out var found)) return found;
            var fact = Fact(key);
            object Frame(JsonElement frame) => new { file_version_id = binding.Files.Single(file => file.Key == Text(frame, "file_key")).FileVersionId,
                start_byte = frame.GetProperty("start_byte").GetInt64(), end_byte = frame.GetProperty("end_byte").GetInt64(), origin_kind = Text(frame, "origin_kind") };
            var components = CompilerFacts.Element(new { file_version_id = binding.Files.Single(file => file.Key == Text(fact, "file_key")).FileVersionId,
                start_byte = fact.GetProperty("start_byte").GetInt64(), end_byte = fact.GetProperty("end_byte").GetInt64(), origin_kind = Text(fact, "origin_kind"),
                origin_stack = fact.GetProperty("origin_stack").EnumerateArray().Select(Frame).ToArray() });
            var cause = Text(fact, "cause_span_key") is { } causeKey ? Span(causeKey) : null;
            var id = Primary("source_span", components, Row(("file_version_id", Text(components, "file_version_id")),
                ("start_byte", fact.GetProperty("start_byte").GetInt64()), ("end_byte", fact.GetProperty("end_byte").GetInt64()),
                ("origin_kind", Text(fact, "origin_kind")), ("cause_span_id", cause)), independent: true, nullStates: Inapplicable("cause_span_id"));
            ids[key] = id; return id;
        }

        private string EntityId(string key)
        {
            if (ids.TryGetValue(key, out var found)) return found;
            var fact = Fact(key); var identityKind = Text(fact, "compiler_identity_kind");
            var owner = Text(fact, "owner_entity_key") is { } ownerKey ? EntityId(ownerKey) : null;
            var nativeLocal = binding.Language == "cpp" && Text(fact, "linkage") is "internal" or "no_linkage";
            object? origin = null;
            if (identityKind == "source_local")
            {
                var span = Fact(Text(fact, "origin_span_key")!);
                origin = new { file_version_id = binding.Files.Single(file => file.Key == Text(span, "file_key")).FileVersionId,
                    start_byte = span.GetProperty("start_byte").GetInt64(), end_byte = span.GetProperty("end_byte").GetInt64(), origin_kind = Text(span, "origin_kind") };
            }
            var components = CompilerFacts.Element(new { language = binding.Language, context_id = binding.ContextId, output_identity = binding.OutputIdentity,
                identity_kind = identityKind, compiler_key = Text(fact, "compiler_key"), owner_entity_id = owner,
                declared_ordinal = fact.GetProperty("declared_ordinal"), origin,
                linkage_domain = binding.Language == "csharp" ? "assembly:" + binding.OutputIdentity : nativeLocal ? "unit:" + binding.UnitId : "external",
                identity_unit_id = nativeLocal ? binding.UnitId : null,
                compiler_signature_key = fact.GetProperty("compiler_signature_key"), compiler_type_key = fact.GetProperty("compiler_type_key") });
            var id = StableId.Create("entity/1", components); ids[key] = id; identityComponents[id] = components; return id;
        }

        private void ImportEntities()
        {
            foreach (var fact in Facts("entities"))
            {
                var key = Text(fact, "key")!; var id = ids[key];
                var primitive = fact.GetProperty("compiler_type_key");
                if (primitive.ValueKind != JsonValueKind.Null) PrimitiveType(primitive, primitive.GetProperty("tag").GetString() == "named" ? id : null);
                var nativeLocal = binding.Language == "cpp" && Text(fact, "linkage") is "internal" or "no_linkage";
                var states = Inapplicable("linkage", "access", "containing_entity_id", "compiler_identity", "identity_unit_id", "availability_condition");
                Require(Primary("entity", identityComponents[id], Row(("language", binding.Language), ("entity_kind", Text(fact, "entity_kind")),
                    ("written_name", Text(fact, "written_name")), ("qualified_name", Text(fact, "qualified_name")), ("compiler_identity", Text(fact, "compiler_key")),
                    ("identity_scheme", "pl-entity-" + Text(fact, "compiler_identity_kind")), ("identity_version", "1"),
                    ("containing_entity_id", Ref(fact, "owner_entity_key")), ("identity_unit_id", binding.Language == "csharp" || nativeLocal ? binding.UnitId : null),
                    ("origin", Text(fact, "origin")), ("access", Text(fact, "access")), ("linkage", Text(fact, "linkage")), ("resolution", Text(fact, "resolution"))),
                    Ref(fact, "origin_span_key"), nullStates: states) == id, "Entity identity drifted.");
            }
            foreach (var modifier in Facts("entity_modifiers")) Insert("entity_modifier", Row(("entity_id", Ref(modifier, "entity_key")),
                ("modifier", Text(modifier, "modifier")), ("ordinal", modifier.GetProperty("ordinal").GetInt64())));
        }
    }
}

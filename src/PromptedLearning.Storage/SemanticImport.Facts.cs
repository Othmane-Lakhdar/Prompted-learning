using System.Text;
using System.Text.Json;
using PromptedLearning.Core;

namespace PromptedLearning.Storage;

public static partial class SemanticImport
{
    private sealed partial class Importer
    {
        private void ImportAppearancesAndBodies()
        {
            foreach (var fact in Facts("appearances"))
            {
                var entity = Ref(fact, "entity_key"); var span = Ref(fact, "span_key"); var kind = Text(fact, "kind");
                var id = Primary("appearance", Generic("appearance", [entity, span, binding.UnitId, kind]), Row(("entity_id", entity),
                    ("span_id", span), ("unit_id", binding.UnitId), ("appearance_kind", kind), ("active", true)), span,
                    nullStates: Inapplicable("surrounding_id", "spelling_span_id", "expansion_span_id"));
                ids[Text(fact, "key")!] = id;
                var unitRole = kind switch { "definition" => "definition", "declaration" => "declaration", "reference" => "use", _ => throw new InvalidDataException("Unsupported pilot appearance kind.") };
                Insert("entity_unit", Row(("entity_id", entity), ("unit_id", binding.UnitId), ("role", unitRole)));
            }
            foreach (var fact in Facts("bodies"))
            {
                var owner = Ref(fact, "owner_entity_key"); var span = Ref(fact, "span_key");
                var components = new { owner_entity_id = owner, unit_id = binding.UnitId, span_id = span,
                    representation = Text(fact, "representation"), body_kind = Text(fact, "kind"), body_role = "source_body" };
                ids[Text(fact, "key")!] = Primary("body", components, Row(("owner_entity_id", owner), ("unit_id", binding.UnitId),
                    ("body_kind", Text(fact, "kind")), ("span_id", span), ("representation", Text(fact, "representation")), ("status", Text(fact, "status"))), span);
                Coverage(ids[Text(fact, "key")!], "operations", span, Text(fact, "status")!, Text(fact, "reason"));
            }
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var fact in Facts("entities"))
            {
                var owner = Ref(fact, "owner_entity_key"); if (owner is null || Text(fact, "entity_kind") == "local_variable") continue;
                var role = Text(fact, "entity_kind") == "parameter" ? "parameter" : "declared_member";
                var partition = owner + "/" + role; var ordinal = ordinals.GetValueOrDefault(partition); ordinals[partition] = ordinal + 1;
                Insert("member_relation", Row(("owner_id", owner), ("member_id", ids[Text(fact, "key")!]), ("role", role), ("ordinal", ordinal), ("evidence_id", compilerEvidence)));
            }
        }

        private void ImportOperations()
        {
            Require(!Facts("external_symbols").Any() && !Facts("unresolved_references").Any(), "The exact M1B pilots must not contain unsupported external/unresolved shapes.");
            foreach (var fact in Facts("operations"))
            {
                var body = Ref(fact, "body_key"); var span = Ref(fact, "span_key"); var cause = Ref(fact, "cause_span_key");
                var components = new { body_id = body, representation = Text(fact, "representation"), structural_path = fact.GetProperty("structural_path"),
                    compiler_kind = Text(fact, "compiler_kind"), span_id = span, cause_span_id = cause };
                var constant = fact.GetProperty("constant");
                ids[Text(fact, "key")!] = Primary("operation", components, Row(("body_id", body), ("representation", Text(fact, "representation")),
                    ("operation_kind", Text(fact, "operation_kind")), ("compiler_kind", Text(fact, "compiler_kind")), ("span_id", span), ("cause_span_id", cause),
                    ("implicit", fact.GetProperty("implicit").GetBoolean()), ("semantic_role", Text(fact, "semantic_role")), ("execution_point", Text(fact, "execution_point")),
                    ("type_id", Ref(fact, "type_key")), ("referenced_entity_id", Ref(fact, "referenced_entity_key")), ("operator_kind", Text(fact, "operator_kind")),
                    ("constant_kind", constant.ValueKind == JsonValueKind.Null ? null : Text(constant, "kind")),
                    ("constant_value", constant.ValueKind == JsonValueKind.Null ? null : Text(constant, "value")),
                    ("supported", fact.GetProperty("supported").GetBoolean()), ("unsupported_reason", Text(fact, "unsupported_reason"))), span,
                    nullStates: Inapplicable("cause_span_id", "type_id", "referenced_entity_id", "operator_kind", "constant_kind", "constant_value", "unsupported_reason"));
            }
            foreach (var fact in Facts("operations"))
            {
                var id = ids[Text(fact, "key")!];
                if (Ref(fact, "parent_operation_key") is { } parent)
                    Insert("operation_child", Row(("parent_id", parent), ("child_id", id), ("ordinal", fact.GetProperty("child_ordinal").GetInt64()), ("role", Text(fact, "child_role"))));
                foreach (var operand in fact.GetProperty("operands").EnumerateArray())
                    InsertWithStates("operation_operand", Row(("operation_id", id), ("role", Text(operand, "role")),
                        ("ordinal", operand.GetProperty("ordinal").GetInt64()), ("target_id", Ref(operand, "target_key")),
                        ("parameter_id", Ref(operand, "parameter_entity_key"))), id, Inapplicable("parameter_id"));
                var body = Fact(Text(fact, "body_key")!); var complete = Text(body, "status") == "complete";
                Collection(id, "operation_child", "operation_child", complete ? "known" : "unknown", complete ? null : Text(body, "reason"));
                Collection(id, "operation_operand", "operation_operand", complete ? "known" : "unknown", complete ? null : Text(body, "reason"));
            }
        }

        private void ImportAnnotationsAndPreprocessing()
        {
            var annotationOrdinals = new Dictionary<string, int>();
            foreach (var fact in Facts("annotations"))
            {
                Require(binding.Language == "cpp" && Text(fact, "attribute_type_key") is null, "Only the native pilot's builtin annotation is supported at M1B.");
                var owner = Ref(fact, "owner_entity_key")!; var ordinal = annotationOrdinals.GetValueOrDefault(owner); annotationOrdinals[owner] = ordinal + 1;
                var span = Ref(fact, "span_key");
                var id = Primary("annotation", Generic("annotation", [owner, span, Text(fact, "written_name"), ordinal]),
                    Row(("owner_id", owner), ("span_id", span), ("resolution", Text(fact, "resolution"))), span,
                    nullStates: new Dictionary<string, (string State, string Reason)> { ["attribute_type_id"] = ("not_applicable", "builtin_attribute_has_no_type") });
                ids[Text(fact, "key")!] = id;
                var extension = CompilerFacts.Element(new { schema = "cpp-attribute/1", written_name = Text(fact, "written_name"),
                    @namespace = Text(fact, "namespace"), syntax = Text(fact, "syntax") });
                ContractCatalog.Default.Validate("cpp-attribute", extension);
                Insert("record_extension", Row(("record_id", id), ("schema_name", "cpp-attribute"), ("schema_version", 1L), ("payload_json", Encoding.UTF8.GetString(CanonicalJson.Serialize(extension)))));
                foreach (var argument in fact.GetProperty("arguments").EnumerateArray())
                {
                    var constant = argument.GetProperty("constant");
                    InsertWithStates("annotation_argument", Row(("annotation_id", id), ("ordinal", argument.GetProperty("ordinal").GetInt64()),
                        ("name", Text(argument, "name")), ("expression_id", Ref(argument, "expression_key")),
                        ("constant_kind", constant.ValueKind == JsonValueKind.Null ? null : Text(constant, "kind")),
                        ("constant_value", constant.ValueKind == JsonValueKind.Null ? null : Text(constant, "value"))), id,
                        Inapplicable("name", "expression_id", "constant_kind", "constant_value"));
                }
                Collection(id, "annotation_argument", "annotation_argument", "known");
            }
            var eventOrdinals = new Dictionary<string, int>();
            foreach (var fact in Facts("preprocessing"))
            {
                var file = Text(fact, "file_key")!; var ordinal = eventOrdinals.GetValueOrDefault(file); eventOrdinals[file] = ordinal + 1;
                var span = Ref(fact, "span_key");
                ids[Text(fact, "key")!] = Primary("preprocessing", Generic("preprocessing", [Text(fact, "event_kind"), span, Text(fact, "written_name"), ordinal]),
                    Row(("event_kind", Text(fact, "event_kind")), ("span_id", span), ("name", Text(fact, "written_name")),
                        ("expression_text", Text(fact, "expression_text")), ("selected", fact.GetProperty("selected").ValueKind == JsonValueKind.Null ? null : fact.GetProperty("selected").GetBoolean()),
                        ("resolved_target_id", Ref(fact, "resolved_file_key"))), span,
                    nullStates: Inapplicable("expression_text", "resolved_target_id", "definition_id", "spelling_span_id", "expansion_span_id", "generation_id", "origin_setting_id"));
            }
        }

        private void ImportCoverageAndDiagnostics()
        {
            foreach (var fact in Facts("coverage"))
            {
                var subject = Ref(fact, "file_key"); var feature = Text(fact, "feature");
                Coverage(subject!, feature!, null, Text(fact, "status")!, Text(fact, "reason"));
                foreach (var missing in fact.GetProperty("unparsed_span_keys").EnumerateArray())
                    Coverage(subject!, feature!, ids[missing.GetString()!], Text(fact, "status")!, Text(fact, "reason"));
                Collection(subject!, "coverage", "coverage:" + feature, "known");
            }
            foreach (var fact in Facts("diagnostics"))
            {
                var subject = Ref(fact, "subject_key"); var span = Ref(fact, "span_key"); var message = Text(fact, "message");
                Primary("diagnostic", Generic("diagnostic", [producerId, subject, Text(fact, "severity"), Text(fact, "code"),
                    CanonicalJson.Sha256(CanonicalJson.Serialize(message)), span, null]), Row(("producer_id", producerId), ("subject_id", subject),
                    ("severity", Text(fact, "severity")), ("code", Text(fact, "code")), ("message", message), ("span_id", span)), span, nullStates: Inapplicable("cause_id"));
            }
        }

        private void Coverage(string subject, string feature, string? span, string status, string? reason)
        {
            var states = Inapplicable("span_id", "reason");
            if (status != "complete") states["unresolved_count"] = ("unknown", reason ?? "producer_incomplete");
            Primary("coverage", Generic("coverage", [producerId, subject, feature, span]), Row(("producer_id", producerId), ("subject_id", subject),
                ("feature", feature), ("span_id", span), ("status", status), ("reason", reason), ("unresolved_count", status == "complete" ? 0L : null)), span,
                nullStates: states);
        }

        private void CompleteCollections()
        {
            foreach (var fact in Facts("entities"))
            {
                var id = ids[Text(fact, "key")!];
                foreach (var (table, role) in new[] { ("entity_unit", "entity_unit"), ("entity_modifier", "entity_modifier"),
                    ("appearance", "appearances:" + Uri.EscapeDataString(binding.UnitId)), ("signature", "signatures"),
                    ("body", "bodies:" + Uri.EscapeDataString(binding.UnitId) + ":semantic"), ("annotation", "annotations"),
                    ("member_relation", "member_relation:declared_member"), ("member_relation", "member_relation:parameter") })
                    Collection(id, table, role, result.Status == "complete" ? "known" : "unknown", result.Status == "complete" ? null : result.StopReason);
                foreach (var (table, role) in new[] { ("documentation", "documentation"), ("relationship", "outgoing_relationships"),
                    ("relationship", "incoming_relationships"), ("call_site", "calls") }) Collection(id, table, role, "not_collected", "m1b_producer_not_requested");
            }
            foreach (var body in Facts("bodies")) Collection(ids[Text(body, "key")!], "operation", "operations:semantic",
                Text(body, "status") == "complete" ? "known" : "unknown", Text(body, "status") == "complete" ? null : Text(body, "reason"));
        }
    }
}

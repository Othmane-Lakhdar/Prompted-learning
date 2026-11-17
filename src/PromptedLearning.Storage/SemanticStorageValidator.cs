using System.Text;
using System.Text.Json;
using PromptedLearning.Core;

namespace PromptedLearning.Storage;

internal sealed partial class FoundationValidator
{
    private bool ValidateSemanticIdentity(string id, string table, IReadOnlyDictionary<string, object?> row, JsonElement components)
    {
        void Same(string component, string column) => Require(JsonSame(components.GetProperty(component), row[column]), "Semantic identity differs from " + table + "." + column);
        switch (table)
        {
            case "entity":
                Same("language", "language"); Same("compiler_key", "compiler_identity"); Same("owner_entity_id", "containing_entity_id");
                Require(Text(row, "identity_scheme") == "pl-entity-" + components.GetProperty("identity_kind").GetString() && Text(row, "identity_version") == "1", "Entity identity scheme differs.");
                var context = payload[Scope(row, "context_id")!].Row;
                Require(JsonSame(components.GetProperty("output_identity"), context["output_identity"]), "Entity output identity differs.");
                if (Text(row, "language") == "csharp") Require(components.GetProperty("identity_unit_id").ValueKind == JsonValueKind.Null && row["identity_unit_id"] is string,
                    "C# unit is provenance, not a named entity identity dependency.");
                else Require(JsonSame(components.GetProperty("identity_unit_id"), row["identity_unit_id"]), "Native linkage unit differs.");
                if (components.GetProperty("identity_kind").GetString() == "source_local")
                {
                    var span = payload[Text(registry[id], "origin_span_id")!].Row;
                    foreach (var field in new[] { "file_version_id", "start_byte", "end_byte", "origin_kind" })
                        Require(JsonSame(components.GetProperty("origin").GetProperty(field), span[field]), "Local entity origin differs.");
                }
                return true;
            case "type_node":
                var primitive = components.GetProperty("compiler_type_key"); var tag = primitive.GetProperty("tag").GetString();
                Require(Text(row, "type_kind") == tag, "Type kind differs from primitive identity.");
                if (tag == "builtin") Require(JsonSame(primitive.GetProperty("name"), row["builtin_name"]), "Builtin type name differs.");
                if (tag == "pointer") Require(SamePrimitive(Text(row, "element_type_id"), primitive.GetProperty("element")), "Pointer element differs.");
                if (tag == "named")
                {
                    Require(row["named_entity_id"] is string named && payload.TryGetValue(named, out var namedEntity) && namedEntity.Table == "entity"
                        && JsonSame(primitive.GetProperty("compiler_key"), namedEntity.Row["compiler_identity"]), "Named type entity differs.");
                }
                if (tag == "function")
                {
                    Require(row["function_signature_id"] is string function && identities.TryGetValue(function, out var signature), "Function type has no structured signature.");
                    var signatureComponents = identities[Text(row, "function_signature_id")!].GetProperty("components");
                    Require(signatureComponents.TryGetProperty("function_type_id", out var owner) && owner.GetString() == id
                        && SameJson(signatureComponents.GetProperty("compiler_signature_key"), primitive.GetProperty("signature")), "Function signature owner or primitive differs.");
                }
                else Require(row["function_signature_id"] is null, "Nonfunction type has function signature.");
                return true;
            case "signature":
                var typeOwned = components.TryGetProperty("function_type_id", out var functionType);
                var callable = typeOwned ? functionType.GetString()! : components.GetProperty("callable_entity_id").GetString()!;
                Require(Text(row, "callable_id") == callable && payload.TryGetValue(callable, out var ownerPayload), "Signature owner differs.");
                var signaturePrimitive = components.GetProperty("compiler_signature_key");
                if (typeOwned)
                {
                    Require(payload[callable].Table == "type_node" && Text(payload[callable].Row, "type_kind") == "function" && Text(row, "callable_kind") == "function_type"
                        && Text(row, "body_state") == "absent" && Text(payload[callable].Row, "function_signature_id") == id, "Invalid type-owned signature backlink or body.");
                    Require(SameJson(identities[callable].GetProperty("components").GetProperty("compiler_type_key").GetProperty("signature"), signaturePrimitive), "Type-owned signature primitive differs.");
                }
                else
                {
                    Require(payload[callable].Table == "entity" && SameJson(identities[callable].GetProperty("components").GetProperty("compiler_signature_key"), signaturePrimitive), "Callable signature collision check differs.");
                    Require(Text(row, "callable_kind") == signaturePrimitive.GetProperty("kind").GetString(), "Callable kind differs.");
                }
                Require(SamePrimitive(Text(row, "return_type_id"), signaturePrimitive.GetProperty("return_type")), "Signature return type differs.");
                foreach (var field in new[] { "return_passing", "calling_convention", "language_linkage" })
                    Require(JsonSame(signaturePrimitive.GetProperty(field), row[field]), "Signature property differs: " + field);
                var expectedParameters = signaturePrimitive.GetProperty("parameters").EnumerateArray().ToArray();
                var actualParameters = rows["parameter"].Where(parameter => Text(parameter, "signature_id") == id).OrderBy(parameter => Number(parameter, "ordinal")).ToArray();
                Require(expectedParameters.Length == actualParameters.Length, "Signature parameter count differs.");
                foreach (var pair in expectedParameters.Zip(actualParameters))
                {
                    Require(SamePrimitive(Text(pair.Second, "type_id"), pair.First.GetProperty("type")) && JsonSame(pair.First.GetProperty("passing"), pair.Second["passing_mode"]), "Parameter type/passing differs.");
                    if (typeOwned) Require(pair.Second["entity_id"] is null && pair.Second["name"] is null && pair.Second["default_value"] is null
                        && pair.Second["default_expression_id"] is null && Number(pair.Second, "optional") == 0, "Type-owned signature fabricates parameter declaration facts.");
                }
                var qualifiers = rows["signature_qualifier"].Where(qualifier => Text(qualifier, "signature_id") == id).OrderBy(qualifier => Number(qualifier, "ordinal")).Select(qualifier => Text(qualifier, "qualifier"));
                Require(qualifiers.SequenceEqual(signaturePrimitive.GetProperty("qualifiers").EnumerateArray().Select(qualifier => qualifier.GetString())), "Signature qualifiers differ from ordered primitive qualifiers.");
                return true;
            case "parameter": Same("signature_id", "signature_id"); Same("ordinal", "ordinal"); return true;
            case "body":
                foreach (var field in new[] { "owner_entity_id", "unit_id", "span_id", "representation", "body_kind" }) Same(field, field);
                Require(components.GetProperty("body_role").GetString() == "source_body", "Unregistered M1B body role."); return true;
            case "operation":
                foreach (var field in new[] { "body_id", "representation", "compiler_kind", "span_id", "cause_span_id" }) Same(field, field);
                var path = components.GetProperty("structural_path").EnumerateArray().Select(value => value.GetInt64()).ToArray();
                Require(path.Length > 0 && path[0] == 0, "Operation structural path has no root.");
                var parents = rows["operation_child"].Where(child => Text(child, "child_id") == id).ToArray();
                if (path.Length == 1) Require(parents.Length == 0, "Root operation has a structural parent.");
                else
                {
                    Require(parents.Length == 1 && Number(parents[0], "ordinal") == path[^1], "Operation parent/ordinal differs from structural path.");
                    var parent = Text(parents[0], "parent_id")!;
                    Require(Text(payload[parent].Row, "body_id") == Text(row, "body_id")
                        && identities[parent].GetProperty("components").GetProperty("structural_path").EnumerateArray().Select(value => value.GetInt64()).SequenceEqual(path[..^1]), "Operation path crosses or skips its body tree.");
                }
                return true;
            default: return false;
        }
    }

    private bool SamePrimitive(string? typeId, JsonElement primitive) => typeId is not null && identities.TryGetValue(typeId, out var identity)
        && identity.GetProperty("domain").GetString() == "type_node/1" && SameJson(identity.GetProperty("components").GetProperty("compiler_type_key"), primitive);
    private static bool SameJson(JsonElement left, JsonElement right) => CanonicalJson.Serialize(left).SequenceEqual(CanonicalJson.Serialize(right));

    private void ValidateSemanticRows()
    {
        foreach (var entity in rows["entity"]) Guard("entity", Text(entity, "id"), "entity_semantics", () =>
        {
            var id = Text(entity, "id")!; var kind = Text(entity, "entity_kind");
            Require(Text(entity, "language") != "csharp" || entity["linkage"] is null, "C# linkage is inapplicable.");
            if (kind is "namespace" or "parameter" or "local_variable") Require(entity["access"] is null, "Entity access is inapplicable.");
            Require(rows["entity_detail"].Count(detail => Text(detail, "entity_id") == id) == 1, "Entity lacks its detail record.");
            var detail = rows["entity_detail"].Single(detail => Text(detail, "entity_id") == id);
            var primitive = identities[id].GetProperty("components");
            if (primitive.GetProperty("compiler_type_key").ValueKind != JsonValueKind.Null)
                Require(SamePrimitive(Text(detail, "type_id"), primitive.GetProperty("compiler_type_key")), "Entity detail type differs from compiler identity.");
            if (primitive.GetProperty("identity_kind").GetString() == "parameter")
                Require(JsonSame(primitive.GetProperty("declared_ordinal"), detail["parameter_position"]), "Parameter entity position differs.");
            if (primitive.GetProperty("compiler_signature_key").ValueKind != JsonValueKind.Null)
                Require(detail["signature_id"] is string, "Callable entity lacks its required structured signature.");
            if (detail["signature_id"] is string signature)
                Require(payload.TryGetValue(signature, out var signatureRow) && signatureRow.Table == "signature" && Text(signatureRow.Row, "callable_id") == id
                    && Text(signatureRow.Row, "callable_kind") != "function_type", "Entity detail signature belongs to another callable.");
        });
        foreach (var body in rows["body"]) Guard("body", Text(body, "id"), "body_tree", () =>
        {
            var operations = rows["operation"].Where(operation => Text(operation, "body_id") == Text(body, "id")).ToArray();
            if (Text(body, "status") == "complete") Require(operations.Count(operation => !rows["operation_child"].Any(child => Text(child, "child_id") == Text(operation, "id"))) == 1,
                "Complete body does not have one root.");
        });
        foreach (var operation in rows["operation"]) Guard("operation", Text(operation, "id"), "operation_semantics", () =>
        {
            if (Text(operation, "operation_kind") == "reference") Require(Text(operation, "semantic_role") is "read" or "destination", "Reference lacks read/destination role.");
            if (Text(operation, "operation_kind") == "invocation") Require(Text(operation, "semantic_role") == "call", "Invocation lacks call role.");
            if (Number(operation, "implicit") == 1) Require(operation["span_id"] is string span && Text(payload[span].Row, "origin_kind") == "implicit_cause"
                && Equals(payload[span].Row["cause_span_id"], operation["cause_span_id"]), "Implicit operation lacks matching cause.");
            if (operation["constant_kind"] is null) Require(operation["constant_value"] is null, "Untagged constant value.");
        });
        foreach (var operand in rows["operation_operand"]) Guard("operation_operand", Text(operand, "operation_id"), "operand_semantics", () =>
        {
            var operation = payload[Text(operand, "operation_id")!].Row;
            Require(payload.TryGetValue(Text(operand, "target_id")!, out var target) && target.Table == "operation"
                && Equals(target.Row["body_id"], operation["body_id"]), "Pilot operand target must be an operation in the same body.");
            if ((Text(operation, "operation_kind") == "invocation" && Text(operand, "role") == "argument") || Text(operation, "operation_kind") == "argument")
                Require(operand["parameter_id"] is string, "Direct pilot argument lacks its required declared parameter mapping.");
            if (operand["parameter_id"] is string parameter)
            {
                Require(payload.TryGetValue(parameter, out var entity) && entity.Table == "entity" && Text(entity.Row, "entity_kind") == "parameter", "Operand parameter mapping is not a parameter entity.");
                var call = operation;
                if (Text(operation, "operation_kind") == "argument")
                {
                    var parent = rows["operation_child"].SingleOrDefault(child => Text(child, "child_id") == Text(operation, "id"));
                    Require(parent is not null, "Argument mapping has no invocation parent.");
                    call = payload[Text(parent!, "parent_id")!].Row;
                }
                Require(Text(call, "operation_kind") == "invocation" && Text(payload[parameter].Row, "containing_entity_id") == Text(call, "referenced_entity_id"), "Argument maps to another callable's parameter.");
                var callOperand = ReferenceEquals(call, operation) ? operand : rows["operation_operand"].SingleOrDefault(candidate => Text(candidate, "operation_id") == Text(call, "id")
                    && Text(candidate, "target_id") == Text(operation, "id"));
                Require(callOperand is not null && Text(callOperand, "role") == "argument", "Mapped operation is not an invocation argument.");
                var detail = rows["entity_detail"].Single(entityDetail => Text(entityDetail, "entity_id") == parameter);
                Require(Number(detail, "parameter_position") == Number(callOperand!, "ordinal"), "Argument maps to the wrong declared parameter ordinal.");
            }
        });
        foreach (var input in rows["unit_input"].Where(input => Number(input, "observed_consumed") == 1))
            if (!rows["record_evidence"].Any(evidence => Text(evidence, "record_id") == Text(input, "unit_id") && Text(evidence, "role") is "compiler_consumed_inputs" or "compiler_source_semantics"))
                Add("consumption_evidence", "unit_input", Text(input, "unit_id"), "Observed compiler input has no compiler evidence.");
    }
}

using System.Text;
using System.Text.Json;
using PromptedLearning.Core;

namespace PromptedLearning.Storage;

public static partial class SemanticImport
{
    private sealed partial class Importer
    {
        private string PrimitiveType(JsonElement primitive, string? namedEntity = null)
        {
            var fingerprint = CanonicalJson.Sha256(CanonicalJson.Serialize(primitive));
            if (primitiveTypes.TryGetValue(fingerprint, out var existingType)) return existingType;
            var tag = Text(primitive, "tag")!;
            Require(tag is "builtin" or "named" or "pointer" or "function", "Type shape is outside the M1B pilot importer.");
            var components = CompilerFacts.Element(new { context_id = binding.ContextId, language = binding.Language, compiler_type_key = primitive });
            var id = StableId.Create("type_node/1", components);
            primitiveTypes.Add(fingerprint, id);
            var element = tag == "pointer" ? PrimitiveType(primitive.GetProperty("element")) : null;
            string? functionSignature = null;
            if (tag == "function")
            {
                var signatureKey = primitive.GetProperty("signature");
                var signatureComponents = CompilerFacts.Element(new { function_type_id = id, compiler_signature_key = signatureKey });
                functionSignature = StableId.Create("signature/1", signatureComponents);
                ImportSignature(functionSignature, signatureComponents, id, "function_type", signatureKey, null);
            }
            Require(Primary("type_node", components, Row(("type_kind", tag), ("named_entity_id", namedEntity), ("element_type_id", element),
                ("builtin_name", tag == "builtin" ? Text(primitive, "name") : null), ("nullable", "not_applicable"),
                ("resolution", "explicit"), ("function_signature_id", functionSignature)), nullStates:
                Inapplicable("named_entity_id", "element_type_id", "builtin_name", "rank", "bound_expression_id", "function_signature_id")) == id,
                "Type identity drifted.");
            Collection(id, "type_argument", "type_argument", "known"); Collection(id, "type_qualifier", "type_qualifier", "known");
            if (tag == "function") Collection(id, "signature", "signatures", "known");
            return id;
        }

        private void ImportSignatures()
        {
            foreach (var signature in Facts("signatures"))
            {
                var owner = Ref(signature, "owner_entity_key")!; var primitive = signature.GetProperty("compiler_signature_key");
                var components = CompilerFacts.Element(new { callable_entity_id = owner, compiler_signature_key = primitive });
                var id = StableId.Create("signature/1", components); ids[Text(signature, "key")!] = id;
                ImportSignature(id, components, owner, Text(primitive, "kind")!, primitive, signature);
            }
            foreach (var entity in Facts("entities"))
            {
                var id = ids[Text(entity, "key")!]; var type = entity.GetProperty("compiler_type_key");
                var signature = Facts("signatures").SingleOrDefault(signature => Text(signature, "owner_entity_key") == Text(entity, "key"));
                var states = Inapplicable("type_id", "signature_id", "initializer_id", "constant_kind", "constant_value", "bit_field_width", "underlying_type_id",
                    "operator_kind", "accessor_kind", "backing_field_id", "parameter_position", "parameter_kind", "default_type_id", "alias_written_target", "alias_target_id",
                    "original_definition_id", "layout_kind", "alignment_expression", "requirement_expression_id", "associated_statement_id", "mutable");
                InsertWithStates("entity_detail", Row(("entity_id", id), ("type_id", type.ValueKind == JsonValueKind.Null ? null : PrimitiveType(type)),
                    ("signature_id", signature.ValueKind == JsonValueKind.Undefined ? null : ids[Text(signature, "key")!]),
                    ("parameter_position", entity.GetProperty("declared_ordinal").ValueKind == JsonValueKind.Null ? null : entity.GetProperty("declared_ordinal").GetInt64()),
                    ("parameter_kind", Text(entity, "entity_kind") == "parameter" ? "value" : null)), id, states);
                Collection(id, "entity_detail", "entity_detail", "known");
            }
        }

        private void ImportSignature(string id, JsonElement components, string owner, string kind, JsonElement primitive, JsonElement? source)
        {
            var bodyState = source is null ? "absent" : Facts("bodies").Any(body => Ref(body, "owner_entity_key") == owner) ? "written" : "absent";
            var display = Encoding.UTF8.GetString(CanonicalJson.Serialize(primitive));
            Require(Primary("signature", components, Row(("callable_id", owner), ("callable_kind", kind), ("return_type_id", PrimitiveType(primitive.GetProperty("return_type"))),
                ("return_passing", Text(primitive, "return_passing")), ("calling_convention", Text(primitive, "calling_convention")),
                ("language_linkage", Text(primitive, "language_linkage")), ("body_state", bodyState), ("display", display)),
                nullStates: Inapplicable("calling_convention", "language_linkage", "exception_specification")) == id, "Signature identity drifted.");
            var parameters = source?.GetProperty("parameters").EnumerateArray().ToArray();
            var primitives = primitive.GetProperty("parameters").EnumerateArray().ToArray();
            for (var ordinal = 0; ordinal < primitives.Length; ordinal++)
            {
                var parameter = parameters?[ordinal]; var parameterComponents = new { signature_id = id, ordinal };
                var parameterId = Primary("parameter", parameterComponents, Row(("signature_id", id),
                    ("entity_id", parameter is { } row ? Ref(row, "entity_key") : null), ("ordinal", ordinal),
                    ("name", parameter is { } named ? Text(named, "name") : null), ("type_id", PrimitiveType(primitives[ordinal].GetProperty("type"))),
                    ("passing_mode", Text(primitives[ordinal], "passing")),
                    ("optional", parameter?.GetProperty("optional").GetBoolean() ?? false), ("variadic", parameter?.GetProperty("variadic").GetBoolean() ?? false)),
                    nullStates: Inapplicable("entity_id", "name", "default_expression_id", "default_value_kind", "default_value"));
                if (parameter is { } keyed) ids[Text(keyed, "key")!] = parameterId;
            }
            var qualifiers = primitive.GetProperty("qualifiers").EnumerateArray().ToArray();
            for (var ordinal = 0; ordinal < qualifiers.Length; ordinal++) Insert("signature_qualifier", Row(("signature_id", id), ("ordinal", ordinal), ("qualifier", qualifiers[ordinal].GetString())));
            Collection(id, "parameter", "parameters", "known"); Collection(id, "signature_qualifier", "signature_qualifier", "known");
        }
    }
}

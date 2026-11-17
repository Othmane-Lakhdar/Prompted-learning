using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptedLearning.Core;

public sealed record CompilerInputFile(string Key, string FileVersionId, string LogicalPath,
    string OriginalSha256, byte[] Bytes, SourceEncoding Encoding);
public sealed record SemanticUnitBinding(string Language, string OutputIdentity, string SnapshotId,
    string ContextId, string UnitId, string CaptureIndexFingerprint, string SemanticInputFingerprint,
    CompilerProducer Producer, IReadOnlyList<CompilerInputFile> Files, IReadOnlyList<string> ExpectedConsumedFileKeys,
    long MaxRecords = 100000, long MaxOperations = 100000, long MaxResultBytes = 16777216,
    IReadOnlyList<JsonElement>? AllowedTypeProviders = null);

public sealed class ValidatedSemanticUnit
{
    internal ValidatedSemanticUnit(SemanticUnitResult result, SemanticUnitBinding binding, Dictionary<string, JsonElement> index)
    { Result = result; Binding = binding; Records = new ReadOnlyDictionary<string, JsonElement>(index); }
    public SemanticUnitResult Result { get; }
    public SemanticUnitBinding Binding { get; }
    public IReadOnlyDictionary<string, JsonElement> Records { get; }
}

/// <summary>M1B source semantics acceptance, before canonical IDs or database writes.</summary>
public static class SemanticResultValidator
{
    private static readonly HashSet<string> EntityKinds = "namespace module class struct record union interface enum enumerator field global_variable method function constructor destructor operator conversion property indexer event delegate parameter local_variable constant local_function lambda type_parameter value_parameter template_parameter alias concept label scope unsupported".Split(' ').ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> CallableKinds = "method function constructor destructor operator conversion local_function lambda".Split(' ').ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> OperationKinds = "block variable_declaration_group variable_declaration variable_declarator expression_statement argument literal reference assignment binary unary conversion invocation return if loop jump throw try handler await yield initializer implicit unsupported".Split(' ').ToHashSet(StringComparer.Ordinal);
    private static readonly string[] KeyedArrays = ["spans", "entities", "types", "signatures", "appearances", "bodies", "operations", "external_symbols", "unresolved_references", "annotations", "preprocessing"];
    private static readonly string[] Features = ["parse", "entities", "types", "modifiers", "annotations", "preprocessing", "operations", "control_flow", "storage_events"];
    private static readonly Regex CanonicalId = new("^[a-z][a-z0-9_-]*:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public static ValidatedSemanticUnit Validate(SemanticUnitResult result, SemanticUnitBinding binding)
    {
        CompilerFacts.ValidateSchema(result);
        Require(binding.Language is "csharp" or "cpp", "Unsupported semantic language.");
        Require(result.SnapshotId == binding.SnapshotId && result.ContextId == binding.ContextId && result.UnitId == binding.UnitId
            && result.CaptureIndexFingerprint == binding.CaptureIndexFingerprint && result.SemanticInputFingerprint == binding.SemanticInputFingerprint
            && result.Producer == binding.Producer, "Semantic scope, producer or input fingerprint mismatch.");
        Require(result.Status is "complete" or "partial", "Failed/cancelled outcomes cannot publish semantic facts.");
        Require(binding.MaxRecords > 0 && binding.MaxOperations > 0 && binding.MaxResultBytes > 0, "Invalid semantic budget.");
        Require(CanonicalJson.Serialize(result).LongLength + 1 <= binding.MaxResultBytes, "Semantic result byte budget exceeded.");
        var files = new Dictionary<string, CompilerInputFile>(StringComparer.Ordinal);
        var boundaries = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var file in binding.Files)
        {
            Require(file.Key.StartsWith("file:", StringComparison.Ordinal) && !CanonicalId.IsMatch(file.Key) && files.TryAdd(file.Key, file), "Duplicate/invalid captured local file key.");
            Require(CanonicalJson.Sha256(file.Bytes) == file.OriginalSha256, "Bound source bytes differ from their hash.");
            var decoded = SourceDecoder.Decode(file.Bytes);
            Require(decoded.Status == "valid" && decoded.Source is not null && decoded.Encoding == file.Encoding, "Bound source encoding differs or is invalid.");
            var points = new HashSet<long> { 0 };
            for (var offset = 0; offset <= decoded.Source!.Text.Length; offset++)
                if (offset == 0 || offset == decoded.Source.Text.Length || !char.IsLowSurrogate(decoded.Source.Text[offset])) points.Add(decoded.Source.ByteOffset(offset));
            boundaries.Add(file.Key, points);
        }
        Require(files.Count > 0, "No bound source inputs.");
        var consumed = result.ConsumedFileKeys.ToHashSet(StringComparer.Ordinal);
        Require(consumed.Count == result.ConsumedFileKeys.Count && consumed.IsSubsetOf(files.Keys)
            && consumed.SetEquals(binding.ExpectedConsumedFileKeys), "Final consumed input set differs from the declared set.");
        Require(binding.ExpectedConsumedFileKeys.Count == binding.ExpectedConsumedFileKeys.Distinct(StringComparer.Ordinal).Count(), "Duplicate expected consumed key.");
        var facts = result.Facts.Clone();
        Require(!Rows(facts, "control_flow").Any(), "M1B cannot accept CFG/storage-event producers.");
        Require(!Rows(facts, "discovery_requests").Any(), "Unresolved discovery cannot publish facts.");
        var index = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var array in KeyedArrays) foreach (var row in Rows(facts, array)) Register(row);
        foreach (var signature in Rows(facts, "signatures")) foreach (var parameter in Rows(signature, "parameters")) Register(parameter);
        var wireRecords = CompilerFacts.ArrayNames.Sum(name => (long)facts.GetProperty(name).GetArrayLength()) + result.ConsumedFileKeys.Count;
        Require(Math.Max(index.Count, wireRecords) <= binding.MaxRecords && Rows(facts, "operations").LongCount() <= binding.MaxOperations, "Semantic record/operation budget exceeded.");

        void Register(JsonElement row)
        {
            var key = Text(row, "key")!;
            Require(!CanonicalId.IsMatch(key) && !files.ContainsKey(key) && index.TryAdd(key, row), "Duplicate or canonical discovered key: " + key);
        }
        JsonElement Reference(string? key, string kinds, bool nullable = false)
        {
            if (key is null) { Require(nullable, "Missing required local reference."); return default; }
            var kind = key.Split(':')[0]; Require(kinds.Split('|').Contains(kind, StringComparer.Ordinal), "Wrong local reference kind: " + key);
            if (kind == "file") { Require(files.ContainsKey(key), "Uncaptured file reference."); return default; }
            Require(index.TryGetValue(key, out var row), "Dangling local reference: " + key); return row;
        }
        void SpanBounds(JsonElement span)
        {
            var file = Text(span, "file_key")!; Reference(file, "file");
            var start = Number(span, "start_byte"); var end = Number(span, "end_byte");
            Require(start >= 0 && end >= start && end <= files[file].Bytes.LongLength && boundaries[file].Contains(start) && boundaries[file].Contains(end), "Invalid byte/scalar source interval.");
        }
        foreach (var span in Rows(facts, "spans"))
        {
            SpanBounds(span); var stack = Rows(span, "origin_stack").ToArray();
            foreach (var frame in stack) SpanBounds(frame);
            var cause = Text(span, "cause_span_key");
            if (stack.Length == 0) Require(cause is null, "Origin cause lacks its primitive stack.");
            else
            {
                var target = Reference(cause, "span");
                foreach (var key in new[] { "file_key", "start_byte", "end_byte", "origin_kind" }) Require(Same(stack[0].GetProperty(key), target.GetProperty(key)), "Origin cause does not match first stack frame.");
                Require(Same(CompilerFacts.Element(stack.Skip(1).ToArray()), target.GetProperty("origin_stack")), "Origin cause stack suffix differs.");
            }
        }
        Acyclic(Rows(facts, "spans"), "cause_span_key");
        var compilerKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in Rows(facts, "entities"))
        {
            var kind = Text(entity, "entity_kind")!; Require(EntityKinds.Contains(kind), "Unregistered entity kind.");
            Reference(Text(entity, "owner_entity_key"), "entity", true); Reference(Text(entity, "origin_span_key"), "span", true);
            Require(binding.Language == "csharp" ? Text(entity, "linkage") is null : Text(entity, "linkage") is not null, "Language linkage applicability differs.");
            Require(Text(entity, "access") is null or "public" or "private" or "protected" or "internal" or "protected_internal" or "private_protected" or "file", "Unregistered/inapplicable access tag.");
            if (kind is "namespace" or "parameter" or "local_variable") Require(Text(entity, "access") is null, "Entity access is inapplicable.");
            switch (Text(entity, "compiler_identity_kind"))
            {
                case "compiler": Require(Text(entity, "compiler_key") is { } compilerKey && compilerKeys.Add(compilerKey), "Missing/duplicate compiler entity key."); break;
                case "parameter":
                    Require(kind == "parameter" && Text(entity, "owner_entity_key") is not null && entity.GetProperty("declared_ordinal").ValueKind == JsonValueKind.Number, "Parameter lacks owner/ordinal."); break;
                case "source_local": Require(Text(entity, "owner_entity_key") is not null && Text(entity, "origin_span_key") is not null, "Local identity lacks owner/origin."); break;
            }
            if (CallableKinds.Contains(kind)) Require(entity.GetProperty("compiler_signature_key").ValueKind == JsonValueKind.Object, "Callable lacks primitive signature collision check.");
            if (entity.GetProperty("compiler_type_key").ValueKind != JsonValueKind.Null) ValidatePrimitive(entity.GetProperty("compiler_type_key"), binding);
        }
        Acyclic(Rows(facts, "entities"), "owner_entity_key");
        foreach (var type in Rows(facts, "types"))
        {
            var primitive = type.GetProperty("compiler_type_key"); ValidatePrimitive(primitive, binding);
            var named = Reference(Text(type, "named_entity_key"), "entity|external|unresolved", true);
            var element = Reference(Text(type, "element_type_key"), "type", true);
            foreach (var argument in Rows(type, "argument_type_keys")) Reference(argument.GetString(), "type");
            if (primitive.TryGetProperty("element", out var primitiveElement))
                Require(element.ValueKind != JsonValueKind.Undefined && Same(primitiveElement, element.GetProperty("compiler_type_key")), "Composite element contradicts primitive type.");
            else Require(element.ValueKind == JsonValueKind.Undefined, "Unexpected composite element.");
            var arguments = Rows(type, "argument_type_keys").ToArray();
            if (primitive.TryGetProperty("arguments", out var primitiveArguments))
            {
                Require(arguments.Length == primitiveArguments.GetArrayLength(), "Type argument count differs from primitive.");
                for (var i = 0; i < arguments.Length; i++) Require(Same(primitiveArguments[i], Reference(arguments[i].GetString(), "type").GetProperty("compiler_type_key")), "Type argument contradicts primitive.");
            }
            else Require(arguments.Length == 0, "Unexpected type arguments.");
            if (Text(primitive, "tag") == "builtin") Require(Text(type, "named_entity_key") is null && Text(type, "element_type_key") is null && !Rows(type, "argument_type_keys").Any(), "Builtin type has contradictory composite fields.");
            if (Text(primitive, "tag") == "named" && named.ValueKind != JsonValueKind.Undefined && Text(type, "named_entity_key")!.StartsWith("entity:", StringComparison.Ordinal))
                Require(Text(primitive, "compiler_key") == Text(named, "compiler_key"), "Named primitive type differs from declaration key.");
        }
        var signatureOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signature in Rows(facts, "signatures"))
        {
            Require(signatureOwners.Add(Text(signature, "owner_entity_key")!), "Duplicate signature owner.");
            var owner = Reference(Text(signature, "owner_entity_key"), "entity");
            var primitive = signature.GetProperty("compiler_signature_key");
            ValidatePrimitive(primitive, binding);
            Require(CallableKinds.Contains(Text(owner, "entity_kind")!) && Same(primitive, owner.GetProperty("compiler_signature_key")), "Signature owner/primitive collision check differs.");
            var returned = Reference(Text(signature, "return_type_key"), "type");
            Require(Same(primitive.GetProperty("return_type"), returned.GetProperty("compiler_type_key")), "Signature return type differs from primitive key.");
            var parameters = Rows(signature, "parameters").ToArray(); Ordinals(parameters, "ordinal");
            var primitiveParameters = Rows(primitive, "parameters").ToArray(); Require(parameters.Length == primitiveParameters.Length, "Signature parameter count differs.");
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i]; var entity = Reference(Text(parameter, "entity_key"), "entity");
                var type = Reference(Text(parameter, "type_key"), "type");
                Require(Text(entity, "entity_kind") == "parameter" && Text(entity, "owner_entity_key") == Text(signature, "owner_entity_key") && Number(entity, "declared_ordinal") == i, "Parameter detail/identity owner mismatch.");
                Require(Same(primitiveParameters[i].GetProperty("type"), type.GetProperty("compiler_type_key")) && Same(entity.GetProperty("compiler_type_key"), type.GetProperty("compiler_type_key")) && Text(primitiveParameters[i], "passing") == Text(parameter, "passing_mode"), "Parameter type/passing differs from primitive signature.");
            }
        }
        Require(signatureOwners.SetEquals(Rows(facts, "entities").Where(e => CallableKinds.Contains(Text(e, "entity_kind")!)).Select(e => Text(e, "key")!)), "Callable signature details are missing.");
        foreach (var appearance in Rows(facts, "appearances")) { Reference(Text(appearance, "entity_key"), "entity"); Reference(Text(appearance, "span_key"), "span"); }
        foreach (var group in Rows(facts, "entity_modifiers").GroupBy(r => Text(r, "entity_key")))
        {
            Reference(group.Key, "entity"); Ordinals(group.OrderBy(r => Number(r, "ordinal")), "ordinal");
            Require(group.Select(r => Text(r, "modifier")).Distinct(StringComparer.Ordinal).Count() == group.Count(), "Duplicate entity modifier.");
            foreach (var row in group) Reference(Text(row, "span_key"), "span", true);
        }
        foreach (var annotation in Rows(facts, "annotations"))
        {
            Reference(Text(annotation, "owner_entity_key"), "entity"); Reference(Text(annotation, "span_key"), "span"); Reference(Text(annotation, "attribute_type_key"), "entity|external|unresolved", true);
            Ordinals(Rows(annotation, "arguments"), "ordinal");
            foreach (var argument in Rows(annotation, "arguments"))
            {
                Reference(Text(argument, "expression_key"), "operation", true);
                if (argument.GetProperty("constant").ValueKind != JsonValueKind.Null) Constant(argument.GetProperty("constant"));
            }
        }
        foreach (var item in Rows(facts, "preprocessing"))
        {
            Reference(Text(item, "file_key"), "file"); var span = Reference(Text(item, "span_key"), "span"); Reference(Text(item, "resolved_file_key"), "file", true);
            Require(Text(span, "file_key") == Text(item, "file_key"), "Preprocessing origin file differs.");
            if (Text(item, "event_kind") == "include" && Text(item, "resolution") == "resolved") Require(Text(item, "resolved_file_key") is not null, "Resolved include lacks target.");
        }
        var children = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal); var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in Rows(facts, "operations"))
        {
            var bodyKey = Text(operation, "body_key")!; var body = Reference(bodyKey, "body");
            Require(Text(operation, "representation") == Text(body, "representation"), "Operation/body representations differ.");
            var parentKey = Text(operation, "parent_operation_key"); var parent = Reference(parentKey, "operation", true);
            Reference(Text(operation, "span_key"), "span", true); Reference(Text(operation, "cause_span_key"), "span", true);
            Reference(Text(operation, "type_key"), "type", true); Reference(Text(operation, "referenced_entity_key"), "entity|external|unresolved", true);
            var kind = Text(operation, "operation_kind")!; Require(OperationKinds.Contains(kind), "Unregistered operation kind.");
            Require(operation.GetProperty("supported").GetBoolean() ? Text(operation, "unsupported_reason") is null && kind != "unsupported" : Text(operation, "unsupported_reason") is not null, "Operation support/reason contradicts.");
            var path = Rows(operation, "structural_path").Select(v => v.GetInt64()).ToArray();
            Require(paths.Add(bodyKey + "/" + string.Join('/', path)), "Duplicate operation structural path.");
            if (parentKey is null) Require(operation.GetProperty("child_ordinal").ValueKind == JsonValueKind.Null && path.SequenceEqual([0L]), "Invalid root ordinal/path.");
            else
            {
                Require(Text(parent, "body_key") == bodyKey && path.SequenceEqual(Rows(parent, "structural_path").Select(v => v.GetInt64()).Append(Number(operation, "child_ordinal"))), "Cross-body child or structural path mismatch.");
                if (!children.TryGetValue(parentKey, out var list)) children[parentKey] = list = [];
                list.Add(operation);
            }
            if (kind == "reference" && Text(operation, "referenced_entity_key") is { } referenced && referenced.StartsWith("entity:", StringComparison.Ordinal)
                && Text(index[referenced], "entity_kind") is "parameter" or "local_variable")
            {
                Require(Text(operation, "semantic_role") is "read" or "destination", "Storage reference lost read/destination semantics.");
                Require(Text(index[referenced], "owner_entity_key") == Text(body, "owner_entity_key"), "Storage reference crosses callable ownership without capture support.");
            }
            if (kind == "invocation") Require(Text(operation, "semantic_role") == "call", "Invocation lost call semantics.");
            if (kind == "argument")
            {
                Require(parent.ValueKind != JsonValueKind.Undefined && Text(parent, "operation_kind") == "invocation", "Argument wrapper lacks its invocation.");
                var mappings = Rows(parent, "operands").Where(p => Text(p, "role") == "argument" && Text(p, "target_key") == Text(operation, "key")).ToArray();
                Require(mappings.Length == 1 && Rows(operation, "operands").All(p => Text(p, "parameter_entity_key") == Text(mappings[0], "parameter_entity_key")), "Argument wrapper and invocation parameter mappings differ.");
            }
            if (operation.GetProperty("constant").ValueKind != JsonValueKind.Null) Constant(operation.GetProperty("constant"));
            foreach (var group in Rows(operation, "operands").GroupBy(o => Text(o, "role")))
            {
                Ordinals(group.OrderBy(o => Number(o, "ordinal")), "ordinal");
                foreach (var operand in group)
                {
                    var target = Reference(Text(operand, "target_key"), "operation|entity|external|unresolved|type");
                    if (Text(operand, "target_key")!.StartsWith("operation:", StringComparison.Ordinal))
                        Require(Text(target, "body_key") == bodyKey, "Operand crosses body boundary.");
                    var parameter = Reference(Text(operand, "parameter_entity_key"), "entity", true);
                    if (parameter.ValueKind != JsonValueKind.Undefined)
                        Require(kind is "invocation" or "argument" && group.Key == "argument" && Text(parameter, "entity_kind") == "parameter", "Parameter mapping is not a call argument.");
                    if (kind == "invocation" && group.Key == "argument" && Text(operation, "referenced_entity_key") is { } callee && callee.StartsWith("entity:", StringComparison.Ordinal))
                        Require(parameter.ValueKind != JsonValueKind.Undefined && Text(parameter, "owner_entity_key") == callee
                            && Number(parameter, "declared_ordinal") == Number(operand, "ordinal"), "Direct positional argument mapping differs from its compiler-bound parameter.");
                }
            }
        }
        Acyclic(Rows(facts, "operations"), "parent_operation_key");
        foreach (var group in children.Values) Ordinals(group.OrderBy(o => Number(o, "child_ordinal")), "child_ordinal");
        foreach (var body in Rows(facts, "bodies"))
        {
            var owner = Reference(Text(body, "owner_entity_key"), "entity"); Require(CallableKinds.Contains(Text(owner, "entity_kind")!), "Body has non-callable owner.");
            Reference(Text(body, "span_key"), "span", true);
            var rootKey = Text(body, "root_operation_key"); var root = Reference(rootKey, "operation", true);
            var owned = Rows(facts, "operations").Where(o => Text(o, "body_key") == Text(body, "key")).ToArray();
            var roots = owned.Where(o => Text(o, "parent_operation_key") is null).Select(o => Text(o, "key")).ToArray();
            if (Text(body, "status") == "complete") Require(rootKey is not null && Text(body, "reason") is null, "Complete body lacks its root or has failure reason.");
            else Require(Text(body, "reason") is not null, "Incomplete body lacks reason.");
            if (owned.Length != 0) Require(rootKey is not null && roots.SequenceEqual([rootKey]) && Text(root, "body_key") == Text(body, "key"), "Detached or unregistered operation root.");
            else Require(rootKey is null && Text(body, "status") != "complete", "Empty body cannot claim a complete tree.");
        }
        var coverage = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows(facts, "coverage"))
        {
            var file = Text(row, "file_key")!; Reference(file, "file"); var feature = Text(row, "feature")!;
            Require(coverage.Add(file + "/" + feature), "Duplicate coverage record.");
            foreach (var span in Rows(row, "unparsed_span_keys")) Require(Text(Reference(span.GetString(), "span"), "file_key") == file, "Coverage span belongs to another file.");
            if (feature is "control_flow" or "storage_events") Require(Text(row, "status") == "not_attempted" && Text(row, "reason") == "not_requested", "Unrequested producer has misleading coverage.");
            else if (Text(row, "status") == "complete") Require(Text(row, "reason") is null && !Rows(row, "unparsed_span_keys").Any(), "Complete coverage contains unsupported region/reason.");
            else Require(Text(row, "reason") is not null, "Unavailable coverage lacks reason.");
        }
        Require(coverage.SetEquals(files.Keys.SelectMany(file => Features.Select(feature => file + "/" + feature))), "Missing file/feature coverage.");
        foreach (var diagnostic in Rows(facts, "diagnostics"))
        {
            Reference(Text(diagnostic, "span_key"), "span", true);
            if (Text(diagnostic, "subject_key") is { } subject) Require(index.ContainsKey(subject) || files.ContainsKey(subject), "Dangling diagnostic subject.");
        }
        if (result.Status == "complete")
        {
            Require(Rows(facts, "coverage").Where(r => Text(r, "feature") is not ("control_flow" or "storage_events")).All(r => Text(r, "status") == "complete"), "Complete result contains incomplete requested coverage.");
            Require(Rows(facts, "operations").All(o => o.GetProperty("supported").GetBoolean()) && Rows(facts, "bodies").All(b => Text(b, "status") == "complete")
                && Rows(facts, "entities").All(e => Text(e, "entity_kind") != "unsupported" && Text(e, "resolution") == "resolved"), "Complete result contains unsupported/incomplete source facts.");
            Require(!Rows(facts, "diagnostics").Any(d => Text(d, "severity") == "error"), "Complete result contains compiler errors.");
        }
        return new(result with { Facts = facts, ConsumedFileKeys = result.ConsumedFileKeys.ToArray() }, binding, index);
    }

    private static void ValidatePrimitive(JsonElement value, SemanticUnitBinding binding)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("provider", out var provider))
            {
                Require(binding.AllowedTypeProviders is not null && binding.AllowedTypeProviders.Any(allowed => Same(provider, allowed)), "Primitive provider was not bound by the trusted host context.");
                foreach (var name in new[] { "version", "content_fingerprint" })
                {
                    var state = provider.GetProperty(name); var known = Text(state, "state") == "known";
                    Require(known ? Text(state, "value") is not null && Text(state, "reason") is null : Text(state, "value") is null && Text(state, "reason") is not null, "Primitive provider knowledge state contradicts.");
                }
                if (Text(provider, "kind") == "internal_output") Require(Text(provider, "logical_name") == binding.OutputIdentity && Text(provider.GetProperty("content_fingerprint"), "state") == "not_collected", "Internal source output has wrong identity or invented/inapplicable binary fingerprint.");
            }
            foreach (var property in value.EnumerateObject()) ValidatePrimitive(property.Value, binding);
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidatePrimitive(item, binding);
    }
    private static void Constant(JsonElement constant)
    {
        var kind = Text(constant, "kind"); var value = Text(constant, "value")!;
        switch (kind)
        {
            case "signed_integer": case "unsigned_integer":
                Require(BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) && number.ToString(CultureInfo.InvariantCulture) == value && (kind != "unsigned_integer" || number >= 0), "Noncanonical integer constant."); break;
            case "boolean": Require(value is "true" or "false", "Invalid Boolean constant."); break;
            case "null": Require(value == "null", "Invalid null constant."); break;
            default: throw new InvalidDataException("Constant kind is outside the frozen M1B pilot: " + kind);
        }
    }
    private static void Acyclic(IEnumerable<JsonElement> values, string parent)
    {
        var rows = values.ToDictionary(v => Text(v, "key")!, StringComparer.Ordinal);
        foreach (var row in rows.Values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal); var cursor = Text(row, "key");
            while (cursor is not null) { Require(seen.Add(cursor), "Cyclic " + parent); cursor = Text(rows[cursor], parent); }
        }
    }
    private static void Ordinals(IEnumerable<JsonElement> rows, string field)
    { long next = 0; foreach (var row in rows) Require(Number(row, field) == next++, "Noncontiguous ordered role: " + field); }
    private static IEnumerable<JsonElement> Rows(JsonElement value, string property) => value.GetProperty(property).EnumerateArray();
    private static string? Text(JsonElement value, string property) => value.GetProperty(property).ValueKind == JsonValueKind.Null ? null : value.GetProperty(property).GetString();
    private static long Number(JsonElement value, string property) => value.GetProperty(property).GetInt64();
    private static bool Same(JsonElement left, JsonElement right) => CanonicalJson.Serialize(left).AsSpan().SequenceEqual(CanonicalJson.Serialize(right));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}

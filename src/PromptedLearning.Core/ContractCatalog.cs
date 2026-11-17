using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptedLearning.Core;

public sealed class ContractException(string message) : Exception(message);

/// <summary>Validates the exact JSON Schema vocabulary used by the frozen contracts.</summary>
public sealed class ContractCatalog
{
    private readonly JsonDocument document;
    private static readonly Lazy<ContractCatalog> Instance = new(() =>
    {
        using var stream = typeof(ContractCatalog).Assembly.GetManifestResourceStream("PromptedLearning.Core.contracts.json")
            ?? throw new InvalidDataException("Missing embedded contracts.");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        return new ContractCatalog(buffer.ToArray());
    });
    public static ContractCatalog Default => Instance.Value;
    public JsonElement Root => document.RootElement;

    public ContractCatalog(ReadOnlySpan<byte> bytes)
    {
        document = CanonicalJson.Parse(bytes);
        InspectSchema(Root, "#");
    }

    public void Validate(string definition, JsonElement value)
    {
        _ = CanonicalJson.Serialize(value);
        if (!Root.GetProperty("$defs").TryGetProperty(definition, out var schema))
            throw new ContractException($"Unknown contract definition: {definition}.");
        var error = Error(schema, value, "$", 0);
        if (error is not null) throw new ContractException(error);
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$ref", "$defs", "title", "description", "type", "properties", "required",
        "additionalProperties", "items", "prefixItems", "contains", "enum", "const", "oneOf", "anyOf", "allOf", "if", "then", "else",
        "not", "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems", "uniqueItems", "pattern"
    };

    // An unsupported validation keyword must fail loading rather than be ignored.
    private static void InspectSchema(JsonElement schema, string path)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (schema.ValueKind != JsonValueKind.Object) throw new ContractException($"Invalid schema at {path}.");
        foreach (var p in schema.EnumerateObject())
        {
            if (p.Name.StartsWith("x-", StringComparison.Ordinal)) continue;
            if (!Keywords.Contains(p.Name)) throw new ContractException($"Unsupported schema keyword {path}/{p.Name}.");
            if (p.Name is "properties" or "$defs") foreach (var child in p.Value.EnumerateObject()) InspectSchema(child.Value, path + "/" + p.Name + "/" + child.Name);
            else if (p.Name is "items" or "contains" or "additionalProperties" or "if" or "then" or "else" or "not") InspectSchema(p.Value, path + "/" + p.Name);
            else if (p.Name is "oneOf" or "anyOf" or "allOf" or "prefixItems") foreach (var child in p.Value.EnumerateArray()) InspectSchema(child, path + "/" + p.Name);
        }
    }

    private string? Error(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (depth > 256) return path + ": schema recursion limit.";
        if (schema.ValueKind == JsonValueKind.True) return null;
        if (schema.ValueKind == JsonValueKind.False) return path + ": forbidden value.";
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var pointer = reference.GetString()!;
            if (!pointer.StartsWith("#/", StringComparison.Ordinal)) return path + ": external schema references are unsupported.";
            var target = Root;
            foreach (var part in pointer[2..].Split('/')) target = target.GetProperty(part.Replace("~1", "/").Replace("~0", "~"));
            var referencedError = Error(target, value, path, depth + 1);
            if (referencedError is not null) return referencedError;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var matches = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Any(t => TypeMatches(t.GetString()!, value)) : TypeMatches(type.GetString()!, value);
            if (!matches) return path + ": wrong value type.";
        }
        if (schema.TryGetProperty("const", out var constant) && !Same(constant, value)) return path + ": wrong constant.";
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(c => Same(c, value))) return path + ": unknown enum value.";
        foreach (var combination in new[] { "oneOf", "anyOf", "allOf" })
            if (schema.TryGetProperty(combination, out var alternatives))
            {
                var count = alternatives.EnumerateArray().Count(a => Error(a, value, path, depth + 1) is null);
                if (combination == "oneOf" && count != 1 || combination == "anyOf" && count == 0 || combination == "allOf" && count != alternatives.GetArrayLength())
                    return path + ": " + combination + " constraint failed.";
            }
        if (schema.TryGetProperty("not", out var not) && Error(not, value, path, depth + 1) is null) return path + ": forbidden schema match.";
        if (schema.TryGetProperty("if", out var condition))
        {
            var branch = Error(condition, value, path, depth + 1) is null ? "then" : "else";
            if (schema.TryGetProperty(branch, out var selected))
            {
                var branchError = Error(selected, value, path, depth + 1);
                if (branchError is not null) return branchError;
            }
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray()) if (!value.TryGetProperty(name.GetString()!, out _)) return path + ": missing " + name.GetString() + ".";
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            foreach (var property in value.EnumerateObject())
            {
                var childPath = path + "/" + property.Name.Replace("~", "~0").Replace("/", "~1");
                if (hasProperties && properties.TryGetProperty(property.Name, out var childSchema))
                {
                    var childError = Error(childSchema, property.Value, childPath, depth + 1);
                    if (childError is not null) return childError;
                }
                else if (schema.TryGetProperty("additionalProperties", out var additional))
                {
                    var childError = Error(additional, property.Value, childPath, depth + 1);
                    if (childError is not null) return childError;
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var count = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out var min) && count < min.GetInt64() || schema.TryGetProperty("maxItems", out var max) && count > max.GetInt64()) return path + ": array length.";
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
            {
                var keys = value.EnumerateArray().Select(v => Convert.ToBase64String(CanonicalJson.Serialize(v)));
                if (keys.Distinct(StringComparer.Ordinal).Count() != count) return path + ": duplicate item.";
            }
            var prefixLength = 0;
            if (schema.TryGetProperty("prefixItems", out var prefix))
            {
                prefixLength = prefix.GetArrayLength();
                for (var index = 0; index < Math.Min(count, prefixLength); index++)
                {
                    var childError = Error(prefix[index], value[index], path + "/" + index, depth + 1);
                    if (childError is not null) return childError;
                }
            }
            if (schema.TryGetProperty("contains", out var contains) && !value.EnumerateArray().Any(item => Error(contains, item, path, depth + 1) is null)) return path + ": contains constraint failed.";
            if (schema.TryGetProperty("items", out var itemSchema))
            {
                for (var index = prefixLength; index < count; index++)
                {
                    var childError = Error(itemSchema, value[index], path + "/" + index, depth + 1);
                    if (childError is not null) return childError;
                }
            }
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!; var length = text.EnumerateRunes().Count();
            if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt64() || schema.TryGetProperty("maxLength", out var max) && length > max.GetInt64()) return path + ": string length.";
            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return path + ": pattern mismatch.";
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetInt64();
            if (schema.TryGetProperty("minimum", out var min) && number < min.GetInt64() || schema.TryGetProperty("maximum", out var max) && number > max.GetInt64()) return path + ": numeric bound.";
        }
        return null;
    }

    private static bool TypeMatches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String, "null" => value.ValueKind == JsonValueKind.Null,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" or "number" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        _ => throw new ContractException("Unsupported schema type: " + type)
    };
    private static bool Same(JsonElement left, JsonElement right) => CanonicalJson.Serialize(left).AsSpan().SequenceEqual(CanonicalJson.Serialize(right));
}

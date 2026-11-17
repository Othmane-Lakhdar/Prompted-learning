using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PromptedLearning.Core;

/// <summary>The pl-jsonl/1 scalar/object encoding. Semantic floating values use tagged text.</summary>
public static class CanonicalJson
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();
    public static IComparer<string?> Utf8Comparer { get; } = new Utf8StringComparer();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 128
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static byte[] Serialize(object? value, bool trailingNewline = false)
    {
        // Validate strings before System.Text.Json can replace malformed UTF16.
        if (value is not JsonElement) ValidateObjectStrings(value, new HashSet<object>(ReferenceEqualityComparer.Instance));
        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, JsonOptions);
        var text = new StringBuilder();
        Write(element, text, 0);
        if (trailingNewline) text.Append('\n');
        return new UTF8Encoding(false, true).GetBytes(text.ToString());
    }

    public static JsonDocument Parse(ReadOnlySpan<byte> bytes)
    {
        var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 128 });
        try { _ = Serialize(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Write(JsonElement value, StringBuilder text, int depth)
    {
        if (depth > 128) throw new JsonException("Canonical JSON nesting exceeds 128.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToArray();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new JsonException("Duplicate object property.");
                text.Append('{');
                var firstProperty = true;
                foreach (var property in properties.OrderBy(p => p.Name, Utf8Comparer))
                {
                    if (!firstProperty) text.Append(','); firstProperty = false;
                    WriteString(property.Name, text); text.Append(':'); Write(property.Value, text, depth + 1);
                }
                text.Append('}'); break;
            case JsonValueKind.Array:
                text.Append('['); var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem) text.Append(','); firstItem = false; Write(item, text, depth + 1);
                }
                text.Append(']'); break;
            case JsonValueKind.String: WriteString(value.GetString()!, text); break;
            case JsonValueKind.Number:
                if (!value.TryGetInt64(out var number)) throw new JsonException("Semantic numbers must be signed Int64 integers.");
                text.Append(number.ToString(CultureInfo.InvariantCulture)); break;
            case JsonValueKind.True: text.Append("true"); break;
            case JsonValueKind.False: text.Append("false"); break;
            case JsonValueKind.Null: text.Append("null"); break;
            default: throw new JsonException("Undefined JSON value.");
        }
    }

    private static void WriteString(string value, StringBuilder text)
    {
        ValidateString(value); text.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (ch < 32) text.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(ch);
                    break;
            }
        }
        text.Append('"');
    }

    public static void ValidateString(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
                throw new JsonException("Unpaired surrogate in canonical text.");
            remaining = remaining[consumed..];
        }
    }

    private static void ValidateObjectStrings(object? value, HashSet<object> visiting, int depth = 0)
    {
        if (depth > 128) throw new JsonException("Canonical input nesting exceeds 128.");
        if (value is null or JsonElement) return;
        if (value is string text) { ValidateString(text); return; }
        if (value is char ch) { ValidateString(ch.ToString()); return; }
        if (value is float or double or decimal) throw new JsonException("Use tagged text for floating and decimal values.");
        if (value is DateTime or DateTimeOffset or TimeSpan or Guid) throw new JsonException("Format non-scalar values explicitly as contract text.");
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum) return;
        if (!visiting.Add(value)) throw new JsonException("Cyclic input object.");
        try
        {
            if (value is System.Collections.IDictionary map)
                foreach (System.Collections.DictionaryEntry entry in map) { ValidateObjectStrings(entry.Key, visiting, depth + 1); ValidateObjectStrings(entry.Value, visiting, depth + 1); }
            else if (value is System.Collections.IEnumerable list)
                foreach (var item in list) ValidateObjectStrings(item, visiting, depth + 1);
            else
                foreach (var property in type.GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
                    ValidateObjectStrings(property.GetValue(value), visiting, depth + 1);
        }
        finally { visiting.Remove(value); }
    }

    private sealed class Utf8StringComparer : IComparer<string?>
    {
        public int Compare(string? left, string? right)
        {
            if (left is null || right is null) return left is null ? right is null ? 0 : -1 : 1;
            ValidateString(left); ValidateString(right);
            return Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
        }
    }
}

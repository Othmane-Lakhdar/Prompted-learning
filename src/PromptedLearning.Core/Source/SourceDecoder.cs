using System.Text;

namespace PromptedLearning.Core;

public sealed class DecodedSource
{
    private readonly long[] byteOffsets;
    internal DecodedSource(string text, SourceEncoding encoding, long[] offsets)
    {
        Text = text;
        Encoding = encoding;
        byteOffsets = offsets;
    }
    public string Text { get; }
    public SourceEncoding Encoding { get; }
    public long ByteOffset(int utf16Position)
    {
        if ((uint)utf16Position >= (uint)byteOffsets.Length || byteOffsets[utf16Position] < 0)
            throw new ArgumentOutOfRangeException(nameof(utf16Position), "Position is outside the source or inside a surrogate pair.");
        return byteOffsets[utf16Position];
    }
}

public sealed record SourceDecoding(SourceEncoding Encoding, string Status, string? Reason,
    long? ErrorByte, DecodedSource? Source);

public static class SourceDecoder
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16Le = new(false, false, true);
    private static readonly UnicodeEncoding Utf16Be = new(true, false, true);

    public static SourceDecoding Decode(ReadOnlySpan<byte> bytes, bool binary = false)
    {
        if (binary)
            return new(new("not_applicable", null, null, "binary_input"), "not_applicable", "binary_input", null, null);
        // UTF-32 LE shares its first two bytes with UTF-16 LE. Check the longer BOM first.
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            return new(new("unknown", null, 4, "unsupported_encoding_utf32"), "not_attempted", "unsupported_encoding_utf32", 0, null);
        var (name, bom) = Detect(bytes);
        var encoding = new SourceEncoding("known", name, bom, null);
        var codec = Codec(name);
        string text;
        try { text = codec.GetString(bytes[bom..]); }
        catch (DecoderFallbackException error)
        {
            return new(encoding, "invalid", "invalid_encoded_sequence", Math.Max(bom, bom + error.Index), null);
        }
        var offsets = new long[text.Length + 1];
        long position = bom;
        offsets[0] = position;
        for (var index = 0; index < text.Length;)
        {
            int length = char.IsHighSurrogate(text[index]) ? 2 : 1;
            if (length == 2) offsets[index + 1] = -1;
            position += codec.GetByteCount(text.AsSpan(index, length));
            index += length;
            offsets[index] = position;
        }
        return new(encoding, "valid", null, null, new(text, encoding, offsets));
    }

    internal static (string Name, int Bom) Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return ("utf-8", 3);
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) return ("utf-16le", 2);
        if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) return ("utf-16be", 2);
        return ("utf-8", 0);
    }

    internal static string DecodeRange(ReadOnlySpan<byte> bytes, SourceEncoding encoding, long startByte)
    {
        if (encoding.State != "known" || encoding.Name is null || encoding.BomBytes is null)
            throw new InvalidDataException("Source encoding is unavailable.");
        var bom = encoding.BomBytes.Value;
        if (encoding.Name != "utf-8" && startByte != 0 && (startByte - bom) % 2 != 0)
            throw new InvalidDataException("Interval starts inside a UTF-16 code unit.");
        if (bytes.IsEmpty) return "";
        if (startByte == 0 && bom > 0)
        {
            if (bytes.Length < bom) throw new InvalidDataException("Interval splits the byte-order mark.");
            bytes = bytes[bom..];
        }
        try { return Codec(encoding.Name).GetString(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Interval splits or contains an invalid encoded scalar.", error); }
    }

    private static Encoding Codec(string name) => name switch
    {
        "utf-8" => Utf8, "utf-16le" => Utf16Le, "utf-16be" => Utf16Be,
        _ => throw new InvalidDataException("Unsupported source encoding.")
    };
}

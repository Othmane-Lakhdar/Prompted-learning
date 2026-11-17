using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptedLearning.Core;

public static partial class StableId
{
    [GeneratedRegex("^[a-z][a-z0-9_-]*/[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    public static string Create(string domain, params object?[] parts)
    {
        if (!DomainPattern().IsMatch(domain)) throw new ArgumentException("Invalid identity domain.", nameof(domain));
        var preimage = new object?[parts.Length + 1]; preimage[0] = domain; parts.CopyTo(preimage, 1);
        return domain[..domain.IndexOf('/')] + ":" + CanonicalJson.Sha256(CanonicalJson.Serialize(preimage));
    }

    public static JsonElement Envelope(string domain, object components)
    {
        using var document = CanonicalJson.Parse(CanonicalJson.Serialize(new { schema = "identity-input/1", domain, components }));
        var envelope = document.RootElement.Clone();
        ContractCatalog.Default.Validate("identity-input", envelope);
        return envelope;
    }

    public static void Verify(string id, JsonElement envelope)
    {
        ContractCatalog.Default.Validate("identity-input", envelope);
        var expected = Create(envelope.GetProperty("domain").GetString()!, envelope.GetProperty("components"));
        if (!StringComparer.Ordinal.Equals(id, expected)) throw new InvalidDataException("Identity preimage does not match ID.");
    }
}

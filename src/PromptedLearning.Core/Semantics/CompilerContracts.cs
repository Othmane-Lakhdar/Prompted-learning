using System.Text.Json;

namespace PromptedLearning.Core;

public sealed record CompilerProducer(string Name, string Version, string CompilerVersion, string BinarySha256);

/// <summary>Host-side transport-neutral result; facts retain the frozen typed compiler schemas.</summary>
public sealed record SemanticUnitResult(string SnapshotId, string ContextId, string UnitId,
    string CaptureIndexFingerprint, string SemanticInputFingerprint, CompilerProducer Producer,
    string Status, string? StopReason, JsonElement Facts, IReadOnlyList<string> ConsumedFileKeys)
{
    public string Schema => "semantic-unit-result/1";
}

public static class CompilerFacts
{
    public static IReadOnlyList<string> ArrayNames { get; } = Array.AsReadOnly(new[]
    {
        "spans", "entities", "types", "signatures", "appearances", "bodies", "operations", "control_flow",
        "external_symbols", "unresolved_references", "coverage", "diagnostics", "discovery_requests",
        "entity_modifiers", "annotations", "preprocessing"
    });

    public static JsonElement Element(object value)
    {
        using var document = CanonicalJson.Parse(CanonicalJson.Serialize(value));
        return document.RootElement.Clone();
    }

    public static void ValidateSchema(SemanticUnitResult result)
        => ContractCatalog.Default.Validate("semantic-unit-result", Element(result));

    public static JsonElement Empty()
        => Element(ArrayNames.ToDictionary(name => name, _ => Array.Empty<object>(), StringComparer.Ordinal));
}

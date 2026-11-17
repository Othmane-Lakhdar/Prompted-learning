using System.Collections.ObjectModel;
using System.Text.Json;

namespace PromptedLearning.Core;

public sealed record CaptureExclusion(string Rule, string Reason);
public sealed record CaptureRoot(string LogicalName, string? PhysicalPath, string Boundary = "internal",
    IReadOnlyList<CaptureExclusion>? Exclusions = null, bool AllowReparsePoints = false);
public sealed record CaptureInput(string RootName, string RelativePath, string Role = "source",
    string Language = "unknown", string Origin = "written", bool Binary = false,
    byte[]? VirtualBytes = null, string? DoNotCaptureReason = null);
public sealed record CapturePlan(string RepositoryIdentity, IReadOnlyList<CaptureRoot> Roots,
    IReadOnlyList<CaptureInput> Inputs, long MaxDecodeBytes = 64 * 1024 * 1024);
public sealed record CaptureAttempt(string LogicalPath, int Attempt);
public sealed record CaptureDiagnostic(string Code, string LogicalPath, long? ByteOffset, string Message);
public sealed record SourceEncoding(string State, string? Name, int? BomBytes, string? Reason);
public sealed record ContextMembership(string ContextId, string Selection = "included", string? Reason = null);
public sealed record CaptureBindings(string SnapshotId, IReadOnlyList<string> ContextIds,
    IReadOnlyDictionary<string, IReadOnlyList<ContextMembership>> MembershipsByLogicalPath);
public sealed record CapturedRoot(string Id, string LogicalName, string? OriginalPath, string RootKind,
    string Boundary, IReadOnlyList<CaptureExclusion> Exclusions);
public sealed record CapturedBlob(string Hash, long ByteLength, string StorageKey);
public sealed record CapturedFile(string Id, string RootId, string FileVersionId, string RootName,
    string RelativePath, string LogicalPath, string OriginalPath, IReadOnlyList<string> Aliases,
    string Role, string Language, string Origin, string? ContentHash, long? ByteLength,
    string CollectionStatus, string? CollectionReason, SourceEncoding Encoding,
    string Decoding, string? DecodingReason, long? DecodingErrorByte, int CaptureAttempts);

public sealed record CapturedInputs(string ArchiveRoot, string SnapshotId,
    IReadOnlyList<CapturedRoot> Roots, IReadOnlyList<CapturedFile> Files,
    IReadOnlyList<CapturedBlob> Blobs, IReadOnlyDictionary<string, JsonElement> IdentityInputs,
    IReadOnlyList<CaptureDiagnostic> Diagnostics);
public sealed record SealedCapture(string ArchiveRoot, string SnapshotId, string IndexFingerprint,
    string ManifestHash, JsonElement Index, JsonElement Manifest, CapturedInputs Inputs);

public sealed record SourceRead(string FileVersionId, string ContentHash, long StartByte,
    long EndByte, byte[] Bytes, long BytesRead);
public sealed record SourceTextRead(string FileVersionId, string ContentHash, long StartByte,
    long EndByte, string Text, SourceEncoding Encoding, long BytesRead);

public sealed record SourcePreparationReport(string Schema, string CaptureIndexFingerprint,
    string? SourceManifestHash, string Mode, int MaxOpenBlobs, long MaxPrepareBytes,
    long MaxPrepareTimeMs, int HashBufferBytes, long VerifiedBytes, int UniqueBlobs,
    int PeakOpenHandles, long ElapsedMs, string Outcome, string? Reason);

public sealed class SourceArchiveException(string message, SourcePreparationReport report, Exception? inner = null)
    : IOException(message, inner)
{
    public SourcePreparationReport Report { get; } = report;
}

internal static class SourceDocuments
{
    internal static IComparer<string> Utf8Order { get; } = Comparer<string>.Create((left, right) =>
        System.Text.Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(System.Text.Encoding.UTF8.GetBytes(right)));
    internal static JsonElement Element(object value)
    {
        using var document = CanonicalJson.Parse(CanonicalJson.Serialize(value));
        return document.RootElement.Clone();
    }
    internal static JsonElement Identity(string domain, object components) => Element(new { schema = "identity-input/1", domain, components });
    internal static IReadOnlyDictionary<TKey, TValue> ReadOnly<TKey, TValue>(Dictionary<TKey, TValue> values) where TKey : notnull
        => new ReadOnlyDictionary<TKey, TValue>(values);
}

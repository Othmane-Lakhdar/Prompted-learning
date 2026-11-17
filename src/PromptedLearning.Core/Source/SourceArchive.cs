using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace PromptedLearning.Core;

/// <summary>A Windows session pins verified blobs. It never opens a working source path.</summary>
public sealed class SourceArchive : IAsyncDisposable
{
    public const int MaximumOpenBlobs = 4096;
    public const long MaximumPreparationBytes = 8589934592;
    public const long MaximumPreparationMilliseconds = 120000;
    public const int HashBufferBytes = 65536;
    private readonly Dictionary<string, FileStream> handles;
    private readonly Dictionary<string, JsonElement> files;
    private bool disposed;
    private long readBytes;
    private SourceArchive(JsonElement index, JsonElement manifest, Dictionary<string, FileStream> handles,
        Dictionary<string, JsonElement> files, SourcePreparationReport report)
    {
        Index = index;
        Manifest = manifest;
        this.handles = handles;
        this.files = files;
        Preparation = report;
    }
    public JsonElement Index { get; }
    public JsonElement Manifest { get; }
    public SourcePreparationReport Preparation { get; }
    /// <summary>Actual post-preparation archive bytes read, including reads before a failed text boundary check.</summary>
    public long BytesRead => Interlocked.Read(ref readBytes);

    public static async Task<SourceArchive> OpenAsync(string archiveRoot, string expectedIndexFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (expectedIndexFingerprint.Length != 64 || expectedIndexFingerprint.Any(ch => !"0123456789abcdef".Contains(ch)))
            throw new ArgumentException("Expected index fingerprint must be lowercase SHA-256.");
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(MaximumPreparationMilliseconds));
        var preparationToken = deadline.Token;
        var handles = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        long verifiedBytes = 0;
        var peakHandles = 0;
        string? manifestHash = null;
        SourcePreparationReport Report(string outcome, string? reason) => new("source-archive-preparation/1", expectedIndexFingerprint,
            manifestHash, "windows_pinned_read_handles_v1", MaximumOpenBlobs, MaximumPreparationBytes,
            MaximumPreparationMilliseconds, HashBufferBytes, verifiedBytes, handles.Count, peakHandles,
            watch.ElapsedMilliseconds, outcome, reason);
        void Budget()
        {
            preparationToken.ThrowIfCancellationRequested();
            if (watch.ElapsedMilliseconds > MaximumPreparationMilliseconds) throw new IOException("archive_preparation_time_limit");
        }
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows pinned read handles are required.");
            archiveRoot = Path.GetFullPath(archiveRoot);
            Budget();
            var indexBytes = await File.ReadAllBytesAsync(SourcePaths.Physical(archiveRoot, "capture-index.json", false), preparationToken);
            if (CanonicalJson.Sha256(indexBytes) != expectedIndexFingerprint) throw new InvalidDataException("capture_index_hash_mismatch");
            using var indexDocument = CanonicalJson.Parse(indexBytes);
            var index = indexDocument.RootElement.Clone();
            ContractCatalog.Default.Validate("capture-index", index);
            if (!indexBytes.AsSpan().SequenceEqual(CanonicalJson.Serialize(index, true))) throw new InvalidDataException("capture_index_not_canonical");
            manifestHash = index.GetProperty("source_manifest_hash").GetString()!;
            var manifestBytes = await File.ReadAllBytesAsync(SourcePaths.Physical(archiveRoot, "manifest.json", false), preparationToken);
            if (CanonicalJson.Sha256(manifestBytes) != manifestHash) throw new InvalidDataException("source_manifest_hash_mismatch");
            using var manifestDocument = CanonicalJson.Parse(manifestBytes);
            var manifest = manifestDocument.RootElement.Clone();
            ContractCatalog.Default.Validate("source-manifest", manifest);
            if (!manifestBytes.AsSpan().SequenceEqual(CanonicalJson.Serialize(manifest, true))) throw new InvalidDataException("source_manifest_not_canonical");
            var policy = index.GetProperty("collection_policy");
            if (CanonicalJson.Sha256(CanonicalJson.Serialize(policy, true)) != index.GetProperty("collection_policy_fingerprint").GetString())
                throw new InvalidDataException("collection_policy_hash_mismatch");
            var blobs = manifest.GetProperty("blobs").EnumerateArray().ToArray();
            if (blobs.Length > MaximumOpenBlobs) throw new IOException("archive_preparation_handle_limit");
            long total = 0;
            string? previousHash = null;
            var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var blob in blobs)
            {
                var hash = blob.GetProperty("hash").GetString()!;
                var length = blob.GetProperty("byte_length").GetInt64();
                if (previousHash is not null && string.CompareOrdinal(previousHash, hash) >= 0) throw new InvalidDataException("source_manifest_blob_order_or_duplicate");
                previousHash = hash;
                if (blob.GetProperty("storage_key").GetString() != "sha256/" + hash[..2] + "/" + hash) throw new InvalidDataException("source_blob_storage_key_mismatch");
                if (length > MaximumPreparationBytes - total) throw new IOException("archive_preparation_byte_limit");
                total += length;
                lengths.Add(hash, length);
            }
            var files = ValidateIndex(index, lengths);
            var buffer = new byte[HashBufferBytes];
            foreach (var blob in blobs)
            {
                Budget();
                var hash = blob.GetProperty("hash").GetString()!;
                var path = SourcePaths.Physical(archiveRoot, blob.GetProperty("storage_key").GetString()!, false);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferBytes,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
                handles.Add(hash, stream);
                peakHandles = Math.Max(peakHandles, handles.Count);
                if (!SourcePaths.Within(SourcePaths.HandlePath(stream, path), archiveRoot)) throw new IOException("archive_boundary_escape");
                if (stream.Length != lengths[hash]) throw new InvalidDataException("source_blob_length_mismatch");
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (true)
                {
                    Budget();
                    var count = await stream.ReadAsync(buffer, preparationToken);
                    if (count == 0) break;
                    verifiedBytes = checked(verifiedBytes + count);
                    if (verifiedBytes > MaximumPreparationBytes) throw new IOException("archive_preparation_byte_limit");
                    hasher.AppendData(buffer.AsSpan(0, count));
                }
                if (Convert.ToHexStringLower(hasher.GetHashAndReset()) != hash) throw new InvalidDataException("source_blob_hash_mismatch");
                stream.Position = 0;
            }
            Budget();
            var report = Report("passed", null);
            ContractCatalog.Default.Validate("source-archive-preparation", SourceDocuments.Element(report));
            return new(index, manifest, handles, files, report);
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var report = Report(cancelled ? "cancelled" : "failed", error is OperationCanceledException
                ? (cancelled ? "cancelled" : "archive_preparation_time_limit") : error.Message);
            foreach (var handle in handles.Values) await handle.DisposeAsync();
            throw new SourceArchiveException("Source archive preparation did not complete.", report, error);
        }
    }

    private static Dictionary<string, JsonElement> ValidateIndex(JsonElement index, Dictionary<string, long> lengths)
    {
        var contexts = index.GetProperty("context_ids").EnumerateArray().Select(value => value.GetString()!).ToArray();
        if (!contexts.SequenceEqual(contexts.Order(StringComparer.Ordinal))) throw new InvalidDataException("capture_context_order");
        var roots = index.GetProperty("roots").EnumerateArray().Select(value => value.GetProperty("logical_name").GetString()!).ToArray();
        if (roots.Distinct(StringComparer.Ordinal).Count() != roots.Length || !roots.SequenceEqual(roots.Order(SourceDocuments.Utf8Order)))
            throw new InvalidDataException("capture_root_order_or_duplicate");
        foreach (var root in roots) SourcePaths.RootName(root);
        var files = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var usedHashes = new HashSet<string>(StringComparer.Ordinal);
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        var logicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        foreach (var file in index.GetProperty("files").EnumerateArray())
        {
            var version = file.GetProperty("file_version_id").GetString()!;
            var path = SourcePaths.Relative(file.GetProperty("logical_path").GetString()!);
            var root = file.GetProperty("root_name").GetString()!;
            if (!roots.Contains(root) || !path.StartsWith(root + "/", StringComparison.Ordinal)) throw new InvalidDataException("capture_file_root_mismatch");
            if (previousPath is not null && SourceDocuments.Utf8Order.Compare(previousPath, path) >= 0) throw new InvalidDataException("capture_file_order");
            previousPath = path;
            if (!files.TryAdd(version, file) || !fileIds.Add(file.GetProperty("file_id").GetString()!) || !logicalPaths.Add(path))
                throw new InvalidDataException("capture_duplicate_file_identity_or_path");
            var available = file.GetProperty("collection_status").GetString() == "available";
            if (available)
            {
                var hash = file.GetProperty("content_hash").GetString()!;
                if (!lengths.TryGetValue(hash, out var length) || length != file.GetProperty("byte_length").GetInt64() || file.GetProperty("collection_reason").ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("capture_file_blob_mismatch");
                usedHashes.Add(hash);
            }
            else if (file.GetProperty("content_hash").ValueKind != JsonValueKind.Null || file.GetProperty("byte_length").ValueKind != JsonValueKind.Null ||
                string.IsNullOrWhiteSpace(file.GetProperty("collection_reason").GetString())) throw new InvalidDataException("unavailable_capture_has_fabricated_bytes");
            var encoding = file.GetProperty("encoding");
            var knownEncoding = encoding.GetProperty("state").GetString() == "known";
            var name = encoding.GetProperty("name").GetString();
            var bom = encoding.GetProperty("bom_bytes").ValueKind == JsonValueKind.Null ? (int?)null : encoding.GetProperty("bom_bytes").GetInt32();
            if (knownEncoding)
            {
                if (name is null || bom is null || (name == "utf-8" ? bom is not (0 or 3) : bom != 2) ||
                    encoding.GetProperty("reason").ValueKind != JsonValueKind.Null) throw new InvalidDataException("capture_encoding_state_mismatch");
            }
            else if (name is not null || string.IsNullOrWhiteSpace(encoding.GetProperty("reason").GetString()))
                throw new InvalidDataException("capture_encoding_unknown_without_reason");
            var decoding = file.GetProperty("decoding").GetString();
            if (decoding == "valid" && (!knownEncoding || !available || file.GetProperty("decoding_reason").ValueKind != JsonValueKind.Null))
                throw new InvalidDataException("capture_valid_decoding_without_encoding_or_bytes");
            if (decoding != "valid" && string.IsNullOrWhiteSpace(file.GetProperty("decoding_reason").GetString()))
                throw new InvalidDataException("capture_decoding_reason_missing");
            if (available && bom > file.GetProperty("byte_length").GetInt64()) throw new InvalidDataException("capture_bom_exceeds_file");
            var memberships = file.GetProperty("context_memberships").EnumerateArray().ToArray();
            var memberIds = memberships.Select(member => member.GetProperty("context_id").GetString()!).ToArray();
            if (memberIds.Distinct(StringComparer.Ordinal).Count() != memberIds.Length || memberIds.Any(member => !contexts.Contains(member)) ||
                !memberIds.SequenceEqual(memberIds.Order(StringComparer.Ordinal))) throw new InvalidDataException("capture_membership_context_mismatch");
            foreach (var member in memberships)
                if (member.GetProperty("selection").GetString() != "included" && string.IsNullOrWhiteSpace(member.GetProperty("reason").GetString()))
                    throw new InvalidDataException("capture_selection_reason_missing");
        }
        if (!usedHashes.SetEquals(lengths.Keys)) throw new InvalidDataException("source_manifest_has_unowned_blobs");
        foreach (var family in index.GetProperty("collection_policy").EnumerateObject())
            foreach (var supplied in family.Value.GetProperty("supplied_file_version_ids").EnumerateArray())
                if (!files.TryGetValue(supplied.GetString()!, out var file) || file.GetProperty("collection_status").GetString() != "available")
                    throw new InvalidDataException("collection_policy_evidence_unavailable");
        return files;
    }

    private (JsonElement File, FileStream Stream, string Hash) Resolve(string fileVersionId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!files.TryGetValue(fileVersionId, out var file)) throw new ArgumentException("File version is outside the captured index.");
        if (file.GetProperty("collection_status").GetString() != "available") throw new InvalidOperationException("Captured source bytes are unavailable.");
        var hash = file.GetProperty("content_hash").GetString()!;
        return (file, handles[hash], hash);
    }

    public async Task<SourceRead> ReadBytesAsync(string fileVersionId, long startByte, long endByte, long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        var (file, stream, hash) = Resolve(fileVersionId);
        var length = file.GetProperty("byte_length").GetInt64();
        if (startByte < 0 || endByte < startByte || endByte > length) throw new ArgumentOutOfRangeException(nameof(startByte));
        var count = endByte - startByte;
        if (maximumBytes < 0 || count > maximumBytes || count > int.MaxValue) throw new InvalidOperationException("source_read_budget_exceeded");
        var bytes = new byte[(int)count];
        var read = 0;
        while (read < bytes.Length)
        {
            var received = await RandomAccess.ReadAsync(stream.SafeFileHandle, bytes.AsMemory(read), startByte + read, cancellationToken);
            if (received == 0) throw new EndOfStreamException("Pinned source length changed unexpectedly.");
            Interlocked.Add(ref readBytes, received);
            read += received;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(fileVersionId, hash, startByte, endByte, bytes, read);
    }

    public async Task<SourceTextRead> ReadTextAsync(string fileVersionId, long startByte, long endByte, long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        var (file, _, hash) = Resolve(fileVersionId);
        if (file.GetProperty("decoding").GetString() != "valid") throw new InvalidDataException("Source text decoding is unavailable; exact bytes remain accessible.");
        var encoding = JsonSerializer.Deserialize<SourceEncoding>(file.GetProperty("encoding"), CanonicalJson.JsonOptions)!;
        var read = await ReadBytesAsync(fileVersionId, startByte, endByte, maximumBytes, cancellationToken);
        long bytesRead = read.BytesRead;
        if (startByte == endByte)
        {
            var length = file.GetProperty("byte_length").GetInt64();
            var bom = encoding.BomBytes!.Value;
            if (startByte > 0 && startByte < bom) throw new InvalidDataException("Empty interval is inside the BOM.");
            if (startByte != 0 && startByte != bom && startByte != length)
            {
                var width = encoding.Name == "utf-8" ? 1 : 2;
                if (width == 2 && (startByte - bom) % 2 != 0) throw new InvalidDataException("Empty interval is inside a UTF-16 code unit.");
                var look = await ReadBytesAsync(fileVersionId, startByte, startByte + width, maximumBytes, cancellationToken);
                bytesRead += look.BytesRead;
                if (width == 1 && (look.Bytes[0] & 0xc0) == 0x80) throw new InvalidDataException("Empty interval is inside a UTF-8 scalar.");
                if (width == 2)
                {
                    var unit = encoding.Name == "utf-16le" ? look.Bytes[0] | look.Bytes[1] << 8 : look.Bytes[0] << 8 | look.Bytes[1];
                    if (unit is >= 0xdc00 and <= 0xdfff) throw new InvalidDataException("Empty interval is inside a surrogate pair.");
                }
            }
        }
        return new(fileVersionId, hash, startByte, endByte, SourceDecoder.DecodeRange(read.Bytes, encoding, startByte), encoding, bytesRead);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        foreach (var handle in handles.Values) await handle.DisposeAsync();
    }
}

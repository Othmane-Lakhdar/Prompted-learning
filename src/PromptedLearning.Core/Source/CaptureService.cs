using System.Security.Cryptography;
using System.Text.Json;

namespace PromptedLearning.Core;

/// <summary>Captures an explicit evaluated input set. Project/glob evaluation belongs to the host.</summary>
public sealed class CaptureService(Action<CaptureAttempt>? onAttempt = null)
{
    private sealed record Pending(CaptureInput Input, string OriginalPath, IReadOnlyList<string> Aliases,
        CapturedBlob? Blob, string? Failure, SourceDecoding Decode, int Attempts);

    public async Task<CapturedInputs> CaptureAsync(CapturePlan plan, string archiveRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.RepositoryIdentity);
        if (plan.MaxDecodeBytes < 0 || plan.MaxDecodeBytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(plan));
        archiveRoot = Path.GetFullPath(archiveRoot);
        if (File.Exists(Path.Combine(archiveRoot, "capture-index.json")) || File.Exists(Path.Combine(archiveRoot, "manifest.json")))
            throw new InvalidOperationException("A sealed capture cannot be extended or overwritten.");
        var roots = plan.Roots.OrderBy(root => root.LogicalName, SourceDocuments.Utf8Order).ToArray();
        if (roots.Length == 0 || roots.Select(root => root.LogicalName).Distinct(StringComparer.Ordinal).Count() != roots.Length)
            throw new ArgumentException("Capture requires unique logical roots.");
        foreach (var root in roots)
        {
            SourcePaths.RootName(root.LogicalName);
            if (root.Boundary is not ("internal" or "external" or "vendor")) throw new ArgumentException("Unknown source boundary.");
            if ((root.Exclusions ?? []).Select(exclusion => (exclusion.Rule, exclusion.Reason)).Distinct().Count() != (root.Exclusions?.Count ?? 0))
                throw new ArgumentException("Duplicate exclusion definition.");
            foreach (var exclusion in root.Exclusions ?? [])
                if (string.IsNullOrWhiteSpace(exclusion.Rule) || string.IsNullOrWhiteSpace(exclusion.Reason))
                    throw new ArgumentException("Exclusion rules require text and a reason.");
        }
        var rootMap = roots.ToDictionary(root => root.LogicalName, StringComparer.Ordinal);
        var inputs = plan.Inputs.OrderBy(input => input.RootName + "/" + input.RelativePath, SourceDocuments.Utf8Order).ToArray();
        var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (!rootMap.ContainsKey(input.RootName)) throw new ArgumentException("Input references an undeclared root.");
            SourcePaths.Relative(input.RelativePath);
            if (!names.Add(input.RootName + "/" + input.RelativePath)) throw new ArgumentException("Input paths collide under filesystem case rules.");
            if (input.VirtualBytes is not null && input.Origin != "generated") throw new ArgumentException("Virtual bytes require generated origin.");
        }
        Directory.CreateDirectory(archiveRoot);
        var pending = new List<Pending>();
        var diagnostics = new List<CaptureDiagnostic>();
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await CaptureOne(input, rootMap[input.RootName], archiveRoot, plan.MaxDecodeBytes, cancellationToken);
            pending.Add(item);
            if (item.Failure is not null) diagnostics.Add(new(item.Failure, input.RootName + "/" + input.RelativePath, null, "Input bytes were not captured."));
            if (item.Decode.Reason is not null && !input.Binary && item.Blob is not null)
                diagnostics.Add(new(item.Decode.Reason, input.RootName + "/" + input.RelativePath, item.Decode.ErrorByte, "Bytes retained; strict source decoding is unavailable."));
        }
        object RootKey(CaptureRoot root) => new { root_key = root.LogicalName, boundary = root.Boundary,
            exclusions = (root.Exclusions ?? []).Select(exclusion => new { rule = exclusion.Rule, reason = exclusion.Reason }).ToArray() };
        var snapshotComponents = new { repository_identity = plan.RepositoryIdentity,
            roots = roots.Select(RootKey).ToArray(),
            files = pending.Select(item => new { root_key = item.Input.RootName, relative_path = item.Input.RelativePath,
                content_hash = item.Blob?.Hash, availability = item.Blob is null ? "unavailable" : "available", origin = item.Input.Origin }).ToArray() };
        var snapshotId = StableId.Create("snapshot/1", snapshotComponents);
        var identities = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        { [snapshotId] = SourceDocuments.Identity("snapshot/1", snapshotComponents) };
        var capturedRoots = roots.Select(root =>
        {
            var components = new { snapshot_id = snapshotId, root = RootKey(root) };
            var id = StableId.Create("source_root/1", components);
            identities.Add(id, SourceDocuments.Identity("source_root/1", components));
            return new CapturedRoot(id, root.LogicalName, root.PhysicalPath is null ? null : Path.GetFullPath(root.PhysicalPath),
                root.PhysicalPath is null ? "virtual" : "physical", root.Boundary, Array.AsReadOnly((root.Exclusions ?? []).ToArray()));
        }).ToArray();
        var capturedRootMap = capturedRoots.ToDictionary(root => root.LogicalName, StringComparer.Ordinal);
        var files = pending.Select(item =>
        {
            var fileComponents = new { snapshot_id = snapshotId, root_key = item.Input.RootName, relative_path = item.Input.RelativePath };
            var fileId = StableId.Create("source_file/1", fileComponents);
            var versionComponents = new { file_id = fileId, content_hash = item.Blob?.Hash,
                availability = item.Blob is null ? "unavailable" : "available", unavailable_reason = item.Failure };
            var versionId = StableId.Create("file_version/1", versionComponents);
            identities.Add(fileId, SourceDocuments.Identity("source_file/1", fileComponents));
            identities.Add(versionId, SourceDocuments.Identity("file_version/1", versionComponents));
            return new CapturedFile(fileId, capturedRootMap[item.Input.RootName].Id, versionId, item.Input.RootName,
                item.Input.RelativePath, item.Input.RootName + "/" + item.Input.RelativePath, item.OriginalPath,
                item.Aliases, item.Input.Role, item.Input.Language, item.Input.Origin, item.Blob?.Hash, item.Blob?.ByteLength,
                item.Blob is null ? "unavailable" : "available", item.Failure, item.Decode.Encoding,
                item.Decode.Status, item.Decode.Reason, item.Decode.ErrorByte, item.Attempts);
        }).ToArray();
        foreach (var identity in identities.Values) ContractCatalog.Default.Validate("identity-input", identity);
        return new(archiveRoot, snapshotId, Array.AsReadOnly(capturedRoots), Array.AsReadOnly(files),
            Array.AsReadOnly(pending.Where(item => item.Blob is not null).Select(item => item.Blob!).DistinctBy(blob => blob.Hash).OrderBy(blob => blob.Hash, StringComparer.Ordinal).ToArray()),
            SourceDocuments.ReadOnly(identities), Array.AsReadOnly(diagnostics.ToArray()));
    }

    private async Task<Pending> CaptureOne(CaptureInput input, CaptureRoot root, string archiveRoot, long maxDecodeBytes, CancellationToken cancellationToken)
    {
        var original = root.PhysicalPath is null ? "virtual:" + input.RootName + "/" + input.RelativePath
            : Path.GetFullPath(Path.Combine(root.PhysicalPath, input.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        Pending Unavailable(string reason, int attempts, bool intentionallySkipped = false) => new(input, original, Array.Empty<string>(), null, reason,
            new(new(intentionallySkipped ? "not_collected" : "unknown", null, null, reason), "not_attempted", reason, null, null), attempts);
        if (input.DoNotCaptureReason is not null)
        {
            if (string.IsNullOrWhiteSpace(input.DoNotCaptureReason)) throw new ArgumentException("No-capture reason is empty.");
            return Unavailable(input.DoNotCaptureReason, 0, true);
        }
        if (input.VirtualBytes is null && root.PhysicalPath is null) return Unavailable("virtual_bytes_unavailable", 0);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporary = Path.Combine(archiveRoot, ".capture-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                string? resolved = null;
                if (input.VirtualBytes is not null)
                {
                    onAttempt?.Invoke(new(input.RootName + "/" + input.RelativePath, attempt));
                    await File.WriteAllBytesAsync(temporary, input.VirtualBytes.ToArray(), cancellationToken);
                }
                else
                {
                    resolved = SourcePaths.Physical(root.PhysicalPath!, input.RelativePath, root.AllowReparsePoints);
                    var before = new FileInfo(resolved);
                    var beforeLength = before.Length;
                    var beforeTime = before.LastWriteTimeUtc;
                    onAttempt?.Invoke(new(input.RootName + "/" + input.RelativePath, attempt));
                    await using var source = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var actual = SourcePaths.HandlePath(source, resolved);
                    if (!SourcePaths.Within(actual, root.PhysicalPath!)) throw new IOException("reparse_boundary_escape");
                    resolved = actual;
                    await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                        await source.CopyToAsync(target, 65536, cancellationToken);
                    var after = new FileInfo(resolved);
                    if (beforeLength != source.Length || beforeTime != after.LastWriteTimeUtc || beforeLength != after.Length)
                    {
                        if (attempt == 2) return Unavailable("input_changed_during_capture", attempt);
                        continue;
                    }
                }
                string hash;
                long length;
                await using (var saved = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
                {
                    length = saved.Length;
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(saved, cancellationToken));
                }
                var key = "sha256/" + hash[..2] + "/" + hash;
                var destination = SourcePaths.Physical(archiveRoot, key, false);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                destination = SourcePaths.Physical(archiveRoot, key, false);
                if (File.Exists(destination))
                {
                    await using var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                    if (existing.Length != length || Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, cancellationToken)) != hash)
                        throw new InvalidDataException("Existing content-addressed blob is corrupt.");
                }
                else File.Move(temporary, destination);
                SourceDecoding decode;
                if (input.Binary) decode = SourceDecoder.Decode([], true);
                else if (length > maxDecodeBytes) decode = new(new("not_collected", null, null, "source_decoding_size_limit"), "not_attempted", "source_decoding_size_limit", null, null);
                else
                {
                    var decodeBytes = await File.ReadAllBytesAsync(destination, cancellationToken);
                    if (decodeBytes.LongLength != length || CanonicalJson.Sha256(decodeBytes) != hash)
                        throw new InvalidDataException("Blob changed between capture and strict decoding.");
                    decode = SourceDecoder.Decode(decodeBytes);
                }
                var aliases = resolved is not null && !string.Equals(resolved, original, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    ? new[] { resolved } : Array.Empty<string>();
                return new(input, original, Array.AsReadOnly(aliases), new(hash, length, key), null, decode, attempt);
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var reason = error.Message is "reparse_boundary_escape" or "source_boundary_escape" or "reparse_not_authorized" or "reparse_root_not_supported"
                    ? error.Message : "source_unavailable";
                if (attempt == 2 || reason != "source_unavailable") return Unavailable(reason, attempt);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        throw new InvalidOperationException("Capture retry state is invalid.");
    }

    public async Task<SealedCapture> SealAsync(CapturedInputs inputs, CaptureBindings bindings, CancellationToken cancellationToken = default)
    {
        ValidateCapturedInputs(inputs);
        if (bindings.SnapshotId != inputs.SnapshotId) throw new InvalidDataException("Snapshot binding mismatch.");
        if (bindings.ContextIds.Count == 0 || bindings.ContextIds.Distinct(StringComparer.Ordinal).Count() != bindings.ContextIds.Count)
            throw new InvalidDataException("Capture requires unique selected contexts.");
        if (!bindings.MembershipsByLogicalPath.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(inputs.Files.Select(file => file.LogicalPath)))
            throw new InvalidDataException("Context membership must cover exactly the captured file descriptors.");
        foreach (var pair in inputs.IdentityInputs)
        {
            var domain = pair.Value.GetProperty("domain").GetString()!;
            if (StableId.Create(domain, pair.Value.GetProperty("components")) != pair.Key) throw new InvalidDataException("Capture identity preimage mismatch.");
        }
        var manifest = SourceDocuments.Element(new { schema = "source-manifest/1", blobs = inputs.Blobs.Select(blob => new { hash = blob.Hash, byte_length = blob.ByteLength, storage_key = blob.StorageKey }).ToArray() });
        ContractCatalog.Default.Validate("source-manifest", manifest);
        var manifestBytes = CanonicalJson.Serialize(manifest, true);
        var manifestHash = CanonicalJson.Sha256(manifestBytes);
        var emptyPolicy = new { requested = false, supplied_file_version_ids = Array.Empty<string>(), collection_state = "not_collected", reason = "not_requested_and_no_supplied_evidence" };
        var policy = new { build_observation = emptyPolicy, artifact_inspection = emptyPolicy, runtime_observation = emptyPolicy };
        var index = SourceDocuments.Element(new { schema = "capture-index/1", serializer_version = "pl-jsonl/1", snapshot_id = inputs.SnapshotId,
            source_manifest_hash = manifestHash, context_ids = bindings.ContextIds.Order(StringComparer.Ordinal).ToArray(),
            roots = inputs.Roots.Select(root => new { logical_name = root.LogicalName, boundary = root.Boundary, exclusion_rules = root.Exclusions }).ToArray(),
            files = inputs.Files.Select(file =>
            {
                var members = bindings.MembershipsByLogicalPath[file.LogicalPath];
                if (members.Count == 0 || members.Select(member => member.ContextId).Distinct(StringComparer.Ordinal).Count() != members.Count ||
                    members.Any(member => !bindings.ContextIds.Contains(member.ContextId) || member.Selection is not ("included" or "excluded" or "undetermined") ||
                        (member.Selection != "included" && string.IsNullOrWhiteSpace(member.Reason))))
                    throw new InvalidDataException("Invalid context membership or missing selection reason.");
                return new { file_id = file.Id, file_version_id = file.FileVersionId, root_name = file.RootName, logical_path = file.LogicalPath,
                    role = file.Role, language = file.Language, origin = file.Origin, content_hash = file.ContentHash, byte_length = file.ByteLength,
                    collection_status = file.CollectionStatus, collection_reason = file.CollectionReason, encoding = file.Encoding,
                    decoding = file.Decoding, decoding_reason = file.DecodingReason, context_memberships = members.OrderBy(member => member.ContextId, StringComparer.Ordinal).ToArray() };
            }).ToArray(), collection_policy = policy, collection_policy_fingerprint = CanonicalJson.Sha256(CanonicalJson.Serialize(policy, true)) });
        ContractCatalog.Default.Validate("capture-index", index);
        var handles = new List<FileStream>();
        try
        {
            foreach (var blob in inputs.Blobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var handle = new FileStream(SourcePaths.Physical(inputs.ArchiveRoot, blob.StorageKey, false), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                handles.Add(handle);
                if (!SourcePaths.Within(SourcePaths.HandlePath(handle, Path.Combine(inputs.ArchiveRoot, blob.StorageKey)), inputs.ArchiveRoot))
                    throw new IOException("archive_boundary_escape");
                if (handle.Length != blob.ByteLength || Convert.ToHexStringLower(await SHA256.HashDataAsync(handle, cancellationToken)) != blob.Hash)
                    throw new InvalidDataException("Blob verification failed before capture seal.");
                var decodedFiles = inputs.Files.Where(file => file.ContentHash == blob.Hash && file.Decoding is "valid" or "invalid").ToArray();
                if (decodedFiles.Length > 0)
                {
                    if (handle.Length > int.MaxValue) throw new InvalidDataException("Decoded source exceeds the supported offset-map size.");
                    handle.Position = 0;
                    var verifiedBytes = new byte[checked((int)handle.Length)];
                    await handle.ReadExactlyAsync(verifiedBytes, cancellationToken);
                    var verifiedDecoding = SourceDecoder.Decode(verifiedBytes);
                    if (decodedFiles.Any(file => file.Encoding != verifiedDecoding.Encoding || file.Decoding != verifiedDecoding.Status ||
                        file.DecodingReason != verifiedDecoding.Reason || file.DecodingErrorByte != verifiedDecoding.ErrorByte))
                        throw new InvalidDataException("Capture decoding metadata does not match the pinned original bytes.");
                }
            }
            var indexBytes = CanonicalJson.Serialize(index, true);
            await WriteNew(Path.Combine(inputs.ArchiveRoot, "manifest.json"), manifestBytes, cancellationToken);
            await WriteNew(Path.Combine(inputs.ArchiveRoot, "capture-index.json"), indexBytes, cancellationToken);
            return new(inputs.ArchiveRoot, inputs.SnapshotId, CanonicalJson.Sha256(indexBytes), manifestHash, index, manifest, inputs);
        }
        finally { foreach (var handle in handles) await handle.DisposeAsync(); }
    }

    private static void ValidateCapturedInputs(CapturedInputs inputs)
    {
        var expectedIds = new HashSet<string>(StringComparer.Ordinal);
        void Identity(string id, string domain, object components)
        {
            if (!expectedIds.Add(id) || StableId.Create(domain, components) != id ||
                !inputs.IdentityInputs.TryGetValue(id, out var envelope) ||
                !CanonicalJson.Serialize(envelope).AsSpan().SequenceEqual(CanonicalJson.Serialize(SourceDocuments.Identity(domain, components))))
                throw new InvalidDataException("Capture descriptors do not match their identity preimages.");
        }
        if (!inputs.IdentityInputs.TryGetValue(inputs.SnapshotId, out var snapshot)) throw new InvalidDataException("Snapshot identity is absent.");
        object RootKey(CapturedRoot root) => new { root_key = root.LogicalName, boundary = root.Boundary,
            exclusions = root.Exclusions.Select(exclusion => new { rule = exclusion.Rule, reason = exclusion.Reason }).ToArray() };
        if (!inputs.Roots.Select(root => root.LogicalName).SequenceEqual(inputs.Roots.Select(root => root.LogicalName).Order(SourceDocuments.Utf8Order)) ||
            !inputs.Files.Select(file => file.LogicalPath).SequenceEqual(inputs.Files.Select(file => file.LogicalPath).Order(SourceDocuments.Utf8Order)))
            throw new InvalidDataException("Capture descriptor order is not canonical.");
        Identity(inputs.SnapshotId, "snapshot/1", new { repository_identity = snapshot.GetProperty("components").GetProperty("repository_identity").GetString(),
            roots = inputs.Roots.Select(RootKey).ToArray(), files = inputs.Files.Select(file => new { root_key = file.RootName,
                relative_path = file.RelativePath, content_hash = file.ContentHash, availability = file.CollectionStatus, origin = file.Origin }).ToArray() });
        var rootMap = inputs.Roots.ToDictionary(root => root.LogicalName, StringComparer.Ordinal);
        foreach (var root in inputs.Roots) Identity(root.Id, "source_root/1", new { snapshot_id = inputs.SnapshotId, root = RootKey(root) });
        var blobs = inputs.Blobs.ToDictionary(blob => blob.Hash, StringComparer.Ordinal);
        var usedBlobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in inputs.Files)
        {
            if (!rootMap.TryGetValue(file.RootName, out var root) || root.Id != file.RootId || file.LogicalPath != file.RootName + "/" + SourcePaths.Relative(file.RelativePath))
                throw new InvalidDataException("Captured file root/path mismatch.");
            Identity(file.Id, "source_file/1", new { snapshot_id = inputs.SnapshotId, root_key = file.RootName, relative_path = file.RelativePath });
            Identity(file.FileVersionId, "file_version/1", new { file_id = file.Id, content_hash = file.ContentHash,
                availability = file.CollectionStatus, unavailable_reason = file.CollectionReason });
            if (file.CollectionStatus == "available")
            {
                if (file.ContentHash is null || !blobs.TryGetValue(file.ContentHash, out var blob) || blob.ByteLength != file.ByteLength ||
                    blob.StorageKey != "sha256/" + blob.Hash[..2] + "/" + blob.Hash || file.CollectionReason is not null)
                    throw new InvalidDataException("Captured file bytes do not match the blob manifest.");
                usedBlobs.Add(file.ContentHash);
            }
            else if (file.CollectionStatus != "unavailable" || file.ContentHash is not null || file.ByteLength is not null || string.IsNullOrWhiteSpace(file.CollectionReason))
                throw new InvalidDataException("Unavailable source has fabricated bytes or lacks a reason.");
        }
        if (!expectedIds.SetEquals(inputs.IdentityInputs.Keys) || !usedBlobs.SetEquals(blobs.Keys)) throw new InvalidDataException("Unowned capture identity or blob.");
    }

    private static async Task WriteNew(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using PromptedLearning.Core;

namespace PromptedLearning.Tests;

public static class SourceTests
{
    private static string Folder(TestContext context)
    {
        var path = Path.Combine(context.ScratchRoot, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static CaptureBindings Bind(CapturedInputs inputs) => new(inputs.SnapshotId, ["build_context:" + new string('a', 64)],
        inputs.Files.ToDictionary(file => file.LogicalPath, _ => (IReadOnlyList<ContextMembership>)[new("build_context:" + new string('a', 64))]));
    private static async Task<SealedCapture> Capture(TestContext context, params (string Path, byte[] Bytes)[] files)
    {
        var folder = Folder(context);
        var input = Path.Combine(folder, "input");
        Directory.CreateDirectory(input);
        foreach (var file in files)
        {
            var path = Path.Combine(input, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, file.Bytes);
        }
        var service = new CaptureService();
        var captured = await service.CaptureAsync(new("tests", [new("root", input)], files.Select(file => new CaptureInput("root", file.Path)).ToArray()), Path.Combine(folder, "sources"));
        return await service.SealAsync(captured, Bind(captured));
    }
    private static string BlobPath(SealedCapture capture, CapturedBlob blob) => Path.Combine(capture.ArchiveRoot, blob.StorageKey.Replace('/', Path.DirectorySeparatorChar));

    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        yield return TestCase.Sync("source.strict_encoding_offsets_and_original_line_endings", () =>
        {
            const string text = "Aé😀\r\nZ";
            foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
            {
                var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
                var decoded = SourceDecoder.Decode(bytes);
                Check.Equal("valid", decoded.Status);
                Check.Equal(text, decoded.Source!.Text);
                foreach (var position in new[] { 0, 1, 2, 4, 5, 6, 7 })
                    Check.Equal((long)(encoding.GetPreamble().Length + encoding.GetByteCount(text.AsSpan(0, position))), decoded.Source.ByteOffset(position));
                Check.Throws<ArgumentOutOfRangeException>(() => decoded.Source.ByteOffset(3));
                Check.Throws<ArgumentOutOfRangeException>(() => decoded.Source.ByteOffset(-1));
                Check.Throws<ArgumentOutOfRangeException>(() => decoded.Source.ByteOffset(8));
            }
        });
        yield return TestCase.Sync("source.malformed_and_utf32_remain_explicit", () =>
        {
            var malformed = SourceDecoder.Decode(new byte[] { 0xc3, 0x28 });
            Check.Equal("invalid", malformed.Status);
            Check.Equal(0L, malformed.ErrorByte);
            Check.True(malformed.Source is null);
            Check.Equal("invalid", SourceDecoder.Decode(new byte[] { 0xff, 0xfe, 0x00, 0xd8 }).Status);
            foreach (var bytes in new[] { new byte[] { 0xff, 0xfe, 0, 0, 0x41, 0, 0, 0 }, new byte[] { 0, 0, 0xfe, 0xff, 0, 0, 0, 0x41 } })
            {
                var unsupported = SourceDecoder.Decode(bytes);
                Check.Equal("unsupported_encoding_utf32", unsupported.Reason);
                Check.Equal(4, unsupported.Encoding.BomBytes);
            }
        });
        yield return new("source.same_bytes_distinct_files_and_portable_snapshot", async () =>
        {
            var first = await Capture(context, ("a.cs", Encoding.UTF8.GetBytes("same")), ("b.cs", Encoding.UTF8.GetBytes("same")));
            var second = await Capture(context, ("a.cs", Encoding.UTF8.GetBytes("same")), ("b.cs", Encoding.UTF8.GetBytes("same")));
            Check.Equal(1, first.Inputs.Blobs.Count);
            Check.True(first.Inputs.Files[0].Id != first.Inputs.Files[1].Id);
            Check.True(first.Inputs.Files[0].FileVersionId != first.Inputs.Files[1].FileVersionId);
            Check.Equal(first.SnapshotId, second.SnapshotId);
            Check.Equal(first.IndexFingerprint, second.IndexFingerprint);
            foreach (var pair in first.Inputs.IdentityInputs) StableId.Verify(pair.Key, pair.Value);
        });
        yield return new("source.archive_ignores_changed_working_files_and_pins_blobs", async () =>
        {
            var capture = await Capture(context, ("a.cs", Encoding.UTF8.GetBytes("original\r\n")));
            await File.WriteAllTextAsync(capture.Inputs.Files[0].OriginalPath, "changed");
            var blob = BlobPath(capture, capture.Inputs.Blobs[0]);
            await using (var archive = await SourceArchive.OpenAsync(capture.ArchiveRoot, capture.IndexFingerprint))
            {
                var result = await archive.ReadTextAsync(capture.Inputs.Files[0].FileVersionId, 0, 10, 10);
                Check.Equal("original\r\n", result.Text);
                Check.Equal(10L, result.BytesRead);
                Check.Equal(10L, archive.BytesRead);
                Check.Equal(10L, archive.Preparation.VerifiedBytes);
                Check.Throws<IOException>(() => File.WriteAllText(blob, "tampered!!"));
                Check.Throws<IOException>(() => File.Move(blob, blob + ".replacement"));
            }
            using var writable = new FileStream(blob, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check.Equal(10L, writable.Length);
        });
        yield return new("source.exact_text_ranges_and_empty_scalar_boundaries", async () =>
        {
            var capture = await Capture(context, ("a.cs", Encoding.UTF8.GetBytes("a😀b")));
            await using var archive = await SourceArchive.OpenAsync(capture.ArchiveRoot, capture.IndexFingerprint);
            var id = capture.Inputs.Files[0].FileVersionId;
            Check.Equal("😀", (await archive.ReadTextAsync(id, 1, 5, 4)).Text);
            await Check.ThrowsAsync<InvalidOperationException>(() => archive.ReadBytesAsync(id, 1, 5, 3));
            await Check.ThrowsAsync<InvalidDataException>(() => archive.ReadTextAsync(id, 2, 2, 1));
            Check.Equal(5L, archive.BytesRead);
            var empty = await archive.ReadTextAsync(id, 1, 1, 1);
            Check.Equal("", empty.Text);
            Check.Equal(1L, empty.BytesRead);
            await Check.ThrowsAsync<InvalidOperationException>(() => archive.ReadTextAsync(id, 1, 1, 0));
            await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => archive.ReadBytesAsync(id, -1, 0, 1));
            Check.Equal(0L, (await archive.ReadTextAsync(id, 6, 6, 0)).BytesRead);
            await Check.ThrowsAsync<ArgumentException>(() => archive.ReadBytesAsync("file_version:unknown", 0, 0, 0));
        });
        yield return new("source.utf16_bom_and_surrogate_empty_ranges", async () =>
        {
            foreach (var encoding in new[] { new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
            {
                var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("a😀b")).ToArray();
                var capture = await Capture(context, ("a.cs", bytes));
                await using var archive = await SourceArchive.OpenAsync(capture.ArchiveRoot, capture.IndexFingerprint);
                var id = capture.Inputs.Files[0].FileVersionId;
                Check.Equal("a😀b", (await archive.ReadTextAsync(id, 0, bytes.Length, bytes.Length)).Text);
                Check.Equal("😀", (await archive.ReadTextAsync(id, 4, 8, 4)).Text);
                await Check.ThrowsAsync<InvalidDataException>(() => archive.ReadTextAsync(id, 1, 1, 2));
                await Check.ThrowsAsync<InvalidDataException>(() => archive.ReadTextAsync(id, 5, 5, 2));
                await Check.ThrowsAsync<InvalidDataException>(() => archive.ReadTextAsync(id, 6, 6, 2));
                Check.Equal("", (await archive.ReadTextAsync(id, 2, 2, 0)).Text);
            }
        });
        yield return new("source.unavailable_malformed_and_decode_limit_keep_truthful_states", async () =>
        {
            var folder = Folder(context);
            await File.WriteAllBytesAsync(Path.Combine(folder, "bad.cs"), new byte[] { 0xc3, 0x28 });
            await File.WriteAllBytesAsync(Path.Combine(folder, "utf32.cs"), new byte[] { 0xff, 0xfe, 0, 0, 0x41, 0, 0, 0 });
            var service = new CaptureService();
            var inputs = await service.CaptureAsync(new("tests", [new("root", folder)], [new("root", "bad.cs"), new("root", "missing.cs"), new("root", "utf32.cs")]), Path.Combine(folder, "archive"));
            Check.Equal("available", inputs.Files[0].CollectionStatus);
            Check.Equal("invalid", inputs.Files[0].Decoding);
            Check.Equal("unavailable", inputs.Files[1].CollectionStatus);
            Check.True(inputs.Files[1].ContentHash is null && inputs.Files[1].ByteLength is null);
            Check.Equal("unsupported_encoding_utf32", inputs.Files[2].DecodingReason);
            var sealedCapture = await service.SealAsync(inputs, Bind(inputs));
            await using var archive = await SourceArchive.OpenAsync(sealedCapture.ArchiveRoot, sealedCapture.IndexFingerprint);
            var raw = await archive.ReadBytesAsync(inputs.Files[0].FileVersionId, 0, 2, 2);
            Check.True(raw.Bytes.SequenceEqual(new byte[] { 0xc3, 0x28 }));
            await Check.ThrowsAsync<InvalidDataException>(() => archive.ReadTextAsync(inputs.Files[0].FileVersionId, 0, 2, 2));
            await Check.ThrowsAsync<InvalidOperationException>(() => archive.ReadBytesAsync(inputs.Files[1].FileVersionId, 0, 0, 0));
            var limited = await service.CaptureAsync(new("tests", [new("root", folder)], [new("root", "bad.cs")], MaxDecodeBytes: 0), Path.Combine(folder, "limited"));
            Check.Equal("source_decoding_size_limit", limited.Files[0].DecodingReason);
            Check.Equal("available", limited.Files[0].CollectionStatus);
        });
        yield return new("source.stable_read_retry_and_persistent_change", async () =>
        {
            var folder = Folder(context);
            var path = Path.Combine(folder, "a.cs");
            await File.WriteAllTextAsync(path, "a");
            var service = new CaptureService(attempt => { if (attempt.Attempt == 1) File.WriteAllText(path, "second-content"); });
            var inputs = await service.CaptureAsync(new("tests", [new("root", folder)], [new("root", "a.cs")]), Path.Combine(folder, "capture"));
            Check.Equal(2, inputs.Files[0].CaptureAttempts);
            Check.Equal("available", inputs.Files[0].CollectionStatus);
            var changing = new CaptureService(attempt => File.AppendAllText(path, "x"));
            var failed = await changing.CaptureAsync(new("tests", [new("root", folder)], [new("root", "a.cs")]), Path.Combine(folder, "changing"));
            Check.Equal("input_changed_during_capture", failed.Files[0].CollectionReason);
            Check.Equal(0, failed.Blobs.Count);
            Check.True(!Directory.EnumerateFiles(failed.ArchiveRoot, "*.tmp").Any());
        });
        yield return new("source.virtual_inputs_explicit_selection_and_new_snapshot", async () =>
        {
            var folder = Folder(context);
            var service = new CaptureService();
            var generated = new CaptureInput("generated", "output.cs", Origin: "generated", VirtualBytes: Encoding.UTF8.GetBytes("class A {}"));
            var excluded = new CaptureInput("generated", "excluded.cs", Origin: "generated", DoNotCaptureReason: "explicit_exclusion");
            var roots = new CaptureRoot[] { new("generated", null, Exclusions: [new("excluded.cs", "explicit_exclusion")]) };
            var first = await service.CaptureAsync(new("tests", roots, [generated, excluded]), Path.Combine(folder, "first"));
            var skipped = first.Files.Single(file => file.RelativePath == "excluded.cs");
            Check.Equal("not_collected", skipped.Encoding.State);
            Check.Equal(0, skipped.CaptureAttempts);
            var contextA = "build_context:" + new string('a', 64);
            var contextB = "build_context:" + new string('b', 64);
            var memberships = first.Files.ToDictionary(file => file.LogicalPath, file => (IReadOnlyList<ContextMembership>)
                [new(contextA, file == skipped ? "excluded" : "included", file == skipped ? "explicit_exclusion" : null), new(contextB, "excluded", "not_selected_in_context")]);
            var sealedFirst = await service.SealAsync(first, new(first.SnapshotId, [contextA, contextB], memberships));
            Check.True(!sealedFirst.Index.GetRawText().Contains(folder, StringComparison.Ordinal));
            Check.Equal("virtual", first.Roots[0].RootKind);
            var originalIndex = await File.ReadAllBytesAsync(Path.Combine(first.ArchiveRoot, "capture-index.json"));
            var second = await service.CaptureAsync(new("tests", roots, [generated, excluded,
                new("generated", "additional.cs", Origin: "generated", VirtualBytes: Encoding.UTF8.GetBytes("class B {}"))]), Path.Combine(folder, "second"));
            await service.SealAsync(second, new(second.SnapshotId, [contextA, contextB], second.Files.ToDictionary(file => file.LogicalPath,
                file => (IReadOnlyList<ContextMembership>)[new(contextA, file.RelativePath == "excluded.cs" ? "excluded" : "included", file.RelativePath == "excluded.cs" ? "explicit_exclusion" : null),
                    new(contextB, "excluded", "not_selected_in_context")])));
            Check.True(first.SnapshotId != second.SnapshotId);
            var retainedIndex = await File.ReadAllBytesAsync(Path.Combine(first.ArchiveRoot, "capture-index.json"));
            Check.True(originalIndex.SequenceEqual(retainedIndex));
        });
        yield return new("source.seal_rejects_forged_descriptors_and_in_place_extension", async () =>
        {
            var folder = Folder(context);
            await File.WriteAllTextAsync(Path.Combine(folder, "a.cs"), "text");
            var service = new CaptureService();
            var inputs = await service.CaptureAsync(new("tests", [new("root", folder)], [new("root", "a.cs")]), Path.Combine(folder, "archive"));
            var forged = inputs with { Files = [inputs.Files[0] with { LogicalPath = "root/forged.cs" }] };
            await Check.ThrowsAsync<InvalidDataException>(() => service.SealAsync(forged, Bind(forged)));
            var length = inputs with { Files = [inputs.Files[0] with { ByteLength = 77 }] };
            await Check.ThrowsAsync<InvalidDataException>(() => service.SealAsync(length, Bind(length)));
            var encoding = inputs with { Files = [inputs.Files[0] with { Encoding = new("known", "utf-16be", 2, null) }] };
            await Check.ThrowsAsync<InvalidDataException>(() => service.SealAsync(encoding, Bind(encoding)));
            var capture = await service.SealAsync(inputs, Bind(inputs));
            await Check.ThrowsAsync<IOException>(() => service.SealAsync(inputs, Bind(inputs)));
            await Check.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync(new("tests", [new("root", folder)], [new("root", "a.cs")]), capture.ArchiveRoot));
        });
        yield return new("source.hash_tamper_detected_and_failed_open_releases_handles", async () =>
        {
            var capture = await Capture(context, ("a.cs", Encoding.UTF8.GetBytes("first")), ("b.cs", Encoding.UTF8.GetBytes("second")));
            var lastBlob = capture.Inputs.Blobs[^1];
            await File.WriteAllBytesAsync(BlobPath(capture, lastBlob), Enumerable.Repeat((byte)'x', checked((int)lastBlob.ByteLength)).ToArray());
            var error = await Check.ThrowsAsync<SourceArchiveException>(() => SourceArchive.OpenAsync(capture.ArchiveRoot, capture.IndexFingerprint));
            Check.Equal("source_blob_hash_mismatch", error.Report.Reason);
            Check.Equal("failed", error.Report.Outcome);
            foreach (var blob in capture.Inputs.Blobs)
            {
                using var unlocked = new FileStream(BlobPath(capture, blob), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Check.Equal(blob.ByteLength, unlocked.Length);
            }
        });
        yield return new("source.cancellation_and_preparation_limits", async () =>
        {
            var capture = await Capture(context, ("a.cs", Encoding.UTF8.GetBytes("a")));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = await Check.ThrowsAsync<SourceArchiveException>(() => SourceArchive.OpenAsync(capture.ArchiveRoot, capture.IndexFingerprint, cancellation.Token));
            Check.Equal("cancelled", cancelled.Report.Outcome);
            Check.Equal(0L, cancelled.Report.VerifiedBytes);
            Check.True(cancelled.Report.SourceManifestHash is null);
            ContractCatalog.Default.Validate("source-archive-preparation", JsonSerializer.SerializeToElement(cancelled.Report, CanonicalJson.JsonOptions));
            var wrongIndex = await Check.ThrowsAsync<SourceArchiveException>(() => SourceArchive.OpenAsync(capture.ArchiveRoot, new string('f', 64)));
            Check.Equal("capture_index_hash_mismatch", wrongIndex.Report.Reason);
            Check.True(wrongIndex.Report.SourceManifestHash is null);
            Check.Equal(0L, wrongIndex.Report.VerifiedBytes);
            ContractCatalog.Default.Validate("source-archive-preparation", JsonSerializer.SerializeToElement(wrongIndex.Report, CanonicalJson.JsonOptions));
            var manifest = JsonNode.Parse(capture.Manifest.GetRawText())!.AsObject();
            manifest["blobs"]![0]!["byte_length"] = SourceArchive.MaximumPreparationBytes + 1;
            using var parsedManifest = CanonicalJson.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            var manifestBytes = CanonicalJson.Serialize(parsedManifest.RootElement, true);
            await File.WriteAllBytesAsync(Path.Combine(capture.ArchiveRoot, "manifest.json"), manifestBytes);
            var index = JsonNode.Parse(capture.Index.GetRawText())!.AsObject();
            index["source_manifest_hash"] = CanonicalJson.Sha256(manifestBytes);
            using var parsedIndex = CanonicalJson.Parse(Encoding.UTF8.GetBytes(index.ToJsonString()));
            var indexBytes = CanonicalJson.Serialize(parsedIndex.RootElement, true);
            await File.WriteAllBytesAsync(Path.Combine(capture.ArchiveRoot, "capture-index.json"), indexBytes);
            var limit = await Check.ThrowsAsync<SourceArchiveException>(() => SourceArchive.OpenAsync(capture.ArchiveRoot, CanonicalJson.Sha256(indexBytes)));
            Check.Equal("archive_preparation_byte_limit", limit.Report.Reason);
            Check.Equal(0, limit.Report.PeakOpenHandles);
            manifest["blobs"] = new JsonArray(Enumerable.Range(0, SourceArchive.MaximumOpenBlobs + 1).Select(number =>
            {
                var hash = number.ToString("x64");
                return (JsonNode)new JsonObject { ["hash"] = hash, ["byte_length"] = 0, ["storage_key"] = "sha256/" + hash[..2] + "/" + hash };
            }).ToArray());
            using var manyManifest = CanonicalJson.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            var manyBytes = CanonicalJson.Serialize(manyManifest.RootElement, true);
            await File.WriteAllBytesAsync(Path.Combine(capture.ArchiveRoot, "manifest.json"), manyBytes);
            index["source_manifest_hash"] = CanonicalJson.Sha256(manyBytes);
            using var manyIndex = CanonicalJson.Parse(Encoding.UTF8.GetBytes(index.ToJsonString()));
            var manyIndexBytes = CanonicalJson.Serialize(manyIndex.RootElement, true);
            await File.WriteAllBytesAsync(Path.Combine(capture.ArchiveRoot, "capture-index.json"), manyIndexBytes);
            var handles = await Check.ThrowsAsync<SourceArchiveException>(() => SourceArchive.OpenAsync(capture.ArchiveRoot, CanonicalJson.Sha256(manyIndexBytes)));
            Check.Equal("archive_preparation_handle_limit", handles.Report.Reason);
            Check.Equal(0, handles.Report.PeakOpenHandles);
        });
        yield return new("source.path_case_collisions_and_reparse_escape", async () =>
        {
            var folder = Folder(context);
            var input = Path.Combine(folder, "input");
            var outside = Path.Combine(folder, "outside");
            Directory.CreateDirectory(input);
            Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(outside, "a.cs"), "outside");
            // A directory junction exercises the same reparse-boundary rule without requiring
            // the symbolic-link privilege unavailable on the verified Windows test host.
            var command = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", Path.Combine(input, "escape"), outside }) command.ArgumentList.Add(argument);
            using (var process = Process.Start(command) ?? throw new IOException("Could not create junction fixture process."))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(deadline.Token); }
                catch { process.Kill(true); throw; }
                Check.True(process.ExitCode == 0, "Junction fixture failed: " + await output + await error);
            }
            var service = new CaptureService();
            var inputs = await service.CaptureAsync(new("tests", [new("root", input, AllowReparsePoints: true)], [new("root", "escape/a.cs")]), Path.Combine(folder, "archive"));
            Check.Equal("reparse_boundary_escape", inputs.Files[0].CollectionReason);
            Check.Equal(0, inputs.Blobs.Count);
            await Check.ThrowsAsync<ArgumentException>(() => service.CaptureAsync(new("tests", [new("root", input)], [new("root", "A.cs"), new("root", "a.cs")]), Path.Combine(folder, "collision")));
            await Check.ThrowsAsync<ArgumentException>(() => service.CaptureAsync(new("tests", [new("root", input)], [new("root", "../outside/a.cs")]), Path.Combine(folder, "traversal")));
        });
    }
}

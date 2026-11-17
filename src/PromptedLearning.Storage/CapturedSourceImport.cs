using System.Text;
using System.Text.Json;
using PromptedLearning.Core;

namespace PromptedLearning.Storage;

/// <summary>Imports a completed capture descriptor set; it never reads the moving source tree.</summary>
public static class CapturedSourceImport
{
    public static void ImportCapturedSources(this CanonicalWrite write, CapturedInputs capture,
        DateTimeOffset capturedUtc, string? vcsRevision = null, bool? localModificationsIncluded = null)
    {
        var snapshot = capture.IdentityInputs[capture.SnapshotId].GetProperty("components");
        var inputFingerprint = CanonicalJson.Sha256(CanonicalJson.Serialize(snapshot));
        var policyComponents = new
        {
            family = "evidence", scope = new { snapshot_id = capture.SnapshotId, context_id = (string?)null, context_independent = true },
            producer_contract = "source-capture/1", producer_version = "1", input_fingerprint = inputFingerprint,
            local_key = new { rule = "evidence-local-v1", parts = new object?[] { "policy", null, null, null, null, "capture:" + capture.SnapshotId, CanonicalJson.Sha256(CanonicalJson.Serialize("source_snapshot_capture")) } }
        };
        var evidenceId = StableId.Create("evidence/1", policyComponents);
        void Registry(string id, string kind, string section)
            => write.Insert("record", Row(("id", id), ("kind", kind), ("section", section), ("snapshot_id", capture.SnapshotId), ("context_id", null), ("context_independent", true), ("origin_span_id", null)));
        void State(string id, string table, string column, string state, string reason)
            => write.SetFieldState(id, table, [id], column, state, reason, evidenceId);
        void Evidence(string id)
        {
            write.Insert("record_evidence", Row(("record_id", id), ("evidence_id", evidenceId), ("role", "source_capture")));
            write.SetCollectionState(id, "record_evidence", "record_evidence", "known", null);
            write.SetCollectionState(id, "record_extension", "record_extension", "known", null);
        }

        write.Insert("snapshot", Row(("id", capture.SnapshotId), ("repository_identity", snapshot.GetProperty("repository_identity").GetString()),
            ("vcs_revision", vcsRevision), ("local_modifications_included", localModificationsIncluded),
            ("created_utc", capturedUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("boundary_policy", "explicit_capture_roots/1")));
        Registry(capture.SnapshotId, "snapshot", "BD0.1");
        write.AddIdentityInput(capture.SnapshotId, capture.IdentityInputs[capture.SnapshotId]);
        Registry(evidenceId, "evidence", "BD0.16");
        write.Insert("evidence", Row(("id", evidenceId), ("kind", "policy"), ("source_span_id", null), ("tool_id", null),
            ("build_run_id", null), ("blob_hash", null), ("locator", "capture:" + capture.SnapshotId), ("detail", "source_snapshot_capture")));
        write.AddIdentityInput(evidenceId, StableId.Envelope("evidence/1", policyComponents));
        foreach (var column in new[] { "source_span_id", "tool_id", "build_run_id", "blob_hash" })
            State(evidenceId, "evidence", column, "not_applicable", "capture_policy_has_no_" + column);
        if (vcsRevision is null) State(capture.SnapshotId, "snapshot", "vcs_revision", "not_collected", "vcs_revision_not_supplied");
        if (localModificationsIncluded is null) State(capture.SnapshotId, "snapshot", "local_modifications_included", "not_collected", "vcs_worktree_status_not_supplied");
        Evidence(capture.SnapshotId);
        write.SetCollectionState(evidenceId, "record_extension", "record_extension", "known", null);
        write.SetCollectionState(evidenceId, "record_evidence", "record_evidence", "known", null);
        write.SetCollectionState(capture.SnapshotId, "source_root", "roots", "known", null);

        foreach (var blob in capture.Blobs)
            write.AddSourceBlob(blob);
        foreach (var root in capture.Roots)
        {
            Registry(root.Id, "source_root", "BD0.1");
            write.Insert("source_root", Row(("id", root.Id), ("logical_name", root.LogicalName), ("original_path", root.OriginalPath ?? "virtual:" + root.LogicalName),
                ("root_kind", root.RootKind), ("boundary", root.Boundary), ("policy", Encoding.UTF8.GetString(CanonicalJson.Serialize(capture.IdentityInputs[root.Id].GetProperty("components").GetProperty("root"))))));
            write.AddIdentityInput(root.Id, capture.IdentityInputs[root.Id]); Evidence(root.Id);
            write.SetCollectionState(root.Id, "source_file", "files", "known", null);
            write.SetCollectionState(root.Id, "exclusion", "exclusions", "known", null);
            foreach (var exclusion in root.Exclusions)
            {
                var components = new
                {
                    family = "exclusion", scope = new { snapshot_id = capture.SnapshotId, context_id = (string?)null, context_independent = true },
                    producer_contract = "source-capture/1", producer_version = "1", input_fingerprint = inputFingerprint,
                    local_key = new { rule = "exclusion-local-v1", parts = new object?[] { root.Id, exclusion.Rule, exclusion.Reason, "excluded" } }
                };
                var id = StableId.Create("exclusion/1", components);
                Registry(id, "exclusion", "BD0.1");
                write.Insert("exclusion", Row(("id", id), ("root_id", root.Id), ("rule", exclusion.Rule), ("reason", exclusion.Reason), ("selection_state", "excluded"), ("matched_count", null)));
                write.AddIdentityInput(id, StableId.Envelope("exclusion/1", components));
                State(id, "exclusion", "matched_count", "not_collected", "rule_match_count_not_collected"); Evidence(id);
            }
        }
        foreach (var file in capture.Files)
        {
            var language = file.Language == "unknown" ? null : file.Language;
            Registry(file.Id, "source_file", "BD0.2");
            write.Insert("source_file", Row(("id", file.Id), ("root_id", file.RootId), ("relative_path", file.RelativePath), ("original_path", file.OriginalPath),
                ("name", file.RelativePath.Split('/')[^1]), ("extension", Path.GetExtension(file.RelativePath)), ("language", language), ("role", file.Role), ("origin", file.Origin), ("generation_id", null)));
            write.AddIdentityInput(file.Id, capture.IdentityInputs[file.Id]); Evidence(file.Id);
            if (language is null) State(file.Id, "source_file", "language", "unknown", "language_not_declared");
            if (file.Origin == "generated") State(file.Id, "source_file", "generation_id", "not_collected", "generator_evidence_not_supplied");
            write.SetCollectionState(file.Id, "file_version", "versions", "known", null);
            write.SetCollectionState(file.Id, "file_path", "file_path", "known", null);
            write.Insert("file_path", Row(("file_id", file.Id), ("path", file.OriginalPath), ("path_kind", file.OriginalPath.StartsWith("virtual:", StringComparison.Ordinal) ? "virtual" : "original")));
            foreach (var alias in file.Aliases.Where(a => a != file.OriginalPath).Distinct(StringComparer.Ordinal))
                write.Insert("file_path", Row(("file_id", file.Id), ("path", alias), ("path_kind", "alias")));
            Registry(file.FileVersionId, "file_version", "BD0.2");
            var virtualUri = file.OriginalPath.StartsWith("virtual:", StringComparison.Ordinal) ? file.OriginalPath : null;
            write.Insert("file_version", Row(("id", file.FileVersionId), ("file_id", file.Id), ("content_hash", file.ContentHash), ("byte_length", file.ByteLength),
                ("encoding", file.Encoding.Name), ("bom_bytes", file.Encoding.BomBytes), ("available", file.CollectionStatus == "available"), ("extraction_status", "not_attempted"), ("virtual_uri", virtualUri)));
            write.AddIdentityInput(file.FileVersionId, capture.IdentityInputs[file.FileVersionId]); Evidence(file.FileVersionId);
            var unavailableState = file.CaptureAttempts > 0 || file.CollectionReason == "virtual_bytes_unavailable" ? "unknown" : "not_collected";
            if (file.ContentHash is null) State(file.FileVersionId, "file_version", "content_hash", unavailableState, file.CollectionReason ?? "capture_unavailable");
            if (file.ByteLength is null) State(file.FileVersionId, "file_version", "byte_length", unavailableState, file.CollectionReason ?? "capture_unavailable");
            if (file.Encoding.Name is null) State(file.FileVersionId, "file_version", "encoding", file.Encoding.State, file.Encoding.Reason ?? "encoding_unavailable");
            if (file.Encoding.BomBytes is null) State(file.FileVersionId, "file_version", "bom_bytes", file.Encoding.State, file.Encoding.Reason ?? "encoding_unavailable");
            if (virtualUri is null) State(file.FileVersionId, "file_version", "virtual_uri", "not_applicable", "physical_file");
        }
    }

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] fields)
        => fields.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
}

using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PromptedLearning.Core;
using PromptedLearning.Storage;

namespace PromptedLearning.Tests;

public static class StorageTests
{
    public static IEnumerable<TestCase> Cases(TestContext context)
    {
        yield return TestCase.Sync("SQLite provider meets the fixed security minimum", () =>
        {
            using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False"); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "SELECT sqlite_version()";
            var version = (string)command.ExecuteScalar()!;
            Check.True(Version.Parse(version) >= new Version(3, 50, 2), "SQLite provider is older than 3.50.2: " + version);
            var reportDirectory = Path.Combine(context.ScratchRoot, "storage"); Directory.CreateDirectory(reportDirectory);
            File.WriteAllText(Path.Combine(reportDirectory, "sqlite-provider.json"), JsonSerializer.Serialize(new
            {
                native_sqlite_version = version, minimum_version = "3.50.2",
                managed_provider_assembly_version = typeof(SqliteConnection).Assembly.GetName().Version?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        });
        yield return new("Captured text, binary and unavailable bytes retain states and snapshot identity", async () =>
        {
            var capture = await Capture(context, "valid"); using var store = CanonicalStore.CreateInMemory();
            using var write = store.BeginWrite(); write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch);
            var result = write.CommitFoundation(); Check.True(result.IsValid); Check.True(result.ProducedRecords >= 9);
            Check.Equal(3, store.ReadRows("source_file").Count);
            var versions = store.ReadRows("file_version");
            Check.Equal(2, versions.Count(r => (long)r["available"]! == 1));
            Check.True(versions.Any(r => r["encoding"] is null && (long)r["available"]! == 1), "Binary availability was conflated with decoding.");
            Check.True(store.ReadRows("record").Any(r => (string)r["id"]! == capture.SnapshotId && (string)r["snapshot_id"]! == capture.SnapshotId));
            Check.True(store.ReadRows("field_state").Any(r => (string)r["record_id"]! == capture.SnapshotId && (string)r["state"]! == "not_collected"));
        });
        yield return new("Committed foundation survives reopen without finalization", async () =>
        {
            var capture = await Capture(context, "reopen"); var path = Path.Combine(NewDirectory(context, "db"), "index.sqlite");
            using (var store = CanonicalStore.Create(path))
            using (var write = store.BeginWrite()) { write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch); write.CommitFoundation(); }
            using var reopened = CanonicalStore.Open(path);
            Check.True(reopened.ValidateFoundation().IsValid); Check.Equal(3, reopened.ReadRows("source_file").Count);
            Check.Throws<IOException>(() => CanonicalStore.Create(path));
        });
        yield return new("Attempted unavailable capture remains unknown while deliberate omission is not collected", async () =>
        {
            var directory = NewDirectory(context, "unavailable"); var input = Path.Combine(directory, "input"); Directory.CreateDirectory(input);
            var capture = await new CaptureService().CaptureAsync(new("unavailable-test", [new("pilot", input)],
                [new("pilot", "attempted.cpp", Language: "cpp"), new("pilot", "skipped.cpp", Language: "cpp", DoNotCaptureReason: "not_requested")]), Path.Combine(directory, "sources"));
            using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite();
            write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch); write.CommitFoundation();
            var attempted = capture.Files.Single(f => f.RelativePath == "attempted.cpp");
            var skipped = capture.Files.Single(f => f.RelativePath == "skipped.cpp");
            Check.True(attempted.CaptureAttempts > 0); Check.Equal(0, skipped.CaptureAttempts);
            foreach (var column in new[] { "content_hash", "byte_length" })
            {
                string StateFor(string id) => (string)store.ReadRows("field_state").Single(r => (string)r["record_id"]! == id && ((string)r["field_path"]!).EndsWith("/" + column, StringComparison.Ordinal))["state"]!;
                Check.Equal("unknown", StateFor(attempted.FileVersionId)); Check.Equal("not_collected", StateFor(skipped.FileVersionId));
            }
        });
        yield return new("Reversed exclusion rule order remains importable and preserves declared policy", async () =>
        {
            var directory = NewDirectory(context, "exclusions"); var input = Path.Combine(directory, "input"); Directory.CreateDirectory(input);
            var capture = await new CaptureService().CaptureAsync(new("exclusion-test",
                [new("pilot", input, Exclusions: [new("z/**", "excluded_z"), new("a/**", "excluded_a")])], []), Path.Combine(directory, "sources"));
            using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite();
            write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch); write.CommitFoundation();
            Check.Equal(2, store.ReadRows("exclusion").Count);
            var policy = (string)store.ReadRows("source_root").Single()["policy"]!;
            using var document = CanonicalJson.Parse(Encoding.UTF8.GetBytes(policy));
            Check.Equal("z/**", document.RootElement.GetProperty("exclusions")[0].GetProperty("rule").GetString());
        });
        yield return new("Disposed or cancelled staging writes leave no partial records", async () =>
        {
            var capture = await Capture(context, "rollback"); using var store = CanonicalStore.CreateInMemory();
            using (var write = store.BeginWrite()) write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch);
            Check.Equal(0, store.ReadRows("record").Count);
            using (var write = store.BeginWrite())
            {
                write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch);
                Check.Throws<OperationCanceledException>(() => write.CommitFoundation(new CancellationToken(true)));
            }
            Check.Equal(0, store.ReadRows("record").Count);
            using (var write = store.BeginWrite()) { write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch); write.CommitFoundation(); }
        });
        yield return TestCase.Sync("Constrained insertion rejects SQL identifiers and implicit numeric conversion", () =>
        {
            using var store = CanonicalStore.CreateInMemory();
            using (var write = store.BeginWrite()) Check.Throws<ArgumentException>(() => write.Insert("source_blob; DROP TABLE snapshot", Row(("hash", "a"))));
            using (var write = store.BeginWrite()) Check.Throws<ArgumentException>(() => write.Insert("source_blob", Row(("hash", new string('a', 64)), ("byte_length", "1"), ("storage_key", "x"))));
            using (var write = store.BeginWrite())
            {
                Check.Throws<SqliteException>(() => write.Insert("source_blob", Row(("hash", "short"), ("byte_length", 1L), ("storage_key", "x"))));
                Check.Throws<InvalidOperationException>(() => write.CommitFoundation());
            }
            Check.Equal(0, store.ReadRows("source_blob").Count); Check.Equal(0, store.ReadRows("snapshot").Count);
        });
        yield return TestCase.Sync("Open rejects extra columns and weakened CHECK constraints", () =>
        {
            foreach (var tamper in new[] { "ALTER TABLE source_file ADD COLUMN hidden_payload TEXT",
                "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql,'CHECK(length(hash)=64)','CHECK(1)') WHERE name='source_blob'; PRAGMA writable_schema=OFF" })
            {
                var path = Path.Combine(NewDirectory(context, "tamper"), "index.sqlite");
                using (CanonicalStore.Create(path)) { }
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
                { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = tamper; command.ExecuteNonQuery(); }
                Check.Throws<InvalidDataException>(() => CanonicalStore.Open(path));
            }
        });
        yield return new("Typed values cannot disagree with a valid identity envelope", async () =>
        {
            var capture = await Capture(context, "identity");
            var altered = capture with { Files = capture.Files.Select((f, i) => i == 0 ? f with { RelativePath = "renamed.cpp" } : f).ToArray() };
            using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite(); write.ImportCapturedSources(altered, DateTimeOffset.UnixEpoch);
            var error = Check.Throws<FoundationValidationException>(() => write.CommitFoundation());
            Check.True(error.Result.Issues.Any(i => i.Code == "identity_columns")); Check.Equal(0, store.ReadRows("record").Count);
        });
        yield return new("Invalid identity hash is rejected before insertion and faults its transaction", async () =>
        {
            var capture = await Capture(context, "bad_hash"); using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite();
            Check.Throws<InvalidDataException>(() => write.AddIdentityInput("snapshot:" + new string('0', 64), capture.IdentityInputs[capture.SnapshotId]));
            Check.Throws<InvalidOperationException>(() => write.CommitFoundation());
        });
        yield return new("Snapshot typed payload without registry pair is rejected", async () =>
        {
            var capture = await Capture(context, "missing_pair"); using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite();
            write.Insert("snapshot", Row(("id", capture.SnapshotId), ("repository_identity", "storage-tests"), ("created_utc", "1970-01-01T00:00:00Z"), ("boundary_policy", "explicit")));
            var error = Check.Throws<FoundationValidationException>(() => write.CommitFoundation());
            Check.True(error.Result.Issues.Any(i => i.Code == "registry_missing"));
        });
        yield return new("Registry anchor without typed payload is rejected in canonical storage", async () =>
        {
            var capture = await Capture(context, "anchor"); using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite();
            write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch);
            Registry(write, "operation:" + new string('1', 64), "operation", "BD0.7", capture.SnapshotId);
            var error = Check.Throws<FoundationValidationException>(() => write.CommitFoundation()); Check.True(error.Result.Issues.Any(i => i.Code == "payload_missing"));
        });
        yield return new("Field state rejects wrong owner, missing row and contradictory known value", async () =>
        {
            foreach (var variant in new[] { "owner", "row", "value" })
            {
                var capture = await Capture(context, "state_" + variant); using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite();
                write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch); var file = capture.Files[0];
                write.SetFieldState(variant == "owner" ? capture.SnapshotId : file.Id, "source_file",
                    [variant == "row" ? "source_file:" + new string('0', 64) : file.Id], "name", "unknown", "fixture_missing");
                var error = Check.Throws<FoundationValidationException>(() => write.CommitFoundation()); Check.True(error.Result.Issues.Any(i => i.Code == "field_state_path"));
            }
        });
        yield return new("Cross-snapshot evidence cannot silently attach to another capture", async () =>
        {
            var first = await Capture(context, "scope_a"); var second = await Capture(context, "scope_b", 42);
            using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite();
            write.ImportCapturedSources(first, DateTimeOffset.UnixEpoch); write.ImportCapturedSources(second, DateTimeOffset.UnixEpoch);
            var evidence = store.ReadRows("record").Single(r => (string)r["kind"]! == "evidence" && (string)r["snapshot_id"]! == second.SnapshotId)["id"];
            write.Insert("record_evidence", Row(("record_id", first.Files[0].Id), ("evidence_id", evidence), ("role", "wrong_scope")));
            var error = Check.Throws<FoundationValidationException>(() => write.CommitFoundation()); Check.True(error.Result.Issues.Any(i => i.Code == "cross_snapshot_reference"));
        });
        yield return new("Source span bounds are checked against the retained byte length", async () =>
        {
            var capture = await Capture(context, "span"); var file = capture.Files.First(f => f.RelativePath == "sample.cpp");
            using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite(); write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch);
            var components = new { file_version_id = file.FileVersionId, start_byte = 0L, end_byte = file.ByteLength!.Value + 1, origin_kind = "written", origin_stack = Array.Empty<object>() };
            var id = StableId.Create("source_span/1", components); Registry(write, id, "source_span", "BD0.3", capture.SnapshotId);
            write.Insert("source_span", Row(("id", id), ("file_version_id", file.FileVersionId), ("start_byte", 0L), ("end_byte", file.ByteLength.Value + 1), ("origin_kind", "written"), ("cause_span_id", null)));
            write.AddIdentityInput(id, StableId.Envelope("source_span/1", components));
            var error = Check.Throws<FoundationValidationException>(() => write.CommitFoundation()); Check.True(error.Result.Issues.Any(i => i.Code == "span_out_of_bounds"));
        });
        yield return new("Context options bind identity, field states and contiguous ordering", async () =>
        {
            foreach (var broken in new[] { false, true })
            {
                var capture = await Capture(context, "context"); using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite();
                write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch); AddContext(write, store, capture, broken);
                if (broken)
                { var error = Check.Throws<FoundationValidationException>(() => write.CommitFoundation()); Check.True(error.Result.Issues.Any(i => i.Code == "noncontiguous_order")); }
                else Check.True(write.CommitFoundation().IsValid);
            }
        });
        yield return new("M1A refuses a finalized dataset state", async () =>
        {
            var capture = await Capture(context, "final"); using var store = CanonicalStore.CreateInMemory(); using var write = store.BeginWrite();
            write.ImportCapturedSources(capture, DateTimeOffset.UnixEpoch);
            write.Insert("dataset", Row(("id", "dataset:" + new string('c', 64)), ("snapshot_id", capture.SnapshotId), ("configuration_fingerprint", new string('d', 64)),
                ("configuration_json", "{}"), ("state", "finalized"), ("created_utc", "1970-01-01T00:00:00Z")));
            var error = Check.Throws<FoundationValidationException>(() => write.CommitFoundation()); Check.True(error.Result.Issues.Any(i => i.Code == "not_collecting"));
        });
    }

    private static async Task<CapturedInputs> Capture(TestContext context, string label, byte binary = 255)
    {
        var directory = NewDirectory(context, label); var source = Path.Combine(directory, "input"); Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "sample.cpp"), Encoding.UTF8.GetBytes("// café\r\nint value = 7;\r\n"));
        await File.WriteAllBytesAsync(Path.Combine(source, "data.bin"), [binary, 0, 1]);
        return await new CaptureService().CaptureAsync(new("storage-tests", [new("pilot", source)],
            [new("pilot", "sample.cpp", Language: "cpp"), new("pilot", "data.bin", Role: "resource", Binary: true),
             new("pilot", "missing.cpp", Language: "cpp", DoNotCaptureReason: "fixture_not_collected")]), Path.Combine(directory, "sources"));
    }
    private static string NewDirectory(TestContext context, string label)
    { var path = Path.Combine(context.ScratchRoot, "storage", label + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] fields)
        => fields.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
    private static void Registry(CanonicalWrite write, string id, string kind, string section, string snapshot, string? context = null)
        => write.Insert("record", Row(("id", id), ("kind", kind), ("section", section), ("snapshot_id", snapshot), ("context_id", context), ("context_independent", context is null), ("origin_span_id", null)));

    private static void AddContext(CanonicalWrite write, CanonicalStore store, CapturedInputs capture, bool gap)
    {
        var file = capture.Files.First(f => f.RelativePath == "sample.cpp");
        var projectComponents = new { snapshot_id = capture.SnapshotId, definition_file_version_id = file.FileVersionId, system_kind = "other", logical_project_key = "fixture" };
        var projectId = StableId.Create("project/1", projectComponents); Registry(write, projectId, "project", "BDG.3", capture.SnapshotId);
        write.Insert("project", Row(("id", projectId), ("name", "fixture"), ("definition_file_id", file.Id), ("system_kind", "other"), ("system_name", "explicit_test_input")));
        write.AddIdentityInput(projectId, StableId.Envelope("project/1", projectComponents));
        var components = new
        {
            snapshot_id = capture.SnapshotId, project_id = projectId, solution_mapping = (object?)null,
            configuration = "Debug", platform = "x64", architecture = "x64", target_framework = (string?)null,
            runtime_identifier = (string?)null, language_version = "c++17", output_identity = "fixture",
            options = new[] { new { kind = "literal", value = "-std=c++17" }, new { kind = "literal", value = "-fno-exceptions" } },
            tools = Array.Empty<object>(), providers = Array.Empty<object>(), generated_inputs = Array.Empty<object>(),
            path_semantics_policy = "logical_roots/1", path_sensitive_values = Array.Empty<object>()
        };
        var id = StableId.Create("build_context/1", components); Registry(write, id, "build_context", "BDB.1", capture.SnapshotId, id);
        write.Insert("build_context", Row(("id", id), ("project_id", projectId), ("configuration", "Debug"), ("platform", "x64"),
            ("architecture", "x64"), ("language_version", "c++17"), ("output_identity", "fixture"),
            ("effective_fingerprint", CanonicalJson.Sha256(CanonicalJson.Serialize(components))), ("working_directory", "virtual:pilot")));
        write.AddIdentityInput(id, StableId.Envelope("build_context/1", components));
        write.SetFieldState(id, "build_context", [id], "target_framework", "not_applicable", "native_context");
        write.SetFieldState(id, "build_context", [id], "runtime_identifier", "not_applicable", "native_context");
        var evidence = store.ReadRows("evidence").Single()["id"];
        write.Insert("context_setting", Row(("context_id", id), ("category", "compiler_option"), ("ordinal", 0L), ("name", ""), ("value", "-std=c++17"), ("evidence_id", evidence)));
        write.Insert("context_setting", Row(("context_id", id), ("category", "compiler_option"), ("ordinal", gap ? 2L : 1L), ("name", ""), ("value", "-fno-exceptions"), ("evidence_id", evidence)));
    }
}

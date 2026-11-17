using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PromptedLearning.Core;

namespace PromptedLearning.Storage;

public sealed record StorageIssue(string Code, string Table, string? RecordId, string Message);
public sealed record FoundationValidationResult(IReadOnlyList<StorageIssue> Issues, int ProducedRecords)
{
    public bool IsValid => Issues.Count == 0;
    public string Scope => "M1A produced foundation records only; no extractor finalization or later producer claim.";
}
public sealed class FoundationValidationException(FoundationValidationResult result)
    : IOException("Foundation validation failed: " + string.Join("; ", result.Issues.Select(x => x.Code + ": " + x.Message)))
{
    public FoundationValidationResult Result { get; } = result;
}

public sealed record SemanticCollectionValidationResult(IReadOnlyList<StorageIssue> Issues, int ProducedRecords)
{
    public bool IsValid => Issues.Count == 0;
    public string Scope => "M1B collecting source semantics only; no CFG, data flow, export or finalization.";
}
public sealed class SemanticCollectionValidationException(SemanticCollectionValidationResult result)
    : IOException("Semantic collection validation failed: " + string.Join("; ", result.Issues.Select(issue => issue.Code + ": " + issue.Message)))
{
    public SemanticCollectionValidationResult Result { get; } = result;
}

public sealed record SourceRelationsValidationResult(IReadOnlyList<StorageIssue> Issues, int ProducedRecords)
{
    public bool IsValid => Issues.Count == 0;
    public string Scope => "M1C0 collecting direct source calls only; no runtime candidates, CFG, data flow or finalization.";
}
public sealed class SourceRelationsValidationException(SourceRelationsValidationResult result)
    : IOException("Source relation validation failed: " + string.Join("; ", result.Issues.Select(issue => issue.Code + ": " + issue.Message)))
{
    public SourceRelationsValidationResult Result { get; } = result;
}

/// <summary>Append-only collecting storage. It exposes neither arbitrary SQL nor dataset finalization.</summary>
public sealed class CanonicalStore : IDisposable
{
    private readonly SqliteConnection connection;
    internal StorageSchema Schema { get; }
    private CanonicalWrite? activeWrite;
    private bool disposed;

    private CanonicalStore(SqliteConnection connection, bool create)
    {
        this.connection = connection;
        Execute("PRAGMA foreign_keys=ON");
        if (create)
        {
            Execute(StorageSchema.Ddl);
            Execute("INSERT INTO schema_info VALUES (1,'pl-vocabulary/1','pl-jsonl/1')");
        }
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("Unsupported SQLite schema version.");
        Schema = new StorageSchema(connection);
    }

    public static CanonicalStore Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        // Reserve without overwriting a prior generation or another writer's database.
        using (new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        return Connect(full, SqliteOpenMode.ReadWrite, true);
    }
    public static CanonicalStore CreateInMemory() => Connect(":memory:", SqliteOpenMode.Memory, true);
    public static CanonicalStore Open(string path) => Connect(Path.GetFullPath(path), SqliteOpenMode.ReadWrite, false);

    private static CanonicalStore Connect(string path, SqliteOpenMode mode, bool create)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = mode, Cache = SqliteCacheMode.Private, Pooling = false, ForeignKeys = true }.ToString());
        try { connection.Open(); return new(connection, create); }
        catch { connection.Dispose(); throw; }
    }

    public CanonicalWrite BeginWrite()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeWrite is not null) throw new InvalidOperationException("A staging transaction is already active.");
        if (ReadRows("dataset").Any(x => (string)x["state"]! != "collecting"))
            throw new InvalidOperationException("Only collecting datasets can be changed by the M1A writer.");
        var transaction = connection.BeginTransaction();
        Execute("PRAGMA defer_foreign_keys=ON", transaction);
        return activeWrite = new CanonicalWrite(this, transaction);
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadRows(string table)
        => ReadRows(table, activeWrite?.Transaction);

    internal IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadRows(string table, SqliteTransaction? transaction)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Schema.Tables.TryGetValue(table, out var definition)) throw new ArgumentException("Unknown table.", nameof(table));
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT * FROM " + StorageSchema.Quote(table) + " ORDER BY "
            + string.Join(",", definition.PrimaryKey.Select(StorageSchema.Quote));
        using var reader = command.ExecuteReader();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(new ReadOnlyDictionary<string, object?>(row));
        }
        return rows;
    }

    public FoundationValidationResult ValidateFoundation(CancellationToken cancellationToken = default)
        => new FoundationValidator(this, connection, activeWrite?.Transaction).Validate(cancellationToken);

    public SemanticCollectionValidationResult ValidateSemanticCollection(CancellationToken cancellationToken = default)
    {
        var result = new FoundationValidator(this, connection, activeWrite?.Transaction, semanticCollection: true).Validate(cancellationToken);
        return new(result.Issues, result.ProducedRecords);
    }

    public SourceRelationsValidationResult ValidateSourceRelationsCollection(CancellationToken cancellationToken = default)
    {
        var result = new FoundationValidator(this, connection, activeWrite?.Transaction, semanticCollection: true, sourceRelations: true).Validate(cancellationToken);
        return new(result.Issues, result.ProducedRecords);
    }

    internal void UpdateCollecting(string table, IReadOnlyDictionary<string, object?> expected,
        IReadOnlyDictionary<string, object?> changes, SqliteTransaction transaction)
    {
        if (table is not ("compilation_unit" or "unit_input" or "field_state")) throw new InvalidOperationException("Only collecting compiler-observation transitions are permitted.");
        var definition = Schema.Tables[table];
        if (changes.Keys.Any(key => !definition.Columns.ContainsKey(key) || definition.PrimaryKey.Contains(key)))
            throw new ArgumentException("A collecting transition cannot alter its identity.");
        if (!definition.PrimaryKey.All(expected.ContainsKey)) throw new ArgumentException("A collecting transition requires the complete key.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var values = changes.ToArray(); var guards = expected.ToArray();
        command.CommandText = "UPDATE " + StorageSchema.Quote(table) + " SET " + string.Join(",", values.Select((pair, index) => StorageSchema.Quote(pair.Key) + "=$v" + index))
            + " WHERE " + string.Join(" AND ", guards.Select((pair, index) => StorageSchema.Quote(pair.Key) + " IS $g" + index));
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$v" + index, values[index].Value ?? DBNull.Value);
        for (var index = 0; index < guards.Length; index++) command.Parameters.AddWithValue("$g" + index, guards[index].Value ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Collecting transition no longer matches the retained state.");
    }

    internal void Insert(string table, IReadOnlyDictionary<string, object?> values, SqliteTransaction transaction)
    {
        if (!Schema.Tables.TryGetValue(table, out var definition)) throw new ArgumentException("Unknown table.", nameof(table));
        if (table == "schema_info") throw new ArgumentException("Schema metadata is initialized by the store.", nameof(table));
        if (values.Count == 0) throw new ArgumentException("An inserted row cannot be empty.", nameof(values));
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var names = values.Keys.Order(StringComparer.Ordinal).ToArray();
        for (var i = 0; i < names.Length; i++)
        {
            if (!definition.Columns.TryGetValue(names[i], out var column)) throw new ArgumentException("Unknown column: " + names[i], nameof(values));
            var value = values[names[i]];
            object dbValue = value ?? DBNull.Value;
            if (value is not null)
            {
                if (column.Type == "TEXT" && value is not string) throw new ArgumentException("TEXT requires a string: " + names[i]);
                if (column.Type == "INTEGER") dbValue = value switch
                {
                    bool boolean => boolean ? 1L : 0L, byte b => (long)b, short s => (long)s, int n => (long)n, long n => n,
                    _ => throw new ArgumentException("INTEGER requires a signed integral value: " + names[i])
                };
                if (value is string text) _ = CanonicalJson.Serialize(text);
            }
            command.Parameters.AddWithValue("$p" + i, dbValue);
        }
        command.CommandText = "INSERT INTO " + StorageSchema.Quote(table) + " ("
            + string.Join(",", names.Select(StorageSchema.Quote)) + ") VALUES ("
            + string.Join(",", names.Select((_, i) => "$p" + i)) + ")";
        command.ExecuteNonQuery();
    }
    private void Execute(string sql, SqliteTransaction? transaction = null)
    { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery(); }
    internal void EndWrite(CanonicalWrite writer) { if (ReferenceEquals(activeWrite, writer)) activeWrite = null; }
    public void Dispose() { if (disposed) return; activeWrite?.Dispose(); connection.Dispose(); disposed = true; }
}

public sealed class CanonicalWrite : IDisposable
{
    private readonly CanonicalStore store;
    internal SqliteTransaction Transaction { get; }
    private bool closed;
    private bool faulted;
    internal CanonicalWrite(CanonicalStore store, SqliteTransaction transaction) { this.store = store; Transaction = transaction; }
    internal StorageSchema Schema => store.Schema;
    internal IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadRows(string table) => store.ReadRows(table);
    internal SourceRelationsValidationResult ValidateSourceRelations(CancellationToken cancellationToken) => store.ValidateSourceRelationsCollection(cancellationToken);
    internal T ImportOperation<T>(Func<T> import)
    {
        EnsureActive();
        try { return import(); }
        catch { faulted = true; throw; }
    }
    internal void UpdateCollecting(string table, IReadOnlyDictionary<string, object?> expected, IReadOnlyDictionary<string, object?> changes)
        => ImportOperation(() => { store.UpdateCollecting(table, expected, changes, Transaction); return true; });
    public void Insert(string table, IReadOnlyDictionary<string, object?> values)
    {
        EnsureActive();
        try { store.Insert(table, values, Transaction); }
        catch { faulted = true; throw; }
    }
    public void AddIdentityInput(string id, JsonElement envelope)
    {
        EnsureActive();
        try { StableId.Verify(id, envelope); }
        catch { faulted = true; throw; }
        Insert("record_extension", new Dictionary<string, object?>
        {
            ["record_id"] = id, ["schema_name"] = "identity-input", ["schema_version"] = 1L,
            ["payload_json"] = Encoding.UTF8.GetString(CanonicalJson.Serialize(envelope))
        });
    }
    public void AddSourceBlob(CapturedBlob blob)
    {
        EnsureActive();
        var existing = store.ReadRows("source_blob").SingleOrDefault(row => (string)row["hash"]! == blob.Hash);
        if (existing is not null)
        {
            if ((long)existing["byte_length"]! != blob.ByteLength || (string)existing["storage_key"]! != blob.StorageKey)
            { faulted = true; throw new InvalidDataException("Existing content-addressed blob has conflicting metadata."); }
            return;
        }
        Insert("source_blob", new Dictionary<string, object?>
        { ["hash"] = blob.Hash, ["byte_length"] = blob.ByteLength, ["storage_key"] = blob.StorageKey });
    }
    public void SetFieldState(string ownerId, string table, object?[] primaryKey, string column,
        string state, string? reason, string? evidenceId = null)
    {
        var key = Convert.ToBase64String(CanonicalJson.Serialize(primaryKey)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        SetState(ownerId, "/" + table + "/" + key + "/" + column, state, reason, evidenceId);
    }
    public void SetCollectionState(string ownerId, string table, string role, string state, string? reason, string? evidenceId = null)
        => SetState(ownerId, "/" + table + "/@collection/" + role.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal), state, reason, evidenceId);
    private void SetState(string owner, string path, string state, string? reason, string? evidence)
        => Insert("field_state", new Dictionary<string, object?>
        { ["record_id"] = owner, ["field_path"] = path, ["state"] = state, ["reason"] = reason, ["evidence_id"] = evidence });

    public FoundationValidationResult CommitFoundation(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = store.ValidateFoundation(cancellationToken);
            if (!result.IsValid) throw new FoundationValidationException(result);
            cancellationToken.ThrowIfCancellationRequested();
            Transaction.Commit(); closed = true; Transaction.Dispose(); store.EndWrite(this);
            return result;
        }
        catch { Dispose(); throw; }
    }

    public SemanticCollectionValidationResult CommitSemanticCollection(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = store.ValidateSemanticCollection(cancellationToken);
            if (!result.IsValid) throw new SemanticCollectionValidationException(result);
            cancellationToken.ThrowIfCancellationRequested();
            Transaction.Commit(); closed = true; Transaction.Dispose(); store.EndWrite(this);
            return result;
        }
        catch { Dispose(); throw; }
    }

    public SourceRelationsValidationResult CommitSourceRelationsCollection(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = store.ValidateSourceRelationsCollection(cancellationToken);
            if (!result.IsValid) throw new SourceRelationsValidationException(result);
            cancellationToken.ThrowIfCancellationRequested();
            Transaction.Commit(); closed = true; Transaction.Dispose(); store.EndWrite(this);
            return result;
        }
        catch { Dispose(); throw; }
    }
    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (faulted) throw new InvalidOperationException("This staging transaction failed and must be disposed.");
    }
    public void Dispose()
    {
        if (closed) return;
        try { Transaction.Rollback(); }
        finally { closed = true; Transaction.Dispose(); store.EndWrite(this); }
    }
}

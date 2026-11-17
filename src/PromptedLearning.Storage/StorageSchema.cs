using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PromptedLearning.Core;

namespace PromptedLearning.Storage;

internal sealed record ColumnDefinition(string Name, string Type, bool Required, int KeyOrdinal);
internal sealed record ForeignKeyDefinition(string Column, string Table, string TargetColumn);
internal sealed record TableDefinition(string Name, IReadOnlyDictionary<string, ColumnDefinition> Columns,
    IReadOnlyList<string> PrimaryKey, IReadOnlyList<ForeignKeyDefinition> ForeignKeys);

internal sealed class StorageSchema
{
    public static string Ddl { get; } = Resource("schema.sql");
    public static JsonElement Dictionary { get; } = ParseDictionary();
    public IReadOnlyDictionary<string, TableDefinition> Tables { get; }
    public IReadOnlySet<string> PrimaryTables { get; }

    public StorageSchema(SqliteConnection connection)
    {
        VerifyExactSchema(connection);
        var names = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        var tables = new Dictionary<string, TableDefinition>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var columns = new Dictionary<string, ColumnDefinition>(StringComparer.Ordinal);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(" + Quote(name) + ")";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var column = new ColumnDefinition(reader.GetString(1), reader.GetString(2),
                        reader.GetInt64(3) != 0, checked((int)reader.GetInt64(5)));
                    columns.Add(column.Name, column);
                }
            }
            var keys = new List<ForeignKeyDefinition>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_key_list(" + Quote(name) + ")";
                using var reader = command.ExecuteReader();
                while (reader.Read()) keys.Add(new(reader.GetString(3), reader.GetString(2), reader.GetString(4)));
            }
            tables.Add(name, new(name, columns, columns.Values.Where(x => x.KeyOrdinal > 0)
                .OrderBy(x => x.KeyOrdinal).Select(x => x.Name).ToArray(), keys));
        }
        Tables = tables;
        PrimaryTables = tables.Values.Where(x => x.ForeignKeys.Any(f => f.Column == "id" && f.Table == "record"))
            .Select(x => x.Name).Append("snapshot").ToHashSet(StringComparer.Ordinal);
        var expected = Dictionary.GetProperty("tables").EnumerateArray().Select(x => x.GetProperty("table").GetString()!);
        if (!tables.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidDataException("SQLite tables differ from the embedded contract.");
        foreach (var field in Dictionary.GetProperty("fields").EnumerateArray())
        {
            var storage = field.GetProperty("storage");
            var table = tables[storage.GetProperty("table").GetString()!];
            var columnName = storage.GetProperty("column").GetString()!;
            if (!table.Columns.TryGetValue(columnName, out var column) || column.Type != storage.GetProperty("sqlite_type").GetString())
                throw new InvalidDataException("SQLite column differs from embedded contract: " + table.Name + "." + columnName);
        }
    }

    private static void VerifyExactSchema(SqliteConnection actual)
    {
        using var expected = new SqliteConnection("Data Source=:memory:;Pooling=False;Foreign Keys=True");
        expected.Open();
        using (var command = expected.CreateCommand())
        { command.CommandText = Ddl; command.ExecuteNonQuery(); }
        static string[] Definitions(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name";
            using var reader = command.ExecuteReader();
            var definitions = new List<string>();
            while (reader.Read()) definitions.Add(string.Join("\0", Enumerable.Range(0, 4)
                .Select(i => reader.IsDBNull(i) ? "" : reader.GetString(i))));
            return definitions.ToArray();
        }
        if (!Definitions(actual).SequenceEqual(Definitions(expected), StringComparer.Ordinal))
            throw new InvalidDataException("SQLite definitions differ from the exact embedded DDL (including constraints and indexes).");
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PromptedLearning.Storage." + name)
            ?? throw new InvalidOperationException("Missing embedded storage resource: " + name);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
    private static JsonElement ParseDictionary()
    {
        using var document = CanonicalJson.Parse(System.Text.Encoding.UTF8.GetBytes(Resource("field-dictionary.json")));
        return document.RootElement.Clone();
    }
}

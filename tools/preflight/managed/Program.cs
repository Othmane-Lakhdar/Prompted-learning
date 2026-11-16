using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.Data.Sqlite;

if (args.Contains("--expected-failure")) return 7;

const string code = "class Demo { static int Twice(int x) => x * 2; static int Run() => Twice(21); }";
var tree = CSharpSyntaxTree.ParseText(code);
var compilation = CSharpCompilation.Create("Probe", [tree],
    [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
if (errors.Length > 0) throw new Exception(string.Join("\n", errors.Select(e => e.ToString())));
var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
var operation = compilation.GetSemanticModel(tree).GetOperation(invocation) as IInvocationOperation;
if (operation?.TargetMethod.ToDisplayString() != "Demo.Twice(int)" || operation.Arguments.Length != 1)
    throw new Exception("Roslyn did not resolve the expected invocation.");
Console.WriteLine($"Roslyn semantic symbol/operation passed: {operation.TargetMethod}; version={typeof(Compilation).Assembly.GetName().Version}");

using var database = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
database.Open();
using var command = database.CreateCommand();
command.CommandText = "CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent(id)); INSERT INTO parent VALUES(1); INSERT INTO child VALUES(1,1);";
command.ExecuteNonQuery();
var rejected = false;
command.CommandText = "INSERT INTO child VALUES(2,999);";
try { command.ExecuteNonQuery(); }
catch (SqliteException e) when (e.SqliteErrorCode == 19) { rejected = true; }
if (!rejected) throw new Exception("SQLite accepted a dangling foreign key.");
using (var transaction = database.BeginTransaction()) {
    command.Transaction = transaction;
    command.CommandText = "INSERT INTO parent VALUES(2);";
    command.ExecuteNonQuery();
    transaction.Rollback();
}
command.Transaction = null;
command.CommandText = "SELECT COUNT(*) FROM parent;";
if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new Exception("SQLite rollback did not restore row count.");
command.CommandText = "SELECT sqlite_version();";
Console.WriteLine($"SQLite native provider, foreign-key rejection and rollback passed: {command.ExecuteScalar()}");
Console.WriteLine($"Managed compile/run passed: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
return 0;

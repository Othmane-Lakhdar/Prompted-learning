using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PromptedLearning.Tests;

var values = args.Chunk(2).ToDictionary(p => p[0], p => p.Length == 2 ? p[1] : throw new ArgumentException("Expected option value."));
var projectRoot = Path.GetFullPath(values.GetValueOrDefault("--project-root", Directory.GetCurrentDirectory()));
var scratchRoot = Path.GetFullPath(values.GetValueOrDefault("--scratch", Path.Combine(Path.GetTempPath(), "PromptedLearning-tests", Guid.NewGuid().ToString("N"))));
Directory.CreateDirectory(scratchRoot);
var context = new TestContext(projectRoot, scratchRoot);
var group = values.GetValueOrDefault("--group", "all");
var cases = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => t.Name.EndsWith("Tests", StringComparison.Ordinal) && (group == "all" || t.Name.Equals(group + "Tests", StringComparison.OrdinalIgnoreCase)))
    .Select(t => t.GetMethod("Cases", BindingFlags.Public | BindingFlags.Static))
    .Where(m => m is not null)
    .SelectMany(m => (IEnumerable<TestCase>)m!.Invoke(null, [context])!).ToArray();
if (cases.Length == 0) throw new InvalidOperationException("No tests selected; an empty suite is not a pass.");
var results = new List<object>(); var failed = 0;
foreach (var test in cases)
{
    var watch = Stopwatch.StartNew();
    try { await test.Run(); results.Add(new { name = test.Name, status = "passed", elapsed_ms = watch.Elapsed.TotalMilliseconds }); }
    catch (Exception error) { failed++; results.Add(new { name = test.Name, status = "failed", elapsed_ms = watch.Elapsed.TotalMilliseconds, error = error.ToString() }); }
}
var report = new { stage = values.GetValueOrDefault("--stage", "M1B"), group, tests = cases.Length, failed, status = failed == 0 ? "passed" : "failed", results };
var output = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n";
Console.Write(output);
if (values.TryGetValue("--report", out var reportPath)) await File.WriteAllTextAsync(reportPath, output);
return failed == 0 ? 0 : 1;

namespace PromptedLearning.Tests;

public sealed record TestContext(string ProjectRoot, string ScratchRoot);
public sealed record TestCase(string Name, Func<Task> Run)
{
    public static TestCase Sync(string name, Action run) => new(name, () => { run(); return Task.CompletedTask; });
}
public static class Check
{
    public static void True(bool condition, string message = "Expected true") { if (!condition) throw new Exception(message); }
    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message ?? $"Expected {expected}; got {actual}.");
    }
    public static T Throws<T>(Action run) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    public static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}

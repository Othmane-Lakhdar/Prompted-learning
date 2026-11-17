using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PromptedLearning.Core;

public sealed record NativeWorkerExecution(JsonElement WireResult, string StandardError, long ElapsedMs);

/// <summary>Bounded process transport. Only the separate semantic validator can authorize persistence.</summary>
public sealed class NativeWorkerHost
{
    public async Task<NativeWorkerExecution> RunAsync(string executable, JsonElement request, CancellationToken cancellationToken = default)
    {
        ContractCatalog.Default.Validate("native-worker-request", request);
        var stamped = NativeWorkerWire.Stamp(request);
        foreach (var key in new[] { "request_fingerprint", "semantic_input_fingerprint" })
            Require(Text(stamped, key) == Text(request, key), "Native request fingerprint mismatch.");
        var budgets = request.GetProperty("budgets");
        var milliseconds = budgets.GetProperty("time_ms").GetInt64();
        var outputLimit = budgets.GetProperty("max_result_bytes").GetInt64();
        Require(milliseconds <= 60000 && outputLimit <= 16777216, "Native host pilot budget exceeds supported bounds.");
        var bytes = CanonicalJson.Serialize(request, true);
        Require(bytes.Length <= 64 * 1024 * 1024, "Native request framing budget exceeded.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(milliseconds));
        var token = deadline.Token;
        // On Windows the pinned read handle denies replacement/writes throughout launch and execution.
        await using var binary = new FileStream(Path.GetFullPath(executable), FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(binary, token));
        Require(digest == Text(request.GetProperty("worker"), "binary_sha256"), "Native worker binary differs from evaluated tool.");
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        var watch = Stopwatch.StartNew();
        using var memoryJob = WindowsProcessBudget.Create(budgets.GetProperty("memory_bytes").GetInt64());
        using var process = new Process { StartInfo = start };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Native worker launch failed.");
        var output = ReadBounded(process.StandardOutput.BaseStream, outputLimit, deadline);
        var error = ReadBounded(process.StandardError.BaseStream, 131072, deadline);
        try
        {
            memoryJob.Attach(process);
            await process.StandardInput.BaseStream.WriteAsync(bytes, token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            var resultBytes = await output; var stderr = await error;
            Require(process.ExitCode == 0, "Native worker failed with exit code " + process.ExitCode + ".");
            var result = NativeWorkerWire.ValidateResponse(request, resultBytes);
            return new(result, new UTF8Encoding(false, true).GetString(stderr), watch.ElapsedMilliseconds);
        }
        finally
        {
            // Worker is an isolated read-only compiler process: no database transaction or output file write.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            deadline.Cancel();
            try { await Task.WhenAll(output, error); } catch (Exception failure) when (failure is OperationCanceledException or IOException or InvalidDataException) { }
        }
    }

    private static async Task<byte[]> ReadBounded(Stream source, long maximum, CancellationTokenSource deadline)
    {
        using var result = new MemoryStream(); var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, deadline.Token); if (read == 0) break;
                Require(result.Length + read <= maximum, "Native process output budget exceeded.");
                result.Write(buffer, 0, read);
            }
            return result.ToArray();
        }
        catch { deadline.Cancel(); throw; }
    }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}

public static class NativeWorkerWire
{
    public static JsonElement Stamp(JsonElement request)
    {
        var node = JsonNode.Parse(request.GetRawText())!.AsObject();
        var semantic = new JsonObject();
        foreach (var name in new[] { "snapshot_id", "context_id", "unit_id", "capture_index_fingerprint", "worker", "target", "main_file_key", "files", "options", "path_bindings", "requested_features", "expected_consumed_file_keys" })
            semantic[name] = node[name]!.DeepClone();
        foreach (var file in semantic["files"]!.AsArray()) file!.AsObject().Remove("bytes_base64");
        foreach (var binding in semantic["path_bindings"]!.AsArray()) binding!.AsObject().Remove("physical_path");
        node["semantic_input_fingerprint"] = CanonicalJson.Sha256(CanonicalJson.Serialize(Element(new JsonArray("native-worker-semantic-input/1", semantic))));
        node.Remove("request_fingerprint");
        node["request_fingerprint"] = CanonicalJson.Sha256(CanonicalJson.Serialize(Element(node)));
        return Element(node);
    }

    public static JsonElement ValidateResponse(JsonElement request, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes[^1] != '\n' || bytes[..^1].IndexOf((byte)'\n') >= 0 || bytes.IndexOf((byte)'\r') >= 0
            || bytes.Length > request.GetProperty("budgets").GetProperty("max_result_bytes").GetInt64())
            throw new InvalidDataException("Native response is not one bounded UTF8 LF frame.");
        using var parsed = CanonicalJson.Parse(bytes);
        var result = parsed.RootElement;
        ContractCatalog.Default.Validate("native-worker-result", result);
        var count = result.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).Sum(p => (long)p.Value.GetArrayLength());
        if (count > request.GetProperty("budgets").GetProperty("max_records").GetInt64()
            || result.GetProperty("operations").GetArrayLength() > request.GetProperty("budgets").GetProperty("max_operations").GetInt64())
            throw new InvalidDataException("Native response record/operation budget exceeded.");
        if (!CanonicalJson.Serialize(result, true).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Native response is not canonical.");
        foreach (var name in new[] { "request_id", "request_fingerprint", "semantic_input_fingerprint", "snapshot_id", "context_id", "unit_id", "worker" })
            if (!CanonicalJson.Serialize(request.GetProperty(name)).AsSpan().SequenceEqual(CanonicalJson.Serialize(result.GetProperty(name))))
                throw new InvalidDataException("Native response scope/producer echo mismatch: " + name);
        var status = result.GetProperty("status").GetString(); var reason = result.GetProperty("stop_reason");
        if ((status == "complete") != (reason.ValueKind == JsonValueKind.Null)) throw new InvalidDataException("Native outcome reason contradicts status.");
        var known = request.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("key").GetString()!).ToHashSet(StringComparer.Ordinal);
        var consumed = result.GetProperty("consumed_file_keys").EnumerateArray().Select(f => f.GetString()!).ToHashSet(StringComparer.Ordinal);
        if (!consumed.IsSubsetOf(known)) throw new InvalidDataException("Native result consumed an uncaptured file.");
        var semanticArrays = CompilerFacts.ArrayNames.Except(new[] { "spans", "coverage", "diagnostics", "discovery_requests" });
        if (request.GetProperty("phase").GetString() == "discovery" || status is "failed" or "cancelled")
        {
            if (semanticArrays.Any(name => result.GetProperty(name).GetArrayLength() != 0)) throw new InvalidDataException("Provisional or failed native result contains publishable facts.");
            ValidateDiagnosticSpans(request, result);
        }
        else
        {
            if (result.GetProperty("discovery_requests").GetArrayLength() != 0 || !consumed.SetEquals(request.GetProperty("expected_consumed_file_keys").EnumerateArray().Select(f => f.GetString()!)))
                throw new InvalidDataException("Native final consumed set is not closed.");
        }
        foreach (var discovery in result.GetProperty("discovery_requests").EnumerateArray())
            if (!known.Contains(discovery.GetProperty("including_file_key").GetString()!)) throw new InvalidDataException("Native discovery names an uncaptured including file.");
        return result.Clone();
    }

    private static void ValidateDiagnosticSpans(JsonElement request, JsonElement result)
    {
        var spans = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var span in result.GetProperty("spans").EnumerateArray())
            if (!spans.TryAdd(Text(span, "key"), span)) throw new InvalidDataException("Duplicate provisional diagnostic span.");
        var files = request.GetProperty("files").EnumerateArray().ToDictionary(f => Text(f, "key"), StringComparer.Ordinal);
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in result.GetProperty("diagnostics").EnumerateArray())
        {
            var subject = diagnostic.GetProperty("subject_key").GetString();
            if (subject is not null && !files.ContainsKey(subject) && !spans.ContainsKey(subject)) throw new InvalidDataException("Provisional diagnostic names a semantic subject.");
            var key = diagnostic.GetProperty("span_key").GetString(); var path = new HashSet<string>(StringComparer.Ordinal);
            while (key is not null)
            {
                if (!path.Add(key) || !spans.TryGetValue(key, out var span)) throw new InvalidDataException("Missing/cyclic diagnostic origin.");
                retained.Add(key); Bounds(span);
                var stack = span.GetProperty("origin_stack"); var cause = span.GetProperty("cause_span_key").GetString();
                foreach (var frame in stack.EnumerateArray()) Bounds(frame);
                if (stack.GetArrayLength() == 0)
                { if (cause is not null) throw new InvalidDataException("Provisional origin cause lacks stack."); }
                else
                {
                    if (cause is null || !spans.TryGetValue(cause, out var target)) throw new InvalidDataException("Provisional origin target is absent.");
                    foreach (var field in new[] { "file_key", "start_byte", "end_byte", "origin_kind" })
                        if (!Same(stack[0].GetProperty(field), target.GetProperty(field))) throw new InvalidDataException("Provisional cause/frame differs.");
                    if (!Same(CompilerFacts.Element(stack.EnumerateArray().Skip(1).ToArray()), target.GetProperty("origin_stack"))) throw new InvalidDataException("Provisional cause stack suffix differs.");
                }
                key = cause;
            }
        }
        if (!retained.SetEquals(spans.Keys)) throw new InvalidDataException("Provisional result contains unrelated source spans.");
        void Bounds(JsonElement span)
        {
            if (!files.TryGetValue(Text(span, "file_key"), out var file)) throw new InvalidDataException("Uncaptured diagnostic source.");
            var bytes = Convert.FromBase64String(Text(file, "bytes_base64"));
            var start = span.GetProperty("start_byte").GetInt64(); var end = span.GetProperty("end_byte").GetInt64();
            if (start < 0 || end < start || end > bytes.LongLength) throw new InvalidDataException("Diagnostic source interval is out of bounds.");
            var decoded = SourceDecoder.Decode(bytes).Source ?? throw new InvalidDataException("Diagnostic source is not decodable.");
            var points = new HashSet<long> { 0 };
            for (var i = 0; i <= decoded.Text.Length; i++) if (i == decoded.Text.Length || !char.IsLowSurrogate(decoded.Text[i])) points.Add(decoded.ByteOffset(i));
            if (!points.Contains(start) || !points.Contains(end)) throw new InvalidDataException("Diagnostic span splits a source scalar.");
        }
        static bool Same(JsonElement left, JsonElement right) => CanonicalJson.Serialize(left).AsSpan().SequenceEqual(CanonicalJson.Serialize(right));
    }

    public static SemanticUnitResult ToSemanticResult(JsonElement request, JsonElement result)
    {
        if (request.GetProperty("phase").GetString() != "final") throw new InvalidDataException("Discovery cannot become a semantic unit.");
        result = ValidateResponse(request, CanonicalJson.Serialize(result, true));
        var worker = result.GetProperty("worker");
        var facts = CompilerFacts.Element(CompilerFacts.ArrayNames.ToDictionary(name => name, name => result.GetProperty(name)));
        return new(Text(result, "snapshot_id"), Text(result, "context_id"), Text(result, "unit_id"), Text(request, "capture_index_fingerprint"),
            Text(result, "semantic_input_fingerprint"), new(Text(worker, "name"), Text(worker, "version"), Text(worker, "clang_version"), Text(worker, "binary_sha256")),
            Text(result, "status"), result.GetProperty("stop_reason").GetString(), facts,
            result.GetProperty("consumed_file_keys").EnumerateArray().Select(value => value.GetString()!).ToArray());
    }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static JsonElement Element(JsonNode node) { using var parsed = CanonicalJson.Parse(Encoding.UTF8.GetBytes(node.ToJsonString())); return parsed.RootElement.Clone(); }
}

using PromptedLearning.Core;

namespace PromptedLearning.Managed;

public static class ManagedPilotBinding
{
    /// <summary>Host-held inputs, independent of returned compiler facts.</summary>
    public static async Task<SemanticUnitBinding> CreateAsync(ManagedAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        PilotInputGuard.ValidateCapture(request.Evaluation, request.Capture.Inputs);
        if (request.Producer != ManagedPilotAdapter.ProducerFromEvaluation(request.Evaluation)
            || request.SemanticInputFingerprint != ManagedPilotAdapter.SemanticInputFingerprint(request))
            throw new InvalidDataException("Managed binding differs from evaluated producer/input fingerprint.");
        await using var archive = await SourceArchive.OpenAsync(request.Capture.ArchiveRoot, request.Capture.IndexFingerprint, cancellationToken);
        var files = new List<CompilerInputFile>();
        foreach (var source in request.Sources)
        {
            var matches = request.Capture.Inputs.Files.Where(f => f.FileVersionId == source.FileVersionId).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Managed binding source is missing or ambiguous.");
            var file = matches[0];
            var bytes = await archive.ReadBytesAsync(file.FileVersionId, 0, file.ByteLength!.Value, request.MaxSourceBytes, cancellationToken);
            files.Add(new(source.FileKey, file.FileVersionId, file.LogicalPath, file.ContentHash!, bytes.Bytes, file.Encoding));
        }
        var output = request.Evaluation.Properties["AssemblyName"];
        // Exact PilotInputGuard forbids generated/user assembly attributes; the compiler default is 0.0.0.0.
        var internalOutput = CompilerFacts.Element(new
        {
            kind = "internal_output", logical_name = output,
            version = new { state = "known", value = "0.0.0.0", reason = (string?)null },
            content_fingerprint = new { state = "not_collected", value = (string?)null, reason = "output_not_emitted" },
            root_key = "output", relative_location = output
        });
        return new("csharp", output, request.Capture.SnapshotId, request.ContextId, request.UnitId, request.Capture.IndexFingerprint,
            request.SemanticInputFingerprint, request.Producer, files.AsReadOnly(), request.ExpectedConsumedFileKeys.ToArray(),
            AllowedTypeProviders: request.Evaluation.Providers.Select(p => p.Descriptor.Clone()).Append(internalOutput).ToArray());
    }
}

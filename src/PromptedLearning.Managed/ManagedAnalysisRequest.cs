using PromptedLearning.Core;

namespace PromptedLearning.Managed;

public sealed record ManagedSourceInput(string FileKey, string FileVersionId);
public sealed record ManagedAnalysisRequest(MsBuildEvaluation Evaluation, SealedCapture Capture,
    string ContextId, string UnitId, string SemanticInputFingerprint, CompilerProducer Producer,
    IReadOnlyList<ManagedSourceInput> Sources, IReadOnlyList<string> ExpectedConsumedFileKeys,
    int TimeoutMs = 60000, long MaxSourceBytes = 1048576);

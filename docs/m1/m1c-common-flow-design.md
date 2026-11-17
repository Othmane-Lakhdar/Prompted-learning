# M1C1 common validation and replay handoff

Design frozen after A005 and independent native CFG oracle review; see the [prerequisite gate](evidence/gate-m1c1-prerequisites.json). Product implementation is still pending. The [compiler design](m1c-compiler-design.md) and [ordered plan](m1c-plan.md) govern scope. This document adds no product APIs yet. C0 is published at 4f1e2659be63000a1a35fe0582cae5a39e5d0a6c.

## Acceptance boundary

A proposed CompilerFlowBinding contains the existing SemanticUnitBinding plus the exact requested feature array and A005 analysis_selection. A nonconstructible ValidatedCompilerFlow retains the original unmodified SemanticUnitResult, trusted binding, validated source index and validated per-body flow results. CompilerFlowValidator.Validate is the only public acceptance entry point for this token.

Refactor source validation internally for reuse without weakening SemanticResultValidator.Validate. The public M1B path must still reject nonempty control_flow and any requested flow coverage. The explicit flow path validates all source records and all flow records from the same original result. Do not erase flow arrays, rewrite coverage to not_requested or expose a public skip-validation switch to manufacture an accepted source token.

At flow acceptance, check A005 feature/selection/settings schemas and exact selected compiler identity/signature before accepting any graph. Register CFG, block, edge and access local keys separately from source keys, reject duplicates and dangling/cross-body references, and count nested blocks/members/edges/events in record budgets. Binding bytes/settings are revalidated when imported because public buffers can be changed after initial validation.

Requested source features retain their own coverage. Aggregate partial due to three unselected bodies does not make the selected Choose source tree incomplete. Conversely, source completeness never proves CFG/event completeness. A selected complete graph must satisfy the independent language-specific oracle, structural invariants and event replay. Unselected results contain no graph/event payload. Partial selected results cannot authorize C2.

## One deterministic scalar replay policy

Use one shared pure scalar replay implementation for common validation and later canonical replay. Its small typed input view contains body/caller identity, owned scalar storage declarations, operations with ordered child/operand roles, selected call signatures/parameters, typed CFG members/conditions and ordered edges. Identifiers are opaque strings: compiler local keys before import and canonical IDs after import. Neither view reads a working source file or reruns a compiler.

The view is an explicit projection of validated source/CFG records, not a generic JSON dump or a second durable payload. Storage constructs it from canonical tables and validates C0 selected-call mappings. Core constructs it from validated source records. Both require complete exact-pilot source evidence, builtin-int tracked storage, no address/ref/capture/shared-storage escape, and exact direct Add(int,int) value parameters before a complete scalar event sequence is possible.

Replay emits ordered tuples (block, ordinal, event kind, storage, operation, span, phase, reason). Wire access keys are validated as unique local transport keys; they are not durable event record IDs. A canonical event hash uses the ordered tuples with canonical IDs, not ephemeral wire access keys or pointer addresses. C1 can return this hash in an in-memory receipt; C2 recomputes it from persisted source/CFG. No storage_event table, mandatory evidence array or identity extension stores event payload.

Parameter entry is a definition with no operation and the parameter declaration span. Traverse only the relevant branch condition, argument/RHS or return value for a member's phase. Assignment target evaluation is not a read. Preserve native implicit conversions as source operations but do not turn them into extra reads. Deduplicate by block/source-operation/event-kind/storage, preserving first actual evaluation order. Check the adapter's complete tuples against replay; do not derive expected tuples from the supplied event array.

The six selected scalar events are fixed by the independent source/CFG oracles. A missing read, duplicate write, changed storage, reordered RHS/write or fabricated initializer must fail validation. Failed tracked-storage proof yields located unknown_effect/partial, not an invented local write or a callee purity claim. Scalar replay does not infer runtime return values or interprocedural effects. C2 remains unimplemented until C1 persistence/replay passes.

## Atomic canonical import

Expose a separate ImportCompilerFlow(ValidatedCompilerFlow, unitIdentityInput) and CommitFlowCollection. Reuse source mapping internally through explicit typed source/flow entry points, each revalidating its original carrier before writes. Do not create a public boolean that bypasses old validators. SemanticImport's public source-only entry remains unchanged in behavior.

One fresh collecting transaction imports captured/evaluated context, accepted source, C0 direct calls and validated CFG. Revalidate captured bytes, producer descriptors, context/unit identities and selected call mappings. Failure faults/rolls back the whole write; old datasets and compiler contexts remain untouched. Changed compiler/adapter/Core output logic requires fresh declared tool descriptors and resulting context/unit identities. If managed event production calls Core replay, GetAnalysisTools must bind that actual Core binary too; the native host already declares Core.

Persist analysis_run/result/assumption/dependency headers and cfg_block/cfg_operation/cfg_edge payload. A005 settings contain exact feature/selection/options/limits without events. Preserve correspondence, phase and compiler element kind in the three typed member columns. Complete selected results are final=1; unselected/partial results are final=0. All feasibility remains not_established. Use the frozen compiler_cfg_v1 guarantee/assumptions exactly.

CFG input fingerprints bind the accepted body/source semantic inputs, context/unit, actual producer tools, selection/settings and normalizer policy dependencies used by the tracked-storage proof. Do not include the result's own ID or timestamps. Allowed dependencies are body, compilation_unit, build_context and tool headers; no per-operation dependency list or payload-bearing evidence. A C0 normalizer input fingerprint may be bound without copying its input rows into metadata. The implementation must freeze its exact recipe before claiming fingerprint stability.

The persisted validator reconstructs the same typed view, validates identities/foreign keys, rechecks graph membership and replays events. It compares an import receipt only as additional evidence; a reopened database cannot depend on an old in-memory sequence. CFG-only requests keep event coverage not_requested and cannot satisfy C2's event prerequisite.

## Proposed fingerprint preimages to freeze before code

A005, the native oracle and independent review must approve these concrete preimages before shared interface implementation. All objects use CanonicalJson, lowercase SHA256 and exact strings; arrays retain the order specified here. No timestamp, filesystem path, allocation address or result's own ID is included.

The canonical replay source view has schema compiler-flow-source-view/1 and exactly these fields:

- body_id, caller_id, source_complete.
- storage: entries sorted by entity_id; each has entity_id, owner_entity_id, entity_kind, compiler_type_key and declaration_span_id. Include every owned parameter/local considered by the proof, including declarations without initialization. compiler_type_key is the frozen primitive type structure.
- operations: entries sorted by operation_id; each has operation_id, parent_operation_id, child_ordinal, child_role, representation, operation_kind, compiler_kind, semantic_role, operator_kind, implicit, compiler_type_key, referenced_entity_id, span_id, cause_span_id, supported and unsupported_reason. operator_kind is present with null when inapplicable. representation must be semantic for this pilot; operator kind and source/operand roles distinguish simple assignments from unsupported increment, compound assignment and address-taking effects. Include the entire selected body, not only CFG members. Ordered source operands are separate entries sorted by (operation_id, role UTF8, ordinal), with operation_id, role, ordinal, target_id and parameter_id. Parent/child links preserve source ordering without a second child list.
- calls: entries sorted by operation_id; each has operation_id, selected_declaration_id, target_entity_kind, target_language, target_owner_kind, target_static, compiler_signature_key, parameters and arguments. Parameters are ordered by ordinal and contain ordinal, entity_id, passing_mode and compiler_type_key. Arguments are ordered by ordinal and contain ordinal, expression_id, parameter_id and passing_mode. Canonical construction requires these to match C0 mappings and retained source facts; wire construction proves the equivalent mapping directly from accepted compiler records. target_static is true only with the retained managed static modifier; free native functions use false with their actual owner kind. Missing owner kind is null.

The common view is an internal hash/replay representation, never stored as a mandatory metadata payload. Source completeness, storage types and call eligibility are verified against the original typed records rather than accepted as caller-provided booleans. Literal constant values and execution_point are not consumed for scalar event/effect eligibility; no value/feasibility inference is made. Any additional attribute consumed by replay/proof requires a new view-policy version.

source_view_fingerprint = SHA256(CanonicalJson(["compiler-flow-source-view/1", canonical_view])). The view above uses canonical IDs for this fingerprint. A wire/local-key view is for validation only and cannot substitute for the durable canonical view hash.

CFG input fingerprint = SHA256(CanonicalJson(["compiler-cfg-input/1", {snapshot_id,context_id,unit_id,body_id,source_semantic_input_fingerprint,source_view_fingerprint,producer_tool_id,validator_tool_id,normalizer_tool_id,normalizer_input_fingerprint,settings}])). settings is the exact A005 compiler-flow-analysis-settings/1 object. producer_tool_id identifies the actual compiler adapter/worker; validator_tool_id identifies the actual Core validation/replay binary as a declared tool. normalizer_tool_id and normalizer_input_fingerprint are the actual C0 normalization receipt, rechecked from canonical source. All three tool headers are explicit permitted dependencies; normalizer input rows are not copied into metadata.

The canonical source view and C0 fingerprint are computed before CFG result IDs and exclude all flow output/coverage/state transitions. Repeating an unchanged import therefore cannot change the fingerprint through its own outputs. Changing any consumed source fact, proof, selection, setting or producing/validating/normalizing binary invalidates reuse.

Canonical event fingerprint = SHA256(CanonicalJson(["scalar-local-events/1", {body_id,events}])). events are ordered by (block ordinal,event ordinal); each entry contains block_id, ordinal, event_kind, storage_entity_id, operation_id, span_id, phase and reason. A parameter-definition operation_id is null. Every optional field is present with null where the frozen event semantics require it. C2 additionally binds its accepted CFG result/dependencies; it does not use the event hash alone as a graph identity.

## Shared interface proposal

Freeze these signatures with A005/oracle approval before coding. Record types mirror the exact source-view and graph/member/edge fields above; they do not own compiler objects or database connections.

```csharp
public sealed record CompilerFlowBinding(
    SemanticUnitBinding Source,
    IReadOnlyList<string> RequestedFeatures,
    JsonElement AnalysisSelection);

// Internal constructor; no caller can manufacture an accepted flow token.
public sealed class ValidatedCompilerFlow
{
    public SemanticUnitResult Result { get; }
    public CompilerFlowBinding Binding { get; }
    public IReadOnlyDictionary<string, JsonElement> SourceRecords { get; }
}

public static class CompilerFlowValidator
{
    public static ValidatedCompilerFlow Validate(
        SemanticUnitResult result, CompilerFlowBinding binding);
}

public static class ScalarEventReplay
{
    public static ScalarReplayResult Replay(
        ScalarSourceView source, ScalarGraphView graph,
        JsonElement validatedSelectionSettings,
        CancellationToken cancellationToken = default);
}

// Storage extension and collecting gate; no generic validation bypass flag.
public static ImportedCompilerFlow ImportCompilerFlow(
    this CanonicalWrite write, ValidatedCompilerFlow flow,
    JsonElement unitIdentityInput);
public FlowCollectionValidationResult ValidateFlowCollection(
    CancellationToken cancellationToken = default);
public FlowCollectionValidationResult CommitFlowCollection(
    CancellationToken cancellationToken = default);
```

ScalarGraphView has body_id, entry_block_id, exit_block_id, ordered blocks and edges. Blocks carry id/ordinal/kind and ordered typed members (operation_id,ordinal,correspondence,phase,compiler_element_kind); edges carry id/source_block_id/destination_block_id/kind/condition_operation_id/feasibility. It excludes adapter-supplied events. ScalarReplayResult carries status/reason and ordered event tuples. Factories construct views from accepted wire records or validated canonical rows; the pure replay API itself does not grant an accepted token.

ImportedCompilerFlow returns the source-unit mapping, canonical per-body result IDs and selected event fingerprints. The C0 receipt must expose its actual NormalizerToolId alongside its existing input fingerprint, so the flow importer need not guess among historical tool records. Adding this field is part of the later C1 interface change, not a reason to recompute earlier gate evidence.

Graph acceptance must verify the frozen structured pilot role topology, not merely six blocks/edges. Identify entry/test/then/else/return/exit using source membership and condition/branch ownership. Verify true/false successor meaning and both branch-to-return paths, exact language-specific member order and appended native terminator. Swapped labels or edges to the wrong arm must fail even when counts and reachability still look valid. This bounded topology check does not add loop/general CFG support.

## Implementation ownership after prerequisite approval

- Root: shared Core flow binding/validator/replay and integration tests for graph/event mutations, then common native host plumbing.
- Managed/storage owner: retained Roslyn session mapping plus atomic flow import and persisted replay validation, using the reviewed shared interfaces.
- Native owner: retained AST-pointer CFG mapping and event production from the independently frozen oracle.
- Reviewer: independent read-only diff/evidence review; no ownership overlap.

Before production edits, freeze A005 and the native oracle, then agree concrete shared type signatures. Build/test each owned change in separate external directories. Preserve the m1c0-build gate artifacts; use a new directory for C1. Review must cover old M1B/C0 rejection boundaries, exact selected/unselected coverage, fresh provenance, mutation detection, rollback and reopened replay. No M1C2 solver or M1D export/query implementation starts during C1.

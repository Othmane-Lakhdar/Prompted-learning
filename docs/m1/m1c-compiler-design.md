# M1C1 compiler CFG and scalar-event design

Status: reviewed design with the independent native oracle below; this is not
an adapter implementation or M1C1 gate pass. M1C0 passed and was published at
4f1e2659be63000a1a35fe0582cae5a39e5d0a6c. The coordinator authorized the native
oracle prerequisite after that publication. A005 and the applicable review gates
must pass before compiler, validation, import or test implementation starts.

This document applies to the two frozen Choose bodies only. It preserves the
full [T04 requirements](../specifications/tasks.md#t04-derive-flow-calls-and-reachability)
and [support matrix](../m0/support-matrix.json): M1C2 may derive scalar
may-reaching definitions; indirect candidates, explicit-root reachability,
loops, captures, alias effects and advanced flow remain M3. In particular,
normalizing a direct selected declaration does not produce runtime candidates
or establish that an uncalled method is dead.

## Existing evidence and missing prerequisite

The existing [Roslyn CFG correspondence oracle](../m0/evidence/roslyn-cfg-correspondence.json)
already proves six blocks, their successors and the sixteen transformed
operation observations for Choose. All sixteen observations have one source
SyntaxNode/OperationKind candidate; none is the same IOperation instance.
Its referenced raw hash is
ea31fafb5880aeb951ad9938e0737c902bd95efa0d47a23863c861d1f93bf834.
Do not rerun this passed probe without a concrete discrepancy.

The [Clang source oracle](../m0/evidence/pilot-clang-ast-oracle.log) proves the
22 source nodes and four implicit casts. The earlier native capability test
established one Choose CFG with one two-successor branch, but did not preserve
its block IDs, ordered CFG elements or AST-pointer correspondence. Those
details were **not observed evidence** when the original design was reviewed.
The bounded [independent native oracle](evidence/native-cfg-oracle.json) now
records those details after M1C0, using the exact source hashes, target,
Clang version and CFG options below. Its [execution manifest](evidence/native-cfg-oracle-execution.json)
retains commands, tool/input hashes and successful configure/build/run evidence.
The native table addendum freezes the observations before adapter implementation.
No allocation addresses are persisted.

The native prerequisite probe was built and run once. No production CFG adapter,
storage-event implementation or scalar-dataflow test was run for this design.

## A005 required before implementation

The existing native wire has the CFG, edge, correspondence and storage-event
shapes, but its feature selection has no explicit body scope. The managed
request/fingerprint and common acceptance path are currently source-only.
The following reviewed additive amendment is required; this document alone
does not change those interfaces.

1. Add a strict optional analysis_selection object to the native request and
   managed request. Require it when cfg or storage_events is requested; forbid
   it for the source-only M1B request. Use policy choose-cfg/1 and exactly one
   selector with compiler_identity_kind=compiler, compiler_key and the complete
   primitive compiler_signature_key. The context/unit/output/producer remain
   separately bound by the request. Storage events require cfg.
2. The only allowed selectors are C#
   M:Pilot.Arithmetic.Choose(System.Int32) and native c:@N@pilot@F@Choose#I#,
   each with its exact frozen signature. Name matching alone is insufficient.
   Zero or multiple matching bodies fails selection; the host must not silently
   choose the first method. An attempted selector for Add/Unused is unsupported.
3. Include selection and requested features in the semantic-input hash.
   Recommended versioning: preserve native-worker-semantic-input/1 and
   managed-semantic-input/1 exactly for M1B; use /2 when analysis_selection is
   present, with the prior components plus that object. Native request hashes
   still cover the whole request excluding their own hash. The amendment must
   freeze this recipe and positive/negative examples before either adapter uses it.
4. Register the strict policy/settings shape, the scalar-local-events/1 replay
   policy, the limits below and explicit failure/selection reason codes.
   Reuse existing compiler_cfg_v1 provenance guarantee/assumption strings
   exactly. Do not insert free-form graph/event dumps into analysis metadata.
5. Define coverage over the full examined file as well as the selected body.
   Three nonselected callable bodies have explicit not_attempted/not_selected
   CFG results with null entry/exit and no blocks, edges or events. The selected
   Choose result may be complete. File-level control_flow and storage_events
   coverage remains partial/not_selected_bodies wherever other bodies were not
   analyzed; overall result status is partial. Source feature coverage and all
   four source bodies remain complete. The header-only native file has no
   selected executable body and receives not_attempted/no_selected_body.
   These are analysis-selection states, not source exclusions or export omissions.
6. Extend cfg_operation with the three typed member fields already required by
   the wire/prose: correspondence TEXT NOT NULL constrained to
   clang_ast_pointer/roslyn_unique_source/explicit_lowered; phase TEXT NOT NULL
   constrained to evaluation/branch_value/terminator/initializer; and
   compiler_element_kind TEXT NOT NULL with nonempty text. The existing
   block_id/operation_id/ordinal key remains unchanged. This closes a real
   lossless-persistence gap; it is not optional explanatory evidence. Update
   the field dictionary, exact DDL checks, profile-owned child mapping and
   contract/schema hashes. Historical databases are not silently upgraded or
   accepted as the new schema. explicit_lowered remains unavailable to the
   pilot despite being represented.

This deliberately avoids expanding support to all four callable bodies merely
to obtain a complete aggregate status. M1B source-only compatibility tests must
continue to pass with their original wire and hashes.

## Retained compiler session and minimal APIs

Produce source facts and requested compiler flow in the **same** compilation
session. Both products bind the same fresh producer/context/unit/source IDs.
Changing the adapter binary requires a fresh evaluated tool/context and unit;
do not attach new CFG facts to the previous M1B tool's identities. Rerun the
already-approved M1C0 normalizer over that new scope before using call facts.

The internal session retains compiler objects until source keys, CFG membership
and storage events have been materialized. No compiler object crosses the
JSON/process boundary or survives as a database key.

| Layer | Minimal responsibility |
| --- | --- |
| Managed request | Explicit requested features and reviewed selection; fingerprint them before parsing. Existing source-only defaults remain intact. |
| ManagedFactsBuilder | Retain the parentless IMethodBodyOperation, its SemanticModel, body key, source IOperation-to-key map, and source candidate index for each callable. Produce flow before disposing the compilation. |
| Native Extractor | Retain ASTContext, FunctionDecl, body key and the existing Stmt/Decl pointer-to-operation-key map. Build CFG while these objects are alive; preserve DeclStmt and VarDecl as separate source operations. |
| Common acceptance | A separate ValidateCompilerFlow(result,binding) returns a nonconstructible ValidatedCompilerFlow containing validated source scope, selected/unselected CFG results and normalized event sequences. |
| Canonical import | A separate ImportCompilerFlow/CommitFlowCollection path consumes only ValidatedCompilerFlow, allocates IDs, normalizes source call facts and validates the complete staged source/CFG collection. |
| Later scalar analysis | Consume replayed validated events and accepted CFG dependencies; do not call compiler adapters or recursively execute CFG-operation trees again. |

SemanticResultValidator.Validate and CommitSemanticCollection retain their
source-only guards. They must continue to reject nonempty control_flow rather
than ignore it. Internal source validators/mappers can be reused explicitly by
the new flow path; no public skip-validation flag or silently filtered carrier
is needed. Source and flow can be staged in one fresh transaction and accepted
atomically. Failure cannot partially overwrite earlier source coverage or
mutate an older collecting generation.

## Compiler-to-source correspondence

Native CFGStmt references are resolved through the exact retained AST pointer
map. CFG terminator and condition pointers use the same map. Missing pointers
are located unsupported correspondence, never span/kind joins. Enumerate actual
compiler blocks by getBlockID(), elements in the compiler's forward iteration
order and successors in their declared compiler order; emit explicit ordinals
and edge kinds. A null/pruned/unsupported successor is not a fabricated block.

Freeze native BuildOptions explicitly: PruneTriviallyFalseEdges=false;
AddEHEdges/AddInitializers/AddImplicitDtors/AddLifetime/AddLoopExit/
AddTemporaryDtors/AddScopes/AddStaticInitBranches/AddCXXNewAllocator/
AddCXXDefaultInitExprInCtors/AddCXXDefaultInitExprInAggregates/
AddRichCXXConstructors/MarkElidedCXXConstructors/AddVirtualBaseBranches/
OmitImplicitValueInitializers=false; no setAllAlwaysAdd override. The independent
oracle must verify this choice, since the older capability probe used defaults.
Unsupported exception, cleanup, constructor or suspension elements make the
affected graph partial; omitting their elements is not complete support.

Roslyn calls ControlFlowGraph.Create on the retained **parentless method-body**
operation, not on its nested Block. For each top-level BasicBlock.Operations
member and BranchValue, match original source SyntaxNode identity and
OperationKind within the same body, then compare primitive symbol/type keys,
implicit flag, operator/conversion facts and semantic subtree structure.
Exactly one candidate is required. Child ordinal/parent role from the new CFG
tree is not used as the source key. This matters for the return BranchValue,
which is detached from its original Return source parent. No match or ambiguity
produces partial correspondence and blocks scalar analysis of that body.

Persist source-operation references for the compiler's actual block members,
plus BranchValue/terminator references where they are not already members.
Do not serialize every recursive descendant as a separately ordered Roslyn
block member: descendant traversal is correspondence checking and event
derivation, not evidence that syntax preorder is execution order.

Native uses correspondence=clang_ast_pointer; managed uses
roslyn_unique_source. explicit_lowered is unsupported for this gate. Distinct
source nodes with overlapping spans remain distinct, including all four native
implicit casts. A duplicate compiler member may retain its own ordinal when
the compiler genuinely reports it; event deduplication is independent.

## Exact selected graph expectations

These are structural transitions, with feasibility=not_established throughout.
The source provides an entry, test, true assignment, false assignment, return
and exit. The test branches carry the comparison source operation key:
managed/native path 0/1/0. Return edges carry no condition key; their return
read remains explicitly represented by a block member/event.

The existing Roslyn oracle establishes this exact graph:

| Block | Kind | Ordered source-operation members | Outgoing edges |
| --- | --- | --- | --- |
| 0 | entry | none | continuation to 1 |
| 1 | ordinary | 0/1/0, phase branch_value, Binary | true_branch to 2; false_branch to 3 |
| 2 | ordinary | 0/1/1, phase evaluation, ExpressionStatement | continuation to 4 |
| 3 | ordinary | 0/1/2, phase evaluation, ExpressionStatement | continuation to 4 |
| 4 | ordinary | 0/2/0, phase branch_value, LocalReference | return to 5 |
| 5 | exit | none | none |

There are six blocks, six edges and four top-level/member references in this
managed representation. Its sixteen descendant correspondences must still
match the existing oracle. ConditionalSuccessor is the **false** successor
because block 1 has ConditionKind=WhenFalse; fallthrough is the true successor.
Block 4's Return branch semantics makes its edge a return, despite the edge
occupying the compiler's fallthrough property.

The native graph implements the same six-role transition relation and exact
source paths below. Its observed numeric IDs, member order and branch meanings
are frozen in the native table addendum. The probe establishes true/false
successor semantics by matching each successor's assignment AST to the actual
IfStmt then/else source subtree, independently of successor position. It does
not copy Roslyn ordinals or treat successor position as a universal branch rule.
Adapter discrepancies must be logged rather than changing this independent
oracle to fit the adapter.

## Exact scalar storage-event expectations

scalar-local-events/1 describes accesses to proven caller-local scalar storage,
not all memory effects. It emits exactly the following six events for Choose.
Ordinals are contiguous within each block. Native block IDs are replaced by
the independently observed blocks for the named roles.

| Role / managed block | Event order | Storage | Managed source path | Native source path | Phase |
| --- | --- | --- | --- | --- | --- |
| entry / 0 | parameter_definition 0 | Choose parameter value | operation null | operation null | entry |
| test / 1 | read 0 | value | 0/1/0/0 | 0/1/0/0/0 | condition |
| true / 2 | read 0 | value | 0/1/1/0/1/0/0 | 0/1/1/1/1/0 | argument |
| true / 2 | write 1 | local result | 0/1/1/0 | 0/1/1 | assignment |
| false / 3 | write 0 | result | 0/1/2/0 | 0/1/2 | assignment |
| return / 4 | read 0 | result | 0/2/0 | 0/2/0/0 | return |

Each read points to the written parameter/local reference operation, never to
an additional implicit conversion read. Each write points to its assignment
operation. Parameter entry uses the parameter entity and its retained
declaration span, including its header origin in C++. Event reason is null
for these supported events. Read spans are the reference spans; write spans
are assignment spans. No initialization definition is fabricated for the
uninitialized declaration of result. Literal values, the function-address
reference Add, and assignment destinations do not emit reads.

Use a deterministic scalar transfer walker over validated source operations:
evaluate RHS/argument/condition/return reads before a write; bare scalar
assignment-target evaluation has no read effect. Visit only the condition of
a conditional terminator, never the bodies of both branches from that node.
Clang CFG can expose both descendants and an enclosing assignment. Deduplicate
each emitted event by (block, source operation key, event kind, storage entity),
with the parameter-entry case keyed by its parameter. Already-processed reads
must not reappear when a later enclosing node is traversed after its write.
Preserve the first occurrence's order and require the replayed order to agree
with compiler evaluation for this fixture. Side-effectful operand ordering,
compound assignments, increments and non-scalar lvalues are outside this gate.

The call result is an unknown value here; an assignment still defines result.
There is no inferred value+1 range, overflow guarantee, return-value constant,
callee purity or runtime behavior claim. The policy proves only that the
selected scalar caller variables have no address/ref escape and arguments are
passed by value, so this direct call cannot be invented as an extra write to
those storage identities. Nonlocal/shared effects are outside the selected
storage scope. A by-ref/pointer/indirect call or unproven escape instead emits
an unknown_effect with the located call operation, nullable storage and an
explicit unsupported-storage-effect reason; selected event completeness then
fails. C1 does not expand into interprocedural value/effect summaries.

That tracked-storage proof is an acceptance check, not an assumption inferred
from the function name. Require the complete frozen Choose source tree, owned
builtin-int parameter/local entities, no field/shared-storage references,
address-of/ref/out/capture operations or unsupported operation, and the exact
resolved Add(int,int) declaration/signature with both parameters passed by
value. Check these facts against the accepted source records and M1C0 mappings.
Failure invalidates the selected event-completeness claim. Record
tracked_storage=owned_non_escaping_scalar and call_effect_policy=
by_value_cannot_write_tracked_storage in the registered analysis settings.
This permits six complete scalar events without a fabricated seventh local
write. It does not establish complete nonlocal effects or a known call value;
broader effect/value collections remain explicitly uncollected.

## Validation, persistence and replay handoff

The flow validator must verify unique typed keys, body ownership, one entry and
exit for complete graphs, block/member/event ordinals, all edge endpoints,
condition/source references, selected-body identity, contiguous source paths,
event storage ownership and same-body operation references. A parameter entry
may have a declaration span outside the body; this explicit entry rule does
not authorize arbitrary cross-body event references. Graph members must map to
retained source operations; source offsets alone cannot establish correspondence.

The validator independently replays scalar-local-events/1 from the source tree,
CFG member references, branch conditions and policy, comparing the complete
ordered event tuples supplied by the adapter. This prevents an omitted read,
duplicated definition or reordered assignment from silently becoming C2 input.
Unselected graphs must have no entry/exit/blocks/edges/events. When cfg and
storage_events are both requested, a selected control_flow result is complete
only if both its graph and event mapping passed; a mapping failure conservatively
makes that selected result partial. With cfg alone, events remain unrequested.

Canonical CFG payload uses analysis_run/analysis_result, cfg_block,
cfg_operation and cfg_edge. Source/body/context/tool rows remain the canonical
owners/dependencies. Do not create a new ad hoc event table or place an event
array in analysis settings, evidence, identity-input or an extension. The
current schema intentionally has no durable storage-event payload.

The A005 member columns preserve correspondence, phase and compiler-element
kind exactly, including Roslyn branch/return values versus ordinary operations
and native terminators versus CFGStmt elements. Replay uses these typed fields
alongside the source structure; it must not guess the lost phase from an
overlapping span or put member metadata in a mandatory result header.

For a selected complete CFG, use family control_flow, policy compiler_cfg_v1,
version 1 and final=1. Its analysis-result unit_id scopes the body; translation
unit/context/tool dependencies are explicit headers. Unselected/partial
results remain final=0 and cannot authorize C2. Run outcome is partial when
the requested file contains unselected bodies. The registered guarantee and
assumptions are exactly the frozen provenance strings. Retain source/compiler
evidence without embedding graph dumps into mandatory metadata.

The CFG input fingerprint binds body/source semantic content, context/unit,
producer tool/version, selection, explicit CFG options and event policy/limits.
It does not depend on its own result/block IDs or on wall-clock times.
Dependencies reference body, compilation_unit, build_context and tool headers;
do not enumerate operation dependencies merely to retain them as export anchors.

For C2, reload the retained source/CFG payload and deterministically replay the
same policy. The C2 input fingerprint additionally binds the accepted CFG result
and a canonical hash of the ordered event tuples. An in-memory C1 receipt may
carry this hash for comparison; it is not an alternative payload store.
Reconstructing events requires the canonical source and CFG, not a P1/P2/P3
projection. Older datasets remain immutable. A restart cannot substitute
different compiler pointers or an unrelated in-memory event sequence.

## Bounds and failure semantics

Register these conservative per-selected-body limits in A005 settings:
max_selected_bodies=1, max_cfg_blocks=64, max_cfg_edges=128,
max_cfg_members=512, max_storage_events=128. Existing total record, source
operation, input byte, result byte, deadline and native memory/CPU bounds still
apply. Count nested CFG blocks, members, edges and events toward the total
record budget; no nested array is free. Managed cancellation remains cooperative.
No limit is an export truncation budget.

Missing source correspondence, zero/multiple selection matches, unknown CFG
elements, unknown successor semantics, unsupported storage effects and exhausted
budgets produce explicit partial/failed selected outcomes, source diagnostics
and no complete scalar-analysis prerequisite. Valid source facts can remain
available with their original complete source coverage. A truncated/nonzero
native process result is atomically rejected, not reconstructed from stderr.
Expected aggregate partial coverage from unselected bodies is distinguishable
from an actual selected-body failure.

## Gate checks and next actions

1. Pass M1C0 and record its source-call normalization result; preserve the
   M1BSEQ001 history. Root confirms M1C1 is open.
2. Review and freeze A005 selection/fingerprint/settings/coverage semantics and
   source-only compatibility cases. No native schema or shared API is changed
   by this design-only assignment.
3. Capture the missing native CFG correspondence oracle with exact CFG options;
   freeze compiler-specific blocks/elements/edges and the six source-derived
   scalar events before implementing the native adapter.
4. Implement retained-session graph mapping and scalar events in parallel only
   after assigning separate native/managed/common-storage ownership.
5. Check Roslyn's six edges/four members/six events and native's newly frozen
   exact graph/members/six events, including BranchValue return reads and both
   true/false meanings. Verify no duplicated read/write through overlapping
   compiler elements and no false definition at the local declaration.
6. Mutate source correspondence, condition keys, successor labels, entry/exit,
   body/storage ownership, event order/duplicates/omissions, selected identity,
   coverage and budgets. Accept no partial selected result as a supported pass.
   Verify old source-only requests still reject CFG payload.
7. Reopen persisted CFGs and replay events; compare the exact canonical tuples
   and fingerprint. Independently review actual diff and evidence before C2.
   C2 is only scalar Choose reaching definitions; M3 retains broader T04 work.

Design inspection confirms the existing JSON wire has typed blocks, members,
edges and events and the SQL has CFG/result payloads but no storage-event table.
The existing SQL member table omits three required wire/provenance fields;
A005 must close that identified gap before dependent implementation.
The relative links were checked separately. M1C0 and the bounded native oracle
are now executed; A005 acceptance and production compiler-flow implementation
remain separate gates.

## Native oracle observations frozen after M1C0

The independent [probe source](../../tools/preflight/clang-cfg-oracle/main.cpp)
uses Clang 19.1.7 with the sixteen explicit false BuildOptions above and no
always-add override. The only parser-visible files are the two frozen buffers
in its InMemoryFileSystem. It verifies their original byte lengths and hashes
before parsing. The executable links the pinned Clang/LLVM libraries directly,
without the extractor, canonical storage or scalar-event implementation.

Observed on 2026-11-17: six blocks, seven forward CFGStmt members and six edges;
entry is block 5 and exit is block 0. Each of the seven member pointers, the
IfStmt terminator and its condition resolves in the same retained source AST
map. All 22 source nodes, including DeclStmt/VarDecl and four implicit casts,
remain distinct. No unmapped pointer, unsupported element, null/pruned successor
or compiler diagnostic occurred. These zero counts are actual successful probe
checks, not substitutes for missing observations.

| Block | Role | Forward compiler members (ordinal: path, AST kind) | Outgoing compiler order |
| --- | --- | --- | --- |
| 0 | exit | none | none |
| 1 | return | 0: 0/2/0, ImplicitCastExpr; 1: 0/2, ReturnStmt | 0: return to 0 |
| 2 | false assignment | 0: 0/1/2, BinaryOperator | 0: continuation to 1 |
| 3 | true assignment | 0: 0/1/1/1, CallExpr; 1: 0/1/1, BinaryOperator | 0: continuation to 1 |
| 4 | declaration and condition | 0: 0/0, DeclStmt; 1: 0/1/0, BinaryOperator | 0: true_branch to 3; 1: false_branch to 2 |
| 5 | entry | none | 0: continuation to 4 |

Block 4 has terminator IfStmt at source path 0/1 and condition path 0/1/0.
The condition is already a compiler member. Freeze the carrier mapping as
follows: all seven actual CFGStmt members retain their forward ordinal,
correspondence=clang_ast_pointer, phase=evaluation and
compiler_element_kind=Statement. Append the previously absent IfStmt reference
to block 4 at ordinal 2, phase=terminator, compiler_element_kind=IfStmt.
The carrier therefore has **eight source-operation member references**;
the raw compiler observation still has **seven CFGStmt elements**. Do not
duplicate the condition or invent a separate VarDecl CFG member. The ReturnStmt
remains an actual evaluation member while its outgoing edge is classified return.
The added terminator is a source AST reference with a distinct declared phase,
not a claim that Clang emitted an eighth CFGStmt.

The six predeclared scalar event positions map to native blocks as follows:
parameter entry to 5; condition read to 4; argument read then assignment write
to 3; false assignment write to 2; return read to 1. The probe independently
checks unique block containment of the five written source-operation paths.
It does not emit storage events or claim that scalar replay/dataflow is tested.
Its parameter observation reports the definition parameter span in the source;
the eventual entry event still uses the canonical parameter's retained
declaration span in the header, as required above.

CallExpr followed by its enclosing assignment in block 3, and ImplicitCastExpr
followed by its enclosing ReturnStmt in block 1, independently demonstrate why
replay must deduplicate reads across overlapping compiler elements. Block 4's
uninitialized DeclStmt must not invent a definition. The IfStmt terminator
must only replay its condition, never traverse both branch bodies again.

Raw and retained oracle JSON are identical UTF-8/LF bytes:
caf4a6abaaf19536e5cd897b5f5ba368ad31191d3a60adbccdce6ffb5f2102c6.
Probe executable SHA-256:
56341ae8b4e5e03cce3eadfd0f67341f3f653bc4bea373d72c3d1caa4178b534.
The build/run commands and all input/tool/source bindings are in the execution
manifest. The production native worker was not rebuilt; its prior SHA-256
954bd0629a3c21899116332346221ccf184ea74e8fb3c47c3ff2f08c5189092a
was rechecked unchanged.

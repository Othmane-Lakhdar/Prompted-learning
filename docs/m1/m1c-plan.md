# M1C ordered plan

Prerequisite M1B is published at e8c6cca9162a860f49fd2568f33f1491db4d52d6 with95/95 tests and independent review. M1C0 passed 100/100 combined tests and independent review; publication opens C1 prerequisites. C1-C3 material below is design, not completed functionality.

## C0: direct source relations

Finish the narrow T03 mapping deliberately left out of the M1B entity/operation commit. A separate source-relations collecting gate normalizes existing validated direct invocations into relationship, occurrence, call-site, selected-target and ordered argument rows. Preserve both canonical caller/callee identities, original spans/causes, exact overload and parameter mapping. Reconcile repeated import without changing IDs or duplicating occurrences.

For each frozen language, exactly one Choose invocation resolves to Add(int,int), with two source argument expressions and parameter ordinals0/1. Free native functions and static managed methods require compiler-backed eligibility. Unsupported/partial source outcomes retain located uncertainty. Runtime candidate analysis is not performed: selected_declaration resolution stays separate from not_analyzed candidates and their nonempty remainder reason. Broad populated occurrence/relation collections remain unknown when other kinds are not collected; qualified complete direct-call/body scans may be known.

Normalization fingerprints bind the exact consumed persisted source rows, scope, policy/version and actual normalizer binary descriptor under source-relations-input/1. Source compiler evidence remains linked; a separately identified normalizer tool supplies derivation evidence. Its optional detail is null under the existing policy. Do not place source/result dumps or per-operation dependency lists in mandatory evidence. New output rows/collection transitions are excluded from input fingerprints so idempotent import remains stable.

Gate: actual captured managed/native integration, exact records/arguments, partial and empty-set knowledge states, tamper/ownership/scope/rollback negatives, independent review and coherent commit. M1A/M1B commit APIs must still reject the new producer tables.

## C1: selected compiler CFG and storage events

No C1 implementation before C0 passes. See the compiler-owner design for the concrete proposal. FLOW-PILOT selects the exact Choose compiler identity and primitive signature, not a written-name match. Proposed A005 makes this scope explicit in request fingerprints while preserving source-only M1B behavior. Other bodies receive not_attempted/not_selected outcomes; file CFG/event coverage remains partial where unselected bodies exist. Complete source-feature coverage must remain separate from overall partial requested-feature coverage.

Compiler/adapter changes require new tool descriptors, contexts and final unit identities. Source and CFG must come from the same retained compiler session. M1B validation/import must not silently drop nonempty flow arrays; C1 needs a separate validated flow/collecting path and atomic scope checks.

Roslyn evidence already establishes six Choose blocks, sixteen cloned CFG observations, correct WhenFalse successor labels and the return BranchValue read. Correspondence requires unique syntax-object/kind plus symbol/type/implicit/subtree checks. Native correspondence uses original AST pointers, never span/kind joins. Exact native CFG block/member evidence is missing and is a bounded C1 prerequisite probe after C0, not an inferred oracle.

Storage events are a deterministic derivation of retained source operations and CFG membership/edges under the registered scalar policy. Validate adapter events against canonical replay; retain no hidden event dump. Choose has parameter entry definition, condition read, true-argument read, true result write, false result write and return result read. Uninitialized result declaration is not a write. Deduplicate parent/child observations once per block/source operation/event role/storage; reads precede writes. Unmodeled call/ref/alias effects block completeness or create explicit unknown boundaries.

## C2: scalar may-reaching definitions

No C2 implementation before accepted C1 CFG/event inputs. Use finite definition sets and deterministic block-ordinal fixed-point iteration. GEN/KILL applies only to proven local/parameter storage; joins union predecessor sets. Bind body/CFG/settings, event derivation version and actual producer identity in input fingerprints. Dependencies retain only permitted header IDs; no optional-payload dump in assumptions or evidence.

At Choose's return, exactly the true and false assignment definitions may reach result. Do not emit must, observed runtime values or a fabricated initializer. Parameter-entry read facts remain explicit. Synthetic solver tests cover read-before-write x=x+1, kill/join, duplicate playback, missing condition/return reads, wrong storage/body scope, unknown effects and deterministic budget/nonconvergence outcomes. These tests do not expand compiler-loop support.

FLOW-EXPANSION gates loops/alias/indirect candidates and explicit-root reachability to M3. Full T04 describes later work too; it does not authorize those features in M1C. M1CPLAN001 preserves the corrected planning history.

## C3: canonical gate

After the selected source/CFG/data-flow checks pass, complete the canonical validation/finalization prerequisite for M1D using the frozen provenance and fingerprint policy. Preserve all unrequested/partial coverage. Validate identity, foreign keys, immutable result versions, allowed dependency headers and evidence restrictions before any finalized generation is exposed. Do not implement profile projection or query behavior here.

Every step retains actual failures and incomplete prerequisites in deviations.json and PROJECT_STATE.md. Weekly quota checks apply before assignments, expensive batches and gates; at15% or unavailable reliable quota, safe pause supersedes this plan.

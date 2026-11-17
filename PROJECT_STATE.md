# Persistent project checkpoint

Updated 2026-11-17 after the M1C0 gate. Mode: ACTIVE under the weekly usage policy.

## Objective and phase

Implement the full 33-section C++/C# extraction specification: canonical records, exact captured source, explicit uncertainty/coverage, four SQLite profiles and bounded retrieval. Read the full specifications and M0 contracts; this checkpoint does not replace requirements.

M0, M1A, M1B and M1C0 passed their scoped gates. M1C0 passed 100/100 combined tests and independent review. Its coherent commit/publication is the immediate next action. M1C1 is still prerequisite work: freeze A005 and independently observe the native CFG before implementing compiler flow. M1C2 scalar dataflow, C3 finalization, M1D exports/queries, M1E benchmark and M2-M5 remain gated.

[CHECKPOINT] retains the discussion/planning return semantics of docs/checkpoints/2026-11-17-post-preflight.md. Preserve subsequent work; do not reset Git destructively. Earlier detailed progress is preserved in docs/checkpoints/2026-11-17-through-m1b-validation.md and Git history.

## Usage and stop rule

Last reliable coordinator check: 28% weekly remaining at 2026-11-17T11:26:05.691Z, codex 10080-minute window, reset 1795453198. No credits or resets used. AGENTS.md contains the complete user policy and preserves previous instructions; all agents follow it.

Continue only with reliable allowance above 15%. At 15% or unavailable quota, stop new substantive work, broadcast SAFE PAUSE, gather bounded handoffs, preserve work/process state here, give the required concise report and wait for explicit user authorization. Shutdown may use at most 10 additional percentage points; 5% is an emergency floor. Check before substantial assignments/builds and at gates. Elapsed time and token counters are not weekly quota evidence.

## Repository and publication

Checkout: C:/_Travail/tools/Prompted-Learning. External sources/toolchains/builds/reports: C:/_Travail/tools/Prompted-Learning-inputs. Branch: codex/extractor-design-implementation. GitHub: Othmane-Lakhdar/Prompted-learning.

Current published/local HEAD before the pending C0 commit: e8c6cca9162a860f49fd2568f33f1491db4d52d6, tree 518ac42231f38416d446584355cb00b50912415f (M1B). Earlier gates: M1A da35e5da506d08680f73de48408ba2b2c68b1d07; M0 a7e02b472a59da9d270e58ccf696ffc3ad95498f. All 61 M1B blobs, tree, guarded remote update and local reconciliation were verified.

Seven C0 Storage/test source files are staged. C0 evidence, plan/state/deviation updates, compiler design and M1B artifact-retention map await inclusion in the coherent commit. Original Coral-main.zip and CppSharp-main.zip remain untouched/untracked; never stage them.

Local commits work. Local HTTPS push authentication failed; use the verified connected GitHub API route: read remote head, create exact-byte blobs, verify blob/tree hashes, create commit with expected parent, guarded non-force branch update, fetch/readback, reconcile only after exact tree equality while preserving index/worktree. Never parse/rewrite contracts.json through JavaScript numbers because it contains exact Int64 limits. Raw bytes/base64 are safe. No deletion/cleanup is needed.

## Verified outcomes and artifacts

M1B: docs/m1/evidence/gate-m1b.json; 95/95 tests. Captured-only managed/native compilation, exact 22-node Choose oracles, typed source validation, atomic normalized semantic import, partial coverage and corruption detection. A002 transport-neutral carrier, A003 abstract function signatures and A004 pragma SQL support are reviewed and implemented. Contract SHA 11b8c2c29bb401b72838df7ce565c44182a9a1edb46149116b96f1b047fc94ab; SQL SHA 126c053ffc3c40827b7a236d7d362977c1b5509d0634f3515c00fea4f639dfb2.

M1B binaries and dependencies are preserved before later rebuilds at external gates/m1b. All eight gate binary hashes matched; 79 files/91,630,566 bytes retained. Mapping and inventory hash: docs/m1/evidence/m1b-artifact-retention.json. Original manifest and paths remain unchanged.

M1C0: docs/m1/evidence/gate-m1c0.json; clean Release build with zero warnings/errors; 100/100 combined tests. It adds one exact Choose to Add(int,int) selected call per language, reciprocal occurrence/relationship records, original spans/causes and two source arguments mapped to declared parameters. It retains native implicit conversion and unwraps only the managed argument wrapper. Runtime candidates remain not_analyzed. Partial scans preserve unknown occurrence sets and located reasons; broader/context incoming collections remain unknown.

Normalization fingerprints bind exact consumed persisted source rows, unit/compiler identity, policy/version and independently hashed normalizer tool. Arrays use canonical UTF8 JSON order. Derived outputs/knowledge transitions are excluded for idempotence. Derivation evidence stays separate from original compiler evidence; compiler context tools remain immutable. M1A/M1B commit APIs still reject relation tables; C0 is a collecting-only gate.

Final root artifacts:
- Build: external m1c0-build; log m1c0-integrated-build.log.
- Raw combined report: m1c0-integrated-report.json, SHA 09ec8b5f2a8dc3a1ea34daa5078bf18ebd77917d593726e223fed366eec36c68.
- Repo LF report: docs/m1/evidence/m1c0-tests.json, SHA b7c2a09f6981356f37517b0e68207d2364a10efc8979da79c4c23102c9e7c9e3.
- Frozen manifest: m1c0-reviewed-inputs.json, SHA 0caf834404f88cb8084dd4c9a5a07ca1781693cb0e26df140b5ea89e7c1d1837; all 76 source/staged bindings and 8 binary paths independently reverified after tests.
- Root normalizer DLL: SHA 791e3b96cc7c0a54e755dfce0822ab94b5727da168fadc60d13d6935e54f2cb6; both retained receipts bind it.
- Four source/destination evidence copies and exact transformations are verified in m1c0-evidence-copies.json.
- Owner final focused report4: 5/5, SHA 94803516fddd8e029c696e95c4a30bb1f96c27816c64a4cd72913fcbe6bc8ec7. Earlier failed/passing runs remain indexed in m1c0-validation-history.json.

## Decisions, errors and remaining prerequisites

M1BSEQ001, M1C0TEST001 and M1C0REVIEW001 are resolved with final integration/review evidence; failed attempts remain in docs/deviations.json. Corrections included argument-role mapping, language-specific expectations, partial occurrence completeness, missing scan summaries, unsupported broad completeness and provenance-bypass guards. M1BPUB001 records a helper's misclassification of a running exec session; absent exit_code means running, not failure. Poll its session_id.

Read docs/m1/m1c-plan.md and m1c-compiler-design.md (reviewed design SHA a3753dc47fb7f87d736e89389553fe612cd4c7f186eb34fbc8d9614bfbac0035). M1CDESIGN001 and M1CCFG001 block dependent C1/C2 implementation until resolved:
- A005 must freeze exact Choose compiler identity/signature selection, feature-bound /2 fingerprints with source-only /1 compatibility, limits/settings and honest selected/unselected coverage.
- cfg_operation must retain typed correspondence, phase and compiler_element_kind; update dictionary, ownership and historical-schema checks. Do not hide member data in mandatory evidence.
- Missing native CFG block/member/pointer oracle must be captured after C0 publication with exact options. No native block count is inferred.
- Source and CFG must share one retained compiler session and fresh producer/context/unit identities. Preserve M1B source-only rejection via a separate validated flow/import gate.
- Six scalar events require an explicit checked no-escape/by-value proof for tracked caller storage. Failed proof creates located unknown_effect/partial. No callee purity or known return value is inferred.
- Replay events from canonical source plus typed CFG under a registered policy; there is no storage_event table or hidden event dump.

C1 precedes C2, which is only scalar Choose may-reaching-definitions: exactly two assignment definitions may reach result at return, no fabricated initializer/must/runtime value. M1CPLAN001 corrected full-T04 scope confusion before code; indirect candidates, loops/alias expansion and explicit-root reachability remain M3.

## Agents and processes

- /root: integration, common interfaces, evidence/gates, commits/publication. Combined test session 58456 exited 0; manifest helper 87340 exited 0. No root build/test/write transaction remains.
- /root/m0_requirements: owns C0 Storage and SourceRelations tests. Seven files frozen; final private build clean, run4 5/5, session 87481 exited 0. Idle with no process. Next assignment only after C0 publication and bounded ownership.
- /root/m0_contracts: completed design-only compiler-flow document; independently reviewed PASS. Idle, no probe/build/product changes. Next eligible task is missing native oracle after C0 publication.
- /root/preflight_review: final independent C0 PASS after 76+8 bindings, 100 results and four evidence copies. Read-only, idle, no process. Future review stays independent.

Reusable MSBuild/compiler servers may remain idle. Do not terminate unrelated processes. There is no active critical transaction/migration.

## Commands and technical constraints

Host: external toolchains/dotnet-10.0.404/dotnet.exe (runtime 10.0.15). Build: host build PromptedLearning.slnx --configuration Release --artifacts-path C:/_Travail/tools/Prompted-Learning-inputs/m1c0-build --nologo --verbosity minimal. Tests: host external m1c0-build/bin/PromptedLearning.Tests/release/PromptedLearning.Tests.dll --group all --stage M1C0 --project-root C:/_Travail/tools/Prompted-Learning --scratch <new external directory> --report <external report>. Set PL_NATIVE_WORKER per process to external m1-native-build/Release/prompted-learning-clang-worker.exe. Native worker SHA 954bd0629a3c21899116332346221ccf184ea74e8fb3c47c3ff2f08c5189092a; Clang 19.1.7. Do not rerun passed suites without changes or a concrete concern.

Analyzed C# uses SDK 9.0.304/net8 refs 8.0.19/C#12; do not add root global.json. Host and analyzed toolchains stay separate. MSBuild VS2022 Community 17.14.19; MSVC 19.44.35215.0/toolset 14.44.35207; Windows SDK 10.0.26100. Managed cancellation is cooperative. Native Job attaches before request bytes; startup precedes attachment. Missing capture expansion requires a fresh sealed snapshot.

Pinned real resources: Coral 320c90de59d9cf81a56f3265d8a5d4a1777431ce (small, 207801 selected bytes/57 files); CppSharp 42ccd59b112072a6bcf3e1406efca174a46c8cf8 (medium, 2353348 bytes/231 files). Exact sizing/exclusions/upstream tests remain docs/testing. Their extractor expansion is M2; full CppSharp C++/CLI and upstream cleanup limitations remain disclosed. Passing upstream tests never substitute for extractor checks.

## Exact next actions

1. Validate final gate/state/doc links and JSON; stage only coherent C0 files, excluding original ZIPs. Commit and publish with exact tree verification.
2. Check reliable quota, then assign native oracle and A005 contract work with separate ownership. No C1 adapter implementation before those prerequisites are reviewed/frozen.
3. Implement and verify C1, then C2/C3 in order, keeping deviations and this checkpoint current. Continue M1D/M1E/M2-M5 only through applicable gates.
4. At SAFE PAUSE, preserve work and handoffs, report project/completed/in-progress/next/usage/warnings concisely and await explicit authorization.

# Persistent project checkpoint

Updated2026-11-17. Mode ACTIVE under the weekly policy. This is the rolling execution checkpoint; [CHECKPOINT] still returns discussion/planning to docs/checkpoints/2026-11-17-post-preflight.md while preserving subsequent work. Earlier detailed progress is preserved in docs/checkpoints/2026-11-17-through-m1b-validation.md.

## Objective and current phase

Implement all33 specification sections for C++/C# extraction: one canonical dataset, exact captured source, explicit uncertainty/coverage, four SQLite profiles and bounded retrieval. Specifications, M0 plans/field dictionary and task definitions remain authoritative; no summary replaces them.

M0 passed/published at a7e02b472a59da9d270e58ccf696ffc3ad95498f. M1A passed/published at da35e5da506d08680f73de48408ba2b2c68b1d07. M1B now passed95/95 tests and independent review: docs/m1/evidence/gate-m1b.json. Publication is the immediate next operation. M1C implementation has not begun; M1D exports/queries, M1E360trials and M2-M5 remain gated.

## Usage and stop rule

Last reliable coordinator check:35% weekly remaining at2026-11-17T10:49:33.262Z, codex10080-minute window, reset1795453198. No credits/resets used. The entire user policy is in AGENTS.md, retaining previous instructions, and all agents follow it.

Continue only with reliable allowance above15%. At15% or unavailable reliable quota, stop new substantive work, broadcast SAFE PAUSE, gather bounded handoffs, preserve changes/process status here, give the required concise report and wait for explicit user authorization. Reserve at most10 additional percentage points for shutdown;5% is an emergency floor. The initial verification hold was temporary and did not trigger SAFE PAUSE.

## Repository and preserved work

Checkout C:/_Travail/tools/Prompted-Learning; external source/toolchains/builds/reports C:/_Travail/tools/Prompted-Learning-inputs. Branch codex/extractor-design-implementation. Current published/local parent HEAD da35e5da506d08680f73de48408ba2b2c68b1d07, tree316883a8e8e8c1ffb33ef7e4d66f78ec90558872. M1B additions are staged/in the worktree and ready for one coherent feature commit after final document checks. Original Coral-main.zip and CppSharp-main.zip remain untouched/untracked and must never be staged.

Local commits work. Local HTTPS push authentication failed; use the connected GitHub API publication route: read remote head, upload exact staged bytes, compare Git blob/tree SHAs, create commit with expected parent, guarded non-force ref update, fetch/readback and reconcile local ref only while preserving index/worktree. Never rewrite contracts JSON using JavaScript numbers: it contains exact Int64 limits. Raw bytes/base64 only. No cleanup/deletion is needed.

## Verified outcomes and evidence

Final root Release build SDK10.0.404 passed0 warnings/errors; final combined95 tests passed. Groups: M1A Foundation10/Source13/Storage17/Context5/PilotGuard9/ContextStorage5; M1B CompilerContract5/Managed8/Native4/NativeSemantic4/SemanticValidation2/NativeHost4/SemanticStorage9. Earlier failed/narrower reports remain indexed in docs/m1/evidence/m1b-validation-history.json and docs/deviations.json.

Independent reviewer verified all71 source/worktree/staged bindings and8 binary paths after the run, exact full22-node oracles, all4 evidence copies and current corrections. Manifest docs/m1/evidence/m1b-reviewed-inputs.json SHA40fd4c7448b0fd4f2dd495f9038016b4d2c274f83e54ccc050a7646cad8b17c0. Final raw report m1b-integrated-final-report-2.json SHA7bff817a975820ecfd1e10216460c03a4b70ee83c65cfc0bbcb128a5cce7a4ac; repoLFcopy m1b-tests.json SHAb6075db2ff2438d2a69b8c538f7a1530b73e43f8e1465c572ebced5e69b0970c. Encodingmanifest records only verified CRLF-to-LF transformations. M0 Roslyn oracle and embeddedpilot.json remained byte-identical to published M0.

Native worker C:/_Travail/tools/Prompted-Learning-inputs/m1-native-build/Release/prompted-learning-clang-worker.exe SHA954bd0629a3c21899116332346221ccf184ea74e8fb3c47c3ff2f08c5189092a, ProductVersion1, Clang19.1.7. Managed Roslyn4.14.0.0. Current contractsJSONSHA11b8c2c29bb401b72838df7ce565c44182a9a1edb46149116b96f1b047fc94ab; SQLSHA126c053ffc3c40827b7a236d7d362977c1b5509d0634f3515c00fea4f639dfb2. A002 transport-neutral carrier, A003 abstract function signatures, A004 pragma SQL enum passed review/runtime; original M0/M1A hashes remain historical.

M1B consumes only captured bytes, verifies contexts/tools/providers and exact final input closure, emits exact pilot symbols/types/operations, and imports atomically with stable identity preimages. It preserves partial coverage spans/reasons and unknown collections. Common and persisted validators reject corrupted graphs, ownership, null applicability, signatures/qualifiers and references. Historical schemas are rejected unchanged; fresh collecting generations use A004. CommitFoundation remains M1A-only; CommitSemanticCollection does not finalize datasets.

## Boundaries, decisions and risks

All actual M1B errors/findings are resolved with final evidence and retained history. M1BSEQ001 remains an explicit handoff: operation call targets/parameter mappings are present, but normalized direct call_site/occurrence/relationship rows are not collected yet. T03 includes those source facts; T04 consumes them. Clarified M1C0 finishes that normalization after M1B publication, before compiler CFG/scalar dataflow/call-reachability analyses. Do not claim allT03 complete or assume these tables exist in M1D.

M1B is exact frozen pilot support only. Public Coral/CppSharp extractor coverage remains M2. Pinned resources: Coral320c90de59d9cf81a56f3265d8a5d4a1777431ce, small207801 selected bytes/57files; CppSharp42ccd59b112072a6bcf3e1406efca174a46c8cf8, medium2353348bytes/231files. Exact metric/exclusions/upstream build scopes remain docs/testing. Full CppSharp C++/CLI/toolchain and upstream cleanup limits persist in their preflight deviations.

Managed cancellation is cooperative with bounded pilot source size, not hard process isolation. Native host Job attaches before any request bytes, bounding input-driven framing/hash/parse/compilation; pinned runtime startup precedes assignment. Native worker has its own compilation Job/CPU budget; host deadline/output limits remain separate. Missing capture expansion returns to T02 and a new sealed snapshot; provisional facts are not persisted. No CFG/events/reaching definitions, complete canonical finalizer, profiles/queries or benchmark have run.

Host SDK10.0.404/runtime10.0.15 is separate from analyzed managed pilot SDK9.0.304/net8.0 references8.0.19/C#12. Do not add root global.json. MSBuild VS2022 Community17.14.19; MSVC19.44.35215.0/toolset14.44.35207, WinSDK10.0.26100. Preserve exact-pilot guards and explicit tool hashes when adding next producers.

## Agent handoffs and processes

- /root: Core host/shared validation, scaffolding, integration, evidence/gates/publication. Final test session80414 exited0. No root build/test/transaction remains.
- /root/m0_requirements: Managed producer, then Storage importer/validator and SemanticStorage tests. Latest9/9 private report5 passed, SHA bb26f361acbce4bd199ff55df4b07645c8c49649d898aa0de5eb41e487fda6c5. Files frozen, idle, no process. Receipts in m1-semantic-storage-tests-5; root copied reviewed hash-only receipts.
- /root/m0_contracts: native worker, Native/NativeSemantic tests and both exact oracle helpers. Latest Native4/4 report5, NativeSemantic4/4 report3, clean build8/hostbuild4. Files frozen, idle, no process.
- /root/preflight_review: final independent M1B PASS after95test report and71+8 input verification; read-only, no writes/processes. Final gate/state prose is coordinator-authored after source approval.

Reusable MSBuild/compiler servers may remain idle; no critical transaction/migration is active. Do not terminate unrelated processes. Fresh bounded ownership and reliable quota checks are required before reactivating agents.

## Commands and next steps

Host executable C:/_Travail/tools/Prompted-Learning-inputs/toolchains/dotnet-10.0.404/dotnet.exe. Build: host build PromptedLearning.slnx --configuration Release --artifacts-path C:/_Travail/tools/Prompted-Learning-inputs/m1-build --nologo --verbosity minimal. Test assembly: C:/_Travail/tools/Prompted-Learning-inputs/m1-build/bin/PromptedLearning.Tests/release/PromptedLearning.Tests.dll. Set PL_NATIVE_WORKER per process to the verified native executable, then run host testassembly --group all --stage M1B --project-root C:/_Travail/tools/Prompted-Learning --scratch <new external directory> --report <external report>. Do not rerun passing suites without changes or a concrete concern.

1. Finish final gate/state/report staging checks; publish M1B through guarded API and exact tree readback. Record resulting commit here and machine-readable state.
2. Recheck quota; read AGENTS.md, this checkpoint, milestones.json and relevant T03/T04 contracts before M1C planning. Assign nonoverlapping M1C0/CFG design tasks plus independent review only after publication.
3. Implement M1C0 direct call normalization, then compiler CFG/source correspondence, bounded scalar reaching definitions and scoped call/reachability under the approved existing contracts; log unmet prerequisites before downstream reliance.
4. Independent M1C gate/commit, then M1D/M1E/M2-M5 in order while usage policy permits. At SAFE PAUSE preserve current work and wait for explicit authorization.

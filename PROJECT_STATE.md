# Persistent project checkpoint

Updated: 17 November 2026, Europe/Paris. Execution mode: ACTIVE under the weekly usage policy.

## Objective and current gate

Implement the complete C++/C# extraction project in all 33 specification sections, one canonical dataset, exact captured source, explicit uncertainty/coverage, four SQLite profile exports and bounded retrieval. Full sources and the implementation plan remain in docs/specifications and docs/m0; this checkpoint does not replace them.

Preflight completed with declared limits. M0 passed and was published at a7e02b472a59da9d270e58ccf696ffc3ad95498f. M1A now passed: docs/m1/evidence/gate-m1a.json. M1B compiler adapters may start after that gate is published. M1C flow, M1D exports/queries, M1E benchmark and M2-M5 remain gated. The 360-trial comparison has not run.

The historical [CHECKPOINT] command still returns discussion/planning to docs/checkpoints/2026-11-17-post-preflight.md without deleting later work. This file is the rolling execution checkpoint for normal continuation.

## Usage

Last reliable coordinator check: get_usage_limits, codex weekly window10080 minutes, 51% remaining at2026-11-17T09:36:41Z; reset Unix1795453198. Initial policy activation check was57%; no resets or credits were used. Policy is in AGENTS.md without removing earlier instructions and applies to all agents.

Continue only above15% with reliable usage information. At15% or unavailable quota, stop new substantive work, collect bounded handoffs, preserve state, report and wait for explicit user authorization. The initial verification hold was temporary; reliable quota allowed continuation and did not trigger threshold SAFE PAUSE.

## Repository and changes

- Checkout: C:/_Travail/tools/Prompted-Learning.
- Branch: codex/extractor-design-implementation.
- Current published HEAD before M1A publication: 993966098c70c26fa71f63171ac5bfae826a9e9d, tree2965cb1ae56821024a0807b6d416f7e7ddca0cc8. This is the policy/checkpoint commit; its original local equivalent fbaf86fbd5169159d060ffd4b256ae811fd718ad had the exact same tree.
- M1A changes are staged for one coherent foundation commit: solution/projects, Core source/identity/context implementation, Storage,59 tests, A001 amendment, evidence, state and deviation updates. Publication is the immediate next step.
- Coral-main.zip and CppSharp-main.zip remain untouched/untracked; never stage them.
- External resources, toolchains, build products, databases and raw reports: C:/_Travail/tools/Prompted-Learning-inputs. No cleanup/deletion is needed.

Local commits work; HTTPS push authentication failed in preflight. Use the verified connected GitHub API publication route: read remote head, upload exact staged blobs, compare each blob SHA and full tree SHA, create commit on expected parent, guarded non-force update, fetch/readback, then reconcile local ref only while preserving index/worktree. Never rewrite contracts JSON through JavaScript number parsing: it contains Int64 limits. Upload raw UTF-8/base64 bytes.

## Verified M1A outcome

- Fresh Release build with private SDK10.0.404 passed with zero warnings/errors.
- Integrated suite59/59 passed: Foundation10, Source13, Storage17, Context5, PilotGuard9, ContextStorage5.
- Independent reviewer inspected the actual staged diff, checked all32 source/contract hashes,3 built binaries including deployed copies,6 frozen pilot files, and actual capture-index/manifest/6 blob bytes. No material findings remain.
- Gate evidence: docs/m1/evidence/gate-m1a.json, m1a-tests.json, m1a-reviewed-inputs.json, context-storage-evidence.json and sqlite-provider.json.
- Original execution report SHA256: 2abfb85e421076c8a86f4877143707d6621fdea0f843e7a19efabc68663266e9. Repository LF copy SHA256: be45706bcf8138da855c46e7b9d306b9a43ece86a99dadcdbb4bedc34c9ef3d2. Evidence encoding transformations and original hashes are in docs/m1/evidence/evidence-encoding.json.
- Reviewed-input manifest SHA256: c5a4f58dae1c6e32c23d50bcab1d3fc9febb5845d49f367e403afa30fabb301a.
- Actual integrated capture index: 6610083eaedef443ad14f3cfeb2eb82f95680b7b5794c4a45adb0457527327e2; source manifest: cd19d549bc11869ce6f04ed9918f801c1f8642bddf850fb8fa58ad4b2eeaf53e.
- Actual SQLite3.54.0 / Microsoft.Data.Sqlite assembly9.0.8.0; bundle2.1.14 fixed the originally vulnerable transitive package without suppressing audit.
- All three amended design validators passed: requirements19 negatives, contracts86 checks, worker6 positive/38 negative. Design reports are not product extraction claims.

A001 permits null source_manifest_hash on early failed/cancelled preparation, never on success. It is independently reviewed and tested. Amended contracts hashc2d3075882bffd7d2668495c0cfc83aeaed827f8502e905e4bf3119a3bf13faa; original M0 approval/hash99b7d121ab836d600dc42ea9cf694596d42f3349fa1168953dc8ebd67a7f6653 remains historical.

## Implementation decisions and next-stage constraints

Core implements strict canonical JSON and identity preimages, frozen runtime schemas, exact source capture/strict decoding/sealing and bounded pinned archive reads. Source search/cursors remain M1D. Storage is normalized, transactional and collecting only; no semantic finalizer or complete future collections are claimed.

MsBuildEvaluator is restricted to the frozen Release/x64 pilots. It verifies project/source hashes before/after evaluation, explicit item groups, native compiler FileVersion19.44.35215.0/toolset14.44.35207, options, compiler/analyzer/reference hashes and imported build definitions before/after. Import locators use logical roots; raw installed definition content is hashed unchanged. Only registered NuGet path fields in exact supported scaffolding are normalized. The inherited child environment is frozen; CL/_CL_ are rejected and environment values are never persisted.

ContextImport binds all captured pilot descriptors/hash/length/identity to that evaluation without rereading working source. It preserves evaluated source/reference order and explicit knowledge states. Managed compilation unit is not_attempted with observed_consumed not_collected. Native units are deferred as not_collected/native_input_discovery_not_run until M1B establishes consumed headers. Do not create provisional native unit IDs/facts in canonical storage.

M1B must add the actual analysis adapter descriptor through the explicit AnalysisTools request before final context/unit IDs, revalidate tools/providers before using them, consume captured bytes, verify final discovered input closure, and implement only exact frozen entity/operation expectations. Discovery/final passes and worker messages remain governed by docs/m0/contracts.json/md. Do not implement CFG/reaching definitions before M1C.

Host uses private SDK10; analyzed managed pilot must retain SDK9.0.304/net8.0 refs8.0.19/C#12. Do not add root global.json. MSBuild is VS2022 Community17.14.19; Roslyn adapter target4.14.0; Clang target19.1.7 with declared MSVC compatibility arguments.

All M1A deviations are now resolved with gate evidence; retain full history in docs/deviations.json, including failed runs. External limits remain: full CppSharp C++/CLI toolchain missing, upstream cleanup issue, local HTTPS push authentication, unexecuted later-stage gates. Do not silently broaden support from foundation tests.

## Test resources

Coral pin320c90de59d9cf81a56f3265d8a5d4a1777431ce: small207801 selected bytes/57 files. CppSharp pin42ccd59b112072a6bcf3e1406efca174a46c8cf8: medium2353348 bytes/231 files. Exact metric/exclusions/pins remain docs/testing/candidates.json. Upstream declared tests passed in preflight; extractor coverage on these resources remains M2.

## Agent handoffs and processes

- /root: shared Core/scaffolding/context, integration, gates/deviations/publication. Integrated build and test session45888 exited0. No active build/test/transaction.
- /root/m0_requirements: source capture/archive and PilotInputGuard/guard tests; Source13 and guard9 passed. Files frozen, idle, no active processes. Next possible bounded assignment: managed adapter or input/host validation within M1B.
- /root/m0_contracts: canonical storage/import and ContextStorage tests; Storage17 and actual integration5 passed. Files frozen, idle, no active processes. Next possible bounded assignment: native worker or semantic import, with fresh explicit ownership.
- /root/preflight_review: independent M1A PASS with hashes/archive verification; idle, no file writes/processes. Reactivate for M1B design boundary and actual implementation review after assignment.
- Reusable MSBuild nodes/compiler server may remain idle; no critical transaction or migration is active. Do not terminate unrelated processes.

Agents must reread AGENTS.md and this checkpoint, check reliable quota, and receive bounded ownership before substantial work. Keep the reviewer independent.

## Commands and precise next steps

Private host: C:/_Travail/tools/Prompted-Learning-inputs/toolchains/dotnet-10.0.404/dotnet.exe.
Build: host build PromptedLearning.slnx --configuration Release --artifacts-path C:/_Travail/tools/Prompted-Learning-inputs/m1-build --nologo --verbosity minimal.
Test runner: C:/_Travail/tools/Prompted-Learning-inputs/m1-build/bin/PromptedLearning.Tests/release/PromptedLearning.Tests.dll.
Run with host and --group all --project-root C:/_Travail/tools/Prompted-Learning --scratch <new external folder> --report <external report>.
Groups: Foundation, Source, Storage, Context, PilotGuard, ContextStorage. Do not repeat passed checks unless changes or a concrete concern justify it.
Design commands remain node docs/m0/requirements-validation.mjs and python docs/m0/validate-contracts.py / validate-worker-contracts.py --dependency-path C:/_Travail/tools/Prompted-Learning-inputs/m0-jsonschema.

1. Finish state/document staging checks and publish M1A through guarded API/tree readback. Record actual resulting commit here and in machine-readable state.
2. Recheck weekly usage and dispatch only M1B bounded adapter/host tasks with nonoverlapping ownership; read worker contracts, fixture gold and support matrix before code.
3. Implement managed/native pilot semantic producers and exact conformance/integration tests; log errors before downstream reliance; obtain independent review and commit at M1B gate.
4. Only then M1C -> M1D -> M1E -> M2-M5 under the original plan and weekly usage policy.

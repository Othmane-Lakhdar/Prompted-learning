# Persistent project checkpoint

Updated: 17 November 2026, Europe/Paris. Execution mode: **ACTIVE under weekly usage policy**. This is a rolling execution checkpoint, not a claim that M1A passed.

## Objective and gates

Implement the complete C++/C# extraction project described in all 33 inventory sections in `docs/specifications/`, using one canonical dataset, captured source, explicit coverage/uncertainty, four SQLite exports and bounded retrieval. Preserve `docs/m0/milestones.json`, the complete M0 contracts, acceptance fixtures, benchmark plan and `docs/deviations.json`.

Preflight completed with declared limits; M0 passed independent review and executable design checks. Current phase **M1A: source capture, stable identities, evaluated build context and canonical storage**. M1A integration and final review are incomplete. Do not implement M1B compiler adapters, M1C flow/finalization, M1D exports/queries or later milestones until their preceding gates pass. M1E includes the actual 360-run retrieval comparison; it has not run.

`[CHECKPOINT]` still returns discussion/planning to `docs/checkpoints/2026-11-17-post-preflight.md` without discarding subsequent work. This file preserves the latest execution state for normal resumption.

## Usage and coordination

Reliable account tool `get_usage_limits`, bucket `codex`, weekly window 10,080 minutes: **43% used / 57% remaining**, checked 17 November 2026 before policy activation. Reset Unix time: `1795453198`. No credits or resets used. The >15% condition permits continuation; the temporary coordination hold while checking quota did not trigger threshold-based SAFE PAUSE. Recheck at reasonable bounded execution checkpoints. At <=15%, or if reliable usage becomes unavailable, follow AGENTS.md shutdown and wait for explicit user authorization after the report.

All three agents received the policy hold and supplied handoffs. At handoff no agent build/test/write operation remained running. Process inspection showed reusable MSBuild nodes and a compiler server; these are not evidence of an active build and were left intact. No critical transaction or migration was active.

## Repository and preserved modifications

- Checkout: `C:\_Travail\tools\Prompted-Learning`.
- Branch: `codex/extractor-design-implementation`.
- Last committed/published implementation baseline: `a7e02b472a59da9d270e58ccf696ffc3ad95498f` (M0 gate), tree `515858ff3758dce6723d342ab0c768309af84af3`.
- Uncommitted M1A: `.gitignore`, `PromptedLearning.slnx`, `src/`, `tests/PromptedLearning.Tests/`, `docs/m1/contract-amendments.json`; modifications to `docs/m0/contracts.json`, `docs/m0/contracts.md`, `docs/deviations.json`, `docs/project-state.json`. This checkpoint and appended AGENTS.md policy are additional documentation changes.
- User files `Coral-main.zip` and `CppSharp-main.zip` remain untouched/untracked. Do not stage them.
- Third-party sources, toolchains, builds, databases and raw reports live in `C:\_Travail\tools\Prompted-Learning-inputs`, not the project Git tree.
- Local commits work. Local HTTPS push failed authentication in preflight; connected GitHub API publication was verified. Read remote head, preserve base tree, compare exact tree/blob hashes and update with expected head, never force. Do not replace dirty working files when reconciling API commits.

## Verified outcomes and decisions

- M0 frozen schema: 114 tables / 783 fields; all 33 specification sections retained. Design checks previously passed: requirements 19 negatives, contracts 86 checks, worker schemas 6 positive / 38 negative. Original reviewed contract JSON SHA256 `99b7d121ab836d600dc42ea9cf694596d42f3349fa1168953dc8ebd67a7f6653`; original M0 gate evidence remains historical.
- Two pinned real resources: Coral `320c90de59d9cf81a56f3265d8a5d4a1777431ce`, small 207,801 bytes / 57 selected files; CppSharp `42ccd59b112072a6bcf3e1406efca174a46c8cf8`, medium 2,353,348 bytes / 231 files. Upstream checks passed within documented scopes; these do not establish extractor coverage. Full CppSharp C++/CLI remains blocked by missing toolchain and its incremental cleanup issue remains recorded.
- Shared Core canonical JSON/stable IDs/runtime schema checks implemented. Foundation suite **9/9 passed** in `..\Prompted-Learning-inputs\m1-foundation-tests-report.json`. Handles exact UTF-8 ordering/escaping, Int64-only numbers, malformed UTF-16, validated identity envelopes, prefixItems/contains semantics and unknown schema keywords. Final integrated review is pending.
- Source owner reports latest exact suite **13/13 passed**, report `..\Prompted-Learning-inputs\m1a-source-tests\source-results-final.json`; earlier Core Release build had zero warnings/errors. Captures evaluated input lists, strict raw/text decoding, verified pinned archive handles, sealing and bounded preparation. Source search/query/cursors remain M1D.
- Storage owner reports latest private build zero warnings/errors and suite **17/17 passed**, report `..\Prompted-Learning-inputs\m1-storage-tests-report-2.json`; earlier failed report preserved as `m1-storage-tests-report.json`. Normalized SQLite creation/import/identity validation/rollback implemented. Minimum native SQLite >=3.50.2 test passed; record exact version during integration. Store remains collecting, without later-stage completeness claims.
- M1A contract amendment A001: failed/cancelled source preparation may have null manifest hash before a trusted index exists; passed preparation still requires real 64-hex hash. This removes fabricated all-zero evidence. `docs/m1/contract-amendments.json` preserves M0 hashes. Amended contracts JSON SHA256 `c2d3075882bffd7d2668495c0cfc83aeaed827f8502e905e4bf3119a3bf13faa`. Reviewer found amendment structurally sound; rerun design checks and inspect fresh report evidence before closing.
- NuGet restore exposed vulnerable SQLitePCLRaw transitive 2.1.10. Explicit bundle 2.1.14 restored without suppressing audit; Microsoft.Data.Sqlite remains 9.0.8. Actual behavior/version and final review remain required.
- Host SDK is private .NET 10.0.404; analyzed C# pilot retains SDK 9.0.304, net8.0 reference pack 8.0.19, language 12. Do not add a root global.json that changes pilot SDK selection.
- Context identity fingerprint is SHA256 of canonical identity components without LF. Context setting compiler_option uses literal descriptor value (empty name) or normalized path descriptor option plus `<root_key>/relative_path`. Provider dependency path is normalized. Retain typed descriptors in identity inputs.

## Assignments, handoffs and pending findings

| Agent | Ownership / latest status | Next action |
|---|---|---|
| `/root` | Shared Core, scaffolding, `Core/Build/MsBuildEvaluator.cs`, FoundationTests/ContextTests, integration, gates, deviations, publication. Latest root build output after adding evaluator/context tests was truncated; do not assume it passed. | Inspect files/status; run one fresh integrated build and bounded suites; integrate actual evaluated contexts/providers/units with captured source/store. |
| `/root/m0_requirements` | `Core/Source/*.cs`, `Tests/Source/SourceTests.cs`. Source13 passed, active session50956 exited0. Nullable failure hash and early cancellation/index failure tests done; junction escape test replaces unavailable privileged symlink; CS4007 span-await compile error fixed. Duplicate exact exclusions now rejected. No active operation at handoff. | Review fresh exact source report; resolve only concrete integration/review findings. |
| `/root/m0_contracts` | StorageSchema, CanonicalStore, FoundationValidator, CapturedSourceImport and StorageTests; root owns csproj. Storage17 passed, build39857/test29002 exited0. Collection role names, shared blobs, unknown vs not_collected and reversed exclusions corrected. | Independent verification of current fixes, then root context/source ingestion integration. |
| `/root/preflight_review` | Read-only independent review. Prior M0 approved. Root Core fixes reviewed; latest combined report and context sufficiency pending. No active processes or changes. | Verify current exclusion correction/importer states against fresh tests; review bounded context options/tools/providers/imported inputs and actual integrated evidence; give gate verdict only when complete. |

Keep separate write ownership; no agent commits/state changes without coordinator agreement. Read updated AGENTS.md before restarting substantial work. Agent reports above are handoff evidence, not a substitute for coordinator inspection and independent gate approval.

Open deviations include M1CORE001/002, M1BUILD001/002, M1SOURCE001, M1STORE001/002/003, M1DESIGN001, M1TEST001; M1OPS001 (wrong output path) is resolved. Read exact entries for status/evidence before closing. Newly observed exclusion-order mismatch and SourceTests CS4007 need explicit history if not already logged. An apply_patch path typo caused no changes, then was corrected by source owner.

Context evaluator is new and **not yet verified as complete**. It evaluates the exact pilot via MSBuild, captures selected properties/items and hashes references/tool. Review compiler/tool identity (currently MSBuild plus toolset properties), imported build input closure, full relevant option coverage and path-sensitive semantics. Do not generalize pilot support or treat missing inputs as empty. Root has not yet integrated actual evaluated providers/context/source records into a canonical store.

## Commands and exact continuation

Private host: `C:\_Travail\tools\Prompted-Learning-inputs\toolchains\dotnet-10.0.404\dotnet.exe`.
MSBuild: `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`.

```powershell
& 'C:/_Travail/tools/Prompted-Learning-inputs/toolchains/dotnet-10.0.404/dotnet.exe' build PromptedLearning.slnx --configuration Release --artifacts-path C:/_Travail/tools/Prompted-Learning-inputs/m1-build --nologo --verbosity minimal
& 'C:/_Travail/tools/Prompted-Learning-inputs/toolchains/dotnet-10.0.404/dotnet.exe' 'C:/_Travail/tools/Prompted-Learning-inputs/m1-build/bin/PromptedLearning.Tests/release/PromptedLearning.Tests.dll' --group all --project-root C:/_Travail/tools/Prompted-Learning --scratch C:/_Travail/tools/Prompted-Learning-inputs/m1-integrated-tests --report C:/_Travail/tools/Prompted-Learning-inputs/m1-integrated-tests-report.json
node docs/m0/requirements-validation.mjs
python docs/m0/validate-contracts.py --dependency-path C:/_Travail/tools/Prompted-Learning-inputs/m0-jsonschema
python docs/m0/validate-worker-contracts.py --dependency-path C:/_Travail/tools/Prompted-Learning-inputs/m0-jsonschema
```

1. Read AGENTS.md, this file, machine-readable state, relevant M0 plan/contracts and deviation entries. Verify branch/status and current usage; no tests merely for rereading a checkpoint.
2. Reactivate reviewer for current Source13/Storage17 evidence and context sufficiency. Source/storage owners address concrete findings only within M1A.
3. Verify root evaluator/context tests, integrate pilot capture/context/provider/unit/store, preserve failures and exact new evidence. Root owns `m1-build`; agents use separate artifact directories.
4. Run appropriate final M1A checks plus amended contract validators once stable. Review actual diff and evidence, resolve only demonstrated fixes, then commit/publish coherent changes. M1B remains blocked until M1A gate passes.
5. Continue M1B -> M1C -> M1D -> M1E and later milestones under their gates and weekly quota checks. Update this checkpoint at meaningful changes and before any safe shutdown.

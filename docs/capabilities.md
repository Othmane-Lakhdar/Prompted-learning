# Git and build capability audit

Updated 16 November 2026 after execution on the user's Windows checkout. The [earlier cloud audit](preflight/2026-11-16-cloud-audit.md) is retained as history.

## Result

Local execution, pinned clones, dependency restore, native/managed builds, Roslyn semantic access, Clang AST/CFG access and SQLite are verified within the scopes below. Preflight can hand over to M0 with explicit limits. M0 is not complete and extractor implementation has not started.

Coral passed 41 native checks and 19 managed checks in each of two assembly contexts. CppSharp's native parser/C# P/Invoke generator-test subset built and ran: 91 passed, 1 explicitly ignored upstream. Its full Windows solution and C++/CLI tests remain unverified because the C++/CLI component is absent. This adaptation supplies a real mixed-language build/test input without establishing whole-repository or extractor coverage.

See the [local report](preflight/2026-11-16-local/README.md), [command records](preflight/2026-11-16-local/commands.json), and [deviations](deviations.json). A passed probe is evidence only for its stated scope.

## Git and repository capabilities

| ID | Current evidence and boundary |
|---|---|
| G01 | Passed: local instructions, refs, history and remote inspected; origin fetched and existing work branch selected. |
| G02 | Passed: exact candidate revisions cloned; all 288 manifest-listed blob IDs and sizes verified. |
| G03 | Passed: authorized branch now has a local tracking checkout. |
| G04–G05 | Earlier GitHub API commit/publication path verified. Local commit/push evidence is tracked in project-state.json after delivery of this audit. No force push or main-branch update. |
| G06 | Passed: actual nonempty local diffs and Git history inspected. |
| G07 | Passed: Git-protocol clone/fetch/checkout. No gitlinks in either pinned tree; no LFS transfer required. |
| G08 | Local edits and branch switching work. Worktree creation and conflict resolution not exercised. Commit/push recorded separately. |
| G09 | Historical API policy observations retained; ordinary work-branch publication only. |
| G10 | PR creation/merge not run; unnecessary for preflight. |

## Execution and build capabilities

| ID | Current evidence and boundary |
|---|---|
| E01 | Passed: PowerShell, filesystem writes, exit capture and environment restoration. The old failed cloud environment is no longer this chat's execution path. |
| E02 | Passed: source hashing and binary ZIP round-trip. About 224 GB free initially; scale/peak-memory sufficiency not established. |
| E03 | Exit codes, durations and logs verified, including deliberate exit 7 and launch failure. Cancellation, timeout and atomic product publication remain later obligations. |
| E04 | Passed: GitHub clones, Premake/LLVM downloads and NuGet restore. Other future endpoints not assumed accessible. |
| E05 | Passed: installed tools selected and private SDK 10.0.404 extracted with verified official SHA-512. No global Visual Studio component installation. |
| B01 | Passed: SDK 9.0.304/net9 probe; SDK 9 plus runtime 8.0.19 for CppSharp net8; private SDK 10.0.404/runtime 10.0.15 for Coral net10. |
| B02 | Passed: Roslyn 4.14.0 parses a program, reports no compiler errors, resolves Demo.Twice(int) and exposes its invocation operation. Full adapter mappings await M0/M1. |
| B03 | Passed: MSVC 19.44.35215, Windows SDK 10.0.26100.0, C++17 compile/link/run and candidate native builds. |
| B04 | Passed: CMake 3.31.6-msvc6, VS generator, build and nonempty CTest runs. Coral has no CTest registration; its executable is checked separately. |
| B05 | Bounded probe passed: downloaded Clang 19.1.7 libraries link into an MSVC executable producing an AST and CFG (five blocks, one branch). No standalone clang executable installed. Broad language/MSVC compatibility and final adapter choice remain M0 gates. |
| B06 | Native Windows/VS2022/MSBuild path passed. C++/CLI absent; full CppSharp suite blocked, declared P/Invoke subset tested. |
| B07 | SDK property/item evaluation and generated CppSharp net8/x64/Release/CI configuration verified. Complete build-context extraction not implemented. |
| B08 | Build logs and a CppSharp binlog captured locally. Binary/source mapping is not implemented or accepted; M4 remains separate. |

## Tests, storage and automation

| ID | Current evidence and boundary |
|---|---|
| T01 | Coral scoped run passed; CppSharp generator subset passed with one upstream ignore. Process exit alone is not treated as test success. |
| T02 | Extractor fixtures and expected facts await M0/M1. Capability probes are not extractor acceptance tests. |
| T03 | SQLite 3.46.1 through Microsoft.Data.Sqlite 9.0.8 passed native load, dangling-reference rejection and rollback; binary ZIP round-trip passed. Product profile/schema invariants remain unimplemented. |
| T04 | Product queries and AI transport not implemented or exercised. |
| T05 | Controlled AI benchmark execution remains unverified; specify its route in M0. This chat is not the planned automated comparison. |
| T06 | Git blob sizes rechecked, command durations and raw-log hashes retained. Peak-memory/scale measurements remain future work. |
| C01 | Historical CI read evidence retained; no new CI run claimed. |
| C02–C03 | CI write/run/log/artifact execution not exercised. Local execution is now the verified build path. |
| C04 | Durable state/history/deviations maintained with independent review of source, results and the actual diff. |

## Remaining gates

M0 must specify feature coverage, identities, schemas, query/profile contracts, fixtures and benchmark access. Upstream tests do not satisfy those requirements. Full CppSharp Windows coverage requires C++/CLI and its own tests. Its ignored test and resource-cleanup defect remain recorded. Fresh copied resource files were verified, but incremental cleanup was not accepted.

Coral's initial CMake build mixes configurations for testing/example managed projects. The verified test deployment explicitly rebuilds managed tests and their reference in Release beside the native test executable.

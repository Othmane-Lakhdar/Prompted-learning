# Post-preflight checkpoint

Recorded on 17 November 2026 (Europe/Paris) at the user's request, before autonomous design and implementation. This is the active reference for `[CHECKPOINT]`, replacing the earlier checkpoint as the active target while preserving its history.

## Return semantics

When the user sends `[CHECKPOINT]`, stop ongoing project work and agent assignments, return discussion and planning to this baseline, and summarize later work separately. Preserve later files and commits. This signal does not itself request deletion or a destructive Git reset.

## Exact baseline and current stage

- Repository: [Othmane-Lakhdar/Prompted-learning](https://github.com/Othmane-Lakhdar/Prompted-learning).
- Baseline commit: `4f194253e7a4a01ffa2c6452d62ce2d12b781d49`, on `codex/extractor-preflight-m0` when this checkpoint was requested.
- New work branch: `codex/extractor-design-implementation`, created from that exact baseline for the checkpoint and subsequent work.
- Preflight: completed with declared scope limits. M0 contracts are not complete; extractor implementation has not started.
- The tracked working tree was clean. The user's `Coral-main.zip` and `CppSharp-main.zip` remain untracked and untouched.
- Verified source clones, build products, private SDK and original logs are outside the project checkout in `C:\_Travail\tools\Prompted-Learning-inputs`.

## Requirements retained

The [historical checkpoint](2026-11-16-preimplementation.md) retains the specification links and architecture. The complete inventory and sub-elements remain authoritative requirements: 16 BD0 sections, 7 BDG sections and 10 BDB sections; BDR remains deferred. This checkpoint does not replace the full specification with a summary.

Retain one canonical dataset, immutable captured source, exact byte spans and contextual identities, explicit uncertainty and coverage, bounded queries, and the original four SQLite profile exports. P1 omits BD0.7/12/13; P2 omits BD0.7/12; P3 omits BD0.7; P4 omits none. All other sections remain included. Source exclusions, incomplete analysis, export omissions and query pagination remain distinct.

## Verified capabilities and limits

The [preflight report](../preflight/2026-11-16-local/README.md), [capability audit](../capabilities.md) and [publication evidence](../preflight/2026-11-16-local/publication.json) record executed checks. These results were obtained on 16 November; this checkpoint does not claim a new build run.

- Branch creation, local commits and publication through the connected GitHub tools work. Local HTTPS Git push failed authentication; its API alternative was verified by fetching and comparing trees.
- Both pinned public repositories were cloned and all 288 selected source blobs verified.
- MSVC compilation, Roslyn symbol/operation access, a bounded Clang AST/CFG probe, SQLite integrity/rollback and binary source-archive round-trip checks passed.
- Coral: 41 native checks and 19 managed checks in each of two assembly contexts passed using the documented private .NET 10 deployment.
- CppSharp: the native parser/C# P/Invoke generator-test subset built; 91 tests passed, one upstream test was ignored, and none failed. Full Windows testing remains blocked by missing C++/CLI support. The resource-cleanup defect remains a limit on incremental builds.
- Upstream builds and tests do not establish extractor correctness, general MSVC/Clang or C++/CLI compatibility, scale, cancellation/atomic export, or automated AI comparison results.

## Test resources and size

Retain the pinned [resource manifest](../testing/candidates.json) and [build recipes](../testing/README.md). Each resource contains substantive C++ and C#.

| Resource | Exact revision | Selected source bytes | Files | Class |
|---|---|---:|---:|---|
| Coral | `320c90de59d9cf81a56f3265d8a5d4a1777431ce` | 207,801 | 57 | Small |
| CppSharp | `42ccd59b112072a6bcf3e1406efca174a46c8cf8` | 2,353,348 | 231 | Medium |

Size is the sum of selected tracked Git blob byte sizes at the pin, including comments, whitespace and headers. Small is greater than zero and at most 256 KiB; medium is greater than 256 KiB and at most 8 MiB. Exact paths and exclusions are recorded in the manifest. This measurement does not authorize omitting required extraction inputs. Controlled fixtures with explicit expected facts and deliberate failure cases must complement these resources.

## Authorized execution and stage gates

The user authorizes autonomous design and implementation on the new branch, readable focused commits, and specialist agents, after receiving the requested capability confirmation. Routine work within this scope does not require another permission request.

1. M0: account for every inventory sub-element; define field dictionary, schema/identity/source contracts, producer mappings, language/build support, profile/query contracts, fixtures, acceptance tests and the controlled AI comparison route. Independently review these before their dependent implementation.
2. M1: implement and verify the declared end-to-end pilot against the completed applicable contracts; begin size and AI retrieval comparisons with explicit execution evidence.
3. M2: expand source and organizational coverage. M3: expand analyses. M4: add build evidence. M5: validate realistic scale and release readiness.

Do not implement later-stage features early or silently reduce requirements. A required unmet prerequisite blocks its dependent work. An adaptation needs a recorded rationale and evidence that it preserves the affected requirement.

Assign agents bounded roles and separate write ownership within the current stage: design/contracts, implementation of agreed tasks, test expectations, and independent adversarial review. The coordinator integrates changes and checks gates. Parallel work is appropriate only where dependencies allow it. Agent agreement is not a substitute for executed checks.

Keep commits scoped to coherent tasks with purpose, validation and material limits. Preserve existing content, read the branch head before publication, and use guarded non-force updates. Keep third-party trees, build outputs, databases and credentials outside commits.

## Persistent deviation handling

Continue [docs/deviations.json](../deviations.json); preserve resolved history. Record errors and partial or unmet results with expected versus observed behavior, evidence, affected requirements and downstream tasks, blocking status, corrective action/adaptation, and verification outcome. Record failures before downstream work relies on their results. Unknown, not run and skipped checks are not passes or zero results.

Current limitations include local HTTPS Git authentication, full CppSharp C++/CLI coverage, the resource-cleanup defect, and later-stage checks listed in the preflight report. The pending M0 contracts remain prerequisites, not completed deliverables. Keep [project-state.json](../project-state.json) current as milestones advance.

# Git and build capability audit

Audit date: 16 November 2026. Target: Othmane-Lakhdar/Prompted-learning.

## Result

GitHub API branch creation, multi-file commit creation, guarded branch update and pinned file readback have now been exercised successfully. The first verified commit is 0d7f481f21e74e3aabf5c9dc9a14f95b5175d0a4 on codex/extractor-preflight-m0.

Autonomous compilation and testing are not yet available through a verified execution path. A direct shell probe failed before command launch. GitHub Actions is a possible alternative, but no workflow execution has been demonstrated. Cloning repositories on the user's computer does not automatically expose that filesystem or execution environment to the assistant.

This audit changes no extractor implementation stage: preflight is in progress and M0 is not complete.

## Status meanings

- Verified: the stated operation actually succeeded, with the limited evidence described.
- Reported: a permission or policy is advertised but the corresponding operation was not demonstrated.
- Unverified: a working path has not been tested; this is not proof that a tool is absent.
- Blocked: an observed prerequisite currently prevents the check.
- Conditional: needed only for selected inputs, formats or deployment choices.

## Git and repository capabilities

| ID | Required capability | Stage | Check and result |
|---|---|---|---|
| G01 | Identify the intended repository and read its contents, instructions, refs and history | Preflight | Verified through GitHub API reads. Initial main SHA: 7bb07d6680bb78ee45e3da86ebd2b9ce52c176ad. Root contained LICENSE and README.md; no root AGENTS.md. |
| G02 | Read public test-resource repositories and pin exact source revisions | Preflight and M0 | Verified public metadata/source/tree reads. Candidate revision and source-size evidence is recorded in docs/testing/candidates.json. This does not establish a Git clone. |
| G03 | Create an isolated work branch | Preflight | Verified: codex/extractor-preflight-m0 created from the recorded main SHA. |
| G04 | Create coherent multi-file commits while preserving the base tree | Preflight onward | Verified through Git tree and commit creation. Bootstrap commit added only AGENTS.md, a checkpoint and project-state.json. Readback of all three files matched their authored contents. |
| G05 | Publish a commit and protect against a changed branch head | Preflight onward | Verified API ref update with force=false and the expected previous SHA; resulting branch head matched the created commit. A deliberately conflicting update was not tested. |
| G06 | Compare revisions and inspect diffs | Preflight onward | History and commit-diff APIs succeeded. Initial comparison was a SHA against itself and returned identical; that alone was not a nonempty-diff test. |
| G07 | Clone/fetch/checkout pinned inputs using Git, including submodules or LFS when required | M1 inputs; conditional extras | Blocked locally by missing execution. The user proposes performing the clones. Submodule/LFS requirements must be checked per candidate; no transfer success is claimed. |
| G08 | Local edits, local commits, Git-protocol authentication/push, isolated worktrees and conflict resolution | If using a local development checkout | Unverified. Successful GitHub API writes do not verify these local/protocol operations. API authoring is an available alternative for the current documentation work. |
| G09 | Inspect branch restrictions and operate within repository policy | Before remote writes | main reported protected=false and the rulesets endpoint returned an empty list. Account permission reported admin/push. A branch-rules endpoint was rejected by the connector allowlist; see deviations. |
| G10 | Open/read/update a PR when a review deliverable is requested | Integration | PR listing succeeded and returned none; write tools are available but PR creation was not exercised. No merge is implied by the work-branch authorization. |

## Execution and build capabilities

| ID | Required capability | Stage | Check and result |
|---|---|---|---|
| E01 | A usable command runner with writable workspace and process control | Before any build | Blocked. tools.exec_command with cmd=pwd and workdir=/workspace failed before execution: No usable execution environment is available. Wait for an environment to become available before using this tool. |
| E02 | Retain exact source bytes and generated inputs; hash/archive files; sufficient disk, CPU and memory | M1 onward | Unverified because commands and filesystem are unavailable. Current source-index.json and older workspace backups are not assumed to exist. |
| E03 | Capture exit status, stdout/stderr, durations, memory, cancellation and timeouts; preserve old valid outputs and publish atomically | M1 through M5 | Unverified execution capabilities and future implementation obligations. A successful compile alone would not verify recovery behavior. |
| E04 | Download source and restore dependencies over working TLS/proxy/network paths | Before candidate builds | Policy is enforced and permits relevant GitHub, .NET, NuGet, LLVM and Ubuntu hosts. Actual clone, package restore and redirected downloads have not run. Allowlisting is not a successful connection test. |
| E05 | Install or select reproducible SDK/compiler/build-tool versions and required packages | Before candidate builds | Unverified. Package-manager privileges, native library availability and exact installed tool versions have not been inspected. Containers are optional, not a baseline prerequisite. |
| B01 | .NET SDK and CLI restore/build/test plus the runtime required by test targets | M1 C# pilot | Unverified. Candidate-specific SDK and target/runtime requirements are documented with their pinned source. No dotnet --info or build/test result exists. |
| B02 | Usable Roslyn semantic APIs and compiler/reference assemblies | M1 C# extraction | Unverified. Must restore compatible packages and demonstrate symbol/type/operation access on controlled examples; .NET availability alone is insufficient. |
| B03 | C++ compiler, linker, SDK and standard-library headers | M1 C++ pilot | Unverified. Must compile/link/run a small C++ program and the chosen resource with the declared options and target. |
| B04 | CMake and a supported build driver such as Ninja, Make or MSBuild; CTest | Candidate C++ build | Unverified. Must configure, build and run the actual upstream tests; obtain usable compilation options such as compile_commands.json when supported by the generator. |
| B05 | An appropriate Clang frontend development interface and compatible libraries | M1 C++ extraction | Unverified. A clang executable or libclang being present would not prove access to every required semantic or flow construct. The chosen adapter interface must be demonstrated against its declared features. |
| B06 | Windows, Visual Studio 2022/MSBuild, MSVC toolset, Windows SDK and applicable .NET targeting packs/workloads | Required for the retained Windows/MSVC pilot | Unverified. A portable Linux build does not satisfy this compatibility gate. A Windows local or CI runner is required for the corresponding checks. |
| B07 | Evaluate build configurations, imports, references and per-file settings; execute required generators and capture generated inputs | M1 onward | Unverified. Must verify actual SDK/reference selection, conditional configurations, generated-source origins and compilation-unit options rather than assume listed files were compiled. |
| B08 | Read build logs and supported executable/assembly/symbol/debug formats | M4, according to supported formats | Unverified. Plan appropriate MSBuild log readers, .NET metadata readers and LLVM/MSVC inspection tools. Do not implement M4 features during preflight. |

## Testing storage queries and automation

| ID | Required capability | Stage | Check and result |
|---|---|---|---|
| T01 | Run upstream candidate tests and preserve their logs and exit codes | M1 resource validation | Unverified. User-local runs can supply evidence if the source revision, tools, configuration, exact commands and logs are recorded. They are not assistant-executed builds. |
| T02 | Run controlled extractor fixtures with expected facts, negative cases and explicit uncertainty | M1 onward | Unverified; fixtures and contracts belong to M0/M1. Upstream tests validate the resource, not the correctness of the extractor's records. |
| T03 | SQLite provider/native dependencies, schema constraints and transactions; archive/export readback | M1 onward | Unverified. A SQLite server is not required; an appropriate library/provider is. Must test references, states, reproducibility and profile equivalence. |
| T04 | Run the bounded query service and connect it to an AI host through an explicit supported interface | M1 onward | Unverified. CLI/API or MCP can provide a transport; MCP is not a mandatory baseline choice. Source retrieval and profile isolation still require tests. |
| T05 | A reproducible AI evaluation interface with recorded model/prompt versions, budgets, tool interactions and token usage | Pilot experiment and later comparisons | Unverified. This chat alone does not prove that the specified automated benchmark can be executed reproducibly. Monetary cost may be reported unknown when unavailable. |
| T06 | Measure source scale, bytes, timings and memory with versioned rules; preserve exact revision/configuration/dependency information | M0 through M5 | Source-blob size can be measured via GitHub tree metadata and is recorded for candidates. Build/run measurements remain unverified. Pinning Git alone does not pin all dependencies or compilers. |
| C01 | Read CI runs and commit statuses | Preflight | Verified API access; initial target repository had zero runs and no commit statuses. Empty results do not establish successful CI or runner availability. |
| C02 | Write workflows, trigger a run, acquire appropriate Linux/Windows runners and perform dependency restore/build/test | Possible execution alternative | Unverified. Workflow write scope, Actions policy, runner availability and execution were not tested. Branch/content commit success does not prove workflow write permission. |
| C03 | Retrieve CI jobs, logs and artifacts and rerun failures | If CI is used | Tools are exposed, but there are no target run/job/artifact examples to verify the full path. No dedicated workflow-dispatch tool is exposed; a push trigger is a possible, unverified alternative. |
| C04 | Preserve durable failure records and stage gates while collaborating | Now onward | Repository checkpoint, instructions, state and this audit are being persisted through GitHub. Execution failures and unsupported connector endpoints are recorded in docs/deviations.json. |

## Checks that establish execution readiness

1. Obtain a usable local/remote command runner or demonstrate a real CI run. A clone on the user's computer is not automatically an assistant-accessible runner.
2. In that environment, record Git, .NET SDK/runtime, C++ compiler, CMake/build-driver and platform versions. Verify package restore with the actual clients.
3. Clone the pinned candidate revisions and confirm their HEAD SHAs; build the documented target subset and run its tests.
4. Exercise Roslyn and the selected Clang frontend on controlled semantic examples. Verify the required Windows/MSVC context independently.
5. Exercise the SQLite provider and, at the appropriate milestone, profile production and querying.
6. Record every command and result. A missing capability must remain blocked or unverified until a relevant check succeeds.

## Evidence limits

The earlier status API reported starting with no failure reason. A subsequent selected-environment notification reported failed: failed to query executor configuration capabilities for environment ccarenv_b64_Y2NhcmVudl9iNWQ3ZTlhYThkZDQ4MTkxOGQ0MjNhMmJlNjAwN2E5ZQ. Diagnostic metadata reported no retained captures and no legacy snapshot. This identifies the failed readiness operation, not its underlying cause. No raw diagnostic logs were accessed.

The GitHub connector rejected GET /actions/workflows and GET /rules/branches/main with HTTP 400 INVALID_ARGUMENT, stating that the URL is not an allowed public GitHub repository or search endpoint. Those are connector endpoint restrictions, not proof of repository denial. Successful run, branch and ruleset reads provide limited alternatives.

The user now proposes cloning the resources and project branch. That supports a shared workflow: the assistant can author verified GitHub commits; local build execution still needs an accessible runner or user-supplied results. No build is claimed here.

# Preimplementation checkpoint

This is the historical checkpoint requested on 16 November 2026. Statements about repository state describe that checkpoint, before this branch was created. Current operational status is tracked separately in docs/project-state.json and the capability/deviation records.

Checkpoint recorded on 16 November 2026 before autonomous implementation begins. The project is at the transition from an information inventory and task plan to M0 specification work. No extractor implementation, completed M0 schema, extraction run, profile database or verified build has been produced in this chat.

# Meaning of CHECKPOINT

When the user sends [CHECKPOINT], pause ongoing project work and return the discussion and planning baseline to the state recorded here. Summarize any work produced since this checkpoint separately. This command does not, by itself, authorize deleting files, resetting Git history or discarding later commits. The checkpoint restores the agreed reference state for discussion and decisions.

# Project documents

| Document | Role |
|---|---|
| [Reference inventory for C++ and C# code extraction](https://chatgpt.com/space/page_477a6bc92bbc819192d77a641cb836df) | Complete original proposed inventory: 16 BD0 sections, 7 BDG sections and 10 BDB sections, including sub-elements and verification conditions. |
| [Draft implementation plan](https://chatgpt.com/space/page_2db1f4e640908191b8e24bd99c144dfa) | Architecture, storage rules, export profiles, size controls, queries, milestones and acceptance checks. |
| [Draft task specifications and graphs](https://chatgpt.com/space/page_de720b1a53ec8191953b38ed47f4c702) | Latest task organization: T01–T12 with inputs, outputs, dependencies, checks and details to specify; preserves the baseline and all 33 inventory sections. |

The latest task document has 175 stored blocks. Its task graph and analysis feedback graph are saved. Its content was checked against the implementation draft. These documents remain proposals whose field-level contracts and implementations must be completed and verified.

# Repository baseline

The user identified [Othmane-Lakhdar/Prompted-learning](https://github.com/Othmane-Lakhdar/Prompted-learning) as the dedicated project repository. It is public, uses main as its default branch, and currently contains README.md and LICENSE at the root. No root AGENTS.md was found. Preserve existing content and license.

The observed main branch commit is 7bb07d6680bb78ee45e3da86ebd2b9ce52c176ad. The connected account reports administrator and push access. No project branch or project commit has been created by this preflight check. Actual successful write operations have not yet been tested.

# Scope and architecture retained

Build an executable for selected C++ and C# codebases. BD0 describes source entities, members, locations, relationships and static analysis results. BDG supplies organizational groups and derived views. BDB records build contexts, tools, dependencies, actions and evidence. Application runtime observation, BDR, remains deferred.

Define record kinds and field contracts before their producers are implemented. Actual record instances emerge during source discovery, parsing, symbol resolution and analysis. These activities can overlap. Control flow, data flow and call-target analysis can refine one another; their dependencies, assumptions, versions and incomplete outcomes must remain explicit.

Accumulate one canonical analysis dataset for the captured inputs and settings, then validate its identity and coverage. Generate profile exports without rerunning analysis. The proposed delivered storage is local SQLite, with an immutable source archive and focused, bounded queries. A completed dataset is not a claim of complete knowledge.

Source spans use exact file versions and zero-based byte intervals with the end excluded. Names alone do not establish identity. Declared members link to their declaring type. Retain separate states for known, unknown, inapplicable, not collected, excluded, incomplete, omitted and paginated information. External symbol descriptions remain limited to qualified identity/signature and supplying library name, with supporting references; external implementations remain outside the retained scope.

Roslyn for C#, a Clang adapter for C++ with an MSVC compatibility check, and a Visual Studio 2022/MSBuild pilot remain the proposed starting tools. A successful Linux build would not, by itself, establish the required Windows/MSVC compatibility. Other build systems and unsupported features require explicit support declarations.

# Original profile definitions

| Profile | BD0.7 executable structure | BD0.12 control flow | BD0.13 data flow |
|---|---|---|---|
| P1 | Omitted | Omitted | Omitted |
| P2 | Omitted | Omitted | Included |
| P3 | Omitted | Included | Included |
| P4 | Included | Included | Included |

All other sections remain included. The baseline profiles use the same canonical analysis, contexts, source-access policy and query contract; included optional sections are uncapped. Size variants use separate identities and preserve complete units, necessary references and explicit omission reasons. Export budgets, analysis budgets and query budgets are distinct.

Comparisons of alternative control-flow-only and data-flow-only outputs were discussed; no replacement of the original P1–P4 baseline was adopted. Direct canonical querying is an optional, explicitly selected mode, never a hidden fallback from profile queries.

# Authorized work after the preflight

The user authorizes autonomous design and implementation on a new branch in the dedicated repository, structured readable commits, and use of specialist agents. Implementation must follow the plan and respect milestone boundaries: do not develop later-stage features before their stage is reached. This request begins with capability verification before proceeding with implementation.

Choose two public GitHub repositories as test resources: one small and one medium. Define size reproducibly and record measurements, revisions, exclusions and measurement-tool versions. A proposed interpretation is to cover C++ and C# across the two repositories; exact selections and size thresholds remain to be established during M0. Build the selected repositories and run their relevant tests. Add controlled examples with explicit expected extraction results, since a successful upstream build alone does not establish extractor correctness.

Use agents for bounded work within the current stage, such as requirements/schema design, independent review, and test-expectation design. Assign separate ownership for writes, share explicit contracts, and integrate through one coordinator. Independent review is useful feedback; it is not proof of correctness without relevant checks.

Keep a persistent record of errors, deviations, incomplete results and unmet criteria. Each entry must identify the expected and observed behavior, evidence, affected requirements and downstream tasks, severity or blocking status, investigation/decision, corrective action and verification outcome. A dependent task may proceed only with the prerequisite met or with an explicit documented adaptation that prevents a false assumption of success. Preserve resolved entries and their verification evidence.

# Current limitations and evidence

| ID | State at checkpoint | Consequence and required follow up |
|---|---|---|
| ENV001 | Open blocker: the managed workspace reports starting; no readiness or failure reason has been returned. | Local cloning, compiler availability, builds and test execution are unverified. Do not claim passed checks. Recheck readiness before execution. |
| ACCESS001 | GitHub reads work; the identified repository reports admin and push permission. Branch and commit tools are available. | Actual remote writes remain to be verified when the authorized branch and commits are created. |
| TEST001 | No two candidate revisions have been selected, measured, built or tested. A read of leethomason/tinyxml2 was only a public-access check. | Establish reproducible size criteria, select and pin candidates, and record actual build and test results. |
| SPEC001 | M0 field dictionary, concrete schema, producer mapping and fixture expectations remain unfinished. | Complete and review these before corresponding implementation. |

The earlier local backup files under /workspace/context-migration/ have not been reverified. The request to retrieve source-index.json was cancelled by the user after workspace startup stalled. The preserved Pages remain accessible and are the available specification references; do not restart that cancelled file-retrieval task merely because implementation resumes.

# Next stage and completion gates

Start with M0 and T01: account for every inventory sub-element, define field meanings/types/units/multiplicity/order/applicability/missing states/producers/owners/evidence/validation, record and relationship schemas, IDs, supported features, profile and query contracts, fixtures and acceptance expectations. Resolve the pilot repositories, compiler/build contexts and analysis assumptions. Review the design and record deviations before implementing its corresponding storage or producers.

Then implement M1 as a small complete path through source capture, representative extraction, limited declared flow analysis, canonical storage, four exports and queries. Build and test the pinned candidate resources and report both successful and unsuccessful checks. Start size and AI retrieval comparisons during this pilot rather than postponing them to the end.

Later stages remain M2 source and organization expansion, M3 analysis expansion, M4 build evidence, and M5 realistic-scale and release validation. Advance only after the preceding applicable completion gates are met. Commit by coherent task and milestone, with the purpose, verification and unresolved limitations recorded.

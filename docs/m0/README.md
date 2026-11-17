# M0 contract and scope

M0 passed the [recorded gate](evidence/gate.json) on 17 November 2026. These design artifacts do not establish extractor support or authorize bypassing an implementation gate. The active checkpoint remains the [post-preflight checkpoint](../checkpoints/2026-11-17-post-preflight.md).

The complete source specifications are frozen in [inventory.md](../specifications/inventory.md), [implementation.md](../specifications/implementation.md), and [tasks.md](../specifications/tasks.md), with provenance and hashes in [sources.json](../specifications/sources.json). The snapshots preserve all original Page blocks in order. Later clarifications in the implementation/task specifications govern ambiguities; none of the 33 inventory sections disappears.

| Deliverable | Purpose |
|---|---|
| [requirements.json](requirements.json) and [traceability.md](traceability.md) | Exact inventory source anchors, disposition, producer, storage owner, retrieval and validation paths |
| [field-dictionary.json](field-dictionary.json) | Field meaning, type, units, cardinality, order, applicability, knowledge state, ownership, evidence and validation |
| [schema.sql](schema.sql), [contracts.md](contracts.md) and [contracts.json](contracts.json) | Concrete normalized storage, typed worker interfaces, acyclic identities, contexts, serialization, exact table ownership, manifest and query contracts |
| [pilot.json](pilot.json) and [support-matrix.json](support-matrix.json) | Exact pilot inputs, compiler/build contexts, language and analysis boundaries |
| [acceptance.json](acceptance.json) | Fixture programs, positive and negative expectations, and declared expansion stages |
| [benchmark.json](benchmark.json) and [benchmark-protocol.md](benchmark-protocol.md) | Fixed questions and gold facts, controlled AI protocol and measurements |
| [milestones.json](milestones.json) | Dependencies and evidence required before advancing |
| [evidence](evidence) | Executed M0 capability and contract-validation results, separate from future product tests |

Run the design checks from the repository root with `node docs/m0/requirements-validation.mjs`, `python docs/m0/validate-contracts.py --dependency-path ../Prompted-Learning-inputs/m0-jsonschema`, and `python docs/m0/validate-worker-contracts.py --dependency-path ../Prompted-Learning-inputs/m0-jsonschema`. The private dependency directory contains the verified JSON Schema validation package. These checks cover design consistency and synthetic positive/negative examples; they do not execute an extractor.

The producer architecture is a .NET orchestrator with Roslyn for C# and a native C++ Clang worker. The orchestrator owns source capture, canonical SQLite validation, profile projection, bounded queries and the experiment controller. Worker interchange is typed and versioned; it is not permission to replace normalized database fields with untyped JSON. A producing tool's runtime is distinct from the compiler/runtime context of analyzed inputs.

Contracts and requirements design have separate agent ownership. An independent reviewer checks the resulting diff and executable evidence. The coordinator resolves discrepancies, updates [deviations.json](../deviations.json), and publishes focused commits. Review agreement alone does not satisfy an executable check.

M0 completion requires all listed deliverables, resolved blocking design findings, source/column/reference validation, executable schema checks, pilot build/context compatibility evidence, an executable AI route and an independent integrated review. M1 begins only after that result is recorded. Product fixtures and the full 24-question, five-arm, three-attempt experiment run during M1; their pass status is not inferred from M0 design checks.

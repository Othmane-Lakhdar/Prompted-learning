# M0 inventory and field traceability

Status: contracts defined for review; producers are not implemented. These files preserve the full inventory rather than replacing it with a section summary.

- [requirements.json](requirements.json) accounts for all 674 nonempty source lines from the frozen [inventory](../specifications/inventory.md). That includes all 33 section headings, 387 list items, entity-family and relationship tables, prose constraints, ownership rules, examples and verification conditions. Formatting and contextual headings are explicitly distinguished from substantive requirements.
- [field-dictionary.json](field-dictionary.json) defines each physical scalar in [schema.sql](schema.sql), plus logical collections and their ownership, order and knowledge states. A requirement ID identifies original wording; a field ID identifies an exact `table.column`. They are deliberately different identifiers.
- [contracts.md](contracts.md) supplies shared identity, evidence, source, analysis, export, query and validation rules. A physical field and an SQL foreign key alone cannot prove the corresponding semantic invariant.

Each inventory source unit retains its exact text, one-based line and SHA-256 of the complete source snapshot. Every numbered-section list item and each entity/relationship data row maps to concrete fields. Prose constraints retain normative contract links; substantive prose about extra retained values also maps to fields. Section entries provide inherited producer, task, main expansion stage, retrieval operation, acceptance/fixture IDs, validation assertion, support state and collection prerequisites. Shared rules have explicit acceptance mappings. Reverse links in the dictionary let a reviewer find the original requirements for a physical field.

The field dictionary supplies meaning, SQLite type, semantic value type, units, multiplicity, ordering, applicability, missing states, producer, storage owner, evidence route and validation rules. Required technical identities and discriminators are known when their row exists; failure to obtain a required value is a rejected or explicitly incomplete record, never a fabricated default. Optional values use the field-state contract. Confirmed empty collections require a known collection state and cannot be inferred from absent rows.

Child rows use their actual primary key, not an array position guessed at query time. Their field-state address and owner follow the grammar in [Field conventions](contracts.md#field-conventions). Logical collection roles are declared in the dictionary, including registry-backed children such as signature parameters, appearances and annotations. Knowledge can be scoped to a role or context without changing the underlying ordering partition. For example, condition/branch filters preserve `operation_child` order across the complete parent, while action inputs/outputs order within action and direction. Typed child rows hold values; `field_state` holds knowledge metadata only.

## How to review a requirement

For example, `BD0.1.F005` is the exact inventory requirement for each retained file's content fingerprint. It maps to `file_version.content_hash` and `source_blob.hash`, whose field contracts identify immutable SHA-256 content and refer to snapshot acceptance. A repeated hash does not merge two file identities.

`BD0.3.F003` maps exact source spans to the file-version-relative byte interval. Its acceptance includes encoding cases; Roslyn character positions cannot be copied into these byte fields. `BD0.14.F003` maps a compiler-selected callable declaration separately from `BD0.14.F005` candidate implementation targets. A resolved interface declaration therefore cannot silently become one concrete runtime implementation.

For the richer entity-family table, `BD0.5.T017` is the generic/template definition and specialization row. Its mapping includes original-definition, ordered arguments and explicit parameter-to-argument substitutions. Required defaults and pack mappings cannot be reconstructed by merely assuming positional correspondence.

`BD0.5.T015` retains the compiler-established captured-variable identity list through `member_relation(role=capture)` in every baseline profile. Additional BD0.13 capture-effect findings are separately optional. Captured instance receivers use a typed implicit parameter identity and do not masquerade as a type or lexical-scope ID.

`BD0.9.F004` maps native annotation spelling to the registered `cpp-attribute/1` extension: written name, namespace and syntax have exact property contracts in the dictionary, distinct from physical SQLite columns. The normalized annotation owner, source span, resolution and ordered arguments remain present. `BD0.9.F005` explicitly treats a builtin C++ attribute's nonexistent attribute class as `not_applicable`, with reason `builtin_attribute_has_no_type`; it does not invent a managed type or confuse inapplicability with unresolved binding. Extension rows inherit their annotation owner's section and profile inclusion.

The registered `identity-input/1` extension maps 18 original identity and validation requirements, including `BD0.4.F001` and `GLOBAL.REVIEW.T026`, to persisted typed ID preimages. Its three envelope properties and all 56 domain component schemas are separate from physical columns. Finalization compares components with captured/contextual/typed facts and recomputes the ID; this is an identity-consistency check, not proof that compiler extraction was correct. Minimal profile anchors carry no identity extension. A snapshot has a required same-ID, context-independent registry record in BD0.1; its nullable fields, evidence and snapshot/1 preimage use that concrete owner. This semantic pairing is validated even though the snapshot table has no reverse SQL foreign key.

## Scheduling and support

The stage entries reproduce the original plan's main expansion milestone. They are not blanket support claims or removals. M1 may implement only the explicitly accepted pilot slice from the [pilot declaration](pilot.json), support matrix and acceptance contracts. Its representative source, CFG and definition-use records do not complete all fields in those sections. M2/M3/M4/M5 remain gated expansions. A known result for one small fixture does not establish full Coral, CppSharp, MSVC or language coverage.

Every physical field currently has `implementation_status: not_implemented`. Collection prerequisites identify where actual build observations, inspectable artifacts, semantic models or supported analysis inputs are needed. Such facts remain not collected or explicitly unknown until evidence exists. Schema support does not permit fabricated build timings, successful skips, complete indirect-call target sets or arbitrary runtime values.

## Design validation

From the repository root, run:

```text
node docs/m0/requirements-validation.mjs
```

The validator requires Node and Python 3 with SQLite `STRICT` support. `M0_PYTHON` may select a different Python executable. It reads files as UTF-8 explicitly and creates only an in-memory SQLite database.

It checks complete source-line coverage and exact text/hash anchors, the 33-section set, unique requirement and field IDs, all concrete column mappings, complete field metadata, DDL/dictionary type/nullability/primary-key agreement, reverse traceability, collection ownership, local contract anchors and declared query operation names. It also checks that the DDL can be created and passes empty-schema integrity checks.

Nineteen deliberate negative mutations must be rejected: a lost source line, an unmapped item, an unknown storage column, changed source wording, a missing schema field, a wrong scalar type, a false implementation claim, a lost knowledge state, a missing parameter collection, incorrect singleton cardinality, role filtering that changes child order, missing membership context scope, required captures lost from P1, an unmapped native attribute spelling, an invented native builtin attribute class, a missing identity extension, a preimage leaked through a minimal anchor, a changed identity-component recipe, and missing snapshot registry ownership. These mutations occur only in memory and do not edit the documents.

The separate [worker contract validator](validate-worker-contracts.py) exercises hand-authored native-message and identity examples against the JSON schemas and explicit semantic acceptance rules. It includes wrong-kind/dangling/local-key references, capture/hash/scope mismatches, invalid byte boundaries, truthful partial discovery, unavailable CFGs, phase-specific feature requests, and branch/return storage-event requirements. These are design checks; no production adapter, IPC transport or analysis is executed.

Passing this validator establishes design-accounting consistency. It does not establish that prose semantics are correct, that populated records satisfy all invariants, or that an extractor, profile query, real repository build or AI benchmark has passed. Independent design review and the milestone's actual semantic and negative fixtures remain separate gates.

## Updating contracts

Keep the source snapshots immutable; a specification change needs a separately recorded source revision and disposition for affected requirement IDs. For an accepted schema change, update exact field contracts, requirement links, collection ownership and the schema digest together. Re-run the validator and independent review. Preserve errors and corrections in the project deviation log before dependent work relies on changed assumptions.

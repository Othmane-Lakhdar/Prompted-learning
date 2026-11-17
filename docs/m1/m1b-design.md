# M1B compiler semantic adapters

Prerequisite: published M1A gate at da35e5da506d08680f73de48408ba2b2c68b1d07. The scoped M1B gate passed in evidence/gate-m1b.json; this document records implementation decisions and does not replace that evidence.

Managed and native producers consume the frozen pilot and emit only source_semantics. Their fact arrays retain the existing typed local keys, compiler primitive keys, spans, entities/types/signatures/appearances/bodies/operations, modifiers/annotations/preprocessing and explicit coverage/diagnostics. The host assigns canonical IDs after validation. CFG/storage-event producers and reaching definitions remain M1C. Documentation expansion is M2 and is not silently marked complete now.

## Common validation and in-process Roslyn

A002 adds semantic-facts and semantic-unit-result definitions referencing the frozen native result property schemas. Native wire shape remains unchanged. A native envelope is validated before conversion. Roslyn uses CompilerProducer with name prompted-learning-roslyn-adapter, version1, actual Roslyn compiler version4.14.0.0 and adapter binary SHA256. It does not fabricate clang_version. Common validation must check scope/tool/input equality, typed local references, uniqueness, byte/scalar spans, primitive compiler keys/signatures, ownership, ordered operation topology/operand roles, vocabulary/applicability, coverage and final consumed input closure before persistence.

The managed semantic input fingerprint is SHA256 of canonical JSON ["managed-semantic-input/1", components], without LF. Components contain exactly snapshot_id, context_id, unit_id, capture_index_fingerprint, producer, files, requested_features and expected_consumed_file_keys. Files preserve declared source order and each has key, file_version_id, logical_path, original_sha256, byte_length, encoding and bom_bytes from the sealed archive. requested_features is exactly ["source_semantics"]. The context already binds options, actual tool binaries and ordered provider fingerprints. Physical locators/time budgets are excluded. Supplied fingerprints are recomputed before parsing and independently bound to host-held scope.

The managed adapter opens the pinned source archive and reads captured bytes, applies evaluated parse/compilation options and loads verified reference bytes through MetadataReference.CreateFromImage. Actual adapter and Roslyn assemblies enter context identity before units are finalized. Cooperative cancellation and the bounded exact-pilot source size are not hard process memory/time isolation claims. Final compiler source/reference input sets must match the host's expected sets.

## Native process boundary

The native worker uses pinned Clang19.1.7 libraries and an in-memory VFS only for this exact pilot. It has no real filesystem overlay; unexpected external include needs remain unsupported/discovery evidence. The existing native request/result contract, canonical hashes, single UTF8 LF framing, duplicate/Int64 validation, self binary hash, and strict scope echoes remain mandatory. It separates discovery from final consumed-input acceptance; discovery IDs/facts do not enter canonical storage. At most eight discovery passes may establish closure. Worker Job Object limits are reported only if installed; the host also enforces wall-time/output limits and rejects missing/truncated/nonzero results atomically.

The bounded native lowering uses literals -std=c++17, -fms-extensions, -fms-compatibility, -fms-compatibility-version=19.44, -DNDEBUG and one registered -I path descriptor for the captured native root. Target is x86_64-pc-windows-msvc/C++17/MS compatibility19.44. Full evaluated MSBuild options/import fingerprints remain bound by context identity. Unsupported literals are rejected, not ignored. PilotInputGuard establishes the exact source/settings envelope. Captured UTF8 and Clang C++17 macro behavior implement the fixture's /utf-8 and /Zc:__cplusplus intent; no generic compiler equivalence or external-header support is claimed.

## Ownership, storage and tests

The managed agent owns the new Managed adapter and its tests; the native agent owns the new Native worker/build files and its tests. The coordinator owns common contracts/validation/process hosting/canonical import and project scaffolding. The reviewer remains read-only.

Preserve CommitFoundation as M1A-only. M1B receives a separate collecting semantic commit/validation path with shared foundation constraints and a precise supported-table set, without a finalizer. Build actual semantic producers, compare exact pilot expected facts (including both22-node Choose trees and all4 native implicit casts), validate malformed/partial/cancelled/mismatched inputs, and independently review actual evidence before M1C. Matching node counts alone is insufficient: targets, roles, topology, ordering, origins, partial-type/redeclaration reconciliation and overload separation must agree.

## Current integration refinements

A003 adds abstract function-type-owned signature identities without changing old callable-entity identities, SQL, or native messages. Types are hashed from finite primitive keys before signatures and payload backlinks are populated. See the amendment and contracts prose.

Native host creates a Windows Job before launch and attaches the worker before any request bytes. The requested process memory cap covers all input-driven framing, binary hashing, request parsing and compilation; ordinary startup of the pinned runtime precedes assignment. Failure to assign the Job aborts before input and terminates the isolated read-only worker. Host wall-time and output bounds remain separate; the worker's inner Job also bounds compilation.

Exact pilot discovery supplies all selected captured main/header buffers, runs a provisional main-only unit request, and uses observed consumed keys to construct final ordered main/include identities. A final pass must consume the identical set. Missing capture expansion returns an explicit T02-required error instead of reading working files; the exact frozen pilot needs one discovery pass. Provisional facts are never imported.

Common validation now checks host-held provider descriptors, callable signature completeness, composite type consistency, source scalar boundaries/causal origin stacks, operation ownership/topology/roles, per-role ordinals and positional call mappings. Native/managed exact oracle checks remain separate from generic structural acceptance. Storage is a separate collecting-only path; later flow/export producers remain closed.

## Direct call mapping handoff

M1B accepts exact entity/operation outputs with resolved invocation target and parameter mapping. It does not claim full T03 completion. T03 explicitly includes normalized direct call sites and relationships; T04 consumes those source facts. The clarified M1C0 step finishes that mapping from accepted operations before CFG, data-flow or call/reachability analyses; M1D cannot assume those tables are already complete. This sequencing is recorded in milestones.json and deviation M1BSEQ001, with no early M1C implementation.

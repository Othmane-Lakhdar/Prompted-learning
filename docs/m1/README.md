# M1 pilot implementation

M1A implements shared contracts, exact captured source, stable identity, evaluated pilot contexts and collecting SQLite storage. The [M1A gate](evidence/gate-m1a.json) **passed** with59 tests and independent review. The [M1B gate](evidence/gate-m1b.json) passed95 tests and independent review for compiler adapters and collecting semantic storage; flow analysis, exports and queries remain gated. Read [PROJECT_STATE.md](../../PROJECT_STATE.md) for the current assignments and latest verified evidence, and [deviations](../deviations.json) for failures and corrections.

## Scope and boundaries

- `PromptedLearning.Core` supplies strict canonical JSON, frozen schema validation, identity preimages, immutable source capture and pinned archive reads. Source search and query cursors belong to M1D.
- `MsBuildEvaluator` supports only the exact frozen [pilot](../m0/pilot.json). It verifies source/project hashes and evaluated inputs/settings, records compiler/analyzer/reference fingerprints and rejects unsupported input families. It fingerprints the ordered imported build definitions before and after acquisition. Path normalization applies to import locators and narrowly validated generated NuGet path fields; raw installed definition contents are hashed unchanged. The raw preprocessed file remains in the external intermediate directory for audit.
- `PromptedLearning.Storage` stores normalized records with identity evidence, field knowledge states, foreign keys and transactional validation. The M1A writer accepts collecting datasets only. It does not finalize semantic analysis or infer complete later-stage collections.
- Native header consumption is not inferred from `ClInclude` project items. Native compilation-unit IDs wait for M1B discovery to establish the consumed input set. M1B must bind its actual analysis adapter descriptor before final context IDs and semantic facts are accepted.
- Evaluated settings and resolved dependencies are not observed build execution. Full BDB build-definition/action/artifact producers remain M4.

The host targets .NET 10. The analyzed managed pilot retains its selected .NET SDK9.0.304, net8.0 references8.0.19 and C#12; do not add a root `global.json` that changes this selection. Microsoft.Data.Sqlite9.0.8 is paired with SQLitePCLRaw bundle2.1.14 after the original transitive native package failed the NuGet security audit. The integration tests assert the actual loaded native version meets the recorded minimum.

## Verification

Build with a .NET10 SDK and direct artifacts outside the checkout. The executable behavior suite deliberately fails when no tests are selected. It covers exact bytes/encodings, pinned archive behavior, source tamper/cancellation, strict IDs and schemas, database constraints/rollback, evaluated context inputs and integration. Negative cases pass only when the expected deviation is detected.

```powershell
$env:PL_NATIVE_WORKER = 'C:/_Travail/tools/Prompted-Learning-inputs/m1-native-build/Release/prompted-learning-clang-worker.exe'
& 'C:/_Travail/tools/Prompted-Learning-inputs/toolchains/dotnet-10.0.404/dotnet.exe' build PromptedLearning.slnx --configuration Release --artifacts-path C:/_Travail/tools/Prompted-Learning-inputs/m1-build --nologo
& 'C:/_Travail/tools/Prompted-Learning-inputs/toolchains/dotnet-10.0.404/dotnet.exe' 'C:/_Travail/tools/Prompted-Learning-inputs/m1-build/bin/PromptedLearning.Tests/release/PromptedLearning.Tests.dll' --group all --project-root C:/_Travail/tools/Prompted-Learning --scratch C:/_Travail/tools/Prompted-Learning-inputs/m1-integrated-tests --report C:/_Travail/tools/Prompted-Learning-inputs/m1-integrated-tests-report.json
```

Build the [native worker](../../src/PromptedLearning.Native/README.md) before running all tests. `--group` can select Foundation, Source, Storage, Context, PilotGuard, ContextStorage, CompilerContract, Managed, Native, NativeSemantic, SemanticValidation, NativeHost, SemanticStorage or SourceRelations. `PL_MSBUILD` overrides the declared Visual Studio MSBuild executable path; a different toolchain/settings envelope may be explicitly rejected by the bounded pilot guard. Raw execution reports remain external; reviewed evidence is copied into [evidence](evidence/) without substituting upstream test success for extractor correctness.

## M1B source semantics

The [M1B design](m1b-design.md) describes the exact compiler input boundary. `ManagedPilotAdapter` runs pinned Roslyn4.14 over captured bytes and verified reference images. `NativePilotAdapter` runs pinned Clang19.1.7 through the bounded native process host, observes captured header consumption, creates the final compilation-unit identity and reruns with that exact input set. Missing capture expansion explicitly returns to source capture; working-source fallback is never used.

`SemanticResultValidator` checks scope, producer, source intervals, origins, primitive types/signatures, references, operation topology, argument mappings and coverage before canonical IDs are assigned. `ImportSemanticUnit` revalidates its inputs and imports within a transaction. `CommitSemanticCollection` is a collecting-only gate separate from `CommitFoundation`; failed validation rolls back. Partial source results preserve located coverage, body reasons and unknown collection states. Direct call targets remain in operations. Normalized direct relationship/call-site mapping is the first M1C prerequisite step, finishing T03 source work before T04 flow/call analyses; M1B does not claim all of T03 complete. CFG, data-flow, documentation, export and query producers retain their later-stage boundaries.

The managed adapter uses cooperative cancellation and a bounded exact-pilot input size; it does not claim hard process isolation. The native host attaches a Windows memory Job before sending request bytes and enforces wall-time/output limits. Pinned runtime startup precedes attachment. Schema amendments A002-A004 are recorded explicitly: transport-neutral facts, abstract function-type signatures, and native pragma storage. Existing M1A database schemas are rejected without an in-place migration; use a fresh collecting generation with the amended schema.

[A001](contract-amendments.json) permits an unknown source-manifest hash on early failed/cancelled archive preparation. Successful preparation still requires the actual verified hash. The original M0 gate/hash remains historical; the M1 gate must identify the reviewed amended contract.

## M1C0 direct source relations

The [ordered M1C plan](m1c-plan.md) begins with direct source call normalization. `NormalizeDirectSourceRelations(unitId)` consumes accepted canonical source operations and exact selected signatures, preserving the call origin and argument-to-parameter mapping. `CommitSourceRelationsCollection` is a separate collecting gate; the M1A and M1B commit APIs continue to reject these additional tables. The [M1C0 gate](evidence/gate-m1c0.json) passed 100/100 combined tests and independent review.

A selected static method or free function is a compiler-resolved declaration. Runtime target candidates remain not analyzed. Complete body scans establish their direct outgoing calls, including known empty sets; partial scans preserve unknown occurrence collections and located reasons. Broader incoming/outgoing relationships and context-wide incoming calls remain unknown.

The source-relations-input/1 fingerprint binds the consumed source rows, source compiler evidence, unit identity, registered policy/version and separately hashed Storage normalizer tool. Row arrays use canonical UTF8 JSON ordering. Derived relations, call coverage and knowledge-state transitions do not enter the input hash, so repeated normalization is idempotent. The normalizer has derivation evidence and does not alter the compiler context's tool list. The [compiler-flow design](m1c-compiler-design.md) remains a subsequent gated step.

## Weekly usage

The full user policy is appended to [AGENTS.md](../../AGENTS.md). The coordinator and subagents check reliable weekly usage at bounded checkpoints, stop new substantive work at15% remaining or when reliable quota is unavailable, preserve changes and handoffs in PROJECT_STATE.md, and wait for explicit authorization after safe shutdown. Credits and resets are never used automatically.

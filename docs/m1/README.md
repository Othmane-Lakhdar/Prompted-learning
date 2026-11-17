# M1 foundation implementation

M1A implements shared contracts, exact captured source, stable identity, evaluated pilot contexts and collecting SQLite storage. The [M1A gate](evidence/gate-m1a.json) **passed** with59 tests and independent review. M1B compiler adapters are next; flow analysis, exports and queries remain gated. Read [PROJECT_STATE.md](../../PROJECT_STATE.md) for the current assignments and latest verified evidence, and [deviations](../deviations.json) for failures and corrections.

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
& 'C:/_Travail/tools/Prompted-Learning-inputs/toolchains/dotnet-10.0.404/dotnet.exe' build PromptedLearning.slnx --configuration Release --artifacts-path C:/_Travail/tools/Prompted-Learning-inputs/m1-build --nologo
& 'C:/_Travail/tools/Prompted-Learning-inputs/toolchains/dotnet-10.0.404/dotnet.exe' 'C:/_Travail/tools/Prompted-Learning-inputs/m1-build/bin/PromptedLearning.Tests/release/PromptedLearning.Tests.dll' --group all --project-root C:/_Travail/tools/Prompted-Learning --scratch C:/_Travail/tools/Prompted-Learning-inputs/m1-integrated-tests --report C:/_Travail/tools/Prompted-Learning-inputs/m1-integrated-tests-report.json
```

`--group` can select Foundation, Source, Storage, Context PilotGuard or ContextStorage instead of all. `PL_MSBUILD` overrides the declared Visual Studio MSBuild executable path; a different toolchain/settings envelope may be explicitly rejected by the bounded pilot guard. Raw execution reports remain external; reviewed evidence is copied into [evidence](evidence/) without substituting upstream test success for extractor correctness.

[A001](contract-amendments.json) permits an unknown source-manifest hash on early failed/cancelled archive preparation. Successful preparation still requires the actual verified hash. The original M0 gate/hash remains historical; the M1 gate must identify the reviewed amended contract.

## Weekly usage

The full user policy is appended to [AGENTS.md](../../AGENTS.md). The coordinator and subagents check reliable weekly usage at bounded checkpoints, stop new substantive work at15% remaining or when reliable quota is unavailable, preserve changes and handoffs in PROJECT_STATE.md, and wait for explicit authorization after safe shutdown. Credits and resets are never used automatically.

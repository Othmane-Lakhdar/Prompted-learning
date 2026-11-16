# Local Windows preflight — 16 November 2026

Preflight completed with declared scope limits. M0 and extractor implementation have not started. Source pins, export profiles and the historical checkpoint are unchanged.

## Verified results

| Check | Result |
|---|---|
| Git/network/input identity | Origin fetch and pinned clones passed; all 57 Coral and 231 CppSharp selected blobs match the manifest. Tracked source remains clean after builds. |
| Native C++ | MSVC compile/link/run and CTest: 1/1 passed. |
| Managed tools | NuGet restore/build, Roslyn symbol/operation access, SQLite foreign-key rejection/rollback passed. Deliberate exit 7 propagated. |
| Clang development API | Candidate-local Clang 19.1.7 linked with MSVC; AST/CFG probe 1/1 passed (five blocks, one branch). |
| Coral | 41/41 native checks plus 19/19 managed checks in each of two assembly contexts. |
| CppSharp | Native parser/C# P/Invoke generator-test graph built: 91 passed, 1 ignored, 0 failed. The initial one-case AST test is part of that suite, not an additional unique test. |
| Source/archive/evaluation | Seven-byte binary ZIP round-trip preserved SHA-256; MSBuild property/Compile-item evaluation passed. |
| Failure detection | Coral checker rejects wrong totals, missing runs, exceptions and failed checks even with exit 0. Collector records launch failure and restores environment. |

CppSharp ignores TestUniqueNamesAcrossTranslationUnits because nameless enums are no longer uniquely named. Per-case TRX outcomes report this skip even though aggregate notExecuted=0 is misleading. Other test assemblies and the full Windows C++/CLI solution were not run.

## Evidence and paths

- PROJECT: C:\_Travail\tools\Prompted-Learning.
- INPUTS: C:\_Travail\tools\Prompted-Learning-inputs. Contains candidate clones, products, private SDK, original logs/TRX/binlog and dependencies.
- VS: C:\Program Files\Microsoft Visual Studio\2022\Community.
- [commands.json](commands.json) records argument arrays, directories, exit codes and durations. Paths use <PROJECT>, <INPUTS>, <VS> and <USERPROFILE> substitutions. Its log paths refer to originals; committed copies are in [logs](logs).
- cppsharp-generate ran in an x64 VS developer environment with CI=true; this command predates environment capture in the collector. Generated build/config.props confirms CI=true, net8.0, Release, x64.
- [environment.json](environment.json) records the initial tools; [dotnet10-install.json](dotnet10-install.json) records the subsequently added private SDK.
- [input-verification.json](input-verification.json) covers Git objects and clean tracked checkout, not raw-byte equality after Git newline conversion. Extraction must still preserve actual input bytes.
- [cppsharp-test-summary.json](cppsharp-test-summary.json) preserves per-test outcomes and original TRX hashes. Raw TRX/binlog remain local because they contain machine metadata.
- [raw-log-hashes.json](raw-log-hashes.json) hashes original logs. Committed copies replace root paths, remove terminal color escapes and trim trailing whitespace, so their hashes differ.

The user's ZIPs were untouched. Coral's ZIP comment identifies the selected revision; CppSharp's identifies 182c3b180b8f0d4e6af1f80ffad85d681979d728, not the selected 42ccd59b112072a6bcf3e1406efca174a46c8cf8. Builds used verified pinned clones. ZIP comments alone are not build-input integrity evidence.

## Reproduction

Start from the [pinned sibling clones](../../testing/README.md). Select the same SDK/compiler versions. The collector tools/preflight/Invoke-Logged.ps1 runs a native executable with an argument array, working directory and optional process environment overrides, records a log and JSON, and propagates its exit code (127 for setup/launch failure).

### Coral

The first configure with installed .NET 7/9 failed at the runtime-10 prerequisite. A private SDK 10.0.404/runtime 10.0.15 was extracted into INPUTS/toolchains/dotnet-10.0.404. [dotnet10-package.json](dotnet10-package.json) records its official URL, release metadata and SHA-512; the downloaded ZIP matched. This follows Microsoft's [manual ZIP installation](https://learn.microsoft.com/en-us/dotnet/core/install/windows). System SDK selection was not changed.

Set DOTNET_ROOT and DOTNET_ROOT_X64 for the Coral process to that private directory. Use VS's bundled cmake.exe to configure Coral/cmake with the Visual Studio 17 2022 generator, x64, CORAL_TESTING=ON, CORAL_EXAMPLE=ON and DOTNET=<private-directory>/dotnet.exe. Build with --config Release --parallel 2. Exact commands are in commands.json.

The upstream CMake managed-testing/example commands omit --configuration. For the accepted test deployment, from the Coral clone explicitly run the private dotnet.exe:

```text
build Tests/Testing.Managed/Testing.Managed-Static.csproj --configuration Release --output build-msvc-dotnet10/Tests/Release
```

Copy nethost.dll from the private SDK's packs/Microsoft.NETCore.App.Host.win-x64/10.0.15/runtimes/win-x64/native directory into that test deployment directory. Managed build output supplies both managed assemblies, runtimeconfig/deps files and Newtonsoft.Json.

Invoke Testing.Native.exe by absolute path, with its deployment directory as working directory, through the collector. The executable derives assembly paths from argv[0]. Run Assert-CoralLog.ps1 -LogPath <captured-log>; it reads the collector's .json too. Raw exit 0 is insufficient. The checker requires 41 native checks, two complete runs of all 19 named managed tests and no recognized failure markers.

The first checker incorrectly expected 18 managed tests, missing StringMarshalTest2 during source review. It rejected the actual 19-test run. A source recount corrected the expectation; the initial rejection, corrected validation and negative checker cases are preserved. Upstream source was not edited to make tests pass.

### CppSharp

Enter an x64 Visual Studio developer environment. With CI=true, run Git Bash in CppSharp/build:

```bash
bash build.sh generate -configuration Release -platform x64 -vs 2022 -target-framework net8.0
```

Generation acquired Premake 5.0.0-beta2 and the LLVM cd708029e0b2869e80abe31ddb175f7c35361f90 Windows prebuilt package (77,628,811 bytes). Verify generated build/config.props selects CI=true, net8.0, Release, x64. From the clone root:

```text
MSBuild.exe src/Generator.Tests/CppSharp.Generator.Tests.csproj -restore -p:Configuration=Release -p:Platform=x64 -p:CI=true -v:minimal -m:2
dotnet test bin/Release/CppSharp.Generator.Tests.dll --logger "trx;LogFileName=cppsharp-generator.trx"
```

Use VS MSBuild for its native vcxproj dependencies. SDK 9 restored net8 references; runtime 8.0.19 ran tests. CI=true selects the C# P/Invoke parser but does not remove Windows C++/CLI test projects. Use the explicit assembly instead of test.sh's broad DLL glob for this partial build.

An upstream postbuild command erroneously prepends % to the resource-cleanup directory, producing a file-not-found diagnostic. The subsequent copy has the correct destination. All 237 copied resource files were hashed and match with no extras. Fresh-build success does not validate incremental cleanup; this defect remains open.

## Remaining gates

M0 must define inventory field mappings, source/identity rules, supported language variants/build contexts, query/profile contracts, fixtures and the controlled AI comparison route. One header-free Clang example does not establish general MSVC compatibility or C++/CLI parsing. Full CppSharp Windows testing requires the missing C++/CLI component. CI execution, scale/performance, cancellation/atomic export and automated AI evaluation remain unverified at their applicable stages.

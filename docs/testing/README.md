# Mixed C++ and C# test resource candidates

These pinned resources each contain substantive C++ and C# in the same project. Both were cloned locally and their selected blob sizes reverified. Coral's native/managed tests passed; CppSharp's native parser/C# P/Invoke generator-test subset built and ran (91 passed, 1 upstream ignore). Full CppSharp Windows/C++/CLI testing remains blocked. No extractor run has been performed. See the [local preflight report and exact commands](../preflight/2026-11-16-local/README.md).

| Resource | Pin | Selected C# source | Selected C++ source and headers | Total | Classification |
|---|---|---|---|---|---|
| [Coral](https://github.com/StudioCherno/Coral) | 320c90de59d9cf81a56f3265d8a5d4a1777431ce | 14 files / 97,650 bytes | 43 files / 110,151 bytes | 57 files / 207,801 bytes | Small |
| [CppSharp](https://github.com/mono/CppSharp) | v1.2 at 42ccd59b112072a6bcf3e1406efca174a46c8cf8 | 207 files / 2,033,907 bytes | 24 files / 319,441 bytes | 231 files / 2,353,348 bytes | Medium |

Coral is a native .NET hosting library with a C# managed assembly for loading, reflection and invocation. CppSharp has a native C++ parser using Clang and managed C# AST/parser/generator code connected through generated bindings. These provide meaningful native/managed boundaries; being in the same repository does not by itself prove any particular cross-language call can be resolved statically.

## Size definition

selected-source-blob-bytes-v1 sums Git blob byte sizes at each pinned revision. It includes comments, whitespace and original encoding bytes; headers count for C++. Small means more than zero and at most 262,144 bytes (256 KiB). Medium means more than 262,144 and at most 8,388,608 bytes (8 MiB). These are project-specific thresholds.

[candidates.json](candidates.json) records every counted path, blob SHA and byte count, plus inclusion and exclusion rules. Both Git-tree responses were complete, with truncated=false, and the reported totals were recomputed from the selected entries.

This is not a count of source-code lines, repository storage, dependency downloads, compiled binaries or database size. The explicit size exclusions do not remove necessary generated files or dependencies from builds, retained source or extraction-coverage accounting.

## Clone as sibling directories

Keep the test-resource working trees outside the project checkout. From a common parent directory:

```text
git clone --branch codex/extractor-preflight-m0 https://github.com/Othmane-Lakhdar/Prompted-learning.git

git clone https://github.com/StudioCherno/Coral.git
git -C Coral checkout --detach 320c90de59d9cf81a56f3265d8a5d4a1777431ce
git -C Coral rev-parse HEAD

git clone https://github.com/mono/CppSharp.git
git -C CppSharp checkout --detach 42ccd59b112072a6bcf3e1406efca174a46c8cf8
git -C CppSharp rev-parse HEAD
```

Compare each HEAD with its recorded pin. A detached checkout is deliberate for reproducible test inputs. Building the unpinned default branch would test a different source revision.

This chat has verified local filesystem and build access. Preserve revision, environment, commands, exit codes and logs for subsequent runs.

## Coral build entry point and checks still required

Pinned sources: [README](https://github.com/StudioCherno/Coral/blob/320c90de59d9cf81a56f3265d8a5d4a1777431ce/README.md), [CMake configuration](https://github.com/StudioCherno/Coral/blob/320c90de59d9cf81a56f3265d8a5d4a1777431ce/cmake/CMakeLists.txt), [managed project](https://github.com/StudioCherno/Coral/blob/320c90de59d9cf81a56f3265d8a5d4a1777431ce/Coral.Managed/Coral.Managed-Static.csproj), and [host discovery](https://github.com/StudioCherno/Coral/blob/320c90de59d9cf81a56f3265d8a5d4a1777431ce/cmake/Dotnet.cmake).

For the proposed Windows x64 path, use VS2022 Desktop development with C++, Windows SDK, CMake 3.25 or later, and a compatible .NET 10 SDK/runtime/native host pack on PATH. The pinned managed projects target net10.0. The CMake script uses block(), so its declared top-level minimum of 3.19 is not sufficient for that command.

The pinned CMake route deliberately invokes the standalone dotnet CLI for managed builds with VS2022; do not assume VS2022 MSBuild can directly build the net10.0 projects. Its generic README Premake example is not the complete recipe for this pinned layout.

Candidate configure/build commands from the common parent directory:

```text
cmake -S Coral/cmake -B Coral/build-msvc -G "Visual Studio 17 2022" -A x64 -DCORAL_TESTING=ON -DCORAL_EXAMPLE=ON
cmake --build Coral/build-msvc --config Release
```

These commands were executed. Installed .NET 7/9 initially failed configuration; the verified route uses an explicit path to private SDK 10.0.404/runtime and host pack 10.0.15. NuGet restore and the declared build passed. See the local report for the exact DOTNET override.

The native test program loads Testing.Managed.dll and Coral runtime files relative to its executable. The verified test recipe explicitly rebuilds managed tests and their reference in Release into the native executable directory, then stages nethost.dll. This also corrects the CMake test command's omitted --configuration argument for the accepted test run; the original CMake command alone mixes configurations.

[Tests/Testing.Native/Source/Main.cpp](https://github.com/StudioCherno/Coral/blob/320c90de59d9cf81a56f3265d8a5d4a1777431ce/Tests/Testing.Native/Source/Main.cpp) prints failed checks but ends main with return 0. CMake does not register CTest cases. Neither exit zero nor a zero-case CTest run establishes success. The added tools/preflight/Assert-CoralLog.ps1 checks one 41-pass native summary, two 19-pass managed summaries, the expected managed test names and failure markers. Positive and negative validation results are recorded. See RESOURCE-CORAL-001 and RESOURCE-CORAL-002 in [the deviation log](../deviations.json).

## CppSharp build entry point and checks still required

Pinned sources: [build.sh](https://github.com/mono/CppSharp/blob/42ccd59b112072a6bcf3e1406efca174a46c8cf8/build/build.sh), [test.sh](https://github.com/mono/CppSharp/blob/42ccd59b112072a6bcf3e1406efca174a46c8cf8/build/test.sh), [Windows CI](https://github.com/mono/CppSharp/blob/42ccd59b112072a6bcf3e1406efca174a46c8cf8/.github/workflows/main.yml), [parser project](https://github.com/mono/CppSharp/blob/42ccd59b112072a6bcf3e1406efca174a46c8cf8/src/Parser/CppSharp.Parser.csproj), and [test configuration](https://github.com/mono/CppSharp/blob/42ccd59b112072a6bcf3e1406efca174a46c8cf8/build/Tests.lua).

The proposed Windows x64 Release path needs VS2022 with Desktop development with C++, Windows SDK, C++/CLI support, and MSBuild.exe visible in the VS developer environment. Also provide Git Bash, curl, archive tools including 7-Zip, a suitable .NET SDK/runtime, and GitHub/NuGet access. The scripts download Premake 5.0.0-beta2.

An explicit net8.0 configuration was verified for the declared P/Invoke generator-test subset using SDK 9.0.304 and runtime 8.0.19. The repository has no global.json; leaving the target unspecified derives it from dotnet --version. Record SDK and runtime versions instead of relying on whichever happens to be newest.

Run the following from Git Bash with the VS developer environment available, starting in the common parent directory:

```bash
cd CppSharp/build
bash build.sh generate -configuration Release -platform x64 -vs 2022 -target-framework net8.0
bash build.sh restore -configuration Release -platform x64
bash build.sh -build_only -configuration Release -platform x64
bash test.sh -configuration Release -platform x64
```

The commands above describe the full upstream Windows route; they were not run as a full suite. The verified adaptation uses CI=true during generation and builds only Generator.Tests with VS MSBuild, then runs that explicit assembly. Release is intentional: upstream CI notes Debug-test duration. GettingStarted's older VS2019/v142 instructions differ from the pinned scripts and CI using VS2022. The local report preserves the tested subset and its limits.

The pinned LLVM commit is cd708029e0b2869e80abe31ddb175f7c35361f90. Its Windows VS2022 x64 Release archive was downloaded (77,628,811 compressed bytes), extracted and used in the successful scoped native build. A separate Clang AST/CFG capability executable also linked and ran against those libraries. No upstream archive checksum was supplied by the build script; downloaded-package identity, local file hashes and execution evidence are recorded separately.

The normal Windows parser uses generated C++/CLI bindings. CI mode can select a C# P/Invoke parser binding, but Windows CLI tests remain in the solution. Do not interpret CI=true as a fully CLI-free build. M0 must state which compilation units and language variants the extractor supports. Ordinary native C++ support through Clang does not imply C++/CLI support, and unavailable coverage cannot be counted as passed.

## What the resources establish

Their own builds and tests can validate the chosen resource configuration. They cannot prove that the extractor records the right entities, relationships, flow facts, contexts or omissions. M0/M1 must add controlled examples with known expected results, explicit unsupported cases and checks that deliberately detect deviations.

The two resources now have actual build/test evidence for the scopes above. This is not full CppSharp Windows coverage or extractor validation. The prior TinyXML2/Newtonsoft.Json pair was replaced because each newly requested candidate must contain both languages; it was not a failed build experiment.

# M1 native compiler worker

This Windows executable implements the frozen `native-worker-request/1` and
`native-worker-result/1` boundary for the M1 native pilot. It uses Clang 19.1.7
from the pinned external LLVM package. It does not persist canonical records.
The host checks the frozen pilot/context, assigns final IDs, validates every
returned reference and imports an accepted unit atomically.

## Build and test

Use the Visual Studio 2022 CMake executable if CMake is not on `PATH`. Keep the
build and LLVM package outside this checkout:

```powershell
& $cmake -S src/PromptedLearning.Native -B $nativeBuild `
  -G 'Visual Studio 17 2022' -A x64 "-DLLVM_PACKAGE=$llvmPackage"
& $cmake --build $nativeBuild --config Release --parallel 2
$env:PL_NATIVE_WORKER = "$nativeBuild/Release/prompted-learning-clang-worker.exe"
& $dotnet $testAssembly --group Native --project-root $projectRoot `
  --scratch $testScratch --report $testReport
```

The build embeds the frozen contract as a Windows resource and records worker
ProductVersion `1`. The actual executable SHA-256 is part of every request;
rebuilding requires the host to refresh its tool descriptor before freezing a
context. The worker rejects any other self hash, version, Clang version or
target dialect. Schema changes trigger CMake reconfiguration.

## Capture and lifecycle

The worker reads one UTF-8 JSON line from stdin through EOF. It checks duplicate
properties, signed Int64 numbers, the frozen schema, both canonical request
fingerprints, original byte hashes/lengths, encoding and supported options.
Its stdout is exactly one canonical JSON line on success; stderr is diagnostic
text only. Nonzero exit, truncation, malformed output and exceeded bounds must
be handled by the host as an atomic worker failure.

Captured UTF-8 buffers are mounted at `X:/__capture/<logical_path>` in an LLVM
in-memory filesystem. `X:` is a fixed virtual root; it is never opened through
the operating-system filesystem. There is no real-filesystem overlay, no SDK
header fallback and no use of request physical paths. Compiler path separators
are normalized when mapping locations back to captured file keys. Byte offsets
remain offsets into the original bytes, including BOM and CRLF where present.

Preprocessor callbacks record entered files, the include occurrence and the
header's `#pragma once`. An uncaptured include becomes a discovery request.
Discovery returns no entities, types, signatures, appearances, bodies,
operations, modifiers, annotations or preprocessing payload. Spans survive only
when reachable from a diagnostic's location and its finite cause chain; a
successful discovery without diagnostics has no spans. The host uses the
consumed set to recompute its unpublished unit and repeats discovery as defined
by the contract. Final results require the exact expected consumed set and no
unresolved discovery; a mismatch clears all semantic payload and reports failure.

## Supported boundary

The supported gate is the frozen `Arithmetic.cpp` / `Arithmetic.hpp` pilot,
validated by the host before invoking this worker. The worker lowers only the
declared C++17/MSVC19.44 options, NDEBUG and captured include roots. Arbitrary
compiler switches and external paths are rejected. UTF-16 input remains an
explicit unsupported worker input for this pilot.

Canonical Clang declarations reconcile namespace/function redeclarations;
parameters use their canonical callable and ordinal. Local variables retain
source origins. All semantic AST children are preserved, including explicit
DeclStmt-to-VarDecl expansion and the four implicit casts in the 22-node Choose
body. Source pointer maps are internal and deterministic structural paths form
local operation keys. Written native attributes retain their builtin name and
syntax without a fabricated attribute type. Unknown constructs produce partial
coverage and cannot satisfy the pilot gate.

M1B does not produce CFGs, storage events or data flow. Requests for those
features fail; ordinary M1B results retain explicit `not_requested` coverage.

## Resource enforcement

Transport has a fixed 64 MiB envelope cap in addition to host input/output and
wall-time bounds. The host attaches the requested Windows process-memory job
before writing any request bytes to stdin. Runtime loading and the worker's
fixed initial 64 KiB input-buffer reservation occur after launch and may precede
job attachment; no captured input is delivered in that interval. Attachment
failure terminates the isolated worker. After strict request verification, before Clang runs, the
worker installs a Windows job with the requested process-memory and CPU-time
limits and a wall-time watchdog. Failure to install limits is a worker failure.
The input-byte, total-record, operation and serialized-result limits are checked
explicitly. Exceeding a bound never emits an empty successful fact set. The host
retains its own memory job and deadline while sending stdin and reading stdout,
including the validation phase before the worker's inner execution limits are installed.

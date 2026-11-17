# Independent native CFG prerequisite oracle

This bounded probe observes the frozen native Choose body directly through
Clang 19.1.7. It is independent of the extractor and does not implement storage
events, reaching definitions, imports or queries. The executable verifies both
source hashes before using an in-memory filesystem with no physical fallback.

Build outside the checkout so the already-reviewed native worker stays intact.
In PowerShell, with the pinned LLVM package already provisioned:

~~~powershell
$cmake = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
& $cmake -S tools/preflight/clang-cfg-oracle -B ../Prompted-Learning-inputs/m1c-native-cfg-oracle -G 'Visual Studio 17 2022' -A x64 -DLLVM_PACKAGE=C:/_Travail/tools/Prompted-Learning-inputs/CppSharp/build/llvm/llvm-cd7080-windows-vs2022-x64-Release
& $cmake --build ../Prompted-Learning-inputs/m1c-native-cfg-oracle --config Release --parallel 2
& ../Prompted-Learning-inputs/m1c-native-cfg-oracle/Release/clang-cfg-oracle.exe tests/fixtures/pilot/native ../Prompted-Learning-inputs/m1c-native-cfg-oracle-raw.json
~~~

The recorded run used a hidden child process with a 30-second timeout; see the
[execution manifest](../../../docs/m1/evidence/native-cfg-oracle-execution.json).
The [retained raw observation](../../../docs/m1/evidence/native-cfg-oracle.json)
contains only logical source names, byte spans, compiler IDs and AST source
paths. In-memory pointer equality establishes correspondence but pointer values
are never serialized. Branch meanings are checked against actual then/else AST
assignment membership, not inferred from successor index alone.

Source preorder explicitly includes VarDecl under DeclStmt. Actual compiler CFG
members preserve forward iteration order; terminator/condition references are
recorded separately. Predeclared scalar source points only test block containment.
They are not an event implementation or a substitute for the later replay tests.

Any unsupported CFG element, unmapped pointer, malformed source range, ambiguous
source-point block, unexpected frozen input or failed compiler invocation causes
a nonzero result and no successful report. Broader C++ constructs are outside
this prerequisite. The exact observed table and future carrier normalization
are frozen in the [compiler design](../../../docs/m1/m1c-compiler-design.md#native-oracle-observations-frozen-after-m1c0).

# M0 pilot inputs

These controlled inputs specify extraction expectations. They are test resources, not extractor implementation. The corresponding exact acceptance facts are in docs/m0/acceptance.json.

FX-PILOT-CS selects the two explicitly listed C# files in Pilot.csproj, Release/x64, .NET 8 and C# 12. The two partial declarations denote one Pilot.Arithmetic type. The int and long Add overloads are distinct. Choose binds the int overload.

FX-PILOT-CPP selects Arithmetic.cpp and its internal Arithmetic.hpp, Release/x64, C++17. Repeated declarations and definitions denote the same four functions in namespace pilot. Choose binds Add(int,int), not Add(long long,long long). Its noinline declaration requires the tested MSVC extension path; this does not establish C++/CLI or general Windows-header compatibility.

For both inputs, the value > 0 condition selects between assigning Add(value,1) and assigning zero to result. Both assignments can reach the return of result. Neither assignment alone must reach that return across all paths. Unused has no call site in these fixtures; it is not thereby proven dead in an open library.

The UTF-8 multibyte comment is intentional. Save original bytes, including newline style; use explicit compiler-character to byte-span conversion. Build outputs belong outside this directory. Additional encoding, identity/context, source-family and analysis fixtures are specified separately and enter their declared milestone.

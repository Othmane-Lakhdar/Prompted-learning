cmake_minimum_required(VERSION 3.25)
project(PreflightClang LANGUAGES CXX)
if(NOT DEFINED LLVM_PACKAGE)
  message(FATAL_ERROR "LLVM_PACKAGE must identify CppSharp's pinned extracted LLVM package")
endif()
add_executable(preflight-clang main.cpp)
target_compile_features(preflight-clang PRIVATE cxx_std_17)
target_compile_definitions(preflight-clang PRIVATE _CRT_SECURE_NO_WARNINGS)
target_include_directories(preflight-clang PRIVATE
  "${LLVM_PACKAGE}/include" "${LLVM_PACKAGE}/llvm/include"
  "${LLVM_PACKAGE}/clang/include" "${LLVM_PACKAGE}/build/include"
  "${LLVM_PACKAGE}/build/clang/include")
# These are upstream prebuilt Release libraries, not a new frontend implementation.
file(GLOB LLVM_LIBRARIES "${LLVM_PACKAGE}/build/lib/*.lib")
if(NOT LLVM_LIBRARIES)
  message(FATAL_ERROR "Pinned LLVM development libraries are missing")
endif()
target_link_libraries(preflight-clang PRIVATE ${LLVM_LIBRARIES}
  version ntdll bcrypt ws2_32 psapi shell32 ole32 uuid)
enable_testing()
add_test(NAME clang-ast-and-cfg COMMAND preflight-clang)

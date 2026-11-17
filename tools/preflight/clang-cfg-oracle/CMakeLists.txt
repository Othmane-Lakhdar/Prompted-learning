cmake_minimum_required(VERSION 3.25)
project(ClangCfgOracle LANGUAGES CXX)
if(NOT WIN32 OR NOT MSVC OR NOT DEFINED LLVM_PACKAGE)
  message(FATAL_ERROR "The oracle requires Windows/MSVC and pinned LLVM_PACKAGE")
endif()
add_executable(clang-cfg-oracle main.cpp)
target_compile_features(clang-cfg-oracle PRIVATE cxx_std_17)
target_compile_definitions(clang-cfg-oracle PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(clang-cfg-oracle PRIVATE /W4 /WX /wd4291 /utf-8 /EHsc)
target_include_directories(clang-cfg-oracle SYSTEM PRIVATE
  "${LLVM_PACKAGE}/include" "${LLVM_PACKAGE}/llvm/include"
  "${LLVM_PACKAGE}/clang/include" "${LLVM_PACKAGE}/build/include"
  "${LLVM_PACKAGE}/build/clang/include")
file(GLOB LLVM_LIBRARIES "${LLVM_PACKAGE}/build/lib/*.lib")
if(NOT LLVM_LIBRARIES)
  message(FATAL_ERROR "Pinned LLVM libraries missing")
endif()
target_link_libraries(clang-cfg-oracle PRIVATE ${LLVM_LIBRARIES}
  version ntdll bcrypt ws2_32 psapi shell32 ole32 uuid)

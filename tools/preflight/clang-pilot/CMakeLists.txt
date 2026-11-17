cmake_minimum_required(VERSION 3.25)
project(ClangPilotCompatibility LANGUAGES CXX)
if(NOT DEFINED LLVM_PACKAGE OR NOT DEFINED PILOT_SOURCE)
  message(FATAL_ERROR "LLVM_PACKAGE and PILOT_SOURCE are required")
endif()
add_executable(clang-pilot main.cpp)
target_compile_features(clang-pilot PRIVATE cxx_std_17)
target_compile_definitions(clang-pilot PRIVATE _CRT_SECURE_NO_WARNINGS)
target_include_directories(clang-pilot PRIVATE
  "${LLVM_PACKAGE}/include" "${LLVM_PACKAGE}/llvm/include"
  "${LLVM_PACKAGE}/clang/include" "${LLVM_PACKAGE}/build/include"
  "${LLVM_PACKAGE}/build/clang/include")
file(GLOB LLVM_LIBRARIES "${LLVM_PACKAGE}/build/lib/*.lib")
if(NOT LLVM_LIBRARIES)
  message(FATAL_ERROR "Pinned LLVM libraries missing")
endif()
target_link_libraries(clang-pilot PRIVATE ${LLVM_LIBRARIES}
  version ntdll bcrypt ws2_32 psapi shell32 ole32 uuid)
enable_testing()
add_test(NAME native-pilot-msvc-compatibility COMMAND clang-pilot "${PILOT_SOURCE}")

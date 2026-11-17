cmake_minimum_required(VERSION 3.25)
project(PromptedLearningNative VERSION 0.1.0 LANGUAGES CXX)
if(NOT WIN32 OR NOT MSVC OR NOT DEFINED LLVM_PACKAGE)
  message(FATAL_ERROR "M1 requires Windows/MSVC and the pinned LLVM_PACKAGE")
endif()
configure_file("${CMAKE_CURRENT_SOURCE_DIR}/../../docs/m0/contracts.json" "${CMAKE_CURRENT_BINARY_DIR}/Contract.json" COPYONLY)
add_executable(prompted-learning-clang-worker main.cpp Protocol.cpp Extraction.cpp Worker.rc)
target_compile_features(prompted-learning-clang-worker PRIVATE cxx_std_17)
target_compile_definitions(prompted-learning-clang-worker PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(prompted-learning-clang-worker PRIVATE /W4 /WX /wd4291 /utf-8 /EHsc)
target_include_directories(prompted-learning-clang-worker PRIVATE "${CMAKE_CURRENT_BINARY_DIR}")
target_include_directories(prompted-learning-clang-worker SYSTEM PRIVATE
  "${LLVM_PACKAGE}/include" "${LLVM_PACKAGE}/llvm/include"
  "${LLVM_PACKAGE}/clang/include" "${LLVM_PACKAGE}/build/include"
  "${LLVM_PACKAGE}/build/clang/include")
file(GLOB LLVM_LIBRARIES "${LLVM_PACKAGE}/build/lib/*.lib")
if(NOT LLVM_LIBRARIES)
  message(FATAL_ERROR "Pinned LLVM libraries missing")
endif()
target_link_libraries(prompted-learning-clang-worker PRIVATE ${LLVM_LIBRARIES}
  version ntdll bcrypt ws2_32 psapi shell32 ole32 uuid)

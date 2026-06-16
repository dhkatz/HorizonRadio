cmake_minimum_required(VERSION 3.25)

project(HorizonRadio
        VERSION 0.6.0
        LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Match the historic msbuild layout: every binary lands in
# build/<Config>/ at the repo root. The C# UI csproj (and dev tooling
# like deploy.ps1) reads version.dll + librespot.exe from these exact
# paths, so we preserve them rather than changing call sites.
foreach(_cfg Debug Release RelWithDebInfo MinSizeRel)
  string(TOUPPER "${_cfg}" _cfg_upper)
  set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_${_cfg_upper}
      "${CMAKE_SOURCE_DIR}/build/${_cfg}")
  set(CMAKE_LIBRARY_OUTPUT_DIRECTORY_${_cfg_upper}
      "${CMAKE_SOURCE_DIR}/build/${_cfg}")
endforeach()

if(MSVC)
  set(CMAKE_MSVC_RUNTIME_LIBRARY
      "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")
endif()

option(HORIZON_RUN_CLANG_TIDY "Run clang-tidy during compilation" OFF)
if(HORIZON_RUN_CLANG_TIDY)
  find_program(CLANG_TIDY_EXE NAMES clang-tidy REQUIRED)
  set(CMAKE_CXX_CLANG_TIDY "${CLANG_TIDY_EXE}")
endif()

option(HORIZON_BUILD_TESTS "Build the doctest test exe" ON)

add_subdirectory(HorizonRadio)
if(HORIZON_BUILD_TESTS)
  enable_testing()
  add_subdirectory(HorizonRadio.Tests)
endif()

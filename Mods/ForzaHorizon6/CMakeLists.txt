cmake_minimum_required(VERSION 3.25)

project(HorizonRadio
        VERSION 0.6.0
        LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Every binary lands in build/<Config>/ next to this file
# (Mods/ForzaHorizon6/build/<Config>/). The C# UI csproj, the dev-mode
# ModInstaller probe, and CI read version.dll from that exact path.
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

add_subdirectory(Bridge)
if(HORIZON_BUILD_TESTS)
  enable_testing()
  add_subdirectory(Tests)
endif()

cmake_minimum_required(VERSION 3.25)

# horizon_audio_tap.dll: loaded into Spotify Soloist when librespot is refused a track, to take the
# audio Soloist plays (see src/dllmain.cpp). Windows-only. Builds like the FH6 mod
# (Mods/ForzaHorizon6): MSVC via Ninja Multi-Config, doctest tests, clang-tidy in the analysis
# preset. HorizonRadio.Source.Spotify.csproj copies build/Release/horizon_audio_tap.dll into the app.
project(HorizonRadioAudioTap
        VERSION 1.0.0
        LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

foreach(_cfg Debug Release RelWithDebInfo MinSizeRel)
  string(TOUPPER "${_cfg}" _cfg_upper)
  set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_${_cfg_upper} "${CMAKE_SOURCE_DIR}/build/${_cfg}")
  set(CMAKE_LIBRARY_OUTPUT_DIRECTORY_${_cfg_upper} "${CMAKE_SOURCE_DIR}/build/${_cfg}")
endforeach()

# Static CRT: the DLL loads into someone else's process, which may not ship our CRT.
if(MSVC)
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

option(HORIZON_RUN_CLANG_TIDY "Run clang-tidy during compilation" OFF)
if(HORIZON_RUN_CLANG_TIDY)
  find_program(CLANG_TIDY_EXE NAMES clang-tidy REQUIRED)
  set(CMAKE_CXX_CLANG_TIDY "${CLANG_TIDY_EXE}")
endif()

# Shared by the DLL and the test exe.
set(TAP_CORE_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/src/tap_writer.cpp")

add_library(horizon_audio_tap SHARED
  src/dllmain.cpp
  ${TAP_CORE_SOURCES}
  include/horizon/tap/byte_ring.hpp
  include/horizon/tap/protocol.hpp
  include/horizon/tap/tap_writer.hpp
)
target_include_directories(horizon_audio_tap PRIVATE include)
target_compile_definitions(horizon_audio_tap PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE)
target_link_libraries(horizon_audio_tap PRIVATE ole32)
if(MSVC)
  # C4324: the ring's counters are padded to their own cache lines on purpose.
  target_compile_options(horizon_audio_tap PRIVATE /W4 /permissive- /utf-8 /wd4324)
endif()

option(HORIZON_BUILD_TESTS "Build the doctest test exe" ON)
if(HORIZON_BUILD_TESTS)
  enable_testing()
  add_subdirectory(Tests)
endif()

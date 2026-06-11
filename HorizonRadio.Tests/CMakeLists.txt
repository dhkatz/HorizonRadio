# Test executable. Runs doctest with the production translation units
# compiled directly into the exe — the simplest way to give tests
# access to internal helpers without exporting them from the DLL.
# CI just runs the exe and checks its exit code.

# doctest is header-only; FetchContent pulls it at configure time and
# unpacks under build/_deps/. Pinned to a specific tag so CI is
# reproducible — bump deliberately.
#
# CMAKE_POLICY_VERSION_MINIMUM=3.5 floors doctest's own
# `cmake_minimum_required(VERSION 2.8.5)` — CMake 4+ rejects anything
# below 3.5 outright. Remove once doctest bumps their min upstream.
include(FetchContent)
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
FetchContent_Declare(
  doctest
  GIT_REPOSITORY https://github.com/doctest/doctest.git
  GIT_TAG        v2.4.11
  GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(doctest)

set(HORIZON_TEST_SOURCES
  test_main.cpp
  test_fmod_bridge.cpp
  test_fmod_resolver.cpp
  test_fmod_types.cpp
  test_heap_scan.cpp
  test_metadata_injector.cpp
  test_msvc_rtti.cpp
  test_msvc_string.cpp
  test_normalizer.cpp
  test_ring_buffer.cpp
  test_sigscan.cpp
)

# The production TUs come straight from HORIZON_CORE_SOURCES (defined in
# HorizonRadio/CMakeLists.txt and exported to this scope) so there's one
# source list, not a hand-synced copy. dllmain.cpp / version_proxy.cpp
# are excluded there because their forwarders only make sense in a DLL.
add_executable(horizon_radio_tests
  ${HORIZON_TEST_SOURCES}
  ${HORIZON_CORE_SOURCES})

set_target_properties(horizon_radio_tests PROPERTIES
  OUTPUT_NAME HorizonRadio.Tests)

# horizon_common carries the shared include dirs, compile definitions,
# warning/optimization flags, and (on MinGW) the static-runtime link
# options; only the console subsystem is test-specific.
target_link_libraries(horizon_radio_tests PRIVATE horizon_common doctest::doctest)

if(MSVC)
  target_link_options(horizon_radio_tests PRIVATE /SUBSYSTEM:CONSOLE)
endif()

add_test(NAME horizon_radio_tests
         COMMAND horizon_radio_tests)

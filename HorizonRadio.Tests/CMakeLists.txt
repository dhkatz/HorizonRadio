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

# Production TUs compiled into the test exe. Mirrors the vcxproj
# version: every src/*.cpp the tests transitively need, except
# dllmain.cpp (its forwarders only make sense in a DLL).
set(HORIZON_PROD_SOURCES_FOR_TESTS
  ../HorizonRadio/src/fmod/bridge.cpp
  ../HorizonRadio/src/fmod/resolver.cpp
  ../HorizonRadio/src/fmod/system_resolver.cpp
  ../HorizonRadio/src/inject/game_resolver.cpp
  ../HorizonRadio/src/inject/heap_scan.cpp
  ../HorizonRadio/src/inject/metadata_injector.cpp
  ../HorizonRadio/src/inject/msvc_rtti.cpp
  ../HorizonRadio/src/inject/msvc_string.cpp
  ../HorizonRadio/src/inject/sigscan.cpp
)

add_executable(horizon_radio_tests
  ${HORIZON_TEST_SOURCES}
  ${HORIZON_PROD_SOURCES_FOR_TESTS})

set_target_properties(horizon_radio_tests PROPERTIES
  OUTPUT_NAME HorizonRadio.Tests)

target_include_directories(horizon_radio_tests PRIVATE
  ${CMAKE_SOURCE_DIR}/HorizonRadio/include)

target_compile_definitions(horizon_radio_tests PRIVATE
  WIN32_LEAN_AND_MEAN
  NOMINMAX
  _CRT_SECURE_NO_WARNINGS
  $<$<CONFIG:Debug>:_DEBUG>
  $<$<NOT:$<CONFIG:Debug>>:NDEBUG>)

target_link_libraries(horizon_radio_tests PRIVATE doctest::doctest)

if(MSVC)
  target_compile_options(horizon_radio_tests PRIVATE
    /W3
    /permissive-
    $<$<CONFIG:Release>:/O2>)
  target_link_options(horizon_radio_tests PRIVATE
    /SUBSYSTEM:CONSOLE
    /DEBUG)
else()
  # Same static-link rationale as the main DLL: the test exe should
  # not need MinGW runtime DLLs alongside to run on Windows.
  target_compile_options(horizon_radio_tests PRIVATE
    -Wall
    -Wextra
    $<$<CONFIG:Release>:-O2>)
  target_link_options(horizon_radio_tests PRIVATE
    -static-libgcc
    -static-libstdc++
    -Wl,-Bstatic,-lwinpthread,-Bdynamic)
endif()

add_test(NAME horizon_radio_tests
         COMMAND horizon_radio_tests)

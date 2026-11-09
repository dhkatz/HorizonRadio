# The tap's testable parts (ring, framing, formats), with the production TUs compiled in. The hooks
# themselves need a real audio device, so the C# side tests those where one exists.
include(FetchContent)
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
FetchContent_Declare(
  doctest
  GIT_REPOSITORY https://github.com/doctest/doctest.git
  GIT_TAG        v2.4.11
  GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(doctest)

add_executable(audio_tap_tests
  test_main.cpp
  test_byte_ring.cpp
  test_tap_writer.cpp
  ${TAP_CORE_SOURCES})

set_target_properties(audio_tap_tests PROPERTIES
  OUTPUT_NAME AudioTap.Tests
  CXX_CLANG_TIDY "")
target_include_directories(audio_tap_tests PRIVATE ../include)
target_compile_definitions(audio_tap_tests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE)
target_link_libraries(audio_tap_tests PRIVATE doctest::doctest)
if(MSVC)
  target_compile_options(audio_tap_tests PRIVATE /utf-8 /wd4324)
  target_link_options(audio_tap_tests PRIVATE /SUBSYSTEM:CONSOLE)
endif()

add_test(NAME audio_tap_tests COMMAND audio_tap_tests)

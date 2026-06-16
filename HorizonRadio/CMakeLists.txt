set(HORIZON_HEADERS
  include/horizon/audio/normalizer.hpp
  include/horizon/audio/ring_buffer.hpp
  include/horizon/fmod/bridge.hpp
  include/horizon/fmod/resolver.hpp
  include/horizon/fmod/signatures.hpp
  include/horizon/fmod/system_resolver.hpp
  include/horizon/fmod/types.hpp
  include/horizon/inject/game_resolver.hpp
  include/horizon/inject/heap_scan.hpp
  include/horizon/inject/metadata_injector.hpp
  include/horizon/inject/msvc_rtti.hpp
  include/horizon/inject/msvc_string.hpp
  include/horizon/inject/safe_mem.hpp
  include/horizon/inject/sigscan.hpp
  include/horizon/inject/title_write_controller.hpp
  include/horizon/ipc/ipc_server.hpp
  include/horizon/ipc/pcm_pipe_server.hpp
)

# Production TUs shared by the DLL and the test exe. dllmain.cpp and
# version_proxy.cpp are DLL-only (the DLL entry point + the version.dll
# export forwarders only make sense inside a DLL), so they're added to
# the `version` target directly below and are NOT in this list.
#
# Single source of truth: the absolute paths are pushed to PARENT_SCOPE
# so HorizonRadio.Tests/CMakeLists.txt compiles the exact same set into
# the test exe instead of maintaining a hand-synced copy.
set(_core_sources
  src/fmod/bridge.cpp
  src/fmod/resolver.cpp
  src/fmod/system_resolver.cpp
  src/inject/game_resolver.cpp
  src/inject/heap_scan.cpp
  src/inject/metadata_injector.cpp
  src/inject/msvc_rtti.cpp
  src/inject/msvc_string.cpp
  src/inject/sigscan.cpp
  src/ipc/ipc_server.cpp
  src/ipc/pcm_pipe_server.cpp
)
list(TRANSFORM _core_sources PREPEND "${CMAKE_CURRENT_SOURCE_DIR}/")
# Current scope (for the `version` target below) and PARENT_SCOPE (for
# HorizonRadio.Tests, a sibling add_subdirectory) — PARENT_SCOPE alone
# wouldn't define it here.
set(HORIZON_CORE_SOURCES "${_core_sources}")
set(HORIZON_CORE_SOURCES "${_core_sources}" PARENT_SCOPE)

# Compile/link settings shared by the DLL and the test exe so the two
# targets can't drift. Target-specific bits (DLL defines/subsystem/LTCG,
# test console subsystem) stay on the individual targets below.
add_library(horizon_common INTERFACE)
target_include_directories(horizon_common INTERFACE
  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>)
target_compile_definitions(horizon_common INTERFACE
  WIN32_LEAN_AND_MEAN
  NOMINMAX
  _CRT_SECURE_NO_WARNINGS
  $<$<CONFIG:Debug>:_DEBUG>
  $<$<NOT:$<CONFIG:Debug>>:NDEBUG>)
if(MSVC)
  target_compile_options(horizon_common INTERFACE
    /W3
    /permissive-
    $<$<CONFIG:Release>:/O2>)
  target_link_options(horizon_common INTERFACE /DEBUG)
else()
  # clang/gcc targeting Windows (MinGW). Statically link the C++ stdlib +
  # libgcc + pthread so neither the DLL nor the test exe needs
  # libstdc++-6.dll / libgcc_s_seh-1.dll / libwinpthread-1.dll alongside.
  target_compile_options(horizon_common INTERFACE
    -Wall
    -Wextra
    $<$<CONFIG:Release>:-O2>)
  target_link_options(horizon_common INTERFACE
    -static-libgcc
    -static-libstdc++
    -Wl,-Bstatic,-lwinpthread,-Bdynamic)
endif()

add_library(version SHARED
  src/dllmain.cpp
  src/version_proxy.cpp
  ${HORIZON_CORE_SOURCES}
  ${HORIZON_HEADERS})
target_link_libraries(version PRIVATE horizon_common)

# version_proxy.cpp is pure forwarder trampolines whose signatures
# intentionally redeclare <winver.h>'s exports; clang-tidy's clang-cl
# frontend flags those as conflicting-type / dllimport-mismatch errors
# that don't apply to the MSVC build. Nothing to lint here anyway, so
# skip it. (SKIP_LINTING is CMake 3.27+; older CMake ignores it.)
set_source_files_properties(src/version_proxy.cpp PROPERTIES SKIP_LINTING ON)

# clang+MinGW prepends "lib" to shared libraries by default (POSIX
# convention). FH6's loader searches for the bare name `version.dll`,
# not `libversion.dll` — strip the prefix.
set_target_properties(version PROPERTIES PREFIX "")

target_compile_definitions(version PRIVATE
  _WINDOWS
  _USRDLL)

if(MSVC)
  # /GL + /LTCG = the old vcxproj's <WholeProgramOptimization> for the
  # shipped Release DLL (cross-TU inlining/devirtualization). Kept off
  # for the test exe (perf irrelevant there) and off in Debug.
  target_compile_options(version PRIVATE
    $<$<CONFIG:Release>:/Gy>
    $<$<CONFIG:Release>:/Oi>
    $<$<CONFIG:Release>:/GL>)
  target_link_options(version PRIVATE
    /SUBSYSTEM:WINDOWS
    $<$<CONFIG:Release>:/OPT:REF>
    $<$<CONFIG:Release>:/OPT:ICF>
    $<$<CONFIG:Release>:/LTCG>)
else()
  target_link_options(version PRIVATE -Wl,--subsystem,windows)
endif()

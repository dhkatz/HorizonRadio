set(HORIZON_SOURCES
  src/dllmain.cpp
  src/version_proxy.cpp
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
  include/horizon/ipc/ipc_server.hpp
  include/horizon/ipc/pcm_pipe_server.hpp
)

add_library(version SHARED ${HORIZON_SOURCES} ${HORIZON_HEADERS})

# clang+MinGW prepends "lib" to shared libraries by default (POSIX
# convention). FH6's loader searches for the bare name `version.dll`,
# not `libversion.dll` — strip the prefix.
set_target_properties(version PROPERTIES PREFIX "")

target_include_directories(version PUBLIC
  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>)

target_compile_definitions(version PRIVATE
  _WINDOWS
  _USRDLL
  WIN32_LEAN_AND_MEAN
  NOMINMAX
  _CRT_SECURE_NO_WARNINGS
  $<$<CONFIG:Debug>:_DEBUG>
  $<$<NOT:$<CONFIG:Debug>>:NDEBUG>)

if(MSVC)
  target_compile_options(version PRIVATE
    /W3
    /permissive-
    $<$<CONFIG:Release>:/O2>
    $<$<CONFIG:Release>:/Gy>
    $<$<CONFIG:Release>:/Oi>)
  target_link_options(version PRIVATE
    /SUBSYSTEM:WINDOWS
    /DEBUG
    $<$<CONFIG:Release>:/OPT:REF>
    $<$<CONFIG:Release>:/OPT:ICF>)
else()
  # clang/gcc targeting Windows (MinGW). Drop the MSVC runtime + flag
  # spellings and statically link the C++ stdlib + libgcc + pthread
  # so the produced version.dll doesn't need libstdc++-6.dll,
  # libgcc_s_seh-1.dll, libwinpthread-1.dll dropped alongside it.
  target_compile_options(version PRIVATE
    -Wall
    -Wextra
    $<$<CONFIG:Release>:-O2>)
  target_link_options(version PRIVATE
    -Wl,--subsystem,windows
    -static-libgcc
    -static-libstdc++
    -Wl,-Bstatic,-lwinpthread,-Bdynamic)
endif()

# Cross-compile to Windows x64 from Linux or macOS using clang
# (frontend) + mingw-w64 (headers, import libs, runtime).
#
# Why clang and not mingw-w64's gcc:
#   - clang accepts `#pragma comment` in MSVC-target mode and emits
#     usable SEH (__try/__except) on MinGW targets too. gcc silently
#     drops the pragmas and lacks SEH on Windows targets, which would
#     break safe_mem.hpp and metadata_injector.cpp.
#   - One toolchain spelling works on macOS (LLVM) and Linux (Debian
#     packages), so the dev story is consistent.
#
# Install once:
#   macOS:   brew install llvm lld mingw-w64 ninja
#   Linux:   apt install clang lld mingw-w64 ninja-build
#            (Fedora: dnf install clang lld mingw64-gcc mingw64-winpthreads-static ninja-build)
#
# Why lld specifically (not the BFD ld that ships with mingw-w64):
# mingw-w64's libstdc++.a contains RTTI sections whose sizes differ
# between gcc and clang object files. BFD ld treats this as a hard
# error ("duplicate section ... has different size"); lld merges
# them silently and produces a working DLL.
#
# Then configure:
#   cmake --preset macos-cross-x64    (or linux-cross-x64)
#   cmake --build --preset macos-cross-x64-release

set(CMAKE_SYSTEM_NAME      Windows)
set(CMAKE_SYSTEM_PROCESSOR x86_64)

# Discover the mingw-w64 sysroot (the tree that holds windows.h).
# Package managers lay this out differently and `gcc -print-sysroot` is
# only populated on some of them — Homebrew returns the Cellar path,
# but Debian/Ubuntu return an EMPTY string (no sysroot configured;
# headers sit at /usr/x86_64-w64-mingw32). So rather than trust one
# method, probe the known locations and take the first that actually
# contains the Windows headers.
find_program(_MINGW_GCC NAMES x86_64-w64-mingw32-gcc)
if(NOT _MINGW_GCC)
  message(FATAL_ERROR
    "x86_64-w64-mingw32-gcc not found on PATH. "
    "Install mingw-w64 (brew install mingw-w64 / apt install mingw-w64).")
endif()
execute_process(
  COMMAND "${_MINGW_GCC}" -print-sysroot
  OUTPUT_VARIABLE _MINGW_PRINT_SYSROOT
  OUTPUT_STRIP_TRAILING_WHITESPACE)

# Candidates, in priority order:
#   <print-sysroot>/x86_64-w64-mingw32   Homebrew / sysroot-style
#   <print-sysroot>                       headers directly under it
#   <gcc-prefix>/x86_64-w64-mingw32       Debian/Ubuntu/Arch (e.g. /usr/...)
#   <gcc-prefix>/.../sys-root/mingw       Fedora
# Empty/non-existent candidates fall through the EXISTS check below.
get_filename_component(_MINGW_BINDIR "${_MINGW_GCC}" DIRECTORY)
get_filename_component(_MINGW_PREFIX "${_MINGW_BINDIR}" DIRECTORY)
set(_MINGW_SYSROOT_CANDIDATES
  "${_MINGW_PRINT_SYSROOT}/x86_64-w64-mingw32"
  "${_MINGW_PRINT_SYSROOT}"
  "${_MINGW_PREFIX}/x86_64-w64-mingw32"
  "${_MINGW_PREFIX}/x86_64-w64-mingw32/sys-root/mingw"
  "/usr/x86_64-w64-mingw32"
  "/usr/x86_64-w64-mingw32/sys-root/mingw")
set(_MINGW_SYSROOT "")
foreach(_cand IN LISTS _MINGW_SYSROOT_CANDIDATES)
  if(_cand AND EXISTS "${_cand}/include/windows.h")
    set(_MINGW_SYSROOT "${_cand}")
    break()
  endif()
endforeach()
if(NOT _MINGW_SYSROOT)
  message(FATAL_ERROR
    "Could not locate the mingw-w64 sysroot: no 'include/windows.h' found "
    "under print-sysroot, ${_MINGW_PREFIX}/x86_64-w64-mingw32, "
    "/usr/x86_64-w64-mingw32, or a Fedora sys-root layout. Reinstall "
    "mingw-w64 or set CMAKE_SYSROOT manually before configuring.")
endif()

# macOS's /usr/bin/clang is Apple Clang and lacks the mingw-w64
# target. Force Homebrew LLVM (keg-only, so not on PATH by default).
# Linux/distros typically ship a clang that already supports the
# target, so we fall through to PATH lookup there.
#
# CMAKE_HOST_APPLE (not APPLE): CMAKE_SYSTEM_NAME=Windows above flips
# APPLE off during toolchain evaluation because APPLE describes the
# *target*. We care about the host here.
# Toolchain files are re-evaluated multiple times during configure;
# the NOT-already-set gate is the standard pattern that avoids
# overwriting CMake's own cached compiler choice on later passes.
if(NOT CMAKE_C_COMPILER)
  if(CMAKE_HOST_APPLE)
    find_program(_HZN_CLANG NAMES clang
                 PATHS /opt/homebrew/opt/llvm/bin /usr/local/opt/llvm/bin
                 NO_DEFAULT_PATH)
    if(NOT _HZN_CLANG)
      message(FATAL_ERROR
        "Apple Clang doesn't ship a mingw-w64 target. Install Homebrew LLVM "
        "(brew install llvm) or set CMAKE_C_COMPILER to a clang that does.")
    endif()
    get_filename_component(_HZN_CLANG_DIR "${_HZN_CLANG}" DIRECTORY)
    set(CMAKE_C_COMPILER   "${_HZN_CLANG}")
    set(CMAKE_CXX_COMPILER "${_HZN_CLANG_DIR}/clang++")
  else()
    set(CMAKE_C_COMPILER   clang)
    set(CMAKE_CXX_COMPILER clang++)
  endif()
endif()

set(_triple x86_64-w64-mingw32)
set(CMAKE_C_COMPILER_TARGET   ${_triple})
set(CMAKE_CXX_COMPILER_TARGET ${_triple})

# Both tools that clang invokes for resource compilation. Prefer
# llvm-rc (always present alongside clang) but fall back to the
# MinGW-shipped windres on installs that lack it.
find_program(CMAKE_RC_COMPILER NAMES llvm-rc x86_64-w64-mingw32-windres windres REQUIRED)
find_program(CMAKE_AR          NAMES llvm-ar x86_64-w64-mingw32-ar         REQUIRED)
find_program(CMAKE_RANLIB      NAMES llvm-ranlib x86_64-w64-mingw32-ranlib REQUIRED)

set(CMAKE_SYSROOT "${_MINGW_SYSROOT}")
set(CMAKE_FIND_ROOT_PATH "${_MINGW_SYSROOT}")

# Tell clang where mingw-w64's gcc-internal libs live (libgcc,
# libstdc++, libsupc++). `--sysroot` covers only <sysroot>/lib;
# gcc's runtime is in a sibling tree (<toolchain>/lib/gcc/...).
# `--gcc-toolchain=` is the obvious flag but clang silently ignores
# it for mingw targets, so we discover the dir from gcc itself and
# pass it as a literal -L.
execute_process(
  COMMAND "${_MINGW_GCC}" -print-libgcc-file-name
  OUTPUT_VARIABLE _MINGW_LIBGCC
  OUTPUT_STRIP_TRAILING_WHITESPACE)
get_filename_component(_MINGW_LIBGCC_DIR "${_MINGW_LIBGCC}" DIRECTORY)
add_link_options(-L${_MINGW_LIBGCC_DIR})

# Same problem on the header side. clang doesn't know about gcc's
# libstdc++ layout in the sysroot (<sysroot>/include/c++/<ver>/...)
# or about gcc's own intrinsics/fixed headers
# (<toolchain>/lib/gcc/<triple>/<ver>/{include,include-fixed}). Glob
# the version dirs so we don't hardcode 15.2.0 here, then `-isystem`
# each one.
# SHELL: prefix prevents CMake from de-duplicating the repeated
# `-isystem` flag — without it, only the first path actually reaches
# the compiler and the rest fall through as bare positional args.
#
# Only the libstdc++ paths are added; the C-internal "compiler builtin"
# directory (<toolchain>/lib/gcc/<triple>/<ver>/include) is NOT added
# because it ships gcc-flavored x86 intrinsics that clang can't parse.
# clang supplies its own intrinsics from its resource dir, which is on
# the search path automatically.
# SEH (__try/__except) is an MSVC-extension keyword. clang only
# recognizes it when -fms-extensions is on. The Windows MSVC target
# turns this on by default; the MinGW target does not, so we opt in
# explicitly. safe_mem.hpp + metadata_injector.cpp depend on it.
add_compile_options(-fms-extensions)

# Pick the libstdc++ headers that MATCH the libgcc we link against. The
# version is the basename of the libgcc dir (…/lib/gcc/<triple>/<ver>/),
# and libstdc++ headers live at <sysroot>/include/c++/<ver>/. On a host
# with two mingw gcc versions installed, blindly taking the first glob
# entry can pair <ver-A> headers with <ver-B> libs → RTTI/ABI skew. Match
# by version; fall back to the highest version present if the layout
# doesn't line up.
get_filename_component(_MINGW_GCC_VER "${_MINGW_LIBGCC_DIR}" NAME)
file(GLOB _MINGW_CXX_DIRS LIST_DIRECTORIES TRUE "${_MINGW_SYSROOT}/include/c++/*")
list(SORT _MINGW_CXX_DIRS COMPARE NATURAL ORDER DESCENDING)
set(_MINGW_CXX_INC "")
foreach(_dir IN LISTS _MINGW_CXX_DIRS)
  if(_dir MATCHES "/${_MINGW_GCC_VER}$")
    set(_MINGW_CXX_INC "${_dir}")
    break()
  endif()
endforeach()
if(NOT _MINGW_CXX_INC AND _MINGW_CXX_DIRS)
  list(GET _MINGW_CXX_DIRS 0 _MINGW_CXX_INC) # highest version (sorted desc)
endif()
if(_MINGW_CXX_INC)
  add_compile_options(
    "SHELL:-isystem ${_MINGW_CXX_INC}"
    "SHELL:-isystem ${_MINGW_CXX_INC}/${_triple}"
    "SHELL:-isystem ${_MINGW_CXX_INC}/backward")
endif()

# Headers + libs come from the sysroot; programs (compilers, linkers)
# still come from the host PATH.
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)

# Use lld. mingw-w64's BFD ld errors on sections from clang object
# files merged with gcc's libstdc++.a ("duplicate section ... has
# different size") because the two compilers emit subtly different
# RTTI layouts. lld is permissive about this and produces a working
# DLL.
add_link_options(-fuse-ld=lld)

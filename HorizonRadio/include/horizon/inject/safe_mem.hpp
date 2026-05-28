#pragma once

#include <windows.h>

#include <cstddef>
#include <cstdint>
#include <cstring>

// Memory-safety primitives for the heap scan, RTTI walker, and metadata
// injector. We dereference signature-scanned pointers and walk pointer
// chains through game-owned objects, so any wrong byte would otherwise be
// an access violation in the game process.
//
// Two complementary primitives are provided:
//
//   is_readable / safe_read_*  — cheap VirtualQuery guard before the read.
//     One syscall; rejects unmapped, guarded, and execute-only pages.
//     Doesn't catch races where the page is freed between the query and
//     the read. Use in cold paths where the target memory is stable
//     (resolved globals, module sections).
//
//   seh_call(fn)               — structured exception handler wrapping a
//     lambda. Catches access violations during the body. More expensive
//     than is_readable (x64 SEH frame setup), but survives races. Use in
//     hot scan loops where a page might be freed mid-iteration.
//
// SEH/C++ destructor caveat (MSVC C2712): __try/__except cannot live in
// the same function as a C++ object with a destructor. The function-local
// helpers below contain no destructible locals; the seh_call template
// solves the caller's side by parking __try in the helper while the
// lambda's destructible locals live in a separate function frame.

namespace horizon::inject {

// True iff [addr, addr+size) lies in committed pages with read access and
// no PAGE_GUARD. Cheap (one VirtualQuery); does not catch races.
inline bool is_readable(const void* addr, std::size_t size) noexcept {
    if (addr == nullptr || size == 0) return false;
    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(addr, &mbi, sizeof(mbi)) != sizeof(mbi)) return false;
    if (mbi.State != MEM_COMMIT) return false;
    if (mbi.Protect & PAGE_GUARD) return false;
    if (mbi.Protect & PAGE_NOACCESS) return false;
    constexpr DWORD readable =
        PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY |
        PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    if ((mbi.Protect & readable) == 0) return false;
    const auto* end_required = static_cast<const std::byte*>(addr) + size;
    const auto* region_end   = static_cast<const std::byte*>(mbi.BaseAddress) + mbi.RegionSize;
    return end_required <= region_end;
}

// SEH-wrapped pointer-sized read. Returns 0 if the address AVs.
// noexcept + no destructible locals so MSVC accepts __try here.
inline std::uintptr_t safe_read_qword(const void* addr) noexcept {
    __try {
        return *reinterpret_cast<const std::uintptr_t*>(addr);
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
                ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        return 0;
    }
}

// SEH-wrapped bulk read. Returns false on AV.
inline bool safe_read_bytes(void* dst, const void* src, std::size_t n) noexcept {
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
                ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        return false;
    }
}

// Run `fn()` under SEH. Returns true if it completed, false if it raised.
//
// The template body has no C++ destructors, so MSVC accepts __try inside.
// The lambda's body (where the caller's locals with destructors live) is a
// SEPARATE function frame; the C2712 restriction applies per-function, so
// destructible locals are fine on the caller side.
//
// Caveat: SEH does NOT run C++ destructors during the stack unwind from
// the access violation. Any destructible locals inside the lambda are
// leaked when the AV is caught. Acceptable for the loops where we use
// this (heap scan iterations) since the lambda body should be trivially
// destructible — keep it that way.
template <class Fn>
inline bool seh_call(Fn&& fn) noexcept {
    __try {
        fn();
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

} // namespace horizon::inject

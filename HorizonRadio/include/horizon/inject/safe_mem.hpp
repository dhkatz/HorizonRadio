#pragma once

#include <cstddef>
#include <cstring>
#include <utility>
#include <windows.h>

namespace horizon::inject {

inline bool is_readable(const void* addr, std::size_t size) noexcept {
    if (addr == nullptr || size == 0)
        return false;
    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(addr, &mbi, sizeof(mbi)) != sizeof(mbi))
        return false;
    if (mbi.State != MEM_COMMIT)
        return false;
    if (mbi.Protect & PAGE_GUARD)
        return false;
    if (mbi.Protect & PAGE_NOACCESS)
        return false;
    constexpr DWORD readable = PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ |
                               PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    if ((mbi.Protect & readable) == 0)
        return false;
    const auto* end_required = static_cast<const std::byte*>(addr) + size;
    const auto* region_end   = static_cast<const std::byte*>(mbi.BaseAddress) + mbi.RegionSize;
    return end_required <= region_end;
}

inline std::uintptr_t safe_read_qword(const void* addr) noexcept {
    __try {
        return *static_cast<const std::uintptr_t*>(addr);
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER
                                                                 : EXCEPTION_CONTINUE_SEARCH) {
        return 0;
    }
}

inline bool safe_read_bytes(void* dst, const void* src, std::size_t n) noexcept {
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER
                                                                 : EXCEPTION_CONTINUE_SEARCH) {
        return false;
    }
}

template <class Fn> bool seh_call(Fn&& fn) noexcept {
    __try {
        std::forward<Fn>(fn)();
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

} // namespace horizon::inject

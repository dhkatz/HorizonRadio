#include <horizon/inject/heap_scan.hpp>

#include <windows.h>

#include <cstdint>

namespace horizon::inject {

namespace {

constexpr DWORD kReadableProtectMask =
    PAGE_READONLY | PAGE_READWRITE |
    PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE |
    PAGE_WRITECOPY;

constexpr DWORD kUnreadableFlags = PAGE_GUARD | PAGE_NOACCESS;

bool region_is_scannable(const MEMORY_BASIC_INFORMATION& mbi) {
    if (mbi.State != MEM_COMMIT)    return false;
    if (mbi.Type  != MEM_PRIVATE)   return false;        // skip MEM_IMAGE/MEM_MAPPED
    if (mbi.Protect & kUnreadableFlags) return false;
    if (!(mbi.Protect & kReadableProtectMask)) return false;
    return true;
}

} // namespace

std::vector<const void*> find_heap_instances(const void* target) {
    std::vector<const void*> matches;
    find_heap_instances_streaming(target, matches,
                                  [] { return true; },
                                  [](std::size_t) {});
    return matches;
}

void find_heap_instances_streaming(
    const void* target,
    std::vector<const void*>& matches_out,
    const std::function<bool()>& should_continue,
    const std::function<void(std::size_t)>& on_progress) {
    const auto target_val = reinterpret_cast<std::uintptr_t>(target);

    MEMORY_BASIC_INFORMATION mbi{};
    const auto* cursor = static_cast<const std::byte*>(nullptr);
    std::size_t bytes_since_progress = 0;
    std::size_t bytes_total          = 0;
    constexpr std::size_t kProgressInterval = 64 * 1024 * 1024;

    while (VirtualQuery(cursor, &mbi, sizeof(mbi)) == sizeof(mbi)) {
        if (region_is_scannable(mbi)) {
            const auto* p = static_cast<const std::uintptr_t*>(mbi.BaseAddress);
            const std::size_t n = mbi.RegionSize / sizeof(std::uintptr_t);
            for (std::size_t i = 0; i < n; ++i) {
                if (p[i] == target_val) {
                    matches_out.push_back(reinterpret_cast<const void*>(p + i));
                }
            }
            bytes_since_progress += mbi.RegionSize;
            bytes_total          += mbi.RegionSize;
            if (bytes_since_progress >= kProgressInterval) {
                on_progress(bytes_total);
                if (!should_continue()) return;
                bytes_since_progress = 0;
            }
        }

        const auto* next = static_cast<const std::byte*>(mbi.BaseAddress) + mbi.RegionSize;
        if (next <= cursor) break;
        cursor = next;
    }
    on_progress(bytes_total);
}

} // namespace horizon::inject

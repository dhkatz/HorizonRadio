#include <cstdint>
#include <horizon/inject/game_resolver.hpp>
#include <horizon/inject/safe_mem.hpp>
#include <windows.h>

namespace horizon::game {

namespace {

using inject::safe_read_qword;

} // namespace

GameResolver::GameResolver(const inject::PeImage& image) : image_(image) {}

void* GameResolver::resolve_global_via_rip_load(std::string_view pattern, std::ptrdiff_t mov_offset_in_match) const {
    if (pattern.empty())
        return nullptr;

    const std::byte* match = inject::find_pattern(image_.text(), pattern);
    if (match == nullptr)
        return nullptr;

    // The MOV should be `48 8B 1D <disp32>`. Sanity-check the opcode
    // bytes so we don't misdecode if the pattern matched but the
    // offset is wrong.
    const std::byte* mov = match + mov_offset_in_match;
    if (static_cast<std::uint8_t>(mov[0]) != 0x48 || static_cast<std::uint8_t>(mov[1]) != 0x8B ||
        static_cast<std::uint8_t>(mov[2]) != 0x1D) {
        return nullptr;
    }

    std::int32_t disp32 = 0;
    std::memcpy(&disp32, mov + 3, sizeof(disp32));

    // The instruction is 7 bytes total; RIP after fetch points at mov+7.
    auto* mov_end     = mov + 7;
    auto  target_addr = reinterpret_cast<std::uintptr_t>(mov_end) + static_cast<std::intptr_t>(disp32);
    return reinterpret_cast<void*>(target_addr);
}

std::vector<const void*> find_instances_in_heap_arenas(const void* expected_vtable, const inject::PeImage& image,
                                                       std::size_t min_region_bytes, std::size_t max_hits) {
    std::vector<const void*> matches;
    if (expected_vtable == nullptr)
        return matches;
    const auto vt = reinterpret_cast<std::uintptr_t>(expected_vtable);

    const auto mod_lo = image.base();
    const auto mod_hi = mod_lo + image.image_size();

    SYSTEM_INFO si{};
    GetSystemInfo(&si);
    const auto* addr     = static_cast<const std::byte*>(si.lpMinimumApplicationAddress);
    const auto* max_addr = static_cast<const std::byte*>(si.lpMaximumApplicationAddress);

    constexpr std::size_t kMaxRegionBytes = 0x4000000; // 64 MB

    while (addr < max_addr && matches.size() < max_hits) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (VirtualQuery(addr, &mbi, sizeof(mbi)) != sizeof(mbi))
            break;

        const auto* region_base = static_cast<const std::byte*>(mbi.BaseAddress);
        const auto  region_size = mbi.RegionSize;
        const auto* region_end  = region_base + region_size;

        // Heap-shaped: committed, private (not mapped/image), plain
        // R/W or R/WCOPY (no execute, no guard), reasonably sized,
        // doesn't overlap the FH6 module image (we don't want hits in
        // .data/.bss).
        const auto region_addr     = reinterpret_cast<std::uintptr_t>(region_base);
        const bool overlaps_module = region_addr >= mod_lo && region_addr < mod_hi;
        const bool prot_ok =
            mbi.Protect == PAGE_READWRITE || mbi.Protect == PAGE_WRITECOPY || mbi.Protect == PAGE_READONLY;
        const bool is_heap_shaped = mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE && prot_ok && !overlaps_module &&
                                    region_size >= min_region_bytes && region_size <= kMaxRegionBytes;

        if (is_heap_shaped) {
            // 16-byte aligned start, 8-byte aligned end (so the final
            // 24-byte read of slot/use/weak/inner-vtable stays in
            // bounds).
            auto*       scan = reinterpret_cast<const std::byte*>((region_addr + 15) & ~std::uintptr_t{15});
            const auto* scan_end =
                reinterpret_cast<const std::byte*>(reinterpret_cast<std::uintptr_t>(region_end) & ~std::uintptr_t{7});
            for (; scan + 24 <= scan_end && matches.size() < max_hits; scan += 16) {
                const auto* slot = reinterpret_cast<const std::uintptr_t*>(scan);
                if (safe_read_qword(slot) != vt)
                    continue;

                // Refcount sanity: `uses` and `weaks` are uint32s
                // bounded by allocation lifetime; a real shared_ptr
                // typically has both <= a few; > 0x80 is implausible.
                std::uint32_t use_w = 0, weak_w = 0;
                std::memcpy(&use_w, scan + 8, 4);
                std::memcpy(&weak_w, scan + 12, 4);
                if (use_w == 0 || weak_w == 0)
                    continue;
                if (use_w > 0x80 || weak_w > 0x80)
                    continue;

                // Inner vtable check: `_Ref_count_obj2<T>` embeds T
                // at +16; T also has a vtable, which must live in
                // the FH6 module's .rdata. This is what eliminates
                // /OPT:ICF false matches: random heap bytes equal to
                // our vtable won't have a module pointer at +16.
                const auto inner_vt = safe_read_qword(scan + 16);
                if (inner_vt < mod_lo || inner_vt >= mod_hi)
                    continue;

                matches.push_back(slot);
            }
        }

        if (region_end <= addr)
            break;
        addr = region_end;
    }
    return matches;
}

} // namespace horizon::game

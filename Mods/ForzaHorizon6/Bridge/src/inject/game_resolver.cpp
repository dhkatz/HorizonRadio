#include <cstdint>
#include <cstring>
#include <horizon/inject/game_resolver.hpp>
#include <horizon/inject/safe_mem.hpp>
#include <windows.h>

namespace horizon::game {

namespace {

using inject::safe_read_qword;

} // namespace

GlobalResolution resolve_rip_load_global(const std::span<const std::byte> text, const inject::Pattern& pattern,
                                         const std::ptrdiff_t mov_offset_in_match, const std::uintptr_t image_lo,
                                         const std::uintptr_t image_hi) {
    GlobalResolution out;
    if (pattern.bytes.empty())
        return out;

    // `mov rbx, [rip+disp32]` is 7 bytes and must sit inside the pattern, so
    // the bytes we decode are bytes the pattern actually matched.
    constexpr std::ptrdiff_t kMovLen = 7;
    if (mov_offset_in_match < 0 || mov_offset_in_match + kMovLen > static_cast<std::ptrdiff_t>(pattern.bytes.size())) {
        out.status = GlobalResolution::Status::unexpected_instruction;
        return out;
    }

    const auto matches = inject::find_pattern_matches(text, pattern);
    out.match_count    = matches.count;
    if (matches.count == 0) {
        out.status = GlobalResolution::Status::not_found;
        return out;
    }
    if (matches.count > 1) {
        out.status = GlobalResolution::Status::ambiguous;
        return out;
    }

    // Sanity-check the opcode bytes so we don't misdecode if the pattern
    // matched but the offset is wrong.
    const std::byte* mov = matches.first + mov_offset_in_match;
    if (std::to_integer<std::uint8_t>(mov[0]) != 0x48 || std::to_integer<std::uint8_t>(mov[1]) != 0x8B ||
        std::to_integer<std::uint8_t>(mov[2]) != 0x1D) {
        out.status = GlobalResolution::Status::unexpected_instruction;
        return out;
    }

    std::int32_t disp32 = 0;
    std::memcpy(&disp32, mov + 3, sizeof(disp32));

    // RIP after fetch points at the end of the instruction.
    const auto target = reinterpret_cast<std::uintptr_t>(mov + kMovLen) + static_cast<std::intptr_t>(disp32);
    if (target < image_lo || target >= image_hi) {
        out.status = GlobalResolution::Status::target_outside_image;
        return out;
    }

    out.status  = GlobalResolution::Status::ok;
    out.address = reinterpret_cast<void*>(target);
    return out;
}

GameResolver::GameResolver(const inject::PeImage& image) : image_(image) {}

GlobalResolution GameResolver::resolve_global_via_rip_load(const std::string_view pattern,
                                                           const std::ptrdiff_t   mov_offset_in_match) const {
    if (pattern.empty() || !image_.valid())
        return {};
    return resolve_rip_load_global(image_.text(), inject::compile_pattern(pattern), mov_offset_in_match, image_.base(),
                                   image_.base() + image_.image_size());
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

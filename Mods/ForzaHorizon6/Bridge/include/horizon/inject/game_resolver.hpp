#pragma once

#include <cstddef>
#include <cstdint>
#include <horizon/inject/sigscan.hpp>
#include <span>
#include <string_view>
#include <vector>

namespace horizon::game {

// Outcome of resolving a global through a RIP-relative load. Every failure
// mode is distinct so a game update that breaks the signature is diagnosable
// from the log alone.
struct GlobalResolution {
    enum class Status {
        ok,
        empty_pattern,
        not_found,              // pattern matched nowhere in .text
        ambiguous,              // pattern matched more than once; refuse to guess
        unexpected_instruction, // match found, but no `48 8B 1D` at the offset
        target_outside_image,   // decoded address isn't inside the game module
    };

    Status      status      = Status::empty_pattern;
    std::size_t match_count = 0; // capped at 2 (enough to flag ambiguity)
    void*       address     = nullptr;
};

// Pure decode step, separated from PeImage so it can be unit-tested on a
// synthetic buffer: find `pattern` in `text` (it must match exactly once),
// decode the `48 8B 1D <disp32>` (`mov rbx, [rip+disp32]`) at
// `mov_offset_in_match`, and require the loaded-from address to fall inside
// [image_lo, image_hi) -- a global always lives in the module's own data.
[[nodiscard]] GlobalResolution resolve_rip_load_global(std::span<const std::byte> text, const inject::Pattern& pattern,
                                                       std::ptrdiff_t mov_offset_in_match, std::uintptr_t image_lo,
                                                       std::uintptr_t image_hi);

// Resolves game-side globals (e.g. RadioState*) by sigscanning .text and
// decoding the matched function's `mov reg, [rip+disp32]`.
class GameResolver {
public:
    explicit GameResolver(const inject::PeImage& image);

    // resolve_rip_load_global over the game image's .text, with the module's
    // own address range as the valid target range.
    [[nodiscard]] GlobalResolution resolve_global_via_rip_load(std::string_view pattern,
                                                               std::ptrdiff_t   mov_offset_in_match) const;

private:
    const inject::PeImage& image_;
};

// Whole-process heap scan for `_Ref_count_obj2<T>` instances (see
// docs/architecture.md -> "Finding the instances"). Walks committed MEM_PRIVATE
// regions at 16-byte stride; a slot must satisfy ALL of (the last three reject
// /OPT:ICF false matches):
//   *(uint64*)(slot + 0)  == expected_vtable
//   *(uint32*)(slot + 8)   in (0, 0x80]            (refcount `uses`)
//   *(uint32*)(slot + 12)  in (0, 0x80]            (refcount `weaks`)
//   *(void**)(slot + 16)   inside the module range (embedded T's vtable)
std::vector<const void*> find_instances_in_heap_arenas(const void* expected_vtable, const inject::PeImage& image,
                                                       std::size_t min_region_bytes = std::size_t{64} * 1024,
                                                       std::size_t max_hits         = 64);

} // namespace horizon::game

#pragma once

#include <horizon/inject/sigscan.hpp>
#include <string_view>
#include <vector>

namespace horizon::game {

// Resolves game-side globals (e.g. RadioState*) by sigscanning .text and
// decoding the matched function's `mov reg, [rip+disp32]`.
class GameResolver {
public:
    explicit GameResolver(const inject::PeImage& image);

    // Sigscan `pattern` in .text, locate the `48 8B 1D <disp32>`
    // instruction at `mov_offset_in_match` within the match, decode
    // the RIP-relative displacement, and return the address of the
    // global it loads from. Returns nullptr if the pattern is missing
    // or the MOV bytes don't match the expected `48 8B 1D` prefix.
    [[nodiscard]] void* resolve_global_via_rip_load(std::string_view pattern, std::ptrdiff_t mov_offset_in_match) const;

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

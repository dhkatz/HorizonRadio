#pragma once

#include <horizon/inject/sigscan.hpp>

#include <cstddef>
#include <string_view>
#include <vector>

namespace horizon::game {

// Resolves game-side singletons and global pointers via sigscan +
// RIP-relative decoding. Replaces the brute-force heap scan: a sigscan
// over .text is sub-second and finds exactly one match for the
// well-chosen patterns we use; decoding the matched function's
// `mov reg, [rip+disp32]` instruction gives us the address of a
// global that holds the data we want (e.g., RadioState*).
class GameResolver {
public:
    explicit GameResolver(const horizon::inject::PeImage& image);

    // Sigscan `pattern` in .text, locate the `48 8B 1D <disp32>`
    // instruction at `mov_offset_in_match` within the match, decode
    // the RIP-relative displacement, and return the address of the
    // global it loads from. Returns nullptr if the pattern is missing
    // or the MOV bytes don't match the expected `48 8B 1D` prefix.
    void* resolve_global_via_rip_load(std::string_view pattern,
                                      std::ptrdiff_t mov_offset_in_match);

private:
    const horizon::inject::PeImage& image_;
};

// Whole-process scan for `_Ref_count_obj2<T>` instances on the heap.
// Walks the user address space via VirtualQuery; for each committed
// MEM_PRIVATE region that's plausibly a heap arena (size in
// [min_region_bytes, 64 MB], PAGE_READWRITE/WRITECOPY/READONLY, no
// PAGE_GUARD, doesn't overlap the module image) scans at 16-byte
// stride looking for slots that satisfy ALL of:
//   *(uint64*)(slot + 0)  == expected_vtable
//   *(uint32*)(slot + 8)   in (0, 0x80]   (refcount `uses`)
//   *(uint32*)(slot + 12)  in (0, 0x80]   (refcount `weaks`)
//   *(void**)(slot + 16)   inside the module range  (embedded T's vtable)
//
// The last three filters are what reject /OPT:ICF false matches:
// random heap bytes that happen to equal the vtable won't have
// plausible refcount values and a module-pointer at +16.
//
// Caps at `max_hits` candidates to bound scan time; with the filters
// applied, real candidates are typically <10.
std::vector<const void*> find_instances_in_heap_arenas(
    const void* expected_vtable,
    const horizon::inject::PeImage& image,
    std::size_t min_region_bytes = 64 * 1024,
    std::size_t max_hits = 64);

} // namespace horizon::game

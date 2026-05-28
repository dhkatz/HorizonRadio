#pragma once

#include <horizon/fmod/types.hpp>
#include <horizon/inject/sigscan.hpp>

namespace horizon::fmod {

// Resolve the game's FMOD `System*` from a live RadioStreamFmod
// instance. Walks `radio_stream + 0x08` to an intermediate object X,
// then `X + 0xC0` to the SystemI pointer.
//
// Verified against the current FH6 build (cross-referenced from
// g0ldyy/fh6-universal-radio's resolve_fmod_system). The endpoint's
// first qword must be a module-range pointer (SystemI's own vtable);
// any other value means we walked into garbage and we return nullptr.
//
// `radio_stream` is the embedded RadioStreamFmod address inside the
// `_Ref_count_obj2<RadioStreamFmod>` allocation. Given a candidate
// slot pointer `slot` (= refcount block), `radio_stream = slot + 0x10`.
//
// All reads are SEH-wrapped: invalid pointers return nullptr instead
// of taking down the calling thread.
System* resolve_fmod_system_from_stream(const horizon::inject::PeImage& image,
                                        const void* radio_stream) noexcept;

} // namespace horizon::fmod

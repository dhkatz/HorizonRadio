#pragma once

#include <horizon/fmod/types.hpp>
#include <horizon/inject/sigscan.hpp>

namespace horizon::fmod {

System* resolve_fmod_system_from_stream(const inject::PeImage& image, const void* radio_stream) noexcept;

} // namespace horizon::fmod

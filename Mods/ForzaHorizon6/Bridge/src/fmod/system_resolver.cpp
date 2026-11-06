#include <horizon/fmod/system_resolver.hpp>
#include <horizon/inject/safe_mem.hpp>

namespace horizon::fmod {

System* resolve_fmod_system_from_stream(const inject::PeImage& image, const void* radio_stream) noexcept {
    if (radio_stream == nullptr)
        return nullptr;
    using inject::safe_read_qword;

    const auto* p = static_cast<const std::byte*>(radio_stream);

    // radio_stream + 0x08 -> X (first non-vtable field).
    const auto x_val = safe_read_qword(p + 0x08);
    if (x_val == 0)
        return nullptr;

    // X + 0xC0 -> SystemI*.
    const auto sys_val = safe_read_qword(reinterpret_cast<const void*>(x_val + 0xC0));
    if (sys_val == 0)
        return nullptr;

    // Sanity-check: the SystemI's first qword should be its own
    // vtable, which lives inside the FH6 module. If it isn't, we
    // walked into garbage -- bail rather than hand a bogus pointer
    // back to createDSP.
    const auto vt     = safe_read_qword(reinterpret_cast<const void*>(sys_val));
    const auto mod_lo = image.base();
    const auto mod_hi = mod_lo + image.image_size();
    if (vt < mod_lo || vt >= mod_hi)
        return nullptr;

    return reinterpret_cast<System*>(sys_val);
}

} // namespace horizon::fmod

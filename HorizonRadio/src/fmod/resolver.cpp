#include <horizon/fmod/resolver.hpp>

namespace horizon::fmod {

namespace {

// Resolve one signature entry to a function pointer. Returns nullptr
// and sets found_out = false if the entry is empty, the anchor isn't
// in .rdata (when anchor is provided), or no enclosing function
// matches the prologue pattern. Returns the function-start pointer
// and sets found_out = true on success.
template <typename Fn> Fn resolve_one(const inject::PeImage& image, const SignaturePattern& sig, bool& found_out) {
    found_out = false;
    if (sig.empty())
        return nullptr;

    const std::byte* hit;
    if (sig.anchor.empty()) {
        hit = horizon::inject::find_function_by_pattern(image, sig.pattern);
    } else {
        hit = horizon::inject::find_function_by_anchor(image, sig.anchor, sig.pattern);
    }
    if (hit == nullptr)
        return nullptr;
    found_out = true;
    return reinterpret_cast<Fn>(const_cast<std::byte*>(hit));
}

} // namespace

FmodResolver::FmodResolver(const inject::PeImage& image, SignatureSet sigs) : image_(image), sigs_(sigs) {}

ResolvedHooks FmodResolver::resolve() {
    ResolvedHooks hooks{};
    hooks.createDsp    = resolve_one<SystemCreateDspFn>(image_, sigs_.createDsp, report_.createDsp);
    hooks.addDsp       = resolve_one<ChannelControlAddDspFn>(image_, sigs_.addDsp, report_.addDsp);
    hooks.removeDsp    = resolve_one<ChannelControlRemoveDspFn>(image_, sigs_.removeDsp, report_.removeDsp);
    hooks.dspRelease   = resolve_one<DspReleaseFn>(image_, sigs_.dspRelease, report_.dspRelease);
    hooks.setMode      = resolve_one<ChannelControlSetModeFn>(image_, sigs_.setMode, report_.setMode);
    hooks.handleOpen   = resolve_one<HandleOpenFn>(image_, sigs_.handleOpen, report_.handleOpen);
    hooks.handleUnlock = resolve_one<HandleUnlockFn>(image_, sigs_.handleUnlock, report_.handleUnlock);
    return hooks;
}

} // namespace horizon::fmod

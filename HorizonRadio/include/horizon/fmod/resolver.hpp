#pragma once

#include <horizon/fmod/types.hpp>
#include <horizon/inject/sigscan.hpp>

#include <string_view>

namespace horizon::fmod {

// One signature entry: optional FMOD anchor string + a pattern
// (alternation via `|`). Empty `pattern` means "skip this slot"; the
// resolver leaves it null and reports `false`.
//
// Two resolution modes:
//
//   Non-empty `anchor`: find the anchor string in .rdata, find every
//     `lea reg, [rip+disp32]` in .text whose target is the anchor,
//     and walk back via .pdata to the enclosing function. Accept the
//     unique function whose prologue matches `pattern`. Robust
//     against prologue variations and reordering of unrelated code.
//
//   Empty `anchor`: scan .text directly for `pattern`, preferring
//     .pdata function starts. Used for leaf functions FMOD doesn't
//     reference by name string (Handle::unlock and friends).
struct SignaturePattern {
    std::string_view anchor{};
    std::string_view pattern{};

    bool empty() const noexcept { return pattern.empty(); }
};

// Bundle of signatures for the FMOD entry points we resolve. Slots
// left default-constructed are skipped; resolve() returns a partial
// ResolvedHooks and the report tells the caller which slots were
// filled.
struct SignatureSet {
    SignaturePattern createDsp;
    SignaturePattern addDsp;
    SignaturePattern removeDsp;
    SignaturePattern dspRelease;
    SignaturePattern setMode;
    SignaturePattern handleOpen;
    SignaturePattern handleUnlock;
};

struct ResolverReport {
    bool createDsp    = false;
    bool addDsp       = false;
    bool removeDsp    = false;
    bool dspRelease   = false;
    bool setMode      = false;
    bool handleOpen   = false;
    bool handleUnlock = false;

    // Required for any DSP install path: addDsp/removeDsp/dspRelease
    // plus handleOpen for live-channel validation. createDsp is
    // intentionally omitted — FMOD lazy-loads its System::createDSP
    // code path on certain FH6 builds, so the LEA we anchor against
    // isn't in .text at DllMain time. We construct the bridge anyway
    // and resolve createDsp on the first install attempt via
    // FmodBridge::set_create_dsp_resolver.
    bool ready() const noexcept {
        return addDsp && removeDsp && dspRelease && handleOpen;
    }
};

// Resolves FMOD function pointers in a loaded module by byte signature.
//
// Game-agnostic: signatures are passed in, not hardcoded. When FH6
// patches and the patterns drift, we update SignatureSet rather than
// touching the resolver itself. The same class works for any future
// game whose FMOD entry points we want to find.
class FmodResolver {
public:
    FmodResolver(const horizon::inject::PeImage& image, SignatureSet sigs);

    // Try to resolve every configured entry point. Returns a partial
    // ResolvedHooks; unresolved slots are nullptr. Call report() to
    // see which slots were filled.
    ResolvedHooks resolve();

    const ResolverReport& report() const noexcept { return report_; }

private:
    const horizon::inject::PeImage& image_;
    SignatureSet   sigs_;
    ResolverReport report_;
};

} // namespace horizon::fmod

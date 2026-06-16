#pragma once

#include <horizon/fmod/types.hpp>
#include <horizon/inject/sigscan.hpp>
#include <string_view>

namespace horizon::fmod {

// One signature entry: optional anchor string + pattern (alternation via `|`).
// Empty `pattern` skips the slot. Non-empty `anchor` => anchored resolution
// (.rdata string -> referencing lea -> .pdata enclosing fn); empty `anchor` =>
// direct .text scan. See docs/architecture.md -> "Signature resolution".
struct SignaturePattern {
    std::string_view anchor;
    std::string_view pattern;

    [[nodiscard]] bool empty() const noexcept {
        return pattern.empty();
    }
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

    // Required for the DSP install path. createDsp is intentionally omitted: FMOD
    // lazy-loads it, so the bridge resolves it on first install instead.
    [[nodiscard]] bool ready() const noexcept {
        return addDsp && removeDsp && dspRelease && handleOpen;
    }
};

// Resolves FMOD function pointers in a loaded module by byte signature.
// Game-agnostic: signatures are passed in (update SignatureSet on drift, not
// this class).
class FmodResolver {
public:
    FmodResolver(const inject::PeImage& image, SignatureSet sigs);

    // Try to resolve every configured entry point. Returns a partial
    // ResolvedHooks; unresolved slots are nullptr. Call report() to
    // see which slots were filled.
    ResolvedHooks resolve();

    [[nodiscard]] const ResolverReport& report() const noexcept {
        return report_;
    }

private:
    const inject::PeImage& image_;
    SignatureSet           sigs_;
    ResolverReport         report_;
};

} // namespace horizon::fmod

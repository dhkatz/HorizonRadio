#pragma once

#include <cstddef>
#include <cstdint>

// Minimum subset of the FMOD Studio API ABI we need to interact with the
// game's already-loaded FMOD library.
//
// We don't link against FMOD: the game's process has FMOD mapped, and we
// invoke its createDSP / addDSP / removeDSP / release entry points via
// raw function pointers resolved by sigscan. The structs and enums below
// MUST match FMOD's wire layout exactly -- changing field order, type,
// or alignment will corrupt the game.
//
// This header is intentionally minimal: only the types we actually pass
// across the ABI boundary. Declaring the full ~150 FMOD callback typedefs
// just to leave them all nullptr would just be noise -- void* in the
// DspDescription slots we don't use is honest about that.
//
// Reference: FMOD Studio API 2.02 fmod_dsp.h and fmod_common.h.

namespace horizon::fmod {

// All opaque -- pointer-only, never dereferenced from our side.
struct System;
struct Dsp;
struct ChannelControl;
struct DspState;

// FMOD_RESULT. Zero is FMOD_OK; any non-zero value is an error code and
// our callers log+abort the install. We don't enumerate every code -- the
// caller doesn't branch on the specific failure mode.
enum class Result : int {
    Ok = 0,
};

// DSP plugin SDK version. 110 matches FMOD 2.02; older 2.0x builds used
// 100. If a future FH6 patch ships a different FMOD build we'll need to
// match whatever it expects.
inline constexpr unsigned int kPluginSdkVersion = 110;

// DSP read callback -- called from the FMOD mixer thread.
//
//   state         opaque; we don't use it for source-only DSPs
//   in_buffer     ignored for source DSPs (we generate, not transform)
//   out_buffer    we write `length * *outchannels` floats here
//   length        frame count (samples per channel)
//   inchannels    number of input channels (0 for a source DSP)
//   outchannels   we read the expected output channel count and may
//                 also write back what we actually filled
using DspReadFn = Result (*)(DspState* state,
                             float*    in_buffer,
                             float*    out_buffer,
                             unsigned int length,
                             int       inchannels,
                             int*      outchannels);

// FMOD_DSP_DESCRIPTION. Layout MUST match fmod_dsp.h exactly. Fields we
// don't use are typed as void* to avoid declaring 20+ callback typedefs
// for slots we leave nullptr.
struct DspDescription {
    unsigned int pluginsdkversion;
    char         name[32];
    unsigned int version;
    int          numinputbuffers;
    int          numoutputbuffers;

    void*        create;             // FMOD_DSP_CREATE_CALLBACK
    void*        release;            // FMOD_DSP_RELEASE_CALLBACK
    void*        reset;              // FMOD_DSP_RESET_CALLBACK
    DspReadFn    read;
    void*        process;            // FMOD_DSP_PROCESS_CALLBACK (newer-style alternative to read)
    void*        setposition;        // FMOD_DSP_SETPOSITION_CALLBACK

    int          numparameters;
    void*        paramdesc;          // FMOD_DSP_PARAMETER_DESC**

    void*        setparameterfloat;
    void*        setparameterint;
    void*        setparameterbool;
    void*        setparameterdata;
    void*        getparameterfloat;
    void*        getparameterint;
    void*        getparameterbool;
    void*        getparameterdata;
    void*        shouldiprocess;

    void*        userdata;

    void*        sys_register;
    void*        sys_deregister;
    void*        sys_mix;
};

// Sanity: catch obvious ABI drift at compile time. Offsets calculated
// for x64 default packing (16-byte alignment of pointers within an
// aggregate is not required; 8-byte is). If a future FMOD adds fields,
// these fire and we update with intent rather than silently sliding.
static_assert(sizeof(unsigned int)             == 4,   "FMOD ABI expects 32-bit int");
static_assert(sizeof(void*)                    == 8,   "x64 only");
static_assert(offsetof(DspDescription, name)   == 4,   "DspDescription.name offset");
static_assert(offsetof(DspDescription, version)== 36,  "DspDescription.version offset");
static_assert(offsetof(DspDescription, create) == 48,  "DspDescription.create offset");
static_assert(offsetof(DspDescription, read)   == 72,  "DspDescription.read offset");
static_assert(sizeof(DspDescription)           == 216, "DspDescription total size");

// Resolved game function pointers. The FmodBridge constructor takes a
// struct of these so the sigscan layer doesn't leak into the bridge API.
//
// Channel control ABI note: FMOD's public API takes a `ChannelControl*`,
// but at the ABI level it's a 32-bit packed handle zero-extended to 64
// bits. We expose the parameter as `ChannelControl*` because that's
// FMOD's public type; call sites cast `(ChannelControl*)(uint64_t)handle`.
using SystemCreateDspFn          = Result (*)(System* system,
                                              const DspDescription* description,
                                              Dsp** dsp_out);
using ChannelControlAddDspFn     = Result (*)(ChannelControl* control,
                                              int index,
                                              Dsp* dsp);
using ChannelControlRemoveDspFn  = Result (*)(ChannelControl* control,
                                              Dsp* dsp);
using DspReleaseFn               = Result (*)(Dsp* dsp);

// FMOD_MODE bit field. We only ever set FMOD_LOOP_NORMAL = 0x2 on the
// radio channel so the placeholder sample doesn't end and tear the
// channel down underneath our DSP.
using ChannelControlSetModeFn    = Result (*)(ChannelControl* control,
                                              std::uint32_t mode);

// FMOD's internal Handle::open and Handle::unlock. Used to validate
// that a 32-bit channel handle is still resolvable; FMOD writes the
// active channel handle into RadioStreamFmod+0x20 and clears the slot
// when the channel is destroyed, but a brief race window exists
// during channel teardown where the slot is stale -- Handle::open
// fails on those.
//
// IMPORTANT: every successful Handle::open must be paired with
// Handle::unlock(lock_state). Skipping the unlock leaks a slot in
// FMOD's resolver table; after enough leaks the game thread freezes
// contending on the table lock.
using HandleOpenFn   = std::uint32_t (*)(std::uint32_t handle,
                                         void** out_inst,
                                         std::uint64_t* out_lock_state);
using HandleUnlockFn = std::uint32_t (*)(std::uint64_t lock_state);

// FMOD_LOOP_NORMAL — pin the channel in continuous-loop mode so it
// doesn't get torn down when the underlying sample reaches its end.
inline constexpr std::uint32_t kFmodLoopNormal = 0x2;

// SDK-version stamps to try when calling createDSP. FMOD shipped these
// across the 1.x line; createDSP rejects mismatches with a non-zero
// FMOD_RESULT. Try each in order and accept the first that succeeds.
inline constexpr std::uint32_t kFmodPluginSdkVersions[] = {
    0x00011000, 0x00011003, 0x00010000
};

// Bundle of resolved entry points. Produced by the resolver, consumed
// by FmodBridge. Lives in types.hpp (not bridge.hpp) so the resolver
// can produce one without depending on the bridge.
//
// Required for any DSP install:  createDsp, addDsp, removeDsp, dspRelease,
//                                 handleOpen
// Best-effort (install proceeds without them):
//   setMode       -- without it, FMOD may tear down the radio channel
//                    when the placeholder sample ends; user-visible
//                    failure mode is "audio cuts after ~2 min."
//   handleUnlock  -- without it, handle validation leaks slots; the
//                    game thread freezes after enough invocations.
struct ResolvedHooks {
    SystemCreateDspFn          createDsp     = nullptr;
    ChannelControlAddDspFn     addDsp        = nullptr;
    ChannelControlRemoveDspFn  removeDsp     = nullptr;
    DspReleaseFn               dspRelease    = nullptr;
    ChannelControlSetModeFn    setMode       = nullptr;
    HandleOpenFn               handleOpen    = nullptr;
    HandleUnlockFn             handleUnlock  = nullptr;
};

} // namespace horizon::fmod

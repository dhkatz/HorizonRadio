#pragma once

#include <cstddef>
#include <cstdint>

namespace horizon::fmod {

struct System;
struct Dsp;
struct ChannelControl;
struct DspState;

enum class Result : int {
    Ok = 0,
};

inline constexpr unsigned int kPluginSdkVersion = 110;

using DspReadFn = Result (*)(DspState* state, float* in_buffer, float* out_buffer, unsigned int length, int inchannels,
                             int* outchannels);

struct DspDescription {
    unsigned int pluginsdkversion;
    char         name[32];
    unsigned int version;
    int          numinputbuffers;
    int          numoutputbuffers;

    void*     create;  // FMOD_DSP_CREATE_CALLBACK
    void*     release; // FMOD_DSP_RELEASE_CALLBACK
    void*     reset;   // FMOD_DSP_RESET_CALLBACK
    DspReadFn read;
    void*     process;     // FMOD_DSP_PROCESS_CALLBACK (newer-style alternative to read)
    void*     setposition; // FMOD_DSP_SETPOSITION_CALLBACK

    int   numparameters;
    void* paramdesc; // FMOD_DSP_PARAMETER_DESC**

    void* setparameterfloat;
    void* setparameterint;
    void* setparameterbool;
    void* setparameterdata;
    void* getparameterfloat;
    void* getparameterint;
    void* getparameterbool;
    void* getparameterdata;
    void* shouldiprocess;

    void* userdata;

    void* sys_register;
    void* sys_deregister;
    void* sys_mix;
};

// A macro is the only portable way to compute a member offset as a constant
// expression here; a constexpr template can't replace __builtin_offsetof.
// NOLINTBEGIN(cppcoreguidelines-macro-usage)
#if defined(__clang__) || defined(__GNUC__)
#define HORIZON_OFFSETOF(type, member) __builtin_offsetof(type, member)
#else
#define HORIZON_OFFSETOF(type, member) offsetof(type, member)
#endif
// NOLINTEND(cppcoreguidelines-macro-usage)

static_assert(sizeof(unsigned int) == 4, "FMOD ABI expects 32-bit int");
static_assert(sizeof(void*) == 8, "x64 only");
static_assert(HORIZON_OFFSETOF(DspDescription, name) == 4, "DspDescription.name offset");
static_assert(HORIZON_OFFSETOF(DspDescription, version) == 36, "DspDescription.version offset");
static_assert(HORIZON_OFFSETOF(DspDescription, create) == 48, "DspDescription.create offset");
static_assert(HORIZON_OFFSETOF(DspDescription, read) == 72, "DspDescription.read offset");
static_assert(sizeof(DspDescription) == 216, "DspDescription total size");

#undef HORIZON_OFFSETOF

using SystemCreateDspFn         = Result (*)(System* system, const DspDescription* description, Dsp** dsp_out);
using ChannelControlAddDspFn    = Result (*)(ChannelControl* control, int index, Dsp* dsp);
using ChannelControlRemoveDspFn = Result (*)(ChannelControl* control, Dsp* dsp);
using DspReleaseFn              = Result (*)(Dsp* dsp);

using ChannelControlSetModeFn = Result (*)(ChannelControl* control, std::uint32_t mode);

using HandleOpenFn   = std::uint32_t (*)(std::uint32_t handle, void** out_inst, std::uint64_t* out_lock_state);
using HandleUnlockFn = std::uint32_t (*)(std::uint64_t lock_state);

inline constexpr std::uint32_t kFmodLoopNormal = 0x2;

inline constexpr std::uint32_t kFmodPluginSdkVersions[] = {0x00011000, 0x00011003, 0x00010000};

struct ResolvedHooks {
    SystemCreateDspFn         createDsp    = nullptr;
    ChannelControlAddDspFn    addDsp       = nullptr;
    ChannelControlRemoveDspFn removeDsp    = nullptr;
    DspReleaseFn              dspRelease   = nullptr;
    ChannelControlSetModeFn   setMode      = nullptr;
    HandleOpenFn              handleOpen   = nullptr;
    HandleUnlockFn            handleUnlock = nullptr;
};

} // namespace horizon::fmod

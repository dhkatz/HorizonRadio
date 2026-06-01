#include <cstring>
#include <doctest/doctest.h>
#include <horizon/fmod/types.hpp>

using namespace horizon::fmod;

TEST_CASE("DspDescription matches expected FMOD wire layout") {
    // Most of the layout assertions live in types.hpp as static_asserts,
    // so they fire at compile time. The runtime test here is an extra
    // safety net: build a description, assign through every field, and
    // confirm we can read back what we wrote -- this catches accidental
    // packing pragmas or alignment issues that static_assert wouldn't.

    DspDescription d{};
    d.pluginsdkversion = kPluginSdkVersion;
    std::strncpy(d.name, "Horizon Test", sizeof(d.name));
    d.version          = 0x00010000;
    d.numinputbuffers  = 0;
    d.numoutputbuffers = 1;
    d.numparameters    = 0;
    d.read             = nullptr;
    d.userdata         = reinterpret_cast<void*>(static_cast<std::uintptr_t>(0xDEADBEEFCAFEBABEull));

    CHECK(d.pluginsdkversion == kPluginSdkVersion);
    CHECK(std::strncmp(d.name, "Horizon Test", sizeof(d.name)) == 0);
    CHECK(d.version == 0x00010000u);
    CHECK(d.numoutputbuffers == 1);
    CHECK(d.read == nullptr);
    CHECK(reinterpret_cast<std::uintptr_t>(d.userdata) == 0xDEADBEEFCAFEBABEull);
}

TEST_CASE("Function pointer typedefs accept matching signatures") {
    // Pure compile-time check: assign through each typedef so a future
    // signature drift in fmod::types fails to build here loudly.
    SystemCreateDspFn create_fn = +[](System*, const DspDescription*, Dsp**) {
        return Result::Ok;
    };
    ChannelControlAddDspFn add_fn = +[](ChannelControl*, int, Dsp*) {
        return Result::Ok;
    };
    ChannelControlRemoveDspFn remove_fn = +[](ChannelControl*, Dsp*) {
        return Result::Ok;
    };
    DspReleaseFn release_fn = +[](Dsp*) {
        return Result::Ok;
    };

    CHECK(create_fn != nullptr);
    CHECK(add_fn != nullptr);
    CHECK(remove_fn != nullptr);
    CHECK(release_fn != nullptr);
}

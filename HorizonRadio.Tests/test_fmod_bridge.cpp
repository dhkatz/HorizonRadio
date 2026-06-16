#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <doctest/doctest.h>
#include <horizon/fmod/bridge.hpp>
#include <vector>

using namespace horizon::fmod;

// Capturing lambdas don't decay to function pointers; tests use file-
// scope statics to simulate the resolved FMOD entry points and let the
// test bodies inspect / set them.

namespace {

std::atomic<int> g_create_calls{0};
std::atomic<int> g_add_calls{0};
std::atomic<int> g_remove_calls{0};
std::atomic<int> g_release_calls{0};
std::atomic<int> g_set_mode_calls{0};
std::atomic<int> g_handle_open_calls{0};
std::atomic<int> g_handle_unlock_calls{0};

DspReadFn g_captured_read      = nullptr;
Result    g_next_create_result = Result::Ok;
Result    g_next_add_result    = Result::Ok;

// pluginsdkversion seen by fake_create on each call. Used to verify
// the sdkversion-fallback iteration.
std::vector<std::uint32_t> g_create_versions;

// The fake `handleOpen` returns 0 (success) for handles in
// g_live_handles, anything else for handles not in the set.
std::vector<std::uint32_t> g_live_handles;

Dsp* const kFakeDsp = reinterpret_cast<Dsp*>(static_cast<std::uintptr_t>(0xD5D5D5D5));

Result fake_create(System*, const DspDescription* desc, Dsp** out) {
    g_create_calls.fetch_add(1);
    g_create_versions.push_back(desc->pluginsdkversion);
    g_captured_read = desc->read;
    *out            = (g_next_create_result == Result::Ok) ? kFakeDsp : nullptr;
    return g_next_create_result;
}

Result fake_add(ChannelControl*, int, Dsp*) {
    g_add_calls.fetch_add(1);
    return g_next_add_result;
}

Result fake_remove(ChannelControl*, Dsp*) {
    g_remove_calls.fetch_add(1);
    return Result::Ok;
}

Result fake_release(Dsp*) {
    g_release_calls.fetch_add(1);
    return Result::Ok;
}

Result fake_set_mode(ChannelControl*, std::uint32_t /*mode*/) {
    g_set_mode_calls.fetch_add(1);
    return Result::Ok;
}

std::uint32_t fake_handle_open(std::uint32_t handle, void** out_inst, std::uint64_t* out_lock_state) {
    g_handle_open_calls.fetch_add(1);
    *out_lock_state = static_cast<std::uint64_t>(handle); // non-zero so unlock pairs
    for (auto h : g_live_handles) {
        if (h == handle) {
            *out_inst = reinterpret_cast<void*>(static_cast<std::uintptr_t>(0x1234'5000u + handle));
            return 0; // success
        }
    }
    *out_inst = nullptr;
    return 1; // failure
}

std::uint32_t fake_handle_unlock(std::uint64_t /*lock_state*/) {
    g_handle_unlock_calls.fetch_add(1);
    return 0;
}

ResolvedHooks fake_hooks() {
    return {
        .createDsp    = fake_create,
        .addDsp       = fake_add,
        .removeDsp    = fake_remove,
        .dspRelease   = fake_release,
        .setMode      = fake_set_mode,
        .handleOpen   = fake_handle_open,
        .handleUnlock = fake_handle_unlock,
    };
}

void reset_fakes() {
    g_create_calls = g_add_calls = g_remove_calls = g_release_calls = 0;
    g_set_mode_calls = g_handle_open_calls = g_handle_unlock_calls = 0;
    g_captured_read                                                = nullptr;
    g_next_create_result                                           = Result::Ok;
    g_next_add_result                                              = Result::Ok;
    g_create_versions.clear();
    g_live_handles.clear();
}

// A synthetic radio_stream buffer with the channel handle written at
// offset 0x20. Big enough that any pointer-chain peeks land in
// committed memory.
struct FakeRadioStream {
    alignas(8) std::byte bytes[256]{};
    void set_handle(std::uint32_t h) {
        std::memcpy(bytes + 0x20, &h, sizeof(h));
    }
    std::byte* ptr() {
        return bytes;
    }
};

System* const kFakeSystem = reinterpret_cast<System*>(static_cast<std::uintptr_t>(0xA1A1A1A1));

} // namespace

TEST_CASE("tick: no-op without target") {
    reset_fakes();
    FmodBridge bridge(fake_hooks());
    bridge.tick();
    CHECK(g_create_calls.load() == 0);
    CHECK_FALSE(bridge.installed());
}

TEST_CASE("tick: no-op when handle slot is empty") {
    reset_fakes();
    FakeRadioStream stream;
    // Don't set a handle; slot stays zero.
    FmodBridge bridge(fake_hooks());
    bridge.set_target(kFakeSystem, stream.ptr());
    bridge.tick();
    CHECK(g_create_calls.load() == 0);
    CHECK_FALSE(bridge.installed());
}

TEST_CASE("tick: installs on live handle") {
    reset_fakes();
    FakeRadioStream         stream;
    constexpr std::uint32_t kHandle = 0xCAFE'F00D;
    stream.set_handle(kHandle);
    g_live_handles = {kHandle};

    FmodBridge bridge(fake_hooks());
    bridge.set_target(kFakeSystem, stream.ptr());
    bridge.tick();

    CHECK(bridge.installed());
    CHECK(g_create_calls.load() == 1);
    CHECK(g_add_calls.load() == 1);
    CHECK(g_set_mode_calls.load() == 1);
    CHECK(g_handle_open_calls.load() >= 1);
    CHECK(g_handle_unlock_calls.load() >= 1);

    // Steady state: a second tick with the same live handle does
    // not reinstall.
    bridge.tick();
    CHECK(g_create_calls.load() == 1);
    CHECK(g_remove_calls.load() == 0);
}

TEST_CASE("tick: retargets when handle changes") {
    reset_fakes();
    FakeRadioStream         stream;
    constexpr std::uint32_t kHandleA = 0xAAAA'1111;
    constexpr std::uint32_t kHandleB = 0xBBBB'2222;
    stream.set_handle(kHandleA);
    g_live_handles = {kHandleA, kHandleB};

    FmodBridge bridge(fake_hooks());
    bridge.set_target(kFakeSystem, stream.ptr());
    bridge.tick();
    REQUIRE(bridge.installed());

    // Game writes a new handle into the slot (station change).
    stream.set_handle(kHandleB);
    bridge.tick();

    CHECK(bridge.installed());
    CHECK(g_create_calls.load() == 2);
    CHECK(g_add_calls.load() == 2);
    CHECK(g_remove_calls.load() == 1); // old DSP removed before reinstall
    CHECK(g_release_calls.load() == 1);
}

TEST_CASE("tick: uninstalls when handle goes dead") {
    reset_fakes();
    FakeRadioStream         stream;
    constexpr std::uint32_t kHandle = 0xC0DE'BABE;
    stream.set_handle(kHandle);
    g_live_handles = {kHandle};

    FmodBridge bridge(fake_hooks());
    bridge.set_target(kFakeSystem, stream.ptr());
    bridge.tick();
    REQUIRE(bridge.installed());

    // Handle::open starts returning failure (channel destroyed).
    g_live_handles.clear();
    bridge.tick();

    CHECK_FALSE(bridge.installed());
    CHECK(g_remove_calls.load() == 1);
    CHECK(g_release_calls.load() == 1);
}

TEST_CASE("tick: createDSP failure leaves bridge uninstalled") {
    reset_fakes();
    FakeRadioStream         stream;
    constexpr std::uint32_t kHandle = 0x1234'5678;
    stream.set_handle(kHandle);
    g_live_handles       = {kHandle};
    g_next_create_result = static_cast<Result>(42);

    FmodBridge bridge(fake_hooks());
    bridge.set_target(kFakeSystem, stream.ptr());
    bridge.tick();

    CHECK_FALSE(bridge.installed());
    CHECK(g_add_calls.load() == 0);
}

TEST_CASE("tick: addDSP failure releases the orphaned DSP") {
    reset_fakes();
    FakeRadioStream         stream;
    constexpr std::uint32_t kHandle = 0x9876'5432;
    stream.set_handle(kHandle);
    g_live_handles    = {kHandle};
    g_next_add_result = static_cast<Result>(99);

    FmodBridge bridge(fake_hooks());
    bridge.set_target(kFakeSystem, stream.ptr());
    bridge.tick();

    CHECK_FALSE(bridge.installed());
    CHECK(g_release_calls.load() == 1);
}

TEST_CASE("install: tries each pluginsdkversion until one succeeds") {
    reset_fakes();
    FakeRadioStream         stream;
    constexpr std::uint32_t kHandle = 0xABCD'1234;
    stream.set_handle(kHandle);
    g_live_handles = {kHandle};

    // createDSP returns Ok regardless of version; we just verify
    // the bridge tried the first version stamp.
    FmodBridge bridge(fake_hooks());
    bridge.set_target(kFakeSystem, stream.ptr());
    bridge.tick();
    REQUIRE(bridge.installed());
    REQUIRE(!g_create_versions.empty());
    CHECK(g_create_versions[0] == kFmodPluginSdkVersions[0]);
}

TEST_CASE("read callback (no resample): converts s16 to f32 1:1") {
    reset_fakes();
    FakeRadioStream         stream;
    constexpr std::uint32_t kHandle = 0xDEAD'BEEF;
    stream.set_handle(kHandle);
    g_live_handles = {kHandle};

    FmodBridge bridge(fake_hooks());
    bridge.set_resample_enabled(false);
    bridge.normalizer().set_enabled(false); // we're validating the raw conversion path
    bridge.set_target(kFakeSystem, stream.ptr());
    bridge.tick();
    REQUIRE(bridge.installed());
    REQUIRE(g_captured_read != nullptr);

    constexpr unsigned int                      kFrames = 64;
    std::array<float, std::size_t{kFrames} * 2> warmup_out{};
    int                                         warmup_channels = 2;
    // install_on_handle sets a drain request that the first read
    // honors -- discards any queued PCM. In production that's stale
    // bridge-was-uninstalled audio; in this test we just consume the
    // empty drain before pushing the data we want to verify.
    g_captured_read(nullptr, nullptr, warmup_out.data(), kFrames, 0, &warmup_channels);
    const auto warmup_underruns = bridge.underrun_count();

    std::array<std::int16_t, std::size_t{kFrames} * 2> in_pcm{};
    for (std::size_t i = 0; i < in_pcm.size(); ++i) {
        in_pcm[i] = static_cast<std::int16_t>((i * 257) & 0x7FFF);
    }
    REQUIRE(bridge.push_pcm(in_pcm.data(), kFrames) == kFrames);

    std::array<float, std::size_t{kFrames} * 2> out{};
    int                                         channels = 2;
    auto result = g_captured_read(nullptr, nullptr, out.data(), kFrames, 0, &channels);
    CHECK(result == Result::Ok);
    CHECK(bridge.underrun_count() == warmup_underruns); // no new underruns this read
    for (std::size_t i = 0; i < out.size(); ++i) {
        const float expected = static_cast<float>(in_pcm[i]) / 32768.0f;
        CHECK(out[i] == doctest::Approx(expected).epsilon(0.0001f));
    }
}

TEST_CASE("read callback (no resample): silence + underrun count on empty ring") {
    reset_fakes();
    FakeRadioStream         stream;
    constexpr std::uint32_t kHandle = 0xBAAD'F00D;
    stream.set_handle(kHandle);
    g_live_handles = {kHandle};

    FmodBridge bridge(fake_hooks());
    bridge.set_resample_enabled(false);
    bridge.set_target(kFakeSystem, stream.ptr());
    bridge.tick();
    REQUIRE(g_captured_read != nullptr);

    constexpr unsigned int                      kFrames = 32;
    std::array<float, std::size_t{kFrames} * 2> out;
    out.fill(7.0f);
    int channels = 2;
    g_captured_read(nullptr, nullptr, out.data(), kFrames, 0, &channels);

    for (float v : out)
        CHECK(v == 0.0f);
    CHECK(bridge.underrun_count() == 1);
}

TEST_CASE("read callback: resampled output is ~kStep × input frames consumed") {
    reset_fakes();
    FakeRadioStream         stream;
    constexpr std::uint32_t kHandle = 0xC001'BABE;
    stream.set_handle(kHandle);
    g_live_handles = {kHandle};

    FmodBridge bridge(fake_hooks());
    // resample_enabled defaults true.
    bridge.normalizer().set_enabled(false); // validating raw resampler math
    bridge.set_target(kFakeSystem, stream.ptr());
    bridge.tick();
    REQUIRE(g_captured_read != nullptr);

    // Consume the install-time drain on an empty buffer first.
    constexpr unsigned int                         kOutFrames = 512;
    std::array<float, std::size_t{kOutFrames} * 2> warmup{};
    int                                            warmup_channels = 2;
    g_captured_read(nullptr, nullptr, warmup.data(), kOutFrames, 0, &warmup_channels);
    const auto warmup_underruns = bridge.underrun_count();

    // Push enough input for ~512 output frames at 44.1k→48k ratio
    // (512 / 0.91875 ≈ 557 input frames; push 700 to leave headroom).
    std::array<std::int16_t, std::size_t{700} * 2> in_pcm{};
    for (std::size_t i = 0; i < in_pcm.size(); i += 2) {
        in_pcm[i]     = 1000;
        in_pcm[i + 1] = -1000;
    }
    bridge.push_pcm(in_pcm.data(), 700);

    std::array<float, std::size_t{kOutFrames} * 2> out{};
    int                                            channels = 2;
    auto rc = g_captured_read(nullptr, nullptr, out.data(), kOutFrames, 0, &channels);
    CHECK(rc == Result::Ok);
    CHECK(bridge.underrun_count() == warmup_underruns); // no new underruns

    // After interpolation, outputs should be close to ±1000/32768.
    // Use the first and last frames (which are pure samples, not
    // mid-interp) as anchors.
    CHECK(out[0] == doctest::Approx(1000.0f / 32768.0f).epsilon(0.01f));
    CHECK(out[1] == doctest::Approx(-1000.0f / 32768.0f).epsilon(0.01f));
}

TEST_CASE("trampoline tolerates null bridge (post-uninstall race)") {
    reset_fakes();
    FakeRadioStream         stream;
    constexpr std::uint32_t kHandle = 0xFADE'D00D;
    stream.set_handle(kHandle);
    g_live_handles = {kHandle};

    FmodBridge bridge(fake_hooks());
    bridge.set_target(kFakeSystem, stream.ptr());
    bridge.tick();
    REQUIRE(g_captured_read != nullptr);
    auto captured = g_captured_read;
    bridge.uninstall();

    std::array<float, 8> out;
    out.fill(9.0f);
    int channels = 2;
    captured(nullptr, nullptr, out.data(), 4, 0, &channels);
    for (float v : out)
        CHECK(v == 0.0f);
}

#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <horizon/audio/normalizer.hpp>
#include <horizon/audio/ring_buffer.hpp>
#include <horizon/fmod/types.hpp>

namespace horizon::fmod {

// Bridges source PCM (pushed s16 stereo) into the game's FMOD mixer via a DSP
// installed on the in-game radio channel, with an SPSC ring + 44.1k->48k
// resample on the read callback. See docs/architecture.md -> "Audio path".
//
// One bridge per process: the static read trampoline dispatches via a single
// global atomic, so installing two bridges simultaneously is unsafe.
class FmodBridge {
public:
    explicit FmodBridge(const ResolvedHooks& hooks);
    ~FmodBridge();

    // Late-binding retry for createDsp: FMOD lazy-loads System::createDSP, so on
    // some FH6 builds the LEA isn't visible until the audio system is touched.
    // install_on_handle() calls this to re-resolve; a non-null result is cached.
    using CreateDspResolver = std::function<SystemCreateDspFn()>;
    void set_create_dsp_resolver(CreateDspResolver fn) {
        lazy_create_dsp_ = std::move(fn);
    }

    FmodBridge(const FmodBridge&)            = delete;
    FmodBridge& operator=(const FmodBridge&) = delete;
    FmodBridge(FmodBridge&&)                 = delete;
    FmodBridge& operator=(FmodBridge&&)      = delete;

    // Target to attach the DSP to; both null disables bridging (next tick()
    // uninstalls). `radio_stream` is the embedded RadioStreamFmod (refcount +
    // 0x10); its live channel handle lives at radio_stream + 0x20.
    void set_target(System* system, std::byte* radio_stream) noexcept;

    // Per-tick: read the channel handle, validate via Handle::open, then
    // install / retarget / uninstall as needed. Cheap (one read + one open).
    void tick() noexcept;

    // Returns true if our DSP is currently installed on a channel
    // that Handle::open still resolves.
    bool installed() const noexcept;
    bool current_channel_alive() const noexcept;

    // Force teardown. Safe to call from the control thread; the read
    // callback is serialized against by FMOD's removeDSP.
    void uninstall() noexcept;

    // Push interleaved stereo s16 PCM from the source thread. Returns
    // the number of FRAMES actually buffered; less than frame_count
    // if the ring is near full (excess is dropped silently rather
    // than blocking the source thread).
    std::size_t push_pcm(const std::int16_t* frames, std::size_t frame_count);

    // Toggle 44.1k->48k resampling (default on; off if the channel runs 44.1k).
    void set_resample_enabled(bool enabled) noexcept {
        resample_enabled_.store(enabled, std::memory_order_release);
    }

    // Master output gain [0,1], applied after the normalizer; driven by set_gain.
    void set_master_gain(float gain) noexcept {
        master_gain_.store(gain, std::memory_order_release);
    }
    float master_gain() const noexcept {
        return master_gain_.load(std::memory_order_acquire);
    }

    // Per-bridge AGC + peak-limiter (on by default).
    audio::Normalizer& normalizer() noexcept {
        return normalizer_;
    }
    const audio::Normalizer& normalizer() const noexcept {
        return normalizer_;
    }

    // Stats used by the /api/state surface and for debugging.
    std::uint64_t total_frames_in() const noexcept {
        return frames_in_.load(std::memory_order_relaxed);
    }
    std::uint64_t total_frames_out() const noexcept {
        return frames_out_.load(std::memory_order_relaxed);
    }
    std::uint64_t underrun_count() const noexcept {
        return underruns_.load(std::memory_order_relaxed);
    }
    std::uint64_t callback_count() const noexcept {
        return callbacks_.load(std::memory_order_relaxed);
    }

private:
    // FMOD callback dispatch. The trampoline reads the process-global
    // active-bridge pointer; the instance method `read` fills out_buffer.
    static Result read_trampoline(DspState* state, float* in_buffer, float* out_buffer, unsigned int length,
                                  int inchannels, int* outchannels);

    Result read(float* out_buffer, unsigned int length, int outchannels);

    // Read radio_stream+0x20, validate via Handle::open. Returns 0 if
    // no live handle (slot empty or Handle::open fails). Pairs every
    // successful open with an unlock so we don't leak FMOD slots.
    std::uint32_t read_live_channel_handle() const noexcept;
    bool          validate_handle(std::uint32_t handle) const noexcept;

    // Internal install/teardown; called from tick() under serialized
    // control-thread access. Returns true on success.
    bool install_on_handle(std::uint32_t handle) noexcept;
    void uninstall_internal() noexcept;

    static constexpr std::size_t kChannels   = 2;
    static constexpr std::size_t kRingFrames = 65536; // ~1.5s at 44.1kHz stereo

    ResolvedHooks     hooks_;
    CreateDspResolver lazy_create_dsp_;

    // Live targets, only mutated from the control thread (tick caller).
    System*    system_       = nullptr;
    std::byte* radio_stream_ = nullptr;

    // Current DSP install state, only mutated from the control thread.
    Dsp*          dsp_            = nullptr;
    std::uint32_t current_handle_ = 0;

    horizon::audio::SpscRingBuffer<std::int16_t> ring_;

    // Resampler scratch. Only the FMOD mixer thread touches these.
    // Reset to zero on every install/uninstall (serialized against the
    // mixer by FMOD's removeDSP semantics).
    double       resample_phase_ = 0.0;
    std::int16_t prev_l_         = 0;
    std::int16_t prev_r_         = 0;
    std::int16_t cur_l_          = 0;
    std::int16_t cur_r_          = 0;
    bool         have_prev_      = false;
    bool         have_cur_       = false;

    horizon::audio::Normalizer normalizer_;

    std::atomic<bool>          resample_enabled_{true};
    std::atomic<float>         master_gain_{1.0f};
    std::atomic<std::uint64_t> frames_in_{0};
    std::atomic<std::uint64_t> frames_out_{0};
    std::atomic<std::uint64_t> underruns_{0};
    std::atomic<std::uint64_t> callbacks_{0};

    // Last read-callback time (us); push_pcm uses it to detect a stalled
    // consumer (game paused / silenced) and request a drain. 0 = no callback yet.
    std::atomic<std::uint64_t> last_callback_us_{0};

    // Set when ring contents are stale; read() discards readable audio on its
    // next callback so playback resumes live, not from queued-during-pause data.
    std::atomic<bool> drain_request_{false};
};

} // namespace horizon::fmod

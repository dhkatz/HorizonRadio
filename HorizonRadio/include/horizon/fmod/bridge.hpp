#pragma once

#include <horizon/audio/normalizer.hpp>
#include <horizon/audio/ring_buffer.hpp>
#include <horizon/fmod/types.hpp>

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>

namespace horizon::fmod {

// Bridges audio source PCM into the game's FMOD mixer.
//
// The active audio source pushes s16 interleaved stereo via push_pcm()
// from its own thread. We hold the PCM in an internal SPSC ring buffer
// and serve it to FMOD's mixer thread via a DSP we install on the
// in-game radio channel. The DSP's read callback pulls from the ring,
// converts s16 → f32, and linear-interpolates 44.1k → 48k.
//
// Wiring lifecycle:
//
//   1. Construct: resolved FMOD entry points + image (for system
//      resolution + module-range checks).
//   2. set_target(system, radio_stream): once metadata discovery
//      finds a chain-valid RadioStreamFmod instance, hand it here.
//      Subsequent tick() calls will install the DSP on the channel
//      handle stored at radio_stream + 0x20.
//   3. tick(): called at ~50 Hz by the control loop. Reads the
//      current channel handle, validates via Handle::open, and
//      installs / retargets / tears down the DSP as needed.
//   4. uninstall(): manual teardown (also called from the destructor).
//
// One bridge per process: the static read trampoline dispatches via
// a single global atomic, set before addDSP and cleared after
// removeDSP. Constructing two bridges and installing both
// simultaneously is unsafe.
class FmodBridge {
public:
    explicit FmodBridge(ResolvedHooks hooks);
    ~FmodBridge();

    // Late-binding hook: install_on_handle() calls this when createDsp
    // wasn't resolved at construction time, to retry the scan against
    // the live module. FMOD lazy-loads its System::createDSP path; on
    // certain FH6 builds the LEA isn't visible until the audio system
    // has actually been touched. Returning a non-null pointer commits
    // it as hooks_.createDsp for the rest of the bridge lifetime.
    using CreateDspResolver = std::function<horizon::fmod::SystemCreateDspFn()>;
    void set_create_dsp_resolver(CreateDspResolver fn) { lazy_create_dsp_ = std::move(fn); }

    FmodBridge(const FmodBridge&)            = delete;
    FmodBridge& operator=(const FmodBridge&) = delete;
    FmodBridge(FmodBridge&&)                 = delete;
    FmodBridge& operator=(FmodBridge&&)      = delete;

    // Set the FMOD System* + the live RadioStreamFmod we'll attach
    // the DSP to. Both may be null to disable bridging; the next
    // tick() will uninstall if it was previously installed.
    //
    // `radio_stream` is the embedded RadioStreamFmod (= refcount + 0x10).
    // FMOD's active channel handle for this stream lives at
    // radio_stream + 0x20.
    void set_target(System* system, std::byte* radio_stream) noexcept;

    // Per-tick maintenance. Reads the channel handle from the active
    // radio_stream, validates it via Handle::open, and:
    //   - installs if not yet installed and handle is valid
    //   - retargets if the handle changed
    //   - uninstalls if the handle went away
    // Cheap to call (one indirect read + one Handle::open per tick).
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

    // Set whether the read callback should resample 44.1k → 48k via
    // linear interpolation. Default is true (FMOD's master rate in FH6
    // is 48k); set false if the underlying channel runs at 44.1k and
    // no resampling is needed. Safe to toggle at any time.
    void set_resample_enabled(bool enabled) noexcept {
        resample_enabled_.store(enabled, std::memory_order_release);
    }

    // Access the per-bridge AGC + peak-limiter. Enabled by default
    // with broadcast-ish defaults; turn off via .set_enabled(false)
    // if you want raw source level.
    horizon::audio::Normalizer&       normalizer()       noexcept { return normalizer_; }
    const horizon::audio::Normalizer& normalizer() const noexcept { return normalizer_; }

    // Stats used by the /api/state surface and for debugging.
    std::uint64_t total_frames_in()  const noexcept { return frames_in_.load(std::memory_order_relaxed); }
    std::uint64_t total_frames_out() const noexcept { return frames_out_.load(std::memory_order_relaxed); }
    std::uint64_t underrun_count()   const noexcept { return underruns_.load(std::memory_order_relaxed); }
    std::uint64_t callback_count()   const noexcept { return callbacks_.load(std::memory_order_relaxed); }

private:
    // FMOD callback dispatch. The trampoline reads the process-global
    // active-bridge pointer; the instance method `read` fills out_buffer.
    static Result read_trampoline(DspState* state,
                                  float*    in_buffer,
                                  float*    out_buffer,
                                  unsigned int length,
                                  int       inchannels,
                                  int*      outchannels);

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
    static constexpr std::size_t kRingFrames = 65536;  // ~1.5s at 44.1kHz stereo

    ResolvedHooks    hooks_;
    CreateDspResolver lazy_create_dsp_;

    // Live targets, only mutated from the control thread (tick caller).
    System*          system_       = nullptr;
    std::byte*       radio_stream_ = nullptr;

    // Current DSP install state, only mutated from the control thread.
    Dsp*             dsp_            = nullptr;
    std::uint32_t    current_handle_ = 0;

    horizon::audio::SpscRingBuffer<std::int16_t> ring_;

    // Resampler scratch. Only the FMOD mixer thread touches these.
    // Reset to zero on every install/uninstall (serialized against the
    // mixer by FMOD's removeDSP semantics).
    double  resample_phase_ = 0.0;
    std::int16_t prev_l_    = 0;
    std::int16_t prev_r_    = 0;
    std::int16_t cur_l_     = 0;
    std::int16_t cur_r_     = 0;
    bool have_prev_ = false;
    bool have_cur_  = false;

    horizon::audio::Normalizer normalizer_;

    std::atomic<bool>          resample_enabled_{true};
    std::atomic<std::uint64_t> frames_in_{0};
    std::atomic<std::uint64_t> frames_out_{0};
    std::atomic<std::uint64_t> underruns_{0};
    std::atomic<std::uint64_t> callbacks_{0};

    // Wall-clock time of the most recent read callback (microseconds
    // since steady_clock epoch). push_pcm samples this to detect
    // "consumer stalled" (game paused, cutscene, FMOD silenced), and
    // signals drain_request_ so the mixer drops stale ring contents
    // on its first call back. 0 means "no callback yet."
    std::atomic<std::uint64_t> last_callback_us_{0};

    // Cross-thread drain signal. Set by push_pcm (or install_on_handle)
    // when ring contents are stale. Honored by read() on the next
    // callback: discards everything currently readable so the consumer
    // resumes from whatever the producer is writing right now,
    // not from queued-during-pause audio.
    std::atomic<bool>          drain_request_{false};
};

} // namespace horizon::fmod

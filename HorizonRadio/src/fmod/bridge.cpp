#include <array>
#include <chrono>
#include <cstring>
#include <horizon/fmod/bridge.hpp>
#include <horizon/inject/safe_mem.hpp>

namespace horizon::fmod {

namespace {

// One bridge per process. Set in install_on_handle() BEFORE addDSP
// and cleared in uninstall_internal() AFTER removeDSP; the trampoline
// reads with acquire so it observes the bridge in a consistent state
// relative to FMOD's addDSP / removeDSP serialization.
std::atomic<FmodBridge*> g_active_bridge{nullptr};

// FMOD callbacks operate on small blocks -- typically 512-1024 frames.
// 4096 covers any realistic worst case; if FMOD ever asks for more
// we return silence rather than risking a stack overflow.
constexpr std::size_t kMaxBlockFrames = 4096;

// Channel handle slot inside the embedded RadioStreamFmod. FMOD writes
// the active channel's packed 32-bit handle here and clears it when
// the channel is destroyed.
constexpr std::ptrdiff_t kChannelHandleOffset = 0x20;

// 44.1 kHz -> 48 kHz step. The DSP read callback runs at the channel
// or mixer rate (48k on FH6); our sources produce 44.1k. We
// linear-interpolate one sample of output per `kStep` samples of input.
constexpr double kResampleStep = 44100.0 / 48000.0;

// Threshold for "consumer stalled" detection: if the source pushes
// and the last DSP read callback was more than this long ago, the
// game is paused / in a cutscene / FMOD muted us. Drop queued
// audio so resume is current, not "starts from the pause moment,
// catches up to live over the next 1.5s."
constexpr std::uint64_t kConsumerStallUs = 100'000; // 100 ms

std::uint64_t now_us() noexcept {
    return static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch())
            .count());
}

// FMOD's `(ChannelControl*)` is a packed 32-bit handle zero-extended.
ChannelControl* handle_as_channel(std::uint32_t h) noexcept {
    return reinterpret_cast<ChannelControl*>(static_cast<std::uintptr_t>(h));
}

} // namespace

FmodBridge::FmodBridge(const ResolvedHooks& hooks) : hooks_(hooks), ring_(kRingFrames * kChannels) {}

FmodBridge::~FmodBridge() {
    uninstall();
}

void FmodBridge::set_target(System* system, std::byte* radio_stream) noexcept {
    // If the radio_stream changed, the next tick() will retarget.
    // If it cleared, the next tick() will uninstall.
    system_       = system;
    radio_stream_ = radio_stream;
}

bool FmodBridge::installed() const noexcept {
    return dsp_ != nullptr;
}

bool FmodBridge::current_channel_alive() const noexcept {
    return current_handle_ != 0 && validate_handle(current_handle_);
}

void FmodBridge::tick() noexcept {
    if (!hooks_.addDsp || !hooks_.removeDsp || !hooks_.dspRelease || !hooks_.handleOpen) {
        return; // resolver didn't fill the required slots
    }
    // createDsp is lazy-resolved on the first install attempt — see
    // install_on_handle below.
    if (radio_stream_ == nullptr || system_ == nullptr) {
        if (installed())
            uninstall_internal();
        return;
    }

    // read_live_channel_handle validates the raw handle via
    // Handle::open every tick. We pay that cost on every call (~20 Hz
    // in production) because the raw slot can stay numerically
    // unchanged while FMOD treats the channel as destroyed — only
    // Handle::open tells us authoritatively.
    const auto handle = read_live_channel_handle();
    if (handle == 0) {
        if (installed())
            uninstall_internal();
        return;
    }
    if (handle == current_handle_ && installed())
        return; // steady state
    if (installed())
        uninstall_internal();
    install_on_handle(handle);
}

void FmodBridge::uninstall() noexcept {
    uninstall_internal();
}

bool FmodBridge::install_on_handle(const std::uint32_t handle) noexcept {
    // Lazy-resolve createDsp. FMOD's System::createDSP path isn't always
    // wired up in .text at DllMain time; by the time we get here (after
    // discovery has found a chain-valid RadioStreamFmod), the game has
    // touched its audio subsystem and the LEA is resident. If the
    // resolver was provided, give it one shot per install attempt.
    if (hooks_.createDsp == nullptr && lazy_create_dsp_ != nullptr) {
        if (const auto resolved = lazy_create_dsp_(); resolved != nullptr) {
            OutputDebugStringW(L"[horizon-radio] bridge: resolved createDsp lazily on first install\n");
            hooks_.createDsp = resolved;
        }
    }
    if (hooks_.createDsp == nullptr) {
        // Still not available — bail; tick() will retry next time
        // through. Cheap enough to keep polling.
        return false;
    }

    DspDescription desc{};
    std::strncpy(desc.name, "Horizon Radio PCM", sizeof(desc.name) - 1);
    desc.version          = 1;
    desc.numinputbuffers  = 1;
    desc.numoutputbuffers = 1;
    desc.read             = &FmodBridge::read_trampoline;
    // userdata not used: we dispatch via the global pointer because
    // FMOD's read callback ABI gives us no userdata pointer we can
    // rely on across the various plugin sdk versions.

    // Publish the bridge BEFORE addDSP returns; FMOD may invoke the
    // read callback the instant addDSP succeeds.
    g_active_bridge.store(this, std::memory_order_release);

    // createDSP rejects mismatched pluginsdkversion stamps. FMOD
    // shipped multiple values across the 1.x line; try them in order
    // and accept whichever the host accepts.
    using inject::seh_call;
    Dsp* dsp = nullptr;
    for (const auto sdk_version : kFmodPluginSdkVersions) {
        desc.pluginsdkversion = sdk_version;
        dsp                   = nullptr;
        auto       rc         = static_cast<Result>(~0);
        const bool ok         = seh_call([&] { rc = hooks_.createDsp(system_, &desc, &dsp); });
        if (ok && rc == Result::Ok && dsp != nullptr)
            break;
        dsp = nullptr;
    }
    if (dsp == nullptr) {
        g_active_bridge.store(nullptr, std::memory_order_release);
        return false;
    }

    Result     add_rc = static_cast<Result>(~0);
    const bool add_ok = seh_call([&] { add_rc = hooks_.addDsp(handle_as_channel(handle), 0, dsp); });
    if (!add_ok || add_rc != Result::Ok) {
        seh_call([&] { hooks_.dspRelease(dsp); });
        g_active_bridge.store(nullptr, std::memory_order_release);
        return false;
    }

    dsp_            = dsp;
    current_handle_ = handle;

    // Drop whatever's queued in the ring: while the bridge was
    // uninstalled the source kept push_pcm-ing, leaving up to 1.5s
    // of stale audio. We request a drain (executed by the mixer on
    // its first read) instead of resetting from this thread — the
    // source's push_pcm runs concurrently and racing it on ring_
    // is UB. Resampler state we DO own exclusively here.
    drain_request_.store(true, std::memory_order_release);
    resample_phase_ = 0.0;
    have_prev_ = have_cur_ = false;
    prev_l_ = prev_r_ = cur_l_ = cur_r_ = 0;

    // Best-effort: pin the channel in loop mode so FMOD doesn't tear
    // it down when the placeholder sample ends. Failure here is
    // logged but not fatal -- the user-visible symptom is "audio
    // cuts after ~2 min."
    if (hooks_.setMode) {
        seh_call([&] { hooks_.setMode(handle_as_channel(handle), kFmodLoopNormal); });
    }
    return true;
}

void FmodBridge::uninstall_internal() noexcept {
    if (dsp_ == nullptr)
        return;

    using inject::seh_call;

    // removeDSP serializes against the FMOD mixer: by the time it
    // returns, no further read callbacks are pending. Only THEN is
    // it safe to clear g_active_bridge. SEH-wrap because the channel
    // handle may already be dead (game destroyed it before we got
    // to our removeDSP), and the dead-handle code path inside FMOD
    // has been observed to AV.
    if (current_handle_ != 0 && hooks_.removeDsp) {
        seh_call([&] { hooks_.removeDsp(handle_as_channel(current_handle_), dsp_); });
    }
    if (hooks_.dspRelease) {
        seh_call([&] { hooks_.dspRelease(dsp_); });
    }
    dsp_            = nullptr;
    current_handle_ = 0;

    g_active_bridge.store(nullptr, std::memory_order_release);

    // Drop any audio queued for the channel we no longer own.
    ring_.reset();
}

std::uint32_t FmodBridge::read_live_channel_handle() const noexcept {
    if (radio_stream_ == nullptr)
        return 0;
    using inject::safe_read_qword;
    // Channel handle is a uint32 at radio_stream + 0x20. Read the
    // qword and take the low 32 bits.
    const auto raw    = safe_read_qword(radio_stream_ + kChannelHandleOffset);
    const auto handle = static_cast<std::uint32_t>(raw);
    if (handle == 0)
        return 0;
    return validate_handle(handle) ? handle : 0;
}

bool FmodBridge::validate_handle(std::uint32_t handle) const noexcept {
    if (handle == 0 || hooks_.handleOpen == nullptr)
        return false;
    void*         inst       = nullptr;
    std::uint64_t lock_state = 0;
    std::uint32_t rc         = ~0u;
    using inject::seh_call;
    const bool ok = seh_call([&] { rc = hooks_.handleOpen(handle, &inst, &lock_state); });
    // Always unlock if we got a lock_state, even on rc != 0. Skipping
    // unlock leaks an FMOD resolver slot and eventually freezes the
    // game thread.
    if (ok && hooks_.handleUnlock && lock_state) {
        seh_call([&] { hooks_.handleUnlock(lock_state); });
    }
    return ok && rc == 0 && inst != nullptr;
}

std::size_t FmodBridge::push_pcm(const std::int16_t* frames, std::size_t frame_count) {
    // Consumer-stall detection: if the mixer hasn't called us in a
    // while, request that it drop queued audio on its next call.
    // Handles game-pause / cutscene gracefully — without this, FMOD
    // would resume by playing the 1.5s of audio that piled up
    // during the pause and only then catch up to live.
    const auto last = last_callback_us_.load(std::memory_order_acquire);
    if (last != 0) {
        const auto since = now_us() - last;
        if (since > kConsumerStallUs) {
            drain_request_.store(true, std::memory_order_release);
        }
    }

    const std::size_t samples       = frame_count * kChannels;
    const std::size_t pushed        = ring_.push(frames, samples);
    const std::size_t pushed_frames = pushed / kChannels;
    frames_in_.fetch_add(pushed_frames, std::memory_order_relaxed);
    return pushed_frames;
}

Result FmodBridge::read_trampoline(DspState* /*state*/, float* /*in_buffer*/, float* out_buffer,
                                   const unsigned int length, int /*inchannels*/, int* outchannels) {
    auto*     bridge       = g_active_bridge.load(std::memory_order_acquire);
    const int channels_out = (outchannels != nullptr) ? *outchannels : 0;
    if (!bridge || channels_out <= 0 || out_buffer == nullptr) {
        if (out_buffer && channels_out > 0) {
            std::memset(out_buffer, 0,
                        static_cast<std::size_t>(length) * static_cast<std::size_t>(channels_out) * sizeof(float));
        }
        return Result::Ok;
    }
    return bridge->read(out_buffer, length, channels_out);
}

Result FmodBridge::read(float* out_buffer, const unsigned int length, const int outchannels) {
    callbacks_.fetch_add(1, std::memory_order_relaxed);
    last_callback_us_.store(now_us(), std::memory_order_release);

    // Honor any pending drain request from the producer (push_pcm
    // detected a long gap) or from install_on_handle (just attached
    // to a fresh channel). We're on the only consumer thread, so
    // discarding via the read-side primitive is race-free w.r.t.
    // the source's concurrent push().
    if (drain_request_.exchange(false, std::memory_order_acq_rel)) {
        ring_.discard_all_from_consumer();
        // Reset resampler state too: holding interpolation samples
        // from before the drain produces a single mixed sample
        // (old prev × new cur) that's audibly wrong.
        resample_phase_ = 0.0;
        have_prev_ = have_cur_ = false;
        prev_l_ = prev_r_ = cur_l_ = cur_r_ = 0;
        // Reset the normalizer so its smoothed-RMS history doesn't
        // carry over from before the gap.
        normalizer_.reset();
    }

    if (length > kMaxBlockFrames || outchannels <= 0) {
        std::memset(out_buffer, 0,
                    static_cast<std::size_t>(length) * static_cast<std::size_t>(outchannels > 0 ? outchannels : 1) *
                        sizeof(float));
        return Result::Ok;
    }

    // Pre-zero: FMOD may hand us a buffer with stale floats on
    // partial fills, and we'd rather underrun-tail be silence than
    // ghost audio.
    const std::size_t total_floats = static_cast<std::size_t>(length) * static_cast<std::size_t>(outchannels);
    std::memset(out_buffer, 0, total_floats * sizeof(float));

    constexpr float kInv32768 = 1.0f / 32768.0f;

    auto pull_frame = [&](std::int16_t& l, std::int16_t& r) -> bool {
        std::int16_t      buf[2];
        const std::size_t got = ring_.pop(buf, kChannels);
        if (got != kChannels)
            return false;
        l = buf[0];
        r = buf[1];
        return true;
    };

    auto write_frame = [&](std::size_t frame_idx, float L, float R) {
        // AGC + peak limiter, then master gain (Events volume/duck),
        // then hard-clip safety. Normalizer hard-clips internally too;
        // the explicit clamp here is a belt-and-braces in case the
        // normalizer is disabled.
        normalizer_.process_stereo(L, R);
        const float mg = master_gain_.load(std::memory_order_relaxed);
        L *= mg;
        R *= mg;
        L        = L > 1.0f ? 1.0f : (L < -1.0f ? -1.0f : L);
        R        = R > 1.0f ? 1.0f : (R < -1.0f ? -1.0f : R);
        float* o = out_buffer + frame_idx * outchannels;
        if (outchannels == 1) {
            o[0] = (L + R) * 0.5f;
        } else {
            o[0]               = L;
            o[1]               = R;
            const float center = (L + R) * 0.5f;
            for (int c = 2; c < outchannels; ++c)
                o[c] = center;
        }
    };

    const bool do_resample = resample_enabled_.load(std::memory_order_acquire);

    if (!do_resample) {
        // 1:1 path -- pop frame, emit. Source and channel rates match.
        std::array<std::int16_t, kMaxBlockFrames * kChannels> scratch;
        const std::size_t samples_needed = static_cast<std::size_t>(length) * kChannels;
        const std::size_t samples_got    = ring_.pop(scratch.data(), samples_needed);
        const std::size_t frames_got     = samples_got / kChannels;
        for (std::size_t f = 0; f < frames_got; ++f) {
            const float L = scratch[2 * f] * kInv32768;
            const float R = scratch[2 * f + 1] * kInv32768;
            write_frame(f, L, R);
        }
        if (frames_got < length) {
            underruns_.fetch_add(1, std::memory_order_relaxed);
        }
        frames_out_.fetch_add(length, std::memory_order_relaxed);
        return Result::Ok;
    }

    // Resample path -- linear interpolation between prev_/cur_ at
    // fractional phase resample_phase_, advancing by kResampleStep
    // per output frame.
    bool          underrun = false;
    std::uint32_t f        = 0;
    for (; f < length; ++f) {
        if (!have_prev_) {
            if (!pull_frame(prev_l_, prev_r_)) {
                underrun = true;
                break;
            }
            have_prev_ = true;
        }
        if (!have_cur_) {
            if (!pull_frame(cur_l_, cur_r_)) {
                underrun = true;
                break;
            }
            have_cur_ = true;
        }
        const double t = resample_phase_;
        const float  L = static_cast<float>(((static_cast<double>(cur_l_) - prev_l_) * t + prev_l_) * kInv32768);
        const float  R = static_cast<float>(((static_cast<double>(cur_r_) - prev_r_) * t + prev_r_) * kInv32768);
        write_frame(f, L, R);

        resample_phase_ += kResampleStep;
        while (resample_phase_ >= 1.0) {
            prev_l_ = cur_l_;
            prev_r_ = cur_r_;
            if (!pull_frame(cur_l_, cur_r_)) {
                have_cur_ = false;
                underrun  = true;
                break;
            }
            resample_phase_ -= 1.0;
        }
        if (underrun)
            break;
    }

    if (underrun) {
        // Tail past `f` already zeroed by pre-zero above.
        underruns_.fetch_add(1, std::memory_order_relaxed);
        resample_phase_ = 0.0;
        have_prev_ = have_cur_ = false;
    }

    frames_out_.fetch_add(length, std::memory_order_relaxed);
    return Result::Ok;
}

} // namespace horizon::fmod

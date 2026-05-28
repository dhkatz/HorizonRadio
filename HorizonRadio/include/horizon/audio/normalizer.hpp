#pragma once

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>

namespace horizon::audio {

// Two-stage stereo normalizer applied in the FMOD DSP read callback
// so every audio source (LocalFile today, Spotify / YouTube later)
// lands at consistent perceived loudness without clipping the FMOD
// mixer downstream.
//
// Stage 1: AGC. Tracks smoothed RMS over a few-second window and
//   computes a target gain to bring it to `target_rms`. The gain is
//   itself smoothed over many seconds so we don't audibly "pump"
//   during loud passages; the goal is to settle different songs
//   onto the same broadcast level, not to flatten dynamics inside a
//   single song.
//
// Stage 2: Peak limiter. After AGC, fast-attack / slow-release gain
//   reduction whenever the post-AGC peak crosses `peak_threshold`.
//   Catches transient peaks (drum hits, bass kicks on hot masters)
//   without sustained level change.
//
// Output is hard-clipped to [-1, +1] as a final safety net — FMOD's
// downstream stages don't enjoy receiving floats outside that range.
class Normalizer {
public:
    // Sane defaults for "broadcast radio" feel. Tune via setters if needed.
    Normalizer() = default;

    void set_enabled(bool e) noexcept { enabled_.store(e, std::memory_order_release); }
    bool enabled() const noexcept     { return enabled_.load(std::memory_order_acquire); }

    void set_target_rms(float v)      noexcept { target_rms_       = std::clamp(v, 0.01f, 0.5f); }
    void set_max_gain(float v)        noexcept { max_gain_         = std::clamp(v, 1.0f, 32.0f); }
    void set_min_gain(float v)        noexcept { min_gain_         = std::clamp(v, 0.01f, 1.0f); }
    void set_peak_threshold(float v)  noexcept { peak_threshold_   = std::clamp(v, 0.5f, 1.0f); }

    // Called by the bridge when it discards stale audio (drain on
    // pause/cutscene/install). Resets the smoothed RMS so the new
    // material's loudness is observed from scratch — without this,
    // resuming after a long silence would inherit "average of long
    // silence" as RMS≈0 and the AGC would slam to max_gain.
    void reset() noexcept {
        rms_sq_smoothed_ = target_rms_ * target_rms_;
        current_gain_    = 1.0f;
        limiter_gain_    = 1.0f;
    }

    // In-place stereo process at the configured sample rate. The
    // alpha coefficients are tuned for ~48 kHz output; reasonably
    // close at 44.1 too. Hot path — kept branchless and inlinable.
    void process_stereo(float& l, float& r) noexcept {
        if (!enabled_.load(std::memory_order_acquire)) return;

        // Running mean of squared amplitude (mean-square ≈ RMS²).
        // alpha ≈ 1 / (3s × 48kHz) ≈ 7e-6 gives a ~3-second window;
        // hardcoded to avoid a runtime divide.
        constexpr float kRmsAlpha = 7e-6f;
        const float instant_sq   = 0.5f * (l * l + r * r);
        rms_sq_smoothed_ = rms_sq_smoothed_ + (instant_sq - rms_sq_smoothed_) * kRmsAlpha;

        // Compute the gain that WOULD hit target_rms. Below a noise
        // floor we hold the previous gain (so silence doesn't slam
        // to max_gain).
        constexpr float kNoiseFloorSq = 1e-8f;  // ~ -80 dBFS²
        float target_gain = current_gain_;
        if (rms_sq_smoothed_ > kNoiseFloorSq) {
            const float rms = std::sqrt(rms_sq_smoothed_);
            target_gain     = target_rms_ / rms;
            target_gain     = std::clamp(target_gain, min_gain_, max_gain_);
        }

        // Smooth gain transitions over ~10 seconds at 48 kHz so
        // different songs settle to the same level over a few
        // seconds while inner-song dynamics survive.
        constexpr float kGainAlpha = 2e-6f;
        current_gain_ = current_gain_ + (target_gain - current_gain_) * kGainAlpha;

        l *= current_gain_;
        r *= current_gain_;

        // Peak limiter: fast attack (~3 ms), slow release (~200 ms).
        // The instantaneous "needed gain" is threshold/peak when peak
        // > threshold, else 1.0; we apply attack when pulling down,
        // release when letting up.
        constexpr float kAttackAlpha  = 0.01f;   // ~3 ms @ 48kHz
        constexpr float kReleaseAlpha = 0.0001f; // ~200 ms @ 48kHz
        const float peak = std::max(std::abs(l), std::abs(r));
        const float needed_lim = peak > peak_threshold_
            ? peak_threshold_ / peak
            : 1.0f;
        const float lim_alpha = needed_lim < limiter_gain_ ? kAttackAlpha : kReleaseAlpha;
        limiter_gain_ = limiter_gain_ + (needed_lim - limiter_gain_) * lim_alpha;

        l *= limiter_gain_;
        r *= limiter_gain_;

        // Hard clip safety net. Shouldn't trigger after the limiter
        // settles, but covers the initial transient before kAttackAlpha
        // has caught up.
        l = std::clamp(l, -1.0f, 1.0f);
        r = std::clamp(r, -1.0f, 1.0f);
    }

    // Telemetry (not synchronized; reader sees an eventually-consistent
    // snapshot, which is fine for a dashboard UI).
    float current_gain() const noexcept { return current_gain_; }
    float current_limiter_gain() const noexcept { return limiter_gain_; }

private:
    std::atomic<bool> enabled_{true};

    // Tunables. Read on the audio thread but written rarely from
    // the control thread; aligned to 8 bytes so the writes are
    // atomic on x64 even without explicit atomics.
    float target_rms_     = 0.15f;   // ≈ -16 dBFS RMS — broadcast-ish
    float max_gain_       = 4.0f;    // +12 dB max boost (quiet songs)
    float min_gain_       = 0.1f;    // -20 dB max cut  (hot masters)
    float peak_threshold_ = 0.92f;   // ≈ -0.7 dBFS limiter ceiling

    // Audio-thread state.
    float rms_sq_smoothed_ = 0.0225f;  // = target_rms² so first frames sound right
    float current_gain_    = 1.0f;
    float limiter_gain_    = 1.0f;
};

} // namespace horizon::audio

#include <algorithm>
#include <cmath>
#include <doctest/doctest.h>
#include <horizon/audio/normalizer.hpp>
#include <numbers>

using horizon::audio::Normalizer;

namespace {

// Run a sine wave at the given peak amplitude for N samples and
// return the post-process peak observed in the output. Lets us
// characterize the AGC/limiter behavior without dragging in the
// full FMOD-bridge harness.
struct ProcessResult {
    float peak_out;
    float last_l;
    float last_r;
};

ProcessResult drive_sine(Normalizer& n, float input_peak, std::size_t frames) {
    constexpr float kFreq = 440.0f;
    constexpr float kSr   = 48000.0f;
    ProcessResult   r{};
    for (std::size_t i = 0; i < frames; ++i) {
        const float t  = static_cast<float>(i) / kSr;
        const float v  = input_peak * std::sin(2.0f * std::numbers::pi_v<float> * kFreq * t);
        float       l  = v;
        float       r2 = v;
        n.process_stereo(l, r2);
        r.peak_out = std::max({r.peak_out, std::abs(l), std::abs(r2)});
        r.last_l   = l;
        r.last_r   = r2;
    }
    return r;
}

} // namespace

TEST_CASE("Normalizer: disabled passes input through unchanged") {
    Normalizer n;
    n.set_enabled(false);
    float l = 0.7f, r = -0.5f;
    n.process_stereo(l, r);
    CHECK(l == 0.7f);
    CHECK(r == -0.5f);
}

TEST_CASE("Normalizer: peak limiter prevents output above ceiling") {
    Normalizer n;
    n.set_target_rms(0.5f); // very high target so AGC won't pull down on its own
    n.set_peak_threshold(0.9f);
    // Drive 5 seconds of full-scale sine. The limiter's slow release
    // means it'll settle below the threshold within the first second.
    const auto res = drive_sine(n, 1.0f, 240000);
    // After settling, sustained peak should be at most ~1% above the
    // threshold (small attack-overshoot is tolerable).
    CHECK(res.peak_out <= 1.01f);
    CHECK(res.last_l <= 0.95f);
    CHECK(res.last_r <= 0.95f);
}

TEST_CASE("Normalizer: AGC boosts a quiet source toward target") {
    Normalizer n;
    n.set_target_rms(0.15f);
    // 60s of a quiet sine — way below target. AGC should boost it
    // up to (but not past) max_gain.
    drive_sine(n, 0.05f, std::size_t{60} * 48000);
    CHECK(n.current_gain() > 1.5f);  // clearly boosted
    CHECK(n.current_gain() <= 4.0f); // never exceeds max_gain
}

TEST_CASE("Normalizer: AGC attenuates a hot source") {
    Normalizer n;
    n.set_target_rms(0.15f);
    // 60s of a full-scale sine. AGC should pull current_gain below
    // unity since the source is way above target loudness.
    drive_sine(n, 1.0f, std::size_t{60} * 48000);
    CHECK(n.current_gain() < 0.5f);
    CHECK(n.current_gain() >= 0.1f); // never exceeds min_gain floor
}

TEST_CASE("Normalizer: reset() returns gain to unity") {
    Normalizer n;
    drive_sine(n, 0.9f, 10000); // drive AGC away from unity
    n.reset();
    CHECK(n.current_gain() == 1.0f);
    CHECK(n.current_limiter_gain() == 1.0f);
}

TEST_CASE("Normalizer: clamps output to [-1, 1] hard ceiling") {
    Normalizer n;
    n.set_enabled(true);
    // Single hostile sample — even before the limiter has a chance to
    // settle, the hard clip must catch us at ±1.
    float l = 5.0f, r = -5.0f;
    n.process_stereo(l, r);
    CHECK(l <= 1.0f);
    CHECK(r >= -1.0f);
}

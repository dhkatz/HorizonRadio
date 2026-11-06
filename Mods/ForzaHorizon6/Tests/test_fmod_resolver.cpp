#include <cstdint>
#include <cstdio>
#include <doctest/doctest.h>
#include <horizon/fmod/resolver.hpp>
#include <string>
#include <windows.h>

using namespace horizon::fmod;
using horizon::inject::PeImage;

// A real function in this test exe with internal linkage prevented so
// its body actually lands in .text. We sigscan for its prologue at
// runtime and expect the resolver to hand back this address.
extern "C" __declspec(noinline) int horizon_resolver_target_one(int x) {
    // Some arithmetic the optimizer can't fold to a constant; keeps
    // the function non-trivially-sized.
    return (x * 7 + 31) ^ (x >> 1);
}

extern "C" __declspec(noinline) int horizon_resolver_target_two(int x, int y) {
    return horizon_resolver_target_one(x) + horizon_resolver_target_one(y);
}

namespace {

// Build an IDA-style pattern from the first N bytes of a function. We
// reserve enough scratch for 24 bytes -> 72 chars + nul.
std::string make_pattern(const void* fn, std::size_t bytes = 16) {
    const auto* p = reinterpret_cast<const std::uint8_t*>(fn);
    std::string out;
    out.reserve(bytes * 3);
    char buf[8];
    for (std::size_t i = 0; i < bytes; ++i) {
        std::snprintf(buf, sizeof(buf), "%s%02X", (i == 0 ? "" : " "), p[i]);
        out += buf;
    }
    return out;
}

} // namespace

TEST_CASE("FmodResolver: empty signatures resolve to nullptr, report not-found") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    FmodResolver r(img, SignatureSet{});
    auto         hooks = r.resolve();

    CHECK(hooks.createDsp == nullptr);
    CHECK(hooks.addDsp == nullptr);
    CHECK(hooks.removeDsp == nullptr);
    CHECK(hooks.dspRelease == nullptr);
    CHECK_FALSE(r.report().ready());
    CHECK_FALSE(r.report().createDsp);
}

TEST_CASE("FmodResolver: resolves a function by its real prologue") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    const std::string pat = make_pattern(reinterpret_cast<void*>(&horizon_resolver_target_one));

    SignatureSet sigs{};
    sigs.createDsp = {.pattern = pat};

    FmodResolver r(img, sigs);
    auto         hooks = r.resolve();

    REQUIRE(r.report().createDsp);
    CHECK(reinterpret_cast<void*>(hooks.createDsp) == reinterpret_cast<void*>(&horizon_resolver_target_one));
}

TEST_CASE("FmodResolver: distinguishes two functions by their distinct prologues") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    const std::string pat_one = make_pattern(reinterpret_cast<void*>(&horizon_resolver_target_one));
    const std::string pat_two = make_pattern(reinterpret_cast<void*>(&horizon_resolver_target_two));
    // Sanity: the two patterns must actually differ, otherwise the test
    // can't tell whether the resolver is working or just lucky.
    REQUIRE(pat_one != pat_two);

    SignatureSet sigs{};
    sigs.createDsp = {.pattern = pat_one};
    sigs.addDsp    = {.pattern = pat_two};

    FmodResolver r(img, sigs);
    auto         hooks = r.resolve();

    CHECK(reinterpret_cast<void*>(hooks.createDsp) == reinterpret_cast<void*>(&horizon_resolver_target_one));
    CHECK(reinterpret_cast<void*>(hooks.addDsp) == reinterpret_cast<void*>(&horizon_resolver_target_two));
}

TEST_CASE("FmodResolver: partial set leaves unconfigured slots null but resolves the rest") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    const std::string pat = make_pattern(reinterpret_cast<void*>(&horizon_resolver_target_one));
    SignatureSet      sigs{};
    sigs.dspRelease = {.pattern = pat};

    FmodResolver r(img, sigs);
    auto         hooks = r.resolve();

    CHECK(hooks.dspRelease != nullptr);
    CHECK(hooks.createDsp == nullptr);
    CHECK(hooks.addDsp == nullptr);
    CHECK(hooks.removeDsp == nullptr);
    CHECK(r.report().dspRelease);
    CHECK_FALSE(r.report().ready());
}

TEST_CASE("FmodResolver: pattern that doesn't match yields nullptr + not-found") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    SignatureSet sigs{};
    // Wildly unlikely byte sequence to appear in .text.
    sigs.createDsp = {.pattern = "DE AD BE EF FE ED FA CE BA AD F0 0D 12 34 56 78"};

    FmodResolver r(img, sigs);
    auto         hooks = r.resolve();

    CHECK(hooks.createDsp == nullptr);
    CHECK_FALSE(r.report().createDsp);
}

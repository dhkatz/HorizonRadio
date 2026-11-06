#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <doctest/doctest.h>
#include <horizon/fmod/signatures.hpp>
#include <horizon/inject/game_resolver.hpp>
#include <ostream>
#include <string_view>

using horizon::game::GlobalResolution;
using horizon::game::resolve_rip_load_global;
using horizon::inject::compile_pattern;

namespace {

// A fake .text: some filler, then the shape the RadioState loader has --
// `push`-ish prefix bytes followed by `mov rbx, [rip+disp32]` (48 8B 1D) and a
// trailing `test rbx, rbx` (48 85 DB). The pattern below wildcards the disp32.
constexpr std::size_t kMovAt = 8; // offset of 48 8B 1D in the buffer

std::array<std::byte, 32> make_text(std::int32_t disp32) {
    std::array<std::byte, 32> buf{};
    buf.fill(std::byte{0xCC});
    const std::uint8_t prefix[] = {0x57, 0x48, 0x83, 0xEC, 0x40}; // push rdi; sub rsp,40
    std::memcpy(buf.data() + kMovAt - sizeof(prefix), prefix, sizeof(prefix));
    buf[kMovAt + 0] = std::byte{0x48};
    buf[kMovAt + 1] = std::byte{0x8B};
    buf[kMovAt + 2] = std::byte{0x1D};
    std::memcpy(buf.data() + kMovAt + 3, &disp32, sizeof(disp32));
    buf[kMovAt + 7] = std::byte{0x48};
    buf[kMovAt + 8] = std::byte{0x85};
    buf[kMovAt + 9] = std::byte{0xDB};
    return buf;
}

constexpr const char*    kPattern   = "57 48 83 EC 40 48 8B 1D ?? ?? ?? ?? 48 85 DB";
constexpr std::ptrdiff_t kMovOffset = 5; // 48 8B 1D sits 5 bytes into the pattern

std::uintptr_t addr(const void* p) {
    return reinterpret_cast<std::uintptr_t>(p);
}

} // namespace

TEST_CASE("resolve_rip_load_global: decodes the RIP-relative target") {
    const auto text = make_text(4); // target = end of mov + 4
    const auto res  = resolve_rip_load_global(text, compile_pattern(kPattern), kMovOffset, addr(text.data()),
                                              addr(text.data()) + text.size());
    REQUIRE(res.status == GlobalResolution::Status::ok);
    CHECK(res.match_count == 1);
    CHECK(addr(res.address) == addr(text.data()) + kMovAt + 7 + 4);
}

TEST_CASE("resolve_rip_load_global: rejects a target outside the module") {
    const auto text = make_text(0x100000);
    const auto res  = resolve_rip_load_global(text, compile_pattern(kPattern), kMovOffset, addr(text.data()),
                                              addr(text.data()) + text.size());
    CHECK(res.status == GlobalResolution::Status::target_outside_image);
    CHECK(res.address == nullptr);
}

TEST_CASE("resolve_rip_load_global: refuses to guess between two matches") {
    const auto                one = make_text(4);
    std::array<std::byte, 64> text{};
    std::memcpy(text.data(), one.data(), one.size());
    std::memcpy(text.data() + one.size(), one.data(), one.size());
    const auto res = resolve_rip_load_global(text, compile_pattern(kPattern), kMovOffset, addr(text.data()),
                                             addr(text.data()) + text.size());
    CHECK(res.status == GlobalResolution::Status::ambiguous);
    CHECK(res.match_count == 2);
    CHECK(res.address == nullptr);
}

TEST_CASE("resolve_rip_load_global: not found / bad offset / empty pattern") {
    const auto text = make_text(4);
    const auto lo   = addr(text.data());
    const auto hi   = lo + text.size();

    CHECK(resolve_rip_load_global(text, compile_pattern("DE AD BE EF 48 8B 1D ?? ?? ?? ??"), 4, lo, hi).status ==
          GlobalResolution::Status::not_found);
    // Offset points at the prefix, not at 48 8B 1D.
    CHECK(resolve_rip_load_global(text, compile_pattern(kPattern), 0, lo, hi).status ==
          GlobalResolution::Status::unexpected_instruction);
    // Offset would decode bytes past the end of the pattern.
    CHECK(resolve_rip_load_global(text, compile_pattern(kPattern), 12, lo, hi).status ==
          GlobalResolution::Status::unexpected_instruction);
    CHECK(resolve_rip_load_global(text, horizon::inject::Pattern{}, kMovOffset, lo, hi).status ==
          GlobalResolution::Status::empty_pattern);
}

TEST_CASE("kFh6Game: the RadioState loader decodes a mov at the configured offset") {
    const auto p = compile_pattern(horizon::game::signatures::kFh6Game.radio_state_loader);
    const auto o = static_cast<std::size_t>(horizon::game::signatures::kFh6Game.mov_rbx_offset_in_match);
    REQUIRE(o + 7 <= p.bytes.size());
    CHECK(std::to_integer<unsigned>(p.bytes[o + 0]) == 0x48u);
    CHECK(std::to_integer<unsigned>(p.bytes[o + 1]) == 0x8Bu);
    CHECK(std::to_integer<unsigned>(p.bytes[o + 2]) == 0x1Du);
    // The displacement must be wildcarded, or the pattern would pin one build's layout.
    for (std::size_t i = 3; i < 7; ++i)
        CHECK_FALSE(p.mask[o + i]);
}

TEST_CASE("known_build_label: recognizes listed builds only") {
    using horizon::game::signatures::known_build_label;
    CHECK(known_build_label(0x6322F5E) == "Steam, June 2026");
    CHECK(known_build_label(0x630E45E) == "Microsoft Store, June 2026");
    CHECK(known_build_label(0x1234).empty());
}

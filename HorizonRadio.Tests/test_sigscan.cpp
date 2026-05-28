#include <stdexcept>
#include <doctest/doctest.h>

#include <horizon/inject/sigscan.hpp>

#include <windows.h>

using namespace horizon::inject;

TEST_CASE("compile_pattern: fixed bytes and wildcards") {
    auto p = compile_pattern("48 89 5C ?? 18");
    REQUIRE(p.bytes.size() == 5);
    REQUIRE(p.mask.size() == 5);
    CHECK(p.mask[0] == true);
    CHECK(p.mask[3] == false);
    CHECK(static_cast<unsigned>(p.bytes[0]) == 0x48u);
    CHECK(static_cast<unsigned>(p.bytes[1]) == 0x89u);
    CHECK(static_cast<unsigned>(p.bytes[2]) == 0x5Cu);
    CHECK(static_cast<unsigned>(p.bytes[4]) == 0x18u);
}

TEST_CASE("compile_pattern: tolerates whitespace and casing") {
    auto p = compile_pattern("  Ab  cD  Ef  ");
    REQUIRE(p.bytes.size() == 3);
    CHECK(static_cast<unsigned>(p.bytes[0]) == 0xABu);
    CHECK(static_cast<unsigned>(p.bytes[1]) == 0xCDu);
    CHECK(static_cast<unsigned>(p.bytes[2]) == 0xEFu);
}

TEST_CASE("compile_pattern: rejects invalid hex") {
    CHECK_THROWS_AS(compile_pattern("4G"), std::invalid_argument);
    CHECK_THROWS_AS(compile_pattern("48 8"), std::invalid_argument);
}

TEST_CASE("find_pattern: finds simple sequence") {
    const std::byte data[] = {
        std::byte{0x00}, std::byte{0x48}, std::byte{0x89},
        std::byte{0x5C}, std::byte{0x18}, std::byte{0xFF}
    };
    auto* hit = find_pattern(data, "48 89 5C");
    REQUIRE(hit != nullptr);
    CHECK(hit == data + 1);
}

TEST_CASE("find_pattern: respects wildcards") {
    const std::byte data[] = {
        std::byte{0x48}, std::byte{0x89}, std::byte{0xAA}, std::byte{0x18}
    };
    CHECK(find_pattern(data, "48 89 ?? 18") == data);
    CHECK(find_pattern(data, "48 89 BB 18") == nullptr);
}

TEST_CASE("find_pattern: returns nullptr when haystack too small") {
    const std::byte data[] = {std::byte{0x48}, std::byte{0x89}};
    CHECK(find_pattern(data, "48 89 5C 24 18") == nullptr);
}

TEST_CASE("find_pattern: empty pattern returns nullptr") {
    const std::byte data[] = {std::byte{0x48}};
    CHECK(find_pattern(data, Pattern{}) == nullptr);
}

TEST_CASE("PeImage: parses sections of the test executable") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());
    CHECK(img.base() != 0);
    CHECK(img.image_size() > 0);
    CHECK(!img.text().empty());
    CHECK(!img.rdata().empty());
    // .pdata is present on every x64 PE that uses table-based unwind,
    // which includes anything MSVC produces for x64.
    CHECK(!img.pdata().empty());
}

TEST_CASE("PeImage + find_pattern: locates a known byte sequence in our own .rdata") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    // String literals are placed in .rdata by MSVC. We hold the address
    // in a volatile pointer so the optimizer can't constant-fold the
    // read below (which would let the linker dead-strip the literal).
    static volatile const char* marker = "HORIZON_SIGSCAN_SELF_TEST_MARKER";
    REQUIRE(marker[0] == 'H');

    // Confirm the literal actually landed inside the .rdata span our PE
    // parser found before testing the scan -- if this REQUIRE fires,
    // the test approach is broken, not find_pattern. (const_cast drops
    // the volatile qualifier we added above; reinterpret_cast can't.)
    const auto* marker_bytes = reinterpret_cast<const std::byte*>(
        const_cast<const char*>(marker));
    const auto  rdata_span   = img.rdata();
    REQUIRE(marker_bytes >= rdata_span.data());
    REQUIRE(marker_bytes <  rdata_span.data() + rdata_span.size());

    auto* hit = find_pattern(rdata_span, "48 4F 52 49 5A 4F 4E 5F");  // "HORIZON_"
    REQUIRE(hit != nullptr);
    CHECK(hit == marker_bytes);
}

TEST_CASE("compile_pattern_set: splits on `|`") {
    auto set = compile_pattern_set("AA BB | CC DD | EE FF");
    REQUIRE(set.alternatives.size() == 3);
    CHECK(static_cast<unsigned>(set.alternatives[0].bytes[0]) == 0xAA);
    CHECK(static_cast<unsigned>(set.alternatives[1].bytes[0]) == 0xCC);
    CHECK(static_cast<unsigned>(set.alternatives[2].bytes[0]) == 0xEE);
}

TEST_CASE("compile_pattern_set: tolerates leading/trailing/double pipes") {
    auto set = compile_pattern_set("| AA | | BB |");
    REQUIRE(set.alternatives.size() == 2);
}

TEST_CASE("match_pattern_set_at: any alternative matches") {
    auto set = compile_pattern_set("AA BB | CC DD");
    const std::byte buf1[]{std::byte{0xAA}, std::byte{0xBB}, std::byte{0x99}};
    const std::byte buf2[]{std::byte{0xCC}, std::byte{0xDD}, std::byte{0x99}};
    const std::byte buf3[]{std::byte{0x11}, std::byte{0x22}, std::byte{0x33}};
    CHECK(match_pattern_set_at({buf1, 3}, set));
    CHECK(match_pattern_set_at({buf2, 3}, set));
    CHECK(!match_pattern_set_at({buf3, 3}, set));
}

TEST_CASE("find_anchor_strings: requires NUL termination on both sides") {
    // "foo\0barbaz\0foo\0" — only the standalone "foo" entries should match.
    std::string buf = std::string("foo\0barbaz\0foo\0", 15);
    std::span<const std::byte> hay{
        reinterpret_cast<const std::byte*>(buf.data()), buf.size()};
    auto hits = find_anchor_strings(hay, "foo");
    CHECK(hits.size() == 2);
    // "bar" appears as a prefix inside "barbaz" — should NOT match.
    auto bar_hits = find_anchor_strings(hay, "bar");
    CHECK(bar_hits.empty());
}

TEST_CASE("find_lea_targeting: decodes `lea reg, [rip+disp32]`") {
    // Hand-assembled: `48 8D 1D 03 00 00 00` is `lea rbx, [rip+3]`,
    // 7-byte instruction; target = (insn_start + 7) + 3 = insn_start + 10.
    alignas(16) const std::byte text[] = {
        std::byte{0x48}, std::byte{0x8D}, std::byte{0x1D},
        std::byte{0x03}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0xCC}, std::byte{0xCC}, std::byte{0xCC},
        std::byte{'X'},  // target byte at offset 10
        std::byte{0x00},
    };
    const std::byte* target_arr[] = {text + 10};
    std::span<const std::byte* const> targets{target_arr, 1};
    auto hits = find_lea_targeting({text, sizeof(text)}, targets);
    REQUIRE(hits.size() == 1);
    CHECK(hits[0] == text);
}

TEST_CASE("enclosing_function_rva: maps instruction to function start") {
    RUNTIME_FUNCTION rfs[3]{};
    rfs[0].BeginAddress = 0x1000; rfs[0].EndAddress = 0x1100;
    rfs[1].BeginAddress = 0x1200; rfs[1].EndAddress = 0x1240;
    rfs[2].BeginAddress = 0x1300; rfs[2].EndAddress = 0x1380;
    std::span<const RUNTIME_FUNCTION> pdata{rfs, 3};

    CHECK(enclosing_function_rva(pdata, 0x1050) == 0x1000);
    CHECK(enclosing_function_rva(pdata, 0x1230) == 0x1200);
    CHECK(enclosing_function_rva(pdata, 0x1380) == 0);  // past end (exclusive)
    CHECK(enclosing_function_rva(pdata, 0x12FF) == 0);  // gap between fns
    CHECK(enclosing_function_rva(pdata, 0x0FFF) == 0);  // before first fn
}

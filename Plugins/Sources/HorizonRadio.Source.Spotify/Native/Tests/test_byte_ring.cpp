#include <array>
#include <cstdint>
#include <doctest/doctest.h>
#include <horizon/tap/byte_ring.hpp>
#include <numeric>
#include <vector>

using horizon::tap::ByteRing;

TEST_CASE("ByteRing round-trips bytes across the wrap") {
    ByteRing                     ring(16);
    std::array<std::uint8_t, 10> a{};
    std::iota(a.begin(), a.end(), std::uint8_t{1});
    REQUIRE(ring.push(a.data(), a.size()));
    std::array<std::uint8_t, 10> out{};
    REQUIRE(ring.pop(out.data(), 6) == 6);
    // 4 left at offset 6; 10 more wraps past the end.
    std::array<std::uint8_t, 10> b{};
    std::iota(b.begin(), b.end(), std::uint8_t{50});
    REQUIRE(ring.push(b.data(), b.size()));
    std::vector<std::uint8_t> rest(14);
    REQUIRE(ring.pop(rest.data(), rest.size()) == 14);
    CHECK(rest[0] == 7);
    CHECK(rest[3] == 10);
    CHECK(rest[4] == 50);
    CHECK(rest[13] == 59);
}

TEST_CASE("ByteRing writes a message whole or not at all") {
    ByteRing                     ring(16);
    std::array<std::uint8_t, 12> header{};
    std::array<std::uint8_t, 8>  body{};
    CHECK_FALSE(ring.push2(header.data(), header.size(), body.data(), body.size()));
    CHECK(ring.readable() == 0);
    CHECK(ring.push2(header.data(), header.size(), body.data(), 4));
    CHECK(ring.readable() == 16);
}

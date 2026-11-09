#include <windows.h>

// After <windows.h>, which the multimedia headers need first.
#include <cstring>
#include <doctest/doctest.h>
#include <horizon/tap/tap_writer.hpp>
#include <ks.h>
#include <ksmedia.h>
#include <mmreg.h>
#include <string>
#include <vector>

using horizon::tap::FormatPayload;
using horizon::tap::Kind;
using horizon::tap::MessageHeader;
using horizon::tap::TapWriter;

namespace {

struct Message {
    Kind                      kind;
    std::vector<std::uint8_t> payload;
};

std::vector<Message> Drain(TapWriter& writer) {
    std::vector<std::uint8_t> bytes(writer.ring().readable());
    writer.ring().pop(bytes.data(), bytes.size());
    std::vector<Message> out;
    for (std::size_t at = 0; at < bytes.size();) {
        MessageHeader h;
        std::memcpy(&h, bytes.data() + at, sizeof h);
        REQUIRE(h.magic == horizon::tap::kMagic);
        REQUIRE(h.version == horizon::tap::kVersion);
        at += sizeof h;
        out.push_back({static_cast<Kind>(h.kind),
                       {bytes.begin() + static_cast<std::ptrdiff_t>(at),
                        bytes.begin() + static_cast<std::ptrdiff_t>(at + h.size)}});
        at += h.size;
    }
    return out;
}

FormatPayload FloatStereo48k() {
    FormatPayload f;
    f.sample_rate     = 48000;
    f.channels        = 2;
    f.bits_per_sample = 32;
    f.is_float        = 1;
    return f;
}

} // namespace

TEST_CASE("The format goes out before the first audio, and again only when it changes") {
    TapWriter                 writer(1 << 16);
    const auto                f = FloatStereo48k();
    std::vector<std::uint8_t> block(8 * 4, 0x11);
    writer.audio(f, block.data(), 4);
    writer.audio(f, block.data(), 4);
    auto m = Drain(writer);
    REQUIRE(m.size() == 3);
    CHECK(m[0].kind == Kind::Format);
    CHECK(m[1].kind == Kind::Audio);
    CHECK(m[1].payload.size() == 32);
    CHECK(m[2].kind == Kind::Audio);

    auto other        = f;
    other.sample_rate = 44100;
    writer.audio(other, block.data(), 4);
    m = Drain(writer);
    REQUIRE(m.size() == 2);
    CHECK(m[0].kind == Kind::Format);
    FormatPayload got;
    std::memcpy(&got, m[0].payload.data(), sizeof got);
    CHECK(got.sample_rate == 44100);
}

TEST_CASE("A reader that just connected gets the format again") {
    TapWriter                 writer(1 << 16);
    std::vector<std::uint8_t> block(8, 0);
    writer.audio(FloatStereo48k(), block.data(), 1);
    Drain(writer);
    writer.reannounce_format();
    writer.audio(FloatStereo48k(), block.data(), 1);
    const auto m = Drain(writer);
    REQUIRE(m.size() == 2);
    CHECK(m[0].kind == Kind::Format);
}

TEST_CASE("A silent block goes out as the same length of zeros, in whole frames") {
    TapWriter writer(1 << 20);
    writer.audio(FloatStereo48k(), nullptr, 5000); // 40000 bytes: more than one piece
    const auto  m     = Drain(writer);
    std::size_t total = 0;
    for (std::size_t i = 1; i < m.size(); ++i) {
        CHECK(m[i].kind == Kind::Audio);
        CHECK(m[i].payload.size() % 8 == 0);
        for (const auto b : m[i].payload) {
            CHECK(b == 0);
        }
        total += m[i].payload.size();
    }
    CHECK(total == 40000);
}

TEST_CASE("Audio that doesn't fit is dropped and counted, never split") {
    TapWriter                 writer(64);
    std::vector<std::uint8_t> block(8 * 16, 1); // 128 bytes: can't fit
    writer.audio(FloatStereo48k(), block.data(), 16);
    CHECK(writer.dropped_messages() == 1);
    const auto m = Drain(writer);
    REQUIRE(m.size() == 1); // only the format
    CHECK(m[0].kind == Kind::Format);
}

TEST_CASE("Log lines go out as text") {
    TapWriter writer(1024);
    writer.log("hooks installed");
    const auto m = Drain(writer);
    REQUIRE(m.size() == 1);
    CHECK(m[0].kind == Kind::Log);
    CHECK(std::string(m[0].payload.begin(), m[0].payload.end()) == "hooks installed");
}

TEST_CASE("Windows stream formats map to the tap's") {
    WAVEFORMATEXTENSIBLE ext{};
    ext.Format.wFormatTag     = WAVE_FORMAT_EXTENSIBLE;
    ext.Format.nChannels      = 2;
    ext.Format.nSamplesPerSec = 48000;
    ext.Format.wBitsPerSample = 32;
    ext.Format.nBlockAlign    = 8;
    ext.Format.cbSize         = 22;
    ext.SubFormat             = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
    auto f                    = horizon::tap::format_from(&ext.Format);
    REQUIRE(f.has_value());
    CHECK(f->is_float == 1);
    CHECK(f->bits_per_sample == 32);
    CHECK(f->sample_rate == 48000);

    ext.SubFormat             = KSDATAFORMAT_SUBTYPE_PCM;
    ext.Format.wBitsPerSample = 24;
    f                         = horizon::tap::format_from(&ext.Format);
    REQUIRE(f.has_value());
    CHECK(f->is_float == 0);
    CHECK(f->bits_per_sample == 24);

    WAVEFORMATEX pcm8{};
    pcm8.wFormatTag     = WAVE_FORMAT_PCM;
    pcm8.nChannels      = 1;
    pcm8.nSamplesPerSec = 8000;
    pcm8.wBitsPerSample = 8;
    CHECK_FALSE(horizon::tap::format_from(&pcm8).has_value());
    CHECK_FALSE(horizon::tap::format_from(nullptr).has_value());
}

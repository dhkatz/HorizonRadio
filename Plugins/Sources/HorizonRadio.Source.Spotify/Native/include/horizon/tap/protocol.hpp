#pragma once

#include <cstdint>

// The tap's wire format, read by HorizonRadio.Source.Spotify (AudioTapReader.cs). Every message is
// a header followed by `size` bytes of payload, little-endian:
//
//   Format  the stream's sample format; sent before the first audio and whenever it changes
//   Audio   interleaved frames in the last announced format, exactly as the process rendered them
//   Log     a UTF-8 line for the app's Console
//
// Bump kVersion on any change; the reader refuses a version it doesn't know.
namespace horizon::tap {

constexpr std::uint32_t kMagic   = 0x50545A48; // "HZTP"
constexpr std::uint16_t kVersion = 1;

enum class Kind : std::uint16_t {
    Format = 1,
    Audio  = 2,
    Log    = 3,
};

#pragma pack(push, 1)
struct MessageHeader {
    std::uint32_t magic   = kMagic;
    std::uint16_t version = kVersion;
    std::uint16_t kind    = 0;
    std::uint32_t size    = 0;
};

struct FormatPayload {
    std::uint32_t sample_rate     = 0;
    std::uint16_t channels        = 0;
    std::uint16_t bits_per_sample = 0; // container size: 16, 24 or 32
    std::uint16_t is_float        = 0; // 1: IEEE float samples (32-bit)
    std::uint16_t reserved        = 0;
};
#pragma pack(pop)

static_assert(sizeof(MessageHeader) == 12);
static_assert(sizeof(FormatPayload) == 12);

inline bool operator==(const FormatPayload& a, const FormatPayload& b) noexcept {
    return a.sample_rate == b.sample_rate && a.channels == b.channels && a.bits_per_sample == b.bits_per_sample &&
           a.is_float == b.is_float;
}

// Bytes per frame (one sample per channel) in this format.
inline std::uint32_t block_align(const FormatPayload& f) noexcept {
    return static_cast<std::uint32_t>(f.channels) * (f.bits_per_sample / 8U);
}

} // namespace horizon::tap

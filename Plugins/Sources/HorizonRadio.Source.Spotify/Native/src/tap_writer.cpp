#include <windows.h>

// After <windows.h>, which the multimedia headers need first.
#include <algorithm>
#include <array>
#include <cstring>
#include <horizon/tap/tap_writer.hpp>
#include <ks.h>
#include <ksmedia.h>
#include <mmreg.h>

namespace horizon::tap {

std::optional<FormatPayload> format_from(const tWAVEFORMATEX* wave) noexcept {
    if (wave == nullptr || wave->nChannels == 0 || wave->nSamplesPerSec == 0) {
        return std::nullopt;
    }
    bool is_float = wave->wFormatTag == WAVE_FORMAT_IEEE_FLOAT;
    bool is_pcm   = wave->wFormatTag == WAVE_FORMAT_PCM;
    if (wave->wFormatTag == WAVE_FORMAT_EXTENSIBLE && wave->cbSize >= 22) {
        const auto* ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(wave);
        is_float        = IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) != 0;
        is_pcm          = IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_PCM) != 0;
    }
    const std::uint16_t bits = wave->wBitsPerSample;
    if (is_float && bits != 32) {
        return std::nullopt;
    }
    if (is_pcm && bits != 16 && bits != 24 && bits != 32) {
        return std::nullopt;
    }
    if (!is_float && !is_pcm) {
        return std::nullopt;
    }
    FormatPayload f;
    f.sample_rate     = wave->nSamplesPerSec;
    f.channels        = wave->nChannels;
    f.bits_per_sample = bits;
    f.is_float        = is_float ? 1 : 0;
    return f;
}

bool TapWriter::push(Kind kind, const std::uint8_t* payload, std::size_t size) {
    MessageHeader header;
    header.kind = static_cast<std::uint16_t>(kind);
    header.size = static_cast<std::uint32_t>(size);
    if (ring_.push2(reinterpret_cast<const std::uint8_t*>(&header), sizeof header, payload, size)) {
        return true;
    }
    dropped_.fetch_add(1, std::memory_order_relaxed);
    return false;
}

void TapWriter::audio(const FormatPayload& format, const std::uint8_t* data, std::uint32_t frames) {
    const std::size_t bytes = static_cast<std::size_t>(frames) * block_align(format);
    if (bytes == 0) {
        return;
    }
    const std::scoped_lock lock(write_lock_);
    const bool             reannounce = reannounce_.exchange(false, std::memory_order_acq_rel);
    if (reannounce || !announced_ || !(*announced_ == format)) {
        if (!push(Kind::Format, reinterpret_cast<const std::uint8_t*>(&format), sizeof format)) {
            // Audio can't go out ahead of its format; try again with the next block.
            reannounce_.store(true, std::memory_order_release);
            return;
        }
        announced_ = format;
    }
    if (data != nullptr) {
        push(Kind::Audio, data, bytes);
        return;
    }
    // A silent block: the same number of zero bytes, in pieces.
    static constexpr std::size_t                  kChunk = 16384;
    static const std::array<std::uint8_t, kChunk> zeros{};
    MessageHeader                                 header;
    header.kind = static_cast<std::uint16_t>(Kind::Audio);
    for (std::size_t left = bytes; left > 0;) {
        // Whole frames per piece, so the reader never sees a split frame.
        const std::size_t align = block_align(format);
        const std::size_t n     = std::min(left, kChunk / align * align);
        header.size             = static_cast<std::uint32_t>(n);
        if (!ring_.push2(reinterpret_cast<const std::uint8_t*>(&header), sizeof header, zeros.data(), n)) {
            dropped_.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        left -= n;
    }
}

void TapWriter::log(std::string_view line) {
    const std::scoped_lock lock(write_lock_);
    push(Kind::Log, reinterpret_cast<const std::uint8_t*>(line.data()), line.size());
}

} // namespace horizon::tap

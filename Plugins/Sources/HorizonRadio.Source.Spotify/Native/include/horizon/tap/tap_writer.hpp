#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <horizon/tap/byte_ring.hpp>
#include <horizon/tap/protocol.hpp>
#include <mutex>
#include <optional>
#include <string_view>

struct tWAVEFORMATEX;

namespace horizon::tap {

// The tap's format for a Windows stream format, or nothing for one it can't describe (compressed,
// 8-bit, or no channels).
std::optional<FormatPayload> format_from(const tWAVEFORMATEX* wave) noexcept;

// Frames what the hooked process renders into messages on a ring the pipe thread drains. Called
// from the process's audio thread, so it never blocks on the pipe: a message that doesn't fit is
// dropped and counted.
class TapWriter {
public:
    explicit TapWriter(std::size_t ring_capacity) : ring_(ring_capacity) {}

    // One rendered block. `data` is null for a block the process marked silent: zeros go out in
    // its place, so the stream keeps its timing.
    void audio(const FormatPayload& format, const std::uint8_t* data, std::uint32_t frames);

    void log(std::string_view line);

    // The next block announces its format again (a reader that just connected needs it).
    void reannounce_format() noexcept {
        reannounce_.store(true, std::memory_order_release);
    }

    ByteRing& ring() noexcept {
        return ring_;
    }

    std::uint64_t dropped_messages() const noexcept {
        return dropped_.load(std::memory_order_relaxed);
    }

private:
    bool push(Kind kind, const std::uint8_t* payload, std::size_t size);

    ByteRing                     ring_;
    std::mutex                   write_lock_;
    std::optional<FormatPayload> announced_;
    std::atomic<bool>            reannounce_{false};
    std::atomic<std::uint64_t>   dropped_{0};
};

} // namespace horizon::tap

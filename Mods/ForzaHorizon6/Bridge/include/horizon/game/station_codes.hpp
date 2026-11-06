#pragma once

#include <array>
#include <string_view>
#include <utility>

namespace horizon::game {

// Which station a radio metadata block belongs to, read from the game's own
// event id at block+0x10 ("HZ6_R6_10FEET_DaiZeroKan" -> station code "R6").
// We never write that field, so it identifies a block even after we replaced
// its title. Mapped live on the November 2026 Steam build with the "titles"
// diagnostic (Console); re-check it there if titles misbehave after an update.
// Streamer Mode has no code of its own that we've seen, so it resolves to ""
// (unknown) and the title writer falls back to waiting for the block to settle.
inline constexpr std::string_view kEventIdPrefix = "HZ6_";

inline constexpr std::array<std::pair<std::string_view, std::string_view>, 9> kFh6StationCodes = {{
    {"R1", "Horizon Pulse"},
    {"R2", "Horizon Bass Arena"},
    {"R3", "Horizon Block Party"},
    {"R4", "Horizon XS"},
    {"R5", "Hospital Records"},
    {"R6", "Gacha City Radio"},
    {"R7", "Sub Pop Records"},
    {"R8", "Horizon Wave"},
    {"R9", "Horizon Opus"},
}};

// True if `station` has a code in the table (its blocks can be identified).
[[nodiscard]] constexpr bool station_has_code(std::string_view station) noexcept {
    for (const auto& entry : kFh6StationCodes) {
        if (entry.second == station)
            return true;
    }
    return false;
}

// The station `event_id` belongs to, or "" when it carries no code we know.
[[nodiscard]] constexpr std::string_view station_for_event_id(std::string_view event_id) noexcept {
    if (!event_id.starts_with(kEventIdPrefix))
        return {};
    const auto rest = event_id.substr(kEventIdPrefix.size());
    const auto end  = rest.find('_');
    if (end == std::string_view::npos)
        return {};
    const auto code = rest.substr(0, end);
    for (const auto& [c, station] : kFh6StationCodes) {
        if (c == code)
            return station;
    }
    return {};
}

} // namespace horizon::game

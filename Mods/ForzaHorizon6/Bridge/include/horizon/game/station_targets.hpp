#pragma once

#include <algorithm>
#include <string>
#include <string_view>
#include <vector>

namespace horizon::game {

// Which in-game stations Horizon Radio replaces. Starts at "any station" so a UI
// that predates station mapping (it only ever sends set_target_station) keeps
// today's behaviour. Not thread-safe; dllmain guards it with g_target_mutex.
class StationTargets {
public:
    // Legacy single target (set_target_station): one station, or "" / "*" for any.
    void set_single(std::string_view station) {
        stations_.clear();
        any_ = station.empty() || station == "*";
        if (!any_)
            stations_.emplace_back(station);
    }

    // A set of stations (set_target_stations), newline-separated: station names
    // never contain newlines, so the existing flat string parser can carry a
    // list. Empty means none: every station plays the game's own music.
    void set_list(std::string_view newline_separated) {
        any_ = false;
        stations_.clear();
        std::size_t start = 0;
        while (start <= newline_separated.size()) {
            const auto end = newline_separated.find('\n', start);
            const auto name =
                newline_separated.substr(start, end == std::string_view::npos ? std::string_view::npos : end - start);
            if (!name.empty())
                stations_.emplace_back(name);
            if (end == std::string_view::npos)
                break;
            start = end + 1;
        }
    }

    [[nodiscard]] bool targets(std::string_view station) const {
        return any_ || std::ranges::find(stations_, station) != stations_.end();
    }

    [[nodiscard]] bool any() const noexcept {
        return any_;
    }

    [[nodiscard]] const std::vector<std::string>& stations() const noexcept {
        return stations_;
    }

private:
    bool                     any_ = true;
    std::vector<std::string> stations_;
};

} // namespace horizon::game

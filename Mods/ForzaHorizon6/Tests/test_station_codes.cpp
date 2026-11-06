#include <doctest/doctest.h>
#include <horizon/game/station_codes.hpp>
#include <ostream>
#include <string_view>

using horizon::game::station_for_event_id;

// Event ids as seen live (November 2026 Steam build).
TEST_CASE("station_for_event_id: maps the game's station codes") {
    CHECK(station_for_event_id("HZ6_R1_BLONDISH_SelfLove") == "Horizon Pulse");
    CHECK(station_for_event_id("HZ6_R5_AnaisxHayve_Redline") == "Hospital Records");
    CHECK(station_for_event_id("HZ6_R6_10FEET_DaiZeroKan") == "Gacha City Radio");
    CHECK(station_for_event_id("HZ6_R7_Corridor_Domino") == "Sub Pop Records");
    CHECK(station_for_event_id("HZ6_R9_OlafurArnalds_ekkihugsa") == "Horizon Opus");
}

TEST_CASE("station_for_event_id: anything else is unknown") {
    CHECK(station_for_event_id("").empty());
    CHECK(station_for_event_id("HZ6_R10_Someone_Song").empty()); // no such code (yet)
    CHECK(station_for_event_id("HZ6_R6").empty());               // no track part
    CHECK(station_for_event_id("FH5_R6_Someone_Song").empty());  // other game
    CHECK(station_for_event_id("HZ6_DJ_Intro").empty());
}

TEST_CASE("station_has_code: stations without a code fall back to waiting") {
    using horizon::game::station_has_code;
    CHECK(station_has_code("Gacha City Radio"));
    CHECK(station_has_code("Horizon Opus"));
    CHECK_FALSE(station_has_code("Streamer Mode"));
    CHECK_FALSE(station_has_code(""));
}

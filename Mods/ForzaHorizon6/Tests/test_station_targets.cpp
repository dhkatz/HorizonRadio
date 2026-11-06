#include <doctest/doctest.h>
#include <horizon/game/station_targets.hpp>

using horizon::game::StationTargets;

TEST_CASE("StationTargets: defaults to any station") {
    const StationTargets t;
    CHECK(t.any());
    CHECK(t.targets("Horizon XS"));
}

TEST_CASE("StationTargets: legacy single target") {
    StationTargets t;
    t.set_single("Horizon Pulse");
    CHECK_FALSE(t.any());
    CHECK(t.targets("Horizon Pulse"));
    CHECK_FALSE(t.targets("Horizon XS"));

    t.set_single("*");
    CHECK(t.targets("Horizon XS"));
    t.set_single("");
    CHECK(t.targets("Horizon XS"));
}

TEST_CASE("StationTargets: newline-separated list") {
    StationTargets t;
    t.set_list("Horizon XS\nHorizon Pulse");
    CHECK_FALSE(t.any());
    CHECK(t.targets("Horizon XS"));
    CHECK(t.targets("Horizon Pulse"));
    CHECK_FALSE(t.targets("Horizon Wave"));
    CHECK(t.stations().size() == 2);
}

TEST_CASE("StationTargets: empty list replaces nothing") {
    StationTargets t;
    t.set_list("");
    CHECK_FALSE(t.any());
    CHECK_FALSE(t.targets("Horizon XS"));
    CHECK(t.stations().empty());
}

TEST_CASE("StationTargets: blank lines are ignored") {
    StationTargets t;
    t.set_list("\nHorizon XS\n\n");
    CHECK(t.stations().size() == 1);
    CHECK(t.targets("Horizon XS"));
}

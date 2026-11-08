#include <doctest/doctest.h>
#include <horizon/game/now_playing.hpp>
#include <string>

using horizon::game::NowPlaying;
using horizon::game::TrackInfo;

namespace {
TrackInfo song() {
    return TrackInfo{.id = "spotify:track:abc", .title = "Melt", .artist = "ryo", .album = "Supercell"};
}
} // namespace

TEST_CASE("NowPlaying: loads with no track, titles shown") {
    const NowPlaying np;
    const auto       s = np.state();
    CHECK_FALSE(s.have_track);
    CHECK(s.show_title);
    CHECK(s.track.title.empty());
}

TEST_CASE("NowPlaying: set_track stores the track and shows it") {
    NowPlaying np;
    const auto stored = np.set_track(song());
    CHECK(stored.title == "Melt");

    const auto s = np.state();
    CHECK(s.have_track);
    CHECK(s.show_title);
    CHECK(s.track.id == "spotify:track:abc");
    CHECK(s.track.artist == "ryo");
}

TEST_CASE("NowPlaying: clear_track hides the title but keeps the audio") {
    NowPlaying np;
    np.set_track(song());
    np.clear_track();

    const auto s = np.state();
    CHECK(s.have_track); // the DSP stays attached
    CHECK_FALSE(s.show_title);
    CHECK(s.track.title.empty());

    np.set_track(song());
    CHECK(np.state().show_title);
}

TEST_CASE("NowPlaying: the first PCM chunk arms a placeholder while the app is connected") {
    NowPlaying np;
    CHECK(np.arm_from_pcm([] { return true; }));
    const auto s = np.state();
    CHECK(s.have_track);
    CHECK(s.show_title);
    CHECK(s.track.title == NowPlaying::kPlaceholderTitle);

    // Already armed: later chunks change nothing.
    CHECK_FALSE(np.arm_from_pcm([] { return true; }));
}

TEST_CASE("NowPlaying: PCM doesn't overwrite a real track") {
    NowPlaying np;
    np.set_track(song());
    CHECK_FALSE(np.arm_from_pcm([] { return true; }));
    CHECK(np.state().track.title == "Melt");
}

TEST_CASE("NowPlaying: disconnect gives the stations back to the game") {
    NowPlaying np;
    np.set_track(song());
    np.disconnect();

    const auto s = np.state();
    CHECK_FALSE(s.have_track); // the DSP detaches, the game's title comes back
    CHECK(s.show_title);
    CHECK(s.track.title.empty());
}

TEST_CASE("NowPlaying: disconnect after clear_track is back to the load state") {
    NowPlaying np;
    np.set_track(song());
    np.clear_track();
    np.disconnect();

    const auto s = np.state();
    CHECK_FALSE(s.have_track);
    CHECK(s.show_title); // the next app's placeholder shows again
}

TEST_CASE("NowPlaying: audio still draining after the app left doesn't re-arm") {
    NowPlaying np;
    np.set_track(song());
    np.disconnect();

    // The PCM pipe still holds a few hundred ms of the old app's audio.
    CHECK_FALSE(np.arm_from_pcm([] { return false; }));
    CHECK_FALSE(np.state().have_track);

    // A new app connects: its audio arms the placeholder again.
    CHECK(np.arm_from_pcm([] { return true; }));
    CHECK(np.state().track.title == NowPlaying::kPlaceholderTitle);
}

TEST_CASE("NowPlaying: only asks about the connection when not yet armed") {
    NowPlaying np;
    int        asked = 0;
    np.set_track(song());
    CHECK_FALSE(np.arm_from_pcm([&] {
        ++asked;
        return true;
    }));
    CHECK(asked == 0); // armed already: no need to ask

    np.disconnect();
    CHECK_FALSE(np.arm_from_pcm([&] {
        ++asked;
        return false;
    }));
    CHECK(asked == 1);
}

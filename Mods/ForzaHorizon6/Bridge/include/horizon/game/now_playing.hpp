#pragma once

#include <mutex>
#include <string>
#include <utility>

namespace horizon::game {

// Track info the UI sends via {"cmd":"set_track"}.
struct TrackInfo {
    std::string id; // canonical id from the source (e.g. "spotify:track:abc"); blank for local
    std::string title;
    std::string artist;
    std::string album;
};

// What the app is playing into the game, as the mod knows it. dllmain keeps one:
// the IPC thread changes it (set_track, clear_track, the app disconnecting), the
// PCM thread arms a placeholder, and the periodic writer reads it every tick.
// Every change happens under one lock, so the writer never sees half of one
// (say, a cleared title still marked as shown).
class NowPlaying {
public:
    // Shown until the app's first set_track arrives.
    static constexpr const char* kPlaceholderTitle = "Horizon Radio";

    struct State {
        bool      have_track = false; // app audio worth playing: the DSP attaches
        bool      show_title = true;  // write our title (false: put the game's back)
        TrackInfo track;
    };

    // set_track: our audio, with this title shown. Returns what's now stored.
    TrackInfo set_track(TrackInfo track) {
        const std::scoped_lock lock(mutex_);
        state_.track      = std::move(track);
        state_.have_track = true;
        state_.show_title = true;
        return state_.track;
    }

    // clear_track: nothing plays on the channel the app routed to. The game's
    // own title comes back; the audio is unaffected.
    void clear_track() {
        const std::scoped_lock lock(mutex_);
        state_.track      = TrackInfo{};
        state_.show_title = false;
    }

    // The app's first PCM chunk plants a placeholder track, so the DSP attaches
    // before real metadata arrives. Only while `app_connected()` holds: the PCM
    // pipe can keep draining buffered audio after the app has gone, and that
    // must not bring back what disconnect() cleared. The check runs under the
    // lock, and the IPC server reports "disconnected" before it calls
    // disconnect(), so the two can't interleave into a stale track. Returns true
    // when it armed.
    template <class AppConnected> bool arm_from_pcm(const AppConnected& app_connected) {
        const std::scoped_lock lock(mutex_);
        if (state_.have_track || !app_connected())
            return false;
        if (state_.track.title.empty())
            state_.track.title = kPlaceholderTitle;
        state_.have_track = true;
        return true;
    }

    // The app closed or crashed: back to the state the mod loads in. With no
    // track the DSP detaches and the game's own title comes back, so the
    // stations we replaced play the game's music again.
    void disconnect() {
        const std::scoped_lock lock(mutex_);
        state_ = State{};
    }

    [[nodiscard]] State state() const {
        const std::scoped_lock lock(mutex_);
        return state_;
    }

private:
    mutable std::mutex mutex_;
    State              state_;
};

} // namespace horizon::game

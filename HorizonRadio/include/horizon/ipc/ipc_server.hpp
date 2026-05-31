#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <thread>

namespace horizon::ipc {

// IPC server exposed to the HorizonRadio.UI desktop app via a Windows
// named pipe. Single connection at a time (the UI is the only client).
//
// Wire format: newline-delimited UTF-8 JSON. The DLL is the publisher;
// commands from the UI back to the DLL will come later. Each event
// has an `"event"` field that names the kind. See the README in the
// UI project for the full schema; the short version:
//
//   {"event":"hello","pid":N,"version":"x.y.z"}
//   {"event":"track","title":"...","artist":"...","album":null,
//                    "source_id":"local","source_display":"Local Files",
//                    "art_b64":null}
//   {"event":"stats","installed":true,"frames_in":N,"frames_out":N,
//                    "underruns":N,"normalizer_gain":1.0,"limiter_gain":1.0}
//   {"event":"source_changed","id":"spotify","display":"Spotify Connect"}
//
// All publish() methods are safe to call from any thread; they buffer
// to an internal outbox and the pipe-writer thread drains it.
class IpcServer {
public:
    // Pipe name without the `\\.\pipe\` prefix. Defaults are fine.
    static constexpr wchar_t kDefaultPipeName[] = L"HorizonRadio";

    IpcServer();
    ~IpcServer();

    // Start the listener thread. Idempotent; calling twice is a no-op.
    void start();

    // Signal the thread to stop and wait for it to exit. The pipe
    // handles are closed; an attached UI sees the pipe disconnect.
    void stop();

    // True while a UI client is connected. Used by publishers to
    // skip the JSON-formatting cost when nobody's listening.
    bool connected() const noexcept {
        return client_connected_.load(std::memory_order_acquire);
    }

    // ----- Event publishers (cheap when disconnected) ---------------

    struct TrackEvent {
        std::string         title;
        std::string         artist;
        std::string         album;     // may be empty
        std::string         source_id; // "local", "spotify", ...
        std::string         source_display;
        const std::uint8_t* art_bytes = nullptr; // optional, base64-encoded for transport
        std::size_t         art_size  = 0;
    };

    struct StatsEvent {
        bool          installed;
        std::uint64_t frames_in;
        std::uint64_t frames_out;
        std::uint64_t underruns;
        float         normalizer_gain;
        float         limiter_gain;
    };

    void publish_track(const TrackEvent& e);
    void publish_stats(const StatsEvent& e);
    void publish_source_changed(std::string_view id, std::string_view display);

    // Publish a detected in-game event (race_start, race_finish,
    // race_restart, station_changed, radio_on, radio_off, …). The C# host
    // maps it to a user-configured action. Cheap when disconnected.
    void publish_game_event(std::string_view kind);

    // Publish a free-form debug line that the UI surfaces in its Console
    // tab under `tag`. Used by the state-watch to stream RadioState byte
    // diffs for offset reverse-engineering. Cheap when disconnected.
    void publish_debug(std::string_view tag, std::string_view text);

    // Snapshot callback fires whenever a UI client (re)connects. The
    // callback is expected to re-publish the latest track + active
    // source via the publish_* methods so the UI doesn't have to wait
    // for the next track change to display something. Without this,
    // the UI starts at "Nothing playing" any time the user opens it
    // after FH6 has already loaded a track.
    using SnapshotFn = std::function<void()>;
    void set_snapshot_callback(SnapshotFn cb);

    // Command callback fires for each newline-terminated JSON object the
    // UI sends back to the DLL. The callback receives the raw line
    // (without trailing newline). Used today for "set_track" commands
    // that route C#-side metadata into the game HUD; future commands
    // (pause, source-switch hints, etc.) ride the same channel.
    //
    // Called from the listener thread; callbacks should be cheap or
    // hand work off to another thread.
    using CommandFn = std::function<void(const std::string& json_line)>;
    void set_command_callback(CommandFn cb);

private:
    void run();
    void send_line_locked(const std::string& line);
    void send_hello();

    std::atomic<bool> running_{false};
    std::atomic<bool> client_connected_{false};
    std::atomic<bool> pipe_broken_{false};
    std::thread       thread_;

    // Pipe handle is touched from the listener thread (server side)
    // and from publish_* (any thread). The mutex serializes WriteFile
    // calls so concurrent publishes don't interleave JSON lines.
    std::mutex pipe_mutex_;
    void*      pipe_handle_ = nullptr; // HANDLE

    std::mutex snapshot_mutex_;
    SnapshotFn snapshot_cb_;

    std::mutex command_mutex_;
    CommandFn  command_cb_;
};

} // namespace horizon::ipc

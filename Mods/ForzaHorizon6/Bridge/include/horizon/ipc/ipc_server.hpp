#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <thread>

namespace horizon::ipc {

// IPC server exposed to the HorizonRadio.UI desktop app via a Windows named
// pipe; single connection (the UI is the only client). All publish() methods
// are thread-safe and cheap when disconnected. Wire protocol (event/command
// schemas): docs/architecture.md -> "IPC wire protocol".
class IpcServer {
public:
    // Pipe name without the `\\.\pipe\` prefix. Defaults are fine.
    static constexpr wchar_t kDefaultPipeName[] = L"HorizonRadio";

    IpcServer() noexcept;
    ~IpcServer();

    // Owns a worker thread + pipe handle; not copyable or movable.
    IpcServer(const IpcServer&)            = delete;
    IpcServer& operator=(const IpcServer&) = delete;
    IpcServer(IpcServer&&)                 = delete;
    IpcServer& operator=(IpcServer&&)      = delete;

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

    // A detected in-game event (race_start, station_changed, …) the C# host
    // maps to a user-configured action.
    void publish_game_event(std::string_view kind);

    // Free-form debug line surfaced in the UI Console under `tag`.
    void publish_debug(std::string_view tag, std::string_view text);

    // Fires on every UI (re)connect; re-publishes current track + source so the
    // UI doesn't sit on its placeholder when attaching mid-playback.
    using SnapshotFn = std::function<void()>;
    void set_snapshot_callback(SnapshotFn cb);

    // Fires for each newline-terminated JSON command from the UI (raw line, no
    // trailing newline). Runs on the listener thread, so keep it cheap.
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

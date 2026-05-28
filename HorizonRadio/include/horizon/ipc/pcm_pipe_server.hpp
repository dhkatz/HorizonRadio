#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <mutex>
#include <thread>

namespace horizon::ipc {

// PCM ingress pipe. The UI process (HorizonRadio.UI / Core) writes
// s16 interleaved stereo PCM at 44.1 kHz to `\\.\pipe\HorizonRadio.pcm`
// and we hand each frame straight to the FMOD bridge's push_pcm.
//
// Separate from the control pipe (`\\.\pipe\HorizonRadio`) on purpose:
// the control pipe is newline-delimited JSON, mixing raw binary into it
// would break the line-protocol the rest of the IPC depends on. A
// second pipe is also one fewer thing-to-think-about — readers + writers
// don't share a handle with the events path so the sync-IO deadlock
// we documented for the control pipe can't bite here either.
//
// One client at a time; the client end auto-reconnects.
class PcmPipeServer {
public:
    static constexpr wchar_t kPipeName[] = L"HorizonRadio.pcm";

    // Callback receives raw s16-stereo PCM frames. Caller-owned buffer,
    // valid only for the duration of the call. frame_count is the number
    // of *stereo frames* (not samples), so byte count = 4 * frame_count.
    using PcmCallback = std::function<void(const std::int16_t* frames, std::size_t frame_count)>;

    PcmPipeServer();
    ~PcmPipeServer();

    void start(PcmCallback on_pcm);
    void stop();

    bool client_connected() const noexcept {
        return client_connected_.load(std::memory_order_acquire);
    }

private:
    void run();

    std::atomic<bool>  running_{false};
    std::atomic<bool>  client_connected_{false};
    std::thread        thread_;
    std::mutex         handle_mutex_;
    void*              pipe_handle_ = nullptr;   // HANDLE
    PcmCallback        on_pcm_;
};

} // namespace horizon::ipc

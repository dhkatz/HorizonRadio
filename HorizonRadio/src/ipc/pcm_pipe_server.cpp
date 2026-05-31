#include <algorithm>
#include <chrono>
#include <cstdint>
#include <horizon/ipc/pcm_pipe_server.hpp>
#include <string>
#include <thread>
#include <windows.h>

namespace horizon::ipc {

namespace {

constexpr DWORD kInBufferSize  = 64 * 1024; // ~370 ms of stereo s16 @ 44.1 kHz
constexpr DWORD kOutBufferSize = 4 * 1024;  // we don't write back yet

std::wstring full_pipe_path() {
    std::wstring p = L"\\\\.\\pipe\\";
    p.append(PcmPipeServer::kPipeName);
    return p;
}

} // namespace

PcmPipeServer::PcmPipeServer() = default;

PcmPipeServer::~PcmPipeServer() {
    stop();
}

void PcmPipeServer::start(pcm_callback on_pcm) {
    bool expected = false;
    if (!running_.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) {
        return;
    }
    on_pcm_ = std::move(on_pcm);
    thread_ = std::thread([this] { run(); });
}

void PcmPipeServer::stop() {
    if (!running_.exchange(false, std::memory_order_acq_rel))
        return;
    {
        std::lock_guard<std::mutex> lock(handle_mutex_);
        if (pipe_handle_ != nullptr && pipe_handle_ != INVALID_HANDLE_VALUE) {
            DisconnectNamedPipe(pipe_handle_);
            CloseHandle(pipe_handle_);
            pipe_handle_ = nullptr;
        }
    }
    if (thread_.joinable())
        thread_.join();
}

void PcmPipeServer::run() {
    OutputDebugStringW(L"[horizon-radio] pcm-pipe: server thread started\n");

    // Read s16 frames in chunks of ~46 ms (matches the existing
    // LocalFileSource read cadence). The client paces; we just consume.
    constexpr std::size_t kChunkFrames = 2048;
    constexpr std::size_t kChunkBytes  = kChunkFrames * 2 /*ch*/ * sizeof(std::int16_t);
    std::int16_t          buf[kChunkFrames * 2];

    while (running_.load(std::memory_order_acquire)) {
        const auto path = full_pipe_path();
        HANDLE     h    = CreateNamedPipeW(path.c_str(),
                                           PIPE_ACCESS_INBOUND, // server reads only
                                           PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, kOutBufferSize, kInBufferSize,
                                           0, nullptr);

        if (h == INVALID_HANDLE_VALUE) {
            std::this_thread::sleep_for(std::chrono::seconds(1));
            continue;
        }
        {
            std::lock_guard<std::mutex> lock(handle_mutex_);
            pipe_handle_ = h;
        }

        BOOL        ok  = ConnectNamedPipe(h, nullptr);
        const DWORD err = GetLastError();
        if (!ok && err != ERROR_PIPE_CONNECTED) {
            std::lock_guard<std::mutex> lock(handle_mutex_);
            if (pipe_handle_ == h) {
                CloseHandle(h);
                pipe_handle_ = nullptr;
            }
            if (!running_.load(std::memory_order_acquire))
                break;
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
            continue;
        }

        client_connected_.store(true, std::memory_order_release);
        OutputDebugStringW(L"[horizon-radio] pcm-pipe: client connected\n");

        // Drain loop. ReadFile blocks until kChunkBytes bytes accumulate
        // OR the pipe closes. The client should always flush at chunk
        // boundaries; if it doesn't, partial reads are still safe — we
        // re-call ReadFile for the rest.
        std::size_t pending  = 0;
        auto*       byte_buf = reinterpret_cast<std::uint8_t*>(buf);
        while (running_.load(std::memory_order_acquire)) {
            DWORD       got  = 0;
            const DWORD want = static_cast<DWORD>(kChunkBytes - pending);
            if (!ReadFile(h, byte_buf + pending, want, &got, nullptr) || got == 0) {
                break; // client disconnected
            }
            pending += got;
            if (pending < kChunkBytes)
                continue; // partial; keep filling
            if (on_pcm_)
                on_pcm_(buf, kChunkFrames);
            pending = 0;
        }

        client_connected_.store(false, std::memory_order_release);
        {
            std::lock_guard<std::mutex> lock(handle_mutex_);
            if (pipe_handle_ == h) {
                DisconnectNamedPipe(h);
                CloseHandle(h);
                pipe_handle_ = nullptr;
            }
        }
        OutputDebugStringW(L"[horizon-radio] pcm-pipe: client disconnected\n");
    }

    OutputDebugStringW(L"[horizon-radio] pcm-pipe: server thread exiting\n");
}

} // namespace horizon::ipc

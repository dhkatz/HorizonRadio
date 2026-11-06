#include <chrono>
#include <cstdio>
#include <horizon/ipc/ipc_server.hpp>
#include <process.h>
#include <sstream>
#include <string>
#include <thread>
#include <utility>
#include <windows.h>

namespace horizon::ipc {
namespace {
std::wstring full_pipe_path() {
    std::wstring path = L"\\\\.\\pipe\\";
    path.append(IpcServer::kDefaultPipeName);
    return path;
}

std::string json_escape(std::string_view s) {
    std::string out;
    out.reserve(s.size() + 2);
    out.push_back('"');
    for (const unsigned char c : s) {
        switch (c) {
        case '"':
            out.append("\\\"");
            break;
        case '\\':
            out.append("\\\\");
            break;
        case '\b':
            out.append("\\b");
            break;
        case '\f':
            out.append("\\f");
            break;
        case '\n':
            out.append("\\n");
            break;
        case '\r':
            out.append("\\r");
            break;
        case '\t':
            out.append("\\t");
            break;
        default:
            if (c < 0x20) {
                char buf[8];
                std::snprintf(buf, sizeof(buf), "\\u%04x", c);
                out.append(buf);
            } else {
                out.push_back(static_cast<char>(c));
            }
        }
    }
    out.push_back('"');
    return out;
}

// Base64 over the std alphabet, no padding stripped — strings are
// short enough that the cost is irrelevant. Used for album-art bytes.
std::string base64_encode(const std::uint8_t* data, std::size_t len) {
    static constexpr char alpha[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string           out;
    out.reserve(((len + 2) / 3) * 4);
    for (std::size_t i = 0; i < len; i += 3) {
        std::uint32_t v = static_cast<std::uint32_t>(data[i]) << 16;
        if (i + 1 < len)
            v |= static_cast<std::uint32_t>(data[i + 1]) << 8;
        if (i + 2 < len)
            v |= static_cast<std::uint32_t>(data[i + 2]);
        out.push_back(alpha[(v >> 18) & 0x3F]);
        out.push_back(alpha[(v >> 12) & 0x3F]);
        out.push_back(i + 1 < len ? alpha[(v >> 6) & 0x3F] : '=');
        out.push_back(i + 2 < len ? alpha[v & 0x3F] : '=');
    }
    return out;
}
} // namespace

IpcServer::IpcServer() noexcept = default;
IpcServer::~IpcServer() {
    stop();
}

void IpcServer::start() {
    bool expected = false;
    if (!running_.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) {
        return;
    }
    thread_ = std::thread([this] { run(); });
}

void IpcServer::stop() {
    if (!running_.exchange(false, std::memory_order_acq_rel))
        return;

    {
        const std::scoped_lock lock(pipe_mutex_);
        if (pipe_handle_ != nullptr && pipe_handle_ != INVALID_HANDLE_VALUE) {
            DisconnectNamedPipe(pipe_handle_);
            CloseHandle(pipe_handle_);
            pipe_handle_ = nullptr;
        }
    }

    if (thread_.joinable())
        thread_.join();
}

void IpcServer::run() {
    OutputDebugStringW(L"[horizon-radio] ipc: server thread started\n");

    while (running_.load(std::memory_order_acquire)) {
        const auto   path = full_pipe_path();
        const HANDLE h    = CreateNamedPipeW(path.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                                             PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                                             /*nMaxInstances*/ 1,
                                             /*nOutBufferSize*/ 64 * 1024,
                                             /*nInBufferSize*/ 4 * 1024,
                                             /*nDefaultTimeOut*/ 0,
                                             /*lpSecurityAttrs*/ nullptr);

        if (h == INVALID_HANDLE_VALUE) {
            std::this_thread::sleep_for(std::chrono::seconds(1));
            continue;
        }

        {
            const std::scoped_lock lock(pipe_mutex_);
            pipe_handle_ = h;
        }

        const HANDLE connect_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        OVERLAPPED   connect_ov{};
        connect_ov.hEvent    = connect_event;
        const auto ok        = ConnectNamedPipe(h, &connect_ov);
        const auto err       = GetLastError();
        bool       connected = false;
        if (ok || err == ERROR_PIPE_CONNECTED) {
            connected = true;
        } else if (err == ERROR_IO_PENDING) {
            while (running_.load(std::memory_order_acquire)) {
                const DWORD w = WaitForSingleObject(connect_event, 200);
                if (w == WAIT_OBJECT_0) {
                    connected = true;
                    break;
                }
                if (w == WAIT_TIMEOUT)
                    continue;
                break;
            }
        }
        CloseHandle(connect_event);

        if (!connected) {
            const std::scoped_lock lock(pipe_mutex_);
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
        pipe_broken_.store(false, std::memory_order_release);
        OutputDebugStringW(L"[horizon-radio] ipc: client connected\n");
        send_hello();

        SnapshotFn cb;
        {
            const std::scoped_lock lock(snapshot_mutex_);
            cb = snapshot_cb_;
        }
        if (cb)
            cb();

        const auto read_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        OVERLAPPED read_ov{};
        read_ov.hEvent = read_event;
        char        read_buf[2048];
        std::string accum;
        bool        read_pending = false;

        while (running_.load(std::memory_order_acquire) && !pipe_broken_.load(std::memory_order_acquire)) {
            if (!read_pending) {
                DWORD got = 0;
                ResetEvent(read_event);
                if (ReadFile(h, read_buf, sizeof(read_buf), &got, &read_ov)) {
                    accum.append(read_buf, got);
                } else {
                    if (const auto r_err = GetLastError(); r_err == ERROR_IO_PENDING) {
                        read_pending = true;
                    } else {
                        break;
                    }
                }
            }
            if (read_pending) {
                if (const auto w = WaitForSingleObject(read_event, 500); w == WAIT_OBJECT_0) {
                    DWORD      got = 0;
                    const BOOL gor = GetOverlappedResult(h, &read_ov, &got, FALSE);
                    if (!gor || got == 0) {
                        // EOF / disconnect.
                        break;
                    }
                    accum.append(read_buf, got);
                    read_pending = false;
                } else if (w == WAIT_TIMEOUT) {
                    // Heartbeat: nothing to read; check whether the
                    // peer is still there before looping.
                    DWORD avail = 0;
                    if (!PeekNamedPipe(h, nullptr, 0, nullptr, &avail, nullptr)) {
                        CancelIoEx(h, &read_ov);
                        break;
                    }
                    continue;
                } else {
                    CancelIoEx(h, &read_ov);
                    break;
                }
            }

            // Dispatch any complete lines in the accumulator.
            for (;;) {
                const auto nl = accum.find('\n');
                if (nl == std::string::npos)
                    break;
                std::string line = accum.substr(0, nl);
                accum.erase(0, nl + 1);
                while (!line.empty() && (line.back() == '\r'))
                    line.pop_back();
                if (line.empty())
                    continue;

                CommandFn cb;
                {
                    const std::scoped_lock lock(command_mutex_);
                    cb = command_cb_;
                }
                if (cb) {
                    try {
                        cb(line);
                    } catch (...) {
                        // One bad command shouldn't tear down IPC; log and carry on.
                        OutputDebugStringW(L"[horizon-radio] ipc: command handler threw; ignoring\n");
                    }
                }
            }
        }
        if (read_pending)
            CancelIoEx(h, &read_ov);
        CloseHandle(read_event);

        client_connected_.store(false, std::memory_order_release);
        {
            const std::scoped_lock lock(pipe_mutex_);
            if (pipe_handle_ == h) {
                DisconnectNamedPipe(h);
                CloseHandle(h);
                pipe_handle_ = nullptr;
            }
        }
        OutputDebugStringW(L"[horizon-radio] ipc: client disconnected\n");
    }

    OutputDebugStringW(L"[horizon-radio] ipc: server thread exiting\n");
}

void IpcServer::send_line_locked(const std::string& line) {
    // Caller holds pipe_mutex_.
    if (pipe_handle_ == nullptr || pipe_handle_ == INVALID_HANDLE_VALUE)
        return;

    HANDLE     ev = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    OVERLAPPED ov{};
    ov.hEvent     = ev;
    DWORD written = 0;
    BOOL  ok      = WriteFile(pipe_handle_, line.data(), static_cast<DWORD>(line.size()), &written, &ov);
    if (!ok && GetLastError() == ERROR_IO_PENDING) {
        ok = GetOverlappedResult(pipe_handle_, &ov, &written, TRUE);
    }
    CloseHandle(ev);

    if (!ok) {
        // Pipe disconnected mid-write. Flag it so the listener thread's
        // poll loop wakes up and recycles to a fresh CreateNamedPipe;
        // don't tear down the handle here, the listener owns it.
        pipe_broken_.store(true, std::memory_order_release);
    }
}

void IpcServer::send_hello() {
    std::ostringstream os;
    os << R"({"event":"hello","pid":)" << _getpid() << ",\"version\":\"0.1.0\"}\n";
    const std::scoped_lock lock(pipe_mutex_);
    send_line_locked(os.str());
}

void IpcServer::publish_track(const TrackEvent& e) {
    if (!client_connected_.load(std::memory_order_acquire))
        return;
    std::ostringstream os;
    os << R"({"event":"track")"
       << ",\"title\":" << json_escape(e.title) << ",\"artist\":" << json_escape(e.artist)
       << ",\"album\":" << (e.album.empty() ? std::string("null") : json_escape(e.album))
       << ",\"source_id\":" << json_escape(e.source_id) << ",\"source_display\":" << json_escape(e.source_display);
    if (e.art_bytes != nullptr && e.art_size > 0) {
        os << R"(,"art_b64":")" << base64_encode(e.art_bytes, e.art_size) << "\"";
    } else {
        os << ",\"art_b64\":null";
    }
    os << "}\n";
    const std::scoped_lock lock(pipe_mutex_);
    send_line_locked(os.str());
}

void IpcServer::publish_stats(const StatsEvent& e) {
    if (!client_connected_.load(std::memory_order_acquire))
        return;
    std::ostringstream os;
    os << R"({"event":"stats")"
       << ",\"installed\":" << (e.installed ? "true" : "false") << ",\"frames_in\":" << e.frames_in
       << ",\"frames_out\":" << e.frames_out << ",\"underruns\":" << e.underruns
       << ",\"normalizer_gain\":" << e.normalizer_gain << ",\"limiter_gain\":" << e.limiter_gain << "}\n";
    const std::scoped_lock lock(pipe_mutex_);
    send_line_locked(os.str());
}

void IpcServer::set_snapshot_callback(SnapshotFn cb) {
    const std::scoped_lock lock(snapshot_mutex_);
    snapshot_cb_ = std::move(cb);
}

void IpcServer::set_command_callback(CommandFn cb) {
    const std::scoped_lock lock(command_mutex_);
    command_cb_ = std::move(cb);
}

void IpcServer::publish_game_event(const std::string_view kind) {
    if (!client_connected_.load(std::memory_order_acquire))
        return;
    std::ostringstream os;
    os << R"({"event":"game_event","kind":)" << json_escape(kind) << "}\n";
    const std::scoped_lock lock(pipe_mutex_);
    send_line_locked(os.str());
}

void IpcServer::publish_debug(const std::string_view tag, const std::string_view text) {
    if (!client_connected_.load(std::memory_order_acquire))
        return;
    std::ostringstream os;
    os << R"({"event":"debug","tag":)" << json_escape(tag) << R"(,"text":)" << json_escape(text) << "}\n";
    const std::scoped_lock lock(pipe_mutex_);
    send_line_locked(os.str());
}

void IpcServer::publish_source_changed(const std::string_view id, const std::string_view display) {
    if (!client_connected_.load(std::memory_order_acquire))
        return;
    std::ostringstream os;
    os << R"({"event":"source_changed")"
       << ",\"id\":" << json_escape(id) << ",\"display\":" << json_escape(display) << "}\n";
    const std::scoped_lock lock(pipe_mutex_);
    send_line_locked(os.str());
}
} // namespace horizon::ipc

// horizon_audio_tap.dll: loaded into a process Horizon Radio starts (Spotify Soloist) to take the
// audio it plays. It patches the process's WASAPI vtables: each block the process renders is
// copied to Horizon Radio over \\.\pipe\HorizonRadio.tap.<pid> (protocol.hpp) and handed to Windows
// marked silent, so the process plays nothing out loud. All IAudioClient / IAudioRenderClient
// objects in a process share one vtable each (AudioSes.dll's), so patching them once, from a client
// of our own, covers the streams the process opens later.

#include <windows.h>

// After <windows.h>, which the audio headers need first.
#include <array>
#include <audioclient.h>
#include <cstdio>
#include <horizon/tap/protocol.hpp>
#include <horizon/tap/tap_writer.hpp>
#include <memory>
#include <mmdeviceapi.h>
#include <mutex>
#include <optional>
#include <unordered_map>

namespace {

using horizon::tap::FormatPayload;
using horizon::tap::TapWriter;

// ~20 s of 48 kHz float stereo: room for the pipe thread to fall behind without dropping audio.
constexpr std::size_t kRingBytes = std::size_t{8} * 1024 * 1024;
constexpr std::size_t kPipeChunk = 65536;

// Vtable slots (IUnknown 0-2, then the interface's methods in declaration order).
constexpr int kInitialize                  = 3;  // IAudioClient::Initialize
constexpr int kGetService                  = 14; // IAudioClient::GetService
constexpr int kInitializeSharedAudioStream = 20; // IAudioClient3::InitializeSharedAudioStream
constexpr int kGetBuffer                   = 3;  // IAudioRenderClient::GetBuffer
constexpr int kReleaseBuffer               = 4;  // IAudioRenderClient::ReleaseBuffer

using InitializeFn    = HRESULT(STDMETHODCALLTYPE*)(IAudioClient*, AUDCLNT_SHAREMODE, DWORD, REFERENCE_TIME,
                                                    REFERENCE_TIME, const WAVEFORMATEX*, LPCGUID);
using GetServiceFn    = HRESULT(STDMETHODCALLTYPE*)(IAudioClient*, REFIID, void**);
using InitShared3Fn   = HRESULT(STDMETHODCALLTYPE*)(IAudioClient3*, DWORD, UINT32, const WAVEFORMATEX*, LPCGUID);
using GetBufferFn     = HRESULT(STDMETHODCALLTYPE*)(IAudioRenderClient*, UINT32, BYTE**);
using ReleaseBufferFn = HRESULT(STDMETHODCALLTYPE*)(IAudioRenderClient*, UINT32, DWORD);

InitializeFn    g_initialize     = nullptr;
GetServiceFn    g_get_service    = nullptr;
InitShared3Fn   g_init_shared3   = nullptr;
GetBufferFn     g_get_buffer     = nullptr;
ReleaseBufferFn g_release_buffer = nullptr;

// Leaked on purpose: hooks can run until the process exits, after static destructors.
TapWriter* g_writer = nullptr;

// Formats by stream: each client's from its Initialize, each render client's from the client that
// made it. A stream opened before the hooks went in falls back to the device's mix format.
struct StreamFormats {
    std::mutex                                     lock;
    std::unordered_map<const void*, FormatPayload> clients;
    std::unordered_map<const void*, FormatPayload> renders;
    std::optional<FormatPayload>                   mix;
};
StreamFormats* g_formats = nullptr; // leaked with g_writer

// The buffer GetBuffer handed this thread, for the ReleaseBuffer that follows it.
thread_local BYTE* t_buffer = nullptr;

void Log(const char* text) {
    if (g_writer != nullptr) {
        g_writer->log(text);
    }
}

void LogHr(const char* what, HRESULT hr) {
    std::array<char, 128> line{};
    std::snprintf(line.data(), line.size(), "%s failed: 0x%08lx", what, static_cast<unsigned long>(hr));
    Log(line.data());
}

void NoteClientFormat(const void* client, const WAVEFORMATEX* wave) {
    if (const auto f = horizon::tap::format_from(wave)) {
        const std::scoped_lock lock(g_formats->lock);
        g_formats->clients[client] = *f;
    }
}

std::optional<FormatPayload> RenderFormat(const void* render) {
    const std::scoped_lock lock(g_formats->lock);
    if (const auto it = g_formats->renders.find(render); it != g_formats->renders.end()) {
        return it->second;
    }
    return g_formats->mix;
}

HRESULT STDMETHODCALLTYPE HookInitialize(IAudioClient* self, AUDCLNT_SHAREMODE mode, DWORD flags,
                                         REFERENCE_TIME duration, REFERENCE_TIME period, const WAVEFORMATEX* format,
                                         LPCGUID session) {
    const HRESULT hr = g_initialize(self, mode, flags, duration, period, format, session);
    if (SUCCEEDED(hr)) {
        NoteClientFormat(self, format);
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE HookInitShared3(IAudioClient3* self, DWORD flags, UINT32 period, const WAVEFORMATEX* format,
                                          LPCGUID session) {
    const HRESULT hr = g_init_shared3(self, flags, period, format, session);
    if (SUCCEEDED(hr)) {
        NoteClientFormat(self, format);
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE HookGetService(IAudioClient* self, REFIID riid, void** service) {
    const HRESULT hr = g_get_service(self, riid, service);
    if (SUCCEEDED(hr) && service != nullptr && *service != nullptr && IsEqualIID(riid, __uuidof(IAudioRenderClient))) {
        const std::scoped_lock lock(g_formats->lock);
        if (const auto it = g_formats->clients.find(self); it != g_formats->clients.end()) {
            g_formats->renders[*service] = it->second;
        }
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE HookGetBuffer(IAudioRenderClient* self, UINT32 frames, BYTE** data) {
    const HRESULT hr = g_get_buffer(self, frames, data);
    t_buffer         = SUCCEEDED(hr) && data != nullptr ? *data : nullptr;
    return hr;
}

HRESULT STDMETHODCALLTYPE HookReleaseBuffer(IAudioRenderClient* self, UINT32 frames, DWORD flags) {
    if (frames > 0 && t_buffer != nullptr) {
        if (const auto format = RenderFormat(self)) {
            const bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
            g_writer->audio(*format, silent ? nullptr : t_buffer, frames);
        }
    }
    t_buffer = nullptr;
    // Taken: the process's speakers get silence.
    return g_release_buffer(self, frames, flags | AUDCLNT_BUFFERFLAGS_SILENT);
}

template <class Fn> void Patch(void** vtable, int slot, Fn hook, Fn* original) {
    if (vtable[slot] == reinterpret_cast<void*>(hook)) {
        return; // already ours
    }
    DWORD old = 0;
    if (VirtualProtect(static_cast<void*>(&vtable[slot]), sizeof(void*), PAGE_READWRITE, &old) == 0) {
        LogHr("VirtualProtect", HRESULT_FROM_WIN32(GetLastError()));
        return;
    }
    *original    = reinterpret_cast<Fn>(vtable[slot]);
    vtable[slot] = reinterpret_cast<void*>(hook);
    VirtualProtect(static_cast<void*>(&vtable[slot]), sizeof(void*), old, &old);
}

// Opens a client of our own on the default output to reach the shared vtables, and patches them.
bool InstallHooks() {
    struct Releaser {
        void operator()(IUnknown* p) const {
            if (p != nullptr) {
                p->Release();
            }
        }
    };
    Log("hooks: opening the device enumerator");
    IMMDeviceEnumerator* enumerator = nullptr;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
    if (FAILED(hr)) {
        LogHr("MMDeviceEnumerator", hr);
        return false;
    }
    const std::unique_ptr<IMMDeviceEnumerator, Releaser> enumerator_guard(enumerator);
    IMMDevice*                                           device = nullptr;
    Log("hooks: finding the default output");
    hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
    if (FAILED(hr)) {
        LogHr("GetDefaultAudioEndpoint", hr);
        return false;
    }
    const std::unique_ptr<IMMDevice, Releaser> device_guard(device);
    IAudioClient*                              client = nullptr;
    Log("hooks: opening a client on it");
    hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&client));
    if (FAILED(hr)) {
        LogHr("Activate(IAudioClient)", hr);
        return false;
    }
    const std::unique_ptr<IAudioClient, Releaser> client_guard(client);
    WAVEFORMATEX*                                 mix = nullptr;
    hr                                                = client->GetMixFormat(&mix);
    if (FAILED(hr)) {
        LogHr("GetMixFormat", hr);
        return false;
    }
    {
        const std::scoped_lock lock(g_formats->lock);
        g_formats->mix = horizon::tap::format_from(mix);
    }
    hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 10'000'000, 0, mix, nullptr);
    CoTaskMemFree(mix);
    if (FAILED(hr)) {
        LogHr("Initialize", hr);
        return false;
    }
    IAudioRenderClient* render = nullptr;
    hr                         = client->GetService(IID_PPV_ARGS(&render));
    if (FAILED(hr)) {
        LogHr("GetService(IAudioRenderClient)", hr);
        return false;
    }
    const std::unique_ptr<IAudioRenderClient, Releaser> render_guard(render);

    auto** client_vtable = *reinterpret_cast<void***>(client);
    auto** render_vtable = *reinterpret_cast<void***>(render);
    Patch(client_vtable, kInitialize, &HookInitialize, &g_initialize);
    Patch(client_vtable, kGetService, &HookGetService, &g_get_service);
    IAudioClient3* client3 = nullptr;
    if (SUCCEEDED(client->QueryInterface(IID_PPV_ARGS(&client3)))) {
        Patch(client_vtable, kInitializeSharedAudioStream, &HookInitShared3, &g_init_shared3);
        client3->Release();
    }
    Patch(render_vtable, kGetBuffer, &HookGetBuffer, &g_get_buffer);
    Patch(render_vtable, kReleaseBuffer, &HookReleaseBuffer, &g_release_buffer);
    return true;
}

// Serves the ring to one reader at a time, reconnecting when it goes away. Runs until the process
// ends.
[[noreturn]] void ServePipe() {
    std::array<wchar_t, 64> name{};
    swprintf_s(name.data(), name.size(), L"\\\\.\\pipe\\HorizonRadio.tap.%lu", GetCurrentProcessId());
    const std::unique_ptr<std::uint8_t[]> chunk(new std::uint8_t[kPipeChunk]);
    for (;;) {
        HANDLE pipe = CreateNamedPipeW(name.data(), PIPE_ACCESS_OUTBOUND, PIPE_TYPE_BYTE | PIPE_WAIT, 1,
                                       static_cast<DWORD>(kPipeChunk), 0, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            Sleep(1000);
            continue;
        }
        if (ConnectNamedPipe(pipe, nullptr) != 0 || GetLastError() == ERROR_PIPE_CONNECTED) {
            g_writer->reannounce_format();
            for (;;) {
                const std::size_t n = g_writer->ring().pop(chunk.get(), kPipeChunk);
                if (n == 0) {
                    Sleep(2);
                    continue;
                }
                DWORD written = 0;
                if (WriteFile(pipe, chunk.get(), static_cast<DWORD>(n), &written, nullptr) == 0) {
                    break; // reader gone
                }
            }
        }
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
}

DWORD WINAPI Serve(void* /*unused*/) {
    ServePipe();
}

// The pipe comes up first, on its own thread, so the app can read the steps below even if one
// of them never returns.
DWORD WINAPI Start(void* /*unused*/) {
    HANDLE serve = CreateThread(nullptr, 0, Serve, nullptr, 0, nullptr);
    if (serve != nullptr) {
        CloseHandle(serve);
    }
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    Log(InstallHooks() ? "hooks installed" : "hooks NOT installed: the process plays out loud");
    CoUninitialize();
    return 0;
}

} // namespace

// The loader looks DllMain up by name: it must stay external.
BOOL APIENTRY DllMain( // NOLINT(misc-use-internal-linkage)
    HMODULE module, DWORD reason, LPVOID /*reserved*/) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        // The hooks point into this DLL: never unload it.
        HMODULE pinned = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                           reinterpret_cast<LPCWSTR>(&Start), &pinned);
        g_writer      = new TapWriter(kRingBytes);
        g_formats     = new StreamFormats();
        HANDLE thread = CreateThread(nullptr, 0, Start, nullptr, 0, nullptr);
        if (thread != nullptr) {
            CloseHandle(thread);
        }
    }
    return TRUE;
}

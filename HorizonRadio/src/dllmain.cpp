#include <windows.h>

// ---- version.dll proxy forwarders --------------------------------------
//
// Forward every export of the real C:\Windows\System32\version.dll so the
// game keeps working when it loads our DLL by the same name from its own
// directory.
//
// The linker's /export directive accepts paths in the module name field; the
// .def EXPORTS directive does NOT (it parses paths as a rename to a local
// symbol and emits LNK2001). That is why these live as pragmas here rather
// than in an exports.def file.
//
// Pointing at "version.<name>" without a path would recurse: the loader
// would find our own version.dll (it's first in the search order) and
// follow the forwarder right back to itself.

#pragma comment(linker, "/export:GetFileVersionInfoA=C:\\Windows\\System32\\version.GetFileVersionInfoA")
#pragma comment(linker, "/export:GetFileVersionInfoByHandle=C:\\Windows\\System32\\version.GetFileVersionInfoByHandle")
#pragma comment(linker, "/export:GetFileVersionInfoExA=C:\\Windows\\System32\\version.GetFileVersionInfoExA")
#pragma comment(linker, "/export:GetFileVersionInfoExW=C:\\Windows\\System32\\version.GetFileVersionInfoExW")
#pragma comment(linker, "/export:GetFileVersionInfoSizeA=C:\\Windows\\System32\\version.GetFileVersionInfoSizeA")
#pragma comment(linker, "/export:GetFileVersionInfoSizeExA=C:\\Windows\\System32\\version.GetFileVersionInfoSizeExA")
#pragma comment(linker, "/export:GetFileVersionInfoSizeExW=C:\\Windows\\System32\\version.GetFileVersionInfoSizeExW")
#pragma comment(linker, "/export:GetFileVersionInfoSizeW=C:\\Windows\\System32\\version.GetFileVersionInfoSizeW")
#pragma comment(linker, "/export:GetFileVersionInfoW=C:\\Windows\\System32\\version.GetFileVersionInfoW")
#pragma comment(linker, "/export:VerFindFileA=C:\\Windows\\System32\\version.VerFindFileA")
#pragma comment(linker, "/export:VerFindFileW=C:\\Windows\\System32\\version.VerFindFileW")
#pragma comment(linker, "/export:VerInstallFileA=C:\\Windows\\System32\\version.VerInstallFileA")
#pragma comment(linker, "/export:VerInstallFileW=C:\\Windows\\System32\\version.VerInstallFileW")
#pragma comment(linker, "/export:VerLanguageNameA=C:\\Windows\\System32\\version.VerLanguageNameA")
#pragma comment(linker, "/export:VerLanguageNameW=C:\\Windows\\System32\\version.VerLanguageNameW")
#pragma comment(linker, "/export:VerQueryValueA=C:\\Windows\\System32\\version.VerQueryValueA")
#pragma comment(linker, "/export:VerQueryValueW=C:\\Windows\\System32\\version.VerQueryValueW")

#include <horizon/fmod/bridge.hpp>
#include <horizon/fmod/resolver.hpp>
#include <horizon/fmod/signatures.hpp>
#include <horizon/fmod/system_resolver.hpp>
#include <horizon/inject/game_resolver.hpp>
#include <horizon/inject/metadata_injector.hpp>
#include <horizon/inject/safe_mem.hpp>
#include <horizon/inject/sigscan.hpp>
#include <horizon/ipc/ipc_server.hpp>
#include <horizon/ipc/pcm_pipe_server.hpp>

#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

namespace {

using horizon::fmod::FmodBridge;
using horizon::fmod::FmodResolver;
using horizon::inject::MetadataInjector;
using horizon::inject::PeImage;

// Minimum track info the metadata injector + IPC echo path need.
// Source-side decoding / search lives in the C# HorizonRadio.UI process
// now; the DLL just stores whatever the UI sends via {"cmd":"set_track"}.
struct TrackInfo {
    std::string id;       // canonical id from the source (e.g. "spotify:track:abc"); blank for local
    std::string title;
    std::string artist;
    std::string album;
};

HMODULE g_module = nullptr;

// Set when the FmodBridge is constructed; the source thread's on_audio
// reads this atomically to deliver PCM. Stays null when the bridge
// can't be brought up (e.g. signatures::kFh6 still empty).
std::atomic<FmodBridge*> g_bridge_for_push{nullptr};

// Set when MetadataInjector has resolved against the running module.
// on_track reads atomically to push the latest title/artist into the
// game's RadioStreamFmod instance. Stays null until kFh6Metadata is
// filled in with verified offsets.
std::atomic<MetadataInjector*> g_metadata_injector{nullptr};

// Address of the RadioState global (in FH6's .data BSS region). Set
// once at startup via GameResolver. *g_radio_state_global yields a
// RadioState* (or nullptr if the game hasn't initialized it yet).
std::atomic<void**> g_radio_state_global{nullptr};

// Resolved RadioStreamFmod vtable, cached for the periodic writer's
// fast scan of RadioState's slot table.
std::atomic<const void*> g_radio_stream_vtable{nullptr};

// Cached latest track. The C# UI sends one via {"cmd":"set_track"}
// shortly after starting a source — well before the game's
// RadioStreamFmod instances exist. The periodic writer re-applies this
// cached info every tick so the metadata lands as soon as the
// instances do.
std::mutex   g_track_mutex;
TrackInfo    g_current_track;
std::atomic<bool> g_have_track{false};

// IPC server exposed to the HorizonRadio.UI desktop companion app.
// Lives for the entire DLL lifetime; events flow out as source
// callbacks fire (track changes) and as the periodic writer ticks
// (bridge stats). Stop() is wired up on DLL_PROCESS_DETACH.
horizon::ipc::IpcServer g_ipc_server;

// PCM ingress pipe. The C# UI writes s16 stereo frames here; we
// forward them to the FMOD bridge. Snake-cased pipe name lives in
// PcmPipeServer; the UI's PcmPipeClient connects on the same name.
horizon::ipc::PcmPipeServer g_pcm_pipe;

// Active source identity, set by inbound {"cmd":"set_track"} commands.
// Used to repopulate a freshly-connected UI client via the snapshot
// callback so a re-launched UI sees the current source name.
std::string g_active_source_id;
std::string g_active_source_display;

void log_w(const wchar_t* s) { OutputDebugStringW(s); }

void logf(const wchar_t* fmt, ...) {
    wchar_t buf[512];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(buf, sizeof(buf) / sizeof(buf[0]), _TRUNCATE, fmt, args);
    va_end(args);
    OutputDebugStringW(buf);
}

// Helper: push a track event over the IPC pipe to the UI companion.
// Cheap when no UI is attached (IpcServer::connected() short-circuits).
void publish_track_ipc(const TrackInfo& t,
                       std::string_view source_id,
                       std::string_view source_display) {
    horizon::ipc::IpcServer::TrackEvent ev{};
    ev.title          = t.title;
    ev.artist         = t.artist;
    ev.album          = t.album;
    ev.source_id      = std::string(source_id);
    ev.source_display = std::string(source_display);
    g_ipc_server.publish_track(ev);
}

// Returns a constructed FmodBridge if all required entry points
// resolve; nullptr otherwise. The bridge is constructed but stays
// dormant (no DSP installed) until the periodic writer hands it a
// chain-valid RadioStreamFmod target.
std::unique_ptr<FmodBridge> bring_up_fmod(const PeImage& game_image) {
    FmodResolver resolver(game_image, horizon::fmod::signatures::kFh6);
    auto hooks = resolver.resolve();

    logf(L"[horizon-radio] resolver: createDsp=%d addDsp=%d removeDsp=%d "
         L"dspRelease=%d setMode=%d handleOpen=%d handleUnlock=%d\n",
         resolver.report().createDsp    ? 1 : 0,
         resolver.report().addDsp       ? 1 : 0,
         resolver.report().removeDsp    ? 1 : 0,
         resolver.report().dspRelease   ? 1 : 0,
         resolver.report().setMode      ? 1 : 0,
         resolver.report().handleOpen   ? 1 : 0,
         resolver.report().handleUnlock ? 1 : 0);

    // For each unresolved anchored signature, run the diagnostic
    // resolver and log the outcome stage. Tells us whether the
    // anchor missed entirely, the lea decode rejected hits, .pdata
    // had no enclosing function, the prologue patterns didn't match
    // this build, or the match was ambiguous.
    auto diag_log = [&](const wchar_t* slot,
                        const horizon::fmod::SignaturePattern& sig,
                        bool resolved) {
        if (resolved) return;
        if (sig.anchor.empty()) {
            logf(L"[horizon-radio]   %ls: unresolved (direct pattern not found)\n", slot);
            return;
        }
        const auto d = horizon::inject::diagnose_function_by_anchor(
            game_image, sig.anchor, sig.pattern);
        const wchar_t* stage = L"?";
        switch (d.status) {
            using S = horizon::inject::AnchorResolution::Status;
            case S::ok:                     stage = L"ok"; break;
            case S::no_anchor_string:       stage = L"anchor string not in .rdata"; break;
            case S::no_lea:                 stage = L"no lea reg, [rip+disp32] references"; break;
            case S::no_enclosing_function:  stage = L"lea not inside any .pdata function"; break;
            case S::no_prologue_match:      stage = L"prologue pattern(s) didn't match"; break;
            case S::ambiguous:              stage = L"multiple distinct functions match"; break;
        }
        logf(L"[horizon-radio]   %ls: %ls "
             L"(anchors=%zu leas=%zu enclosing=%zu prologue_match=%zu)\n",
             slot, stage, d.anchor_count, d.lea_count,
             d.enclosing_fn_count, d.prologue_match_count);

        // For "no prologue match" / "ambiguous", dump the first 24
        // bytes at each enclosing function so we can read off the
        // actual prologue and either extend the alternation or
        // pick a more discriminating disambiguator.
        if (d.status == horizon::inject::AnchorResolution::Status::no_prologue_match ||
            d.status == horizon::inject::AnchorResolution::Status::ambiguous) {
            for (auto* fn : d.enclosing_functions) {
                if (fn == nullptr) break;
                std::uint8_t bytes[24]{};
                if (!horizon::inject::safe_read_bytes(bytes, fn, sizeof(bytes))) continue;
                wchar_t buf[256];
                int off = swprintf_s(buf, L"[horizon-radio]     enclosing fn @ %p: ", fn);
                for (std::size_t i = 0; i < sizeof(bytes) && off < 240; ++i) {
                    off += swprintf_s(buf + off, std::size(buf) - off,
                                      L"%02X ", bytes[i]);
                }
                swprintf_s(buf + off, std::size(buf) - off, L"\n");
                OutputDebugStringW(buf);
            }
        }
    };
    diag_log(L"createDsp",    horizon::fmod::signatures::kFh6.createDsp,    resolver.report().createDsp);
    diag_log(L"addDsp",       horizon::fmod::signatures::kFh6.addDsp,       resolver.report().addDsp);
    diag_log(L"removeDsp",    horizon::fmod::signatures::kFh6.removeDsp,    resolver.report().removeDsp);
    diag_log(L"dspRelease",   horizon::fmod::signatures::kFh6.dspRelease,   resolver.report().dspRelease);
    diag_log(L"setMode",      horizon::fmod::signatures::kFh6.setMode,      resolver.report().setMode);
    diag_log(L"handleOpen",   horizon::fmod::signatures::kFh6.handleOpen,   resolver.report().handleOpen);
    diag_log(L"handleUnlock", horizon::fmod::signatures::kFh6.handleUnlock, resolver.report().handleUnlock);

    // Resolved addresses so we can sanity-check them against IDA/Ghidra.
    // The function-start RVA matters because we call into it directly
    // (createDsp(...) etc.); a wrong address with a matching-by-luck
    // prologue would crash the game thread that calls it.
    logf(L"[horizon-radio] resolver addrs: createDsp=%p addDsp=%p removeDsp=%p dspRelease=%p\n",
         reinterpret_cast<void*>(hooks.createDsp),
         reinterpret_cast<void*>(hooks.addDsp),
         reinterpret_cast<void*>(hooks.removeDsp),
         reinterpret_cast<void*>(hooks.dspRelease));
    logf(L"[horizon-radio] resolver addrs: setMode=%p handleOpen=%p handleUnlock=%p\n",
         reinterpret_cast<void*>(hooks.setMode),
         reinterpret_cast<void*>(hooks.handleOpen),
         reinterpret_cast<void*>(hooks.handleUnlock));

    if (!resolver.report().ready()) {
        log_w(L"[horizon-radio] FMOD signatures incomplete; bridge dormant. "
              L"Update signatures::kFh6 once patterns are verified against the live game.\n");
        return nullptr;
    }

    auto bridge = std::make_unique<FmodBridge>(hooks);

    // Lazy-resolve createDsp. FMOD's System::createDSP code path isn't
    // always wired up in .text at DllMain time, so the resolver's first
    // pass can return null for it. Pass the bridge a callback that
    // re-runs the scan against the (same, fixed) game module on demand;
    // it'll fire once on the first install attempt. Adapted from
    // g0ldyy/fh6-universal-radio's "lazy-resolve createDSP after FMOD
    // finishes loading" fix.
    if (!resolver.report().createDsp) {
        bridge->set_create_dsp_resolver([&game_image]() -> horizon::fmod::SystemCreateDspFn {
            const auto& sig = horizon::fmod::signatures::kFh6.createDsp;
            auto hit = horizon::inject::find_function_by_anchor(
                game_image, sig.anchor, sig.pattern);
            return reinterpret_cast<horizon::fmod::SystemCreateDspFn>(
                const_cast<std::byte*>(hit));
        });
    }

    // Pin the AGC at unity gain. Spotify ReplayGain (and ID3 gain
    // tags for LocalFile sources) already do track-to-track loudness
    // normalization upstream; if our AGC also tries to even things
    // out, the two pull in different directions and we get audible
    // pumping during quiet passages — gain ramps up looking for the
    // RMS target, then snaps back when the next loud transient hits.
    // The limiter logic still runs (clamped to peak_threshold) as a
    // safety net for clipping.
    bridge->normalizer().set_max_gain(1.0f);
    bridge->normalizer().set_min_gain(1.0f);

    log_w(L"[horizon-radio] FmodBridge constructed; awaiting System/ChannelControl targets\n");
    return bridge;
}

// Returns a resolved MetadataInjector if kFh6Metadata.class_mangled_name
// matches a class in the loaded module; nullptr otherwise. A null
// return is the expected state until offsets are filled in.
std::unique_ptr<MetadataInjector> bring_up_metadata(const PeImage& game_image) {
    if (horizon::inject::signatures::kFh6Metadata.class_mangled_name.empty()) {
        log_w(L"[horizon-radio] metadata injector dormant: kFh6Metadata.class_mangled_name "
              L"is empty (no offsets configured yet).\n");
        return nullptr;
    }

    auto inj = std::make_unique<MetadataInjector>(
        game_image, horizon::inject::signatures::kFh6Metadata);
    if (!inj->resolve()) {
        log_w(L"[horizon-radio] metadata injector: RTTI lookup failed for configured class; "
              L"verify kFh6Metadata.class_mangled_name against the live game.\n");
        return nullptr;
    }
    log_w(L"[horizon-radio] MetadataInjector resolved; on_track will update game HUD\n");
    return inj;
}

// SEH-protected read of a pointer-sized slot. Returns nullptr if the
// address itself is unmapped. Thin wrapper over safe_read_qword for
// readability at call sites.
void* safe_deref_slot(void** slot) noexcept {
    if (slot == nullptr) return nullptr;
    return reinterpret_cast<void*>(horizon::inject::safe_read_qword(slot));
}

// Sigscan-based RadioState global resolution. Returns the address of
// the RadioState global slot (the QWORD that holds RadioState*), or
// nullptr if the pattern is missing / drift in the FH6 build.
void* resolve_radiostate_global(const PeImage& game_image) {
    log_w(L"[horizon-radio] game-resolver: scanning for radio_set_station_by_name...\n");
    horizon::game::GameResolver resolver(game_image);
    const auto& sig = horizon::game::signatures::kFh6Game;
    void* slot = resolver.resolve_global_via_rip_load(
        sig.radio_set_station_by_name,
        sig.mov_rbx_offset_in_match);
    if (slot == nullptr) {
        log_w(L"[horizon-radio] game-resolver: radio_set_station_by_name pattern "
              L"didn't match; RadioState global unresolved\n");
        return nullptr;
    }
    logf(L"[horizon-radio] game-resolver: RadioState global slot at %p\n", slot);

    // Probe the slot once now -- if the resolved address isn't even
    // committed-readable, we know the sigscan went sideways without
    // waiting 10s for the periodic writer to find out.
    void* probe = safe_deref_slot(static_cast<void**>(slot));
    logf(L"[horizon-radio] game-resolver: initial read of slot yields %p "
         L"(may be null until game initializes RadioState)\n", probe);
    return slot;
}

DWORD WINAPI bridge_init_thread(LPVOID) {
    log_w(L"[horizon-radio] init thread started\n");

    // IPC server is independent of the bridge; start it early so the
    // companion UI can attach even before FH6 finishes wiring up its
    // radio system. Stays a no-op when no UI is connected. The
    // snapshot callback re-publishes current state on every (re)connect
    // so the UI doesn't sit on its placeholder when the user attaches
    // mid-playback.
    // PCM ingress: forward bytes from the UI process straight to the
    // FMOD bridge. Cheap when no client is writing — the server thread
    // sits in ConnectNamedPipe until something attaches.
    //
    // First chunk also wakes up the metadata pipeline. The periodic
    // writer / DSP installer gates on g_have_track being true (the
    // historical signal "we have audio worth playing"); without a
    // legacy in-DLL source firing on_track, that flag would stay
    // false forever and the DSP would never attach to the game radio
    // channel. Plant a stub so the install path proceeds. The real
    // metadata will come from the C# side over IPC (UI→DLL command
    // channel, coming with Phase 4/5).
    g_pcm_pipe.start([](const std::int16_t* frames, std::size_t frame_count) {
        if (auto* b = g_bridge_for_push.load(std::memory_order_acquire)) {
            b->push_pcm(frames, frame_count);
        }
        if (!g_have_track.load(std::memory_order_acquire)) {
            std::lock_guard lock(g_track_mutex);
            if (g_current_track.title.empty()) {
                g_current_track.title  = "Horizon Radio";
                g_current_track.artist = "";
            }
            g_have_track.store(true, std::memory_order_release);
        }
    });

    // Dispatcher for inbound JSON commands from the UI. Today only
    // {"cmd":"set_track",...} is handled — used so C#-side sources
    // (LocalFile, Spotify, …) can route track metadata into the game
    // HUD via the existing MetadataInjector path. Commands are simple
    // flat JSON; we scan for the keys we care about rather than pulling
    // in a full parser.
    auto json_extract_string = [](const std::string& line,
                                  std::string_view key,
                                  std::string& out) -> bool {
        // Match `"key":"..."` allowing for backslash-escaped quotes
        // inside the value. Plenty for our short, well-formed lines.
        std::string needle = "\"";
        needle.append(key);
        needle.append("\":");
        auto pos = line.find(needle);
        if (pos == std::string::npos) return false;
        pos += needle.size();
        while (pos < line.size() && (line[pos] == ' ' || line[pos] == '\t')) ++pos;
        if (pos >= line.size() || line[pos] != '"') return false;
        ++pos;
        std::string value;
        value.reserve(64);
        while (pos < line.size()) {
            char c = line[pos++];
            if (c == '\\' && pos < line.size()) {
                char esc = line[pos++];
                switch (esc) {
                    case '"':  value.push_back('"');  break;
                    case '\\': value.push_back('\\'); break;
                    case '/':  value.push_back('/');  break;
                    case 'b':  value.push_back('\b'); break;
                    case 'f':  value.push_back('\f'); break;
                    case 'n':  value.push_back('\n'); break;
                    case 'r':  value.push_back('\r'); break;
                    case 't':  value.push_back('\t'); break;
                    case 'u': {
                        if (pos + 4 > line.size()) return false;
                        unsigned cp = 0;
                        for (int i = 0; i < 4; ++i) {
                            char hex = line[pos++];
                            unsigned d = (hex >= '0' && hex <= '9') ? hex - '0'
                                       : (hex >= 'a' && hex <= 'f') ? hex - 'a' + 10
                                       : (hex >= 'A' && hex <= 'F') ? hex - 'A' + 10
                                       : 16u;
                            if (d > 15) return false;
                            cp = (cp << 4) | d;
                        }
                        // Encode as UTF-8 (BMP only — surrogate pairs
                        // would need extra handling; not needed for
                        // typical track titles which arrive as raw UTF-8
                        // and don't get \u-escaped by our writer).
                        if (cp < 0x80) {
                            value.push_back(static_cast<char>(cp));
                        } else if (cp < 0x800) {
                            value.push_back(static_cast<char>(0xC0 | (cp >> 6)));
                            value.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
                        } else {
                            value.push_back(static_cast<char>(0xE0 | (cp >> 12)));
                            value.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
                            value.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
                        }
                        break;
                    }
                    default: value.push_back(esc); break;
                }
            } else if (c == '"') {
                out = std::move(value);
                return true;
            } else {
                value.push_back(c);
            }
        }
        return false;
    };

    g_ipc_server.set_command_callback([json_extract_string](const std::string& line) {
        std::string cmd;
        if (!json_extract_string(line, "cmd", cmd)) return;

        if (cmd == "set_track") {
            std::string title, artist, album, source_id, source_display, external_id;
            json_extract_string(line, "title",          title);
            json_extract_string(line, "artist",         artist);
            json_extract_string(line, "album",          album);
            json_extract_string(line, "source_id",      source_id);
            json_extract_string(line, "source_display", source_display);
            json_extract_string(line, "external_id",    external_id);

            TrackInfo snapshot;
            {
                std::lock_guard lock(g_track_mutex);
                g_current_track.title  = title;
                g_current_track.artist = artist;
                g_current_track.album  = album;
                g_current_track.id     = external_id;
                g_have_track.store(true, std::memory_order_release);
                snapshot = g_current_track;
            }
            if (!source_id.empty() && source_id != g_active_source_id) {
                g_active_source_id      = source_id;
                g_active_source_display = source_display.empty() ? source_id : source_display;
                g_ipc_server.publish_source_changed(g_active_source_id, g_active_source_display);
            }
            // Reset the normalizer so a hot intro on the new track
            // doesn't inherit the prior track's compression curve. Same
            // rule the librespot stderr-parsing path follows.
            if (auto* b = g_bridge_for_push.load(std::memory_order_acquire)) {
                b->normalizer().reset();
            }
            // Echo back so the UI sees its own change (and so a freshly
            // connected snapshot would have it too).
            publish_track_ipc(snapshot, g_active_source_id, g_active_source_display);

            std::wstring wtitle(title.begin(), title.end());
            logf(L"[horizon-radio] cmd set_track: %ls\n", wtitle.c_str());
        }
    });

    g_ipc_server.set_snapshot_callback([] {
        if (!g_active_source_id.empty()) {
            g_ipc_server.publish_source_changed(g_active_source_id, g_active_source_display);
        }
        if (g_have_track.load(std::memory_order_acquire)) {
            TrackInfo snapshot;
            {
                std::lock_guard lock(g_track_mutex);
                snapshot = g_current_track;
            }
            publish_track_ipc(snapshot, g_active_source_id, g_active_source_display);
        }
    });
    g_ipc_server.start();

    // Function-local statics outlive the thread function and tie their
    // lifetime to the process. The init thread blocks forever once
    // setup is done, so the bridge/injector are effectively global for
    // the life of the DLL.
    static PeImage                           game_image(GetModuleHandleW(nullptr));
    static std::unique_ptr<FmodBridge>       bridge;
    static std::unique_ptr<MetadataInjector> injector;

    if (!game_image.valid()) {
        log_w(L"[horizon-radio] failed to parse game PE; FMOD bridge + metadata dormant\n");
    } else {
        logf(L"[horizon-radio] game image: base=%p size=%zu .text=%zu .rdata=%zu .data=%zu\n",
             reinterpret_cast<void*>(game_image.base()),
             game_image.image_size(),
             game_image.text().size(),
             game_image.rdata().size(),
             game_image.data().size());

        bridge   = bring_up_fmod(game_image);
        injector = bring_up_metadata(game_image);
        if (injector && injector->resolved()) {
            // Cache the resolved vtable for the fast RadioState scan.
            auto vt = injector->vtable();
            if (vt) {
                g_radio_stream_vtable.store(vt->address, std::memory_order_release);
            }
            // Resolve the RadioState global -- this is the prize that
            // lets us skip the heap scan entirely.
            void* slot = resolve_radiostate_global(game_image);
            g_radio_state_global.store(static_cast<void**>(slot),
                                       std::memory_order_release);
        }
    }

    if (bridge) {
        // Publish for the source thread to push into. The bridge has
        // no targets yet so install() would fail; we set the pointer
        // anyway so push_pcm() at least buffers PCM for when targets
        // do arrive.
        g_bridge_for_push.store(bridge.get(), std::memory_order_release);
    }
    if (injector) {
        g_metadata_injector.store(injector.get(), std::memory_order_release);

        const auto& cfg = horizon::inject::signatures::kFh6Metadata;
        const bool offsets_configured =
            cfg.sound_name_offset.has_value() ||
            cfg.display_name_offset.has_value() ||
            cfg.artist_offset.has_value();

        if (offsets_configured) {
            log_w(L"[horizon-radio] metadata: periodic writer arming "
                  L"(first attempt in 10s, then 5 Hz)\n");
            std::thread([] {
                OutputDebugStringW(L"[horizon-radio] periodic thread: alive\n");
                std::this_thread::sleep_for(std::chrono::seconds(10));
                OutputDebugStringW(L"[horizon-radio] periodic thread: past initial sleep, entering loop\n");

                // Tick rate: 20 Hz matches g0ldyy's reference mod
                // (their 50 Hz is the upper bound; 20 Hz is enough
                // for radio-toggle and song-change transitions to
                // feel instantaneous). FmodBridge::tick() is now
                // cheap in steady state (one memory read + a compare;
                // no FMOD calls), so the cost per tick is microseconds.
                // The metadata write + bridge tick combined run in
                // well under 1 ms even with 5 candidates.
                constexpr auto kTickInterval   = std::chrono::milliseconds(50);
                constexpr int  kRescanIters    = 100;  // ~5 s between heap scans
                int last_write_n               = -1;
                bool last_installed            = false;
                int iter                       = 0;
                while (true) {
                    auto* inj         = g_metadata_injector.load(std::memory_order_acquire);
                    auto* rs_slot     = g_radio_state_global.load(std::memory_order_acquire);
                    const void* vt    = g_radio_stream_vtable.load(std::memory_order_acquire);

                    if (inj && rs_slot && vt && g_have_track.load(std::memory_order_acquire)) {
                        TrackInfo t;
                        {
                            std::lock_guard lock(g_track_mutex);
                            t = g_current_track;
                        }
                        const std::string sound = t.id.empty() ? t.title : t.id;

                        void* radio_state = safe_deref_slot(rs_slot);

                        // Cache: scan all heap-shaped arenas once and
                        // hold onto the instance list. The custom
                        // allocator scatters RadioStreamFmod across
                        // many arenas (not just RadioState's), so we
                        // need the whole-process scan; but it's
                        // expensive enough that we don't want to redo
                        // it every tick. Re-scan every kRescanIters
                        // iterations.
                        static std::vector<const void*> cached_instances;
                        std::vector<const void*>& instances = cached_instances;
                        if (radio_state && (iter % kRescanIters == 0)) {
                            const auto t0 = std::chrono::steady_clock::now();
                            cached_instances =
                                horizon::game::find_instances_in_heap_arenas(vt, game_image);
                            const auto ms = std::chrono::duration_cast<
                                std::chrono::milliseconds>(
                                    std::chrono::steady_clock::now() - t0).count();
                            wchar_t scan_msg[200];
                            swprintf_s(scan_msg,
                                L"[horizon-radio] heap-arena scan: %zu candidates in %lldms\n",
                                cached_instances.size(),
                                static_cast<long long>(ms));
                            OutputDebugStringW(scan_msg);
                        }

                        // "Preferred instance" stickiness: a radio
                        // station may have multiple chain-valid
                        // RadioStreamFmod instances at the same time
                        // (e.g. music + host segment). If we pick
                        // "first chain-valid" each tick we ping-pong
                        // between music and host channels, producing
                        // audible stutter. Instead we lock onto one
                        // instance and only switch if it falls out of
                        // the scan entirely. When its channel goes
                        // dead (host segment starts on a different
                        // instance), we uninstall the DSP — the game
                        // plays its host audio naturally — and
                        // reattach when the music channel returns.
                        static const void* preferred_instance = nullptr;

                        // Drop the preferred if the scan no longer
                        // contains it (instance got freed / station
                        // reallocated).
                        if (preferred_instance) {
                            bool still_present = false;
                            for (auto* inst : instances) {
                                if (inst == preferred_instance) {
                                    still_present = true; break;
                                }
                            }
                            if (!still_present) preferred_instance = nullptr;
                        }

                        int n = 0;
                        for (const void* inst : instances) {
                            const int wrote = inj->write_to_instance(
                                inst, sound, t.title, t.artist);
                            // Adopt as preferred only if we don't
                            // already have one. Once locked, we stay
                            // on it across host segments.
                            if (wrote && preferred_instance == nullptr) {
                                preferred_instance = inst;
                            }
                            n += wrote;
                        }
                        const void* active_instance = preferred_instance;

                        // Log only on transitions to keep DebugView
                        // readable at 5 Hz tick rate. Steady state of
                        // "1 successful write per tick" prints once
                        // and stays quiet.
                        if (n != last_write_n) {
                            wchar_t msg[240];
                            swprintf_s(msg,
                                L"[horizon-radio] periodic: write=%d->%d "
                                L"instances=%zu radio_state=%p iter=%d\n",
                                last_write_n, n, instances.size(), radio_state, iter);
                            OutputDebugStringW(msg);
                            last_write_n = n;
                        }

                        // Audio bridge: when we have a chain-valid
                        // RadioStreamFmod, resolve FMOD System*
                        // (radio_stream + 0x08 + 0xC0) and hand both
                        // to the bridge. tick() reads the channel
                        // handle at radio_stream + 0x20 and installs
                        // / retargets the DSP. (The earlier
                        // `horizon-radio.bridge-on` trigger-file
                        // gate has been retired now that the install
                        // path has been verified to be crash-safe;
                        // see set_target + SEH-wrapped FMOD calls.)
                        auto* bridge_for_install =
                            g_bridge_for_push.load(std::memory_order_acquire);
                        // Track the system-resolve state across ticks
                        // so we only log on transitions (radio
                        // off↔on) instead of every 200 ms while it's
                        // unresolved.
                        static bool last_sys_resolved = true;
                        if (active_instance && bridge_for_install) {
                            auto* radio_stream =
                                const_cast<std::byte*>(
                                    static_cast<const std::byte*>(active_instance) + 0x10);
                            auto* sys = horizon::fmod::resolve_fmod_system_from_stream(
                                game_image, radio_stream);
                            if (sys == nullptr) {
                                // Radio is OFF (chain endpoint goes
                                // null when the user turns the radio
                                // off in-game; channel handle is also
                                // stale). Proactively clear the
                                // bridge target so it uninstalls our
                                // DSP from the dead channel — leaves
                                // a clean slate to install on the
                                // fresh channel when radio comes
                                // back on. Otherwise bridge would
                                // hold a stale current_handle_ and
                                // removeDsp would target a destroyed
                                // FMOD channel during recovery.
                                bridge_for_install->set_target(nullptr, nullptr);
                                bridge_for_install->tick();
                                if (last_sys_resolved) {
                                    wchar_t smsg[200];
                                    swprintf_s(smsg,
                                        L"[horizon-radio] bridge: system unresolvable "
                                        L"(radio off?); uninstalling DSP iter=%d\n", iter);
                                    OutputDebugStringW(smsg);
                                    last_sys_resolved = false;
                                }
                            } else {
                                if (!last_sys_resolved) {
                                    wchar_t smsg[200];
                                    swprintf_s(smsg,
                                        L"[horizon-radio] bridge: system resolved again "
                                        L"iter=%d\n", iter);
                                    OutputDebugStringW(smsg);
                                    last_sys_resolved = true;
                                }
                                bridge_for_install->set_target(sys, radio_stream);
                                bridge_for_install->tick();
                                const bool now_installed =
                                    bridge_for_install->installed();
                                if (now_installed != last_installed) {
                                    wchar_t bmsg[240];
                                    swprintf_s(bmsg,
                                        L"[horizon-radio] bridge: installed=%d "
                                        L"(sys=%p rs=%p) iter=%d\n",
                                        now_installed ? 1 : 0, sys, radio_stream, iter);
                                    OutputDebugStringW(bmsg);
                                    last_installed = now_installed;
                                    // Note: deliberately NO source
                                    // restart on detach. The source
                                    // keeps decoding and looping
                                    // internally; when the bridge
                                    // reattaches the drain-on-stall
                                    // logic discards any queued
                                    // audio and we resume from
                                    // "wherever the source is now."
                                    // This matches real-radio
                                    // semantics: turning the radio
                                    // off and back on doesn't rewind
                                    // the song.
                                }
                            }
                        }
                    }
                    ++iter;
                    std::this_thread::sleep_for(kTickInterval);
                }
            }).detach();
        }

        // Discovery dump (still opt-in via file). Stays available
        // even after offsets are configured, in case the user wants
        // to verify the layout against the live game.
        if (!cfg.sound_name_offset &&
            !cfg.display_name_offset &&
            !cfg.artist_offset) {
            log_w(L"[horizon-radio] discovery: field offsets unconfigured. "
                  L"Discovery is OPT-IN. To trigger one dump: create an empty "
                  L"file named 'horizon-radio.discover' next to version.dll. "
                  L"The file is deleted after the dump; recreate to trigger again.\n");
            std::thread([] {
                wchar_t module_path[MAX_PATH];
                if (GetModuleFileNameW(g_module, module_path, MAX_PATH) == 0) return;
                const std::filesystem::path trigger_path =
                    std::filesystem::path(module_path).parent_path() /
                    L"horizon-radio.discover";

                int dump_count = 0;
                while (true) {
                    std::this_thread::sleep_for(std::chrono::seconds(5));

                    std::error_code ec;
                    if (!std::filesystem::exists(trigger_path, ec)) continue;

                    // Delete first so a failed dump doesn't loop forever.
                    std::filesystem::remove(trigger_path, ec);

                    auto* inj = g_metadata_injector.load(std::memory_order_acquire);
                    if (!inj) continue;

                    wchar_t marker[96];
                    swprintf_s(marker,
                        L"[horizon-radio] --- discovery dump #%d (user-triggered) ---\n",
                        dump_count++);
                    OutputDebugStringW(marker);

                    std::string dump = inj->dump_candidates();
                    std::wstring wide;
                    wide.reserve(dump.size());
                    for (char c : dump) wide.push_back(static_cast<wchar_t>(c));
                    std::size_t pos = 0;
                    while (pos < wide.size()) {
                        std::size_t end = wide.find(L'\n', pos);
                        if (end == std::wstring::npos) end = wide.size();
                        std::wstring line = L"[horizon-radio] " +
                            std::wstring(wide.data() + pos, end - pos) + L"\n";
                        OutputDebugStringW(line.c_str());
                        pos = end + 1;
                    }
                }
            }).detach();
        }
    }

    // Periodic loop: ticks at 2 Hz for IPC stats publishing (the UI
    // expects roughly this cadence), with a separate 30 s throttle on
    // the DebugView log lines so they don't drown the developer view.
    std::uint64_t last_br_in = 0, last_br_out = 0, last_br_under = 0;
    std::uint64_t last_md_writes = 0;
    auto next_debug_log = std::chrono::steady_clock::now() + std::chrono::seconds(30);
    while (true) {
        std::this_thread::sleep_for(std::chrono::milliseconds(500));

        if (bridge) {
            const auto fi = bridge->total_frames_in();
            const auto fo = bridge->total_frames_out();
            const auto un = bridge->underrun_count();

            // Publish stats every tick; cheap when no UI is connected.
            horizon::ipc::IpcServer::StatsEvent ev{};
            ev.installed       = bridge->installed();
            ev.frames_in       = fi;
            ev.frames_out      = fo;
            ev.underruns       = un;
            ev.normalizer_gain = bridge->normalizer().current_gain();
            ev.limiter_gain    = bridge->normalizer().current_limiter_gain();
            g_ipc_server.publish_stats(ev);

            // Throttled debug-log path: only print when something
            // changed AND the 30 s timer has elapsed.
            const auto now = std::chrono::steady_clock::now();
            if (now >= next_debug_log &&
                (fi != last_br_in || fo != last_br_out || un != last_br_under)) {
                logf(L"[horizon-radio] bridge: installed=%d frames_in=%llu frames_out=%llu underruns=%llu\n",
                     bridge->installed() ? 1 : 0,
                     static_cast<unsigned long long>(fi),
                     static_cast<unsigned long long>(fo),
                     static_cast<unsigned long long>(un));
                last_br_in = fi; last_br_out = fo; last_br_under = un;
                next_debug_log = now + std::chrono::seconds(30);
            }
        }
        if (injector) {
            const auto w = injector->total_writes();
            if (w != last_md_writes &&
                std::chrono::steady_clock::now() >= next_debug_log) {
                logf(L"[horizon-radio] metadata: total_writes=%llu\n",
                     static_cast<unsigned long long>(w));
                last_md_writes = w;
            }
        }
    }
    return 0;
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID /*reserved*/) {
    switch (reason) {
        case DLL_PROCESS_ATTACH:
            g_module = module;
            DisableThreadLibraryCalls(module);
            CreateThread(nullptr, 0, bridge_init_thread, nullptr, 0, nullptr);
            break;
        case DLL_PROCESS_DETACH:
            // Tear down the IPC pipes so the UI cleanly sees disconnect
            // rather than hitting a stuck handle on FH6 exit.
            g_ipc_server.stop();
            g_pcm_pipe.stop();
            // TODO: signal bridge_init_thread to stop and join.
            break;
    }
    return TRUE;
}

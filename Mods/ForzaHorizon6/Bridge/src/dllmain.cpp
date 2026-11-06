#include <windows.h>

// version.dll proxy forwarders live in src/version_proxy.cpp.

#include <algorithm>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <filesystem>
#include <horizon/fmod/bridge.hpp>
#include <horizon/fmod/resolver.hpp>
#include <horizon/fmod/signatures.hpp>
#include <horizon/fmod/system_resolver.hpp>
#include <horizon/game/station_targets.hpp>
#include <horizon/inject/game_resolver.hpp>
#include <horizon/inject/metadata_injector.hpp>
#include <horizon/inject/msvc_string.hpp>
#include <horizon/inject/safe_mem.hpp>
#include <horizon/inject/sigscan.hpp>
#include <horizon/inject/title_write_controller.hpp>
#include <horizon/ipc/ipc_server.hpp>
#include <horizon/ipc/pcm_pipe_server.hpp>
#include <memory>
#include <mutex>
#include <string>
#include <system_error>
#include <thread>

namespace {

using horizon::fmod::FmodBridge;
using horizon::fmod::FmodResolver;
using horizon::inject::MetadataInjector;
using horizon::inject::PeImage;

// Track info the UI sends via {"cmd":"set_track"}; the DLL just stores it.
struct TrackInfo {
    std::string id; // canonical id from the source (e.g. "spotify:track:abc"); blank for local
    std::string title;
    std::string artist;
    std::string album;
};

HMODULE g_module = nullptr;

// Cross-thread state; see docs/architecture.md -> "Shared state". Published by
// the resolvers / IPC command handler, read by the periodic writer.
std::atomic<FmodBridge*>       g_bridge_for_push{nullptr};    // null until bridge is up
std::atomic<MetadataInjector*> g_metadata_injector{nullptr};  // null until resolved
std::atomic<void**>            g_radio_state_global{nullptr}; // slot; *it yields RadioState* or null
std::atomic<const void*>       g_radio_stream_vtable{nullptr};

// Latest track from set_track; the periodic writer re-applies it each tick.
std::mutex        g_track_mutex;
TrackInfo         g_current_track;
std::atomic<bool> g_have_track{false};
// False after clear_track (nothing playing on the routed channel): the writer
// puts the game's own title back instead of ours. Audio is unaffected. The next
// set_track turns it back on.
std::atomic<bool> g_show_title{true};

// Station targeting (set_target_station / set_target_stations). on_target gates
// DSP attach + metadata so other stations keep playing the game's own music.
// g_current_station is the tuned station's name (empty until first read),
// re-published to a reconnecting UI.
std::mutex                    g_target_mutex;
horizon::game::StationTargets g_targets; // default: any station
std::string                   g_current_station;
std::atomic<bool>             g_on_target_station{true}; // default: replace active

horizon::ipc::IpcServer     g_ipc_server; // lives for the DLL's lifetime; stop() on detach
horizon::ipc::PcmPipeServer g_pcm_pipe;   // UI writes s16 stereo frames here

// Active source identity (set_track); re-published to a reconnecting UI.
std::string g_active_source_id;
std::string g_active_source_display;

// One-line game-build fingerprint (.text size, PE timestamp, known/unknown).
// Written once by the init thread BEFORE the IPC server starts, read-only
// after, so the IPC thread's snapshot callback can read it without a lock.
std::string g_build_summary;

void log_w(const wchar_t* s) {
    OutputDebugStringW(s);
}

// printf-style wrapper over OutputDebugStringW. C-style varargs is the
// pragmatic choice here: a std::format migration would mean rewriting the
// %ls/%d format strings at every call site for a debug-only logger.
// NOLINTNEXTLINE(modernize-avoid-variadic-functions)
void logf(const wchar_t* fmt, ...) {
    wchar_t buf[512];
    va_list args; // NOLINT(cppcoreguidelines-init-variables) -- initialized by va_start
    va_start(args, fmt);
    _vsnwprintf_s(buf, sizeof(buf) / sizeof(buf[0]), _TRUNCATE, fmt, args);
    va_end(args);
    OutputDebugStringW(buf);
}

void publish_track_ipc(const TrackInfo& t, std::string_view source_id, std::string_view source_display) {
    horizon::ipc::IpcServer::TrackEvent ev{};
    ev.title          = t.title;
    ev.artist         = t.artist;
    ev.album          = t.album;
    ev.source_id      = std::string(source_id);
    ev.source_display = std::string(source_display);
    g_ipc_server.publish_track(ev);
}

// Construct the bridge if the required entry points resolve (else nullptr). It
// stays dormant until the periodic writer hands it a target. `verbose` emits the
// per-signature diagnostics; retries pass false so a slow unpack doesn't flood
// the log with the same dump every attempt.
std::unique_ptr<FmodBridge> bring_up_fmod(const PeImage& game_image, bool verbose) {
    FmodResolver resolver(game_image, horizon::fmod::signatures::kFh6);
    auto         hooks = resolver.resolve();

    if (!verbose && !resolver.report().ready())
        return nullptr;

    logf(L"[horizon-radio] resolver: createDsp=%d addDsp=%d removeDsp=%d "
         L"dspRelease=%d setMode=%d handleOpen=%d handleUnlock=%d\n",
         resolver.report().createDsp ? 1 : 0, resolver.report().addDsp ? 1 : 0, resolver.report().removeDsp ? 1 : 0,
         resolver.report().dspRelease ? 1 : 0, resolver.report().setMode ? 1 : 0, resolver.report().handleOpen ? 1 : 0,
         resolver.report().handleUnlock ? 1 : 0);

    // For each unresolved anchored signature, log which resolution stage failed
    // (anchor / lea / enclosing fn / prologue / ambiguous) to aid re-deriving it.
    auto diag_log = [&](const wchar_t* slot, const horizon::fmod::SignaturePattern& sig, bool resolved) {
        if (resolved)
            return;
        if (sig.anchor.empty()) {
            logf(L"[horizon-radio]   %ls: unresolved (direct pattern not found)\n", slot);
            return;
        }
        const auto     d     = horizon::inject::diagnose_function_by_anchor(game_image, sig.anchor, sig.pattern);
        const wchar_t* stage = L"?";
        switch (d.status) {
            using S = horizon::inject::AnchorResolution::Status;
        case S::ok:
            stage = L"ok";
            break;
        case S::no_anchor_string:
            stage = L"anchor string not in .rdata";
            break;
        case S::no_lea:
            stage = L"no lea reg, [rip+disp32] references";
            break;
        case S::no_enclosing_function:
            stage = L"lea not inside any .pdata function";
            break;
        case S::no_prologue_match:
            stage = L"prologue pattern(s) didn't match";
            break;
        case S::ambiguous:
            stage = L"multiple distinct functions match";
            break;
        }
        logf(L"[horizon-radio]   %ls: %ls "
             L"(anchors=%zu leas=%zu enclosing=%zu prologue_match=%zu)\n",
             slot, stage, d.anchor_count, d.lea_count, d.enclosing_fn_count, d.prologue_match_count);

        // On prologue-mismatch / ambiguous, dump each enclosing fn's first 24
        // bytes so the real prologue can be read off and the alternation widened.
        if (d.status == horizon::inject::AnchorResolution::Status::no_prologue_match ||
            d.status == horizon::inject::AnchorResolution::Status::ambiguous) {
            for (auto* fn : d.enclosing_functions) {
                if (fn == nullptr)
                    break;
                std::uint8_t bytes[24]{};
                if (!horizon::inject::safe_read_bytes(bytes, fn, sizeof(bytes)))
                    continue;
                wchar_t buf[256];
                int     off = swprintf_s(buf, L"[horizon-radio]     enclosing fn @ %p: ", fn);
                for (std::size_t i = 0; i < sizeof(bytes) && off < 240; ++i) {
                    off += swprintf_s(buf + off, std::size(buf) - off, L"%02X ", bytes[i]);
                }
                swprintf_s(buf + off, std::size(buf) - off, L"\n");
                OutputDebugStringW(buf);
            }
        }
    };
    diag_log(L"createDsp", horizon::fmod::signatures::kFh6.createDsp, resolver.report().createDsp);
    diag_log(L"addDsp", horizon::fmod::signatures::kFh6.addDsp, resolver.report().addDsp);
    diag_log(L"removeDsp", horizon::fmod::signatures::kFh6.removeDsp, resolver.report().removeDsp);
    diag_log(L"dspRelease", horizon::fmod::signatures::kFh6.dspRelease, resolver.report().dspRelease);
    diag_log(L"setMode", horizon::fmod::signatures::kFh6.setMode, resolver.report().setMode);
    diag_log(L"handleOpen", horizon::fmod::signatures::kFh6.handleOpen, resolver.report().handleOpen);
    diag_log(L"handleUnlock", horizon::fmod::signatures::kFh6.handleUnlock, resolver.report().handleUnlock);

    // Resolved addresses so we can sanity-check them against IDA/Ghidra.
    // The function-start RVA matters because we call into it directly
    // (createDsp(...) etc.); a wrong address with a matching-by-luck
    // prologue would crash the game thread that calls it.
    logf(L"[horizon-radio] resolver addrs: createDsp=%p addDsp=%p removeDsp=%p dspRelease=%p\n",
         reinterpret_cast<void*>(hooks.createDsp), reinterpret_cast<void*>(hooks.addDsp),
         reinterpret_cast<void*>(hooks.removeDsp), reinterpret_cast<void*>(hooks.dspRelease));
    logf(L"[horizon-radio] resolver addrs: setMode=%p handleOpen=%p handleUnlock=%p\n",
         reinterpret_cast<void*>(hooks.setMode), reinterpret_cast<void*>(hooks.handleOpen),
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
            auto        hit = horizon::inject::find_function_by_anchor(game_image, sig.anchor, sig.pattern);
            return reinterpret_cast<horizon::fmod::SystemCreateDspFn>(const_cast<std::byte*>(hit));
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
std::unique_ptr<MetadataInjector> bring_up_metadata(const PeImage& game_image, bool verbose) {
    if (horizon::inject::signatures::kFh6Metadata.class_mangled_name.empty()) {
        if (verbose) {
            log_w(L"[horizon-radio] metadata injector dormant: kFh6Metadata.class_mangled_name "
                  L"is empty (no offsets configured yet).\n");
        }
        return nullptr;
    }

    auto inj = std::make_unique<MetadataInjector>(game_image, horizon::inject::signatures::kFh6Metadata);
    if (!inj->resolve()) {
        if (verbose) {
            log_w(L"[horizon-radio] metadata injector: RTTI lookup failed for configured class; "
                  L"verify kFh6Metadata.class_mangled_name against the live game.\n");
        }
        return nullptr;
    }
    log_w(L"[horizon-radio] MetadataInjector resolved; on_track will update game HUD\n");
    return inj;
}

// SEH-protected read of a pointer-sized slot. Returns nullptr if the
// address itself is unmapped. Thin wrapper over safe_read_qword for
// readability at call sites.
void* safe_deref_slot(void** slot) noexcept {
    if (slot == nullptr)
        return nullptr;
    return reinterpret_cast<void*>(horizon::inject::safe_read_qword(static_cast<const void*>(slot)));
}

// Human-readable reason for a failed RadioState lookup (for the log).
const wchar_t* describe(horizon::game::GlobalResolution::Status status) {
    using S = horizon::game::GlobalResolution::Status;
    switch (status) {
    case S::ok:
        return L"ok";
    case S::empty_pattern:
        return L"no pattern configured";
    case S::not_found:
        return L"pattern not found in .text";
    case S::ambiguous:
        return L"pattern matched more than once";
    case S::unexpected_instruction:
        return L"no `mov rbx, [rip+disp32]` at the configured offset";
    case S::target_outside_image:
        return L"decoded address is outside the game module";
    }
    return L"?";
}

// Sigscan-based RadioState global resolution. Returns the address of
// the RadioState global slot (the QWORD that holds RadioState*), or
// nullptr if the pattern is missing / ambiguous / drifted in the FH6 build.
void* resolve_radiostate_global(const PeImage& game_image, bool verbose) {
    const horizon::game::GameResolver resolver(game_image);
    const auto&                       sig = horizon::game::signatures::kFh6Game;
    const auto res  = resolver.resolve_global_via_rip_load(sig.radio_state_loader, sig.mov_rbx_offset_in_match);
    void*      slot = res.address;
    if (slot == nullptr) {
        if (verbose) {
            const wchar_t* why = describe(res.status);
            logf(L"[horizon-radio] game-resolver: radio_state_loader %ls (matches=%zu); "
                 L"RadioState global unresolved\n",
                 why, res.match_count);
        }
        return nullptr;
    }
    logf(L"[horizon-radio] game-resolver: RadioState global slot at %p\n", slot);

    // Probe the slot once now -- if the resolved address isn't even
    // committed-readable, we know the sigscan went sideways without
    // waiting 10s for the periodic writer to find out.
    const void* probe = safe_deref_slot(static_cast<void**>(slot));
    logf(L"[horizon-radio] game-resolver: initial read of slot yields %p "
         L"(may be null until game initializes RadioState)\n",
         probe);
    return slot;
}

// The three independently-resolved pieces of the in-game integration. Owned by
// the init thread; each piece is published to its atomic the moment it
// resolves, so the writer / discovery / PCM threads pick it up without
// restarting. See docs/architecture.md -> "Signature resolution".
struct BringUp {
    std::unique_ptr<FmodBridge>       bridge;
    std::unique_ptr<MetadataInjector> injector;
    bool                              radio_state = false;

    [[nodiscard]] bool complete() const noexcept {
        return bridge && injector && radio_state;
    }
};

// Resolve whatever is still missing. Safe to call repeatedly: resolved pieces
// are skipped. The packed game image decrypts its code at load, so a scan that
// runs before that finishes misses signatures that resolve fine moments later;
// retrying covers that without needing a game restart.
void try_bring_up(const PeImage& game_image, BringUp& s, bool verbose) {
    if (!s.bridge) {
        s.bridge = bring_up_fmod(game_image, verbose);
        // The bridge has no targets yet, so install() would fail; publish it
        // anyway so push_pcm() buffers PCM for when targets arrive.
        if (s.bridge)
            g_bridge_for_push.store(s.bridge.get(), std::memory_order_release);
    }
    if (!s.injector) {
        s.injector = bring_up_metadata(game_image, verbose);
        if (s.injector) {
            // Cache the resolved vtable for the heap-arena instance scan, then
            // publish the injector (vtable first: readers gate on the injector).
            if (auto vt = s.injector->vtable())
                g_radio_stream_vtable.store(vt->address, std::memory_order_release);
            g_metadata_injector.store(s.injector.get(), std::memory_order_release);
        }
    }
    if (!s.radio_state) {
        // Independent of the injector: event detection only needs RadioState.
        if (void* slot = resolve_radiostate_global(game_image, verbose)) {
            g_radio_state_global.store(static_cast<void**>(slot), std::memory_order_release);
            s.radio_state = true;
        }
    }
}

// Set once the first (verbose) bring-up attempt has finished. The IPC server
// starts before it, so a UI that connects early must be told "still scanning"
// rather than a misleading "MISSING" for pieces that simply haven't been tried.
std::atomic<bool> g_bring_up_attempted{false};

// Live bring-up status, readable from any thread (derived from the atomics).
std::string bring_up_status() {
    if (!g_bring_up_attempted.load(std::memory_order_acquire))
        return "bring-up: scanning game code (first attempt in progress)";
    std::string out = "bring-up: fmod=";
    out += g_bridge_for_push.load(std::memory_order_acquire) != nullptr ? "ok" : "MISSING";
    out += " metadata=";
    out += g_metadata_injector.load(std::memory_order_acquire) != nullptr ? "ok" : "MISSING";
    out += " radio_state=";
    out += g_radio_state_global.load(std::memory_order_acquire) != nullptr ? "ok" : "MISSING";
    return out;
}

// Follow radio_state -> +chain0 -> +chain1 -> the station sub-object and
// read the station NAME (MSVC std::string @ +name_off). It lives two
// pointer-hops away, which is why it never showed up watching RadioState
// directly. Returns true if the name resolved.
bool read_station_state(std::uintptr_t base, std::string& out_name) {
    using horizon::inject::MsvcString;
    using horizon::inject::safe_read_bytes;
    using horizon::inject::safe_read_qword;
    const auto& g = horizon::game::signatures::kFh6Game;

    const auto p1 = safe_read_qword(reinterpret_cast<const void*>(base + g.station_chain0_offset));
    if (p1 == 0)
        return false;
    const auto p2 = safe_read_qword(reinterpret_cast<const void*>(p1 + g.station_chain1_offset));
    if (p2 == 0)
        return false;

    MsvcString s{};
    if (!safe_read_bytes(&s, reinterpret_cast<const void*>(p2 + g.station_name_offset), sizeof(s)))
        return false;
    // Plausibility (same shape the metadata injector uses): cap >= 15 (SSO
    // floor), size <= cap, sane length.
    if (s.capacity < 15 || s.size > s.capacity || s.size > 256)
        return false;

    char buf[257] = {};
    if (s.capacity <= 15) {
        std::memcpy(buf, s.u.buf, s.size); // SSO: chars inline in the copy
    } else if (!safe_read_bytes(buf, s.u.ptr, s.size)) {
        return false; // heap: chars live at u.ptr
    }
    out_name.assign(buf, s.size);
    return true;
}

// Poll the RadioState singleton for in-game events and publish them to the
// UI over IPC. Called from the periodic writer thread (single caller), so
// the edge-detection statics are safe without locking. Offsets live in
// kFh6Game; verify them against the one-shot dump this emits on first read.
void poll_game_events(void* radio_state) {
    if (radio_state == nullptr)
        return;

    using horizon::inject::safe_read_bytes;

    const auto& g    = horizon::game::signatures::kFh6Game;
    const auto  base = reinterpret_cast<std::uintptr_t>(radio_state);

    // Race active flags (both nonzero => in a race). Edge-detect start/finish.
    std::uint8_t ra = 0, rb = 0;
    safe_read_bytes(&ra, reinterpret_cast<const void*>(base + g.race_active_a_offset), 1);
    safe_read_bytes(&rb, reinterpret_cast<const void*>(base + g.race_active_b_offset), 1);
    const bool active = (ra != 0) && (rb != 0);

    static bool s_race_init = false;
    static bool s_active    = false;
    if (!s_race_init) {
        s_race_init = true;
        s_active    = active;
    } else if (active != s_active) {
        s_active = active;
        g_ipc_server.publish_game_event(active ? "race_start" : "race_finish");
    }

    // Which station is tuned in. Drives station_changed / the station state
    // event and the targeting gate (only replace the stations the UI chose).
    // The name changes on station switching but NOT on radio power off,
    // which is why on/off isn't a supported event (see notes in git log).
    std::string name;
    if (!read_station_state(base, name) || name.empty())
        return;

    bool changed = false;
    bool first   = false;
    {
        const std::scoped_lock lock(g_target_mutex);
        g_on_target_station.store(g_targets.targets(name), std::memory_order_release);
        changed = name != g_current_station;
        if (changed) {
            first             = g_current_station.empty();
            g_current_station = name;
        }
    }
    // Publish outside the lock (the IPC server takes its own).
    if (changed) {
        g_ipc_server.publish_station(name);
        // The first read isn't a change the player made; don't fire the event.
        if (!first)
            g_ipc_server.publish_game_event("station_changed");
    }
}

[[noreturn]] DWORD WINAPI bridge_init_thread(LPVOID) {
    log_w(L"[horizon-radio] init thread started\n");

    // PCM ingress: forward UI frames to the bridge. The first chunk also plants
    // a stub track so g_have_track flips true and the DSP install path proceeds
    // (nothing else fires it now that sources live in C#); real metadata follows
    // over IPC. See docs/architecture.md -> "Shared state".
    g_pcm_pipe.start([](const std::int16_t* frames, std::size_t frame_count) {
        if (auto* b = g_bridge_for_push.load(std::memory_order_acquire)) {
            b->push_pcm(frames, frame_count);
        }
        if (!g_have_track.load(std::memory_order_acquire)) {
            const std::scoped_lock lock(g_track_mutex);
            if (g_current_track.title.empty()) {
                g_current_track.title  = "Horizon Radio";
                g_current_track.artist = "";
            }
            g_have_track.store(true, std::memory_order_release);
        }
    });

    // Inbound JSON commands from the UI (set_track/set_gain/set_target_station).
    // Flat JSON, so we scan for keys rather than pull in a parser.
    auto json_extract_string = [](const std::string& line, std::string_view key, std::string& out) -> bool {
        std::string needle = "\"";
        needle.append(key);
        needle.append("\":");
        auto pos = line.find(needle);
        if (pos == std::string::npos)
            return false;
        pos += needle.size();
        while (pos < line.size() && (line[pos] == ' ' || line[pos] == '\t'))
            ++pos;
        if (pos >= line.size() || line[pos] != '"')
            return false;
        ++pos;
        std::string value;
        value.reserve(64);
        while (pos < line.size()) {
            const char c = line[pos++];
            if (c == '\\' && pos < line.size()) {
                const char esc = line[pos++];
                switch (esc) {
                case '"':
                    value.push_back('"');
                    break;
                case '\\':
                    value.push_back('\\');
                    break;
                case '/':
                    value.push_back('/');
                    break;
                case 'b':
                    value.push_back('\b');
                    break;
                case 'f':
                    value.push_back('\f');
                    break;
                case 'n':
                    value.push_back('\n');
                    break;
                case 'r':
                    value.push_back('\r');
                    break;
                case 't':
                    value.push_back('\t');
                    break;
                case 'u': {
                    if (pos + 4 > line.size())
                        return false;
                    unsigned cp = 0;
                    for (int i = 0; i < 4; ++i) {
                        const char     hex = line[pos++];
                        const unsigned d   = (hex >= '0' && hex <= '9')   ? hex - '0'
                                             : (hex >= 'a' && hex <= 'f') ? hex - 'a' + 10
                                             : (hex >= 'A' && hex <= 'F') ? hex - 'A' + 10
                                                                          : 16u;
                        if (d > 15)
                            return false;
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
                default:
                    value.push_back(esc);
                    break;
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

    // Extract a bare (unquoted) JSON number value, e.g. the gain in
    // {"cmd":"set_gain","gain":0.3}. json_extract_string only handles
    // quoted strings, so set_gain needs this.
    auto json_extract_number = [](const std::string& line, std::string_view key, double& out) -> bool {
        std::string needle = "\"";
        needle.append(key);
        needle.append("\":");
        auto pos = line.find(needle);
        if (pos == std::string::npos)
            return false;
        pos += needle.size();
        while (pos < line.size() && (line[pos] == ' ' || line[pos] == '\t'))
            ++pos;
        // from_chars, not strtod: locale-independent. The UI sends '.' as the
        // decimal separator (InvariantCulture); strtod honors LC_NUMERIC, so on
        // a comma-locale host it would stop at the '.' and read 0 -> silence.
        const char* first    = line.c_str() + pos;
        const char* last     = line.c_str() + line.size();
        double      v        = 0.0;
        const auto [ptr, ec] = std::from_chars(first, last, v);
        if (ptr == first || ec != std::errc())
            return false;
        out = v;
        return true;
    };

    g_ipc_server.set_command_callback([json_extract_string, json_extract_number](const std::string& line) {
        std::string cmd;
        if (!json_extract_string(line, "cmd", cmd))
            return;

        if (cmd == "set_track") {
            std::string title, artist, album, source_id, source_display, external_id;
            json_extract_string(line, "title", title);
            json_extract_string(line, "artist", artist);
            json_extract_string(line, "album", album);
            json_extract_string(line, "source_id", source_id);
            json_extract_string(line, "source_display", source_display);
            json_extract_string(line, "external_id", external_id);

            TrackInfo snapshot;
            {
                const std::scoped_lock lock(g_track_mutex);
                g_current_track.title  = title;
                g_current_track.artist = artist;
                g_current_track.album  = album;
                g_current_track.id     = external_id;
                g_have_track.store(true, std::memory_order_release);
                g_show_title.store(true, std::memory_order_release);
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

            const std::wstring wtitle(title.begin(), title.end());
            logf(L"[horizon-radio] cmd set_track: %ls\n", wtitle.c_str());
        } else if (cmd == "clear_track") {
            // Nothing is playing on the channel the UI routed to: stop showing
            // our (now stale) title and restore the game's.
            {
                const std::scoped_lock lock(g_track_mutex);
                g_current_track = TrackInfo{};
            }
            g_show_title.store(false, std::memory_order_release);
            log_w(L"[horizon-radio] cmd clear_track\n");
        } else if (cmd == "set_gain") {
            // Events "set volume / duck" action: scale the bridge's
            // master output gain. Clamped to [0, 1].
            double gain = 1.0;
            if (json_extract_number(line, "gain", gain)) {
                if (gain < 0.0)
                    gain = 0.0;
                if (gain > 1.0)
                    gain = 1.0;
                if (auto* b = g_bridge_for_push.load(std::memory_order_acquire))
                    b->set_master_gain(static_cast<float>(gain));
                logf(L"[horizon-radio] cmd set_gain: %.3f\n", gain);
            }
        } else if (cmd == "set_master_volume") {
            // In-app volume slider acting as a pre-amp on the in-game
            // audio. Separate stage from set_gain (the Events duck) so
            // the two don't clobber each other. Clamped to [0, 1].
            double volume = 1.0;
            if (json_extract_number(line, "volume", volume)) {
                if (volume < 0.0)
                    volume = 0.0;
                if (volume > 1.0)
                    volume = 1.0;
                if (auto* b = g_bridge_for_push.load(std::memory_order_acquire))
                    b->set_user_volume(static_cast<float>(volume));
                logf(L"[horizon-radio] cmd set_master_volume: %.3f\n", volume);
            }
        } else if (cmd == "set_target_station") {
            // Legacy: one station to replace, or "" / "*" for whatever is active.
            std::string station;
            json_extract_string(line, "station", station);
            {
                const std::scoped_lock lock(g_target_mutex);
                g_targets.set_single(station);
            }
            const std::wstring ws(station.begin(), station.end());
            logf(L"[horizon-radio] cmd set_target_station: '%ls'\n", ws.c_str());
        } else if (cmd == "set_target_stations") {
            // The stations to replace, newline-separated; empty = none (every
            // station plays the game's own music).
            std::string stations;
            json_extract_string(line, "stations", stations);
            std::size_t count = 0;
            {
                const std::scoped_lock lock(g_target_mutex);
                g_targets.set_list(stations);
                count = g_targets.stations().size();
            }
            logf(L"[horizon-radio] cmd set_target_stations: %zu station(s)\n", count);
        }
    });

    // Function-local statics outlive the thread function and tie their
    // lifetime to the process. The init thread blocks forever once
    // setup is done, so the bring-up state is effectively global for
    // the life of the DLL. PE headers are never packed, so the image
    // parses (and fingerprints) fine even before the game's code decrypts.
    static PeImage game_image(GetModuleHandleW(nullptr));
    static BringUp state;

    if (game_image.valid()) {
        const auto             text_size = game_image.text().size();
        const auto             label     = horizon::game::signatures::known_build_label(text_size);
        const std::string_view shown     = label.empty() ? std::string_view{"UNKNOWN build, not yet verified"} : label;
        char                   buf[256];
        const int n = std::snprintf(buf, sizeof(buf), "game build: .text=0x%zX timestamp=0x%08X (%.*s)", text_size,
                                    game_image.timestamp(), static_cast<int>(shown.size()), shown.data());
        g_build_summary.assign(buf, n > 0 ? static_cast<std::size_t>(n) : 0);
    } else {
        g_build_summary = "game build: failed to parse game PE";
    }

    g_ipc_server.set_snapshot_callback([] {
        if (!g_active_source_id.empty()) {
            g_ipc_server.publish_source_changed(g_active_source_id, g_active_source_display);
        }
        if (g_have_track.load(std::memory_order_acquire) && g_show_title.load(std::memory_order_acquire)) {
            TrackInfo snapshot;
            {
                const std::scoped_lock lock(g_track_mutex);
                snapshot = g_current_track;
            }
            publish_track_ipc(snapshot, g_active_source_id, g_active_source_display);
        }
        std::string station;
        {
            const std::scoped_lock lock(g_target_mutex);
            station = g_current_station;
        }
        if (!station.empty())
            g_ipc_server.publish_station(station);
        // Surface the build fingerprint + what resolved in the UI Console on
        // every (re)connect: after a game update this is the first place to
        // look, and it needs no debugger.
        g_ipc_server.publish_debug("bridge", g_build_summary);
        g_ipc_server.publish_debug("bridge", bring_up_status());
    });
    g_ipc_server.start();

    if (!game_image.valid()) {
        log_w(L"[horizon-radio] failed to parse game PE; FMOD bridge + metadata dormant\n");
    } else {
        logf(L"[horizon-radio] game image: base=%p size=%zu .text=%zu (0x%zX) .rdata=%zu .data=%zu "
             L"timestamp=0x%08X\n",
             reinterpret_cast<void*>(game_image.base()), game_image.image_size(), game_image.text().size(),
             game_image.text().size(), game_image.rdata().size(), game_image.data().size(), game_image.timestamp());
        if (horizon::game::signatures::known_build_label(game_image.text().size()).empty()) {
            log_w(L"[horizon-radio] WARNING: unknown FH6 build (.text size not in kFh6KnownBuilds). "
                  L"If titles/audio don't work, re-verify signatures with the discovery dump.\n");
        }

        try_bring_up(game_image, state, /*verbose=*/true);
        g_bring_up_attempted.store(true, std::memory_order_release);
        // Report the outcome to an already-connected UI (later connects get it
        // from the snapshot callback).
        g_ipc_server.publish_debug("bridge", bring_up_status());
        if (!state.complete())
            log_w(L"[horizon-radio] bring-up incomplete; retrying with backoff (the game may still be unpacking)\n");

        const auto& cfg = horizon::inject::signatures::kFh6Metadata;
        const bool  offsets_configured =
            cfg.sound_name_offset.has_value() || cfg.display_name_offset.has_value() || cfg.artist_offset.has_value();

        if (offsets_configured) {
            log_w(L"[horizon-radio] metadata: periodic writer arming "
                  L"(first attempt in 10s, then 5 Hz)\n");
            std::thread([] {
                std::this_thread::sleep_for(std::chrono::seconds(10));

                // 20 Hz: transitions feel instant; a steady-state tick is microseconds.
                constexpr auto kTickInterval  = std::chrono::milliseconds(50);
                constexpr int  kRescanIters   = 100; // ~5 s between heap scans
                int            last_write_n   = -1;
                bool           last_installed = false;
                int            iter           = 0;
                while (true) {
                    auto*       inj     = g_metadata_injector.load(std::memory_order_acquire);
                    auto*       rs_slot = g_radio_state_global.load(std::memory_order_acquire);
                    const void* vt      = g_radio_stream_vtable.load(std::memory_order_acquire);

                    // In-game event detection runs whenever the RadioState
                    // global is resolved — independent of metadata-injector
                    // state or whether we currently have a track.
                    if (rs_slot)
                        poll_game_events(safe_deref_slot(rs_slot));

                    // Persists across ticks; hoisted above the have-track gate so
                    // on_idle still restores once the source stops. See TitleWriteController.
                    static horizon::inject::TitleWriteController title_writer;

                    if (inj && rs_slot && vt && g_have_track.load(std::memory_order_acquire)) {
                        TrackInfo t;
                        {
                            const std::scoped_lock lock(g_track_mutex);
                            t = g_current_track;
                        }
                        const std::string sound = t.id.empty() ? t.title : t.id;

                        // Only replace the station the user targeted (empty
                        // target = replace whatever's active). When off our
                        // station we skip metadata + detach the DSP so the
                        // game's own station plays untouched.
                        const bool on_target = g_on_target_station.load(std::memory_order_acquire);

                        const void* radio_state = safe_deref_slot(rs_slot);

                        // Cache the instance list; the heap-arena scan is too
                        // expensive to redo every tick. Scan every ~4 ticks
                        // until found (fast attach), then back off to
                        // kRescanIters once we have instances.
                        static std::vector<const void*> cached_instances;
                        // Read-only alias; the scan reassigns cached_instances directly below.
                        const std::vector<const void*>& instances     = cached_instances;
                        const int                       scan_interval = cached_instances.empty() ? 4 : kRescanIters;
                        if (radio_state && (iter % scan_interval == 0)) {
                            const auto t0    = std::chrono::steady_clock::now();
                            cached_instances = horizon::game::find_instances_in_heap_arenas(vt, game_image);
                            const auto ms    = std::chrono::duration_cast<std::chrono::milliseconds>(
                                                   std::chrono::steady_clock::now() - t0)
                                                   .count();
                            wchar_t    scan_msg[200];
                            swprintf_s(scan_msg, L"[horizon-radio] heap-arena scan: %zu candidates in %lldms\n",
                                       cached_instances.size(), static_cast<long long>(ms));
                            OutputDebugStringW(scan_msg);
                        }

                        // Lock onto one instance so multiple chain-valid
                        // instances (e.g. music + host segment) don't make us
                        // ping-pong; only re-pick when it leaves the scan.
                        static const void* preferred_instance = nullptr;
                        if (preferred_instance) {
                            bool still_present = false;
                            for (auto* inst : instances) {
                                if (inst == preferred_instance) {
                                    still_present = true;
                                    break;
                                }
                            }
                            if (!still_present)
                                preferred_instance = nullptr;
                        }

                        // Pick the instance whose FMOD system resolves (the audible
                        // station), so the title lands where the audio is and not on
                        // other loaded stations' blocks (they share the vtable).
                        auto fmod_resolves = [&](const void* inst) {
                            auto* rs = const_cast<std::byte*>(static_cast<const std::byte*>(inst) + 0x10);
                            return horizon::fmod::resolve_fmod_system_from_stream(game_image, rs) != nullptr;
                        };
                        // Diagnostic (Console tag "titles"): the tuned station vs. the
                        // station each audible block belongs to (its event id), logged on
                        // change with a timestamp. Maps event-id station codes to stations
                        // and shows the tuning lag. Read-only.
                        {
                            static const auto  t0 = std::chrono::steady_clock::now();
                            static std::string last_diag;
                            std::string        tuned;
                            {
                                const std::scoped_lock lock(g_target_mutex);
                                tuned = g_current_station;
                            }
                            std::string line = "tuned=";
                            line += tuned.empty() ? std::string("?") : tuned;
                            line += on_target ? " (ours)" : " (game)";
                            line += " audible=";
                            bool any = false;
                            for (const void* inst : instances) {
                                if (!fmod_resolves(inst))
                                    continue;
                                std::string id;
                                if (!inj->read_event_id(inst, id))
                                    id = "?";
                                char where[64];
                                std::snprintf(where, sizeof(where), "@%p->%p", inst, inj->resolve_block(inst));
                                line += any ? ", " : "";
                                line += id;
                                line += where;
                                any = true;
                            }
                            if (!any)
                                line += "none";
                            if (line != last_diag) {
                                last_diag                 = line;
                                const auto        ms      = std::chrono::duration_cast<std::chrono::milliseconds>(
                                                                std::chrono::steady_clock::now() - t0)
                                                                .count();
                                const std::string stamped = "[+" + std::to_string(ms) + " ms] " + line;
                                g_ipc_server.publish_debug("titles", stamped);
                                std::wstring wide(L"[horizon-radio] titles: ");
                                for (const char c : stamped)
                                    wide.push_back(static_cast<wchar_t>(static_cast<unsigned char>(c)));
                                wide.push_back(L'\n');
                                OutputDebugStringW(wide.c_str());
                            }
                        }

                        const void* active_instance = nullptr;
                        if (on_target) {
                            if (preferred_instance && fmod_resolves(preferred_instance)) {
                                active_instance = preferred_instance;
                            } else {
                                for (const void* inst : instances) {
                                    if (fmod_resolves(inst)) {
                                        active_instance = inst;
                                        break;
                                    }
                                }
                                preferred_instance = active_instance;
                            }
                        }

                        int n = 0;
                        if (g_show_title.load(std::memory_order_acquire))
                            n = title_writer.on_active(*inj, active_instance, sound, t.title, t.artist);
                        else
                            title_writer.on_idle(*inj);

                        // Log only on transitions (keeps DebugView readable).
                        if (n != last_write_n) {
                            wchar_t msg[240];
                            swprintf_s(msg,
                                       L"[horizon-radio] periodic: write=%d->%d "
                                       L"instances=%zu radio_state=%p iter=%d\n",
                                       last_write_n, n, instances.size(), radio_state, iter);
                            OutputDebugStringW(msg);
                            last_write_n = n;
                        }

                        // Audio bridge: resolve FMOD System* from the stream
                        // and hand both to the bridge; tick() installs /
                        // retargets the DSP on the stream's channel.
                        auto* bridge_for_install = g_bridge_for_push.load(std::memory_order_acquire);
                        // Track resolve state so we only log on transitions.
                        static bool last_sys_resolved = true;
                        if (!on_target && bridge_for_install) {
                            // Off the targeted station: detach our DSP so
                            // the game's own station audio plays untouched.
                            // Drop the preferred instance so we re-pick when
                            // the user tunes back to our station.
                            bridge_for_install->set_target(nullptr, nullptr);
                            bridge_for_install->tick();
                            preferred_instance = nullptr;
                        } else if (active_instance && bridge_for_install) {
                            auto* radio_stream =
                                const_cast<std::byte*>(static_cast<const std::byte*>(active_instance) + 0x10);
                            auto* sys = horizon::fmod::resolve_fmod_system_from_stream(game_image, radio_stream);
                            if (sys == nullptr) {
                                // Radio off / channel dead: clear the target so
                                // the DSP uninstalls cleanly (a stale handle
                                // would make removeDsp hit a destroyed channel
                                // on recovery).
                                bridge_for_install->set_target(nullptr, nullptr);
                                bridge_for_install->tick();
                                if (last_sys_resolved) {
                                    wchar_t smsg[200];
                                    swprintf_s(smsg,
                                               L"[horizon-radio] bridge: system unresolvable "
                                               L"(radio off?); uninstalling DSP iter=%d\n",
                                               iter);
                                    OutputDebugStringW(smsg);
                                    last_sys_resolved = false;
                                }
                            } else {
                                if (!last_sys_resolved) {
                                    wchar_t smsg[200];
                                    swprintf_s(smsg,
                                               L"[horizon-radio] bridge: system resolved again "
                                               L"iter=%d\n",
                                               iter);
                                    OutputDebugStringW(smsg);
                                    last_sys_resolved = true;
                                }
                                bridge_for_install->set_target(sys, radio_stream);
                                bridge_for_install->tick();
                                const bool now_installed = bridge_for_install->installed();
                                if (now_installed != last_installed) {
                                    wchar_t bmsg[240];
                                    swprintf_s(bmsg,
                                               L"[horizon-radio] bridge: installed=%d "
                                               L"(sys=%p rs=%p) iter=%d\n",
                                               now_installed ? 1 : 0, sys, radio_stream, iter);
                                    OutputDebugStringW(bmsg);
                                    last_installed = now_installed;
                                    // No source restart on detach: the source
                                    // keeps running, so re-attach resumes from
                                    // wherever it is now (real-radio semantics).
                                }
                            }
                        }
                    } else if (inj) {
                        // Not writing this tick (no track / unresolved): put the
                        // game's original title back so a stopped source doesn't
                        // leave ours frozen on the station. write_to_instance is
                        // SEH-safe and re-checks the vptr, so restoring a block
                        // that was freed since is a no-op, not a crash.
                        title_writer.on_idle(*inj);
                    }
                    ++iter;
                    std::this_thread::sleep_for(kTickInterval);
                }
            }).detach();
        }

        // Discovery dump. Always available (even with offsets configured)
        // so we can re-verify the layout against a live build whose offsets
        // shifted under a game update. Opt-in via a trigger file so it never
        // runs unprompted. Output goes to both DebugView and the UI Console
        // (tag "discover") so it can be captured without a debugger attached.
        log_w(L"[horizon-radio] discovery: OPT-IN. To trigger one dump, create an "
              L"empty file named 'horizon-radio.discover' next to version.dll "
              L"(it's deleted after the dump; recreate to trigger again). "
              L"Output appears in the UI Console under the 'discover' tag.\n");
        std::thread([] {
            wchar_t module_path[MAX_PATH];
            if (GetModuleFileNameW(g_module, module_path, MAX_PATH) == 0)
                return;
            const std::filesystem::path trigger_path =
                std::filesystem::path(module_path).parent_path() / L"horizon-radio.discover";

            int dump_count = 0;
            while (true) {
                std::this_thread::sleep_for(std::chrono::seconds(2));

                std::error_code ec;
                if (!std::filesystem::exists(trigger_path, ec))
                    continue;

                // Delete first so a failed dump doesn't loop forever.
                std::filesystem::remove(trigger_path, ec);

                auto* inj = g_metadata_injector.load(std::memory_order_acquire);
                if (!inj)
                    continue;

                char      marker[96];
                const int mn = std::snprintf(marker, sizeof(marker), "--- discovery dump #%d ---", dump_count++);
                g_ipc_server.publish_debug("discover", std::string_view(marker, static_cast<std::size_t>(mn)));
                g_ipc_server.publish_debug("discover", "scanning heap (this can take a few seconds)...");
                OutputDebugStringW(L"[horizon-radio] discovery dump begin\n");

                const std::string dump = inj->dump_candidates();

                // Breadcrumb so we can tell an empty dump apart from a lost
                // one: report size before streaming the lines.
                char      szmsg[96];
                const int sn = std::snprintf(szmsg, sizeof(szmsg), "dump_candidates returned %zu bytes", dump.size());
                g_ipc_server.publish_debug("discover", std::string_view(szmsg, static_cast<std::size_t>(sn)));

                std::size_t pos     = 0;
                int         emitted = 0;
                while (pos < dump.size()) {
                    std::size_t end = dump.find('\n', pos);
                    if (end == std::string::npos)
                        end = dump.size();
                    const std::string_view line(dump.data() + pos, end - pos);
                    g_ipc_server.publish_debug("discover", line);
                    ++emitted;
                    std::wstring wide(L"[horizon-radio] ");
                    for (const char c : line)
                        wide.push_back(static_cast<wchar_t>(c));
                    wide.push_back(L'\n');
                    OutputDebugStringW(wide.c_str());
                    pos = end + 1;
                }

                char      endmsg[96];
                const int en = std::snprintf(endmsg, sizeof(endmsg), "--- discovery dump end (%d lines) ---", emitted);
                g_ipc_server.publish_debug("discover", std::string_view(endmsg, static_cast<std::size_t>(en)));
            }
        }).detach();
    }

    // Periodic loop: ticks at 2 Hz for IPC stats publishing (the UI
    // expects roughly this cadence), with a separate 30 s throttle on
    // the DebugView log lines so they don't drown the developer view.
    // It also retries an incomplete bring-up on a doubling backoff
    // (5 s -> 60 s), giving up after ~10 min: by then the game has long
    // finished unpacking, so whatever is still missing has drifted.
    std::uint64_t last_br_in = 0, last_br_out = 0, last_br_under = 0;
    std::uint64_t last_md_writes = 0;
    auto          next_debug_log = std::chrono::steady_clock::now() + std::chrono::seconds(30);

    constexpr auto kRetryMaxDelay   = std::chrono::seconds(60);
    constexpr auto kRetryGiveUp     = std::chrono::minutes(10);
    const auto     bring_up_started = std::chrono::steady_clock::now();
    auto           retry_delay      = std::chrono::seconds(5);
    auto           next_retry       = bring_up_started + retry_delay;
    int            retry            = 0;
    bool           retrying         = game_image.valid() && !state.complete();

    while (true) {
        std::this_thread::sleep_for(std::chrono::milliseconds(500));

        if (retrying && std::chrono::steady_clock::now() >= next_retry) {
            try_bring_up(game_image, state, /*verbose=*/false);
            ++retry;
            const auto status = bring_up_status();
            logf(L"[horizon-radio] bring-up retry #%d: %hs\n", retry, status.c_str());
            if (state.complete()) {
                log_w(L"[horizon-radio] bring-up complete\n");
                g_ipc_server.publish_debug("bridge", status + " (complete after retry)");
                retrying = false;
            } else if (std::chrono::steady_clock::now() - bring_up_started >= kRetryGiveUp) {
                // One last verbose pass so the log holds the per-signature
                // diagnostics for whatever is still missing.
                try_bring_up(game_image, state, /*verbose=*/true);
                log_w(L"[horizon-radio] bring-up: giving up on retries; the missing signatures have likely "
                      L"drifted with a game update (see AGENTS.md -> In-game title injection)\n");
                g_ipc_server.publish_debug(
                    "bridge", bring_up_status() + " (gave up retrying; signatures likely drifted with a game update)");
                retrying = false;
            } else {
                retry_delay = std::min(retry_delay * 2, kRetryMaxDelay);
                next_retry  = std::chrono::steady_clock::now() + retry_delay;
            }
        }

        if (const auto& bridge = state.bridge) {
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
            if (now >= next_debug_log && (fi != last_br_in || fo != last_br_out || un != last_br_under)) {
                logf(L"[horizon-radio] bridge: installed=%d frames_in=%llu frames_out=%llu underruns=%llu\n",
                     bridge->installed() ? 1 : 0, static_cast<unsigned long long>(fi),
                     static_cast<unsigned long long>(fo), static_cast<unsigned long long>(un));
                last_br_in     = fi;
                last_br_out    = fo;
                last_br_under  = un;
                next_debug_log = now + std::chrono::seconds(30);
            }
        }
        if (const auto& injector = state.injector) {
            const auto w = injector->total_writes();
            if (w != last_md_writes && std::chrono::steady_clock::now() >= next_debug_log) {
                logf(L"[horizon-radio] metadata: total_writes=%llu\n", static_cast<unsigned long long>(w));
                last_md_writes = w;
            }
        }
    }
    // Unreachable: the loop above never breaks. The function is [[noreturn]];
    // a trailing `return` would trip C4645 / clang-diagnostic-invalid-noreturn.
}

// A proxy version.dll sitting next to our companion UI is pulled into the
// UI's own process by the normal DLL search order (version.dll isn't a
// KnownDLL). We must stay inert there — otherwise the IPC server starts
// in-process and the UI "connects" to itself, faking a Forza link.
//
// We can't tell "is this FH6?" by signature this early: the game image is
// packed and only unpacks later, so its code/signatures aren't present at
// DLL_PROCESS_ATTACH. The host exe NAME is known at load time regardless,
// so we gate on that — and only refuse our own UI rather than allow-listing
// a game exe name we can't verify. "HorizonRadio*" can never match the
// game (ForzaHorizon6.exe), so this is zero-risk to the real injection.
bool host_is_companion_ui() {
    wchar_t     path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(nullptr, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH)
        return false;
    const wchar_t* base = wcsrchr(path, L'\\');
    base                = base ? base + 1 : path;
    if (wcslen(base) < 12)
        return false;
    // Case-insensitive "starts with HorizonRadio" via the Win32 API so we
    // don't depend on _wcsnicmp differing across MSVC vs clang+MinGW.
    return CompareStringOrdinal(base, 12, L"HorizonRadio", 12, TRUE) == CSTR_EQUAL;
}

} // namespace

// DllMain is the loader-invoked entry point; it must keep external linkage.
// NOLINTNEXTLINE(misc-use-internal-linkage)
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID /*reserved*/) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        g_module = module;
        DisableThreadLibraryCalls(module);
        // Stay completely inert when our own UI loads the proxy (see
        // host_is_companion_ui) so the IPC server never self-"connects".
        if (host_is_companion_ui())
            break;
        CreateThread(nullptr, 0, bridge_init_thread, nullptr, 0, nullptr);
        break;
    case DLL_PROCESS_DETACH:
        // Tear down the IPC pipes so the UI cleanly sees disconnect
        // rather than hitting a stuck handle on FH6 exit.
        g_ipc_server.stop();
        g_pcm_pipe.stop();
        // The worker threads (bridge init, periodic writer, discovery) are
        // detached deliberately: joining here would deadlock under the loader
        // lock, and on process exit the OS has already terminated them before
        // DLL_PROCESS_DETACH runs.
        break;
    default:
        // DLL_THREAD_ATTACH / DLL_THREAD_DETACH: disabled via
        // DisableThreadLibraryCalls, and nothing else to do.
        break;
    }
    return TRUE;
}

#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <horizon/fmod/resolver.hpp>
#include <horizon/inject/metadata_injector.hpp>
#include <string_view>

namespace horizon::fmod::signatures {

inline constexpr SignatureSet kFh6 = {
    .createDsp =
        {
            .anchor  = "System::createDSP",
            .pattern = "4C 8B DC 56 48 81 EC 70 01 00 00"
                       "| 40 53 55 56 57 41 56 48 81 EC 50 01 00 00",
        },
    .addDsp =
        {
            .anchor  = "ChannelControl::addDSP",
            .pattern = "4C 8B DC 56 48 81 EC 70 01 00 00"
                       "| 40 53 55 56 57 41 56 48 81 EC 50 01 00 00",
        },
    .removeDsp =
        {
            .anchor  = "ChannelControl::removeDSP",
            .pattern = "48 89 5C 24 18 48 89 74 24 20 57 48 81 EC 50 01 00 00",
        },
    .dspRelease =
        {
            .anchor  = "DSP::release",
            .pattern = "48 89 5C 24 10 57 48 81 EC 50 01 00 00",
        },
    .setMode =
        {
            .anchor  = "ChannelControl::setMode",
            .pattern = "4C 8B DC 56 48 81 EC 70 01 00 00"
                       "| 40 53 55 56 57 41 56 48 81 EC 50 01 00 00"
                       "| 48 89 5C 24 10 57 48 81 EC 50 01 00 00"
                       "| 48 89 5C 24 18 48 89 74 24 20 57 48 81 EC 50 01 00 00"
                       "| 40 53 48 83 EC 20"
                       "| 48 89 5C 24 08 57 48 83 EC 20",
        },
    .handleOpen =
        {
            .anchor  = "",
            .pattern = "48 89 6C 24 18 48 89 74 24 20 57 41 56 41 57 48 83 EC 20 "
                       "8B F9 8B C1 C1 EF 11 49 8B F0 D1 E8 81 E7 FF 0F 00 00 "
                       "0F B7 E8 4C 8B F2 4C 8B F9",
        },
    .handleUnlock =
        {
            .anchor  = "",
            .pattern = "48 8B 89 F0 09 01 00 48 85 C9 0F 85 ?? ?? ?? ?? 33 C0 C3",
        },
};

} // namespace horizon::fmod::signatures

namespace horizon::inject::signatures {

// FH6 metadata chain + field offsets, and how to re-derive them after a game
// update: AGENTS.md -> "In-game title injection". NOLINT: the std::string
// members make static init theoretically throwing, unavoidable for the literal.
// NOLINTNEXTLINE(bugprone-throwing-static-initialization)
inline const MetadataInjectorConfig kFh6Metadata = {
    .class_mangled_name = ".?AV?$_Ref_count_obj2@VRadioStreamFmod@@@std@@",

    // instance +0x50 -> track list, +0x08 -> current-track block.
    .chain_offsets = {0x50, 0x08},

    // Block holds std::strings at +0x10 (internal event id, left alone -- not a
    // valid FMOD event name), +0x30 (title), +0x50 (artist).
    .sound_name_offset   = std::nullopt,
    .event_id_offset     = 0x10,
    .display_name_offset = 0x30,
    .artist_offset       = 0x50,

    .field_size       = 0, // unused in MSVC-string mode
    .use_msvc_strings = true,
};

} // namespace horizon::inject::signatures

namespace horizon::game::signatures {

struct GameSignatures {
    // A function that loads the RadioState singleton via
    // `mov rbx, [rip+disp32]`; decoding that displacement yields the address
    // of the global slot holding RadioState*. Must match exactly once.
    std::string_view radio_state_loader;

    // Byte offset of the `48 8B 1D <disp32>` within radio_state_loader.
    std::ptrdiff_t mov_rbx_offset_in_match;

    // In-game event detection, offsets relative to *g_radio_state_global.
    // Verify against dllmain's one-shot state dump on a new build; see
    // docs/architecture.md -> "In-game event detection".
    std::ptrdiff_t race_active_a_offset; // byte; nonzero with B ⇒ in a race
    std::ptrdiff_t race_active_b_offset; // byte
    // Station name (MSVC std::string), reached via
    // *(*(radio_state + chain0) + chain1) + station_name_offset. Tracks station
    // SELECTION only -- the name does NOT change when the radio is powered off.
    std::ptrdiff_t station_chain0_offset;
    std::ptrdiff_t station_chain1_offset;
    std::ptrdiff_t station_name_offset;
};

inline constexpr GameSignatures kFh6Game = {
    .radio_state_loader = "48 89 5C 24 08 48 89 54 24 10 57 48 83 EC 40 "
                          "48 8B FA 48 8B 1D ?? ?? ?? ?? 48 85 DB 74 16 "
                          "48 8D 4C 24 20 E8 ?? ?? ?? ?? 48 8B D0 48 8B CB",

    .mov_rbx_offset_in_match = 18,

    .race_active_a_offset = 0x68,
    .race_active_b_offset = 0x69,

    .station_chain0_offset = 0x40,
    .station_chain1_offset = 0x50,
    .station_name_offset   = 0x200,
};

// Game builds the signatures and offsets above are known to work on, keyed by
// the game module's .text VirtualSize (a cheap per-build fingerprint: it
// changes whenever the game's code does). Running on a build NOT listed here
// isn't necessarily broken -- most updates leave these patterns intact -- but
// it's the first thing to check when titles or audio stop working: re-verify
// with the discovery dump (AGENTS.md -> "In-game title injection"), then add
// the new build here.
//
// The June 2026 entries are the Steam and Microsoft Store builds that
// matkhl/FH6-Spotify-Radio (GPL-3.0) verified live; its FMOD and RadioState
// signatures are byte-identical to ours. The November 2026 Steam build (PE
// timestamp 2026-11-03) was verified live here: audio, titles, and game
// events all work with the signatures unchanged.
struct KnownBuild {
    std::uint32_t    text_size;
    std::string_view label;
};

inline constexpr std::array kFh6KnownBuilds = {
    KnownBuild{.text_size = 0x6322F5E, .label = "Steam, June 2026"},
    KnownBuild{.text_size = 0x630E45E, .label = "Microsoft Store, June 2026"},
    KnownBuild{.text_size = 0x63C3D0E, .label = "Steam, November 2026"},
};

// The label of the known build with this .text size, or empty if unknown.
[[nodiscard]] constexpr std::string_view known_build_label(const std::size_t text_size) noexcept {
    for (const auto& b : kFh6KnownBuilds) {
        if (b.text_size == text_size)
            return b.label;
    }
    return {};
}

} // namespace horizon::game::signatures

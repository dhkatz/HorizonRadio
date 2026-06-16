#pragma once

#include <horizon/fmod/resolver.hpp>
#include <horizon/inject/metadata_injector.hpp>

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

// std::string/vector members mean this static's construction can in theory
// throw (bad_alloc); during a DLL's static init there's nothing to catch it,
// but that's true of any allocation at load and not worth contorting the
// config literal to avoid.
// NOLINTNEXTLINE(bugprone-throwing-static-initialization)
inline const MetadataInjectorConfig kFh6Metadata = {
    .class_mangled_name = ".?AV?$_Ref_count_obj2@VRadioStreamFmod@@@std@@",

    // Reach the now-playing metadata block from the RadioStreamFmod instance:
    //   instance +0x50 -> track list, +0x08 -> block (list[0], the current
    //   track). The block holds std::string fields at +0x10 (internal event
    //   id), +0x30 (title), +0x50 (artist).
    //
    // Re-derived May 2026 via the in-process discovery dump after a game
    // update shifted the chain (field offsets match g0ldyy/fh6-universal-radio,
    // but their build reached the block via +0x48 -> +0x18). Re-run the dump
    // if a future update breaks it; static RE is useless here (packed binary).
    .chain_offsets = {0x50, 0x08},

    // Leave the internal event id (+0x10) alone -- writing our track id there
    // isn't a valid FMOD event name. We only need the visible title/artist.
    .sound_name_offset   = std::nullopt,
    .display_name_offset = 0x30,
    .artist_offset       = 0x50,

    .field_size       = 0, // unused in MSVC-string mode
    .use_msvc_strings = true,
};

} // namespace horizon::inject::signatures

namespace horizon::game::signatures {

struct GameSignatures {
    std::string_view radio_set_station_by_name;

    std::ptrdiff_t mov_rbx_offset_in_match;

    std::string_view radio_state_singleton;

    // ----- In-game event detection (read from the RadioState singleton) -----
    // Offsets relative to *g_radio_state_global. Sourced from
    // g0ldyy/fh6-universal-radio's game_state_probe, which reads the SAME
    // singleton this file resolves (its race pattern is a substring of
    // radio_set_station_by_name above). Verify with the one-shot state dump
    // in dllmain before trusting these on a new build.
    std::ptrdiff_t race_active_a_offset; // byte; nonzero with B ⇒ in a race
    std::ptrdiff_t race_active_b_offset; // byte
    std::ptrdiff_t race_restart_offset;  // int32; == -1 on a race restart
    // Station NAME (MSVC std::string), reached by a pointer chain off the
    // singleton: *(*(radio_state + chain0) + chain1) + station_name_offset.
    // Gives the currently-selected station (e.g. "Horizon Pulse"). NOTE: the
    // name does NOT change when the radio is switched off, so it tracks
    // station SELECTION only, not on/off.
    std::ptrdiff_t station_chain0_offset;
    std::ptrdiff_t station_chain1_offset;
    std::ptrdiff_t station_name_offset;
};

inline constexpr GameSignatures kFh6Game = {
    .radio_set_station_by_name = "48 89 5C 24 08 48 89 54 24 10 57 48 83 EC 40 "
                                 "48 8B FA 48 8B 1D ?? ?? ?? ?? 48 85 DB 74 16 "
                                 "48 8D 4C 24 20 E8 ?? ?? ?? ?? 48 8B D0 48 8B CB",

    .mov_rbx_offset_in_match = 18,

    .radio_state_singleton = "48 8B 89 F0 09 01 00 48 85 C9 0F 85 ?? ?? ?? ?? 33 C0 C3",

    .race_active_a_offset = 0x68,
    .race_active_b_offset = 0x69,
    .race_restart_offset  = 0x80,

    .station_chain0_offset = 0x40,
    .station_chain1_offset = 0x50,
    .station_name_offset   = 0x200,
};

} // namespace horizon::game::signatures

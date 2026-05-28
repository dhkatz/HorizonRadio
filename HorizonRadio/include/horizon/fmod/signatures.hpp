#pragma once

#include <horizon/fmod/resolver.hpp>
#include <horizon/inject/metadata_injector.hpp>

// Byte signatures and metadata-injector configuration for target games.
// These go stale every time the game patches and its FMOD wrappers
// shift -- signatures live in their own header so updating them is a
// one-file touch, not a code change in the resolver or injector.
//
// How to derive a new signature:
//   1. Open the live game's executable in a disassembler (IDA / Ghidra
//      / Binary Ninja). The FMOD wrappers will likely be lazy-decrypted
//      in modern Forza builds, so attach to a running game process
//      rather than disassembling the on-disk file.
//   2. Find the FMOD C++ method by name in the symbol table or by
//      walking from a known reference (e.g. an "fmod_error_string"
//      string in .rdata used by error logs).
//   3. Take the first ~12-20 bytes of the function prologue. Mask out
//      bytes that look like RVAs or stack-frame sizes likely to shift
//      across builds (use "??" wildcards).
//
// Keep signatures pinned to a specific game build version in the
// comment so future maintainers know what they were verified against.

namespace horizon::fmod::signatures {

// Forza Horizon 6 FMOD entry points.
//
// Anchored to FMOD's own `Class::method` strings in .rdata (those are
// stable across FMOD releases; the prologues are not). Each entry's
// `pattern` is a `|`-separated list of x64 MSVC prologues FMOD has
// shipped across its 1.x line; we accept whichever matches the
// current build.
//
// Source: cross-referenced from g0ldyy/fh6-universal-radio
// (src/fmod/dsp_bridge.cpp, working in the current FH6 build) and
// extended/cleaned. Update the prologue alternations if a future FMOD
// release ships a new variant.
inline constexpr SignatureSet kFh6 = {
    .createDsp = {
        .anchor  = "System::createDSP",
        .pattern = "4C 8B DC 56 48 81 EC 70 01 00 00"
                "| 40 53 55 56 57 41 56 48 81 EC 50 01 00 00",
    },
    .addDsp = {
        .anchor  = "ChannelControl::addDSP",
        .pattern = "4C 8B DC 56 48 81 EC 70 01 00 00"
                "| 40 53 55 56 57 41 56 48 81 EC 50 01 00 00",
    },
    .removeDsp = {
        .anchor  = "ChannelControl::removeDSP",
        .pattern = "48 89 5C 24 18 48 89 74 24 20 57 48 81 EC 50 01 00 00",
    },
    .dspRelease = {
        .anchor  = "DSP::release",
        .pattern = "48 89 5C 24 10 57 48 81 EC 50 01 00 00",
    },
    .setMode = {
        // Best-effort: try every prologue we know FMOD has emitted.
        // setMode is a thin forwarder so it's compiled to multiple
        // shapes depending on inlining decisions.
        .anchor  = "ChannelControl::setMode",
        .pattern = "4C 8B DC 56 48 81 EC 70 01 00 00"
                "| 40 53 55 56 57 41 56 48 81 EC 50 01 00 00"
                "| 48 89 5C 24 10 57 48 81 EC 50 01 00 00"
                "| 48 89 5C 24 18 48 89 74 24 20 57 48 81 EC 50 01 00 00"
                "| 40 53 48 83 EC 20"
                "| 48 89 5C 24 08 57 48 83 EC 20",
    },
    .handleOpen = {
        // No anchor -- this is FMOD's internal Handle::open, not
        // referenced by a class::method string. The prologue is
        // unique enough on its own.
        .anchor  = "",
        .pattern = "48 89 6C 24 18 48 89 74 24 20 57 41 56 41 57 48 83 EC 20 "
                   "8B F9 8B C1 C1 EF 11 49 8B F0 D1 E8 81 E7 FF 0F 00 00 "
                   "0F B7 E8 4C 8B F2 4C 8B F9",
    },
    .handleUnlock = {
        // Tiny leaf function; not always recorded in .pdata. The
        // direct-pattern path falls back to a linear .text scan.
        .anchor  = "",
        .pattern = "48 8B 89 F0 09 01 00 48 85 C9 0F 85 ?? ?? ?? ?? 33 C0 C3",
    },
};

} // namespace horizon::fmod::signatures

namespace horizon::inject::signatures {

// Forza Horizon 6 -- configuration for writing track metadata into
// RadioStreamFmod instances. The injector won't write anything until
// class_mangled_name resolves; empty strings are intentional defaults
// that keep the runtime dormant until verified.
//
// Determining each field against the live game:
//
//   class_mangled_name:
//     The MSVC mangled name of the class to heap-scan for. The
//     original abandoned mod targeted the make_shared<RadioStreamFmod>
//     control block:
//        ".?AV?$_Ref_count_obj2@VRadioStreamFmod@@@std@@"
//     A more direct alternative is the class itself:
//        ".?AVRadioStreamFmod@@"
//     The chain_offsets below depend on which entry point you pick;
//     they're for the make_shared control-block variant.
//
//   chain_offsets:
//     Sequence of (add offset, dereference) steps from a candidate
//     instance to the struct holding the three string fields. The
//     original mod's logs ("Chain broken at refcount+0x58", "Chain
//     broken at deref+0x18") tell us the walk is:
//        candidate + 0x58 -> deref -> + 0x18 -> deref -> SampleProperties
//     Verify these offsets against the current FH6 build; if the
//     class layout has shifted they'll need updating.
//
//   sound_name_offset / display_name_offset / artist_offset:
//     Positions of the three MsvcString fields within the chain
//     endpoint (SampleProperties). The original mod's logs name
//     these as "SoundName", "DisplayName", "Artist" -- find the
//     three consecutive 32-byte MsvcString-shaped slots and record
//     each offset.
inline const horizon::inject::MetadataInjectorConfig kFh6Metadata = {
    // Verified via static analysis of forzahorizon6.exe (one match in
    // .data; this is the make_shared<RadioStreamFmod> control block).
    .class_mangled_name  = ".?AV?$_Ref_count_obj2@VRadioStreamFmod@@@std@@",

    // Chain from the make_shared control block (slot pointer) to
    // SampleProperties body. Cross-verified with the abandoned
    // Spotify mod's "Chain broken at refcount+0x58 / deref+0x18"
    // strings and with g0ldyy/fh6-universal-radio's working source:
    //
    //   slot + 0x10              -> embedded RadioStreamFmod
    //     (so radio_stream = slot + 0x10; g0ldyy measures from here)
    //   radio_stream + 0x48      -> SampleProperties wrapper
    //     == slot + 0x58
    //   wrapper + 0x18           -> SampleProperties body
    .chain_offsets       = { 0x58, 0x18 },

    // SampleProperties body holds three MSVC std::strings:
    //   +0x10  SoundName    -- FMOD event identifier (read-only)
    //   +0x30  DisplayName  -- the track title shown on the HUD
    //   +0x50  Artist       -- the artist line shown on the HUD
    //
    // SoundName is intentionally left null in this config: writing
    // it breaks FMOD's lookup of the underlying sample.
    .sound_name_offset   = std::nullopt,
    .display_name_offset = 0x30,
    .artist_offset       = 0x50,

    .field_size          = 0,    // unused in MSVC-string mode
    .use_msvc_strings    = true,
};

} // namespace horizon::inject::signatures

namespace horizon::game::signatures {

// Game-side function sigscans for FH6. These describe FH6's compiled
// code; they're observations about the binary, not the abandoned mod's
// IP. Re-deriving them on each game patch is straightforward (open
// in Ghidra, find the function by string xref or known reference,
// take the first ~20 bytes of the prologue).
//
// What each is for:
//
//   radio_set_station_by_name:
//     Game function that takes a station-name string and switches the
//     current radio to that station. Its prologue loads a global
//     pointer via `mov rbx, [rip + disp32]` -- that global holds
//     RadioState*. Decoding the disp32 gives us the address of the
//     RadioState global, which lets us find live RadioStreamFmod
//     instances in microseconds instead of scanning the heap.
//
//   radio_state_singleton:
//     Alternative path to RadioState -- a getter `mov rcx,
//     [rcx+0x9F0]; test rcx, rcx; ...` that returns the singleton
//     when called with the right `this`. Less convenient than the
//     global because we'd need to find the `this` first. Kept here
//     as a future fallback.
struct GameSignatures {
    // Primary: function whose body holds the `mov rbx, [rip+disp32]`
    // that loads RadioState* from its global slot.
    std::string_view radio_set_station_by_name;

    // Offset within the matched bytes to the start of the
    // `48 8B 1D <disp32>` instruction (i.e., the "48" byte).
    std::ptrdiff_t   mov_rbx_offset_in_match;

    // Reserved for the singleton getter fallback.
    std::string_view radio_state_singleton;
};

inline constexpr GameSignatures kFh6Game = {
    // Verified against the current FH6 build of forzahorizon6.exe:
    // exactly one unique match in .text.
    .radio_set_station_by_name =
        "48 89 5C 24 08 48 89 54 24 10 57 48 83 EC 40 "
        "48 8B FA 48 8B 1D ?? ?? ?? ?? 48 85 DB 74 16 "
        "48 8D 4C 24 20 E8 ?? ?? ?? ?? 48 8B D0 48 8B CB",

    // The pattern decomposes as:
    //   bytes [0..14]   prologue (save regs / sub rsp / etc.)  -- 15 bytes
    //   bytes [15..17]  mov rdi, rdx                            -- 3 bytes
    //   bytes [18..24]  mov rbx, [rip+disp32]                   -- 7 bytes
    // The MOV starts at offset 18.
    .mov_rbx_offset_in_match = 18,

    // Single-hit getter; kept for future use.
    .radio_state_singleton =
        "48 8B 89 F0 09 01 00 48 85 C9 0F 85 ?? ?? ?? ?? 33 C0 C3",
};

} // namespace horizon::game::signatures

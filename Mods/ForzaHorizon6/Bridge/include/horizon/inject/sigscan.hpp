#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <string_view>
#include <vector>
#include <windows.h>

namespace horizon::inject {

// View into a loaded PE module; parses NT headers from the in-memory image
// (section addresses are loaded RVAs, not file offsets). Used for byte-signature
// scans in .text and RTTI lookups in .rdata.
class PeImage {
public:
    explicit PeImage(HMODULE module);

    [[nodiscard]] bool valid() const noexcept {
        return base_ != nullptr;
    }

    [[nodiscard]] std::uintptr_t base() const noexcept {
        return reinterpret_cast<std::uintptr_t>(base_);
    }
    [[nodiscard]] std::size_t image_size() const noexcept {
        return image_size_;
    }

    [[nodiscard]] std::span<const std::byte> text() const noexcept {
        return text_;
    }
    [[nodiscard]] std::span<const std::byte> rdata() const noexcept {
        return rdata_;
    }
    // MSVC emits TypeDescriptor structs into .data (not .rdata), because
    // the `spare` field is mutated at runtime by the type_info demangle
    // cache. Same for the test exe and for FH6.
    [[nodiscard]] std::span<const std::byte> data() const noexcept {
        return data_;
    }

    // Runtime function table (.pdata). Each entry's BeginAddress and
    // EndAddress are RVAs relative to base(); used to map an arbitrary
    // instruction address back to its enclosing function.
    [[nodiscard]] std::span<const RUNTIME_FUNCTION> pdata() const noexcept {
        return pdata_;
    }

private:
    std::byte*                        base_       = nullptr;
    std::size_t                       image_size_ = 0;
    std::span<const std::byte>        text_;
    std::span<const std::byte>        rdata_;
    std::span<const std::byte>        data_;
    std::span<const RUNTIME_FUNCTION> pdata_;
};

// A compiled IDA-style pattern: byte values plus a fixed/wildcard mask.
// Input syntax: hex pairs separated by whitespace, "??" for wildcard:
//   "48 89 5C 24 ?? E8 ?? ?? ?? ??"
struct Pattern {
    std::vector<std::byte> bytes;
    std::vector<bool>      mask; // true = fixed byte, false = wildcard
};

// Throws std::invalid_argument on malformed input.
Pattern compile_pattern(std::string_view ida_style);

// Returns a pointer into haystack at the first match, or nullptr if none.
const std::byte* find_pattern(std::span<const std::byte> haystack, const Pattern& p);

// Convenience overload: compile + find in one call. Useful in tests; in
// hot paths prefer pre-compiling the pattern and reusing it.
inline const std::byte* find_pattern(std::span<const std::byte> haystack, std::string_view ida_style) {
    return find_pattern(haystack, compile_pattern(ida_style));
}

// A union of alternative patterns. Used for function prologues that
// vary across builds (FMOD shipped at least four x64 MSVC prologues
// across its 1.x line); we accept any one as a match.
//
// Syntax: alternatives separated by `|`.
//   "48 89 5C 24 ?? | 40 53 48 83 EC 20"
struct PatternSet {
    std::vector<Pattern> alternatives;
};

PatternSet compile_pattern_set(std::string_view ida_style);

// True if any alternative matches at the start of `haystack`. Bounded
// by the longest alternative; returns false if haystack is shorter
// than every alternative.
bool match_pattern_set_at(std::span<const std::byte> haystack, const PatternSet& set);

// NUL-terminated occurrences of `needle` (preceding + following byte must be
// `\0`, so we don't match the tail of a longer string). Locates FMOD anchor
// strings like "ChannelControl::addDSP" in .rdata.
std::vector<const std::byte*> find_anchor_strings(std::span<const std::byte> haystack, std::string_view needle);

// Walks `text` looking for x64 `lea reg, [rip + disp32]` instructions
// whose computed target address (rip + disp32) is one of `targets`.
// Returns pointers to the first byte of each matching lea (the REX
// prefix). The opcode is `48|4C 8D /5` with ModR/M mod=00 r/m=101.
//
// `targets` must be sorted (binary search inside the loop).
std::vector<const std::byte*> find_lea_targeting(std::span<const std::byte>        text,
                                                 std::span<const std::byte* const> targets);

// Returns the function-start RVA enclosing `instruction_rva`, looking
// it up in `.pdata` via binary search. Returns 0 if no enclosing
// function is recorded (some leaf functions don't appear in .pdata).
//
// CAVEAT: when MSVC emits chained unwind info (UNW_FLAG_CHAININFO),
// `.pdata` has multiple entries per function and this returns the
// BeginAddress of the *chunk* containing the instruction, not the
// true function entry. Use resolve_primary_function_rva to walk
// back to the entry when you need to call the function.
std::uint32_t enclosing_function_rva(std::span<const RUNTIME_FUNCTION> pdata, std::uint32_t instruction_rva);

// Given a `.pdata` chunk's BeginAddress, walk UNWIND_INFO's
// chain-info flag to find the primary (entry-point) RUNTIME_FUNCTION
// and return its BeginAddress. If `chunk_begin_rva` already points
// at the primary (no CHAININFO flag), returns `chunk_begin_rva`.
// Returns 0 on any malformed-info / out-of-bounds access.
//
// UNWIND_INFO layout (Microsoft x64 ABI):
//   +0    uint8  Version:3, Flags:5     (UNW_FLAG_CHAININFO = 0x4)
//   +1    uint8  SizeOfProlog
//   +2    uint8  CountOfCodes
//   +3    uint8  FrameRegister:4, FrameOffset:4
//   +4    UNWIND_CODE codes[CountOfCodes]  (2 bytes each)
//   pad to 4-byte boundary
//   then either ExceptionHandler RVA (Flags & 0x1|0x2)
//        or    RUNTIME_FUNCTION chained (Flags & 0x4)
std::uint32_t resolve_primary_function_rva(const PeImage& image, std::uint32_t chunk_begin_rva);

const std::byte* find_function_by_anchor(const PeImage& image, std::string_view anchor, const PatternSet& prologue);

inline const std::byte* find_function_by_anchor(const PeImage& image, std::string_view anchor,
                                                std::string_view ida_style) {
    return find_function_by_anchor(image, anchor, compile_pattern_set(ida_style));
}

struct AnchorResolution {
    enum class Status {
        ok,
        no_anchor_string,
        no_lea,
        no_enclosing_function,
        no_prologue_match,
        ambiguous,
    };

    Status           status               = Status::no_anchor_string;
    std::size_t      anchor_count         = 0;
    std::size_t      lea_count            = 0;
    std::size_t      enclosing_fn_count   = 0;
    std::size_t      prologue_match_count = 0;
    const std::byte* result               = nullptr;

    // First few enclosing-function addresses (deduped), populated even on
    // no_prologue_match/ambiguous so callers can dump prologue bytes and widen
    // the alternation. Capped at 4.
    std::array<const std::byte*, 4> enclosing_functions{};
};

AnchorResolution diagnose_function_by_anchor(const PeImage& image, std::string_view anchor, const PatternSet& prologue);

inline AnchorResolution diagnose_function_by_anchor(const PeImage& image, std::string_view anchor,
                                                    std::string_view ida_style) {
    return diagnose_function_by_anchor(image, anchor, compile_pattern_set(ida_style));
}

const std::byte* find_function_by_pattern(const PeImage& image, const PatternSet& set);

inline const std::byte* find_function_by_pattern(const PeImage& image, std::string_view ida_style) {
    return find_function_by_pattern(image, compile_pattern_set(ida_style));
}

} // namespace horizon::inject

#include <algorithm>
#include <cctype>
#include <cstring>
#include <functional>
#include <horizon/inject/sigscan.hpp>
#include <optional>
#include <stdexcept>

namespace horizon::inject {

PeImage::PeImage(HMODULE module) {
    if (module == nullptr)
        return;

    auto* base = reinterpret_cast<std::byte*>(module);
    auto* dos  = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
        return;

    auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
        return;

    base_       = base;
    image_size_ = nt->OptionalHeader.SizeOfImage;

    const auto section_count = nt->FileHeader.NumberOfSections;
    auto*      section       = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < section_count; ++i, ++section) {
        const auto  name = reinterpret_cast<const char*>(section->Name);
        std::byte*  ptr  = base + section->VirtualAddress;
        std::size_t size = section->Misc.VirtualSize;

        if (std::strncmp(name, ".text", IMAGE_SIZEOF_SHORT_NAME) == 0) {
            text_ = {ptr, size};
        } else if (std::strncmp(name, ".rdata", IMAGE_SIZEOF_SHORT_NAME) == 0) {
            rdata_ = {ptr, size};
        } else if (std::strncmp(name, ".data", IMAGE_SIZEOF_SHORT_NAME) == 0) {
            data_ = {ptr, size};
        } else if (std::strncmp(name, ".pdata", IMAGE_SIZEOF_SHORT_NAME) == 0) {
            pdata_ = {reinterpret_cast<const RUNTIME_FUNCTION*>(ptr), size / sizeof(RUNTIME_FUNCTION)};
        }
    }
}

namespace {

std::optional<int> nibble(char c) {
    c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    if (c >= '0' && c <= '9')
        return c - '0';
    if (c >= 'a' && c <= 'f')
        return 10 + (c - 'a');
    return std::nullopt;
}

} // namespace

Pattern compile_pattern(std::string_view ida_style) {
    Pattern p;
    auto    it = ida_style.begin();
    while (it != ida_style.end()) {
        if (std::isspace(static_cast<unsigned char>(*it))) {
            ++it;
            continue;
        }
        if (std::distance(it, ida_style.end()) < 2) {
            throw std::invalid_argument("sigscan: dangling half-byte in pattern");
        }
        const char hi = *it++;
        const char lo = *it++;
        if (hi == '?' && lo == '?') {
            p.bytes.push_back(std::byte{0});
            p.mask.push_back(false);
            continue;
        }
        auto h = nibble(hi);
        auto l = nibble(lo);
        if (!h || !l) {
            throw std::invalid_argument("sigscan: invalid hex in pattern");
        }
        p.bytes.push_back(static_cast<std::byte>((*h << 4) | *l));
        p.mask.push_back(true);
    }
    return p;
}

const std::byte* find_pattern(const std::span<const std::byte> haystack, const Pattern& p) {
    if (p.bytes.empty() || haystack.size() < p.bytes.size())
        return nullptr;
    const std::size_t n   = p.bytes.size();
    const std::size_t end = haystack.size() - n;
    for (std::size_t i = 0; i <= end; ++i) {
        bool ok = true;
        for (std::size_t j = 0; j < n; ++j) {
            if (p.mask[j] && haystack[i + j] != p.bytes[j]) {
                ok = false;
                break;
            }
        }
        if (ok)
            return haystack.data() + i;
    }
    return nullptr;
}

PatternSet compile_pattern_set(std::string_view ida_style) {
    PatternSet  set;
    std::size_t start = 0;
    for (std::size_t i = 0; i <= ida_style.size(); ++i) {
        if (i == ida_style.size() || ida_style[i] == '|') {
            auto chunk = ida_style.substr(start, i - start);
            // Skip empties (allows leading/trailing/double `|`).
            bool any = false;
            for (const char c : chunk) {
                if (!std::isspace(static_cast<unsigned char>(c))) {
                    any = true;
                    break;
                }
            }
            if (any)
                set.alternatives.push_back(compile_pattern(chunk));
            start = i + 1;
        }
    }
    return set;
}

bool match_pattern_set_at(const std::span<const std::byte> haystack, const PatternSet& set) {
    for (const auto& p : set.alternatives) {
        if (haystack.size() < p.bytes.size())
            continue;
        bool ok = true;
        for (std::size_t j = 0; j < p.bytes.size(); ++j) {
            if (p.mask[j] && haystack[j] != p.bytes[j]) {
                ok = false;
                break;
            }
        }
        if (ok)
            return true;
    }
    return false;
}

std::vector<const std::byte*> find_anchor_strings(const std::span<const std::byte> haystack, std::string_view needle) {
    std::vector<const std::byte*> out;
    const std::size_t             n = needle.size();
    if (n == 0 || haystack.size() < n + 1)
        return out;
    const auto* p   = haystack.data();
    const auto* end = p + haystack.size() - n;
    for (const auto* cur = p; cur < end; ++cur) {
        if (std::memcmp(cur, needle.data(), n) != 0)
            continue;
        // Must be NUL-terminated (next byte is 0)...
        if (cur[n] != std::byte{0})
            continue;
        // ...and either at the start of the section or preceded by NUL,
        // so we don't pick up "::foo::bar" as a match for "foo::bar".
        if (cur > p && cur[-1] != std::byte{0})
            continue;
        out.push_back(cur);
    }
    return out;
}

std::vector<const std::byte*> find_lea_targeting(std::span<const std::byte>        text,
                                                 std::span<const std::byte* const> targets) {
    std::vector<const std::byte*> out;
    if (targets.empty() || text.size() < 7)
        return out;

    const auto* p = text.data();
    for (const auto* limit = p + text.size() - 7; p <= limit; ++p) {
        // Match REX(W) prefix that we accept: 0x48 (W only) or 0x4C
        // (R + W). Other REX combinations with W are rare for `lea
        // reg, [rip+disp32]` so skip them for now; can extend if a
        // signature ever needs 0x49 or 0x4D.
        const auto b0 = std::to_integer<std::uint8_t>(p[0]);
        if (b0 != 0x48 && b0 != 0x4C)
            continue;
        if (std::to_integer<std::uint8_t>(p[1]) != 0x8D)
            continue;
        // ModR/M: mod=00 (no displacement field), r/m=101 (RIP-relative).
        // Top 2 bits = mod, low 3 bits = r/m. Mask off the middle (reg).
        if ((std::to_integer<std::uint8_t>(p[2]) & 0xC7) != 0x05)
            continue;

        std::int32_t disp = 0;
        std::memcpy(&disp, p + 3, sizeof(disp));
        const auto* tgt = p + 7 + disp;
        if (std::ranges::binary_search(targets, tgt)) {
            out.push_back(p);
        }
    }
    return out;
}

std::uint32_t enclosing_function_rva(std::span<const RUNTIME_FUNCTION> pdata, std::uint32_t instruction_rva) {
    // .pdata entries are sorted by BeginAddress. Find the last entry
    // whose BeginAddress <= instruction_rva; that's the enclosing
    // function (if instruction_rva also falls before EndAddress).
    if (pdata.empty())
        return 0;
    auto it = std::ranges::upper_bound(pdata, instruction_rva, std::less{}, &RUNTIME_FUNCTION::BeginAddress);
    if (it == pdata.begin())
        return 0;
    --it;
    if (instruction_rva >= it->EndAddress)
        return 0;
    return it->BeginAddress;
}

std::uint32_t resolve_primary_function_rva(const PeImage& image, std::uint32_t chunk_begin_rva) {
    if (!image.valid() || chunk_begin_rva == 0)
        return 0;

    constexpr std::uint8_t kUnwFlagChainInfo = 0x4;
    constexpr int          kMaxDepth         = 8;

    auto rf_at = [&](std::uint32_t begin_rva) -> const RUNTIME_FUNCTION* {
        auto pdata = image.pdata();
        auto it    = std::ranges::lower_bound(pdata, begin_rva, std::less{}, &RUNTIME_FUNCTION::BeginAddress);
        if (it == pdata.end() || it->BeginAddress != begin_rva)
            return nullptr;
        return &*it;
    };

    std::uint32_t current = chunk_begin_rva;
    for (int depth = 0; depth < kMaxDepth; ++depth) {
        const auto* rf = rf_at(current);
        if (rf == nullptr)
            return current; // not in .pdata at all; best effort

        // Bounds-check the UnwindData pointer.
        if (rf->UnwindData == 0 || rf->UnwindData >= image.image_size())
            return current;
        const auto* unwind = reinterpret_cast<const std::uint8_t*>(image.base() + rf->UnwindData);

        const auto version_flags = unwind[0];
        const auto flags         = static_cast<std::uint8_t>(version_flags >> 3);
        if ((flags & kUnwFlagChainInfo) == 0)
            return current; // we're at the primary

        // Compute offset of the trailing RUNTIME_FUNCTION:
        //   4 byte header + 2 * CountOfCodes bytes of codes,
        //   padded up to 4-byte alignment.
        const std::uint8_t count_of_codes = unwind[2];
        std::size_t        after_codes    = 4u + static_cast<std::size_t>(count_of_codes) * 2u;
        if (after_codes & 0x2)
            after_codes += 2; // pad to DWORD

        // Bounds-check that the chained RUNTIME_FUNCTION fits within image.
        const std::uint32_t chained_rva = rf->UnwindData + static_cast<std::uint32_t>(after_codes);
        if (chained_rva + sizeof(RUNTIME_FUNCTION) > image.image_size())
            return current;

        const auto* chained = reinterpret_cast<const RUNTIME_FUNCTION*>(unwind + after_codes);
        if (chained->BeginAddress == 0 || chained->BeginAddress == current)
            return current;
        current = chained->BeginAddress;
    }
    return current;
}

AnchorResolution diagnose_function_by_anchor(const PeImage& image, std::string_view anchor,
                                             const PatternSet& prologue) {
    AnchorResolution diag;
    if (!image.valid())
        return diag;

    auto anchors      = find_anchor_strings(image.rdata(), anchor);
    diag.anchor_count = anchors.size();
    if (anchors.empty()) {
        diag.status = AnchorResolution::Status::no_anchor_string;
        return diag;
    }
    std::ranges::sort(anchors);

    auto leas      = find_lea_targeting(image.text(), anchors);
    diag.lea_count = leas.size();
    if (leas.empty()) {
        diag.status = AnchorResolution::Status::no_lea;
        return diag;
    }

    const auto  base       = image.base();
    const auto* text_start = image.text().data();
    const auto* text_end   = text_start + image.text().size();

    // Collect distinct enclosing-function addresses first, then run
    // the prologue filter. Lets us tell "no .pdata coverage" apart
    // from "pdata found something but prologue rejected it."
    //
    // Walk UNW_FLAG_CHAININFO back to the true function entry: MSVC
    // emits chained unwind info for functions with multiple regions,
    // and .pdata's BeginAddress for those is a continuation chunk,
    // not the entry point. The prologue we want to match (and the
    // address we want to call) lives at the primary.
    std::vector<const std::byte*> enclosing;
    enclosing.reserve(leas.size());
    for (const auto* lea : leas) {
        const auto rva       = static_cast<std::uint32_t>(reinterpret_cast<std::uintptr_t>(lea) - base);
        const auto chunk_rva = enclosing_function_rva(image.pdata(), rva);
        if (chunk_rva == 0)
            continue;
        const auto  primary_rva = resolve_primary_function_rva(image, chunk_rva);
        const auto  fn_rva      = primary_rva != 0 ? primary_rva : chunk_rva;
        const auto* fn          = reinterpret_cast<const std::byte*>(base + fn_rva);
        if (fn < text_start || fn >= text_end)
            continue;
        if (std::ranges::find(enclosing, fn) == enclosing.end()) {
            enclosing.push_back(fn);
        }
    }
    diag.enclosing_fn_count = enclosing.size();
    for (std::size_t i = 0; i < enclosing.size() && i < diag.enclosing_functions.size(); ++i) {
        diag.enclosing_functions[i] = enclosing[i];
    }
    if (enclosing.empty()) {
        diag.status = AnchorResolution::Status::no_enclosing_function;
        return diag;
    }

    std::vector<const std::byte*> matches;
    matches.reserve(enclosing.size());
    for (const auto* fn : enclosing) {
        const std::span tail{fn, static_cast<std::size_t>(text_end - fn)};
        if (match_pattern_set_at(tail, prologue))
            matches.push_back(fn);
    }
    diag.prologue_match_count = matches.size();

    if (matches.empty()) {
        diag.status = AnchorResolution::Status::no_prologue_match;
        return diag;
    }
    if (matches.size() > 1) {
        diag.status = AnchorResolution::Status::ambiguous;
        return diag;
    }
    diag.status = AnchorResolution::Status::ok;
    diag.result = matches.front();
    return diag;
}

const std::byte* find_function_by_anchor(const PeImage& image, std::string_view anchor, const PatternSet& prologue) {
    return diagnose_function_by_anchor(image, anchor, prologue).result;
}

const std::byte* find_function_by_pattern(const PeImage& image, const PatternSet& set) {
    if (!image.valid() || set.alternatives.empty())
        return nullptr;

    const auto  base       = image.base();
    const auto* text_start = image.text().data();
    const auto* text_end   = text_start + image.text().size();

    // Pass 1: .pdata function starts only (cheap + no false hits inside
    // function bodies).
    const std::byte* match = nullptr;
    int              seen  = 0;
    for (const auto& rf : image.pdata()) {
        const auto* fn = reinterpret_cast<const std::byte*>(base + rf.BeginAddress);
        if (fn < text_start || fn >= text_end)
            continue;
        const std::span tail{fn, static_cast<std::size_t>(text_end - fn)};
        if (match_pattern_set_at(tail, set)) {
            if (++seen > 1)
                return nullptr;
            match = fn;
        }
    }
    if (match)
        return match;

    // Pass 2: linear .text scan (catches leaf fns absent from .pdata).
    seen = 0;
    for (const auto* p = text_start; p < text_end; ++p) {
        if (const std::span tail{p, static_cast<std::size_t>(text_end - p)}; match_pattern_set_at(tail, set)) {
            if (++seen > 1)
                return nullptr;
            match = p;
        }
    }
    return match;
}

} // namespace horizon::inject

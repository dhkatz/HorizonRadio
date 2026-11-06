#include <cstdio>
#include <horizon/inject/game_resolver.hpp>
#include <horizon/inject/heap_scan.hpp>
#include <horizon/inject/metadata_injector.hpp>
#include <horizon/inject/msvc_string.hpp>
#include <horizon/inject/safe_mem.hpp>
#include <utility>
#include <windows.h>

namespace horizon::inject {

namespace {

// Truncate-and-null `value` into a fixed char[N] field.
void write_fixed_char_array(char* target, const std::size_t buf_size, const std::string_view value) {
    if (buf_size == 0)
        return;
    const std::size_t copy_len = std::min(value.size(), buf_size - 1);
    std::memcpy(target, value.data(), copy_len);
    std::memset(target + copy_len, 0, buf_size - copy_len);
}

// Reject spurious vtable matches: a real MSVC std::string header has cap >= 15
// (SSO floor), size <= cap, and a sane length.
bool plausible_msvc_string(const MsvcString* s) noexcept {
    if (s->capacity < 15)
        return false;
    if (s->size > s->capacity)
        return false;
    constexpr std::size_t kMaxStr = 1u << 24;
    if (s->capacity > kMaxStr)
        return false;
    return true;
}

// Reject non-string char[N] candidates: accept an all-null/fill buffer or
// printable ASCII followed by a null terminator; anything else isn't a field.
bool plausible_char_buffer(const char* buf, std::size_t n) noexcept {
    if (n == 0)
        return false;
    bool saw_printable = false;
    bool saw_null      = false;
    for (std::size_t i = 0; i < n; ++i) {
        const auto c = static_cast<std::uint8_t>(buf[i]);
        if (c == 0) {
            saw_null = true;
            continue;
        }
        if (saw_null) {
            // Bytes after null must stay null/padding (allow fill bytes too).
            if (c != 0 && c != 0xCC && c != 0xFD)
                return false;
            continue;
        }
        if (c >= 0x20 && c < 0x7F) {
            saw_printable = true;
            continue;
        }
        // UTF-8 multi-byte sequences -- accept high-bit bytes too.
        if (c >= 0x80) {
            saw_printable = true;
            continue;
        }
        return false;
    }
    (void)saw_printable;
    return true;
}

int do_write_one(void* base_ptr, const MetadataInjectorConfig& cfg, std::string_view sn, std::string_view dn,
                 std::string_view ar) {
    const auto base = reinterpret_cast<std::uintptr_t>(base_ptr);

    if (cfg.use_msvc_strings) {
        // Validate every configured field before any write -- so a
        // spurious candidate doesn't get a partial corruption.
        auto field_at = [&](std::ptrdiff_t off) {
            return reinterpret_cast<MsvcString*>(base + off);
        };
        if (cfg.sound_name_offset && !plausible_msvc_string(field_at(*cfg.sound_name_offset)))
            return 0;
        if (cfg.display_name_offset && !plausible_msvc_string(field_at(*cfg.display_name_offset)))
            return 0;
        if (cfg.artist_offset && !plausible_msvc_string(field_at(*cfg.artist_offset)))
            return 0;

        bool ok = true;
        if (cfg.sound_name_offset)
            ok &= write_msvc_string(*field_at(*cfg.sound_name_offset), sn);
        if (cfg.display_name_offset)
            ok &= write_msvc_string(*field_at(*cfg.display_name_offset), dn);
        if (cfg.artist_offset)
            ok &= write_msvc_string(*field_at(*cfg.artist_offset), ar);
        return ok ? 1 : 0;
    }

    // char[N] mode: truncate-and-null.
    if (cfg.sound_name_offset) {
        const char* p = reinterpret_cast<const char*>(base + *cfg.sound_name_offset);
        if (!plausible_char_buffer(p, cfg.field_size))
            return 0;
    }
    if (cfg.display_name_offset) {
        const char* p = reinterpret_cast<const char*>(base + *cfg.display_name_offset);
        if (!plausible_char_buffer(p, cfg.field_size))
            return 0;
    }
    if (cfg.artist_offset) {
        const char* p = reinterpret_cast<const char*>(base + *cfg.artist_offset);
        if (!plausible_char_buffer(p, cfg.field_size))
            return 0;
    }

    if (cfg.sound_name_offset) {
        write_fixed_char_array(reinterpret_cast<char*>(base + *cfg.sound_name_offset), cfg.field_size, sn);
    }
    if (cfg.display_name_offset) {
        write_fixed_char_array(reinterpret_cast<char*>(base + *cfg.display_name_offset), cfg.field_size, dn);
    }
    if (cfg.artist_offset) {
        write_fixed_char_array(reinterpret_cast<char*>(base + *cfg.artist_offset), cfg.field_size, ar);
    }
    return 1;
}

// Per-instance pipeline (vptr re-check, chain walk, write), SEH-guarded so a
// freed instance can't AV the thread. No C++ destructors in the body (C2712).
int process_one_instance(const void* instance, std::uintptr_t vt_addr, const MetadataInjectorConfig& cfg,
                         const std::ptrdiff_t* chain_ptr, std::size_t chain_size, std::string_view sn,
                         std::string_view dn, std::string_view ar) {
    __try {
        const auto vptr = *static_cast<const std::uintptr_t*>(instance);
        if (vptr != vt_addr)
            return 0;

        auto current = reinterpret_cast<std::uintptr_t>(instance);
        for (std::size_t i = 0; i < chain_size; ++i) {
            const auto slot = current + static_cast<std::uintptr_t>(chain_ptr[i]);
            current         = *reinterpret_cast<const std::uintptr_t*>(slot);
            if (current == 0)
                return 0;
        }

        return do_write_one(reinterpret_cast<void*>(current), cfg, sn, dn, ar);
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER
                                                                 : EXCEPTION_CONTINUE_SEARCH) {
        return 0;
    }
}

// POD snapshot of the endpoint strings, so it can cross the SEH boundary (C2712).
struct StringSnapshot {
    char        title[512];
    std::size_t title_len;
    char        artist[512];
    std::size_t artist_len;
    bool        ok;
};

// SEH-guarded read mirroring process_one_instance's walk: copy the endpoint
// title/artist into the snapshot so we can restore them later.
void read_strings_one(const void* instance, std::uintptr_t vt_addr, const MetadataInjectorConfig& cfg,
                      const std::ptrdiff_t* chain_ptr, std::size_t chain_size, StringSnapshot* out) {
    out->ok         = false;
    out->title_len  = 0;
    out->artist_len = 0;
    __try {
        const auto vptr = *static_cast<const std::uintptr_t*>(instance);
        if (vptr != vt_addr)
            return;
        auto current = reinterpret_cast<std::uintptr_t>(instance);
        for (std::size_t i = 0; i < chain_size; ++i) {
            current = *reinterpret_cast<const std::uintptr_t*>(current + static_cast<std::uintptr_t>(chain_ptr[i]));
            if (current == 0)
                return;
        }
        if (cfg.display_name_offset) {
            const auto* s = reinterpret_cast<const MsvcString*>(current + *cfg.display_name_offset);
            if (plausible_msvc_string(s)) {
                const std::size_t len = s->size < sizeof(out->title) ? s->size : sizeof(out->title);
                const char*       src = data(*s);
                for (std::size_t i = 0; i < len; ++i)
                    out->title[i] = src[i];
                out->title_len = len;
            }
        }
        if (cfg.artist_offset) {
            const auto* s = reinterpret_cast<const MsvcString*>(current + *cfg.artist_offset);
            if (plausible_msvc_string(s)) {
                const std::size_t len = s->size < sizeof(out->artist) ? s->size : sizeof(out->artist);
                const char*       src = data(*s);
                for (std::size_t i = 0; i < len; ++i)
                    out->artist[i] = src[i];
                out->artist_len = len;
            }
        }
        out->ok = true;
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER
                                                                 : EXCEPTION_CONTINUE_SEARCH) {
        out->ok = false;
    }
}

} // namespace

const void* walk_offset_chain(const void* start, std::span<const std::ptrdiff_t> chain) {
    auto current = reinterpret_cast<std::uintptr_t>(start);
    for (auto offset : chain) {
        const auto next_slot = current + static_cast<std::uintptr_t>(offset);
        current              = *reinterpret_cast<const std::uintptr_t*>(next_slot);
        if (current == 0)
            return nullptr;
    }
    return reinterpret_cast<const void*>(current);
}

MetadataInjector::MetadataInjector(const PeImage& image, MetadataInjectorConfig config)
    : image_(image), config_(std::move(config)) {}

bool MetadataInjector::resolve() {
    const MsvcRtti rtti(image_);
    auto           td = rtti.find_type_descriptor(config_.class_mangled_name);
    if (!td)
        return false;
    auto col = rtti.find_complete_object_locator(*td);
    if (!col)
        return false;
    auto vt = rtti.find_vtable(*col);
    if (!vt)
        return false;
    vt_ = vt;
    return true;
}

namespace {

using horizon::inject::safe_read_bytes;

void append_hex_line(std::string& out, std::ptrdiff_t off, const std::uint8_t* p) {
    char      line[80];
    const int n = std::snprintf(line, sizeof(line),
                                "  +0x%03zx: %02X %02X %02X %02X %02X %02X %02X %02X "
                                "%02X %02X %02X %02X %02X %02X %02X %02X  ",
                                static_cast<std::size_t>(off), p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7], p[8],
                                p[9], p[10], p[11], p[12], p[13], p[14], p[15]);
    out.append(line, static_cast<std::size_t>(n));
    for (int i = 0; i < 16; ++i) {
        out += (p[i] >= 0x20 && p[i] < 0x7F) ? static_cast<char>(p[i]) : '.';
    }
    out += '\n';
}

// Scan a buffer for MsvcString-shaped 32-byte regions and append a
// human-readable description of each to `out`.
void scan_for_msvc_strings(std::string& out, const std::uint8_t* base, std::size_t size, const void* original_addr) {
    if (size < sizeof(MsvcString))
        return;

    // Walk every 8-byte aligned offset; an MsvcString has 8-byte
    // alignment because of the size_t fields.
    for (std::size_t off = 0; off + sizeof(MsvcString) <= size; off += 8) {
        const auto* s = reinterpret_cast<const MsvcString*>(base + off);
        // Plausibility filter -- same logic as the write path uses.
        constexpr std::size_t kMaxStr = 1u << 24;
        if (s->capacity > kMaxStr)
            continue;
        if (s->size > s->capacity)
            continue;

        char header[160];
        int  n = 0;
        if (s->capacity == 15) {
            if (s->size > 15)
                continue;
            if (s->u.buf[s->size] != '\0')
                continue;
            // SSO. Preview the inline bytes.
            char preview[17] = {};
            for (std::size_t i = 0; i < s->size && i < 16; ++i) {
                preview[i] = (s->u.buf[i] >= 0x20 && s->u.buf[i] < 0x7F) ? s->u.buf[i] : '.';
            }
            n = std::snprintf(header, sizeof(header), "  [msvc_string] +0x%03zx  SSO  size=%2zu  preview=\"%s\"\n", off,
                              s->size, preview);
        } else {
            // Heap. Preview the first ~24 chars from the pointer (SEH-safe).
            if (s->u.ptr == nullptr)
                continue;
            char              preview_buf[25] = {};
            const std::size_t want            = std::min<std::size_t>(24, s->size);
            if (!safe_read_bytes(preview_buf, s->u.ptr, want))
                continue;
            for (std::size_t i = 0; i < want; ++i) {
                if (preview_buf[i] < 0x20 || preview_buf[i] >= 0x7F) {
                    preview_buf[i] = '.';
                }
            }
            preview_buf[want] = '\0';
            n = std::snprintf(header, sizeof(header),
                              "  [msvc_string] +0x%03zx  HEAP size=%2zu cap=%zu  preview=\"%s%s\"\n", off, s->size,
                              s->capacity, preview_buf, s->size > 24 ? "..." : "");
        }
        if (n > 0)
            out.append(header, static_cast<std::size_t>(n));
    }
    (void)original_addr;
}

} // namespace

namespace {

// A candidate at an address with a small surrounding committed region
// is almost certainly a stack frame, not a heap object. Used for the
// initial candidate-from-heap-scan filtering. NOTE: VirtualQuery
// returns RegionSize from the queried address to the end of the
// region, so a heap pointer near the end of an arena can yield a
// misleadingly small value. We mitigate by re-querying from the
// allocation base.
bool looks_like_heap(const void* addr) noexcept {
    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(addr, &mbi, sizeof(mbi)) == 0)
        return false;
    if (mbi.State != MEM_COMMIT)
        return false;
    if (mbi.Type != MEM_PRIVATE)
        return false;
    SIZE_T region = mbi.RegionSize;
    if (mbi.AllocationBase) {
        MEMORY_BASIC_INFORMATION base_mbi{};
        if (VirtualQuery(mbi.AllocationBase, &base_mbi, sizeof(base_mbi)) != 0) {
            region = base_mbi.RegionSize;
        }
    }
    return region >= std::size_t{256} * 1024;
}

// Scan a buffer for runs of printable ASCII bytes followed by a null
// terminator. This catches fixed-size char[N] string fields that don't
// follow the MsvcString layout -- which is what Forza's SampleProperties
// turned out to use for its track-name fields.
void scan_for_ascii_strings(std::string& out, const std::uint8_t* base, std::size_t size, std::size_t min_len = 6) {
    std::size_t i = 0;
    while (i < size) {
        if (base[i] < 0x20 || base[i] >= 0x7F) {
            ++i;
            continue;
        }
        std::size_t start = i;
        while (i < size && base[i] >= 0x20 && base[i] < 0x7F)
            ++i;
        const std::size_t len = i - start;
        // Require null termination (or run-up to end of buffer).
        const bool terminated = (i == size) || base[i] == 0;
        if (len >= min_len && terminated) {
            char              header[160];
            char              preview[33] = {};
            const std::size_t cp          = std::min<std::size_t>(32, len);
            std::memcpy(preview, base + start, cp);
            const int n = std::snprintf(header, sizeof(header), "    [ascii] +0x%03zx  len=%2zu  \"%s%s\"\n", start,
                                        len, preview, len > 32 ? "..." : "");
            out.append(header, static_cast<std::size_t>(n));
        }
    }
}

void dump_hex_block(std::string& out, const void* base, std::size_t nbytes) {
    std::vector<std::uint8_t> buf(nbytes);
    if (!safe_read_bytes(buf.data(), base, buf.size())) {
        out += "    (memory not readable)\n";
        return;
    }
    for (std::size_t off = 0; off + 16 <= buf.size(); off += 16) {
        append_hex_line(out, static_cast<std::ptrdiff_t>(off), buf.data() + off);
    }
    out += "    detected MsvcString-shaped slots:\n";
    scan_for_msvc_strings(out, buf.data(), buf.size(), base);
    out += "    detected ASCII char[N]-shaped strings:\n";
    scan_for_ascii_strings(out, buf.data(), buf.size());
}

// True iff the candidate looks "active" -- has at least one 8-byte
// aligned QWORD between offset 0x10 and the end of view_buf that is
// a plausible heap pointer. Pure-zero or pure-CC inactive stations
// are skipped during discovery to keep output focused.
bool candidate_looks_active(const std::uint8_t* view_buf, std::size_t size) {
    for (std::size_t off = 0x10; off + 8 <= size; off += 8) {
        std::uintptr_t v = 0;
        std::memcpy(&v, view_buf + off, 8);
        if (v == 0)
            continue;
        if (looks_like_heap(reinterpret_cast<void*>(v)))
            return true;
    }
    return false;
}

// Dump the targets of the first `max_pointers` heap pointers in view_buf and
// scan each for MsvcStrings -- finds where the radio's strings live without a
// hardcoded chain. `depth` follows pointer hops: depth 2 also recurses into
// each target's pointers, which is what reaches a two-level chain
// (instance -> list -> block) like FH6's track metadata.
void dump_pointer_targets(std::string& out, const std::uint8_t* view_buf, std::size_t view_size,
                          std::size_t max_pointers = 4, std::size_t bytes_per_target = 192, int depth = 1,
                          const char* indent = "  ") {
    char line[200];
    std::snprintf(line, sizeof(line), "%sheap pointers found and what they point to:\n", indent);
    out += line;
    std::size_t reported = 0;
    for (std::size_t off = (depth == 1 ? 8 : 0); off + 8 <= view_size; off += 8) {
        if (reported >= max_pointers)
            break;
        std::uintptr_t v = 0;
        std::memcpy(&v, view_buf + off, 8);
        if (v == 0)
            continue;
        auto* tgt = reinterpret_cast<void*>(v);
        // MEM_PRIVATE only: stay out of module images (MEM_IMAGE) and
        // file mappings (MEM_MAPPED). Reading code pages from a
        // hooked process can trip Forza's anti-tamper. The
        // AllocationBase re-query inside looks_like_heap means we
        // still catch heap pointers near arena boundaries.
        if (!looks_like_heap(tgt))
            continue;

        const int n = std::snprintf(line, sizeof(line), "%s  field +0x%03zx -> 0x%llx (%zu bytes):\n", indent, off,
                                    static_cast<unsigned long long>(v), bytes_per_target);
        out.append(line, static_cast<std::size_t>(n));
        dump_hex_block(out, tgt, bytes_per_target);
        ++reported;

        // Recurse one more hop so two-level chains (wrapper -> body)
        // get their strings dumped, not just the wrapper bytes.
        if (depth > 1) {
            std::vector<std::uint8_t> child(bytes_per_target);
            if (safe_read_bytes(child.data(), tgt, child.size())) {
                dump_pointer_targets(out, child.data(), child.size(), max_pointers, bytes_per_target, depth - 1,
                                     "        ");
            }
        }
    }
    if (reported == 0) {
        std::snprintf(line, sizeof(line), "%s(no heap-arena pointers)\n", indent);
        out += line;
    }
}

} // namespace

std::string MetadataInjector::dump_candidates(std::size_t bytes_per_dump) const {
    std::string out;
    out.reserve(16384);

    if (!vt_) {
        out += "[discovery] MetadataInjector not resolved -- nothing to dump\n";
        return out;
    }

    const auto vt_addr = reinterpret_cast<std::uintptr_t>(vt_->address);

    // Same SEH-safe, refcount-validated heap-arena scan the periodic writer
    // uses -- not the brute-force find_heap_instances, which reads every
    // committed word unguarded and crashed the game during discovery.
    const std::vector<const void*> instances = horizon::game::find_instances_in_heap_arenas(vt_->address, image_);

    char header[200];
    int n = std::snprintf(header, sizeof(header),
                          "[discovery] vtable=%p  heap_candidates=%zu  chain_steps=%zu  dump_bytes=%zu\n", vt_->address,
                          instances.size(), config_.chain_offsets.size(), bytes_per_dump);
    out.append(header, static_cast<std::size_t>(n));

    // We pre-scan each candidate, dropping ones that look entirely
    // inactive (no heap pointers in their first 128 bytes). FH6 keeps
    // one make_shared<RadioStreamFmod> per station, most are dormant,
    // and dumping them all just clutters the output.
    std::vector<const void*> active;
    active.reserve(instances.size());
    for (const void* inst : instances) {
        std::uint8_t probe[128];
        if (!safe_read_bytes(probe, inst, sizeof(probe)))
            continue;
        const auto vptr = *reinterpret_cast<const std::uintptr_t*>(probe);
        if (vptr != vt_addr)
            continue;
        if (candidate_looks_active(probe, sizeof(probe))) {
            active.push_back(inst);
        }
    }
    n = std::snprintf(header, sizeof(header), "[discovery] candidates after activity filter: %zu\n", active.size());
    out.append(header, static_cast<std::size_t>(n));

    constexpr std::size_t kMaxReport = 3;
    int                   idx        = 0;
    for (const void* instance : active) {
        if (std::cmp_greater_equal(idx, kMaxReport)) {
            n = std::snprintf(header, sizeof(header), "[discovery] ... %zu more active candidates not shown\n",
                              active.size() - kMaxReport);
            out.append(header, static_cast<std::size_t>(n));
            break;
        }

        n = std::snprintf(header, sizeof(header), "[discovery] === active candidate #%d  instance=%p ===\n", idx++,
                          instance);
        out.append(header, static_cast<std::size_t>(n));

        // View A: 512 bytes at the instance itself. The RadioStreamFmod
        // object extends well past the first 128 bytes; a *direct* pointer
        // to the current track's properties may live deeper in, which would
        // give a clean one-hop chain instead of walking the playlist array.
        // 512 bytes: the object extends well past the first 128.
        std::uint8_t view_a[512];
        if (!safe_read_bytes(view_a, instance, sizeof(view_a))) {
            out += "  (instance unreadable)\n";
            continue;
        }
        out += "  view A -- 512 bytes at the instance itself:\n";
        for (std::size_t off = 0; off + 16 <= sizeof(view_a); off += 16) {
            append_hex_line(out, static_cast<std::ptrdiff_t>(off), view_a + off);
        }
        out += "    detected MsvcString-shaped slots in view A:\n";
        scan_for_msvc_strings(out, view_a, sizeof(view_a), instance);

        // Follow the instance's heap pointers two hops so a two-level chain
        // (instance -> list -> metadata block) reaches the string block.
        dump_pointer_targets(out, view_a, sizeof(view_a), 8, 192, 2);
    }

    return out;
}

int MetadataInjector::write_to_instance(const void* instance, std::string_view sound_name,
                                        std::string_view display_name, std::string_view artist) {
    if (!vt_)
        return 0;
    if (!config_.sound_name_offset && !config_.display_name_offset && !config_.artist_offset) {
        return 0;
    }
    const auto vt_addr = reinterpret_cast<std::uintptr_t>(vt_->address);
    const int  n       = process_one_instance(instance, vt_addr, config_, config_.chain_offsets.data(),
                                              config_.chain_offsets.size(), sound_name, display_name, artist);
    total_writes_.fetch_add(static_cast<std::uint64_t>(n), std::memory_order_relaxed);
    return n;
}

bool MetadataInjector::read_instance_strings(const void* instance, std::string& out_title,
                                             std::string& out_artist) const {
    if (!vt_)
        return false;
    const auto     vt_addr = reinterpret_cast<std::uintptr_t>(vt_->address);
    StringSnapshot snap{};
    read_strings_one(instance, vt_addr, config_, config_.chain_offsets.data(), config_.chain_offsets.size(), &snap);
    if (!snap.ok)
        return false;
    out_title.assign(snap.title, snap.title_len);
    out_artist.assign(snap.artist, snap.artist_len);
    return true;
}

} // namespace horizon::inject

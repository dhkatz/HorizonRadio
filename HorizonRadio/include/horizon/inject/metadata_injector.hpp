#pragma once

#include <atomic>
#include <cstdint>
#include <horizon/inject/msvc_rtti.hpp>
#include <horizon/inject/sigscan.hpp>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace horizon::inject {

// Configuration for finding and writing into a game's audio-metadata
// struct. All values are game-specific -- the FH6 defaults live in
// signatures::kFh6Metadata.
struct MetadataInjectorConfig {
    // MSVC mangled name of the outer class we heap-scan for. For FH6
    // this is the make_shared<RadioStreamFmod> control block.
    std::string class_mangled_name;

    // Pointer-chase chain from a candidate instance to the struct that
    // holds the string fields. Each entry adds the offset to the
    // current address and then dereferences. Empty means "the
    // candidate address itself already holds the strings."
    std::vector<std::ptrdiff_t> chain_offsets;

    // Offsets within the chain endpoint struct where each char[N]
    // field lives. nullopt means "this field isn't present in this
    // game build, skip the write." Useful because FH6's current
    // SampleProperties only has display_name + sound_name (no
    // separate artist), whereas the abandoned mod's reference layout
    // had three fields.
    std::optional<std::ptrdiff_t> sound_name_offset;
    std::optional<std::ptrdiff_t> display_name_offset;
    std::optional<std::ptrdiff_t> artist_offset;

    // Size of each char[N] field. Only used when use_msvc_strings is
    // false; ignored otherwise.
    std::size_t field_size = 32;

    // Field type. true → fields are MSVC basic_string<char> (32-byte
    // header with SSO/heap pointer + size + capacity); writes go
    // through write_msvc_string. false → fields are plain char[N];
    // writes truncate-and-null. FH6's current SampleProperties uses
    // MSVC std::strings (per g0ldyy/fh6-universal-radio).
    bool use_msvc_strings = false;
};

// Helper exposed for testing. Each chain step: add `offset` to the
// current address, then dereference. Returns nullptr if the
// dereference yields zero at any step.
const void* walk_offset_chain(const void* start, std::span<const std::ptrdiff_t> chain);

// Glue between MsvcRtti, find_heap_instances, and write_msvc_string.
//
// Lifecycle:
//   1. Construct with a PeImage and a config.
//   2. Call resolve() once -- looks up the class's vtable in the module.
//   3. Call write() whenever the active track changes. Each write
//      re-scans the heap for live instances and re-applies the strings.
class MetadataInjector {
public:
    MetadataInjector(const PeImage& image, MetadataInjectorConfig config);

    // Resolve the configured class's vtable via MsvcRtti. Returns false
    // if any of TD / COL / vtable lookups fail.
    bool resolve();
    bool resolved() const noexcept {
        return vt_.has_value();
    }

    // The vtable found during resolve(). Exposed to callers that
    // want to use the resolved address (e.g. the periodic writer's
    // refcount-validated heap-arena scan).
    std::optional<Vtable> vtable() const noexcept {
        return vt_;
    }

    // Write to a specific known instance. Walks the configured
    // chain from `instance`, validates the chain endpoint is a
    // plausible string region (rejects spurious vtable matches),
    // and writes the configured field offsets. Returns 1 on
    // success, 0 on validation failure or null chain.
    int write_to_instance(const void* instance, std::string_view sound_name, std::string_view display_name,
                          std::string_view artist);

    // Read the current title/artist at the chain endpoint into std::strings.
    // Snapshots the game's originals before we overwrite them, so the caller
    // can restore them when it stops replacing a station. SEH-safe; returns
    // false if the chain doesn't resolve to a valid string block.
    bool read_instance_strings(const void* instance, std::string& out_title, std::string& out_artist) const;

    std::uint64_t total_writes() const noexcept {
        return total_writes_.load(std::memory_order_relaxed);
    }

    // Diagnostic: heap-scan for live instances, walk the chain on
    // each, hex-dump bytes_per_dump bytes at the chain endpoint, and
    // identify any MsvcString-shaped regions in the dump. Returns
    // a formatted multi-line report; the caller routes it wherever
    // they want (OutputDebugStringW, file, HTTP, etc.).
    //
    // Used during initial setup against a new game build to discover
    // sound_name_offset / display_name_offset / artist_offset by
    // looking at the dump and identifying which 32-byte slots hold
    // the three track-string fields.
    std::string dump_candidates(std::size_t bytes_per_dump = 192) const;

private:
    const PeImage&             image_;
    MetadataInjectorConfig     config_;
    std::optional<Vtable>      vt_;
    std::atomic<std::uint64_t> total_writes_{0};
};

} // namespace horizon::inject

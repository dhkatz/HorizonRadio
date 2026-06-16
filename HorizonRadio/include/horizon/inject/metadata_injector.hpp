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

// Where to find and how to write a game's audio-metadata struct. Game-specific;
// FH6 defaults live in signatures::kFh6Metadata.
struct MetadataInjectorConfig {
    std::string class_mangled_name; // MSVC mangled name of the heap-scanned class

    // Pointer-chase from a candidate instance to the string-field struct (each
    // step: add offset, deref). Empty = strings live at the candidate itself.
    std::vector<std::ptrdiff_t> chain_offsets;

    // Field offsets in the endpoint struct; nullopt = field absent in this build.
    std::optional<std::ptrdiff_t> sound_name_offset;
    std::optional<std::ptrdiff_t> display_name_offset;
    std::optional<std::ptrdiff_t> artist_offset;

    std::size_t field_size = 32; // char[N] size; ignored when use_msvc_strings

    // true = MSVC std::string fields (write_msvc_string); false = char[N]
    // (truncate-and-null). FH6's SampleProperties uses std::strings.
    bool use_msvc_strings = false;
};

// Each chain step: add `offset`, deref. nullptr if any deref yields zero.
const void* walk_offset_chain(const void* start, std::span<const std::ptrdiff_t> chain);

// Glue between MsvcRtti, the heap scan, and write_msvc_string: construct,
// resolve() the vtable once, then write_to_instance per track change. See
// docs/architecture.md -> "Metadata path".
class MetadataInjector {
public:
    MetadataInjector(const PeImage& image, MetadataInjectorConfig config);

    // Resolve the configured class's vtable via MsvcRtti. Returns false
    // if any of TD / COL / vtable lookups fail.
    bool resolve();
    bool resolved() const noexcept {
        return vt_.has_value();
    }

    // The resolved vtable (used by the periodic writer's heap-arena scan).
    std::optional<Vtable> vtable() const noexcept {
        return vt_;
    }

    // Walk the chain from `instance`, validate the endpoint is a plausible
    // string region (rejects spurious vtable matches), and write the fields.
    // Returns 1 on success, 0 on validation failure / null chain.
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

    // Diagnostic for re-deriving field offsets on a new build: heap-scan, walk
    // the chain, hex-dump the endpoint, and flag MsvcString-shaped regions.
    // Returns a formatted report; see docs/architecture.md -> "Discovery dump".
    std::string dump_candidates(std::size_t bytes_per_dump = 192) const;

private:
    const PeImage&             image_;
    MetadataInjectorConfig     config_;
    std::optional<Vtable>      vt_;
    std::atomic<std::uint64_t> total_writes_{0};
};

} // namespace horizon::inject

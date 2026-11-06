#include <array>
#include <cstddef>
#include <cstring>
#include <doctest/doctest.h>
#include <horizon/inject/metadata_injector.hpp>
#include <memory>
#include <ostream>
#include <string>
#include <windows.h>

using namespace horizon::inject;

// Polymorphic target so MSVC emits RTTI for it. Three char[32] fields
// at known offsets after the vptr -- matches FH6's SampleProperties
// layout (32-byte null-terminated buffers).
class HorizonInjectTarget {
public:
    virtual ~HorizonInjectTarget() = default;
    char sound_name[32]{};
    char display_name[32]{};
    char artist[32]{};
};

namespace {

// Build a config that points at HorizonInjectTarget's fields directly
// (no chain). Used by every behavioural test below.
MetadataInjectorConfig direct_cfg() {
    MetadataInjectorConfig cfg{};
    cfg.class_mangled_name  = ".?AVHorizonInjectTarget@@";
    cfg.sound_name_offset   = offsetof(HorizonInjectTarget, sound_name);
    cfg.display_name_offset = offsetof(HorizonInjectTarget, display_name);
    cfg.artist_offset       = offsetof(HorizonInjectTarget, artist);
    cfg.field_size          = 32;
    return cfg;
}

} // namespace

TEST_CASE("walk_offset_chain: empty chain returns start unchanged") {
    int dummy = 0;
    CHECK(walk_offset_chain(&dummy, {}) == &dummy);
}

TEST_CASE("walk_offset_chain: single deref through known offset") {
    int leaf = 42;
    struct {
        char  pad[16];
        void* p;
    } outer{.p = &leaf};

    std::array<std::ptrdiff_t, 1> chain{16};
    CHECK(walk_offset_chain(&outer, chain) == &leaf);
}

TEST_CASE("walk_offset_chain: multi-step traversal") {
    int   leaf = 42;
    void* mid  = &leaf;
    struct {
        char  pad[8];
        void* p;
    } outer{.p = static_cast<void*>(&mid)};

    std::array<std::ptrdiff_t, 2> chain{8, 0};
    CHECK(walk_offset_chain(&outer, chain) == &leaf);
}

TEST_CASE("walk_offset_chain: returns nullptr when a deref slot is null") {
    struct {
        char  pad[16];
        void* p = nullptr;
    } outer;

    std::array<std::ptrdiff_t, 1> chain{16};
    CHECK(walk_offset_chain(&outer, chain) == nullptr);
}

TEST_CASE("MetadataInjector: resolve fails for unknown class") {
    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MetadataInjectorConfig cfg{};
    cfg.class_mangled_name = ".?AVThisClassIsNotPresent@@";
    MetadataInjector inj(img, cfg);

    CHECK_FALSE(inj.resolve());
    CHECK_FALSE(inj.resolved());
}

TEST_CASE("MetadataInjector: write_to_instance before resolve does nothing") {
    PeImage                img(GetModuleHandleW(nullptr));
    MetadataInjectorConfig cfg{};
    MetadataInjector       inj(img, cfg);

    int dummy = 0;
    CHECK(inj.write_to_instance(&dummy, "a", "b", "c") == 0);
    CHECK(inj.total_writes() == 0);
}

TEST_CASE("MetadataInjector: write_to_instance writes char[32] fields") {
    auto target = std::make_unique<HorizonInjectTarget>();

    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MetadataInjector inj(img, direct_cfg());
    REQUIRE(inj.resolve());

    const int n = inj.write_to_instance(target.get(), "track_name", "Display Title", "Artist Name");
    CHECK(n == 1);
    CHECK(inj.total_writes() == 1);

    CHECK(std::string(target->sound_name) == "track_name");
    CHECK(std::string(target->display_name) == "Display Title");
    CHECK(std::string(target->artist) == "Artist Name");
}

TEST_CASE("MetadataInjector: long strings get truncated to fit char[32]") {
    auto target = std::make_unique<HorizonInjectTarget>();

    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MetadataInjector inj(img, direct_cfg());
    REQUIRE(inj.resolve());

    const std::string long_value(60, 'a');
    inj.write_to_instance(target.get(), long_value, long_value, long_value);

    CHECK(std::strlen(target->sound_name) == 31);
    CHECK(std::strlen(target->display_name) == 31);
    CHECK(std::strlen(target->artist) == 31);
    CHECK(target->sound_name[31] == '\0');
}

TEST_CASE("MetadataInjector: nullopt offsets are skipped without writing") {
    auto target = std::make_unique<HorizonInjectTarget>();
    std::strcpy(target->sound_name, "original_sound");
    std::strcpy(target->display_name, "original_display");
    std::strcpy(target->artist, "original_artist");

    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    auto cfg = direct_cfg();
    cfg.sound_name_offset.reset(); // skip
    cfg.artist_offset.reset();     // skip
    // display_name_offset stays set

    MetadataInjector inj(img, cfg);
    REQUIRE(inj.resolve());

    inj.write_to_instance(target.get(), "ignored", "new_display", "also_ignored");

    CHECK(std::string(target->sound_name) == "original_sound");
    CHECK(std::string(target->display_name) == "new_display");
    CHECK(std::string(target->artist) == "original_artist");
}

TEST_CASE("MetadataInjector: write_to_instance with all-nullopt config is a no-op") {
    auto target = std::make_unique<HorizonInjectTarget>();
    std::strcpy(target->sound_name, "untouched");

    PeImage img(GetModuleHandleW(nullptr));
    REQUIRE(img.valid());

    MetadataInjectorConfig cfg{};
    cfg.class_mangled_name = ".?AVHorizonInjectTarget@@";
    cfg.field_size         = 32;
    // All field offsets stay nullopt.

    MetadataInjector inj(img, cfg);
    REQUIRE(inj.resolve());

    CHECK(inj.write_to_instance(target.get(), "a", "b", "c") == 0);
    CHECK(std::string(target->sound_name) == "untouched");
}

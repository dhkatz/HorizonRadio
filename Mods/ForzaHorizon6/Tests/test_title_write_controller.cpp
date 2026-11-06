#include <doctest/doctest.h>
#include <horizon/inject/title_write_controller.hpp>
#include <map>
#include <string>
#include <string_view>
#include <utility>

using horizon::inject::TitleWriteController;

namespace {

// Stand-in for MetadataInjector: models each game metadata block as a
// (title, artist) pair keyed by instance pointer. Lets the FSM tests assert
// exactly what was written/restored without RTTI, MSVC-string layout, or the
// game process. Satisfies the duck-typed surface TitleWriteController needs.
struct FakeInjector {
    int  writes    = 0;
    bool fail_read = false; // chain "doesn't resolve" for reads

    std::map<const void*, std::pair<std::string, std::string>> blocks;

    bool read_instance_strings(const void* inst, std::string& title, std::string& artist) const {
        if (fail_read)
            return false;
        auto it = blocks.find(inst);
        if (it == blocks.end())
            return false;
        title  = it->second.first;
        artist = it->second.second;
        return true;
    }

    int write_to_instance(const void* inst, std::string_view /*sound*/, std::string_view title,
                          std::string_view artist) {
        blocks[inst] = {std::string(title), std::string(artist)};
        ++writes;
        return 1;
    }
};

// Distinct, stable instance "addresses". Values are never dereferenced -- the
// controller only compares pointers and forwards them to the injector.
int g_a;
int g_b;

const void* const kInst1 = &g_a;
const void* const kInst2 = &g_b;

} // namespace

TEST_CASE("TitleWriteController: first touch snapshots originals, writes ours, restores on idle") {
    FakeInjector inj;
    inj.blocks[kInst1] = {"GameTitle", "GameArtist"};
    TitleWriteController c;

    const int n = c.on_active(inj, kInst1, "sound", "OurTitle", "OurArtist");

    CHECK(n == 1);
    CHECK(c.owns_block());
    CHECK(c.written_instance() == kInst1);
    CHECK(inj.blocks[kInst1] == std::pair<std::string, std::string>{"OurTitle", "OurArtist"});

    c.on_idle(inj);

    CHECK_FALSE(c.owns_block());
    CHECK(inj.blocks[kInst1] == std::pair<std::string, std::string>{"GameTitle", "GameArtist"});
}

TEST_CASE("TitleWriteController: on_idle without an owned block is a no-op") {
    FakeInjector         inj;
    TitleWriteController c;

    c.on_idle(inj);

    CHECK_FALSE(c.owns_block());
    CHECK(inj.writes == 0);
}

TEST_CASE("TitleWriteController: switching to a still-live instance restores the old one") {
    FakeInjector inj;
    inj.blocks[kInst1] = {"Title1", "Artist1"};
    inj.blocks[kInst2] = {"Title2", "Artist2"};
    TitleWriteController c;

    c.on_active(inj, kInst1, "s", "OurTitle", "OurArtist");
    // Selection moves to inst2.
    c.on_active(inj, kInst2, "s", "OurTitle", "OurArtist");

    CHECK(c.written_instance() == kInst2);
    // inst1 put back to its original, inst2 now holds ours.
    CHECK(inj.blocks[kInst1] == std::pair<std::string, std::string>{"Title1", "Artist1"});
    CHECK(inj.blocks[kInst2] == std::pair<std::string, std::string>{"OurTitle", "OurArtist"});
}

TEST_CASE("TitleWriteController: switching away restores the old block even if it left the scan") {
    FakeInjector inj;
    inj.blocks[kInst1] = {"Title1", "Artist1"};
    inj.blocks[kInst2] = {"Title2", "Artist2"};
    TitleWriteController c;

    c.on_active(inj, kInst1, "s", "OurTitle", "OurArtist");
    // inst1 has dropped out of the (stale) heap scan, but the restore must
    // still run: write_to_instance is vptr-checked under SEH, so a truly freed
    // block is a safe no-op, and skipping it would leave our title frozen on the
    // station the user tuned past (the neighbor-metadata bug).
    c.on_active(inj, kInst2, "s", "OurTitle", "OurArtist");

    CHECK(c.written_instance() == kInst2);
    CHECK(inj.blocks[kInst1] == std::pair<std::string, std::string>{"Title1", "Artist1"});
    CHECK(inj.blocks[kInst2] == std::pair<std::string, std::string>{"OurTitle", "OurArtist"});
}

TEST_CASE("TitleWriteController: restore value resyncs when the game advances its own track") {
    FakeInjector inj;
    inj.blocks[kInst1] = {"GameA", "ArtistA"};
    TitleWriteController c;

    // Tick 1: snapshot GameA, write ours.
    c.on_active(inj, kInst1, "s", "Ours", "OursArtist");
    // Game advanced its own track underneath us (block no longer holds ours).
    inj.blocks[kInst1] = {"GameB", "ArtistB"};
    // Tick 2: must notice the change and adopt GameB as the new restore value.
    c.on_active(inj, kInst1, "s", "Ours", "OursArtist");

    c.on_idle(inj);

    CHECK(inj.blocks[kInst1] == std::pair<std::string, std::string>{"GameB", "ArtistB"});
}

TEST_CASE("TitleWriteController: a null active instance restores the previously written block") {
    FakeInjector inj;
    inj.blocks[kInst1] = {"Title1", "Artist1"};
    TitleWriteController c;

    c.on_active(inj, kInst1, "s", "OurTitle", "OurArtist");
    const int n = c.on_active(inj, nullptr, "s", "OurTitle", "OurArtist");

    CHECK(n == 0);
    CHECK_FALSE(c.owns_block());
    CHECK(inj.blocks[kInst1] == std::pair<std::string, std::string>{"Title1", "Artist1"});
}

TEST_CASE("TitleWriteController: no original is restored when the first-touch read failed") {
    FakeInjector inj;
    inj.fail_read      = true; // chain never resolves for reads
    inj.blocks[kInst1] = {"GameTitle", "GameArtist"};
    TitleWriteController c;

    c.on_active(inj, kInst1, "s", "OurTitle", "OurArtist");
    CHECK(inj.blocks[kInst1] == std::pair<std::string, std::string>{"OurTitle", "OurArtist"});

    c.on_idle(inj);

    // saved_valid was false, so idle must not write a (bogus) restore value.
    CHECK(inj.blocks[kInst1] == std::pair<std::string, std::string>{"OurTitle", "OurArtist"});
}

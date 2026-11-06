#pragma once

#include <string>
#include <string_view>

namespace horizon::inject {

// Owns the "write our title into one game metadata block, then restore the
// game's originals when we stop" bookkeeping for the single instance the
// periodic writer picked (the writer owns instance selection). Extracted so the
// transition logic is unit-testable; see docs/architecture.md -> "Metadata
// path". Not thread-safe (the writer is the only caller).
//
// Ownership is tracked by the metadata BLOCK, not the stream instance: the game
// re-points an instance at another station's block when the player tunes, so
// anything done "through the instance" after a switch lands on the new
// station. And while tuning, the station name changes 50-100 ms BEFORE the
// instance is re-pointed, so for a moment the tuned station and the block
// disagree. The rules:
//   * the caller says whether the block belongs to the tuned station
//     (BlockIdentity, from the block's event id): a match is written at once,
//     a mismatch never, and on a mismatch we hand back a block we own while
//     the instance still shows it (we're tuning away from it);
//   * restore only while the instance still resolves to the block we wrote --
//     otherwise the station changed and restoring would stamp the old
//     station's title onto the new one; and
//   * when identity is unknown (a block without a station code), only write
//     after the block has stayed resolved for `settle_ticks` consecutive ticks,
//     so flicking through stations never writes to one you're passing.
//
// Injector is duck-typed so tests can pass a fake; it must provide:
//   const void* resolve_block(const void* instance) const;  // null if unresolvable
//   bool read_instance_strings(const void*, std::string& title, std::string& artist) const;
//   int  write_to_instance(const void*, std::string_view sound, title, artist);
// Whether the block about to be written belongs to the station tuned in.
enum class BlockIdentity {
    Unknown,  // no station code: fall back to waiting for the block to settle
    Match,    // it's the tuned station's block: write now
    Mismatch, // it's another station's (mid-tune): don't write
};

class TitleWriteController {
public:
    // The periodic writer ticks at 20 Hz: 3 ticks ~= 150 ms of stability before
    // our title appears on a newly tuned station whose block identity is unknown.
    static constexpr int kDefaultSettleTicks = 3;

    explicit TitleWriteController(int settle_ticks = kDefaultSettleTicks) noexcept : settle_ticks_(settle_ticks) {}

    // Tick where we have a track and an already-selected target. `active_instance`
    // is the instance the caller chose to replace (may be null when nothing
    // resolves this tick). Returns the injector's write count (0 when nothing
    // was written).
    template <class Injector>
    int on_active(Injector& inj, const void* active_instance, std::string_view sound, std::string_view title,
                  std::string_view artist, BlockIdentity identity = BlockIdentity::Unknown) {
        const void* block = active_instance != nullptr ? inj.resolve_block(active_instance) : nullptr;

        // The block we replaced is no longer the one to write: another instance
        // was picked, this instance now shows a different station's block, or
        // the tuned station moved on while the instance still shows ours.
        // restore() itself refuses to write through a re-pointed instance.
        if (written_block_ != nullptr &&
            (written_instance_ != active_instance || written_block_ != block || identity == BlockIdentity::Mismatch))
            restore(inj);

        if (identity == BlockIdentity::Mismatch || block == nullptr) {
            candidate_block_ = block;
            stable_ticks_    = 0;
            return 0;
        }
        if (block != candidate_block_) {
            candidate_block_ = block;
            stable_ticks_    = 0;
        } else if (stable_ticks_ < settle_ticks_) {
            ++stable_ticks_;
        }
        if (identity != BlockIdentity::Match && stable_ticks_ < settle_ticks_)
            return 0;

        if (written_block_ != block) {
            // First touch of this block: snapshot the game's current
            // title/artist as the restore value.
            saved_valid_       = inj.read_instance_strings(active_instance, saved_title_, saved_artist_);
            written_instance_  = active_instance;
            written_block_     = block;
            have_last_written_ = false;
        } else if (have_last_written_) {
            // Keep the restore value synced to the game's real track: the
            // block holds what we wrote last tick UNLESS the game advanced
            // its own track, in which case it now holds the game's new
            // title. Comparing against our last write (not our current one)
            // avoids mistaking our own title for the game's on the tick our
            // song changes.
            std::string cur_title, cur_artist;
            if (inj.read_instance_strings(active_instance, cur_title, cur_artist) &&
                (cur_title != last_written_title_ || cur_artist != last_written_artist_)) {
                saved_title_  = std::move(cur_title);
                saved_artist_ = std::move(cur_artist);
                saved_valid_  = true;
            }
        }

        const int n = inj.write_to_instance(active_instance, sound, title, artist);
        if (n) {
            last_written_title_.assign(title);
            last_written_artist_.assign(artist);
            have_last_written_ = true;
        }
        return n;
    }

    // Tick where we are not writing (no track / unresolved / off target): put
    // the game's original strings back if we still own a block, so a stopped
    // source doesn't leave our title frozen on the station.
    template <class Injector> void on_idle(Injector& inj) {
        if (written_block_ != nullptr)
            restore(inj);
        candidate_block_ = nullptr;
        stable_ticks_    = 0;
    }

    [[nodiscard]] bool owns_block() const noexcept {
        return written_block_ != nullptr;
    }
    [[nodiscard]] const void* written_instance() const noexcept {
        return written_instance_;
    }

private:
    template <class Injector> void restore(Injector& inj) {
        // Write the originals back only through an instance that still shows
        // the block we changed. If it was re-pointed (station switch) or freed,
        // the block is gone from that path: restoring would hit another
        // station's block, and writing to the old block's address directly
        // could corrupt memory the game has since reused.
        if (written_instance_ != nullptr && saved_valid_ && inj.resolve_block(written_instance_) == written_block_)
            inj.write_to_instance(written_instance_, "", saved_title_, saved_artist_);
        written_instance_  = nullptr;
        written_block_     = nullptr;
        saved_valid_       = false;
        have_last_written_ = false;
    }

    int         settle_ticks_;
    const void* candidate_block_  = nullptr; // block seen this tick, being settled
    int         stable_ticks_     = 0;       // consecutive ticks candidate_block_ held
    const void* written_instance_ = nullptr; // the instance we wrote through (null = none)
    const void* written_block_    = nullptr; // the block we currently replace (null = none)
    std::string saved_title_;                // game's original title to restore
    std::string saved_artist_;               //   (kept in sync as the game advances tracks)
    bool        saved_valid_ = false;
    std::string last_written_title_; // what WE wrote into the block last tick
    std::string last_written_artist_;
    bool        have_last_written_ = false;
};

} // namespace horizon::inject

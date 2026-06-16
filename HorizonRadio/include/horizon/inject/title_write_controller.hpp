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
// Injector is duck-typed so tests can pass a fake; it must provide:
//   bool read_instance_strings(const void*, std::string& title, std::string& artist) const;
//   int  write_to_instance(const void*, std::string_view sound, title, artist);
class TitleWriteController {
public:
    // Tick where we have a track and an already-selected target. `active_instance`
    // is the instance the caller chose to replace (may be null when nothing
    // resolves this tick). Returns the injector's write count (0 when nothing
    // was written).
    template <class Injector>
    int on_active(Injector& inj, const void* active_instance, std::string_view sound, std::string_view title,
                  std::string_view artist) {
        // Switching targets: restore the game's originals on the block we were
        // replacing -- ALWAYS, even if it has dropped out of the (cached,
        // seconds-stale) heap scan. write_to_instance re-checks the vptr under
        // SEH, so a block that was actually freed is a safe no-op; skipping the
        // restore instead leaves our title frozen on the station the user tuned
        // past, which is the "neighbor stations show our metadata" bug that
        // showed up when switching stations quickly.
        if (written_instance_ && written_instance_ != active_instance) {
            restore(inj);
        }

        int n = 0;
        if (active_instance) {
            if (written_instance_ != active_instance) {
                // First touch of this block: snapshot the game's current
                // title/artist as the restore value.
                saved_valid_       = inj.read_instance_strings(active_instance, saved_title_, saved_artist_);
                written_instance_  = active_instance;
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
            n = inj.write_to_instance(active_instance, sound, title, artist);
            if (n) {
                last_written_title_.assign(title);
                last_written_artist_.assign(artist);
                have_last_written_ = true;
            }
        }
        return n;
    }

    // Tick where we are not writing (no track / unresolved): put the game's
    // original strings back if we currently own a block, so a stopped source
    // doesn't leave our title frozen on the station.
    template <class Injector> void on_idle(Injector& inj) {
        if (written_instance_)
            restore(inj);
    }

    [[nodiscard]] bool owns_block() const noexcept {
        return written_instance_ != nullptr;
    }
    [[nodiscard]] const void* written_instance() const noexcept {
        return written_instance_;
    }

private:
    template <class Injector> void restore(Injector& inj) {
        if (written_instance_ && saved_valid_)
            inj.write_to_instance(written_instance_, "", saved_title_, saved_artist_);
        written_instance_  = nullptr;
        saved_valid_       = false;
        have_last_written_ = false;
    }

    const void* written_instance_ = nullptr; // the block we currently replace (null = none)
    std::string saved_title_;                // game's original title to restore
    std::string saved_artist_;               //   (kept in sync as the game advances tracks)
    bool        saved_valid_ = false;
    std::string last_written_title_; // what WE wrote into the block last tick
    std::string last_written_artist_;
    bool        have_last_written_ = false;
};

} // namespace horizon::inject

# Changelog

All notable changes to Horizon Radio are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Keep your settings, mixes and history in step between your PCs (Settings → Devices).** Choose
  Show Code on one PC and type the code on the other: they pair, then keep their mixes, play
  history and synced settings (lyrics, shuffle, the search order, the metadata providers' order,
  event rules and controls) the same, the newest change of each winning. A mix deleted, a song
  removed from History, or History cleared on one goes from all. A PC that was off catches up when it's back. They connect to each other directly
  over the IPFS network or your local network, with no account and no server of ours; folders,
  audio devices, the game, logins and keys stay on each PC. A code is good for 10 minutes and one
  try. Devices can be renamed, removed, or leave.
- **Run a second copy of the app with its own settings: `--profile <name>`.** Started with
  `--profile test`, Horizon Radio keeps everything (settings, history, mixes, logins, caches,
  logs) in `%LOCALAPPDATA%\HorizonRadio\profiles\test` instead, so two copies can run side by
  side without touching each other's data. Downloaded tools are shared. The window title and the
  Spotify Connect devices show the profile ("Horizon Radio [test]").
- **Add any song to the queue or a mix, from wherever it shows.** History rows, search results, and
  a mix's entries have Add To Queue and Add To Mix buttons; right-click a song in the queue, the
  Dashboard, or the player bar for the same menu. Add To Mix lists your mixes (ticking the ones that
  already have the song) and New Mix…, which opens a new mix with the song in it. A radio song is
  first identified and found on Spotify or YouTube, and that's what's added.
- **Mix entries show what they are.** The mix editor shows each entry with its title, artist,
  and cover (a track), or its name, owner, track count, and cover (a Spotify or YouTube playlist,
  a Spotify album, a music folder, a radio station), with a "Looking up…" state while it loads and
  a reason when a link can't be opened. Paste a link or a path into the new add bar and the source
  is picked for you. Mix cards in the library show cover art and their first entries' titles.
  What each entry turned out to be is kept in `%LOCALAPPDATA%\HorizonRadio\mix-entries`, so mixes
  open at once next time. Plugin SDK: content players can describe a collection
  (`IContentDescriber`), and content sources can recognise their own links
  (`IContentSourceFactory.Recognizes`).
- **Spotify tracks Spotify won't give librespot play through Spotify Soloist.** Spotify now
  refuses librespot the audio of some tracks, and for some accounts all of them. Once Soloist is
  installed (Tools tab) and your Soloist key is in the Spotify settings, those tracks play through
  Soloist, Spotify's own Connect client, into the game like any other, and Soloist itself stays
  silent. After the first refusal, later tracks go straight to Soloist. The first time, a toast
  asks you to pick "Horizon Radio (Soloist)" once in the Spotify app to log it in, and the track
  waits (up to 3 minutes) and plays once you have. This is against Spotify's
  developer terms and could get the key revoked.
- **Soloist stays the Spotify player once librespot is refused.** When Spotify refuses librespot
  and Soloist plays the track, later tracks go straight to Soloist, across restarts, so they
  don't wait for librespot to be refused first. librespot gets another try once a day, and right
  away after it's updated; when it plays again, it's the player again.
- **Spotify Soloist in the Tools tab.** Installs Spotify's own Connect client straight from
  Spotify, checked against the checksum Spotify's servers give, and shows when Spotify has a
  newer build (each one stops working 90 days after release). The Spotify settings have a place
  for your Soloist key; playback through Soloist comes next.
- **Session logs on disk.** Everything the Console tab shows, plus the app's own diagnostic
  lines (the queue, sources, metadata), is also written to
  `%LOCALAPPDATA%\HorizonRadio\logs`, one file per session (the last 10 are kept), so a
  problem can be looked into after it happened.
- **Groundwork for playing Spotify tracks librespot is refused.** A small native DLL
  (`horizon_audio_tap.dll`, bundled with the app) can take the audio of Spotify Soloist, Spotify's
  own Connect client, without it playing out loud. Not used yet: the Soloist fallback comes
  next. The release build now also builds and bundles this DLL, and CI tests and lints it.
- **Radio lyrics follow the song from its audio.** When a radio song has synced lyrics, Horizon
  Radio lines the station's audio up against a recording of the song (its video on YouTube or
  Niconico, from VocaDB or a search) and follows the lyrics from exactly where the song is, to a
  few hundredths of a second. This also works for the song that was already playing when you
  tuned in, which used to show its lyrics only as text, and it fixes stations that change their
  title seconds before or after the song changes. A recording that isn't the song never lines up,
  so the wrong one is never used. Each song's recording is downloaded once (a few MB) and its
  fingerprint kept; the waveform button on the lyrics panel turns matching off. Fingerprinting
  uses [AcoustID.NET](https://github.com/wo80/AcoustID.NET) (Chromaprint, LGPL-2.1).

- **Lyrics from the Vocaloid Lyrics Wiki.** When VocaDB knows a song but has no lyrics
  for it (e.g. "Blurry Star"), the lyrics come from the [Vocaloid Lyrics
  Wiki](https://vocaloidlyrics.miraheze.org) instead, with its romaji and English
  translation as versions to switch to. The page is found by the VocaDB song it links,
  so it's always the same song. Credited per the wiki's CC BY-SA 4.0 license.
- **Lyrics on the Dashboard.** The playing song's lyrics show under its title. When the
  song plays from your files, YouTube, or Spotify, they follow along: the line being sung
  is highlighted and kept in view, and clicking a line jumps there in songs you can seek.
  Lyrics come from [Unison](https://unison.boidu.dev) (often synced word by word) and
  [LRCLIB](https://lrclib.net), and synced lyrics are only used when they were timed to a
  recording of the same length, so they don't drift ahead of the singer. Internet radio
  shows the lyrics as text for now, since it can't yet tell where in the song it is.
  Lyrics providers are plugins (`ILyricsPlugin`, under `Plugins/Lyrics/`).
- **Karaoke lyrics.** Synced lyrics fill in as they're sung: word by word (syllable by
  syllable) when the lyrics are word-timed, across the line otherwise. The microphone
  button on the lyrics panel turns it off, to light the whole line at once instead.
- **Lyrics from VocaDB, with romaji and translations.** Vocaloid and other synth-voice
  songs get their lyrics from VocaDB when Unison and LRCLIB have nothing synced, and you
  can switch between the original, its romanization, and translations. The version you
  pick is remembered. On internet radio this more than doubles how many songs have
  lyrics (33 of 40 in a sample of Vocaloid radio, up from 15).
- **Synced lyrics on internet radio.** Radio now knows where in the song it is, from
  where the station's title change falls in the audio, so lyrics follow along (and fill
  karaoke-style) from the second song after tuning in. The song that was already playing
  when you tuned in still shows its lyrics as text. Stations time their title changes
  differently, so the lyrics panel has a nudge (±0.5 s, remembered) for radio.

- **VocaDB now supplies real album art and playable links.** A matched VocaDB song
  contributes its linked album's square cover (preferred over the 16:9 video
  thumbnail) and its promotion-video (PV) links — the official/reprint uploads on
  YouTube, Niconico, Bilibili, SoundCloud, … — which become one-click replay
  sources in play history. Non-YouTube PVs play through the existing yt-dlp engine,
  labeled by their real service. (Existing cached songs backfill on next play via a
  cache-version bump.)

- **Game build and bridge status in the Console.** On every connect the
  in-game DLL reports the FH6 build it's running on (known or unverified) and
  which parts of the bridge resolved (`fmod` / `metadata` / `radio_state`)
  under the `bridge` tag, so a game update that breaks something can be
  diagnosed without a debugger.

### Changed

- **Settings live in one file, `settings.json`.** The app's preferences, each source's
  settings, plugin settings, the metadata providers, event rules, controller and keyboard
  bindings and the diagnostics switch used to be spread over `sources.json`,
  `plugin-settings.json`, `metadata-config.json`, `events-config.json`, `controls-config.json`
  and `diagnostics\settings.json`. The first start of this version copies them across once and
  leaves the old files as they were. Each setting now records whether it may follow you to your
  other machines, ready for syncing your devices. Secrets, such as the Soloist key, stay
  encrypted, and the Spotify metadata client secret, which was stored in plain text, is now
  encrypted too.

- **Play on the Sources tab now plays through the queue.** Local folders, YouTube links,
  Spotify links and radio stations start right away from the queue, the same as Play in
  search. Shuffle, Next/Previous, Restart and pause now behave the same everywhere, which
  fixes playlists going silent after their last track, YouTube ignoring the saved shuffle
  setting, and quick skips getting lost or reversed. For plugin authors: a source that plays
  by itself now implements `ISelfDrivenSourceFactory`; content sources only enumerate items.
- **Faster CI.** The C# format check and the C++ tests run alongside the C# build instead
  of before it, NuGet restores once and to the runner's faster drive, the FH6 plugin no
  longer pulls a second copy of SkiaSharp (~1.2 GB) through LiveCharts, the Ubuntu
  cross-compile installs only the x64 mingw toolchain, and clang-tidy is skipped when the
  C++ it lints, its config, and the toolchain match an earlier passing run.
- **Radio titles change with the song.** A station is read about a second ahead of what
  you hear, so a new song's title (in the app and in game) used to appear before the
  song did. It now appears as the song starts.
- **Song details show up much sooner.** A new song's title and cover art used to take
  several seconds, often 30+ while a queue or mix was filling in. Now they usually take
  under a second. Each song is looked up once instead of three times, the metadata
  providers are asked at the same time instead of one after another, the playing song
  goes ahead of queue, mix, and History lookups, and a slow provider (MusicBrainz's cover
  art can take 2–6 s an image) no longer holds the others' answers back. The metadata
  trace now records how long each provider took.
- **Repository layout.** The app's projects moved under `HorizonRadio/`, the plugin
  SDK under `Plugins/`, and README screenshots to `Assets/`; the solution is now
  `HorizonRadio.slnx`. The root holds `Assets/`, `HorizonRadio/`, `Plugins/`, and
  `Mods/`. Release artifacts are unchanged (`build/release/HorizonRadio.zip`).
- **Use Horizon Radio without a game.** Pick "No Game" to use it as a plain music
  player: it plays through your speakers and hides everything game-related. New
  installs ask which game you play on first launch; existing setups keep their
  game. Switch any time in **Settings → Game** (Horizon Radio restarts to apply).
- **Game chip in the player bar.** Shows the station Forza is tuned to and what it
  plays there (e.g. "Gacha City Radio · Horizon Radio player"); click it to set up
  stations. Replaces the old station picker.
- **Events moved under Forza Horizon 6** in the sidebar (Mods, Stations, Events,
  Stats), and lists the events the game reports. Labels like the output picker and
  the connection status use the game's name.
- **Each FH6 station can play something different.** A new Stations page (under
  Forza Horizon 6) sets every in-game station to the game's own music (the
  default), the Horizon Radio player, or one of your mixes; tuning the in-game
  radio switches what Horizon Radio plays to match. Choose how mix stations
  behave while you're tuned elsewhere: pause and resume (default), start fresh,
  or "live radio" (they keep playing; manual pause is off). Requires the updated
  mod (`version.dll`). Mixes no longer carry a station override. Existing
  settings migrate on first launch: your default target station becomes the
  player's station, and a mix's station override becomes that station playing
  the mix (moved to the new `plugin-settings.json`).
- **Sidebar: game pages live under the game's name.** The Mods and Stats pages
  now come from the Forza Horizon 6 plugin and are listed under a "Forza Horizon
  6" section. Events and Controls moved to Settings, since they aren't specific
  to one game. The setup banner ("Mod Not Installed") is now supplied by the
  game plugin too.
- **Games are plugins now.** The plugin SDK gains `IGamePlugin` /
  `IGameIntegration`: a game integration supplies the audio output, game
  events, connection state, and optional capabilities (station targeting,
  in-game volume, now-playing display), and the app talks to the game only
  through that interface. Forza Horizon 6 support moved out of Core into the
  `HorizonRadio.Game.ForzaHorizon6` plugin. No user-visible change yet; this is
  the groundwork for other games and a game-free "just a player" mode.
- **The FH6 mod moved to `Mods/ForzaHorizon6/`.** The native `version.dll`
  project (formerly the root `HorizonRadio/` + `HorizonRadio.Tests/` dirs,
  root CMake files, and `.clang-*` configs) is now a self-contained CMake
  project under `Mods/ForzaHorizon6/` (`Bridge/` + `Tests/`), with its
  internals doc alongside. Run CMake from that directory; the DLL now lands in
  `Mods/ForzaHorizon6/build/Release/`. First step toward the game-integration
  plugin layout: game-side code under `Mods/<Game>/`, Horizon Radio-side code
  under `Plugins/`.
- **C++ side modernized; clang-tidy now enforced in CI.** Extracted the in-game
  title write/restore state machine out of the periodic-writer thread into a
  unit-tested `TitleWriteController`, then swept the native `version.dll` sources
  against a broad clang-tidy check set (`modernize`/`bugprone`/`performance`/
  `concurrency`/`cppcoreguidelines`/`misc`): `scoped_lock`, C++20 ranges,
  `auto`-on-cast, widening/sign-comparison fixes, `noexcept` global ctors,
  uninitialized-variable/member fixes, and a full const-correctness pass. A new
  CI job lints the production translation units with `WarningsAsErrors` so
  regressions can't merge. Headers are now covered too (an anchored
  `HeaderFilterRegex` had been silently excluding them), adding `[[nodiscard]]`,
  rule-of-5 on the thread-owning IPC/PCM servers, and dead-code removal.

### Fixed

- **Spotify playback through Soloist is sturdier.**
  - When Soloist fails on a track before any audio (an expired build, for one), librespot plays it
    instead of every track failing until the next daily check.
  - With autoplay off, the end of a track is recognized from where it is now, so it no longer replays.
  - A skipped track's late reply from Soloist can't end the next one.
  - A track Soloist can't play fails at once.
  - Soloist restarts when its audio stops arriving.
  - Updating Soloist from the Tools tab works while it's running.
- **librespot refusals are told apart from rate limits.** Spotify rate-limiting librespot no longer
  counts as a refusal, and a late refusal from a skipped track no longer fails the next one.
- **A secret setting that can't be decrypted is kept.** One encrypted for another Windows account
  or PC is kept as stored until you enter a new one, instead of being erased on the next save.
  Session log names include the process id.
- **Soloist could fail to start with "Couldn't take Soloist's audio".** The audio tap was loaded
  the moment Soloist launched, and once neither got going. It now goes in once Soloist is up,
  and if it still doesn't set up, Soloist is restarted once. The Console shows each step the tap
  takes.
- **Source settings save as you edit them.** The Sources tab only saved a source's settings
  when you pressed Play, Start or Connect there, so a setting typed in and then used from
  elsewhere (the Soloist key, then a track played from search) was never saved. When Spotify
  refuses librespot a track and Soloist can't take over, the Console now says what's missing.
- **Secret settings are encrypted.** A setting that's a credential (the Soloist key) is stored
  encrypted for your Windows account instead of in plain text in `sources.json`, and its box
  shows dots instead of the text.
- **A Spotify track Spotify won't play fails at once, with the reason.** When Spotify refuses
  librespot the audio (it does for more and more accounts, librespot #1649), the track used to
  hang for 15 seconds and fail with "the device may be unavailable". It now moves on within a
  second, and the Console says Spotify refused the track's audio.
- **librespot, ffmpeg and yt-dlp no longer outlive the app.** A librespot left running after the
  app closed stayed logged in as the same Spotify Connect device and took the next session's
  play commands, so Spotify tracks hung silently and were dropped. Tools now end with the app,
  however it closes.
- **Songs the app couldn't identify before get looked up again.** A song that matched nothing was
  remembered as a miss for two weeks, even after matching improved, so a radio song could stay
  unidentified long after the app learned to find it. Misses are now retried as soon as matching
  improves. Stations that spell a Japanese artist the English way ("Camelia" for かめりあ) now
  match too, including one-word titles like "∞度寝".

- **Two sources no longer play over each other, and switching stations no longer loses
  songs.** Starting Spotify Connect or the test tone just as the game changed station could
  leave both playing, and a song added to the queue mid-switch could vanish along with the
  one it interrupted. Stopping a mix station from the Sources tab now keeps its place for
  when you tune back, changing a station's switch mode no longer restarts its mix, shuffle
  reaches stations you aren't tuned to, and edited mixes reach their stations.
- **Skips, Play and mix switches no longer get lost while the next song loads.** Next, Play
  Now and mix switches during "Loading…" act at once rather than after that song (or one
  more from the old mix) plays through, and Play Now after Previous plays what you picked.
  Restart then Next skips, Next lights up as soon as you add to the queue, pause can't get
  stuck silent, the queue stops showing a song once Spotify takes over, and tuning back to a
  station no longer brings back a skipped song or the old mix.
- **Settings can no longer be wiped by a save that fails partway.** A crash or an unlucky
  moment while saving could leave `sources.json` (or another settings file) half-written,
  and the app then started with default settings. Settings are now written to a temporary
  file and swapped in only once complete, so the previous settings survive a failed save.
- **A station set to Resume or Live no longer goes silent after you tune back to it.**
  Leaving the station could leave its channel unable to start again until the app
  restarted; it now always picks up when you return.
- **Unplugging the headphones or speakers you're listening on no longer leaves playback
  silently "playing".** Horizon Radio now pauses and tells you the output is unavailable,
  "System default" follows Windows when the default device changes, and the output comes
  back once the device is plugged in again. While it's unavailable, audio no longer slips
  into the game when the picker says speakers.
- **Toggling shuffle while a mix plays no longer replays the song that's playing.** Turning
  shuffle on only reorders the songs that haven't played yet, turning it off carries on in
  order after the current song, and the queue's "Next From" list updates straight away.
- **Closing Horizon Radio gives your stations back to the game.** If the app closed or
  crashed while Forza Horizon 6 was running, the stations it was replacing went silent (and
  kept our song title after a crash); now they play the game's own music and titles again.
  When the app or the game restarts, the in-game title and the race volume duck pick up
  straight away instead of waiting for the next song.
- **The in-game radio no longer risks freezing or crashing when a station's audio goes
  away.** Horizon Radio now lets go of the game's audio cleanly when the radio turns off,
  never touches a channel the game has already destroyed, and stays off rather than
  half-hooking the game's audio when it can't find everything it needs (which could
  eventually freeze the game after an update).
- **A Spotify track no longer gets stuck in silence when its player stops.** If librespot
  exits, or no audio arrives for 15 seconds while playing, Horizon Radio moves on to the next
  track instead of sitting on a frozen progress bar. If you move playback to another Spotify
  device (your phone), it waits rather than taking playback back.
- **Remixes are found when the station credits a different artist.** Stations often credit a
  remix to the remixer while catalogs credit the original artist, or the other way round, and
  some credit someone else entirely: Vocaloid Radio played Sakuzyo's remix of 椎名もた's
  ササメク as "キョトンP - ササメク(sasameku can't be the word found in reayfla's inner world
  mix)" and nothing matched. A remix credit that names the station's artist as the remixer, or
  is long and specific enough to name just that one remix, now identifies the song (a
  misspelled word is fine), and VocaDB also looks among remixes when the plain name search is
  buried under same-named covers.
- **Songs credited to someone else, or titled in two languages, are found.** When only the
  title can be trusted (the station credited its uploader, "(∵)キョトンP - エレクトリック・ラブ")
  and VocaDB's same-titled entries are all one song's original and its covers and remixes, the
  song is that original. A title that carries its native name in parentheses ("Cherry Blossoms
  after the Sunshie ( 晴れのち桜)") is also tried by each name alone.
- **Songs credited only to their singing voice find the real song.** "Hatsune Miku - Melt"
  matched whatever Miku song had "melt" in its name ("Ice melt", a parody), because naming
  the voice counted as confirming the artist. The song is now the voice's most popular
  original with exactly that name (ryo's メルト, wowaka's ローリンガール), and it shows its
  real artist. A vocalist tag ("【初音ミク】Migratory [Clean Tears]") is tried after the
  producer, so it no longer picks another song with that word in it.
- **Fewer wrong songs and covers on internet radio.** When only the title can be trusted,
  VocaDB, which knows which version is the original, now identifies the song before the
  stores are asked: "千本桜" and "テトロドトキサイザ2号" got a cover's artist and art from
  iTunes. A one-word title has to be the song's whole name ("air" no longer matches "The
  Fresh Prince of Bel-Air"), and a one-word title with no artist ("ARiA") isn't guessed.
- **The original release is preferred over live, remix, and re-recorded ones** when the
  station didn't name a version, for the song and for its album art.
- **"Waiting for Spotify Connect…" isn't looked up or added to play history** as a song.
- **Local files show their tags again, and YouTube videos aren't skipped.** A file could
  play under the name taken from its file name ("02" as the artist of "02 - WATCH ME.flac",
  no cover), and a YouTube video could be skipped as "unresolved", when the queue was
  reading ahead at the moment the song started. Playback now waits for that read instead
  of starting from a half-prepared item.
- **A metadata service being briefly unavailable no longer hides a song for weeks.** When
  MusicBrainz throttled, a request timed out, Spotify wasn't signed in yet, or cover art
  failed to download, the song was cached as "not found" (or as having no art) for up to
  14 days. Failures are now retried the next time the song plays and show in the Console;
  one service timing out also no longer throws away the others' answers.
- **No more matches on a remix credit.** A title found only inside a credit like
  "(… Remix)" or "(feat. …)" no longer counts as a match (e.g. "KurageP" matched a remix
  credited to くらげP).
- **Radio: three-part stream titles and station tags.** A title like "和田たけあき -
  Chururira Chururira Daddadda! - KurageP [1MRp]" showed the artist as "KurageP [1MRp]":
  the station's per-song tag leaked into the artist, and the "Artist - Title - Credit"
  reading wasn't tried. Station tags are now dropped before the title is split, that
  reading is tried, and the optional title model's answer is tried right after the
  parser's best guess instead of last. A title ending in its own dash ("-S.S. Mix-") keeps it.
- **The in-game bridge no longer stays dormant when it starts too early.**
  FH6 decrypts its code at load, and the DLL used to scan for its FMOD and
  radio signatures exactly once. If that scan raced the unpack, audio, titles
  and game events stayed off until the game was restarted. Anything that
  didn't resolve is now retried on a backoff for ~10 minutes. The RadioState
  lookup (game events, station targeting) also no longer depends on the
  metadata lookup succeeding, and it now refuses ambiguous or out-of-module
  signature matches instead of trusting the first hit.

- **Changing stations no longer copies our title onto other stations.** Flicking
  past a station could leave the playing song's title on it (and on its
  neighbours). The mod now checks which station each title block belongs to (the
  game's own track id) before writing or restoring, so titles land only on our
  stations and appear immediately on tune-in. A station playing nothing of ours
  also stops showing a stale title from another station.
- **Internet-radio tracks no longer attach a wrong same-titled cover.** A romaji
  broadcast artist (e.g. "HachiojiP") could match an unrelated Japanese-named
  artist's same-titled song (e.g. くろくも's "KiLLER LADY") through the cross-script
  artist bridge, pulling in that cover's art. A lightweight kana romanizer now
  verifies a kana catalog artist actually sounds like the broadcast name before the
  bridge accepts the match.

## [0.6.0] - 2026-06-15

### Added

- **Spotify as a first-class content source** — our engine now drives librespot
  track-by-track via the Web API, so Spotify tracks, playlists, and albums play
  in the global **queue**, **Quick Play**, and **Mixes**, interleaved with
  YouTube/local, with full player-bar **transport and seek**. There are two
  Spotify entries by design: **Spotify Connect** (the zero-config cast receiver,
  no developer app needed) and **Spotify** (the driven, mixable source, which
  needs your own Client ID); they share one librespot device. End-of-track,
  pause/resume, and position are all derived from playback, so even a long pause
  can't end a track early.
- **Unified search** — a single search box in the title bar finds music across
  sources (**Spotify** and **YouTube**) and queues it. A debounced live dropdown
  and a full results page share the same results; each result has Add and Play, a
  per-row source picker, and per-source filter chips. Cross-source duplicates are
  merged conservatively (title + artist + duration proximity), so covers,
  remixes, and different-length versions stay separate.
- **Internet Radio** — a first-class, searchable, mixable source. Browse the
  radio-browser.info directory or paste a stream URL; live ICY song metadata
  updates the now-playing card per song, with the station logo as fallback art.
- **Richer now-playing metadata** — new keyless providers (**Apple/iTunes** and
  **VocaDB**, alongside MusicBrainz and optional Spotify), title-first match
  scoring that tolerates spacing/casing differences and rejects
  same-title/different-artist mismatches, and a candidate-generation →
  catalog-validation pipeline that resolves ambiguous (incl. CJK / fullwidth /
  reversed) stream titles without attaching wrong art to widely-covered songs.
  Album-art recovery for radio improved dramatically (a multi-hour session's
  ~22% art-misses dropped to near zero).
- **Optional local title model** — an opt-in, locally-run model (Qwen2.5-0.5B,
  downloaded from the Tools tab) that extracts artist/title from genuinely
  freeform stream titles, feeding the same catalog-validation loop so a wrong
  guess never reaches the UI. Run policy is configurable (Off / Escalate
  (default) / Always).
- **Metadata diagnostics** — an opt-in, replayable JSONL trace (About →
  Diagnostics) capturing each song's raw title, deterministic parse, model
  extraction, and every provider's scored results, so a metadata bug can be
  reproduced and attached to a report.

### Changed

- Installed tools (ffmpeg, yt-dlp) are now shared across sources through a single
  resolver instead of being configured per source. yt-dlp and Spotify pick up a
  mid-session install without a restart.
- Art-less metadata results are now retried once stale (by TTL or a logic-version
  bump), so a matching/parsing fix surfaces on an already-seen song instead of
  serving a permanent miss.
- Bumped `SpotifyAPI.Web` 7.2.1 → 7.4.2 for the new `/playlists/{id}/items`
  endpoint (Spotify removed the old playlist-tracks route in the March 2026 API
  migration).

### Fixed

- The first Spotify track queued after a fresh app start now reliably plays. A
  cold-start race let the play command reach Spotify's cloud before librespot's
  Connect session was ready, so the track hung silently until you skipped to the
  next; the initial play is now confirmed and re-issued until playback actually
  begins, and a genuinely failed play advances the queue instead of stalling.

## [0.5.0] - 2026-06-12

### Added

- **Queue** — a global play queue, shown as a toggleable right-hand sidebar
  (Spotify-style). Add one-off tracks with the **+** button or Quick Play; they
  play before the active mix, while the mix keeps refilling the queue's tail so
  the in-game radio never goes silent. Each row has play-now, reorder, and
  remove; double-click a row (or its thumbnail's play button) to jump to it.
- **Metadata pipeline** — track metadata is resolved through an ordered,
  multi-provider pipeline that fills in canonical titles, artists, and square
  album art. MusicBrainz is enabled by default (no credentials needed); Spotify
  is optional. Set the provider order and per-field overrides (e.g. always take
  album art from Spotify) in the redesigned **Metadata** tab.
- The queue and **Mixes** lists now show real titles and album art instead of
  raw URLs / filenames — resolved lazily ahead of play, in the background, and
  cached.

### Changed

- Metadata is normalized at the source: YouTube uses its canonical track/artist
  (with a heuristic "Artist – Title" parser as a fallback) rather than the
  channel name, and tagless local files parse their filename.
- Metadata enrichment is now on by default (previously an opt-in single
  provider) and combines the source with the configured providers per field.

## [0.4.0] - 2026-06-12

### Added

- **Mixes** — cross-source playlists. Build an ordered list of entries from
  different sources (a YouTube video or playlist, a local folder / M3U / file)
  and play them back-to-back as one continuous radio stream, with two-level
  shuffle (the entries, and the items within each collection). Create and manage
  them in the new **Mixes** tab and tune to one from the player bar. Mixes
  replace the old single-source profiles, which migrate automatically to
  one-entry mixes on first launch.
- The in-game station Horizon Radio replaces is now a global control in the
  player bar (moved out of the Sources tab), and a mix can optionally override
  it for the station it plays on.
- **Quick Play**: play a one-off URL, folder, M3U, or file from a content source
  without building a saved mix — pick the source from the player-bar picker (a
  quick-play dialog pops up) or use the Quick Play box on the Sources tab.

### Changed

- The Sources tab now configures a source's engine (tool paths, behavior) only —
  what to play moved to mixes. Content sources (Local, YouTube) play through
  mixes rather than being started directly; self-driven sources (Spotify
  Connect, the test tone) still start from the Sources tab.
- Profiles are replaced by mixes throughout: the Profiles tab is now the Mixes
  tab, and the profile-switch actions/hotkeys became mix-switch.

## [0.3.1] - 2026-06-11

### Changed

- `version.dll` is no longer bundled inside the single-file exe; it ships
  only as the loose copy the Mods tab deploys. The single-file host no
  longer extracts a redundant proxy copy into its temp dir on launch.
  (The loose copy can still be pulled into the process by the DLL search
  order, but the `DllMain` guard keeps it inert there.)

### Fixed

- The companion app no longer shows "Connected" when Forza isn't running.
  The bundled `version.dll` (a proxy DLL) was getting loaded into the app's
  own process by the normal DLL search order, where its `DllMain` started
  the IPC server — so the UI connected to itself. `version.dll` now stays
  inert unless it's loaded by the game, never inside our own UI.

## [0.3.0] - 2026-06-11

### Added

- About tab (entry point in the sidebar footer) showing the app version,
  channel, and commit, with links to the repo / releases / issues /
  license. Builds are now stamped with a `Channel` (stable / nightly /
  dev) the app reads at runtime.
- App update check: on launch the app checks GitHub for a newer build on
  its channel (stable → latest release; nightly → the rolling `nightly`
  prerelease) and surfaces it via a footer badge + a one-time toast.
  Currently "Update" opens the release page; an in-place updater follows.
  Dev builds never check. Offline/indeterminate stays silent.
- librespot is now installable on demand from the Tools tab, downloaded
  from a permanent, version-addressed tool blobstore (the `tools` GitHub
  release) and verified against a SHA-256 the app ships in an embedded
  `tools.manifest.json`. Pins live in one place (the manifest), read by
  both the app build and the librespot CI build.
- Nightly builds: a daily workflow publishes a rolling `nightly`
  prerelease of `main`. External tools resolve from the durable
  blobstore, so an installed nightly keeps working even after newer
  nightlies replace it. See `docs/tool-provisioning.md`.
- The Tools tab now checks installed tools for updates in the background
  on launch and flags stale ones. A sidebar badge and a one-time toast
  surface when an update is available; a "Check for updates" button
  re-runs the check on demand. yt-dlp and ffmpeg compare against their
  upstream's current build; librespot compares against the app's pinned
  build — never against upstream (that drift is the maintainer's job).
  The check is hash-based and failure-silent: offline or otherwise
  indeterminate tools are left unflagged rather than shown as stale.
- Starting a source whose required tools (yt-dlp, ffmpeg, librespot)
  aren't installed now shows a clear "Tool required" prompt naming the
  missing tool(s) and pointing to the Tools tab, instead of a generic
  failure. The check runs before the current source is stopped, so a
  misconfigured switch no longer interrupts what's already playing.

### Changed

- C++ side now builds with CMake (`CMakeLists.txt` + `CMakePresets.json`)
  instead of MSBuild `.vcxproj` files. The `windows-x64` preset uses
  Ninja Multi-Config so a future clang-cl / cross-compiler swap is a
  compiler change, not a generator change. `HorizonRadio.slnx` is
  removed; the C# UI continues to build via `HorizonRadio.UI.slnx`.
- Dropped vcpkg entirely. External C++ deps (currently just doctest)
  are now fetched via CMake's `FetchContent` at configure time, so
  the `vcpkg/` submodule, `vcpkg.json`, and the bootstrap step in CI
  are all gone.
- `version.dll` can now be cross-compiled from Linux or macOS using
  clang + mingw-w64 (see the `linux-cross-x64` / `macos-cross-x64`
  presets and `cmake/toolchain-clang-mingw.cmake`). The 17 system
  `version.dll` export forwarders moved out of `dllmain.cpp`'s
  `#pragma comment(linker)` block and into plain dllexport
  trampolines in `src/version_proxy.cpp`, which both MSVC and
  clang+MinGW accept.
- librespot is no longer compiled in-tree on every C++ build. A
  dedicated GitHub Actions composite action
  (`.github/actions/build-librespot`) builds it once per pinned rev,
  caches the result, and publishes it to the permanent, version-
  addressed `tools` blobstore. It is no longer bundled in the app zip
  or attached per-release — the app fetches it on demand from the
  Tools tab, the same as yt-dlp and ffmpeg.

## [0.2.0] - 2026-06-01

### Added

- Shuffle toggle on the Now Playing page for local-file and YouTube playback.
  The choice is remembered across runs; toggling mid-playback keeps the current
  track and shuffles the rest, and a shuffled order reshuffles when it wraps.
- Live per-station targeting: replace the radio on a chosen station only, or any
  station when left empty.
- Now-playing title/artist injection into the Forza Horizon 6 radio HUD.
- Console tab in the UI, an event system with game-state detection, and grouped
  event categories.
- Contribution guardrails: a `PR title` GitHub workflow that enforces
  Conventional Commit PR titles, plus a contributing workflow section in
  `AGENTS.md`.

### Changed

- Relicensed from MIT to GPL-3.0-or-later.
- Reorganized the sidebar, regrouped events by category, and restyled the mod
  banner.

### Fixed

- librespot autoplay is forced on so Spotify keeps playing past the end of the
  queue.
- Spotify title-timing now lines up with the audio.
- Plugged a metadata string leak when restoring the game's original HUD text.

## [0.1.1] - 2026-05-28

### Changed

- Bundle the .NET assemblies into a single executable for cleaner releases.

## [0.1.0] - 2026-05-28

### Added

- Initial release of Horizon Radio.

[Unreleased]: https://github.com/dhkatz/HorizonRadio/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/dhkatz/HorizonRadio/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/dhkatz/HorizonRadio/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/dhkatz/HorizonRadio/compare/v0.3.1...v0.4.0
[0.3.1]: https://github.com/dhkatz/HorizonRadio/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/dhkatz/HorizonRadio/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/dhkatz/HorizonRadio/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/dhkatz/HorizonRadio/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/dhkatz/HorizonRadio/releases/tag/v0.1.0

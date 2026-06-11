# Tool provisioning, build channels, and the tool blobstore

How Horizon Radio ships its external tools (librespot, yt-dlp, ffmpeg)
across release, nightly, and dev builds — and why it's built this way.

## The problem

The app drives external binaries it doesn't contain:

- **librespot** — Spotify Connect client. **We build it** from a pinned
  upstream rev (a dev-branch SHA, because v0.8.0 has a Connect
  state-machine bug). The app build is coupled to *that rev*.
- **yt-dlp** — YouTube resolver. Third-party; **must track latest**
  because YouTube breaks it constantly. Pinning it to an app build
  would actively rot.
- **ffmpeg** — decoder. Third-party; tracks its upstream's current
  build.

We also wanted dev/nightly builds where "the app I'm running knows which
tools it needs." The naive approach — "app built from commit `abcdef`
pulls artifacts from CI run `abcdef`" — does not work:

- **GitHub Actions artifacts are not anonymously downloadable** (the
  download API needs an `actions:read` token; end users have none) and
  **expire** (90 days default).
- Release assets, by contrast, are anonymous, CDN-served, and permanent.

## The key idea: address tools by *their* version, not by the app build

The app↔tool coupling lives in the **source tree** (the pinned librespot
rev), not in a CI run. Many app commits share one librespot pin. So we
address each built tool by its own identity — `(tool, version, os, arch)`
— and store it permanently. The app carries an **embedded manifest**
that says "I need librespot rev X (sha256 Y) from URL Z". Z is keyed by
the rev, so release, nightly, and dev builds that pin the same rev all
resolve to the *same durable URL*. CI artifacts never enter distribution.

This dissolves the ephemerality/auth problem: nothing we distribute
relies on Actions artifacts.

## The tool blobstore

A single dedicated GitHub Release, tag **`tools`**, used as a permanent,
append-only blob store. It is separate from the `vA.B.C` app releases so
the release list stays clean, and it holds **multiple per-tool assets**
(one release, many assets — not a release per tool/version):

```
https://github.com/dhkatz/HorizonRadio/releases/download/tools/librespot-<rev12>-x86_64-pc-windows-msvc.exe
```

`<rev12>` is the first 12 hex chars of the librespot rev (collision-safe,
shorter than the full 40). Publishing is **idempotent and append-only**:
CI checks whether the asset already exists and skips the build if so;
existing assets are never overwritten or deleted. Bumping a pin builds
once; every later app build reuses the asset.

Only tools **we build** live here. yt-dlp/ffmpeg come from their own
upstream `releases/latest/download/...` URLs (anonymous CDN already).

> Why not object storage (R2/S3)? A GitHub Release covers permanence +
> anonymous CDN + zero new infra. Revisit only if asset count/size
> outgrows releases, which won't happen for a handful of exes.

## The manifest (embedded, single source of truth)

`tools.manifest.json` at the repo root is the source of truth for pins
and is embedded into the app at build time (read offline; no API call to
"find itself"). Schema (v1):

```jsonc
{
  "schemaVersion": 1,
  "tools": {
    "librespot": {
      "policy": "pinned",
      "version": "<full git rev>",
      "platforms": {
        "win-x64": {
          "url":    "…/releases/download/tools/librespot-<rev12>-x86_64-pc-windows-msvc.exe",
          "sha256": "<hex>"        // the app's OWN expectation
        }
      }
    },
    "yt-dlp": { "policy": "latest" },   // resolves upstream latest at install
    "ffmpeg": { "policy": "latest" }
  }
}
```

- `policy: pinned` → install from `url`, **verify against `sha256`**.
  This checks integrity against *what we shipped*, not the source's
  self-reported sums — closing the corrupt/poisoned-download hole.
- `policy: latest` → the installer resolves the upstream's latest
  (existing yt-dlp/ffmpeg behavior); the manifest just records intent.
- Keyed by `(os, arch)` (`win-x64` today) so cross-platform app builds
  don't force a schema change later.

The librespot rev is read from this manifest by **both** the app build
*and* the librespot CI action — one pin, no drift with the old
hardcoded `$rev` in the action.

## Build channels

Tools are channel-agnostic (version-addressed above). `version.dll` is
**bundled inside the app zip** (tight IPC/FMOD-offset coupling — never
fetched). So a "channel" is only: how the user gets the zip, what the
app stamps about itself, and where it checks for updates.

| Channel  | App distribution                                              | Stamped (`Version` / `Channel`)        | Update check |
|----------|--------------------------------------------------------------|----------------------------------------|--------------|
| Release  | `publish.yml` on tag `vX.Y.Z` → `HorizonRadio-vX.Y.Z.zip`    | `X.Y.Z` / `stable`                     | `releases/latest` (no prereleases), SemVer |
| Nightly  | **daily** scheduled build → **rolling `nightly` prerelease** | `X.Y.Z-nightly.<date>+<sha>` / `nightly` | `nightly` release date/sha vs embedded |
| Dev      | built locally (or a PR build a maintainer pulls)             | `0.0.0-dev` / `dev`                    | none |

**Decisions made:**

- **Daily nightly**, not per-push: avoids burning builds on doc-only
  commits; predictable. Skips the run if `main` hasn't moved since the
  last nightly.
- **Rolling `nightly`** (recreated each run), not per-commit. The
  durable blobstore means old nightlies keep working: if you're running
  a nightly you already have its zip + bundled `version.dll`, and its
  tools resolve from permanent URLs. Overwriting only drops the ability
  to *re-download an old nightly you never installed* — acceptable.
- **Embedded-only manifest** for now. Can't re-pin without an app
  rebuild; fine because yt-dlp self-resolves latest and librespot is
  per-build. A runtime per-channel manifest can come later if we want to
  bump a stable pin without shipping an app update.

## Durability summary

- librespot → permanent blobstore asset (keyed by rev).
- ffmpeg → upstream pinned/stable release URL.
- yt-dlp → upstream latest (intentionally rolling).
- Local install is version-keyed and kept once fetched, so **only the
  first fetch is exposed to a 404**; an installed tool keeps working
  indefinitely.

When a fetch *does* fail because a build is ancient, surface a specific
message ("this build's tools are no longer available — update") rather
than a raw download error.

## Bumping the librespot pin

1. Edit `tools.manifest.json`: set `librespot.version` to the new rev
   and `…/win-x64.url` to the matching `<rev12>` asset name. Leave/clear
   `sha256`.
2. Push. The `publish-tools` workflow builds the rev, uploads it to the
   `tools` release (idempotent), and prints the SHA-256.
3. Paste the SHA into `tools.manifest.json`'s `sha256` and push. CI
   re-verifies the built artifact matches the manifest hash and fails on
   drift.

(Until a `sha256` is filled, `LibrespotInstaller` downloads and trusts
the blobstore asset with a logged warning — same lenient stance the
yt-dlp/ffmpeg installers take when an upstream sums file is missing.)

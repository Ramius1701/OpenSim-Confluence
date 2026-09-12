# OpenSim-Confluence

Confluence is an independent OpenSimulator fork — in the same vein as
WhiteCore-Dev, Tranquillity, or Homeworldz: a distinct project with its
own web/admin platform, native economy and search services, and
moderation stack, not a thin patch set on top of something else. It
began from OpenSim Continuum's codebase and has continued to absorb
selected grid, identity, scripting, environment, simulator, and
reliability enhancements cherry-picked and hand-ported from the wider
OpenSim ecosystem (Gunthar's fork, Tranquillity, Mobius, and
WhiteCore-Dev). Official OpenSimulator remains the authoritative
upstream baseline.

**Where Confluence and Continuum parted ways:** they share the same
starting lineage, but the two are now independent, parallel efforts.
Continuum's own README describes its web/admin portal work as
"intentionally deferred until the simulator, Robust services, and
addons are complete." Confluence took the opposite bet — it built that
portal, and a full native economy and search layer to go with it,
directly into the fork rather than leaving it to a separate PHP site or
a later phase.

## Project status

| Item | Status |
|---|---|
| GitHub home | [Ramius1701/OpenSim-Confluence](https://github.com/Ramius1701/OpenSim-Confluence) |
| Upstream baseline | `origin/master` (`opensim/opensim`), merged in as of the latest commit |
| Active integration branch | `merge-experiment` (also the repo's default branch) |
| Windows build | Successful — full solution build verified clean |
| Web/Admin UI | Live-verified against a running Robust instance (real HTTP sessions, admin actions, database writes) |
| In-world/viewer testing | Live-verified with a real viewer (Firestorm): login, region crossing, weather, and a full currency/land-purchase transaction |

**On "tested" vs. "compiled":** these are two different claims. The
Web/Admin UI and basic in-world presence (login, region crossing,
weather, a real purchase transaction) are live-verified against a real
viewer and a running grid, not just a clean build. Live verification of
individual LSL/OSSL functions, Experience Tools behavior, and specific
physics/environment tuning is narrower than that — general region
stability doesn't mean every scripting function has been exercised
in-world. `PROJECT_LOG.md` notes verification status for individual
pieces where it matters.

## Project goals

- Stay close enough to official OpenSimulator to accept continuing upstream work.
- Preserve useful enhancements that are difficult to maintain as loose patches.
- Keep optional functionality in `addon-modules` whenever practical.
- Avoid grid-specific hardcoding.
- Support standalone and Robust/grid deployments.
- Retain Windows build and deployment support.
- Provide configuration examples without silently enabling services.
- When porting from another fork, verify with a real build rather than
  trusting a commit message or a docs page.

## Including features from other projects

Confluence's goal is a full, immersive grid platform with everything a
grid owner might reasonably need built in, not scattered across
addon-modules and third-party services grid owners have to discover and
assemble themselves. If another repository — a fork, an addon, a
standalone tool — has a fix, enhancement, or feature that looks like it
belongs here, open an issue or discussion on this repository first for
assessment rather than assuming it fits. Every feature already ported in
from Gunthar/Tranquillity/Mobius/WhiteCore-Dev/Halcyon/Homeworldz/
opensim-lickx (see "Attribution and support" below) went through that
same real review, not a rubber stamp — confirmed present in this
codebase, verified against a real build, and checked against how actual
Second Life/Tranquillity/Mobius/WhiteCore-Dev do it before being ported,
not invented from a description.

Every feature that can reasonably be made optional is: grid owners
choose what to enable through their own `.ini` configuration rather than
taking on the runtime cost or behavior of something they don't want
running. Giving grid owners that choice is a design requirement here,
not an afterthought bolted on later.

## Features

Confluence includes a native Web/Admin UI, in-world economy and search
services, a full moderation and access-control stack, expanded LSL/OSSL
scripting, and a range of physics/environment/reliability improvements
over stock OpenSimulator. See [`FEATURES.md`](FEATURES.md) for the full,
categorized list.

A native, viewer-integrated DirectDelivery Marketplace is also built (see
[`MARKETPLACE.md`](MARKETPLACE.md) for setup and usage) — not yet listed
in `FEATURES.md` since it's still working through live verification; see
`ROADMAP.md`.

## Roadmap

For what's planned, what's deliberately out of scope, and current known
limitations, see [`ROADMAP.md`](ROADMAP.md).

## Building

### Requirements

- .NET 8 SDK or a newer SDK capable of targeting .NET 8
- Visual Studio 2022 or later is optional on Windows

### Windows

```bat
runprebuild.bat
dotnet build OpenSim.sln --configuration Release
```

### Linux or macOS

```bash
./runprebuild.sh
dotnet build OpenSim.sln --configuration Release
```

See `BUILDING.md` for the official base requirements.

## Configuration

Confluence does not install live configuration automatically.

Review:

- `bin/OpenSim.ini.example`
- `bin/Robust.ini.example`
- `bin/Robust.HG.ini.example`
- `bin/config-include/GridCommon.ini.example`
- `bin/config-include/storage/SQLiteRobust.ini`
- module-specific `.ini.example` files under `addon-modules`

Optional modules should remain disabled until dependencies, database
schema, service endpoints, credentials, and runtime behavior have been
validated.

## Repository model

| Reference | Purpose |
|---|---|
| `merge-experiment` | Active integration branch and repo default — everything described above lives here |
| `master` | Stale; predates this round of work |
| `origin/master` | Official OpenSimulator development branch, merged into `merge-experiment` as of the latest commit |

Feature work happens in short-lived isolated git worktrees/branches
(build-verified before merging), merged into `merge-experiment`, then
cleaned up. This keeps history readable and avoids leaving
half-finished work on the integration branch.

## Deployment

Confluence's regions run one-process-per-region rather than the stock
single shared `OpenSim.exe`, so any individual region can be started,
stopped, or restarted (to pick up a new build) without ever having to
take down the rest of the grid — a real, live-tested requirement, not
a theoretical one. Setting this up is a normal deploy step, not a
separate tool or wizard:

1. Build Confluence, then copy `Robust.exe`, `OpenSim.exe`, every built
   DLL, and a real, configured `OpenSim.ini` (DB credentials, grid
   name, etc. — the same one-time setup any OpenSim install needs)
   into a single folder, e.g. `C:\opensim`. This folder is both where
   Robust itself runs from and the source every region's own binaries
   get synced from.
2. In `Robust.HG.ini`'s `[StoreService]` section, set
   `RegionOrderGridRoot` to that same folder, and
   `RegionOrderTemplateIniPath` to the `OpenSim.ini` you just put there
   — it only ever needs to be read as a template (cloned and have a
   handful of region-specific keys rewritten), never actually run
   itself, so there's no chicken-and-egg step where a region has to
   already exist before this works.
3. Run **only `Robust.exe`**. No region process needs to be started by
   hand.
4. Log into the web UI as a grid admin and use **Create Region**
   (`/admin/store/create-region`) to provision your first region — any
   type, any size, for any resident, with no purchase involved. This
   clones the template from step 2 into its own
   `Simulators\<name>\bin\` copy and starts it automatically. Every
   region after that can come the same way, or be sold through the
   Store to residents directly.

Casperia Prime is the reference deployment built this way — a real,
live, public grid with real residents and currency, used throughout
this project's development as the actual proving ground for every
feature described in this README, not just a demo.

## Documentation

| File | Purpose |
|---|---|
| `FEATURES.md` | What Confluence has, organized by area |
| `ROADMAP.md` | What's planned, deferred, or a known limitation |
| `WEBUI_PARITY_CHECKLIST.md` | Working audit tracking the Web UI's page-by-page structural parity against WhiteCore-Dev |
| `INWORLD_COMMANDS.md` | Every in-world chat command available to avatars/estate managers |
| `BUILDING.md` | Official base build requirements |
| `PROJECT_LOG.md` | Full narrative development history — every change, why, and how it was verified |

## Attribution and support

Confluence retains the OpenSimulator license and source history, and
started from OpenSim Continuum's consolidation work. Portable
improvements have been cherry-picked and, where architecturally
incompatible with a straight cherry-pick, hand-ported from:

- [Gunthar's OpenSim fork](https://github.com/GuntharDeNiro/opensim)
- [Tranquillity](https://github.com/OpenSim-NGC/OpenSim-Tranquillity)
- [Mobius](https://github.com/Mobius-Team/Mobius)
- [WhiteCore-Dev](https://github.com/WhiteCoreSim/WhiteCore-Dev) — also
  the primary reference for the native Web/Admin UI's page structure
  and admin-feature set
- opensim-lickx — origin of Confluence's MoneyServer and OpenSimSearch
  modules; its original GitHub repository has since been deleted, so
  the only remaining copy is archived locally
- [Halcyon/InWorldz](https://github.com/HalcyonGrid/halcyon) and
  [Homeworldz](https://github.com/homeworldz/server) — audited as a
  preservation effort for design ideas and code from projects that have
  fallen by the wayside
- OpenSim-Grid-Interface (ManfredAabye's `oswebinterface` fork, a
  separate PHP project — not Confluence's own code, not ported from,
  and not distributed with this repository) — referenced for the
  native Web UI's page structure where WhiteCore-Dev had no equivalent
  page. It plays the same role as this repo's `addon-modules`: an
  optional alternative to the built-in Web UI for grid owners who'd
  rather not run it, maintained independently and under separate,
  ongoing development — not shipped, bundled, or included with
  Confluence

Historical source provenance remains available in Git history.

Report Confluence-specific problems in this repository. Problems
reproducible on an unmodified official OpenSimulator build should be
reported to the official OpenSimulator project.

## License

Confluence is distributed under the same BSD-style license as
OpenSimulator. See `LICENSE.txt`.

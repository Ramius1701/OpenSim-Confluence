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

Confluence supports running a live grid and a separate beta/test grid
side by side — the same split Second Life itself uses (its Agni main
grid and Aditi beta grid). A test grid running the same codebase and a
cloned copy of live data lets new builds, config changes, and content
get verified against something real before ever touching the live
grid, without any risk to live residents or their data.

Casperia, the reference deployment built on Confluence, follows exactly
this pattern:

| Grid | Role |
|---|---|
| Casperia Prime | The live, public grid. Real residents, real currency, real data. |
| Casperia-Dev | A separate beta/test grid — its own domain and port range, cloned from Casperia Prime's data — for verifying changes before they reach the live grid. |

Each grid runs its own Robust and region processes against its own
database, so both can run at the same time without colliding. This
isn't a special mode Confluence has to be configured into — it's just
two independent standalone/grid deployments of the same software,
pointed at different data.

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
  separate PHP project — not Confluence's own code, not ported from)
  — referenced for the native Web UI's page structure where
  WhiteCore-Dev had no equivalent page. Still included and bundled
  alongside Confluence today as a swappable alternative to the native
  Web UI, not superseded or merged in — see `FEATURES.md`'s Web &
  Admin UI section

Historical source provenance remains available in Git history.

Report Confluence-specific problems in this repository. Problems
reproducible on an unmodified official OpenSimulator build should be
reported to the official OpenSimulator project.

## License

Confluence is distributed under the same BSD-style license as
OpenSimulator. See `LICENSE.txt`.

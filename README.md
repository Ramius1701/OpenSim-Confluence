# OpenSim-Confluence

Confluence is a maintained downstream fork of the official
[OpenSimulator](https://github.com/opensim/opensim) development branch.
It started from OpenSim Continuum's foundation and has since absorbed
selected grid, identity, scripting, environment, simulator, web, economy,
and reliability enhancements cherry-picked and hand-ported from several
other OpenSim forks (Gunthar's fork, Tranquillity, Mobius, and
WhiteCore-Dev). Official OpenSimulator remains the authoritative upstream
baseline.

## Project status

| Item | Status |
|---|---|
| Upstream baseline | `origin/master` (`opensim/opensim`) |
| Active integration branch | `merge-experiment` — this is where all current work lives; `master` is stale and predates this round of work |
| Windows build | Successful — full solution build verified clean (0 errors) as of the latest commit |
| GitHub Actions | `.github/workflows/msbuildnet.yml` present; not yet exercised on a pushed repo |

The complete solution builds successfully, including OpenSim, Robust,
MoneyServer, and the included add-on modules, verified repeatedly
throughout development via isolated git worktrees, targeted project
builds, and full-solution builds.

**A successful compile does not mean everything has been tested in-world.**
Almost everything below has been build-verified but not yet exercised
against a running region with a viewer. See "Progress and roadmap" below.

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

## Included enhancements

### Display Names and identity

- Viewer-compatible Display Names for local users.
- Display Name CAPS and viewer protocol handling.
- Display Name storage and account-service integration, including a fix
  so display names survive region restarts and cross-sim hops.
- Hypergrid Display Name lookup and federation.
- Single-name and `username` login handling.
- Terms-of-service acceptance during login.

**Not yet implemented** (referenced in older project docs, verified absent
by direct code search): RSA-key login authentication, and
`InternalPort = MATCHING` region configuration. Both are real Mobius
features not yet ported. See "Progress and roadmap."

### Abuse Reports

- Viewer Abuse Reports CAPS.
- Local and remote service connectors.
- Robust handlers.
- MySQL, PostgreSQL, and SQLite storage and migrations (PGSQL/SQLite
  parity added on top of the original MySQL-only implementation).
- Region-side submission support.

### Parcel, terrain, inventory, and object control

- `osTriggerSoundAtPos`
- Parcel auto-return access through `PARCEL_DETAILS_OBJECT_RETURN` and
  `PARCEL_DETAILS_TELEPORT_ROUTING`
- `osReturnObjects` / `osReturnObject` for scripted parcel auto-return
- In-world terrain console commands
- Script-controlled terrain textures and height ranges
- Sculpt-map animation support (`llSetSculptAnim`)
- Hardware/IP/MAC banning, with PGSQL/SQLite parity

**Not yet implemented**: `llSetAgentRot`, `llReturnObjectsByID`/
`llReturnObjectsByOwner` (LSL versions — the OSSL equivalents above are
implemented), `llSetGroundTexture`, `llGiveAgentInventory`, `llMatchGroup`,
`llSetParcelForSale`, `llGetAttachedListFiltered`, `llFindNotecardTextSync`.
Deliberately deferred, not forgotten — see roadmap.

### Expanded LSL and OSSL compatibility

- `llSignRSA` / `llVerifyRSA` — RSA signing/verification over PEM keys.
- `llGetRegionTimeOfDay`, `llTransferOwnership`, `llSitOnLink`.
- `llSetLinkRenderMaterial`, `llSetLinkGLTFOverrides` — a full PBR
  material override read/write pipeline (glTF JSON extraction, compact
  key-value encoding, KHR texture transforms). The `PRIM_GLTF_*`
  primitive-parameter helpers exist in the codebase but are not yet wired
  into `llSetPrimitiveParams`/`llSetLinkPrimitiveParamsFast` — dormant,
  not dispatched.
- `llIsExperienceTrusted`, `llGetExperiencePermissions`,
  `llExperienceCanAutoGrant`, `llGetExperienceKeyValueStoreStats` —
  Experience introspection queries backed by Confluence's own Experience
  Tools system (see below).
- `osPerlinNoise2D`.

A significant class of bug was found and fixed while building this out:
several LSL/OSSL functions (the entire Experience function family, plus
`osPerlinNoise2D`) were fully implemented and declared in their interface
but never wired into the runtime dispatch layer that compiled scripts
actually call through — meaning they were unreachable from any script
despite the backend working correctly. All known instances of this bug
have been found and fixed, verified by a mechanical sweep of every
interface declaration against its dispatch stub.

### Combat2 scripting

- `llDamage`, `llAdjustDamage`, `llDetectedDamage`, `llDetectedRezzer`.
- Persisted object-health support (via prim dynamic attributes).
- `on_damage`, `final_damage`, `on_death` events.
- Damage adjustment runs through a short async transaction window so an
  `on_damage` handler can call `llAdjustDamage` to override the amount
  before `final_damage`/`on_death` fire.

### EEP environment scripting

- `llGetEnvironment`, `llSetEnvironment`, `llReplaceEnvironment` —
  region- and parcel-level, gated by standard OpenSim estate/parcel
  permissions (`CanIssueEstateCommand` / `CanEditParcelProperties`).
- `llSetAgentEnvironment`, `llReplaceAgentEnvironment` — per-agent,
  gated by Confluence's own Experience Tools permission system.
- Region and parcel sky/water access.

### Pathfinding

- `llCreateCharacter`, `llUpdateCharacter`, `llDeleteCharacter`,
  `llExecCharacterCmd`, `llNavigateTo`, `llWanderWithin`,
  `llPatrolPoints`, `llPursue`, `llEvade`, `llFleeFrom`,
  `llGetStaticPath`, `llGetClosestNavPoint`.

The implementation is a self-contained region-local A* engine: a baked
navmesh sampled from terrain height (cached, rebaked on terrain/size
change), obstacle avoidance against other objects and optionally
avatars, and path-following that reuses the existing `KeyframeMotion`
system rather than a new movement engine. It is not a physics-engine-native
or Linden-proprietary navmesh service.

### Experience Tools

Confluence has its own Experience Tools implementation — not Second Life's
full Experience service, and not the smaller "Experience-Lite" design
used by some sibling forks, but a real, backend-persisted system:

- Residents can create their own Experiences from the viewer (matching
  real SL's protocol, verified against the SL viewer's own open-source
  `llfloaterexperiences.cpp`), with a configurable one-time creation fee
  (paid through whichever `IMoneyModule` is active — Gloebit or
  MoneyServer) and a configurable per-resident cap.
- A real, backend-persisted key-value store
  (`llCreateKeyValue`/`llReadKeyValue`/`llUpdateKeyValue`/
  `llDeleteKeyValue`/`llKeyCountKeyValue`/`llKeysKeyValue`/
  `llDataSizeKeyValue`), not an in-memory dictionary.
- Permission grants and trust checks
  (`llRequestExperiencePermissions`/`llAgentInExperience`/
  `llGetExperienceDetails`/`llGetExperienceErrorMessage`), plus the
  introspection queries listed above.
- `llOpenFloater` is not implemented at all (not even as a stub) —
  OpenSimulator has no viewer-hosted floater service to back it.

### Sit targets and avatar animation

- Enforcement of scripted-only sit targets.
- Storage and lookup for LSL sit flags.
- Configurable male and female walk-animation overrides.
- Movement-animation resend protection.

### Region crossing and attachment reliability

- Configurable transfer and cleanup timeouts.
- Optional preservation of crossing velocity.
- Reduced attachment detach/reattach flashing.
- Duplicate and failed attachment cleanup.
- Coordinated queued attachment-script restarts.

### Background map-tile generation

- Background rendering, non-blocking region startup and grid registration.
- Exact-geometry rendering (mesh/sculpt, alpha texture cards, water depth
  shading) rather than placeholder/fallback boxes.

### Weather

`OpenSimWeather` (rain, snow, storms, lightning, thunder, wind, clouds)
received an end-to-end fix pass: precipitation no longer auto-deletes
after ~60 seconds, lightning no longer strikes underground, all four
weather profiles were retuned against real `RegionLightShareData`
defaults (the "Sunny" profile was badly washed out due to an
undocumented ×3 ambient-conversion factor in the EEP bridge), sky
changes now apply before precipitation emitters, and two real bugs that
made the sun/moon appear to freeze after using weather commands are
fixed. Still reasonably described as experimental — not scientifically
simulated weather.

### Physics realism (ubODE)

Extensive tuning pass: buoyant floating-prim water physics, boat wave
response, rubber bounce and material density, rolling resistance,
avatar/object contact smoothing, and friendly avatar social physics.

## Included add-on modules

All modules are under `addon-modules`. They are generated into the
solution but are not necessarily enabled by default.

- **Gloebit** — optional Gloebit economy integration.
- **GroupAutoInvite** — configurable automatic group invitations.
- **HoloPhysicsGuard** — reduces idle physics load when regions are empty.
- **OpenSimMarketplace** — portable Direct Delivery marketplace system.
- **OpenSimMutelist** — external mute-list service integration.
- **OpenSimSearch** — external viewer search integration.
- **OpenSimTide** — configurable tide and water-level simulation.
- **OpenSimWeather** — rain, snow, storms, lightning, thunder, wind,
  clouds; see "Weather" above.
- **RegionCurrency** — web front end for an existing `IMoneyModule`
  (avatar wallet, balance/statement, PayPal token purchases, admin
  dashboard).
- **RegionWeb** — per-region web pages, protected estate administration,
  an in-world LSL/OSSL compatibility reference (auto-discovered from the
  script API plus hand-written notes), and its own separate currency/wallet
  portal (see note below).

**Known duplication:** RegionCurrency and RegionWeb's `/currency` portal
are two independent PayPal/wallet implementations that both exist in the
tree. This wasn't a deliberate architecture choice — RegionCurrency was
split out of RegionWeb by an earlier AI-assisted session, not by design —
and the two haven't been reconciled. RegionWeb's PayPal integration ships
present but unconfigured/dormant (gated by its own `IsPayPalConfigured()`
check), reserved for future use rather than active.

Detailed Marketplace documentation is located at:

```text
addon-modules/OpenSimMarketplace/README.md
```

For every in-world chat command available to avatars/estate managers across
the whole repo (not just add-ons), see [`INWORLD_COMMANDS.md`](INWORLD_COMMANDS.md).

## MoneyServer enhancements

The included MoneyServer integration provides:

- MoneyServer, region currency module, and MySQL data wrapper.
- Viewer currency purchases without an external `currency.php` helper.
- Configurable daily, weekly, and monthly purchase limits.
- Idempotent confirmation UUID handling.
- Retained banker, transfer, group, email-lock, object-payment,
  upload-charge, and land-sale controls.

Real bugs fixed on top of the original implementation:

- `DTLNSLMoneyModule` was activating as `IMoneyModule` even when it
  wasn't the selected economy module, hijacking Gloebit's role.
- A Nini config case-sensitivity mismatch (`EconomyModule` vs.
  `economymodule`) meant different modules reading the same conceptual
  key could silently disagree.
- `CurrencyGroupOnly`'s group-restriction check rejected purchases
  instead of skipping the check when `CurrencyGroupID` was the
  placeholder zero UUID.
- `MoneyServer.dll.config`'s console appender had a duplicated
  `%newline`, causing blank console lines.
- MoneyServer can now start a basic `CommandConsole` (`[Startup] console
  = "basic"`) to run headless, matching Robust/OpenSim.

The repository does not replace a live `bin/MoneyServer.ini`. Review the
included examples before production use.

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

## Progress and roadmap

Full narrative history of what's been done, why, and which files were
touched lives in [`PROJECT_LOG.md`](PROJECT_LOG.md). A categorized
feature comparison against upstream OpenSim and the OpenSim-Continuum
README this project started from lives in
[`FEATURES_VS_MASTER.md`](FEATURES_VS_MASTER.md). Keep both updated as
work continues — don't let them go stale.

**Known gaps, roughly in priority order:**

- Nothing in this round of work has been tested in-world yet — only
  compiled. That's the immediate next step.
- RSA-key login authentication and `InternalPort = MATCHING` (both real
  Mobius features) are not implemented.
- The misc LSL/OSSL functions listed as "not yet implemented" above.
- `PRIM_GLTF_*` primitive-parameter dispatch is dormant — the backing
  code exists but nothing calls it from `llSetPrimitiveParams`/
  `llSetLinkPrimitiveParamsFast`.
- RegionCurrency vs. RegionWeb's currency portal duplication is
  unreconciled.
- The User Alias service (ported from Tranquillity), the Land Auction
  and Team Combat modules (ported from WhiteCore-Dev), the in-world
  terrain console commands (ported from Mobius), and `osGetAgentViewer`
  (ported from opensim-lickx) haven't been tested in-world yet —
  console commands and connector wiring are unexercised against a live
  grid.
- Halcyon/InWorldz's own Bot/NPC framework (from a preservation-focused
  audit) is superseded, not pursued — Tranquillity's `develop` branch
  shipped its own open, cleanly-licensed bot/NPC framework since, and
  that's what got ported instead (see below). The smaller Halcyon
  candidates from that same audit (a handful of missing LSL functions,
  a sit-target accuracy fix) are still open; see FEATURES_VS_MASTER.md.
- The Bot/NPC management framework (`IBotManager`/`BotManager`/
  `BotPersistenceManager`, ported from Tranquillity) is infrastructure
  only — no script can reach it yet. Tranquillity's ~50 `bot*` OSSL
  functions exist only in their Phlox script engine; wiring an
  equivalent into Confluence's own `OSSL_Api.cs`/`IOSSL_Api.cs`/
  `OSSL_Stub.cs` so YEngine scripts can actually call it is deferred as
  its own effort, comparable in size to the module port itself.
- Grid-level control-panel features (per-region Hypergrid open/close
  toggle, on-demand maptile regeneration, OAR/IAR backup workflows, and
  general admin coverage) live in a separate companion project
  (`OpenSim-Grid-Interface`), not this repository, and have their own
  open items there.
- Two Gunthar HG-identity commits (an account-ServiceURLs-repair console
  command and a standalone-HG-login HomeURI repair, both touching
  `LLLoginService.cs`) were deliberately deferred from the 2026-08-08
  re-audit/port round as warranting dedicated review rather than batch
  inclusion — not yet started.
- Tranquillity's "Phlox" LSL/SLua script engine (~98,000 lines) is
  audited but NOT ported: it turned out to be a resurrection of
  InWorldz/Halcyon's own closed-source Phlox engine, now appearing as
  source with no LICENSE file and no explained chain of custody. Not
  shelved outright — the user is raising a provenance question with
  OpenSim-NGC before any engineering investment is considered. See
  PROJECT_LOG.md for the full writeup and for the complete list of what
  the 2026-08-08 rounds did port (11 batches, `merge-experiment` at
  `b508644e43`).

## Repository model

| Reference | Purpose |
|---|---|
| `merge-experiment` | Active integration branch — everything described above lives here |
| `master` | Stale; predates this round of work |
| `origin/master` | Official OpenSimulator development branch |

Feature work happens in short-lived isolated git worktrees/branches
(build-verified before merging), fast-forward merged into
`merge-experiment`, then cleaned up. This keeps history readable and
avoids leaving half-finished work on the integration branch.

## Known limitations

- Some database migrations remain MySQL-specific in places PGSQL/SQLite
  parity hasn't been added yet.
- Experience Tools is not full Second Life Experience-service
  compatibility.
- `llOpenFloater` is not implemented, not even as a stub.
- `PRIM_GLTF_*` primitive parameters are present but not dispatched.
- Pathfinding is region-local and approximate, not a physics-engine-native
  or Linden-proprietary navmesh service.
- Weather is meaningfully improved but still reasonably called
  experimental.
- RegionWeb's PayPal integration is unconfigured/dormant and duplicates
  RegionCurrency's separate implementation.
- A listed feature may still be disabled in configuration.
- Build success does not replace controlled runtime testing — most of
  this has not yet been tested in-world.

## Attribution and support

Confluence retains the OpenSimulator license and source history, and
started from OpenSim Continuum's consolidation work. Portable
improvements have been cherry-picked and, where architecturally
incompatible with a straight cherry-pick, hand-ported from:

- [Gunthar's OpenSim fork](https://github.com/GuntharDeNiro/opensim)
- [Tranquillity](https://github.com/OpenSim-NGC/OpenSim-Tranquillity)
- [Mobius](https://github.com/Mobius-Team/Mobius)
- [WhiteCore-Dev](https://github.com/WhiteCoreSim/WhiteCore-Dev)
- opensim-lickx — origin of Confluence's MoneyServer and OpenSimSearch
  modules; its original GitHub repository has since been deleted, so
  the only remaining copy is archived locally
- [Halcyon/InWorldz](https://github.com/HalcyonGrid/halcyon) and
  [Homeworldz](https://github.com/homeworldz/server) — audited as a
  preservation effort for design ideas and code from projects that have
  fallen by the wayside; see FEATURES_VS_MASTER.md for what was found

Historical source provenance remains available in Git history.

Report Confluence-specific problems in this repository. Problems
reproducible on an unmodified official OpenSimulator build should be
reported to the official OpenSimulator project.

## License

Confluence is distributed under the same BSD-style license as
OpenSimulator. See `LICENSE.txt`.

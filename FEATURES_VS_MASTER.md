# Casperia vs. upstream OpenSim master

Generated from `git log origin/master..merge-experiment --oneline --no-merges`
(165 commits ahead of `opensim/opensim@master` as of 2026-08-05). Regenerate
with that command if this drifts — git is the source of truth, this is just
a categorized read of it.

## Comparison against the OpenSim-Continuum README

The [OpenSim-Continuum README](https://github.com/Ramius1701/OpenSim-Continuum/blob/master/README.md)
is the master feature list this grid is meant to offer. Checked 2026-08-05
by grepping actual implementation files (not just commit messages, since
the original `a8339fedb4` import was a single squashed commit) on
`merge-experiment` vs. `gunthar/master` (274 commits ahead of us, where a
lot of this originated).

### Confirmed present in Casperia
- Display Names (viewer CAPS, Hypergrid federation lookup, storage/account
  integration), TOS acceptance at login.
- Abuse Reports (CAPS, connectors, Robust handlers, MySQL storage).
- Estate add/remove manager commands, hardware/IP/MAC banning.
- `osPerm2Use`, `PARCEL_DETAILS_OBJECT_RETURN`/`TELEPORT_ROUTING`,
  `osReturnObjects`/`osReturnObject`, `osTriggerSoundAtPos`,
  `llGetRegionTimeOfDay`, `llTransferOwnership`, `llSitOnLink`.
- Per-agent EEP scripting: `llSetAgentEnvironment` /
  `llReplaceAgentEnvironment` (Experience-Lite trust-gated).
- Background/deferred Warp3D map-tile generation.
- Region-crossing and attachment reliability hardening.
- ubODE physics realism pass (buoyancy, boat wave physics, avatar/object
  contact smoothing, friendly avatar social physics).
- All 13 addon-modules present as directories: Gloebit, GroupAutoInvite,
  HoloPhysicsGuard, OpenSimMarketplace, OpenSimMutelist, OpenSimSearch,
  OpenSimTide, OpenSimWeather, RegionCurrency, RegionWeb, plus the
  MoneyServer trio (OpenSim-Grid-MoneyServer, OpenSim-Modules-Currency,
  MySQLMoneyDataWrapper).
- MoneyServer enhancements (purchase limits, atomic credit, idempotent
  confirmation UUIDs, banker/transfer/group controls) — this session
  additionally fixed several real bugs in this area, see PROJECT_LOG.md.

### Confirmed MISSING despite RegionWeb's docs claiming otherwise
RegionWeb's in-world documentation page
(`addon-modules/RegionWeb/RegionWebModule/RegionWebModule.cs`, 148
documented functions) was copied from Gunthar's docs page, but the actual
implementations behind a large chunk of it were never merged into
Casperia. Verified by grepping for the real method definitions in
`OpenSim/Region/ScriptEngine/...` — these exist on `gunthar/master` but
not here:

- **RSA signing:** `llSignRSA`, `llVerifyRSA`.
- **Pathfinding (entire suite):** `llCreateCharacter`, `llUpdateCharacter`,
  `llDeleteCharacter`, `llExecCharacterCmd`, `llNavigateTo`,
  `llWanderWithin`, `llPatrolPoints`, `llPursue`, `llEvade`, `llFleeFrom`,
  `llGetStaticPath`, `llGetClosestNavPoint`.
- **Combat2:** `llDamage`, `llAdjustDamage`, `llDetectedDamage`,
  `llDetectedRezzer`, object-health support, `on_damage`/`final_damage`/
  `on_death`.
- **Region-level EEP scripting:** `llGetEnvironment`, `llSetEnvironment`,
  `llReplaceEnvironment` (only the per-agent variants made it in).
- **GLTF / rendering:** `llSetLinkGLTFOverrides`, `llSetLinkRenderMaterial`.
- **Sculpt-map animation:** `llSetSculptAnim`.
- **Misc parcel/inventory/LSL:** `llGiveAgentInventory`,
  `llSetParcelForSale`, `llGetAttachedListFiltered`,
  `llFindNotecardTextSync`, `llMatchGroup`, `llSetGroundTexture`,
  `llReturnObjectsByID`, `llReturnObjectsByOwner`, `llSetAgentRot`.
- `llOpenFloater` (a stub even in upstream Continuum) — not present at all
  here, not even as a stub.
- `InternalPort = MATCHING` region config and RSA-key login auth —
  unverified, likely tied to the missing RSA signing implementation above.

**Action needed:** either cherry-pick the real implementations from
`gunthar/master` (it has them — see `OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs`
and friends), or trim RegionWeb's doc page so it stops advertising
scripting functions that don't exist yet. Right now a builder reading the
in-world docs would be told these work when they don't.

### Added by us beyond the README (recent work, this session)
Not part of the original OpenSim-Continuum feature list — built directly
in Casperia:
- Full MoneyServer/Currency bugfix pass (IMoneyModule hijack, Nini
  case-sensitivity, CurrencyGroupOnly logic, console logging, ini gaps).
- Weather module end-to-end polish (precipitation persistence, lightning
  positioning, per-profile realism tuning, day/night freeze fix,
  `weather clear` reset-fallback fix).
- Experience Tools self-service creation (configurable fee + per-resident
  cap, matching real SL's `AgentExperiences` capability protocol).
- Display name persistence fix across region restart/cross-sim hops
  (`UserManagementModule.AddUser` cache bug, not present in the README's
  claim list at all).

Full detail on all of the above is in [PROJECT_LOG.md](PROJECT_LOG.md).

---

## Economy / Currency
- MoneyServer + DTLNSLMoneyModule integration, imported from
  OpenSim-Continuum/Continuum-Rebuild addon-modules (`a8339fedb4`).
- This session: fixed IMoneyModule hijack, Nini case-sensitivity,
  CurrencyGroupOnly logic, console logging, ini gaps — see
  [PROJECT_LOG.md](PROJECT_LOG.md).

## Experience Tools
- `IExperienceService` wired into prebuild (`d6ff664893`), StolenRuby
  experience changes (`b82b8a400c` / PR #86), Experience Info typo fixes
  (`71b44361bc` / PR #112), experience permission LSL fix (`c3bb445380` /
  PR #149).
- PGSQL and SQLite backend support for Experience Tools (`84e4d22ecb`,
  `42c44e3e25`).
- This session: full self-service Experience creation with configurable fee
  and per-resident cap, matching real SL protocol — see PROJECT_LOG.md.

## Display Names
- Core Display Names feature (`152579aa12` / PR #94), UserAccounts table
  case fix (`d7c4fa1fdc`), leftover conflict-marker/using-statement
  cleanup, Hypergrid visitors' display names fetched from their home grid
  (`0818d73c2f`).
- This session: fixed display names not surviving region restart/cross-sim
  hops (`UserManagementModule.AddUser` cache bug).

## Moderation / Access Control
- TOS (Terms of Service) Support (`4ae778df9a`).
- Hardware and IP banning (`d7ac79aaec`), basic ban/unban commands for
  IPs/MACs/id0s (`a066403570`).
- Estate add-manager / remove-manager commands (`d3b69de85c`).
- MuteList enforcement in InstantMessageModule (`b35c91e6ab`).
- Abuse Reports service ported from Continuum (`ddc845fcc2`), wired into
  ContinuumModules config (`951d93ed31`), console review commands
  (`3f859d6357`), fatal-crash config-gate fix (`5241c30fb4`).

## LSL / OSSL additions
- `osPerm2Use` (renamed from `osPermissionToCall`) permission-gate function
  (`8f276b7fef`, `7521c8a62b`, `e4a9380d98`).
- `PARCEL_DETAILS_TELEPORT_ROUTING` / `PARCEL_DETAILS_OBJECT_RETURN`
  (`262e7a294f`), missing `OBJECT_*` constants from the 2025 set
  (`056e70f313`).
- `llGetEnv` fix (PR #120), `llTransferOwnership` variables, `osPerlinNoise2D`,
  `osTriggerSoundAtPos`, `osReturnObjects`/`osReturnObject` for scripted
  parcel auto-return, `osAvatarFreeze`/`Thaw` and fly-control functions.
- LinkSet Data feature from SL was added then reverted as redundant
  (`8cf51c1745` / `313ef72883`, PR #64).

## Physics realism (ubODE) — ~40 commits
Large iterative overhaul: friendly avatar social physics (visible,
interactive), soft avatar-to-avatar and avatar-to-object contacts, buoyant
floating-prim water physics (waterline equilibrium, water footprint
sampling, distributed water lift torque, drift/oscillation damping), boat
wave physics (directional drift, wave-aligned roll, moored vs. free
tuning), rubber bounce and material density/inertia tuning, rolling
resistance and near-rest sleep, region-crossing attachment/animation
hardening (debounced script restarts, stale AO/attachment cleanup, velocity
preservation), walk-animation fixes (shape-gender selection, configurable
default, AO-state preservation).

## Map tile rendering (Warp3D) — ~40 commits
Iterative overhaul from the original placeholder/fallback renderer to
exact-geometry aerial map tiles: mesh/sculpt geometry rendering, alpha
texture card handling (fixed solid-rectangle and black-texture bugs), water
depth shading, background/deferred generation to avoid startup and OOM
stalls, texture-asset validation, performance safeguards (skip expensive
rendering while avatars present).

## Terrain
- Perlin-noise terrain generation for new regions (`e2bb7b6090`).
- Look-ahead terrain sampling for flying-avatar height correction
  (`985b58547b`).
- Mainland/island `InitialTerrain` options documented (`7d8629c6db`).

## In-world building
- TextBuild: text-driven in-world building module (`3b33f2e8ee`), richer
  prim shapes/scenic templates, terrain-shaping slash commands, custom
  terrain recipes.

## Sim operations / reliability
- Opt-in sim FPS watchdog with progressive auto-recovery (`98a373c8ce`).
- Viewer god "save region state" command wired up (`d9a6543386`).
- Bind regions and Robust to a specific system IP (`e8e91167db`).
- Option to hide simulator version notices from viewers (`89bf474d2e`).

## Environment
- `ISunModule` implemented, backed by the existing EEP environment system
  (`5c33f098a4`).
- Weather module (addon-modules/OpenSimWeather) — this session's full fix
  pass (precipitation persistence, lightning positioning, profile
  realism, day/night freeze, `weather clear` reset) — see PROJECT_LOG.md.

## Database parity (PGSQL / SQLite vs. MySQL)
- `DisplayName`/`NameChanged`/`TOSDate` columns added to PGSQL
  UserAccounts (`9b4bb6ebb5`).
- Asset name/description columns widened 64→96 / 128 chars
  (`a0a75a730e`).
- Missing PGSQL and SQLite implementations for Experience Tools.

## Stability fixes
- YEngine: prevent crash on orphaned resumed script events, and on missing
  resume stack.
- LLUDP: ACK duplicate resends to protect alpha texture cards on maps.
- `AgentRequestSit` delegate compatibility fix for scripted sits.
- Friends presence info type disambiguation.

## Misc
- Trim "Resident" from username in the welcome message (`489d2adad7`).

---

Everything above is upstream of / separate from the three items worked on
directly this session (MoneyServer bugfixes, Weather module polish,
Experience Tools self-service creation) — those are logged in detail in
[PROJECT_LOG.md](PROJECT_LOG.md) rather than duplicated here.

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

### Was MISSING, now ported from Gunthar's fork (2026-08-06)
RegionWeb's in-world documentation page
(`addon-modules/RegionWeb/RegionWebModule/RegionWebModule.cs`, 148
documented functions) was copied from Gunthar's docs page, but the actual
implementations behind a large chunk of it had never been merged into
Casperia. Ported directly from `gunthar/master` (not cherry-picked —
his commits were too entangled with his own Experience-Lite permission
system, so these were hand-extracted and adapted to sit alongside our
own Experience Tools instead):

- **RSA signing:** `llSignRSA`, `llVerifyRSA`.
- **Pathfinding (entire suite):** `llCreateCharacter`, `llUpdateCharacter`,
  `llDeleteCharacter`, `llExecCharacterCmd`, `llNavigateTo`,
  `llWanderWithin`, `llPatrolPoints`, `llPursue`, `llEvade`, `llFleeFrom`,
  `llGetStaticPath`, `llGetClosestNavPoint` — backed by a self-contained
  region-local A* navmesh engine (`BakedNavMesh`/`CharacterNavState`),
  reusing the existing `KeyframeMotion` system for actual movement.
- **Combat2:** `llDamage`, `llAdjustDamage`, `llDetectedDamage`,
  `llDetectedRezzer`, persisted object-health via `DynAttrs`,
  `on_damage`/`final_damage`/`on_death` events.
- **GLTF / rendering:** `llSetLinkGLTFOverrides` (full PBR material
  override read/write pipeline — glTF JSON extraction, compact
  key-value encoding, KHR texture transforms), `llSetLinkRenderMaterial`.
- **Sculpt-map animation:** `llSetSculptAnim`.
- **Region-level EEP scripting:** `llGetEnvironment`, `llSetEnvironment`,
  `llReplaceEnvironment` — added in a follow-up pass once it turned out
  these are gated by plain OpenSim parcel/estate permissions, not
  Gunthar's Experience-Lite trust system (only the per-agent variants
  were actually entangled with that).

Deliberately left out (see PROJECT_LOG.md for the full rationale):
Gunthar's Experience-Lite permission/trust/KVP-store system (competes
with our own Experience Tools), `llOpenFloater` (a stub even upstream),
and the misc parcel/inventory functions (`llGiveAgentInventory`,
`llSetParcelForSale`, `llGetAttachedListFiltered`, `llFindNotecardTextSync`,
`llMatchGroup`, `llSetGroundTexture`, `llReturnObjectsByID`,
`llReturnObjectsByOwner`, `llSetAgentRot`) — none of these were in the
approved scope for this pass.

Also ported but currently unreachable: `ApplyGltfPrimitiveParams` and its
texture/transform helpers, meant to back `PRIM_GLTF_*` codes in
`llSetPrimitiveParams`/`llSetLinkPrimitiveParamsFast`. Nothing dispatches
to them yet — wiring that in is a separate task, and OpenSim-Continuum's
own README lists that specific path as unsupported.

### Tranquillity feature-parity audit (2026-08-06)
Full audit of `OpenSim-NGC/OpenSim-Tranquillity` against Casperia, same
treatment as the Gunthar audit above. Result: Casperia is now ahead on
LSL/OSSL breadth and addon-module coverage (Tranquillity has neither
RegionWeb nor most of the LSL compatibility work above). Tranquillity's
one big divergence — a migration to Entity Framework Core / ASP.NET
Core Identity for its data layer (`Source/...` tree, dozens of EF model
classes with no basename match anywhere in Casperia) — is a wholesale
architecture swap, not a cherry-pickable feature, and was not pursued.

One genuinely portable feature was found and ported:
- **User Alias service** — lets an account be reachable under one or
  more secondary UUIDs that resolve back to the same UserID grid-side.
  Console-managed only (`create alias`/`show alias`/`delete alias`),
  no HTTP-exposed create/delete, no viewer-visible cosmetic effect.
  Ported with the standard Data/Services/Connectors/CoreModules layering
  used elsewhere in this tree, plus PGSQL and SQLite backends beyond
  Tranquillity's MySQL-only original. Two latent bugs fixed while
  porting (unreachable code in `DeleteAlias`; a possible NRE on null
  `Description` in the HTTP connector). Full detail in PROJECT_LOG.md.
  **Not yet tested in-world.**

### WhiteCore-Dev feature-parity audit (2026-08-06)
Unlike Gunthar's fork or Tranquillity, WhiteCore-Dev shares no git
history at all with vanilla OpenSim (Aurora-Sim lineage, fully
restructured internals — `WhiteCore/DataManager`, its own
`WhiteCore/ScriptEngine`, `WhiteCore/BotManager`, etc.), so this was a
feature-level comparison rather than a cherry-pick. Most of what it has
either duplicates something Casperia already covers (BotManager vs.
core `osNpc*` + Gunthar's ported Pathfinding suite; its WebInterface vs.
the already-imported RegionWeb; `aaWindlight*` vs. the already-ported
EEP functions) or is welded to its own DataManager/ScriptEngine closely
enough that porting would mean a rewrite, not a port (Scheduled
Payments/stipend economy, grid-wide viewer ban by IP/MAC hash, on-demand
soft-start regions). Two features cleared the bar:

- **Land Auction** — a real bid-based parcel auction flow. Casperia had
  `AuctionID`/`SnapshotID` fields on `LandData` but no working mechanism
  behind them. Ported as a self-contained module with console-driven
  start/bid/end/show commands.
- **Team Combat** — team membership, a shared combat respawn point,
  teleport-block while in combat, and configurable health regen for team
  members. Reduced in scope from WhiteCore's original specifically to
  avoid colliding with two damage/health systems Casperia already has
  (vanilla collision damage and Gunthar's Combat2 `llDamage` pipeline) —
  see PROJECT_LOG.md for the full design rationale. Team-kill damage
  mitigation was dropped from scope for the same reason.

Both **not yet tested in-world**. Full detail in PROJECT_LOG.md.

### Mobius feature-parity audit (2026-08-06)
Mobius (discontinued; successor is "NGC/OpenSim-Sasquatch", not audited)
also shares no git history with vanilla OpenSim, but unlike WhiteCore-Dev
its layout is normal vanilla-OpenSim-shaped, so this was a more direct
file-level comparison. Its own README is a detailed beta-by-beta
changelog naming several candidate features — of those, all but one
turned out to already be in Casperia, in some cases more advanced than
Mobius's own version:

- Hardware/IP ban service — `AccessControlService.cs` and friends are
  byte-identical between Mobius and Casperia, already fully wired.
- `PARCEL_DETAILS_TELEPORT_ROUTING`/`OBJECT_RETURN`/`LANDING_POINT` —
  already present (different numeric IDs, same capability), plus more
  of the family than Mobius has.
- `osTriggerSoundAtPos` — present and correctly wired.
- Top Scripts floater stats — Casperia's version reports more
  (native per-script memory tracking, more filter parsing).
- Region restart notification — matches, plus Casperia has a
  "restart immediately if region is empty" optimization Mobius lacks.

One confirmed gap, since ported:
- **In-world terrain console commands** (`terrain elevate/lower/fill`,
  `terrain load texture <uuid>`) — Casperia already had all the
  supporting plumbing (`IRegionConsole`, the existing
  `InterfaceElevateTerrain`/etc. helpers); `TerrainModule.cs` just never
  registered commands against the in-world console. **Not yet tested
  in-world.**

Follow-up check (2026-08-06): the audit's "possibly dangling `LSLSyntaxId`"
flag was a false alarm — `SimulatorFeaturesModule.cs` has a complete,
working `LSLSyntax` CAP (`HandleSyntaxRequest`) that serves real syntax
data read from `bin/ScriptSyntax.xml` (present, 345KB) at startup, with
`[SimulatorFeatures] ScriptSyntax` defaulting to enabled. No gap here;
Casperia already matches Mobius on this one.

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

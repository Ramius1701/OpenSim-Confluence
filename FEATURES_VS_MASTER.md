# Casperia vs. upstream OpenSim master

Generated from `git log origin/master..merge-experiment --oneline --no-merges`
(215 commits ahead of `opensim/opensim@master` as of 2026-08-08). Regenerate
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

### opensim-lickx archival + audit (2026-08-07)
`S:\Github\opensim-lickx` — the vanilla-0.9.3.1-based source of Casperia's
own MoneyServer and OpenSimSearch modules — had its original GitHub repo
deleted, so the local checkout was git-initialized as a pure archival
safety net before auditing it (see PROJECT_LOG.md for detail). Casperia's
currency stack confirmed a superset of everything in its bundled
`opensim.currency-lickx` module. One genuinely missing function ported:
`osGetAgentViewer` (viewer-client identification, `ThreatLevel.Moderate`).
One flagged candidate — automatic MoneyServer schema creation — turned
out to be a false positive; Casperia already has it in a sibling file
(`MySQLMoneyManager.cs`) the audit hadn't checked.

### Halcyon/InWorldz and Homeworldz — preservation audits (2026-08-07)
Explicitly a preservation effort, not just feature-hunting: "some of
those repos like Mobius, WhiteCore, LickX, and Halcyon have fallen by
the wayside. Doesn't mean some of their code and features should be
lost." Two more targets audited on that basis.

**Halcyon** (github.com/HalcyonGrid/halcyon, InWorldz's server) — forked
from OpenSim in 2010, no shared git history with vanilla origin/master
(same pattern as WhiteCore/Mobius) but a mature, still-portable C#/.NET
codebase, ~15 years of independent development. Its actual scripting
engine (`InWorldz.Phlox.Engine`'s compiler/VM) and physics core
(`InWorldz.PhysxPhysics`'s NVIDIA PhysX binding) are closed-source
binaries in this repo — not portable, full stop — but the open C# layers
around them turned up real, substantial findings:
- **Portable:** a complete, mature, LSL-scriptable Bot/NPC framework
  (`OpenSim/Region/CoreModules/Agent/BotManager/`, ~50 documented `bot*`
  functions, pathfinding/wandering/following/sitting/appearance) — at
  the time of this audit, the mature version of what Tranquillity's own
  still-unfinished bot framework was building toward. **Superseded, not
  pursued:** Tranquillity's `develop` branch has since shipped its own
  open, cleanly-licensed bot/NPC framework (module ported into Casperia
  — see the "Tranquillity `develop` mined past its first release"
  section below), making this Halcyon-sourced route unnecessary. Also:
  `llReturnObjectsByOwner`/`llReturnObjectsByID` (confirmed absent from
  Casperia), ~80 `iw*` OSSL-equivalent functions (inventory/string/list/
  agent/group utilities), Euler-rotation LSL functions, a sit-target
  compatibility fix for a long-standing OpenSim sit-position accuracy
  bug, and a small JWT auth module.
- **Worth preserving as knowledge, not portable:** Phlox's fixed-
  timeslice bytecode-interpreter scheduling (a real design contrast to
  YEngine's compile-to-IL approach — VM-interpreted trades raw
  throughput for exact preemption granularity); a deferred-event-
  delivery idea (queue events for not-yet-loaded scripts instead of
  dropping them); three PhysX-era physics design ideas that don't
  actually need PhysX (double-buffered command queues, auto static/
  kinematic/dynamic prim lifecycle, "stick to a moving platform after
  1s" fix for the classic avatar-sliding-off-vehicles problem); and a
  detailed internal design doc on OpenSim's known teleport/region-
  crossing race conditions with Halcyon's proposed staged redesign —
  directly relevant since region crossing is a pain point across the
  whole OpenSim family.
- **Not portable:** the closed-source Phlox VM/compiler and PhysX core
  (licensing + native-binding burden); `InWorldz.Arbiter` (assumes
  Halcyon's own multi-process clustering topology); `InWorldz.Data.
  Assets.Stratus` (Rackspace CloudFiles client, not distinctive).

**Homeworldz** (github.com/homeworldz/server) — NOT a fork, a from-
scratch reimplementation in C++20 (region server) + Go (grid service),
"informed by Halcyon, OpenSimulator, and the SL viewer protocol without
preserving their internal service boundaries or storage formats" (its
own words). Total language mismatch with Casperia's C# — no code is
portable here — but its docs (dedicated `PHYSICS.md`/`PHYSICS_RESULTS.md`,
`SCRIPTING.md`/`VM.md`, and ~30 Architecture Decision Records) are a
genuine source of preservable design rationale, per explicit user
confirmation this kind of ideas-only audit is a legitimate outcome, not
a consolation prize:
- **Physics:** ran a formal Jolt-vs-PhysX-vs-Bullet-vs-Havok evaluation
  before picking Jolt (MIT license, lower integration cost, benchmarked
  lighter on CPU). Two concrete, engine-independent pitfalls worth
  checking against Casperia's own `ubOdeMeshing` pipeline: cylinders
  must keep analytic roundness through the physics pipeline (a backend
  without one "must generate a convex cylinder... must not substitute a
  box," since flattened cylinders are commonly used as wheels), and
  render meshes must never double as collision geometry.
  Contact-force design worth noting: character push force derives from
  mass × configured max acceleration rather than synthetic impulses,
  avoiding the "avatar as unstoppable force" bug.
- **Scripting:** their own "Falcon" VM is a proof-of-concept (15% of
  their own roadmap, one state, two events, two functions) — not a
  reference implementation. The one durable idea: scripts must be
  suspendable after any single completed bytecode instruction, not just
  at event boundaries, so a region crossing never waits on a bad
  handler. They also reversed an earlier plan to build a bespoke
  restricted-Lua VM once they found Second Life's own SLua (MIT-licensed
  Luau fork) already solves the exact state-serialization problem they
  needed — a good example of not reinventing an already-solved problem.
- **Other ideas:** grid-as-trust-anchor with disposable/untrusted
  regions (checksum-verified cross-region asset fetches); an asset model
  splitting immutable content-addressed blobs from viewer-facing assets
  from per-owner instances; honest `SimulatorFeatures` advertisement
  (never claim a capability that doesn't actually work); live
  terrain-derived map tiles instead of scheduled snapshot jobs.

Neither audit resulted in code changes to Casperia beyond the Bot/NPC
framework being flagged as a real, deferred candidate — worth revisiting
as its own project. Homeworldz is worth a second look in 6–12 months
once its scripting/physics phases mature further.

### Full code-level re-audit + 8-batch port (2026-08-08)
Corrected methodology after the opensim-lickx audit above initially
missed a real RemoteAdmin finding (`admin_alert_user`) on a narrow,
currency-module-only pass — full code-level diffs were re-run against
opensim-lickx (full 249-file core tree), Gunthar (279-commit range),
Tranquillity (418-commit range), Mobius (310-file vanilla diff), and
Halcyon (deeper pass), plus a dedicated `LSL_Api.cs`/`OSSL_Api.cs` diff.
8 batches ported and merged into `merge-experiment`; full detail and
commit hashes in PROJECT_LOG.md. Headline items:
- Two more implemented-but-never-wired script functions found
  (`llCastRayV3`, `osSetRot`) — same bug class as `osGetAgentViewer`
  above, present unwired in vanilla OpenSim itself, not lickx-specific.
- Boat turn-banking physics (`ODEDynamics`) was parsed from config but
  never actually applied — now wired into the vertical-attractor roll
  calculation.
- HG identity/friends hardening: canonicalized local-service URLs
  before outbound HG teleport, fixed a no-op stale-cache refresh for
  returning HG visitors, bare-IP HomeURI rejection in
  `GatekeeperService`, and a `HGFriendsService` fix for Mantis 9199.
- Teleport reliability: the hardcoded 10s `WaitForUpdateAgent` wait is
  now configurable (`TransferAgentUpdateWaitMS`, default 30000ms),
  fixing spurious failures on slow HG links — a converged finding from
  both the Gunthar and Mobius audits independently.
- OAR import gained `--lookup-aliases`/`--no-defaultuser`, wiring the
  already-ported User Alias service into creator/owner/last-owner UUID
  resolution instead of always silently reassigning unresolved IDs to
  the estate owner.
- Graceful SIGTERM shutdown for both the region simulator and Robust
  server.
- A closed `/lslhttp/` outbound-URL-filter bypass, and CreatorData
  inventory export flipped from opt-in to opt-out.

Deferred, not yet ported: two riskier Gunthar HG-identity commits that
touch the login critical path directly (account-ServiceURLs-repair
console command, standalone-HG-login HomeURI repair).

### Tranquillity `develop` mined past its first release (2026-08-08)
Tranquillity-Sim published a first formal release
(`OpenSim-NGC/OpenSim-Tranquillity` `release/v1.0`); `develop` had
moved past it with real new work. 3 more batches ported (full detail
in PROJECT_LOG.md):
- **BinaryFormatter removal** — the three remaining live
  `BinaryFormatter` deserialization paths (asset disk cache, YEngine
  script-state migration, KeyframeMotion serialization) replaced with
  fixed-type/explicit-format serialization. `BinaryFormatter` resolves
  types named in the byte stream it deserializes — a code-execution
  vector, removed entirely in .NET 9. Also added `moving_start`/
  `moving_end` script events for general physical movement.
- **Bot/NPC management framework** — `IBotManager`/`BotManager`/
  `BotPersistenceManager`, wrapping Casperia's existing `INPCModule`
  with tag/profile/outfit/navigation tracking and script event
  delivery. Verified as an original implementation wrapping OpenSim's
  own NPC infrastructure, not a resurrection of InWorldz/Halcyon's
  closed-source engine (unlike Phlox below, despite sharing its
  "Legion Grid" origin). Module only — the ~50 `bot*` OSSL functions
  that would let scripts actually call it exist only in Phlox's script
  engine; wiring those into Casperia's own OSSL API is deferred as its
  own effort.
- **Experience Tools SL-conformance fixes** — a new estate-level
  Blocked Experiences tier (previously silently discarded from the
  wire protocol), a real pagination bug in experience search, a
  dropped marketplace-link field, a security hole letting any admin
  (not just the owner) reassign an experience's group, NRE guards, and
  a KV quota raised to the real SL limit (128 MiB). Two related
  Tranquillity fixes were deliberately NOT ported because Casperia's
  own independent implementation is already more capable (an
  acquire-policy gate Casperia already exceeds with a real
  fee-charging creation flow) or because the fix's premise doesn't
  hold here (an EEP-query no-op that's only correct if per-agent EEP
  is a stub — Casperia's is a real, working implementation).

### Phlox script engine — audited, NOT ported: unresolved provenance (2026-08-08)
Tranquillity's `develop` also added a ~98,000-line alternative LSL/SLua
script engine ("Phlox") alongside XEngine/YEngine, with real (partial)
SLua support and genuinely easy integration via the same
`IScriptEngine`/`IScriptModule` seam Casperia already uses for
XEngine/YEngine coexistence. A dedicated research pass — explicitly
NOT a porting attempt — found this is *literally* InWorldz/Halcyon's
own Phlox engine: file headers read "Adapted from InWorldz Halcyon
`ExecutionScheduler.cs`", attributed to "InWorldz Halcyon Developers,"
obtained via an unspecified "Legion Grid" project, with no LICENSE
file, no ThirdPartyLicenses entry, and no explanation of provenance —
just a bare copyright line. This directly contradicts the Halcyon audit
above, which confirmed `InWorldz.Phlox.Engine` shipped as a
**closed-source binary DLL** even in InWorldz's own repository. Other
findings, moot until provenance clears: OSSL support is only 2
functions vs Casperia's 312 (unusable on real content without a large
follow-on effort); Casperia's own independently-built Experience-Lite/
LinksetData interfaces are surprisingly close to what Phlox's adapters
expect (less rework than feared if this ever proceeds). **Status:**
neither shelved nor actioned — the user chose to raise the provenance
question with OpenSim-NGC before any engineering investment.

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

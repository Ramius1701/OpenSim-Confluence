# OpenSim-Confluence vs. upstream OpenSim master

Generated from `git log origin/master..merge-experiment --oneline --no-merges`
(215 commits ahead of `opensim/opensim@master` as of 2026-08-08). Regenerate
with that command if this drifts — git is the source of truth, this is just
a categorized read of it.

**Naming note (2026-08-11):** this project's repo, code, and branding
were renamed to "OpenSim-Confluence" — the original name was only ever
a local working-folder name, never an intentional project identity, and
is not used anywhere in this document by design. See PROJECT_LOG.md's
naming note for the full scope. Entries below predate the rename and
originally used the old name throughout — reworded here to avoid it,
without changing what actually happened.

## Comparison against the OpenSim-Continuum README

The [OpenSim-Continuum README](https://github.com/Ramius1701/OpenSim-Continuum/blob/master/README.md)
is the master feature list this grid is meant to offer. Checked 2026-08-05
by grepping actual implementation files (not just commit messages, since
the original `a8339fedb4` import was a single squashed commit) on
`merge-experiment` vs. `gunthar/master` (274 commits ahead of us, where a
lot of this originated).

### Confirmed present in Confluence
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
Confluence. Ported directly from `gunthar/master` (not cherry-picked —
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
Full audit of `OpenSim-NGC/OpenSim-Tranquillity` against Confluence, same
treatment as the Gunthar audit above. Result: Confluence is now ahead on
LSL/OSSL breadth and addon-module coverage (Tranquillity has neither
RegionWeb nor most of the LSL compatibility work above). Tranquillity's
one big divergence — a migration to Entity Framework Core / ASP.NET
Core Identity for its data layer (`Source/...` tree, dozens of EF model
classes with no basename match anywhere in Confluence) — is a wholesale
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
either duplicates something Confluence already covers (BotManager vs.
core `osNpc*` + Gunthar's ported Pathfinding suite; `aaWindlight*` vs.
the already-ported EEP functions) or is welded to its own DataManager/
ScriptEngine closely enough that porting would mean a rewrite, not a
port (Scheduled Payments/stipend economy, grid-wide viewer ban by
IP/MAC hash, on-demand soft-start regions). Two features cleared the
bar:

**Correction (2026-08-09):** this pass also dismissed WhiteCore's
`WebInterface` as "vs. the already-imported RegionWeb," i.e. a
duplicate not worth porting. A much deeper look this session found
that's wrong — RegionWeb is per-region only, while WhiteCore's
`WebInterface` is a ~100-page grid-wide framework (region/user/estate/
abuse-report/currency manager, user self-service, multi-language) with
no Confluence equivalent at that scope. Its `Currency` module
(`BaseCurrencyServiceModule`/`BaseCurrencyConnector`) was never
code-level audited at all in this pass. Both are now the subject of an
active architecture decision — see "Addon-modules → core
consolidation" in PROJECT_LOG.md — to absorb currency/web-admin/search
into Confluence's own core, following the precedent already set by
Experiences/Abuse Reports/Display Names. Treat the "duplicate, skip it"
verdict on WebInterface as retracted.

- **Land Auction** — a real bid-based parcel auction flow. Confluence had
  `AuctionID`/`SnapshotID` fields on `LandData` but no working mechanism
  behind them. Ported as a self-contained module with console-driven
  start/bid/end/show commands.
- **Team Combat** — team membership, a shared combat respawn point,
  teleport-block while in combat, and configurable health regen for team
  members. Reduced in scope from WhiteCore's original specifically to
  avoid colliding with two damage/health systems Confluence already has
  (vanilla collision damage and Gunthar's Combat2 `llDamage` pipeline) —
  see PROJECT_LOG.md for the full design rationale. Team-kill damage
  mitigation was dropped from scope for the same reason.

Both **not yet tested in-world**. Full detail in PROJECT_LOG.md.

**Second correction (2026-08-10), from a full re-audit requested by the
user after the WebInterface miss above:** three of the five remaining
"duplicate, skip it" / "welded to DataManager" verdicts didn't hold up
either.

- **Land Auction bug found during the re-audit, unrelated to WhiteCore
  itself:** `AuctionModule.AuctionEnd()` transferred parcel ownership
  to the winning bidder without ever charging them - fixed, see
  PROJECT_LOG.md Batch 14.
- **Scheduled Payments/stipend economy — REVERSED, ported.** The
  DataManager coupling was one thin CRUD call, not the algorithm
  itself. Universal stipend payments are live-tested and fully
  automatic; group liability charges were NOT ported (even WhiteCore's
  own version has a never-finished TODO in that exact method); group
  dividends are ported as a real, working, callable method with no
  automatic trigger yet, since Confluence's Groups subsystem has no
  "list every group on the grid" capability at all - a separate gap,
  not a currency one.
- **On-demand/soft-start regions — REVERSED, ported, but not
  activatable.** Near-zero DataManager coupling as suspected. Confluence
  already had the exact mechanism needed (`Scene.Active`, just named
  differently than WhiteCore's `ShouldRunHeartbeat`) so the port itself
  was trivial - but Mono.Addins does not discover the new module as a
  region-module extension on this deployment, for a reason not
  identified despite extensive investigation (ruled out: stale cache,
  bad deployment, wrong folder - see PROJECT_LOG.md for the full trail).
  Code is correct and shipped disabled pending a root cause.
- **Grid-wide viewer ban — PARTIAL, and mostly already existed.**
  Confluence's `LLLoginService` already had IP ban, MAC/ID0 ban, and
  regex-based viewer allow/deny at login - none of that was visible
  from a WhiteCore-only comparison. Added the one real gap (IP *range*
  bans, live-verified against the real DB) and ported baked-texture
  viewer-signature detection as a complementary, not-yet-live-tested
  addition (catches viewers that spoof their self-reported version,
  which the existing regex check can't).
- **`aaWindlight*` and BotManager (mostly)** — re-confirmed as accurate
  dismissals. BotManager's avatar-follow and tag-group management
  specifically are still open items (task remaining, see PROJECT_LOG.md).

Full detail on all of the above, including the investigation trails,
in PROJECT_LOG.md Batch 14.

**Third pass (2026-08-10), working the full "all of it" list from the
re-audit:** BotManager avatar-follow + tag-group management ported to
`osNpc*` (not live-tested with a real script/viewer, same accepted gap
as other viewer-dependent items). SimProtection (FPS auto-mitigation)
ported correctly; initially believed to have hit the same Mono.Addins
region-module discovery issue as on-demand regions, shipped disabled —
**see the final correction below, this was wrong.** **OpenSimSearch
addon replaced with a native land/places search service** (events/
classifieds out of scope, no existing data model for those anywhere in
Confluence) — unlike SimProtection (as first understood), this one *was*
discovered by Mono.Addins and its data layer is fully live-verified,
including confirmed correct coexistence with the untouched OpenSimSearch
addon on a second region. **Correction (2026-08-10, found while building
task #26):** the region-module client-facing wiring specifically (the
code that actually answers a viewer's search panel) was later found to
never execute on Var Test Region — at the time attributed to a
region-specific variant of the Mono.Addins reliability issue. Confirmed
working correctly on Welcome Center instead as a workaround. **See the
final correction directly below — this diagnosis was also wrong.**

**Final correction (2026-08-10): none of this was ever a Mono.Addins
problem.** Chasing an unrelated live hang on Var Test Region traced
everything above to one real bug: `[OnDemand]`/`[SimProtection]`'s
config sections had been inserted into the middle of `[Startup]` in
`Var_Test_Region\OpenSim.ini`, silently truncating it and reattributing
hundreds of lines of real `[Startup]` settings to `[SimProtection]`
instead — a plain INI structuring mistake, confirmed directly against
the file with a throwaway Nini-loading test harness. Fixed by moving
both sections to their correct position. Re-tested OnDemandRegionModule
and SimProtectionModule against the fixed config at the user's request:
**both now discover and wire up correctly** — `Initialise()` and
`AddRegion()` both fire, and OnDemand's real "starting idle" log line
confirmed the heartbeat-pause genuinely engaged. Shipped disabled again
regardless, but now because their actual runtime *behavior* (wake-on-
login, FPS-drop mitigation) hasn't been exercised live yet — not because
anything is broken.

**Follow-up:** Search and WebConsole re-tested on Var Test Region too
(the region they'd originally failed on) — both now confirmed working
there with no workaround needed. WebConsole verified with a full
real-world round-trip (`/admin/console` → region's `/consoleweb`
endpoint → real live region data returned); Search's own client-facing
wiring inferred with high confidence (same shared-module loop, same
restart, zero exceptions, WebConsole succeeding immediately alongside
it) rather than independently proven, since there's no console command
to directly confirm a registered `ISearchModule` interface. Full
investigation in PROJECT_LOG.md.
**OpenSimMutelist "replacement" turned out to need no replacement at
all** — a complete native mute-list stack (service, DB layer, Local/
Remote connectors, HTTP handler, viewer-facing module) already existed
and was already active in every deployed config; the addon was already
dead code. Fixed two small pre-existing cosmetic defects found while
verifying this (a stray namespace, a typo'd filename) and live-verified
the real implementation end-to-end, including exercising the actual
`/mutelist` HTTP endpoint directly. Full detail on all of it in
PROJECT_LOG.md.

### Mobius feature-parity audit (2026-08-06)
Mobius (discontinued; successor is "NGC/OpenSim-Sasquatch", not audited)
also shares no git history with vanilla OpenSim, but unlike WhiteCore-Dev
its layout is normal vanilla-OpenSim-shaped, so this was a more direct
file-level comparison. Its own README is a detailed beta-by-beta
changelog naming several candidate features — of those, all but one
turned out to already be in Confluence, in some cases more advanced than
Mobius's own version:

- Hardware/IP ban service — `AccessControlService.cs` and friends are
  byte-identical between Mobius and Confluence, already fully wired.
- `PARCEL_DETAILS_TELEPORT_ROUTING`/`OBJECT_RETURN`/`LANDING_POINT` —
  already present (different numeric IDs, same capability), plus more
  of the family than Mobius has.
- `osTriggerSoundAtPos` — present and correctly wired.
- Top Scripts floater stats — Confluence's version reports more
  (native per-script memory tracking, more filter parsing).
- Region restart notification — matches, plus Confluence has a
  "restart immediately if region is empty" optimization Mobius lacks.

One confirmed gap, since ported:
- **In-world terrain console commands** (`terrain elevate/lower/fill`,
  `terrain load texture <uuid>`) — Confluence already had all the
  supporting plumbing (`IRegionConsole`, the existing
  `InterfaceElevateTerrain`/etc. helpers); `TerrainModule.cs` just never
  registered commands against the in-world console. **Not yet tested
  in-world.**

Follow-up check (2026-08-06): the audit's "possibly dangling `LSLSyntaxId`"
flag was a false alarm — `SimulatorFeaturesModule.cs` has a complete,
working `LSLSyntax` CAP (`HandleSyntaxRequest`) that serves real syntax
data read from `bin/ScriptSyntax.xml` (present, 345KB) at startup, with
`[SimulatorFeatures] ScriptSyntax` defaulting to enabled. No gap here;
Confluence already matches Mobius on this one.

### opensim-lickx archival + audit (2026-08-07)
`S:\Github\opensim-lickx` — the vanilla-0.9.3.1-based source of Confluence's
own MoneyServer and OpenSimSearch modules — had its original GitHub repo
deleted, so the local checkout was git-initialized as a pure archival
safety net before auditing it (see PROJECT_LOG.md for detail). Confluence's
currency stack confirmed a superset of everything in its bundled
`opensim.currency-lickx` module. One genuinely missing function ported:
`osGetAgentViewer` (viewer-client identification, `ThreatLevel.Moderate`).
One flagged candidate — automatic MoneyServer schema creation — turned
out to be a false positive; Confluence already has it in a sibling file
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
  open, cleanly-licensed bot/NPC framework (module ported into Confluence
  — see the "Tranquillity `develop` mined past its first release"
  section below), making this Halcyon-sourced route unnecessary. Also:
  `llReturnObjectsByOwner`/`llReturnObjectsByID` (confirmed absent from
  Confluence), ~80 `iw*` OSSL-equivalent functions (inventory/string/list/
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
own words). Total language mismatch with Confluence's C# — no code is
portable here — but its docs (dedicated `PHYSICS.md`/`PHYSICS_RESULTS.md`,
`SCRIPTING.md`/`VM.md`, and ~30 Architecture Decision Records) are a
genuine source of preservable design rationale, per explicit user
confirmation this kind of ideas-only audit is a legitimate outcome, not
a consolation prize:
- **Physics:** ran a formal Jolt-vs-PhysX-vs-Bullet-vs-Havok evaluation
  before picking Jolt (MIT license, lower integration cost, benchmarked
  lighter on CPU). Two concrete, engine-independent pitfalls worth
  checking against Confluence's own `ubOdeMeshing` pipeline: cylinders
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

Neither audit resulted in code changes to Confluence beyond the Bot/NPC
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
  `BotPersistenceManager`, wrapping Confluence's existing `INPCModule`
  with tag/profile/outfit/navigation tracking and script event
  delivery. Verified as an original implementation wrapping OpenSim's
  own NPC infrastructure, not a resurrection of InWorldz/Halcyon's
  closed-source engine (unlike Phlox below, despite sharing its
  "Legion Grid" origin). Module only — the ~50 `bot*` OSSL functions
  that would let scripts actually call it exist only in Phlox's script
  engine; wiring those into Confluence's own OSSL API is deferred as its
  own effort.
- **Experience Tools SL-conformance fixes** — a new estate-level
  Blocked Experiences tier (previously silently discarded from the
  wire protocol), a real pagination bug in experience search, a
  dropped marketplace-link field, a security hole letting any admin
  (not just the owner) reassign an experience's group, NRE guards, and
  a KV quota raised to the real SL limit (128 MiB). Two related
  Tranquillity fixes were deliberately NOT ported because Confluence's
  own independent implementation is already more capable (an
  acquire-policy gate Confluence already exceeds with a real
  fee-charging creation flow) or because the fix's premise doesn't
  hold here (an EEP-query no-op that's only correct if per-agent EEP
  is a stub — Confluence's is a real, working implementation).

### Phlox script engine — audited, NOT ported: unresolved provenance (2026-08-08)
Tranquillity's `develop` also added a ~98,000-line alternative LSL/SLua
script engine ("Phlox") alongside XEngine/YEngine, with real (partial)
SLua support and genuinely easy integration via the same
`IScriptEngine`/`IScriptModule` seam Confluence already uses for
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
functions vs Confluence's 312 (unusable on real content without a large
follow-on effort); Confluence's own independently-built Experience-Lite/
LinksetData interfaces are surprisingly close to what Phlox's adapters
expect (less rework than feared if this ever proceeds). **Status:**
neither shelved nor actioned — the user chose to raise the provenance
question with OpenSim-NGC before any engineering investment.

### Added by us beyond the README (recent work, this session)
Not part of the original OpenSim-Continuum feature list — built directly
in Confluence:
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
- This session (early): fixed IMoneyModule hijack, Nini case-sensitivity,
  CurrencyGroupOnly logic, console logging, ini gaps — see
  [PROJECT_LOG.md](PROJECT_LOG.md).
- **Batch 12 (2026-08-09):** native `CurrencyService` replaces the
  Gloebit/MoneyServer/Podex-shaped addon-module dependency as the
  default, following the "absorb proven features into core" mission
  (WhiteCore-Dev precedent).
  Real ledger (`currency_balances`/`currency_transactions`/`currency_purchases`
  MySQL tables), quote/buy XML-RPC protocol matching real viewer
  behavior (`currency.php`, not the root-only default), live balance
  push on transaction, `money add`/`set`/`get` console commands.
  Live-tested end-to-end with a real Firestorm viewer. Full detail in
  PROJECT_LOG.md.

## Native Web/Admin UI (Batch 13, 2026-08-09)
WhiteCore-Dev-inspired grid-wide web framework, hosted on Robust,
replacing a previously-deployed OpenSim-Grid-Interface PHP site (user
approved the replacement explicitly). Built this session, all
live-verified:
- Login/dashboard/home/welcome pages against real grid accounts
  (`IAuthenticationService`, MD5-hashed to match real viewer behavior).
- Grid admin page (`/web/admin`, `UserLevel>=200`): per-region
  Hypergrid open/close toggle, on-demand maptile regeneration, OAR
  backup.
- **Self-service `/web/myregions`**: any logged-in user sees only the
  region(s) they are the estate owner of (`IEstateDataService`), with
  OAR backup and OAR upload/restore (destructive, gated behind an
  explicit confirmation checkbox). Required hand-rolling a
  multipart/form-data parser — no precedent existed anywhere in this
  codebase. Found and fixed a real bug during live testing: `.oar`
  uploads are gzip-compressed and must be decompressed before being
  handed to `DearchiveRegion(Stream)`, which (unlike the string-path
  overload) does not decompress on its own.
- **Self-service `/web/myinventory`**: any logged-in user can back up
  their own inventory or restore from an uploaded `.iar`. Unlike OAR,
  `InventoryArchiverModule`'s save/load API hard-requires a password
  re-check even for an already-logged-in session, so these forms ask
  for the password again; first/last name always come from the
  session, never the form. Found and fixed a real bug in core
  OpenSim along the way: `HttpRequestParser.cs` had a hardcoded
  ~75MB request-body ceiling (an arithmetic typo - `1204` instead of
  `1024`) blocking any sufficiently large upload on **any** HTTP
  endpoint in Robust or a region, not just this feature - raised to
  512MiB. Verified directly against Robust with a real 156MB, 1683-asset
  inventory: full backup then full restore, 0 failures.
- **Abuse Reports (`/web/admin/abuse-reports`)**: surfaces the abuse
  reports Confluence's existing native `IAbuseReportsService` already
  captures via the viewer's report-abuse cap - no new service/DB work,
  just a read-only paginated list plus a detail view with screenshot
  support. Deliberately v1/read-only: `AbuseReportData.CheckFlags` looks
  like an admin resolved-flag but is actually the reporter's own
  submission-time checkboxes, so a real "mark resolved" feature needs an
  actual schema addition across all three data backends
  (MySQL/PGSQL/SQLite) - left as a follow-up rather than misusing that
  field.
- **User Management (`/web/admin/users`)**: search accounts by name,
  view details (including live currency balance), and edit UserLevel -
  replacing the raw-SQL `UPDATE useraccounts SET UserLevel=...` this
  session did by hand earlier (Batch 12) with a real, auditable admin
  path. No create/delete/suspend: `IUserAccountService` has no such
  surface, and a suspend toggle specifically would need extending that
  interface first, since the underlying `active` DB column isn't
  exposed through it at all today.
- **Estate Management (`/web/admin/estates`)**: list all estates
  (owner, region count), and edit name/owner/five key access toggles
  (public access, voice, direct teleport, deny-anonymous, deny-minors)
  per estate. `EstateSettings`' full surface (bans, managers, groups,
  experience lists) is intentionally out of scope for v1 - same
  edit-the-useful-part discipline as User Management. Ownership
  transfer is by name and explicitly refuses to save if the name
  doesn't resolve to a real account (verified live: a bad name is
  rejected and the DB is confirmed untouched, not just visually
  reverted).
- **Purchases & Transactions (`/web/admin/transactions`, 2026-08-10)**:
  grid-wide financial reporting - Transfers and Purchases tabs,
  optional per-agent filter, pagination. Pure read-side work on top of
  Batch 12's ledger; the one small DB-layer change needed was making
  `GetPurchaseHistory`/`NumberOfPurchases` accept `UUID.Zero` as
  "unfiltered" the same way `GetTransactionHistory` already did.
  Live-verified via a real login and real session cookie, not just a
  direct service call - see PROJECT_LOG.md.
- **Grid Statistics (`/web/admin/stats`, 2026-08-10)**: total regions,
  land area, Hypergrid open/closed count, registered accounts, current
  online-user count. The one genuinely new capability -
  `IGridUserService.GetOnlineUserCount()` - promotes a private
  console-command helper (`GridUserService.HandleShowGridUsersOnline`)
  onto the real service interface, threaded through every connector
  layer (Local, Remote, HTTP client, HTTP handler). Live-verified at
  both the WebInterface's own code path and, separately, the actual
  `/griduser` HTTP endpoint the region processes really use - see
  PROJECT_LOG.md.
- **Self-service password reset (`/web/forgot-password` +
  `/web/reset-password`, 2026-08-10)**: real SMTP email (MailKit, same
  `[SMTP]` config the region-side `EmailModule.cs`/llEmail backend
  already uses) with a one-hour single-use token. Live-tested against a
  throwaway raw-socket SMTP listener rather than mocked - this caught a
  real bug (reading the HTTP request body stream twice) before it
  shipped. See PROJECT_LOG.md for the full verification trail.
- **Login-screen news feed (`/admin/news`, `/welcome.php`, `/`,
  2026-08-10)**: grid-operator announcements. The first fully-new
  Data/Service pair added this batch with nothing existing to build on
  (unlike Search/Mutelist) - new `news` table + `INewsService`, no
  region-side component at all since nothing region-side ever needs
  it. Full CRUD cycle live-verified against the real deployed grid -
  see PROJECT_LOG.md.
- **Static page manager (`/admin/pages`, `/web/page/<slug>`,
  2026-08-10)**: admin-authored content pages at an operator-chosen
  URL. Same shape as News, plus slug-uniqueness enforcement and
  `default:`-branch prefix routing since slugs aren't a fixed route
  list. Live-verified including the slug-collision guard actually
  rejecting a real duplicate and a live slug rename - see
  PROJECT_LOG.md.
- **Grid settings editor (`/admin/settings`, 2026-08-10)**:
  live-editable grid name/nickname/welcome message plus a genuinely new
  toggle - whether self-registration is open at all (didn't exist
  before this task). Generic key/value backing store (unlike
  News/StaticPage's typed columns), deliberately, since the set of
  editable keys is expected to grow. Live-verified specifically that
  changes take effect immediately with no Robust restart, and that
  disabling registration is enforced server-side, not just a hidden
  link - see PROJECT_LOG.md.
- **Web-based region console (`/admin/console`, 2026-08-10)**: pick a
  region, run a real console command, see real output — directly
  closes the gap this session's own re-audit called out by name
  (WhiteCore's own equivalent page is a documented stub). Output
  capture required building a small `ICommandConsole`-wrapping,
  non-interactive-safe adapter, since nothing in the existing console
  framework supports capturing command output as a string.
  Deliberately held to a stricter auth standard than this session's
  other Robust↔region internal endpoints, since this one means
  arbitrary command execution. Live-verified with real commands
  producing real live data, plus the auth boundary tested directly
  (wrong/missing/correct secret). Investigating why it initially didn't
  work led to a significant correction of task #18 (see above) — see
  PROJECT_LOG.md for the full story.

This closes the original "region/user/estate/abuse-report/currency
manager" WhiteCore-Dev comparison that started this whole Batch 13
thread. Given how much turned out differently than first assumed while
building it (CheckFlags' real meaning, the `active` column existing but
unexposed, two separate gzip-decompression landmines, a core
`HttpRequestParser` body-size bug), **the rest of the original
WhiteCore-Dev feature-parity audit's verdicts should get a fresh look
before being treated as settled** - noted here as a flagged follow-up,
not yet done.

- **Self-service registration (`/web/register`)**: reimplements
  `UserAccountService.CreateUser`'s full sequence (account, password,
  home region, inventory root) through interface calls only, since that
  method lives on the concrete class, not `IUserAccountService`.
  Prompted by a direct gap found comparing against the old
  `OpenSim-Grid-Interface` PHP site's `register.php`, which the native
  UI had no equivalent of at all until now. Verified live: real account
  created through the public URL, confirmed via DB that home region and
  a full 22-folder inventory tree were both set up (not just a bare
  account row), and all four validation guards (duplicate name,
  password mismatch, too-short password, HG-reserved character in a
  name) confirmed to actually block.

Full detail — including the Apache reverse-proxy setup for
`the test deployment's public hostname`, the currency.php routing bug precedent, the
HttpRequestParser fix, and every live-verification result (including
why public-URL verification of very large uploads from the same
machine/network as the grid is unreliable - likely router NAT
hairpinning, not an application bug) — in
[PROJECT_LOG.md](PROJECT_LOG.md).

### First-landing / marketing tier + WhiteCore-Dev parity, round two (2026-08-10)

Prompted by a direct question — "Doesn't WhiteCore-Dev have these
pages too?" — a full audit of WhiteCore-Dev's real `bin/html/` tree
(not just its server-side code) found genuine, still-live features the
native UI didn't have yet, alongside several pages WhiteCore's own
maintainers have literally commented `<!-- No longer used -->` and
which were correctly excluded. Built in WhiteCore-first order, with
gaps filled from `OpenSim-Grid-Interface` (the user's own production
PHP grid portal) where WhiteCore had nothing:

- **Get a Viewer (`/web/viewers`)** and **Destinations (`/web/destinations`)**:
  real desktop/mobile viewer download list and a self-contained
  (no Leaflet dependency) world map built from the existing
  `map-{zoom}-{x}-{y}-objects.jpg` tile convention and `secondlife:///app/teleport/`
  links. Caught and fixed a Y-axis positioning bug (spurious extra
  `- minY` term) by reading actual computed pixel positions rather
  than trusting the formula — see PROJECT_LOG.md.
- **Web Profile (`/web/profile`), Friends list, self-service account
  pages** (change password/email), **My Transactions**, **My
  Classifieds/My Events**: resident-facing self-service pages with
  no WhiteCore or OpenSim-Grid-Interface equivalent worth copying
  verbatim. Required adding a `CreatorId` field to `EventItem`
  (`GridEventData.cs`) to distinguish resident-owned from admin-owned
  events — live-verified via a full create → list → splash-widget →
  ownership-boundary-403 → owner-delete curl round trip.
- **Announcement banner** (Grid Settings toggle, rendered on
  Home/Welcome) and **admin login-as-user**: both live-verified via
  real settings saves and a real session-cookie mechanism check.
  Deliberately did NOT build delete-account, partner-proposal, or
  ban/kick/message-online-user — no clean/safe backend primitive
  exists for any of the four (`IUserAccountService` has no Delete,
  `LLLoginService` has no per-account ban check, no Robust→region
  message channel exists) — documented rather than faked.
- **About / ToS / DMCA static pages**: real copy adapted from
  OpenSim-Grid-Interface's own `about.php`/`tos.php`/`dmca.php`,
  seeded through the existing static-page-manager admin API (task
  #24) rather than new code. Found and fixed a real bug in the
  process: `HandleStaticPage` was escaping bodies as plain text
  (right for News' short blurbs, wrong for these long-form pages) —
  switched StaticPage rendering to trusted raw HTML, justified since
  static pages are already admin-only.
- **Features page (`/web/features`)**: unlike OpenSim-Grid-Interface's
  hand-set-PHP-constants version, does real live introspection
  (region count/area/HG-open count, registered accounts, currency
  service presence) for what Robust can actually see, and is honest
  about what it can't (per-region script/physics engine settings live
  in each region's own `OpenSim.ini`, never surfaced to Robust) —
  live-verified against real grid numbers, not fabricated ones.
- **Support ticket system (`/web/support`, `/web/admin/support`)**:
  genuinely new ground — neither reference source has a real
  database-backed desk. Guest submission (name+email, no session)
  plus a honeypot anti-spam field, admin queue + status update, full
  logged-in/guest/category-fallback/honeypot live-verification via
  curl. Hit the same DLL-redeployment class of bug twice in one
  deploy (`OpenSim.Data.dll` then `OpenSim.Services.Interfaces.dll`
  both initially forgotten, since reflection-based plugin loading
  fails for a whole assembly if any referenced interface is missing,
  not just the new type) — see PROJECT_LOG.md for the full trail.

Full detail for every item above — audit methodology, every bug found
and fixed, and the complete live-verification trail — in
[PROJECT_LOG.md](PROJECT_LOG.md).

**Correction (2026-08-11):** the "no clean/safe backend primitive
exists" framing above turned out to be wrong for 3 of the 4 items it
named (Ban, Kick+Message, and partner-proposal), and a direct
challenge to that framing prompted a proper re-audit rather than
taking the earlier dismissal at face value. All 3 are now built and
live-verified; only true hard delete-account remains a real gap
(`IUserAccountService` still has no Delete method, and the orphaned-
row risk across Inventory/Groups/Grid/Presence/Currency/Estate is
real) — soft-delete covers the practical need instead. Full estate
list editing (managers/access/bans/groups) was a separate, later v1
scope cut from the "Native Web/Admin UI" batch above, not part of this
dismissal — it was closed out in the same 2026-08-11 pass. Full
detail in PROJECT_LOG.md's "Admin features: Ban/Kick/Message/Estate
lists/Partner proposal" entry.

**Second pass, same day:** a follow-up fresh gap audit (same
methodology — WhiteCore-Dev's real static assets, OpenSim-Grid-
Interface, the wiki, plus an internal consistency check) found nine
more real gaps, all built and live-verified same-day: self-service
estate management for non-admin owners (`/myestates`, matching
WhiteCore's own admin-or-owner estate pages), an admin "create estate"
action, the `PricePerMeter`/`TaxFree` estate fields the edit form had
never exposed, admin-set password reset, admin account creation, a
temporary/auto-expiring ban, admin-editable resident email/name, a
self-service delete-my-account page, and a genuinely new grid-wide
admin Groups oversight page (`/admin/groups` — list every group,
toggle moderation flags, delete a group). One real, documented
limitation: the temp ban's auto-expiry only takes effect via the web
login/admin-page paths, not the actual grid/viewer login
(`LLLoginService`) on its own timer. Full detail in PROJECT_LOG.md's
"Admin features, round two" entry.

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

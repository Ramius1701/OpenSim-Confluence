# Roadmap

What's planned, what's deliberately out of scope, and what's a known
gap today. For what already exists, see `FEATURES.md`.

## In progress / being investigated

- **Vehicle and prim region crossings.** Avatar crossings are already
  smooth (see `FEATURES.md`). Vehicles and other physical objects still
  freeze in place for the duration of a crossing — a deliberate
  server-side safety measure, not a bug, but one that's noticeable on
  a moving vehicle. Fixing this properly needs a new "staged" object
  state so a vehicle can be predictively prepared on the destination
  region without risking a visible duplicate. Designed, not yet built.
- **Hypergrid teleport reliability.** A single retry on outright
  transport failure is in place. Hypergrid teleports still depend on
  several sequential network calls across independently-run grids, so
  a broader retry/backoff strategy remains worth revisiting.
- **A wider audit of the Web/Admin UI against WhiteCore-Dev's page
  set**, to catch anything the current build missed. Ongoing,
  page-by-page — see `WEBUI_PARITY_CHECKLIST.md`.

## Planned, not started

- **SLua support.** Second Life's newer Luau-based scripting language
  (faster than LSL/Mono, native tables, coroutines). No OpenSim fork
  has built this yet. A real embeddable Luau runtime with early C#
  bindings exists, so it's feasible, but the region-crossing-safe
  state serialization, the LSL/OSSL bridge, and the upload/compile
  pipeline would all need to be built from nothing. A genuinely large,
  multi-month undertaking, not a quick add.
- **RSA-key login authentication.** A protocol aimed at bot/proxy
  clients rather than mainstream viewers. No client in this project's
  own stack currently speaks it, so it isn't scheduled until there's a
  concrete reason to build it.
- **True hard account deletion.** Today, deleting an account
  scrambles its password and blocks login (soft delete) rather than
  removing the row outright, since a hard delete would orphan
  references across inventory, groups, presence, currency, and estate
  data. A real hard-delete would need a deliberate cleanup pass across
  all of those first.
- **Halcyon/InWorldz Bot/NPC framework.** A complete, mature LSL-scriptable
  bot framework exists in Halcyon's open C# layer (its scripting engine
  and physics core are closed-source and not portable, but this part
  is). Superseded for now by Tranquillity's own bot framework, which
  Confluence has already adopted — revisit only if a real gap shows up
  that the current framework doesn't cover.
- A handful of smaller Halcyon-sourced candidates remain unported and
  low-priority: `llReturnObjectsByOwner`/`llReturnObjectsByID` (LSL
  versions — the OSSL equivalents already exist), roughly 80 `iw*`
  inventory/string/list/agent/group utility functions, Euler-rotation
  LSL functions, and a small JWT auth module.

## Explicitly out of scope for now

- **Self-service OAR/IAR restore through the web UI** (upload a file
  and have it applied). Backup/save through the web UI is supported;
  restore is not, and is done from the region's own console instead.
  No OpenSim web UI this project has checked offers browser-based
  restore either — a large upload relayed through a typical reverse
  proxy runs into body-size and timeout limits that don't have a clean
  application-level fix.
- **Phlox script engine.** A ~98,000-line alternative script engine
  found in another fork was investigated and not adopted — its
  provenance traces back to a closed-source engine with no clear
  license or chain of custody. On hold pending clarification from the
  original project, not something being built toward.
- **Real-world money.** Every currency system in this project (native
  Currency Service, MoneyServer, RegionWeb's PayPal integration) is
  in-world virtual currency or a straight donation. None of it is a
  payment processor, and there's no plan to make it one.
- **Gunthar's Experience-Lite permission/trust system.** Competes
  directly with Confluence's own Experience Tools implementation.
  Everything genuinely portable and non-overlapping from that fork
  (pathfinding, Combat2, GLTF overrides, RSA signing, region-level EEP
  scripting) has already been ported on its own; the permission/trust
  layer itself was deliberately left out.
- **Tranquillity's Entity Framework Core / ASP.NET Core Identity data
  layer.** A wholesale architecture swap for how that fork stores data,
  not a cherry-pickable feature. Not pursued.
- **A handful of misc LSL/inventory functions** considered and left out
  of an earlier porting pass: `llGiveAgentInventory`,
  `llSetParcelForSale`, `llGetAttachedListFiltered`,
  `llFindNotecardTextSync`, `llMatchGroup`, `llSetGroundTexture`,
  `llReturnObjectsByID`, `llReturnObjectsByOwner`, `llSetAgentRot`.
  None were in scope for that pass; revisit if a real need comes up.
- **Region-manager bypass power.** A reference fork (opensim-lickx)
  trusts region managers with less override power than Confluence
  currently does by default (no automatic bypass on parcel-property
  edits, real grid-god status preferred instead). Confluence's current,
  more permissive default matches upstream OpenSim and is relied on by
  private-estate operators for self-management — flagged as a real
  difference worth knowing about, not changed unilaterally.

## Design research from other projects

Ideas worth remembering from auditing sibling/predecessor projects,
even where nothing was ported — preserved so this research doesn't
have to be redone later. None of this requires action on its own.

**From Halcyon/InWorldz** (closed-source scripting/physics core, but a
mature, still-relevant design):
- Its script engine uses fixed-timeslice bytecode-interpreter
  scheduling rather than compile-to-IL — a real tradeoff (lower raw
  throughput, more exact preemption granularity) worth knowing about if
  YEngine's own scheduling model is ever revisited.
- A deferred-event-delivery idea: queue script events for a
  not-yet-loaded script instead of dropping them.
- Physics ideas that don't need PhysX specifically: double-buffered
  physics command queues, automatic static/kinematic/dynamic prim
  lifecycle, and a "stick to a moving platform after ~1s" fix for
  avatars sliding off vehicles.
- A detailed internal design document on OpenSim's known teleport/
  region-crossing race conditions, with a proposed staged redesign —
  directly relevant since region crossing is a pain point across the
  whole OpenSim family.

**From Homeworldz** (a from-scratch, non-portable C++/Go
reimplementation, but well-documented design rationale):
- Ran a formal physics-engine evaluation (Jolt vs. PhysX vs. Bullet vs.
  Havok) before picking Jolt for MIT licensing and lower CPU cost.
  Two engine-independent pitfalls worth checking against Confluence's
  own physics pipeline: cylinders need to keep analytic roundness
  through the pipeline (a backend without one must generate a convex
  cylinder, not substitute a box — cylinders are commonly used as
  wheels), and render meshes should never double as collision geometry.
- Character push force derived from mass × a configured max
  acceleration, rather than synthetic impulses — avoids an "avatar as
  unstoppable force" bug.
- Their own scripting VM ("Falcon") is only a proof of concept, but one
  durable idea from it: scripts should be suspendable after any single
  completed instruction, not just at event boundaries, so a region
  crossing never waits on a long-running script handler.
- They reversed an earlier plan to build a bespoke restricted-Lua VM
  once they found Second Life's own SLua (MIT-licensed Luau fork)
  already solves the state-serialization problem they needed — a
  useful reminder not to reinvent an already-solved problem.
- Other ideas worth remembering: a grid-as-trust-anchor model with
  checksum-verified cross-region asset fetches for disposable/untrusted
  regions; splitting the asset model into immutable content-addressed
  blobs vs. viewer-facing assets vs. per-owner instances; never
  advertising a capability that doesn't actually work; and generating
  map tiles live from terrain instead of on a scheduled snapshot job.

## Known limitations

- Some database migrations remain MySQL-specific in places PostgreSQL/
  SQLite parity hasn't caught up yet.
- Experience Tools is not full Second Life Experience-service
  compatibility.
- `llOpenFloater` isn't implemented — OpenSimulator has no
  viewer-hosted floater service to back it.
- `PRIM_GLTF_*` readback returns a face's own override data, not the
  base material merged underneath it. Full SL parity would need both.
- Pathfinding is region-local and approximate, not a physics-engine-
  native or Linden-proprietary navmesh service.
- A handful of bot functions (`botListen` position source,
  `botChangeOwner`) have narrower behavior than their `ll`/`os`
  counterparts — documented in code, not planned for expansion right
  now.
- A listed feature may still be disabled in an individual grid's
  configuration — check `bin/*.ini.example` for what ships opt-in.
- Inventory thumbnails and profile-photo upload depend on AIS3, which
  some viewers/builds don't enable by default; there's no legacy-UDP
  fallback for those specific sub-flows.
- **Upgrading an existing MySQL grid to a Confluence version with the
  utf8mb4 fix (see `FEATURES.md` → Database) converts several text
  columns' charset automatically on the next restart** — no admin
  action needed, and it's a lossless conversion for real data in the
  normal case. The one genuine edge case: a handful of the affected
  columns (`useraccounts.DisplayName`/`FirstName`/`LastName`,
  `mutelist.MuteName`) were `latin1`, not the more common `utf8mb3`.
  If a grid already had corrupted ("mojibake") data in those columns
  from some earlier, unrelated charset mismatch, the conversion
  preserves whatever that corruption looked like rather than fixing or
  worsening it. Grid owners with real accented or non-English names on
  their roster should spot-check that table before and after
  upgrading — a fresh install or a grid with plain-ASCII names has
  nothing to check.

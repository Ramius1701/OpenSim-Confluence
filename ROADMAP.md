# Roadmap

What's planned, what's deliberately out of scope, and what's a known
gap today. For what already exists, see `FEATURES.md`.

## In progress / being investigated

- **Meshmerizer vs ubMeshmerizer feature-parity audit (2026-09-09).**
  Full read of both `IMesher` implementations - generic `Meshmerizer`
  (`Meshing/Meshmerizer/`, used by BulletSim + LegionJolt) vs
  `ubMeshmerizer` (`ubOdeMeshing/`, ubODE only) - prompted by the
  disk-cache-mesher idea above. Two real, currently-live bugs found,
  plus a set of lower-priority portable improvements.

  **Live bug, BulletSim, confirmed**: the 8-arg `CreateMesh` overload
  (`Meshmerizer.cs:944-947`) hardcodes `isPhysical=false` regardless of
  what's passed in. `BSShapes.cs:717` (`CreatePhysicalHull`) calls it
  with `isPhysical=true` specifically ("Pass true for physicalness as
  this prevents the creation of bounding box which is not needed"),
  but the override discards that - so any BulletSim physical prim
  under 0.2m/axis (`minSizeForComplexMesh`) gets a 12-triangle
  bounding box instead of real geometry when its convex hull is built,
  exactly the case the caller's own comment says it's avoiding.
  ubODE/Jolt unaffected (ubMeshmerizer's matching overload never reads
  `isPhysical` at all; Jolt calls a different overload entirely,
  `LegionJoltScene.cs:2729`). **Fixed (commit `6532010e06`)** - the
  8-arg overload now delegates the real `isPhysical`/`shouldCache` args
  instead of hardcoding `false`. Built clean, deployed to live
  (`OpenSim.Region.PhysicsModule.Meshing.dll`+pdb, copy verified via
  `md5sum`). Takes effect on any region's next restart - not yet
  confirmed under real content on a running region.

  **Live bug, ubODE, confirmed**: `ubOdeMeshing/Meshmerizer.cs:901`
  sets `primMesh.twistEnd` from `primShape.PathTwistBegin` (copy-paste
  of the line above it) instead of `primShape.PathTwist`, only in the
  circular-extrusion branch (torus/tube/ring). The linear branch three
  lines up gets it right, and generic Meshmerizer's own circular
  branch (`Meshmerizer.cs:865-866`) is also correct - confirmed this
  isn't a units mismatch (traced both engines' twist unit conversions
  through to the same final value). Effect: any twisted torus/tube/
  ring prim physicalized under ubODE gets a collision mesh whose twist
  stays constant instead of interpolating to the real end value -
  physics shape silently diverges from the visual mesh. **Fixed (commit
  `6532010e06`)** - `PathTwistBegin` → `PathTwist`. Built clean,
  deployed to live (`OpenSim.Region.PhysicsModule.ubOdeMeshing.dll`+pdb,
  copy verified via `md5sum`). Same restart caveat as above.

  **Other real findings, lower priority, not yet actioned** (full
  detail and additional file:line citations in `PROJECT_LOG.md`):
  generic's "corrupt prim" guard logs `profileBegin>=profileEnd` but
  doesn't clamp it (ubMeshmerizer's does, unconditionally); a GDI
  bitmap leak in generic's `SculptMap.ScaleImage` (never disposes the
  scaled intermediate; ubMeshmerizer does); ubMeshmerizer recognizes
  `IsometricTriangle`/`RightTriangle` profiles as 3-sided, generic
  only recognizes `EquilateralTriangle` (currently unreachable from
  in-world scripting, reachable from OAR-imported data); ubMeshmerizer
  rejects degenerate 0-vertex/0-face meshes, generic doesn't; generic
  has no outer try/catch around its three mesh-generation calls
  (ubMeshmerizer does); ubMeshmerizer rounds vertices to 6 decimals
  for better welding, generic doesn't.

  **Structural, high-risk, not portable without real design work**:
  generic Meshmerizer has no local convex-hull computation at all for
  procedural (non-mesh-asset) prims or sculpts - it only extracts
  hull data SL already baked into a mesh asset, via methods explicitly
  commented "temporary prototype code - please do not use," yet
  `BSShapes.cs:722-725` uses them in production anyway. ubMeshmerizer
  does real local convex decomposition
  (`ConvexDecompositionDotNet`/`HullUtils`) for any prim type -
  substantially more capable, but porting it means adopting a
  dependency generic doesn't have and restructuring its method
  signatures, not a drop-in. Similarly, ubMeshmerizer's refcounted
  in-memory cache with real eviction (vs generic's monotonically-
  growing, never-evicted static dictionary) and its unit-size mesh
  sharing (N differently-scaled copies of one sculpt share one build)
  both require generic's `Mesh` class to support operations its
  current `Dictionary<Vertex,int>`-based representation isn't built
  for - same category as the already-documented disk-cache idea
  above, not a quick change.

  **Not started** - held pending a priority call, same as the other
  Jolt/Meshmerizer items in this file. The two live bugs are real
  outstanding correctness issues on the actual running grid (BulletSim
  regions right now; ubODE for any twisted torus/tube/ring) - worth a
  decision on a fix separate from the larger architectural items.

- **Native, viewer-integrated Marketplace** (`DirectDeliveryModule` +
  `/marketplace` WebUI) — a real implementation of SL's actual
  `DirectDelivery` capability, traced from Firestorm source: browse and buy
  from a browser, auto-merchant for everyone, unlimited or real finite
  stock per listing, ConfluenceCurrency checkout. See `MARKETPLACE.md` for
  setup/usage. Deployed to Casperia Prime; migration and clean boot
  verified live, browse page confirmed rendering correctly. A real
  end-to-end purchase (buy → charge → deliver) is not yet independently
  verified against live data - present-but-unverified caveat, same as
  WebRTC voice/Aurora below.

  **Real, load-bearing finding, not just a caveat:** Firestorm/AyaneStorm
  hard-block the viewer's own "Marketplace Listings" floater outside real
  Second Life (`LLSLMMenuUpdater::checkMerchantStatus` returns before ever
  asking the region, regardless of caps - confirmed against source, no
  known bypass). `DirectDeliveryModule` is protocol-correct and left in
  place, dormant, for if a non-blocking viewer is ever used - but for now,
  merchants associate inventory through `/marketplace/manage` on the web
  instead (the same `SnapshotListingItem` call the floater would have
  triggered, just invoked from Robust). No in-world UI was added to
  replace it; the folder-organizing step it depends on (`Inventory >
  Marketplace Listings`, item dropped directly inside — a deliberate
  flattening from real SL's per-listing subfolders, so one item is one
  listing) is itself completely ordinary, ungated inventory management on
  every viewer.

  Supersedes `addon-modules/OpenSimMarketplace`'s old v2 HTTP API (a
  service-to-service protocol for an external website, unrelated to the
  real viewer floater), which keeps working unchanged as a legacy/
  external-integration path.
- **Vehicle and prim region crossings — real scoping done, not just a
  guess (2026-09-07).** Avatar crossings are already smooth (see
  `FEATURES.md`). Vehicles and other physical objects still freeze in
  place for the duration of a crossing — a deliberate server-side
  safety measure, not a bug, but one that's noticeable on a moving
  vehicle. Traced the actual freeze mechanism directly rather than
  assuming: `SceneObjectGroup.cs`'s border-crossing path calls
  `root.PhysActor?.CrossingStart()` (ubODE's `ODEPrim.CrossingStart()`
  sets `m_outbounds = true` and zeroes the physics body's velocity) the
  moment a real destination is confirmed, then blocks synchronously on
  `EntityTransferModule.CrossPrimGroupIntoNewRegion()` - a full HTTP
  `CreateObject` POST (serializes and rezzes the whole object on the
  destination) followed by deleting the source copy only on success.
  The freeze lasts exactly as long as that synchronous round trip;
  `ODEPrim.CrossingFailure()` is the existing unfreeze/rollback path,
  restoring position and velocity if a crossing attempt needs to be
  aborted outright.

  **Why the "remove object" RPC alone doesn't fix the freeze.** It
  only lets a *failed* destination copy be cleaned up after the fact -
  the freeze itself comes from doing create-then-delete synchronously
  and serially. Making the crossing non-freezing needs the destination
  copy to already exist and be ready *before* the source stops
  simulating the object - a predictive, staged handoff (informed by a
  sibling project's own crossing design, see "Design research from
  other projects" below) - and the new RPC's real job is the safety
  valve for that: rolling back a staged copy if the prediction turns
  out wrong or the final handoff fails, not the primary mechanism.

  **Buildable in phases, not one large change:**
  - **Phase 0 - the RPC itself - built and deployed (2026-09-07).**
    Added `RemoveObject(GridRegion destination, UUID
    objectID)` to `ISimulationService`, mirroring the existing
    `CloseAgent` pattern end to end: an HTTP `DELETE` client method in
    `SimulationServiceConnector.cs`, the local-then-remote dispatch in
    `RemoteSimulationConnectorModule`, a direct in-process scene call
    in `LocalSimulationConnectorModule`, and a server-side handler in
    `ObjectHandlers.cs`'s `ObjectSimpleHandler` - whose `DELETE` case
    was a hardcoded 405 and whose URL-path parsing for
    `/object/{objectID}/{regionID}/` was written but disabled ("this
    things are ignored") since `CreateObject`'s POST path never needed
    it; both are live now. Dropped the `authToken` parameter originally
    sketched here: unlike `CloseAgent`, which checks a real per-session
    secret established at `CreateAgent` time, `CreateObject` never
    established an equivalent secret for `RemoveObject` to check -
    adding a parameter nothing actually validates would be dead code,
    not real security, so `RemoveObject` sits at the same trust level
    `CreateObject` already does rather than a fabricated stronger one.
    Also wired into its first real caller: `CrossPrimGroupIntoNewRegion`
    (the duplication-bug fix already live, 2026-09-05) now attempts a
    `RemoveObject` rollback of the destination copy when the source-side
    delete exhausts all 3 retries, before falling back to the existing
    log-and-alert path - if the rollback succeeds, the object simply
    stays on the source with no duplicate anywhere and no owner alert
    needed; the alert now only fires if the rollback also fails. Build
    confirmed clean (0 Warning(s), 0 Error(s)).
  - **A separate, real, small fix - built same day the Jolt evaluation
    resumed the Legion-Grid-Code review (2026-09-08).** Not part of
    the freeze/staged-handoff work above - a genuine bug in the entry-
    position clamp itself, found by sampling Legion's own commit
    history and confirmed present in this codebase before porting
    anything: `EntityTransferModule.GetObjectDestination()` used a
    flat `0.2f` entry-distance clamp regardless of the crossing
    object's velocity, which (per Legion's own measured repro on their
    grid) can place a >2m vehicle's root ON the region seam and cause
    a near-stationary vehicle to ping-pong between regions - 16
    crossings observed in 90 seconds. Fixed with a velocity-
    proportional entry offset (`|velocity| * 0.25s`, floored at the
    old `0.2f` for slow objects, capped at `4.0f`), small and
    orthogonal to the freeze redesign below. Build confirmed clean.
    See PROJECT_LOG.md for the full trace.
  - **Phase 1 - a staged/pending object on the destination.** A copy
    exists in the destination scene's memory but isn't added to the
    spatial index, isn't sent to any viewer, and isn't in the physics
    scene - held in a pending-objects table keyed by object UUID rather
    than a new `SceneObjectGroup` state, plus a lightweight "activate"
    call to promote it to fully live. Moderate complexity, genuinely
    new.
  - **Phase 2 - predictive trigger on the source, the hardest and
    riskiest part.** Today, crossing only starts once the object's
    position has actually crossed the border - confirmed by tracing
    `GetObjectDestination()`'s call site directly. A non-freezing
    design needs staging to begin *before* the border is crossed
    (velocity + distance lookahead), which is new logic with real edge
    cases: direction reversal after staging has started (needs the
    Phase 0 rollback), and the sitting-avatar crossing path - already
    far more heavily special-cased in `SceneObjectGroup.cs`
    (`avtocrossInfo`, per-avatar `m_crossingFlags`, far-crossing
    handling) than the empty-vehicle fast path - that any staged
    redesign has to not break.
  - **Phase 3 - matching physics-engine support.** ubODE (confirmed
    the actual live engine on Casperia via direct profiling, see
    PROJECT_LOG.md) is the only one that needs to be correct for this
    grid, but `CrossingStart`/`CrossingFailure` are implemented across
    BulletS/POS/BasicPhysics too and would silently regress for anyone
    using a different engine if ignored outright.

  **Estimated cost**: Phase 0 alone is small and worth doing on its
  own regardless of the rest. The full non-freezing redesign (Phases
  1-3) is a genuine multi-week effort - realistically 3-5 weeks, larger
  than the navmesh estimate above, because a wrong predictive trigger
  risks the exact visible-duplicate failure mode this feature exists to
  prevent, on a live grid with real vehicles and real residents sitting
  on them, and this kind of timing-sensitive crossing behavior can't be
  meaningfully verified without live-grid testing (a real moving
  vehicle, both the empty and sitting-avatar paths, plus a forced-
  rollback test). Recommendation: build Phase 0 now, hold Phases 1-3
  pending a priority call, since the freeze is a working safety measure
  today, not a correctness bug - this is a comfort/polish improvement,
  not a fix.
- **Web/Admin UI audit against WhiteCore-Dev's page set — the
  page-by-page pass is done (last row closed 2026-08-23), not
  ongoing.** Every route in `WEBUI_PARITY_CHECKLIST.md` is checked off.
  All 4 real gaps the audit found are now built (2026-09-07, see
  PROJECT_LOG.md): the per-region profile page (`/region`); a
  grid-wide login toggle (`/admin/settings`) - the latter's own scoping
  pass found the original flagged-gap description was wrong,
  WhiteCore-Dev's own reference toggle is purely cosmetic and never
  actually blocked a login, deployed and fully exercised end to end
  with real accounts; abuse-report resolved/assigned tracking
  (`/admin/abuse-reports`) - `Active`/`AssignedTo`/`Notes` are now real
  fields across all 3 DB backends and the admin page is a real
  editable queue, not read-only, deployed and confirmed clean; and the
  avatar-selection starter-look carousel on `/register` - see its own
  entry directly below (built and deployed the same day).
- **Avatar-selection starter-look carousel on `/register` — built and
  deployed (2026-09-07).** WhiteCore-Dev's reference
  `register.html` sources its carousel from `.aa` "Avatar Archive"
  files (a whole subsystem: a save-as-archive region command, an
  `avatararchives` DB table, admin upload/browse UI) that doesn't exist
  anywhere in this codebase or its history - building that literally
  would mean porting an entire, separate feature just to get a picker.

  **A smaller, already-proven mechanism does the same job.**
  `RemoteAdminPlugin.cs`'s existing `EstablishAppearance`/
  `CopyWearablesAndAttachments`/`CopyInventoryFolders` (used today by
  the XML-RPC `admin_create_user`/`admin_update_user` calls' `model`/
  `gender` parameters) already clones one account's full look - shape,
  skin, clothing, attachments - onto another by copying its Clothing/
  Body Parts inventory folders and calling `AvatarService.SetAppearance`.
  Traced its actual service calls directly: apart from two lines that
  just fetch `scene.InventoryService`/call `scene.AddInventoryItem`
  (confirmed `Scene.AddInventoryItem` is a thin wrapper - for an
  offline target it reduces to a plain `InventoryService.AddItem(item)`
  call), nothing in this mechanism touches a live Scene at all - it's
  pure `IAvatarService`/`IInventoryService` work, both already
  available as fields on `WebInterfaceServiceConnector` (Robust-side).
  So the existing logic can be ported almost directly into a new
  `ApplyStarterLook(destination, modelAccountId)` method there, with no
  new appearance-copying logic to invent.

  **Design: admin-curated "model" accounts, not archive files.** An
  admin creates ordinary `UserAccount`s in-world (any name), logs in
  with a real viewer, and dresses each one up as a starter look -
  completely standard, familiar workflow, no new tooling needed on the
  admin's side. A new `starter_looks` table (`StarterLookData`: LookID,
  Name, ModelAccountID, SortOrder, Enabled), built with the same
  reflection-based generic table handler + migration pattern already
  used for `GridSettings`/`AbuseReports`, lists which accounts appear
  in the carousel and in what order. `/register`'s carousel thumbnail
  reuses two mechanisms that already exist in this exact file, not new
  plumbing: `m_UserProfilesService.AvatarPropertiesRequest` for the
  model account's profile snapshot `ImageId`, rendered via the same
  `/CAPS/GetTexture?texture_id=...` `<img>` pattern already used for
  region terrain/marketplace thumbnails.

  A new `/admin/starter-looks` CRUD page (add/edit/remove, matching the
  abuse-reports list+detail-form pattern) lets the admin pick which
  existing account is the model by typing its name - the same "First
  Last" server-side resolution `HandleAdminTransactions`'s own agent
  filter already uses, not a full account-listing dropdown (simpler,
  and doesn't grow unwieldy on a grid with many accounts).

  **Real edge cases identified, not just the happy path:**
  - A model account with no `AvatarAppearance` yet (never actually
    dressed) must no-op gracefully on apply, matching
    `EstablishAppearance`'s existing null-check - and the admin page
    should warn at save time if a chosen model has no appearance yet,
    so a broken carousel tile doesn't ship silently.
  - Copied Body Parts/Clothing items need `ApplyNextOwnerPermissions`
    (ported alongside, not skipped) so a model's own restrictively-
    permissioned items don't land unusable on the new owner.
  - Model accounts are real `UserAccount` rows and will show up in
    `/admin/users`, search, and profile lookups like any resident -
    same tradeoff the existing RemoteAdmin mechanism already has, not
    new to this feature. Worth a one-line admin-facing note, not a
    blocker.

  **Estimated cost**: comparable to the abuse-report tracking feature
  just shipped - one new data class/table/service (mechanical, matches
  an established pattern exactly), one appearance-copy method ported
  from proven existing logic rather than invented, one register-page
  UI addition, one new admin CRUD page. Realistically a single build
  session, not a multi-day effort.

  **Built same day, matching the estimate.** New `OpenSim.Services.
  StarterLookService` project (`IStarterLookService`/`StarterLookData`/
  `IStarterLookData`, all 3 DB backends via the generic table handler
  pattern, `[StarterLookService]` wired into both `Robust.HG.ini.example`
  and `Robust.ini.example`) plus `ApplyStarterLook`/`CopyStarterLookFolder`
  on `WebInterfaceServiceConnector` (the ported appearance-copy logic),
  a tile-grid carousel on `/register` (reuses the site's existing
  `.widget-grid`/`.widget-card` look, not a new bespoke design), and
  `/admin/starter-looks` for CRUD. One real security check added
  during implementation, not in the original scoping pass: the
  carousel's submitted value is just another account's UUID, so
  `HandleRegister` validates it against the current session's own
  enabled-looks list server-side before calling `ApplyStarterLook` -
  without that, anyone could submit an arbitrary principal ID and clone
  that account's inventory items, not just pick a real carousel option.
  Two build-tooling snags hit and fixed, unrelated to the feature logic
  itself: `dotnet sln add` triggered a latent `MSB5004` "two projects
  named OpenSim" collision between the real `OpenSim.csproj` and an
  identically-named pre-existing solution folder (fixed by renaming the
  folder, cosmetic only); several of this repo's older csproj files
  (`OpenSim.Framework`, `OpenSim.Data`, the three `Data.*` backend
  projects) use explicit `<Compile Include>` lists rather than SDK-style
  globbing, so each new file needed an explicit entry or it silently
  never compiled in. Build confirmed clean (0 Warning(s), 0 Error(s)).
  **Deployed same day (2026-09-07), see PROJECT_LOG.md** - clean, no
  incidents; the `StarterLooks` table was created automatically on
  Robust's first boot. No starter looks are configured yet, so the
  `/register` carousel won't show anything until an admin adds at
  least one via `/admin/starter-looks` against a real, already-dressed
  account.

## Planned, not started

- **RSA-key login authentication.** A protocol aimed at bot/proxy
  clients rather than mainstream viewers. No client in this project's
  own stack currently speaks it, so it isn't scheduled until there's a
  concrete reason to build it. A real reference implementation exists
  in Mobius (Beta 1.2, PEM-format public/private keys) if this is ever
  prioritized — confirmed genuinely absent from this codebase, not
  already covered under a different name.
- **Real navmesh visualization (viewer-side `LLPathingLibImpl`) — real,
  costed option, held for now (2026-09-05).** The Pathfinding floater's
  "View/test" tab shows "Cannot find pathing library implementation"
  because Firestorm/CoolVL/AyaneStorm ship `LLPathingLib` (the viewer
  interface that decodes and renders navmesh geometry) as a genuine,
  empty stub - not a closed-source blocker as earlier entries assumed.
  The interface itself is LGPL-2.1 and lives in the viewer's own
  open-source tree (`indra/llphysicsextensionsos/llpathinglib.h`);
  Linden Lab's real implementation was simply never open-sourced for
  third-party viewers, and no separate closed binary gets swapped in at
  build time to replace it. Since a real implementation would control
  both the server's wire format and the client's decoder, this doesn't
  need to reverse-engineer Linden's actual format - a new, self-defined
  one works, verified because both ends would be ours.

  Real scoping done, not just a guess: the stub is missing 4 of its 16
  required methods (compiles today only because it's never
  instantiated - implementing it also activates a second, unrelated
  debug view, the Pathfinding Characters capsule display, which needs
  those same 4 methods); all rendering-pipeline/shader plumbing is
  already fully built and shipped, so client work is confined to the
  stub itself; the real gap is server-side - `BakedNavMesh`
  (`LSL_Api.cs`) is purely terrain-height-derived today and has zero
  awareness of the per-object `navmesh_category`/walkability properties
  this project's own WebUI already lets residents edit, so a real
  bake-time classification pass would need building, not just
  serializing existing data. `generatePath()` (the "Test path" tab) is
  fully client-local and safe to leave unimplemented for a first
  version without looking broken.

  **Estimated cost**: ~3-4 weeks to a working, self-compiled patch
  (server-side wire format + client `LLPathingLibImpl`, applied by
  someone with an existing Firestorm build environment) - genuinely
  smaller than forking a viewer wholesale, since the affected client
  code is one small, isolated static library. Shipping it instead as a
  fully signed, branded custom viewer release would add another 1-2
  weeks plus *recurring* cost on every future upstream Firestorm
  update - that specific path carries the same overhead this project
  already decided against for a full viewer fork, and isn't
  recommended unless a patch genuinely isn't viable for residents.
  CoolVL doesn't have this floater/library at all and wouldn't benefit
  either way; AyaneStorm's copy of the stub is byte-identical to
  upstream's and would very likely work unchanged. **Held, not
  started** - real, buildable, and worth revisiting if a resident/admin
  build pipeline for a self-compiled patch becomes worth setting up.
- **"LegionJolt" — real Jolt Physics engine port, portability/performance
  evaluation done (2026-09-08), held pending a priority call.**
  Discovered while resuming the paused Legion-Grid-Code `slua-tier2-tables`
  review: ~65 of that branch's 193 unreviewed commits (a third of it,
  previously miscounted as SLua/Phlox work) are a genuine, substantial
  Jolt Physics integration - M1 through M8+, its own design-decision
  log, real production-hardening (a vendored patched `joltc` for
  per-region native-allocator isolation, shutdown-crash fixes). Not
  experimental scaffolding.

  **Licensing: clean, both real dependencies MIT.** Jolt Physics itself
  (`jrouwe/JoltPhysics`, 11.5k stars, used in shipped commercial titles
  like Horizon Forbidden West) and `JoltPhysicsSharp` (the .NET
  binding) are both MIT. Legion's own wrapper code has no repo-level
  LICENSE file (a real, if minor, provenance gap worth knowing about),
  but it wraps two cleanly-licensed dependencies rather than anything
  closed-source - nothing like the Phlox saga.

  **Architecture: a real, complete drop-in, not a stub.**
  `LegionJoltScene : PhysicsScene, INonSharedRegionModule` (3,782
  lines) genuinely implements OpenSim's own physics plugin contract -
  `AddAvatar` (all 3 real overloads), `RemoveAvatar`/`RemovePrim`,
  `AddPrimShape`, `RaycastWorld`, `Simulate`, terrain/water. A file
  header comment claiming "SKELETON ONLY, ZERO physics behaviour" is
  stale, left over from the initial scaffold commit - checked the
  actual method bodies directly (e.g. `AddPrimShape`'s real shape-
  classification logic, correctly distinguishing SL's real box/sphere/
  cylinder profile+extrusion codes from OpenSim's own known
  `CreateCylinder()` quirk) and confirmed real, substantial
  implementations throughout, not accept-and-ignore placeholders. Zero
  `NotImplementedException`/TODO markers remain in the current file.
  The engine core itself is cleanly separated behind
  `ILegionPhysicsBackend` (handles not objects, zero per-frame
  allocation, shapes independently lifetime-managed from bodies, no SL
  semantics below the seam) - a genuinely sound design, not just
  functional.

  **Portability: confirmed directly, not assumed - built and ran
  natively on this Windows machine, zero extra tooling.** Extracted
  the self-contained `Legion.Physics`/`Legion.Physics.TestHarness`
  projects (net8.0, `JoltPhysicsSharp` NuGet package bundles a
  pre-built native `joltc.dll` for `win-x64` - no C++ toolchain or
  Docker/WSL2/VirtualBox needed, matching the operator's own stated
  bar) and ran the real M1-M4.5 proof suite: **all 40 correctness/
  determinism checks passed** - terrain heightfield extent and Y-up-
  to-Z-up axis conversion, box/sphere/capsule/cylinder/mesh/compound
  shape cooking, contact lifecycle (Begin/Persist/End with correct
  UserData and impulse scaling), avatar-avatar blocking, sensor
  overlap, bit-identical determinism across repeated runs, and several
  named regression repros (a loaded-linkset boot stall, a terrain-
  unbury fix) all passing clean.

  **Performance: one real, measured number, not assumed - no
  existing benchmark existed to just read (Legion's own commit history
  only has a thread-safety stress test, not throughput numbers), so
  built one.** A from-scratch throughput test (N dynamic boxes falling
  onto flat terrain, real `Step()` calls timed after a warm-up) found
  **10,000 simultaneously-active dynamic bodies step in ~2.2ms/step,
  single-threaded** - roughly 40x headroom under a 60Hz (16.67ms)
  budget, and far more under OpenSim's actual real-world physics rate
  (the test harness's own code references "OpenSim's 11 fps physics",
  ~90.8ms/step). **Caveat, stated plainly**: this is a synthetic
  microbenchmark (plain boxes, no mesh/sculpt geometry, no scripted
  llApplyImpulse traffic, no vehicles, no SL-glue/collision-dispatch
  overhead on top) and there's no equivalent side-by-side number for
  Confluence's own live ubODE engine - ubODE is deeply embedded in
  OpenSim's scene machinery and wasn't independently extracted for a
  true apples-to-apples run this pass. The number is real and
  comfortably fast on its own terms; it is not a proven "faster than
  ubODE" claim.

  **Integrated as a selectable third physics engine, 2026-09-09** - the
  user's explicit call: add it "like BulletSim is to ubODE," a
  pluggable operator choice, not a replacement for either existing
  engine and not required to have full day-one parity. Legion's own
  repo already laid this out as a self-contained, drop-in module
  (`OpenSim/Region/PhysicsModules/LegionJolt/` +
  `OpenSim/Addons/LegionPhysics/{Legion.Physics,Legion.Vehicles}/`,
  matching Confluence's own `PhysicsModules/BulletS`/`ubOde` sibling
  convention exactly, self-selecting on `[Startup] physics = Jolt`
  with no other config edits needed) - pulled all 17 source files in
  as-is. Two real integration gaps found and fixed, neither present in
  Legion's own build environment apparently, both confirmed live here:
  a latent `OpenSim.sln` MSB5004 name collision (pre-existing, just
  surfaced by `dotnet sln add` - same documented gotcha as before, same
  fix, cosmetic solution-folder rename) and `Legion.Physics.dll`/
  `Legion.Vehicles.dll` not actually landing in the shared `bin\`
  despite the module's own `ProjectReference`s and a code comment
  claiming they would - added explicit copy items, the same workaround
  pattern the csproj already used for the native `joltc.dll`/
  `JoltPhysicsSharp.dll`. Deliberately did NOT bring in Legion's
  vendored, custom-patched `joltc.dll` (their fix for multiple
  `PhysicsSystem`s sharing one static `TempAllocator` when several
  regions run in ONE process) - Casperia's real deployment is one
  `OpenSim.exe` process per region, confirmed from the live process
  list, so that specific patch doesn't apply here; using the stock
  NuGet-provided native instead keeps the integration simpler.

  **Boot-tested for real, not just built** - built the full solution
  clean, then ran an actual isolated `OpenSim.exe` instance (scratch
  copy, throwaway SQLite-backed region, `physics = Jolt` +
  `meshing = Meshmerizer`) rather than trusting a successful compile
  alone. Confirmed live in the log: Mono.Addins discovered and loaded
  the plugin, `[LEGION JOLT] enabled (physics = Jolt)` self-selection
  fired correctly, the native backend initialised with a real terrain
  heightfield conversion and `MaxBodies=65536` on actual Jolt 5.x, and
  the region reached full `TriggerRegionReady`. Ran cleanly across two
  regions in the same test process too. Config documented in both
  `bin/OpenSim.ini.example` and `bin/OpenSimDefaults.ini`, commented
  out by default (matching BulletSim's own template presentation) with
  an explicit "newer/less-proven than the two established engines"
  note, since real in-world testing under Casperia's actual content
  (vehicles, existing prims/scripts, real avatars) hasn't happened yet
  - genuinely open, not a blocker to having it available as a choice.
  **Deployed to the live grid (2026-09-09).** Module DLLs copied to
  Casperia's shared runtime folder and confirmed showing on the live
  `/features` Platform Overview page. Starbase Andromeda's own
  `OpenSim.ini` has since been switched to `physics = Jolt` +
  `meshing = Meshmerizer` at the operator's request, as the first
  real-content test case.

  **First real-content start found and fixed a genuine gap**: 5,160
  prims fell back to a bounding-box collision shape during region load
  (sculpt/mesh asset not fetched yet when the shape was cooked), and -
  unlike BulletSim - LegionJolt never retried once the asset actually
  arrived, so the wrong shape stuck permanently. Confirmed real via a
  restart (same warning fired again, ruling out stale state). Fixed by
  giving LegionJolt the same async-fetch-then-rebuild path BulletSim
  already has (`RequestMeshAssetRebuild` + a step-thread-deferred
  drain, mirroring the module's existing `_pendingActivation` pattern).
  **Confirmed working with direct evidence, not an inferred count**
  (commit `49aedfc869` adds explicit fetch/rebuild-outcome logging,
  after an initial attempt to verify via the `IMesher returned null`
  count swinging across boots turned out to be an invalid proxy - that
  line fires regardless of whether the retry later succeeds): on
  Starbase Andromeda's real content, all 4,654 fallback prims were
  re-fetched and re-cooked - 4,614 became real mesh shapes, 40 stayed
  bbox (genuinely degenerate sculpt geometry, matching the separate
  `mesher geometry unusable` count exactly - correctly not retried).
  Zero failed fetches, zero skipped rebuilds, zero exceptions. Full
  detail in `PROJECT_LOG.md`.

  **Considered and held: a disk-persistent mesh cache, so a restart
  doesn't re-fetch/re-cook the same content every time.** Investigated
  porting `ubMeshmerizer`'s existing `MeshFileCache` pattern (disk-
  backed, survives process restarts) onto the generic `Meshmerizer`
  BulletSim/Jolt share (currently in-memory only - `static
  Dictionary<ulong, Mesh>`, wiped every restart, which is why the
  4,654-prim fetch/re-cook above happens on EVERY boot, not just the
  first). Confirmed feasible: `AMeshKey` is already a shared type
  (`SharedBase/IMesher.cs`), and ubOde's `GetMeshUniqueKey()` only
  depends on shared types - portable as-is. But the two `Mesh` classes
  (`Meshing/Meshmerizer/Mesh.cs` vs `ubOdeMeshing/Mesh.cs`) have
  genuinely different internal field layouts, so `ToStream`/
  `FromStream` would need fresh serialization code, not a copy-paste -
  and a subtle bug there produces *silently wrong* collision geometry
  (not a safe, visible fallback like the bug above), on a mesher
  BulletSim's other live regions share too. **Held, not started** -
  real, worth doing if the every-restart re-fetch cost becomes an
  actual problem, but needs its own isolated build-and-test pass
  (round-trip serialization correctness, a scratch region) before
  going anywhere near live Casperia, same discipline the original
  Jolt integration got.

  **Feature-parity audit vs ubODE/BulletSim (2026-09-09).** FEATURES.md
  credits ubODE with "buoyant floating-prim water physics, boat wave
  response, rubber bounce and material density tuning, rolling
  resistance, avatar/object contact smoothing, and friendly avatar
  social physics" - checked whether Jolt has real equivalents (not
  just similar-sounding code) for each, and what BulletSim itself
  actually has under the same categories (BulletSim's own FEATURES.md
  entry just says "included as-is," which turned out incomplete - see
  below). Read the real method bodies in all three engines' wrapper
  code, not comments or naming.

  | Category | ubODE | BulletSim | Jolt |
  |---|---|---|---|
  | General (non-vehicle) buoyancy | Real - gravity scaled `(1-buoyancy)` | Real - same formula, pushed once via `SetGravity` | **Absent** - `Buoyancy { get => 0f; set { } }` no-op stub |
  | Boat wave response | Real - 2-component travelling sine wave, analytic normal+flow | Real but vehicle-scoped only, same sine-wave approach | **Absent** - vehicle hover uses a flat water plane, no wave math anywhere in Jolt/Legion.Vehicles |
  | Material/rubber-bounce tuning | Real - material table, `sqrt(mu1*mu2)` blend, rubber-biased bounce formula | Real - full material table (Stone/Rubber/Glass/etc, ini-overridable), llSetPhysicsMaterial wired end to end | **Absent** - `SetMaterial` never overridden (uses `PhysicsActor`'s no-op base); backend has flat hardcoded friction defaults (0.6/0.5) regardless of material; llSetPhysicsMaterial has no effect |
  | Rolling resistance | Real - global scene tunable scaled by per-prim friction, velocity-proportional drag | **Absent** - no `RollingFriction` anywhere in the wrapper or native API surface | **Absent**, same as BulletSim |
  | Avatar/object contact smoothing | Real - EMA-filtered contact normals, landing/settle/slope damping, plus ODE contact-joint softening (soft ERP/CFM) | Real - dual-friction (standing/walking) state machine with a stationary-velocity debounce, terminal-velocity clamp, `ContactProcessingThreshold`/`CollisionMargin` | **No custom C# equivalent** - but Jolt's avatar is a native `CharacterVirtual` kinematic controller (`IsSliding` is a real native feature), architecturally different from ubODE/BulletSim's rigid-body-simulated avatars; genuinely unclear whether the same jitter problem even applies, not a confirmed like-for-like gap |
  | "Friendly" avatar-avatar social physics | Real - per-mode (friendly/playful/romantic/no-touch) compliant contact (soft ERP/CFM, depth clamp) plus an explicit separating "social nudge" force scaled by penetration depth | Real but binary - `AvatarToAvatarCollisionsByDefault` toggles a collision-mask bit (`CollisionFilterGroups`/`BulletSimData.CollisionTypeMasks`) so avatars phase through each other entirely; no graduated push/nudge force | **Not built, but the infrastructure already exists**: `JoltPhysicsBackend.cs` has a real per-body `ObjectLayer`/`ObjectLayerPairFilterTable` collision-response filter (starts all-pairs-disabled, selectively enables per layer) - architecturally the same shape as BulletSim's mask toggle, just never wired to an avatar-vs-avatar layer |

  **Portability read**: every category above that's "real" in ubODE/
  BulletSim is, at its core, generic C# scalar/vector math (gravity
  scaling, sine waves over `(x,y,t)`, velocity-proportional damping,
  a material lookup table) - directly reusable against Jolt's own
  `ApplyForce`/`SetGravityFactor`/`SetBodyFriction`/`SetBodyRestitution`
  primitives, which already exist in `JoltPhysicsBackend.cs` and are
  already called from a few places (vehicle friction, avatar ground
  hold). The only genuinely engine-specific piece each time is the
  last mile - BulletSim/ubODE's contact-joint softening (`soft_erp`/
  `soft_cfm`, ODE/Bullet-specific constraint-solver concepts) has no
  1:1 Jolt equivalent and would need re-expressing against Jolt's own
  contact/material API, not a direct port. **Not started** - this is
  a real, evidence-based backlog (general buoyancy and material/
  friction wiring look like the highest-value, lowest-risk starting
  points - both just need scripted values to actually reach
  `SetBodyFriction`/`SetBodyRestitution`/`SetGravityFactor`, which
  already exist), held pending a priority call, same as the disk-cache
  idea above.

  **Provenance correction, same pass**: the boat wave-response code in
  both `BSDynamics.cs` (BulletSim) and `ODEDynamics.cs`/`ODEScene.cs`
  (ubODE) is NOT stock upstream OpenSimulator - confirmed via `git log
  -S "BoatWaveHeight1"`, both engines' wave math was added together by
  `GuntharDeNiro` in two commits (`9db8b8a27c`/`1e50d92bc0`,
  2026-05-24). `FEATURES.md`'s "BulletSim... included as-is" line
  (written earlier this same session) was wrong on this point -
  corrected. Initially looked like it contradicted the "gunthar's
  unported dual-engine physics-tuning cluster" framing elsewhere in
  this file - resolved, not a real discrepancy: `git merge-base
  --is-ancestor` confirms `9db8b8a27c`/`1e50d92bc0` genuinely are
  merged into this branch, while `032b56cada` (explicitly cited in
  `casperia-fork-review-status` memory as part of the still-unported
  ~50+ commit cluster) is confirmed NOT an ancestor of HEAD - still
  sitting only on the gunthar remote. So: an earlier, smaller piece of
  gunthar's wave-response work made it in already; the larger, later
  cluster (buoyancy, contact damping, rolling resistance, near-rest
  sleep, avatar-avatar soft collisions) genuinely has not - the
  existing "held pending direction" status for that cluster stands
  unchanged, just now with the boundary between merged and unmerged
  actually confirmed rather than assumed.
- **Legion-Grid-Code `slua-tier2-tables` review: CLOSED, fully sampled
  (2026-09-08).** The ~115 commits left uncharacterized after the
  Experience (23 commits) and LegionJolt (~65 commits, above) clusters
  were pulled out have now all been checked against Confluence's
  actual current code. Real bugs found and fixed across several
  entries in PROJECT_LOG.md: a vehicle border-crossing bounce loop, a
  DisplayNames clear-throttle bug plus a deeper DisplayNames
  persistence gap in Confluence's own independently-built code, ~16
  LAND/ESTATE bugs (group-power bypasses, missing root-agent guards on
  a money-moving handler and 11 others, missing-return NRE/permission-
  bypass bugs, loop-variable bugs, a data-integrity flag bug), a real
  MySQL data-integrity bug (`StorePrimInventory`'s unprotected
  DELETE-then-INSERT could destroy already-persisted prim inventory on
  a crash - now transactional with a kill-switch and retry-on-failure
  semantics), and a real multi-region console-command bug (`debug
  eq`/`debug attachments log`/several estate commands firing once per
  region on invocation). Search/Classifieds and DirectDelivery were
  checked and confirmed already superseded by Confluence's own more
  mature, independently-built implementations - not porting targets.
  Terrain-gen tooling and a new inbound-email-IMAP capability were
  identified but are out of scope for a bug-porting pass (external
  tooling / a genuinely new feature needing an operator decision,
  respectively) - available on request. See `casperia-fork-review-status.md`
  memory and PROJECT_LOG.md for the full trail.
- **wolfvoice** (`wolfsoftwaresystemsltd/wolfvoice`) — an alternative
  WebRTC voice backend for the already-merged `os-webrtc-janus` addon
  (see "WebRTC voice" below), offering per-listener spatial audio
  mixing without needing a separate Janus gateway server, and claiming
  zero client-side configuration for Firestorm 7.1.10+. Not yet
  evaluated for actual maturity/completeness — only the README has been
  read so far.
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

- **JPEG2000 texture decoder default: CSJ2K vs. OpenJPEG — benchmarked
  for real, keeping CSJ2K (2026-09-05).** WhiteCore-Dev's own timing
  comparison found their native OpenJPEG decoder considerably faster
  than the managed CSJ2K one and switched their default accordingly -
  worth checking directly rather than trusting another fork's number,
  since this exact default has a real history of native-binding
  platform stability problems across the OpenSim ecosystem. Built a
  real benchmark harness against 7 actual texture assets pulled
  straight from the live `casperia` database (153B-524KB, spanning the
  real size distribution), timing both decoders on the two real code
  paths `J2KDecoderModule.cs` actually uses, with a warm-up pass so
  OpenJPEG's one-time native-library-load cost didn't unfairly bias its
  first measurement. **Result: the reverse of WhiteCore's finding for
  this codebase's actual dominant workload.** `DoJ2KDecode` (layer-
  boundary extraction - called on essentially every texture a client
  requests) is CSJ2K 3.9ms/asset vs. OpenJPEG 79.6ms/asset - CSJ2K is
  **~20x faster**, not slower. OpenJPEG only wins on the much-less-
  frequently-called full pixel decode (`DecodeToImage`: CSJ2K 169.9ms
  vs. OpenJPEG 75.8ms, ~2.2x). Root cause found, not just observed:
  OpenJPEG's "layer boundaries" call costs almost exactly the same as
  its own full decode (79.6ms vs 75.8ms) - it does a full decode
  internally either way - while CSJ2K's layer-boundary call is ~43x
  cheaper than its own full decode (3.9ms vs 169.9ms), meaning it has a
  genuinely lightweight codestream-marker-only path OpenJPEG's API
  doesn't expose. WhiteCore's own measurement was almost certainly
  taken on a full-decode-dominated workload, which doesn't match this
  project's actual usage pattern. Flipping the default would make the
  single most common texture operation on this grid ~20x slower - a
  real regression, not the improvement it looked like on paper. Not
  revisited unless CSJ2K's own layer-boundary path is ever shown to
  regress, or the dominant workload shape changes.

- **Phlox script engine / SLua support — investigated and shelved
  (2026-09-05).** The licensing/provenance chain genuinely checks out
  (Halcyon's real engine, `HalcyonGrid/phlox`, Apache 2.0; the
  OpenSim-integration adapter, `HalcyonGrid/halcyon`, BSD; a namespace-
  rename branch, `halcyon/iw_to_hal_scripting`, that lines up cleanly
  against the real core's actual types), but two real, measured
  findings closed this out: **(1)** a real built-and-run benchmark
  (Phlox's actual VM compiled and executed, YEngine's actual production
  compiler pipeline compiled and executed, both verified to produce
  identical output on the same test script) found YEngine roughly
  100-190x *faster* than Phlox on pure arithmetic, not the reverse -
  Phlox is a bytecode interpreter, YEngine JIT-compiles LSL to real
  machine code via `System.Reflection.Emit`, and that architecture gap
  dominates. **(2)** OSSL support in the real Phlox adapter is 0
  functions, not the ~2 an earlier pass estimated - the exact seam that
  would host it (`EngineInterface.GetApi()`) is a literal
  `throw new NotImplementedException()`. Building out this project's
  ~300 OSSL functions from scratch against Phlox's own API shape, plus
  bridging Phlox's own bytecode-snapshot state format against this
  project's XML-state contract (used by OAR/IAR export and HG
  teleport), was estimated at 3-6+ months of focused work - and would
  still ship an engine slower than what's already here. SLua doesn't
  exist anywhere in the real upstream Halcyon/Phlox lineage either (a
  from-scratch language-frontend project, unrelated to any Phlox port).

  **Reopened and re-held, 2026-09-09** - the "not revisited" call above
  turned out wrong on its own terms: several active OpenSim forks
  (including Tranquillity, this project's closest peer) have since
  added real Phlox integrations, correcting the "dead end" framing.
  Re-investigated Tranquillity's actual `#182 Add Phlox: LSL/SLua
  compiler, VM, and region script engine` PR and its `PhloxExperienceAdapter`;
  reported honestly rather than re-closing or overselling. Held again
  at the operator's call pending a priority decision, not rejected -
  see `PROJECT_LOG.md`'s "Jolt readiness re-check... and Phlox held"
  entry.

  **Watch for the same class of bug LegionJolt had, if this resumes**:
  Jolt's own integration shipped with a real, confirmed gap - its
  async request-asset delegate was wired but never called, so any
  prim whose mesh asset wasn't cached yet at physics-actor-creation
  time stayed permanently stuck on a bounding-box fallback instead of
  self-healing once the fetch completed (found and fixed 2026-09-09,
  full detail in `PROJECT_LOG.md`). Phlox wouldn't hit that exact bug
  (no meshing involved), but the same category of risk applies to any
  newly-ported adapter that depends on OpenSim's async asset-fetch
  path - here, script source/bytecode assets rather than sculpt
  textures. Check explicitly whether Phlox's own adapter correctly
  retries/rebuilds after an async asset fetch completes, or silently
  accepts a failed/empty state, before calling any future Phlox
  integration done.
- **`osPlaySoundURL`** (play audio from an arbitrary external URL,
  ported from Legion-Grid-Code). Built, deployed, and tested live on
  2026-08-30/31 - reproducibly hung the calling script's execution
  (isolated to that one script, not a region-wide freeze) somewhere in
  a handful of lines of pure synchronous, in-memory code with no
  identifiable blocking call after hours of live troubleshooting (DNS
  resolution ruled out twice via two different methods, the permission
  check confirmed passing, constructor/HttpClient setup confirmed
  completing). Root cause never found. Fully removed rather than left
  disabled-and-broken - see `PROJECT_LOG.md`'s "osPlaySoundURL" entries
  for the complete diagnostic trail before attempting this again;
  don't re-port it blind.
- **Self-service OAR/IAR restore through the web UI** (upload a file
  and have it applied). Backup/save through the web UI is supported;
  restore is not, and is done from the region's own console instead.
  No OpenSim web UI this project has checked offers browser-based
  restore either — a large upload relayed through a typical reverse
  proxy runs into body-size and timeout limits that don't have a clean
  application-level fix.
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

  **Direct comparison done (2026-09-08), not just an architecture-
  incompatibility call taken on faith.** Checked Gunthar's actual
  `IsScriptExperienceTrusted()` (`LSL_Api.cs`): it's a single,
  grid-wide, config-driven trust list - one experience ID/name total
  (`[ScriptExperiences]`), a static `TrustedOwners`/`TrustedObjects`
  UUID allowlist plus an estate-manager toggle, granting a fixed
  script-permission bitmask. No database, no multi-experience concept,
  no acquisition workflow, no fee - a permission shortcut, not an
  experience marketplace.

  **Real finding, not previously established: Tranquillity's own
  Experience system is NOT related to Gunthar's at all - it's modeled
  on Legion-Grid-Code's separate, fuller system instead**, confirmed
  directly in Tranquillity's own source comment
  (`Source/OpenSim.Region.ClientStack.LindenCaps/ExperienceModule.cs:41-44`,
  authored by Mike Dickson): *"name + values + default as Legion
  ([Experience] ExperienceCreators) so operators moving between Legion
  and Tranquillity see the same knob."* Legion-Grid-Code has its own
  `CanAcquireExperience` (`CoreModules/Experience/ExperienceModule.cs:778`)
  that Tranquillity's role-based switch (`Anyone`/`AdminsOnly`/
  `EstateManagersAndRegionOwners`, config key `ExperienceCreators`)
  directly matches. So the earlier PROJECT_LOG comparison ("Confluence's
  CanCreateExperience vs. Tranquillity's simpler role-only gate") was
  never actually a comparison against Gunthar's design at all - three
  genuinely separate lineages exist in this ecosystem, not one.

  The real three-way comparison: Confluence's `CanCreateExperience`
  uses a per-resident count cap (`m_maxExperiencesPerResident`) plus an
  `IMoneyModule`-charged creation fee; Legion/Tranquillity share a
  role-based switch with no per-resident limit or cost; Gunthar's is a
  static, admin-config-only allowlist with no creation concept at all.
  Confluence's is the only one of the three with both a per-resident
  limit and a real economic cost attached - a genuine, evidence-backed
  "better than" claim now, not an assumed one.
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
- **Deeper pass, 2026-09-09** (1007-commit history read for ideas, not
  diffed for commits — still C++, still nothing portable): a real
  architectural decision worth knowing about, "ADR 0036" — presenting
  a rectangular varregion as a set of square "facets" for crossing/
  presentation purposes, each with its own CAPS seed, re-announced to
  the viewer once per facet rather than once per region. Directly
  relevant background if Confluence's own varregion-tiling design ever
  gets revisited (see the existing "resolve varregion tiling" note
  under Halcyon above). Also: per-outfit live re-baking (bake what a
  resident is actually wearing right now, not a shared cached bake
  across every wearer of similar items) as a real, verified-live
  refinement to appearance baking; and bounding every outbound region-
  to-grid HTTP call so one unresponsive peer can't wedge the whole
  region's own thread — a real operational-hardening pattern worth
  spot-checking against Confluence's own outbound HTTP timeouts
  somewhere down the line, not confirmed either way here. One
  candidate directly checked and ruled out: Homeworldz's own commit
  history shows it once had the "capability advertised but never
  actually authorized/routed" bug for its ServerReleaseNotes-equivalent
  capability — checked Confluence's real `ServerReleaseNotesModule.cs`
  directly, it registers AND redirects correctly, not the same bug
  (also already independently hardened earlier in this project's own
  Tranquillity-review pass, for a different bug in the same module).

**From Aurora-Sim** (`aurora-sim/Aurora-Sim` — dead since 2014-01-13,
20,692-commit history, zero shared git ancestry with either Confluence
or its own real architectural successor WhiteCore-Dev, so no diff-based
review was possible; audited 2026-09-09 via a representative sample,
same low-yield conclusion as `opensim-lickx`):
- A "seamless reconnect after region restart" feature
  (`InworldRestartSerializer.cs`, 2013, author Revolution Smythe — the
  same person behind the already-reviewed, confirmed-redundant
  `halcyon-revsmythe` fork): serializes which agents/circuits were
  connected before a region restart and restores that UDP circuit/agent
  state once the region comes back up, so residents don't get
  disconnected or need to relog. Touches the LLUDP server, `Scene`, and
  `SceneManager` layers deeply enough — and is old/architecturally
  divergent enough (2013-era, pre-.NET-Core, different namespace root)
  — that porting it would mean a ground-up reimplementation against
  Confluence's own current networking stack, not a mechanical port.
  Genuinely interesting if region-restart UX is ever prioritized, not
  attempted here.

**From gOSWI** (`GwynethLlewelyn/goswi` — a real, maintained, unrelated
Go-language standalone grid-admin webapp, audited 2026-09-09 at the
user's request for end-user/admin feature ideas, not a fork to port
commits from). Two concrete wins already built and shipped — see
PROJECT_LOG.md's "gOSWI feature audit: closed" entry for the full
trail, including a real bug the JSON grid-stats work caught live
(`OSDParser.SerializeJsonString` silently drops zero-valued integer
fields — worth checking any other call site emitting a JSON number
that could legitimately be zero). Two ideas remain open, not
forgotten:
- **A federated Libravatar server** — a resident's in-world profile
  picture becomes their real internet-wide avatar on any
  Libravatar-aware service, via DNS SRV records pointed at a small
  local server. The most novel idea from this audit, but it's an
  infrastructure decision (real DNS changes), not just code — needs a
  deliberate go-ahead, not a default build.
- **Web-based avatar profile editing** (About/First-Life/Skills/"Want
  To" as an editable form, gOSWI's approach). Confluence's `/profile`
  page is deliberately read-only by design (a comment in the handler
  says so explicitly) — this is a candidate to *reconsider* that
  choice, not a gap that was missed.

## Known limitations

- **WebRTC voice** (`OpenSim/Addons/os-webrtc-janus`) is real, merged
  code (5,471 lines, builds into the solution, has a real
  `.ini.example`) — not documented in `FEATURES.md` as a working
  feature because it has not been end-to-end tested with a real client
  actually connecting to voice through Janus. Code existing and
  compiling is not the same claim as confirmed working; treat as
  present-but-unverified until someone actually tests it. Separately,
  this project's copy was believed to have drifted behind the real
  upstream (`Misterblue/os-webrtc-janus`) by 52-54 commits - a full
  file-tree reconciliation (no shared git history with upstream, so
  this meant a direct diff rather than a commit-log review; see
  `PROJECT_LOG.md` for the normalize-then-diff methodology) found that
  framing was wrong. Across all 8 addon files, only two real gaps
  turned up and both are now fixed: long-poll HTTP cancellation was
  fully commented out in `JanusSession.cs`, and a `long`-value OSD
  parsing helper (`OSDToLong`) was missing from `JanusMessages.cs`,
  affecting six numeric-ID call sites (session_id, room/handle id,
  error code). Everything else checked - the double-destroy guard,
  session/handle ID handling, and substantial independent hardening in
  `JanusViewerSession.cs` (a thread-safe disconnect-once guard, a
  provisioning semaphore), `JanusRoom.cs` (a real "already in room"
  error-recovery/retry mechanism upstream has no equivalent of),
  `JanusAudioBridge.cs`, and `WebRtcJanusService.cs` (68% more methods
  than upstream) - shows Confluence's copy is ahead of upstream, not
  behind it. One separate, non-reconciliation finding was flagged but
  not fixed: `WebRtcJanusService.cs` has a few more `.AsLong()` call
  sites with the same OSD-parsing bug pattern, but upstream has the
  identical bug at some of the same spots - not something to "catch up
  on," a latent bug neither codebase has fixed yet.

  **Four more real gaps found and fixed (2026-09-08)**, this time via a
  different Wolf Software Systems Ltd fork
  (`wolfsoftwaresystemsltd/os-webrtc-janus` - the maintainer, Mike
  Dickson, is also a confirmed core Tranquillity developer; see
  ROADMAP.md's fork-ecosystem context) rather than the real upstream -
  checked directly against Confluence's own code, not assumed missing:
  STUN servers were never advertised at all (added via
  `ISimulatorFeaturesModule.AddFeature("VoiceStunServers", ...)`,
  config-gated, off by default); `JanusMessages.cs` was missing
  `OSDToString`/`OSDMapToStringMap`/`PluginRespDataList`/
  `AudioBridgeListRoomsResp`/`AudioBridgeListParticipantsResp` (only
  `OSDToLong` existed, from the earlier reconciliation - also added a
  null-check `OSDToLong` itself was missing); and `ChatSessionRequest`
  had three real bugs found by a different contributor (CodeWolf, same
  org) diagnosing text-IM breakage on a large production grid: `start
  p2p voice` fell back to `UUID.Random()` when `params` was absent,
  silently splitting the conversation (the viewer re-keys its floater
  to whatever id is returned, but inbound IM traffic stays filed under
  the real XOR-of-agent-ids) - now refuses the request instead; `start
  conference` returned a bare OK, leaving the viewer to wait out its
  full 30s timeout before reporting anything - now fails fast with a
  real `strings.xml` error key; and `accept invitation`/`call`/
  `invite`/`mute update`/`session update` all fell to a `400 Bad
  Request` the viewer doesn't actually expect - now answer `OK` like
  the other no-op methods, matching upstream's model of only gating on
  what actually blocks session init. `LoginResponse`-based STUN
  advertisement (the same fork's `feature/loginResponseAdds`) was
  deliberately NOT ported - it needs a real `ILoginService.OnLoginResponse`
  event this codebase's `ILoginService` interface doesn't have at all
  (a plain `Login()` call, no event, no `AddAdditionalData`), a bigger,
  separate change to the core login contract, not a same-pass addon
  fix. Build confirmed clean. Still present-but-unverified overall -
  none of this changes that a real client has never connected through
  this addon end to end.

  **Real, durable reason this stays unverified on Casperia
  specifically, per the operator (2026-09-08): the Janus Gateway
  backend this addon talks to requires Linux**, and this grid runs on
  Windows - not a "haven't gotten to it yet" gap but an actual
  infrastructure constraint, matching the operator's own stated
  standard for what's worth carrying (native Windows only, no
  VirtualBox/Docker/WSL2). End-to-end verification here would need
  either a separate Linux host running Janus that this Windows grid
  connects to over the network, or the operator's own future call to
  stand up Linux infrastructure for it - not something to keep chasing
  as if it's just an untested checkbox.
- **Aurora** (`OpenSimWeather`'s northern-lights effect) is built and
  deployed but not yet visually confirmed working in a live viewer —
  same present-but-unverified caveat as WebRTC voice above.
- Some database migrations remain MySQL-specific in places PostgreSQL/
  SQLite parity hasn't caught up yet.
- Experience Tools is not full Second Life Experience-service
  compatibility.
- `llOpenFloater` isn't implemented — OpenSimulator has no
  viewer-hosted floater service to back it.
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

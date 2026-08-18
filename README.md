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
a later phase. That's most of what's new
below.

## Project status

| Item | Status |
|---|---|
| GitHub home | [Ramius1701/OpenSim-Confluence](https://github.com/Ramius1701/OpenSim-Confluence) |
| Upstream baseline | `origin/master` (`opensim/opensim`), merged in as of the latest commit |
| Active integration branch | `merge-experiment` (also the repo's default branch) — this is where all current work lives |
| Windows build | Successful — full solution build verified clean (0 errors) as of the latest commit |
| GitHub Actions | `.github/workflows/msbuildnet.yml` present; not yet exercised now that the repo is pushed |
| Web/Admin UI | Extensively live-verified against a running Robust instance (real HTTP round-trips: login, admin actions, database writes confirmed) — see the note on testing scope below |
| In-world/viewer testing | Live-verified with a real viewer (Firestorm) against a running region: login, teleport between regions, weather rendering, and a full end-to-end currency/land-purchase transaction all confirmed working over an extended live session. An intermittent region-startup hang was tracked as an open blocker for a period (see PROJECT_LOG.md) but has not recurred across many region starts/restarts since, including a full session's worth on 2026-08-16 and again on 2026-08-18 (region processes appeared briefly idle at startup with no open listener ports, but both came up clean within about a minute with no restart needed) — its root cause was never conclusively identified, so treat it as currently non-blocking rather than definitively fixed. |

**On "tested" vs. "compiled":** these are two different claims and this
README used to conflate them. The Web/Admin UI runs on Robust alone and
has been exercised repeatedly with real curl-driven HTTP sessions —
login, admin actions, and database state changes confirmed, not just a
clean build. Region startup and basic in-world presence (login,
teleport, weather, a real currency/land-purchase round-trip) are now
also live-verified against a real viewer, not just build-verified. That
said, live verification of *specific* LSL/OSSL functions, Experience
Tools behavior, and the physics/environment tuning work individually is
still narrower than "the region runs" — don't read general region
stability as proof every scripting function or physics change has been
exercised in-world; those remain build-verified only unless a
PROJECT_LOG entry says otherwise for that specific piece.

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

## Web & Admin UI

A native, Robust-hosted grid portal (`WebInterfaceServiceConnector.cs`) —
not an addon-module, and not a replacement for the optional
`OpenSim-Grid-Interface` PHP site, which remains available as a
swappable alternative. Session-based auth against real grid accounts
(`IAuthenticationService`, MD5-hashed to match real viewer behavior).

**Public pages:** home/splash with live grid stats, grid-wide search
(People/Places/Events/Classifieds/Groups, plus a dedicated Land for
Sale page with size buckets, real per-region maturity filtering, and
trending/autocomplete), a dependency-free world map, a viewer download
page, a live grid-capability "Features" page, guest support tickets,
admin-managed static pages (About/ToS/DMCA) and news/events feeds.

**Resident self-service:** dashboard, public profile (with group
memberships and regions-owned, both privacy/visibility aware), friends
list, a full partner proposal flow (propose/accept/decline/cancel/
breakup, with real reciprocal database writes), transaction history,
classifieds/events management, region management for estate owners
(OAR backup, and full estate settings/access-list editing for any
resident who owns one — not just admins), inventory backup (IAR),
account changes (password/email), and self-service account deletion.
Backup (save) only, by design — see "Known limitations."

**Admin console:** user management (search, create, edit, ban with
optional auto-expiry, soft-delete, kick/message an online resident
through a secured region console channel, admin-set password reset,
login-as-user for support), estate management (create estates, edit
settings, and manage managers/access/ban/group lists — the same
primitives the self-service page above uses), grid-wide group
oversight (list every group, moderate visibility/enrollment flags,
delete a group), abuse report review, financial/transaction reporting,
grid statistics, static page and news/events content management, grid
settings, a web-based region console, per-region Hypergrid open/close
toggling, and on-demand map-tile regeneration.

Full route-by-route detail, every bug found and fixed along the way,
and the live-verification trail for each piece live in PROJECT_LOG.md
and FEATURES_VS_MASTER.md.

## Native economy, search, and grid services

Services under `OpenSim/Services/*`, backed by MySQL/PostgreSQL/SQLite
data layers, replacing what would otherwise be external dependencies:

- **Currency Service** — **in-world virtual currency only**, not a
  real-world payment/financial service. A native `IMoneyModule`
  implementation (`ConfluenceCurrencyModule`) that can serve as the
  default economy instead of requiring Gloebit, MoneyServer, or a
  third-party service, with the same ledger backing the Web UI's
  transaction reporting. The same "in-world currency, not a financial
  product" scope applies equally to the classic MoneyServer integration
  described under "MoneyServer enhancements" below — whichever one a
  grid owner enables, both are strictly in-world virtual currency
  systems.
- **Search Service** — treated as **core, not an optional add-on**: a
  working grid search is baseline functionality every grid owner needs,
  the way it always should have been in OpenSim rather than something
  requiring a separately-deployed external server. Search has been a
  real, recurring pain point for OpenSim users since the platform's
  inception — reliant by default on an external XML-RPC server most
  grid owners never stand up, leaving Directory search silently empty
  out of the box. A native `ISearchModule` (`ConfluenceSearchModule`)
  answers both the in-world
  Directory floater (People/Places/Events/Classifieds/Groups tabs) and
  the Web UI's search pages from the same backend, including
  trending-query tracking and autocomplete. The addon-modules
  `OpenSimSearch` client still exists for anyone who specifically wants
  to point at a separately-deployed compatible search server instead,
  but that's a choice of *which* backend answers search, not whether
  search exists at all.
- **Events, News, Grid Settings, Static Page, and Support Ticket
  services** — the content and configuration backends the Web UI's
  admin console manages.

## Moderation and access control

- Temporary/timed account bans self-clear on their own once expired,
  regardless of which login path a resident uses — the real
  grid/viewer login (`LLLoginService`) and the web dashboard/admin
  login both check and clear an expired ban the same way
  (`AccountBanHelper`, shared between them), rather than only the web
  paths self-clearing while a resident who never touches the web UI
  stayed blocked past their ban's expiry until an admin manually
  unbanned them. Live-verified via a real login attempt through the
  actual `LLLoginService` XML-RPC path.
- Unbanning an account (whether by the admin button or by expiry)
  restores whatever level it actually had before the ban, not a flat
  0 — an estate manager or grid admin who gets banned keeps their
  elevation back on unban rather than being silently downgraded to an
  ordinary account. Fixed after a real incident during this feature's
  own testing; see PROJECT_LOG.md for the full account.
- Deleting a group now actually cleans up after itself — membership,
  role, and role-membership rows, plus any resident's dangling
  `ActiveGroupID` reference, all get removed along with the group
  record. Previously only the group's own row was deleted, silently
  orphaning everything else in the other six `os_groups_*` tables.
  Found and fixed while live-verifying the grid-wide admin Groups
  page against a real test group (MySQL and PGSQL both fixed; no
  SQLite groups backend exists, in this repo or upstream).
- Grid-wide viewer ban, by IP range and client signature.
- Sim protection: opt-in FPS auto-mitigation under load.
- On-demand/soft-start regions.
- A secured web-based region console channel (shared-secret gated),
  used by the admin UI's Kick/Message and free-form console features.
- A native mute-list service (`MuteListModule`/`IMuteListService`),
  answering the same viewer protocol (`MuteListRequest`/
  `MuteListUpdate`) real Second Life uses. This has been genuine stock
  OpenSimulator code since 2009, just historically left incomplete — the
  `OpenSimMutelist` addon (an external-service workaround from that
  earlier era) was removed from this repo once confirmed redundant with
  the native path, rather than kept as unnecessary dead weight.

## Display Names and identity

- Viewer-compatible Display Names for local users.
- Display Name CAPS and viewer protocol handling.
- Display Name storage and account-service integration, including a fix
  so display names survive region restarts and cross-sim hops.
- Hypergrid Display Name lookup and federation.
- Single-name and `username` login handling.
- Terms-of-service acceptance during login.
- Stale Hypergrid identity self-repair: local login and outbound
  Hypergrid launches correct a *stale* (not just missing)
  HomeURI/GatekeeperURI against the canonical configured value, live-
  verified with a real before/after test — a resident whose stored
  identity still pointed at an old IP correctly self-corrected to the
  real hostname on their next login. Directly relevant since this kind
  of deployment's hostname is often dynamic DNS, which can otherwise
  leave a resident's identity stale indefinitely after the IP changes.
  Plus a `repair user service urls <first> <last> [<home-uri>]` admin
  console command for accounts that haven't logged in since.

**Not yet implemented** (referenced in older project docs, verified absent
by direct code search): RSA-key login authentication — a real Mobius
feature, ported from a login-RPC challenge/response scheme aimed at
LibOMV-based bots/proxies rather than stock viewers; no known client
in this project's own stack actually speaks it, so it's logged rather
than started pending confirmation it's worth building. See "Progress
and roadmap." (`InternalPort = MATCHING` region configuration, the
other item formerly listed here, has since been ported — see
PROJECT_LOG.md.)

### Abuse Reports

Treated as **core, not an optional add-on** — the same standing as
Search above. A working abuse-report pipeline is baseline functionality
every grid needs and should have shipped in stock OpenSimulator from
the start; it never did.

- Viewer Abuse Reports CAPS.
- Local and remote service connectors.
- Robust handlers.
- MySQL, PostgreSQL, and SQLite storage and migrations (PGSQL/SQLite
  parity added on top of the original MySQL-only implementation).
- Region-side submission support.

## Scripting: LSL and OSSL

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
  key-value encoding, KHR texture transforms). `PRIM_GLTF_BASE_COLOR`/
  `_NORMAL`/`_METALLIC_ROUGHNESS`/`_EMISSIVE` are wired into both
  directions now — `llSetPrimitiveParams`/`llSetLinkPrimitiveParamsFast`
  and `llGetPrimitiveParams`/`llGetLinkPrimitiveParams` (build-verified;
  see PROJECT_LOG.md). The read side reflects the per-face override
  data a script itself set, not the assigned base material merged
  underneath it — a real, smaller scope than full SL parity, logged as
  such rather than silently claimed as complete.
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
  (paid through whichever `IMoneyModule` is active) and a configurable
  per-resident cap.
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

### Bot/NPC framework

A management framework (`IBotManager`/`BotManager`/
`BotPersistenceManager`, ported from Tranquillity) for scripted bots,
fully reachable from LSL/OSSL scripts via a `bot*` function set mirroring
Tranquillity's own (58 functions: lifecycle, movement/navigation,
chat/IM/interaction/animation, tagging, persistence, profile/outfits,
and bot-hosted sensors/comms), wired into Confluence's own
`OSSL_Api.cs`/`IOSSL_Api.cs`/`OSSL_Stub.cs` so YEngine scripts can call
it directly. Live-verified in-world (2026-08-18): a real bot created,
tagged, given a profile, spoken through — its chat line actually
appeared in local chat — sensed a nearby avatar, and cleanly removed,
all through real script calls with zero server-side errors. Outfits,
persistence, animation, item-giving, and multi-waypoint navigation are
build-verified only so far — see PROJECT_LOG.md for the full batch
breakdown and exactly what was and wasn't exercised. Separately,
avatar-follow and tag-group management remain available via `osNpc` too.

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

## World and environment

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
- **GroupAutoInvite** — configurable automatic group invitations on
  arrival. Verified against Gunthar's real vanilla source
  (`OpenSim/Region/OptionalModules/Avatar/GroupAutoInvite`) — a genuine
  port, not an invented/misattributed module, with reasonable
  adaptations for this repo's addon-module wiring plus a real
  robustness fix (matches invites to the specific login session that
  triggered them, so a stale delayed invite can't fire against a
  since-relogged session).
- **HoloPhysicsGuard** — reduces idle physics load when regions are empty.
- **OpenSimMarketplace** — portable Direct Delivery marketplace system.
- **OpenSimSearch** — external viewer search client (the native Search
  Service above is the default; this remains for anyone who wants to
  point at a separately-deployed compatible search server instead).
- **OpenSimTide** — configurable tide and water-level simulation.
- **OpenSimWeather** — rain, snow, storms, lightning, thunder, wind,
  clouds; see "Weather" above.
- **RegionWeb** — per-region web pages, protected estate administration,
  an in-world LSL/OSSL compatibility reference (auto-discovered from the
  script API plus hand-written notes), and its own avatar wallet portal
  (balance/statement, token purchases, admin dashboard, PayPal donations).

**Removed: RegionCurrency.** It duplicated RegionWeb's own `/currency`
wallet exactly — not by design, but because it turned out to be RegionWeb's
own currency/PayPal code, mechanically split out to its own base path by
an earlier AI-assisted session (confirmed directly from its own code: a
comment on its HTTP entry point read "RegionCurrency now owns its whole
path rather than living under RegionWeb's `/regionweb/currency/` as it
did *in the source project*", and its default storage paths/session
cookie/admin-check method were still literally named after RegionWeb,
never renamed). No unique capability of its own, so removed rather than
reconciled — see PROJECT_LOG.md for the full writeup, including a real
bug the reconciliation work found: both wallets' buy/transfer/admin
balance actions were silently broken (a reflection-based bridge to
`IMoneyModule` methods no real money module in this repo ever
implemented), fixed by calling `ICurrencyService` directly instead.
PayPal donations (RegionWeb's own integration) are real money changing
hands, so they're treated as a straight donation rather than a currency
purchase for now — no token credit, no promised exchange rate — pending
a real decision on directly selling in-world currency for cash.

Detailed Marketplace documentation is located at:

```text
addon-modules/OpenSimMarketplace/README.md
```

For every in-world chat command available to avatars/estate managers across
the whole repo (not just add-ons), see [`INWORLD_COMMANDS.md`](INWORLD_COMMANDS.md).

## MoneyServer enhancements

**In-world virtual currency only**, same scope note as the native
Currency Service above — this is not a real-world payment or financial
service, just an alternate `IMoneyModule` implementation for grid
owners who prefer it over the native one.

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

- An intermittent region-startup hang was tracked here as the top
  blocker for a period — as of 2026-08-16, it has not recurred across
  an extended live session's worth of region starts/restarts (real
  viewer login, teleport, weather, and a full currency/land-purchase
  transaction all confirmed working), so it's no longer treated as
  actively blocking. Its root cause was never conclusively identified
  though, so this isn't a confirmed fix - just an update on current
  observed behavior. Watch for recurrence rather than assuming it's
  gone for good.
- True hard account deletion isn't implemented (`IUserAccountService`
  has no `Delete` method, and removing the row directly would orphan
  Inventory/Groups/Grid/Presence/Currency/Estate references) —
  soft-delete (scrambled password + blocked login) covers the
  practical need instead.
- RSA-key login authentication (a real Mobius feature) is not
  implemented — logged rather than started, since no known client in
  this project's own stack speaks the login-RPC protocol it needs;
  see the Display Names and identity section above.
- **Real PBR terrain support** — genuinely unclaimed territory, verified
  rather than assumed, and now built. Object-level PBR materials
  (per-face `gltf_json` overrides via the `RenderMaterials` capability)
  already worked — inherited from real upstream OpenSim. The gap was the
  `"ModifyRegion"` capability real PBR terrain editing needs, absent
  from this repo's own merged-upstream tree, Gunthar's fork, and
  Tranquillity alike. Turned out to be smaller than first scoped once
  Firestorm's own source (`llpbrterrainfeatures.cpp`) was checked
  directly: `ModifyRegion` only carries the per-slot glTF *override*
  layer (tiling/scale/rotation/offset) — which glTF material occupies
  each of the region's 4 terrain slots is a separate mechanism
  (`RegionSettings.TerrainPBR1-4`) already fully inherited and working
  end to end (DB, OAR, region-handshake delivery to PBR-capable
  viewers) — this repo just never had anything answering the capability
  itself. Implemented as `ModifyRegionModule.cs`
  (`OpenSim/Region/CoreModules/World/Terrain/`), following the same
  `OnRegisterCaps`/`RegisterSimpleHandler` pattern the working
  `RenderMaterials` capability already uses, storing the 4 override
  blobs verbatim (the server never interprets them) in a new
  `RegionSettings.TerrainPBROverrides` field with matching SQLite/
  MySQL/PGSQL migrations and OAR round-trip. Deployed; the grid has
  restarted cleanly with it in place (twice, no errors either time).
  What's still outstanding is testing the capability against a real
  PBR-capable viewer with actual glTF material assets — the grid owner
  doesn't have any uploaded yet, so this is genuinely untested against
  real content, not just "pending a restart." See PROJECT_LOG.md for
  the full writeup.
- **Firestorm (OPENSIM-build) parity gaps** — a three-way research pass
  cross-referenced Firestorm's own source against this repo, scoped
  specifically to what the `OPENSIM` CMake build flag (and the
  viewer's own `LLGridManager::isInOpenSim()` runtime gating) actually
  ships, not full Second-Life-only Firestorm. Findings, most visible
  first:
  - **Inventory thumbnails** — now built. Per-item/per-folder custom
    thumbnail images (gallery/grid inventory views, outfit gallery).
    Confirmed genuinely absent from real upstream `opensim/opensim`
    too (re-verified directly, not assumed, after a direct question
    about it). Reverse-engineered Firestorm's exact two-phase upload
    protocol from its own source
    (`llfloatersimplesnapshot.cpp`/`llinventorymodel.cpp`) rather than
    guessing. New `InventoryThumbnailUpload` capability module plus DB
    schema/serialization changes across all three backends. AIS3-only
    sub-flows ("set to an existing texture via the picker", "clear
    thumbnail") are a known, documented limitation — AIS3 defaults off
    on OpenSim-flagged builds and there's no legacy-UDP fallback for
    either. See PROJECT_LOG.md for the full writeup.
  - **`AgentProfile`/`UploadAgentProfileImage`** — the single biggest
    remaining gap when first flagged, now mostly closed. Firestorm has
    a full legacy-UDP fallback for every profile *text* field (about,
    first-life text, partner, notes), already served by the existing
    UDP `AvatarPropertiesRequest`/`AvatarPropertiesUpdate` handlers —
    verified directly against Firestorm's own `#ifdef OPENSIM` fallback
    code, nothing needed there. The one piece with no UDP fallback at
    all is profile *photo upload* (`UploadAgentProfileImage`) — built,
    same two-phase shape as inventory thumbnails, directly inside
    `UserProfileModule.cs`. See PROJECT_LOG.md for a real data-loss
    risk this caught and fixed along the way (a naive image-only update
    would have silently wiped the rest of a resident's profile text).
  - **Avatar Picker web-profile link** — was silently broken, now
    fixed. Two independent mismatches against Firestorm's own
    `getProfileURL()`: the login response emitted the wrong key
    (`profile-server-url` instead of the `web_profile_url` Firestorm's
    OpenSim-aware code actually reads), and even if pointed at the
    right page, the query-parameter shape didn't match (`?id=<uuid>`
    vs. the `?name=First.Last` the viewer builds). Both fixed.
  - **`AvatarRenderInfo`** (avatar visual-complexity/jellydoll
    accounting) — built. New `AvatarRenderInfoModule.cs`; server is a
    passive aggregator of viewer-reported complexity weights, same
    posture as `ModifyRegionModule.cs` toward the data it relays.
  - **`GroupAPIv1`** (group bans — no legacy-UDP fallback exists for
    this one operation) — built. Exact protocol re-verified directly
    against `llgroupmgr.cpp`: one shared `GroupAPIv1` cap per agent,
    `group_id` passed as a **query-string parameter** on both GET and
    POST (not in the LLSD body — the one place a copy-paste from the
    inventory-thumbnails/AvatarRenderInfo shape would have been silently
    wrong), `ban_action` 1=create/2=delete. New `BanData` model +
    `os_groups_bans` table (MySQL/PGSQL only — Groups has no SQLite
    backend at all in this codebase), new service-layer methods wired
    through all three connector implementations (Local/Remote/Hypergrid
    — HG bans follow the same local-origin-world-only restriction the
    existing role-management operations already use, resolving the
    scoping pass's one open question by precedent rather than attempting
    cross-grid ban propagation), a join/invite-acceptance check so a
    ban actually blocks re-entry, and the capability itself built inside
    `GroupsModule.cs` (its first capability ever — previously 100%
    UDP-driven). Along the way, gave bans a real server-side permission
    check (`GroupPowers.GroupBanAccess`, via the connector's existing
    `HasPower` helper) that the pre-existing `EjectGroupMemberRequest`
    path still doesn't have (its own `// Todo: Security check?` comment,
    left alone — out of scope to fix here). See PROJECT_LOG.md for the
    full five-layer writeup.
  - `SpatialVoiceModerationRequest` (nearby-voice mute/mute-all).
    Scoped, deliberately not built yet — the LLSD layer is small, but a
    real implementation needs to actually silence someone's live voice
    stream, and no callable mute API was found in the WebRTC/Janus voice
    stack to hang it off of. Logged rather than built shallow.
  - `UserInfo` — confirmed **not actually a gap**: Firestorm keeps a
    full legacy-UDP fallback for this one too, already fully served by
    existing UDP handlers.
  - EEP (Extended Environment Protocol) — confirmed **exact wire-shape
    match**, key-for-key, against `llenvironment.cpp`. No server-side
    gap; any remaining EEP issue is viewer-facing UX, not this codebase.
  - Destination Guide — confirmed **already correct in code**; the
    login response already emits `destination_guide_url` correctly and
    the native `/guide` page already exists and is routed. Purely an
    operational toggle (`DestinationGuide = "${Const|BaseURL}/guide"`
    is documented but commented out by default in
    `bin/Robust.ini.example`) — not a code gap.
  - Confirmed NOT gaps (false positives from the raw, unscoped first
    audit pass, corrected once re-checked against the `OPENSIM` flag
    specifically): `VETPBR`, `ObjectAnimation` — both already work via
    alternate mechanisms already inherited from upstream.
- **SLua** — Second Life's modern Luau-based (Roblox's Lua variant)
  scripting language, in open beta on the SL production grid since
  2025-12-02 ([LL's announcement](https://community.secondlife.com/news/featured-news/announcing-the-slua-open-beta-modern-scripting-comes-to-second-life-r11237/)):
  faster than LSL/Mono, ~50% less memory, native tables, dynamic event
  subscription, multiple timers, coroutines, native JSON — while staying
  compatible with existing LSL knowledge. No evidence found of any
  OpenSim fork having touched this. The investigation pass this bullet
  used to call for is now done: SLua is a genuine separate runtime, not
  a new front-end on the existing script engine — Linden Lab's own
  [source repo](https://github.com/secondlife/slua) describes it as "a
  friendly fork of Luau" with SL-specific state-serialization
  (`Ares`/`Eris`) so scripts survive region crossings and restarts, the
  same problem OpenSim's own script-state persistence solves today for
  LSL. Licensing is clean either way (Luau and SL's fork are both
  MIT), unlike Phlox below, and a real embeddable C++ Luau VM exists
  with early-stage C#/.NET P/Invoke bindings already published
  ([NuLua](https://github.com/nuskey8/NuLua),
  successor to the now-archived
  [luau-dotnet](https://github.com/nuskey8/luau-dotnet)) — meaning a
  from-scratch interpreter isn't the likely path, embedding the real
  VM is. What's still missing and would have to be built from nothing:
  the region-crossing-safe state-serialization layer (SL's own
  equivalent is explicitly non-upstreamable, SL-specific code), a
  bridge exposing the existing LSL/OSSL function surface to Luau
  scripts, and the actual upload/compile capability wiring (undocumented
  even within the SL community - no implementer-facing protocol spec
  exists publicly, only user-facing wiki/FAQ pages). Genuinely
  unclaimed territory in the OpenSim ecosystem - no mailing-list
  thread, RFC, or prototype found anywhere. A real, multi-month-class
  undertaking on its own terms, not a quick win the PBR terrain
  investigation turned out to be - logged, not started, deliberately
  left for the user to decide whether/when to commit to it given the
  scope. See PROJECT_LOG.md for the full research writeup and sources.
- Tranquillity's "Phlox" LSL/SLua script engine (~98,000 lines) is
  audited but NOT ported: it turned out to be a resurrection of
  InWorldz/Halcyon's own closed-source Phlox engine, now appearing as
  source with no LICENSE file and no explained chain of custody. Not
  shelved outright — a provenance question is pending with OpenSim-NGC
  before any engineering investment is considered. See PROJECT_LOG.md
  for the full writeup.
- **Sim border-crossing smoothness** — avatar-crossing latency fix built
  and **live-verified**: crossed a region border in a real Firestorm
  session on Casperia-Dev with no perceptible hitch — the target outcome.
  Traced the actual mechanism directly (not assumed): the reactive,
  one-physics-frame crossing trigger (`ScenePresence.CheckForBorderCrossing()`)
  and the two sequential synchronous network round-trips (`QueryAccess`
  then `UpdateAgent`) that sat on that critical path before the viewer
  was told to render the new region. Cross-checked nine other local
  OpenSim-family checkouts before building anything: Confluence already
  carried a real, working partial fix inherited from GuntharDeNiro's fork
  (velocity-preserving handoff + delayed attachment cleanup — stops the
  classic "avatar stalls dead at the border" symptom, which neither
  upstream nor Tranquillity have); WhiteCore-Dev's older, more diverged
  architecture offered two real ideas worth learning from (a
  wider/adaptive crossing-prediction window, and a `QueryAccess`-free
  simulation-service design that collapses two round-trips to one) —
  both now built here: the crossing trigger's lookahead widened from a
  single physics frame to an adaptive 0.1s/0.2s window (WhiteCore's own
  values, not invented), plus a new short-lived `PreApprovedCrossingCache`
  that a predictive, side-effect-free pre-check warms ahead of the actual
  crossing, letting the real crossing skip the `QueryAccess` round-trip
  entirely on a cache hit (falls back to the original synchronous check
  on a miss — no regression, pure latency win in the common case).
  **Vehicle/prim crossings** — fully scoped in a follow-up pass, real
  root cause confirmed, not yet built. The actual cause: `PhysicsActor.CrossingStart()`
  (traced into the real ubODE implementation Casperia-Dev runs) explicitly
  zeroes the object's velocity and disables its physics body the instant
  a crossing begins, and that freeze holds for the entire synchronous
  `CreateObject` transfer (the whole object — mesh, scripts, inventory —
  not a small payload). This is a deliberate server-side freeze, not
  network jitter or lost data — velocity itself is captured and does
  survive (confirmed via `SceneObjectPart.AddToPhysics()`'s
  `applyDynamics` path), it's just held at zero for however long the
  transfer takes. Also found the real trigger point is narrower and
  safer than first assumed: not the general-purpose `AbsolutePosition`
  setter (every code path's choke point), but `SceneObjectPart.PhysicsRequestingTerseUpdate()`
  — the same kind of purpose-built, physics-only hook the avatar fix
  used, meaning a predictive trigger *is* safely buildable here after
  all. The one piece that's genuinely new engineering, now precisely
  named rather than hand-waved: prims have no equivalent of an avatar's
  deliberately-inert child agent, so predictively pre-transferring a
  live vehicle (scripts included) risks a real duplicate — double script
  execution, double collisions, a visible overlap for nearby viewers.
  Closing that gap needs a new "staged/inert" object state that gets
  promoted to live only at the real crossing commit — a concrete,
  four-part candidate design is in PROJECT_LOG.md, not built in the
  scoping pass itself.
  **One real, safe piece built and deployed since**: `CrossingStart()`
  — the call that freezes a vehicle's physics — used to fire
  unconditionally before the code even checked whether a valid
  destination region existed. Moved it to fire only after a destination
  is confirmed, which shrinks the actual freeze window by the
  destination-lookup time in the normal case, and fixes the
  "no matching `CrossingFailure()`" gap by construction — the two
  failure paths no longer freeze physics at all, so there's nothing
  left to leave stuck near a grid edge. Deliberately scoped small and
  safe: no predictive trigger, no caching, no attempt to shrink the
  dominant `CreateObject` transfer cost — those still need the
  staged/inert object state above. Not the full "feels like avatar
  crossing now" outcome; a real, modest, safe win pending the user's
  own live test.
- **Systemic OpenSim complaints campaign** — a deliberate, ongoing
  effort to work through the community's other long-standing pain
  points (script engine performance, Hypergrid reliability, attachment
  reliability, mesh/physics upload quality, region stability, permission
  weaknesses, ecosystem fragmentation), one at a time, with the same
  rigor as border-crossing above rather than shallow fixes. First one
  done: **avatar baking / "cloud avatar" failures**, the single most
  commonly cited OpenSim complaint. Turned out Confluence already had a
  complete, real recovery mechanism upstream `opensim/opensim` doesn't
  have at all (GuntharDeNiro-authored, same fork already credited for
  the region-crossing fix) — but it had never actually been active on
  Casperia-Dev: the live deployment's `OpenSimDefaults.ini` still had
  the mechanism's original pre-enablement values, silently overriding
  the code's own `true` default the whole time. Fixed that, plus a real
  separate code gap — the safety net explicitly excluded Hypergrid
  arrivals, exactly the scenario most likely to produce a genuine cloud
  avatar. Also surfaced a much bigger, two-directional config-drift
  problem between the repo's tracked `OpenSimDefaults.ini` and the live
  deployment's actual copy (real, undeployed physics tuning sitting in
  the repo; real operator customization sitting only on the live grid)
  — flagged, not resolved, pending the user's own call on how to
  reconcile it. Second one done: **script engine (YEngine) performance**.
  Confirmed YEngine is a real compile-to-CIL engine, not the wrong choice
  — the actual gap is `NumThreadScriptWorkers` defaulting to just 2
  worker threads region-wide, confirmed unset (running at that default)
  on both the repo template and live Casperia-Dev, and confirmed the
  *same* hardcoded default and commented-out line in `OpenSim-Tranquillity`
  and upstream `opensim-master` — an ecosystem-wide blind spot, not a
  Confluence regression. Checked the live host has real headroom (8
  cores / 16 threads, 2 regions), so raised `NumThreadScriptWorkers` to
  `4` on the live deployment only (not the repo's shipped default, which
  stays conservative for arbitrary hardware). Config-only change, needs
  an `OpenSim.exe` restart to take effect; no specific speedup promised.
  A secondary check of sensor/timer scanning (`SensorRepeat.cs`) found
  the per-sensor scene scan is the LSL sensor model's inherent cost, not
  a distinct bug — no code fix needed there. Third one scoped (not yet
  built): **Hypergrid teleport reliability**. Traced the actual outbound
  HG code path and found a real, well-evidenced cause: every HG
  teleport requires a minimum of 3–4 serial, synchronous WAN HTTP calls
  across up to 3 independently-run grids (destination Gatekeeper lookup,
  a login relay through the traveler's *home* grid that itself nests a
  home→destination create-agent and a destination→home verify-client
  call inside a single 30s budget, then a direct update-agent call) —
  and confirmed **zero retry logic anywhere in that chain**, in both
  Confluence and, checked side-by-side, identically in upstream
  `opensim-master` and `OpenSim-Tranquillity` (`WhiteCore-Dev` doesn't
  even ship Hypergrid connectors). One transient blip on any hop — even
  ones the source sim never sees directly — fails the whole teleport
  and forces a full manual retry from scratch. Verified a bounded
  retry-with-backoff fix would be safe (traced `Scene.NewUserConnection`
  and confirmed it already dedupes by AgentID, so a retry can't create
  a duplicate agent) before recommending it. **Built and deployed**: a
  bounded 2-attempt retry with a 1s delay, gated strictly on "no reply
  reached us at all" (a transport exception on the Gatekeeper lookup, or
  the absence of the `success` key `AgentHandlers.cs` always sets on any
  real reply) — never on a genuine denial the peer actually sent. Build-
  verified clean, deployed to the live grid (confirmed down first, DLL/
  PDB copied and MD5-verified byte-identical), needs a restart to take
  effect. Full writeup in PROJECT_LOG.md. Fifth item scoped (mixed
  verdict): **attachment reliability (relog/crossing)**. Crossing
  continuity turned out to ride the same `AgentData` transfer already
  hardened above, with one narrow real gap — if an avatar is already at
  `MaxAgentAttachments` when the destination re-attaches, the object
  used to silently sit unattached until temp-object cleanup removed it,
  with no notification and the avatar's appearance data still claiming
  it was worn. **Built and deployed**: `Scene.AddSceneObject` now
  reconciles the appearance record (removes the phantom attachment),
  deletes the orphaned copy immediately instead of leaving a ghost, logs
  a `Warn` naming the item, and correctly reports failure to its caller
  instead of silently claiming success — the last part matters because
  the caller uses that return value to drop the object from its own
  list before firing scene-object events on everything left, so
  reporting success while having just deleted the object would have
  left a dangling reference. The underlying capacity limit isn't
  removed — the item still can't cross while the avatar's at the cap —
  but the failure is now clean instead of a silent leak, and the
  original inventory item was never touched either way. Checked the
  hypothesis that repositioning a worn HUD and logging off without
  detaching would lose the change — traced the actual persistence path
  (`Scene.RemoveClient` → `DeRezAttachments` → `UpdateKnownItem`) and
  found it already correctly wired for graceful logout, so that
  hypothesis was **wrong**, not confirmed; ungraceful disconnects rely
  on the grid's generic dead-client watchdog, out of this pass's scope.
  Flagged one open question rather than guessing, not built: `RezAttachments`
  skips server-side rez of *all* attachments if even one is already
  present when it runs — a plausible mechanism for "only some
  attachments came back after login," confirmed identical in upstream
  `opensim-master`, but whether any real viewer actually triggers this
  race couldn't be confirmed from server source alone. Full writeup in
  PROJECT_LOG.md. Sixth item scoped: **mesh upload / physics shape
  quality**. Traced ubODE's mesh decode → physics-shape resolution →
  collision-geometry pipeline and found a real, silent gap: any mesh
  generation failure (corrupted asset, decode exception, unsupported
  legacy-sculpt combination) falls back to a plain bounding box as the
  *entire* collision volume, with zero signal to the resident who
  uploaded it — the exact mechanism behind "invisible wall" complaints
  on anything with real negative space (archways, staircases, open
  frameworks). Confirmed identical in upstream `opensim-master`.
  Checked the tiny-object bounding-box shortcut (objects ≤10cm skip
  meshing entirely) and confirmed that one's a reasonable, intentional
  performance tradeoff, not a bug. Noted a lower-confidence, code-
  comment-sourced observation that mesh assets carrying both a full
  decomposition and a convex-hull blob get the heavier one when
  Confluence's own comment says SL prefers the lighter one — flagged as
  evidence from the comment, not independently verified. **Built and
  deployed** the fix for the silent-failure gap: a new
  `PhysicsShapeFallback` event on the shared `PhysicsActor` base class,
  fired exactly once per object from `OdePrim.CreateGeom` when — and
  only when — a mesh genuinely failed to build (`MeshState.MeshFailed`,
  not the `noNeed` state every ordinary box/sphere prim carries, which
  would have made this fire on every plain prim in the region if gated
  wrong). `SceneObjectPart` subscribes unconditionally at physics-actor
  creation and sends the object's owner a non-modal in-world alert
  naming the object if they're present in the region. The underlying
  physics behavior is untouched — the object still gets a box collision
  shape either way; this only stops the failure from being invisible.
  Full writeup in PROJECT_LOG.md. Seventh item scoped: **region
  stability under load**. Unlike every prior item, the strongest
  finding here isn't a code gap — it's that Confluence already has a
  complete, working answer (`SimProtectionModule`, WhiteCore-ported)
  that auto-disables scripts then physics when FPS drops below a
  configurable threshold and auto-restarts a genuinely deadlocked
  region, on its own timer decoupled from the region heartbeat it's
  protecting against — and it's disabled on both live regions.
  Confirmed via this repo's own history (Batch 14, 2026-08-10) that the
  module loads and wires up correctly; it was left off afterward for a
  reasoned, still-valid reason — its disruptive mitigation behavior had
  never been exercised against a real FPS drop, and nobody wanted to
  force that on a region in active use. Checked the other classic
  "stability under load" cause — physics numerically exploding — and
  found `ODEPrim` already sanitizes NaN/Infinity on every major physics
  input (Force, Velocity, Torque, Orientation, RotationalVelocity,
  PIDTarget), closing off the "buggy script feeds garbage into
  `llSetForce`" failure mode; flagged one narrower, unconfirmed gap (no
  general max-velocity safety clamp against a legitimate-but-extreme
  physics resolution event, only feature-specific ones). Confirmed the
  thread-stall watchdog's log-only behavior is expected, not a gap —
  it's a visibility tool, and `SimProtection`'s own zero-FPS check is
  the actual recovery path for a truly stuck region. No code change was
  recommended — it was a config decision the user needed to make
  deliberately, not something to flip unilaterally. **The user chose to
  enable it**: `[SimProtection] Enabled = true` at production defaults
  on both live regions (`Var_Test_Region` already had the full config
  block from Batch 14; added the same block to `Welcome_Center`, which
  had none). Config-only change, needs a restart of each region to take
  effect — and worth remembering the mitigation behavior itself
  (scripts/physics auto-disable) is now *enabled* but still not
  *exercised*; the first real FPS drop will be its first live test.
  Full writeup in PROJECT_LOG.md. Eighth item scoped, grounded in a
  direct diff against a reference fork the user pointed to
  (`opensim-lickx`) rather than a blind audit: **permission-system /
  content-protection weaknesses**. Found one real, additive, low-risk
  feature worth porting: lickx's `take_copy_restricted` closes a
  well-known content-theft vector where anyone nearby can right-click
  "Take Copy" on someone else's rezzed full-perm object, since the
  permission bits that make an object usable at all also happen to
  make it copyable by total strangers. Confirmed Confluence lacks this
  check but already has every piece of infrastructure it needs
  (`IsFriendWithPerms`, unused for this purpose) — genuinely low-risk
  since it defaults off. Checked `WhiteCore-Dev`/`OpenSim-Tranquillity`/
  `opensim-master` for the same idea — none have it; this is
  distinctive to this one fork. Also found a real but higher-stakes
  difference presented as a decision, not a bug: lickx consistently
  trusts region managers with less bypass power than Confluence does
  (removes `RegionManagerIsAdmin` entirely, hardcodes no manager
  override on parcel-property edits, favors real grid-god status by
  default) — a tighter security posture than Confluence's current
  `opensim-master`-following defaults, which many private-estate
  operators rely on for self-management. Not changed unilaterally —
  flagged as the user's own call. **Built and deployed**:
  `take_copy_restricted` ported into Confluence's own
  `PermissionsModule.cs`, off by default. Before building, confirmed
  directly (the user asked) that this does not touch region-manager
  bypass power — the only exemptions are the object's owner, a friend
  with explicit modify rights, and `sp.IsGod`, computed entirely by
  Confluence's existing, untouched god-status settings; a region owner
  or manager who already has god status keeps it exactly as before.
  The separate region-manager-posture question from this scoping pass
  was deliberately left alone — only this one additive, opt-in feature
  was ported. Config-only to actually activate (`take_copy_restricted
  = true`, still off by default post-deploy), needs a grid restart.
  Full writeup in PROJECT_LOG.md. Ninth and final item scoped:
  **fragmented-ecosystem feature gaps vs other forks** — and this one
  surfaced the single most consequential finding of the whole
  campaign. `opensim-enhanced` turned out to be a close sibling of
  Confluence itself (shares real "Casperia" lineage) — spot-checked its
  most novel-sounding claims (pathfinding, Combat2, GLTF overrides, RSA
  auth) and all were already present in Confluence, which has actually
  gone further with its own native-service suite. `OpenSim-Continuum`,
  a large independent project with the same reconciliation mission,
  independently reached the same conclusion Confluence did about
  removing `RegionCurrency` as redundant — good cross-validation.
  Investigating Continuum's own economy hardening ("delivery-safe
  object purchase holds") led to checking how Confluence's own
  in-world object purchases actually work, which surfaced a **major,
  confirmed, currently-live gap: `ConfluenceCurrencyModule` — this
  project's own native currency service, and the currently active
  economy module on both live regions — never implemented the
  `OnObjectBuy` handler at all.** `BuySellModule.BuyObject` (read in
  full) delivers an object, a copy, or its contents on every sale type
  without ever calling into a currency interface, and Confluence's
  native module only wires `OnEconomyDataRequest`/
  `OnMoneyBalanceRequest`/`OnMoneyTransferRequest`/`OnLogout` — not
  `OnObjectBuy`. Confirmed the two *older* money modules Confluence
  still ships (`DTLNSLMoneyModule`, `GloebitMoneyModule`) both handle
  this correctly, proving it's a gap specific to the native replacement,
  not a shared architecture limitation. Confirmed live impact directly:
  both regions' `OpenSim.ini` have `EconomyModule =
  ConfluenceCurrencyModule` active right now. Right-clicking "Buy" on
  any for-sale object on the live grid today delivers it at no charge,
  regardless of price. Also found one minor, low-priority item: a
  stale comment in `ConfluenceCurrencyModule.cs` still points to the
  already-and-correctly-removed `RegionCurrency` module for PayPal —
  fixed in passing (now points to RegionWeb's own PayPal wallet).
  **Built and deployed**: a new `ProcessObjectBuy` handler, wired to
  every client's `OnObjectBuy` alongside the module's existing four
  events. Deliberately went beyond a literal port of
  `DTLNSLMoneyModule`'s reference pattern in two ways: it validates
  `saleType`/`salePrice` against the object's own server-side
  `ObjectSaleType`/`SalePrice` rather than trusting whatever the
  requesting viewer claims (closing a real client-tampering angle the
  reference implementation leaves open), and it automatically refunds
  the buyer if `IBuySellModule.BuyObject` reports delivery failed after
  the charge already succeeded — addressing the same "delivery-safe
  purchase" idea `OpenSim-Continuum`'s own economy hardening called
  out. Added a dedicated `ObjectSale` transaction type rather than
  reusing `ObjectPays` (the reverse direction). Build-verified clean,
  grid confirmed down, `OpenSim.Region.CoreModules.dll`/`.pdb` deployed
  and MD5-verified. Needs a grid restart to take effect; live
  verification against a real in-world purchase is left for the user's
  own testing opportunity. Full writeup in PROJECT_LOG.md.
- **Follow-up: systematic audit found a second real gap in
  `ConfluenceCurrencyModule`, `OnRequestPayPrice`.** After `OnObjectBuy`
  turned up missing, the user asked whether more gaps might be sitting
  undiscovered in this module specifically. Rather than wait for a
  third to surface by accident, diffed every client event both
  `DTLNSLMoneyModule` and `GloebitMoneyModule` subscribe to against
  Confluence's own subscriptions — found one more real gap
  (`OnRequestPayPrice`, which answers the viewer's query for an
  object's `llSetPayPrice`-configured Pay-dialog amounts) and ruled out
  two others as genuinely not gaps rather than assuming: `OnScriptAnswer`
  is Gloebit-specific (its own external debit-permission flow, not
  applicable here), and `OnParcelBuyPass` is already handled correctly
  elsewhere via the generic `IMoneyModule` interface. **Built and
  deployed**: `ProcessRequestPayPrice`, mirroring `DTLNSLMoneyModule`'s
  own implementation exactly — a read-only response with no money
  movement, unlike `OnObjectBuy`. Build-verified clean, grid confirmed
  down, deployed and MD5-verified (confirmed the DLL had actually
  changed before confirming the match, since file size alone matched
  the prior build). Needs a grid restart. Full writeup in
  PROJECT_LOG.md.
- **Account membership type / profile badge, renamed and extended.**
  `UserAccount.UserFlags` already packed a "membership type" nibble
  (bits 8-11) the classic viewer renders as a profile badge — Second
  Life's own fixed set (Resident/Trial Member/Charter Member/Linden Lab
  Employee), unused by any admin tooling in this repo. Renamed value 3
  from the meaningless-on-an-independent-grid "Linden Lab Employee" to
  **Grid Team**, and added a new value SL never had: **Supporter**, for
  residents who've financially supported the grid. New
  `AccountMembershipHelper` (same pattern as `AccountBanHelper`) owns
  the naming and the bit-packing so any future caller can reuse it
  without hand-rolling the math. `UserAccount.UserTitle` — also
  previously unused — now does double duty as the actual on-screen
  badge text, since only values 0–3 have a built-in viewer icon;
  anything past that needs `UserTitle` set to be visible at all, so the
  admin UI auto-fills it with the type's name if left blank. Admin
  Users page (`/admin/users/edit-details`) now shows and edits both.
  Checked djphil's `oshelpful` reference table for the older SL
  "200/300/400/600/800" `UserFlags` combinations first — confirmed
  Confluence's actual code doesn't interpret `UserFlags` that way, so
  didn't let an accurate-looking external reference override what the
  code really does. Scoped to the account-type field itself — the
  donor-perk auto-trigger and the still-open "should PayPal actually
  credit currency" question are separate, not built here. Full writeup
  in PROJECT_LOG.md.

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

## Known limitations

- Self-service OAR/IAR restore (upload a file through the web page) is
  deliberately not offered — only backup (save to the server's
  configured folder, same as autobackup) is. A real live 502 while
  testing OAR restore traced back to relaying an entire uploaded
  archive through a public-facing reverse proxy, which runs into
  environment-specific body-size/timeout limits no amount of
  application-level fixing can fully solve. No OpenSim web UI checked
  against this project (WhiteCore-Dev included) offers browser-based
  restore either, and no real grid it's aware of does. Restore an OAR
  from the region's own console, or an IAR the same way inventory
  archives are normally handled server-side. See PROJECT_LOG.md for
  the full investigation.
- Some database migrations remain MySQL-specific in places PGSQL/SQLite
  parity hasn't been added yet.
- Experience Tools is not full Second Life Experience-service
  compatibility.
- `llOpenFloater` is not implemented, not even as a stub.
- `PRIM_GLTF_*` readback returns a face's own override data, not the
  assigned base material merged underneath it (full SL parity would
  need both).
- Pathfinding is region-local and approximate, not a physics-engine-native
  or Linden-proprietary navmesh service.
- Weather is meaningfully improved but still reasonably called
  experimental.
- RegionWeb's PayPal integration is treated as a donation, not a
  currency purchase (see "Included add-on modules" above) — no
  exchange-rate/token-credit path exists yet if that's ever wanted.
- `botListen` gates on bot ownership but delivers using the calling
  object's position (like `llListen`), not the bot's own position —
  `WorldCommModule`'s range-check path resolves its listener host as a
  prim and aborts the whole channel's delivery (not just one listener)
  on a miss, so a bot's `ScenePresence` UUID can't safely be used there.
  `botSensor`/`botSensorRepeat` do sense from the bot's own position
  (a separate, safely-guarded extension to `SensorRepeat.cs`), and
  `botChangeOwner` is unsupported (returns `BOT_ERROR`), matching
  Tranquillity's own bot implementation, which stubs it out too.
- A listed feature may still be disabled in configuration.
- Build success does not replace controlled runtime testing — see the
  "tested vs. compiled" note near the top of this document for exactly
  which parts of the codebase that does and doesn't apply to.

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
  fallen by the wayside; see FEATURES_VS_MASTER.md for what was found
- OpenSim-Grid-Interface (a PHP grid portal) — a secondary reference
  for the Web UI where WhiteCore-Dev had no equivalent page

Historical source provenance remains available in Git history.

Report Confluence-specific problems in this repository. Problems
reproducible on an unmodified official OpenSimulator build should be
reported to the official OpenSimulator project.

## License

Confluence is distributed under the same BSD-style license as
OpenSimulator. See `LICENSE.txt`.

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
| In-world/viewer testing | Live-verified with a real viewer (Firestorm) against a running region: login, teleport between regions, weather rendering, and a full end-to-end currency/land-purchase transaction all confirmed working over an extended live session. An intermittent region-startup hang was tracked as an open blocker for a period (see PROJECT_LOG.md) but has not recurred across many region starts/restarts since, including a full session's worth on 2026-08-16 — its root cause was never conclusively identified, so treat it as currently non-blocking rather than definitively fixed. |

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
`BotPersistenceManager`, ported from Tranquillity) for avatar-follow and
tag-group management via `osNpc`. Infrastructure only at this stage —
see "Progress and roadmap" for what's not wired up yet.

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
- The Bot/NPC management framework is infrastructure only — no script
  can reach it yet. Tranquillity's ~50 `bot*` OSSL functions exist only
  in their Phlox script engine; wiring an equivalent into Confluence's
  own `OSSL_Api.cs`/`IOSSL_Api.cs`/`OSSL_Stub.cs` so YEngine scripts can
  actually call it is deferred as its own effort, comparable in size to
  the module port itself.
- **Real PBR terrain support** — genuinely unclaimed territory, verified
  rather than assumed. Object-level PBR materials (per-face `gltf_json`
  overrides via the `RenderMaterials` capability) already work — that
  part is done, inherited from real upstream OpenSim. Terrain is the
  actual gap: the `"ModifyRegion"` capability real PBR terrain editing
  needs doesn't exist in this repo's own merged-upstream tree, Gunthar's
  fork, or Tranquillity. `SimulatorFeaturesModule.cs` currently echoes
  `PBRTerrainEnabled: true` only because it's parroting a flag the
  viewer itself sets, not because anything backs it — the capability
  request fails today on all three. Building the real capability backend
  first would be a genuine competitive edge, not a port from anywhere.
  Substantial from-scratch build (comparable to Experience Tools or the
  native currency service) — logged, not started. See PROJECT_LOG.md
  for the full investigation.
- **SLua** — Second Life's modern Luau-based (Roblox's Lua variant)
  scripting language, in open beta on the SL production grid since
  2025-12-02 ([LL's announcement](https://community.secondlife.com/news/featured-news/announcing-the-slua-open-beta-modern-scripting-comes-to-second-life-r11237/)):
  faster than LSL/Mono, ~50% less memory, native tables, dynamic event
  subscription, multiple timers, coroutines, native JSON — while staying
  compatible with existing LSL knowledge. No evidence found of any
  OpenSim fork having touched this (checked the same three codebases as
  above). A second, likely larger, genuinely unclaimed opportunity —
  logged, not started, needs its own investigation pass before scoping.
- Tranquillity's "Phlox" LSL/SLua script engine (~98,000 lines) is
  audited but NOT ported: it turned out to be a resurrection of
  InWorldz/Halcyon's own closed-source Phlox engine, now appearing as
  source with no LICENSE file and no explained chain of custody. Not
  shelved outright — a provenance question is pending with OpenSim-NGC
  before any engineering investment is considered. See PROJECT_LOG.md
  for the full writeup.

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

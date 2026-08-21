# Features

What OpenSim-Confluence actually has, organized by area. For how any of
this was built, tested, or debugged, see `PROJECT_LOG.md`. For what's
planned or still missing, see `ROADMAP.md`.

## Web & Admin UI

A native, Robust-hosted grid portal — not an addon-module, and not a
replacement for the optional `OpenSim-Grid-Interface` PHP site, which
remains available as a swappable alternative. Session-based auth
against real grid accounts.

**Public pages:** home/splash with live grid stats, grid-wide search
(People/Places/Events/Classifieds/Groups, plus a dedicated Land for
Sale page with size buckets, per-region maturity filtering, and
trending/autocomplete), a world map, a viewer download page, a live
grid-capability "Features" page, guest support tickets, admin-managed
static pages (About/ToS/DMCA) and news/events feeds.

**Resident self-service:** dashboard, public profile (with group
memberships and regions-owned, both privacy-aware), friends list, a
full partner proposal flow (propose/accept/decline/cancel/breakup),
transaction history, classifieds/events management, region management
for estate owners (OAR backup, full estate settings/access-list
editing for any resident who owns one, not just admins), inventory
backup (IAR), account changes (password/email), self-service account
deletion. Backup (save) only, by design — see `ROADMAP.md`.

**Admin console:** user management (search, create, edit, ban with
optional auto-expiry, soft-delete, kick/message an online resident,
admin-set password reset, login-as-user for support), estate
management (create estates, edit settings, manage managers/access/ban/
group lists), grid-wide group oversight (list every group, moderate
visibility/enrollment flags, delete a group), abuse report review,
financial/transaction reporting, grid statistics, static page and
news/events content management, grid settings, a web-based region
console, per-region Hypergrid open/close toggling, on-demand map-tile
regeneration.

## Native Economy, Search & Grid Services

Backed by MySQL/PostgreSQL/SQLite, replacing what would otherwise be
external dependencies:

- **Currency Service** — in-world virtual currency only, not a
  real-world payment service. A native `IMoneyModule` implementation
  (`ConfluenceCurrencyModule`) usable as the default economy instead of
  Gloebit, MoneyServer, or a third-party service, sharing its ledger
  with the Web UI's transaction reporting. Handles object purchases
  (including automatic refund if delivery fails after payment) and
  scripted Pay-dialog price queries.
- **Search Service** — a working grid search built in as core
  functionality, not an optional external server most grids never
  stand up. Answers both the in-world Directory floater and the Web
  UI's search pages, including trending queries and autocomplete.
- **Events, News, Grid Settings, Static Page, and Support Ticket
  services** — the content and configuration backends the admin
  console manages.

## Moderation & Access Control

- Temporary/timed account bans self-clear on expiry through both the
  real grid/viewer login and the web dashboard/admin login.
- Unbanning restores an account's actual prior level rather than
  resetting it.
- Deleting a group cleans up its membership, role, and role-membership
  rows, plus any resident's dangling active-group reference.
- Grid-wide viewer ban by IP range and client signature.
- Sim protection: opt-in FPS auto-mitigation under load, with
  auto-restart of a genuinely deadlocked region.
- On-demand/soft-start regions.
- A secured web-based region console channel, backing the admin UI's
  Kick/Message and free-form console features.
- A native mute-list service, answering the same viewer protocol real
  Second Life uses.
- `take_copy_restricted` — optional, off by default — blocks bystanders
  from taking a copy of someone else's rezzed full-permission object.
- `DenyNewAccounts` — optional per-estate protection against throwaway
  accounts, computed from account age against a configurable threshold.
- **Trial Member** account tier — public self-registration starts every
  new account as Trial Member, automatically promoted to Resident once
  past the age threshold. Trial Members are blocked from Adult-rated
  regions and Mature-flagged groups until promoted.
- **Account membership types** — Resident, Trial Member, Grid Team, and
  Supporter (the last two are Confluence additions beyond stock SL's
  set), shown as an in-world profile badge.

## Display Names & Identity

- Viewer-compatible Display Names for local users, including CAPS,
  storage, and account-service integration.
- Display names survive region restarts and cross-sim hops.
- Hypergrid Display Name lookup and federation.
- Single-name and `username` login handling.
- Terms-of-service acceptance during login.
- Stale Hypergrid identity self-repair: local login and outbound
  Hypergrid launches correct a stale (not just missing) home/gatekeeper
  URI against the canonical configured value — relevant for deployments
  on dynamic DNS. A `repair user service urls` admin console command
  covers accounts that haven't logged in since.

### Abuse Reports

Treated as core, not an optional add-on:

- Viewer Abuse Reports CAPS.
- Local and remote service connectors, Robust handlers.
- MySQL, PostgreSQL, and SQLite storage.
- Region-side submission support.
- Admin review queue in the Web UI.

## Scripting: LSL and OSSL

### Parcel, terrain, inventory, and object control

- `osTriggerSoundAtPos`
- Parcel auto-return access via `PARCEL_DETAILS_OBJECT_RETURN` and
  `PARCEL_DETAILS_TELEPORT_ROUTING`
- `osReturnObjects` / `osReturnObject`
- In-world terrain console commands
- Script-controlled terrain textures and height ranges
- Sculpt-map animation (`llSetSculptAnim`)
- Hardware/IP/MAC banning, with PostgreSQL/SQLite parity

### Expanded LSL and OSSL compatibility

- `llSignRSA` / `llVerifyRSA` — RSA signing/verification over PEM keys.
- `llGetRegionTimeOfDay`, `llTransferOwnership`, `llSitOnLink`.
- `llSetLinkRenderMaterial`, `llSetLinkGLTFOverrides` — a full PBR
  material override pipeline. `PRIM_GLTF_BASE_COLOR` /`_NORMAL` /
  `_METALLIC_ROUGHNESS` / `_EMISSIVE` are wired into both
  `llSetPrimitiveParams`/`llSetLinkPrimitiveParamsFast` and
  `llGetPrimitiveParams`/`llGetLinkPrimitiveParams`. Real PBR terrain
  editing (`ModifyRegion` capability) is also implemented, alongside
  the pre-existing per-face material override support.
- `llIsExperienceTrusted`, `llGetExperiencePermissions`,
  `llExperienceCanAutoGrant`, `llGetExperienceKeyValueStoreStats`.
- `osPerlinNoise2D`.

### Combat2 scripting

- `llDamage`, `llAdjustDamage`, `llDetectedDamage`, `llDetectedRezzer`.
- Persisted object-health support.
- `on_damage`, `final_damage`, `on_death` events, with a transaction
  window so `on_damage` can override the amount before the other two
  events fire.

### EEP environment scripting

- `llGetEnvironment`, `llSetEnvironment`, `llReplaceEnvironment` —
  region- and parcel-level, gated by standard estate/parcel
  permissions.
- `llSetAgentEnvironment`, `llReplaceAgentEnvironment` — per-agent,
  gated by Confluence's Experience Tools permission system.
- Region and parcel sky/water access.

### Pathfinding

`llCreateCharacter`, `llUpdateCharacter`, `llDeleteCharacter`,
`llExecCharacterCmd`, `llNavigateTo`, `llWanderWithin`,
`llPatrolPoints`, `llPursue`, `llEvade`, `llFleeFrom`,
`llGetStaticPath`, `llGetClosestNavPoint`, backed by a self-contained
region-local A* engine (baked navmesh, obstacle avoidance, and
path-following that reuses the existing keyframe-motion system).

### Experience Tools

A real, backend-persisted system — not full Second Life Experience
service, and not the smaller "Experience-Lite" design some sibling
forks use:

- Self-service Experience creation from the viewer, with a configurable
  one-time creation fee and per-resident cap.
- A real, backend-persisted key-value store (`llCreateKeyValue` and
  friends), not an in-memory dictionary.
- Permission grants and trust checks
  (`llRequestExperiencePermissions`/`llAgentInExperience`/
  `llGetExperienceDetails`/`llGetExperienceErrorMessage`).

### Bot/NPC framework

A management framework for scripted bots, reachable from LSL/OSSL via a
58-function `bot*` set covering lifecycle, movement/navigation, chat/
IM/interaction/animation, tagging, persistence, profile/outfits, and
bot-hosted sensors/comms. Avatar-follow and tag-group management are
also available via `osNpc`.

### Sit targets and avatar animation

- Enforcement of scripted-only sit targets.
- Storage and lookup for LSL sit flags.
- Configurable male and female walk-animation overrides.
- Movement-animation resend protection.

### Region crossing and attachment reliability

- Configurable transfer and cleanup timeouts.
- Preservation of crossing velocity.
- Reduced attachment detach/reattach flashing.
- Duplicate and failed attachment cleanup, with a phantom-attachment
  reconciliation fix for the case where an avatar is already at its
  attachment limit on arrival.
- Coordinated queued attachment-script restarts.
- Widened, adaptive region-crossing prediction window, plus a
  pre-warmed access-check cache that lets a normal crossing skip a
  network round-trip on a cache hit.
- Physics no longer freezes a crossing vehicle/prim before a valid
  destination region is confirmed, shrinking the freeze window and
  removing the old "stuck at the border on a failed crossing" case.
- Hypergrid teleports retry once on a pure transport failure (no reply
  reached the source at all) instead of failing outright on any single
  transient blip.
- Avatar baking/"cloud avatar" recovery is active on Hypergrid arrivals
  as well as local logins (previously local-only).

## World and Environment

### Map tiles

Background, non-blocking rendering with exact geometry (mesh/sculpt,
alpha texture cards, water depth shading) rather than placeholder
boxes. Each 256m cell of a larger region gets its own tile, matching
how the map protocol actually addresses tiles.

### Weather

`OpenSimWeather` (rain, snow, storms, lightning, thunder, wind, clouds)
with persistent precipitation, correctly-positioned lightning, and four
tuned weather profiles. Still reasonably described as experimental —
not scientifically simulated weather.

### Physics realism (ubODE)

Buoyant floating-prim water physics, boat wave response, rubber bounce
and material density tuning, rolling resistance, avatar/object contact
smoothing, and friendly avatar social physics. Mesh-decode failures now
notify the object's owner in-world instead of silently falling back to
an invisible-wall bounding box.

### Region stability

Sim FPS auto-mitigation and stuck-region auto-restart (`SimProtection`)
is enabled on the live grid. Physics inputs are sanitized against
NaN/Infinity before being applied.

## Included Add-on Modules

Under `addon-modules`. Generated into the solution but not necessarily
enabled by default.

- **Gloebit** — optional Gloebit economy integration.
- **GroupAutoInvite** — configurable automatic group invitations on
  arrival.
- **HoloPhysicsGuard** — reduces idle physics load when regions are
  empty.
- **OpenSimMarketplace** — portable Direct Delivery marketplace system.
- **OpenSimSearch** — external viewer search client, for anyone who
  wants to point at a separately-deployed compatible search server
  instead of the native Search Service.
- **OpenSimTide** — configurable tide and water-level simulation.
- **OpenSimWeather** — see "Weather" above.
- **RegionWeb** — per-region web pages, protected estate
  administration, an in-world LSL/OSSL compatibility reference, and its
  own avatar wallet portal (balance/statement, token purchases, admin
  dashboard, PayPal donations treated as a straight donation, not a
  currency purchase).

Detailed marketplace documentation:
`addon-modules/OpenSimMarketplace/README.md`.

For every in-world chat command available to avatars/estate managers
across the whole repo, see [`INWORLD_COMMANDS.md`](INWORLD_COMMANDS.md).

## MoneyServer

In-world virtual currency only — an alternate `IMoneyModule`
implementation for grid owners who prefer it over the native Currency
Service.

- MoneyServer, region currency module, and MySQL data wrapper.
- Viewer currency purchases without an external `currency.php` helper.
- Configurable daily, weekly, and monthly purchase limits.
- Idempotent confirmation UUID handling.
- Banker, transfer, group, email-lock, object-payment, upload-charge,
  and land-sale controls.
- Can run headless with a basic console, matching Robust/OpenSim.

The repository does not ship a live `bin/MoneyServer.ini`. Review the
included examples before production use.

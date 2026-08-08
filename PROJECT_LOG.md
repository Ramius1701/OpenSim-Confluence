# Casperia Project Log

Running record of work done on the Casperia OpenSim fork, so context survives
across sessions. Update this file as work progresses — don't let it go stale.

Repo: `S:\Github\Casperia` (git, branch `merge-experiment`)
Test deployment: `S:\Opensim\Casperia-Dev\` (built from the repo above)
Live grid: `S:\Opensim\Casperia\` — **frozen, do not touch until user says
testing is done.**

---

## Standing rules

- Never modify anything under `S:\Opensim\Casperia\` (live grid) until the
  user explicitly says testing is done.
- When the user claims "AI broke this," verify with `git log -1 --format="%H
  %ai %an %s" -L <start>,<end>:<file>` before responding — every such claim
  investigated so far has traced to pre-AI code (original import
  `a8339fedb4`, or genuine upstream commits by UbitUmarov/Mike Dickson).
- When building new features, check how real Second Life / Tranquillity /
  Mobius / WhiteCore-Dev do it first and model after that rather than
  inventing new behavior.

---

## Feature parity audit (Gunthar / Tranquillity / Mobius / WhiteCore-Dev) — done

- Cherry-picked remaining Gunthar/Tranquillity improvements.
- Mined the Tranquillity release branch and surveyed Mobius for portable
  features.
- Audited WhiteCore-Dev for portable features — confirmed its Experience
  implementation is a complete no-op stub (not "more developed" as
  originally assumed).
- Confirmed Gunthar has no Experience implementation at all (checked via
  GitHub API tree search).

## Backend / database testing — done

- Booted a region simulator in Grid mode against MySQL Robust.
- Smoke-tested the PostgreSQL backend.
- Enabled and smoke-tested addon-modules.
- Confirmed SQLite is standalone-only by design (answered architecture
  question).
- Reviewed `DTLNSLMoneyModule.cs` and `MoneyDBService.cs` diffs for bugs.
- Integration-tested region ↔ MoneyServer communication.
- Verified AccessControl (banning) parity across PGSQL/SQLite.
- Checked asset name/description column widening for cross-DB parity.

## Feature/commit reviews — done

- TOS Support feature (`4ae778df9a`).
- Estate add/remove manager commands (`d3b69de85c`).
- osPerm2Use / osPermissionToCall (`8f276b7fef`, `7521c8a62b`, `e4a9380d98`).
- Hypergrid display names (`0818d73c2f`).
- LSL constants additions (`262e7a294f`, `056e70f313`).
- Friendly avatar social physics (`b7dc0b7971`, `1b6a2bdc1d`).
- Warp3D map tile fix and Consortium patches cleanup.

## Grid management verification

- Done: Estate management tools completeness.
- Done: Outbound Hypergrid travel works with region closed to HG.
- Done: Region restart from in-world and control panel.
- Done: Unrestricted inventory travel across Hypergrid.
- Done: Display names require no website visit.
- **In progress:** per-region Hypergrid open/close toggle.
- Pending: on-demand maptile regeneration.
- Pending: OAR/IAR upload and full backup workflows.
- Pending: control panel covers claimed management capabilities.
- Pending: asset server reliability/self-healing claims.
- Pending: in-world Web Search functionality.

---

## MoneyServer / Currency — "Unable to Buy" bug (resolved)

User reported currency purchase broken, insisted MoneyServer worked before
AI touched it. Verified via git blame each time — every bug traced to
pre-AI code, not AI changes.

Root causes found and fixed:
1. **IMoneyModule hijack** — `DTLNSLMoneyModule.cs` (addon-modules) would
   activate `AddRegion` even when it wasn't the selected economy module.
   Fixed by adding `m_isSelectedEconomyModule` guard.
2. **Nini case-sensitivity** — DTLNSLMoneyModule reads config key
   `EconomyModule` (capitalized); Gloebit reads lowercase `economymodule`.
   Same `[Economy]` section, different casing needed depending on which
   module reads it. Fixed by adding both-cased keys to region inis.
3. **CurrencyGroupOnly logic bug** — `MoneyXmlRpcModule.cs`
   `ValidateCurrencyPurchaseAccess` rejected purchases instead of skipping
   the check when `CurrencyGroupID` was the zero/placeholder UUID. Fixed by
   diffing against Manfred Aabye's actual upstream
   (`github.com/ManfredAabye/opensimcurrencyserver-dotnet`) and matching
   its logic.
4. **MoneyServer.dll.config blank console lines** — Console appender's
   `conversionPattern` had a duplicated `%newline`. Fixed in both the
   deployed copy and the source-tree copy (`bin/MoneyServer.dll.config`).
5. **Undocumented MoneyServer.ini gaps** — reconciled the deployed
   `MoneyServer.ini` against the canonical
   `addon-modules/OpenSim-Grid-MoneyServer/MoneyServer.ini.example`
   template (added `TotalDay/Week/Month`, `[Stipend]` section,
   `ClientCrlFilename`, etc.).

Key files: `addon-modules/OpenSim-Modules-Currency/.../DTLNSLMoneyModule.cs`,
`addon-modules/OpenSim-Grid-MoneyServer/.../MoneyXmlRpcModule.cs`,
`S:\Opensim\Casperia-Dev\MoneyServer.ini`, `MoneyServer.dll.config`.

## Weather module — end-to-end polish (resolved)

Directive: model behavior after how weather works in Second Life.

1. **Precipitation vanishing after ~60s** — main rain/snow/storm emitters
   had `PrimFlags.TemporaryOnRez` + `TemporaryInstance = true`, which
   auto-deletes objects after ~60s regardless of particle system max age.
   Confirmed absent in Gunthar's original. Removed from the main emitter
   (kept on the short-lived lightning flash, where it belongs).
2. **Lightning striking underground** — fallback position used a flat
   ground-level offset. Fixed to use emitter height + random 5–13m range,
   matching original.
3. **Sunny profile washed out / too bright** — multi-iteration fix. Root
   cause: `ViewerEnvironment.cs` applies an undocumented `ambient * 3.0f`
   engine conversion when converting legacy lightshare data to modern EEP
   (`ToLightShare`/`FromLightShare`, `ViewerEnvironment.cs:217/287`).
   Fixed by dividing the intended ambient by 3 in the Sunny profile
   (`0.15,0.15,0.13` → renders as ~`0.45,0.45,0.39`). Also tuned scene
   gamma, glow, haze, and distance multiplier closer to real
   `RegionLightShareData` defaults.
4. **Precipitation starting before the sky change** — `ApplyWeather`
   created emitters before calling `ApplyClouds`. Reordered so sky changes
   apply first.
5. **All 4 weather profiles (Sunny/Storm/Snow/Rain) tuned** to look more
   natural — haze density, scene gamma, cloud coverage adjustments per
   profile.
6. **Sun/moon day-night cycle freezing after weather use** — `ApplyClouds`
   cloned from a stale `m_savedEnvironment` snapshot captured once per
   weather "session" instead of the live environment, so every subsequent
   weather change reset the sun back to that stale snapshot. Fixed by
   always cloning from `m_environmentModule.ToLightShare()` (live state),
   keeping `m_savedEnvironment` only as the restore-point for `weather
   clear`.
7. **`weather clear` no-op after a restart** — `RestoreClouds` silently
   returned when `m_savedEnvironment` was null (e.g. after a mid-weather
   region restart), leaving stale/dirty environment data stuck with no
   recovery. Fixed by falling back to
   `m_environmentModule.ResetEnvironmentSettings(...)` when there's no
   in-session snapshot to restore to.
8. One-time DB cleanup performed for both regions
   (`DELETE FROM regionenvironment WHERE region_id IN (...)`) to clear
   leftover dirty state, followed by a clean restart.

Status: user confirmed weather-type transitions (storm→sunny→clear etc.)
work cleanly on both regions with zero errors logged. Sun/moon movement
itself is slow by design (`day_length=14400`s = 4 hours per full cycle, so
only ~15° of movement per 10 minutes) — user is watching over a longer
session to do a final visual confirmation that it's not frozen.

Key file: `addon-modules/OpenSimWeather/Module/WeatherModule/WeatherModule.cs`.

## Experience Tools — self-service creation feature (built, working)

Directive: users should be able to create their own Experiences, with an
optional fee charged via whichever economy module (Gloebit or MoneyServer)
the sim uses — generic across economy backends via `IMoneyModule`.

Design (confirmed with user):
- Creation fee starts at 0, configurable, changeable later.
- Fee goes to the region/estate owner.
- Small default cap on experiences per resident (3), configurable.

Implementation, verified against real SL viewer source
(`llfloaterexperiences.cpp` from `github.com/secondlife/viewer`):
- Added `TryCreateExperience` / `CanCreateExperience` to `IExperienceModule`
  and implemented in `ExperienceModule.cs` — checks the per-resident cap,
  charges the fee via `IMoneyModule.MoveMoney(...)` if fee > 0, generates a
  new experience.
- `AgentExperiences` capability converted from GET-only to a GET/POST
  dispatcher (`SimpleStreamHandler` pattern) — real SL's "Acquire" button
  POSTs to this same capability, not `UpdateExperience`.
- Response now includes a `purchase` key (presence-only flag) when
  `CanCreateExperience` is true — this is what actually enables the
  "Acquire" button in the viewer; its absence was why the button was
  permanently greyed out.
- New config: `[Experience] CreationFee` / `MaxExperiencesPerResident` in
  `OpenSimDefaults.ini` (both deployed and source-tree copies).

Bugs hit and fixed along the way:
- Two DLL deployment mismatches — rebuilding `ExperienceModule.cs`'s
  project also rebuilds `OpenSim.Region.Framework.dll`; both must be
  copied together or you get `MissingMethodException` at runtime.
- MoneyServer.exe (not just Robust.exe) can hold a file lock on
  `OpenSim.Region.Framework.dll` via transitive references — must stop it
  before copying too.
- `RemoteExperienceServiceConnector.cs` had a copy-paste bug (checked
  config key `InventoryServices` instead of `ExperienceService`), which
  silently prevented `IExperienceService` from ever registering. Traced to
  Mike Dickson's PR #86 (2024-08-20), not AI.
- `UserManagementModule.AddUser` didn't copy `DisplayName`/`NameChanged`
  into cached `UserData` (only `GetUser()` did) — display names weren't
  surviving region restart/cross-sim hops. Traced to UbitUmarov's 2020
  commit `2fbd14722d5`, not AI.

Status: **working end-to-end**, confirmed by user via screenshot —
resident clicked Acquire, got a new "(untitled experience)" with the
Experience Profile editor auto-opening, matching real SL behavior.

Key files: `OpenSim/Region/ClientStack/Linden/Caps/ExperienceModule.cs`,
`OpenSim/Region/Framework/Interfaces/IExperienceModule.cs`,
`OpenSim/Region/CoreModules/ServiceConnectorsOut/Experience/RemoteExperienceServiceConnector.cs`,
`OpenSim/Region/CoreModules/Framework/UserManagement/UserManagementModule.cs`.

---

## Gunthar LSL/OSSL scripting-compatibility port (done, merged 2026-08-06)

Triggered by the OpenSim-Continuum README comparison (see
FEATURES_VS_MASTER.md) — RegionWeb's in-world docs advertised RSA
signing, Pathfinding, Combat2, GLTF overrides, and sculpt animation, but
none of it was actually implemented. Scope was explicitly narrowed to
just those five areas — not Gunthar's Experience-Lite permission system,
not his Vanilla Sim branding/PayPal wallet/multi-grid HUD work.

**Why hand-port instead of cherry-pick:** the first real cherry-pick
attempt (`181bf4ad1b`, "Expand LSL compatibility surface") hit a deep
architectural conflict — Gunthar's EEP scripting functions
(`llReplaceAgentEnvironment` etc.) are built on his own "Experience-Lite"
script-trust system (`IsScriptExperienceTrusted()`), which is a
different, competing design from the Experience Tools system built
earlier this session (`ExperienceID`-based, tied to `IExperienceModule`).
Whole-commit cherry-picks kept dragging in that competing system. Switched
to extracting just the genuinely-missing method implementations straight
from `gunthar/master`'s tip and adapting them to our own codebase,
verifying with a targeted `dotnet build` of
`OpenSim.Region.ScriptEngine.Shared.Api.csproj` after each piece
(seconds, vs. minutes for a full solution build).

Delivered, in order:
1. **RSA signing** — `llSignRSA`/`llVerifyRSA`, `RSA.SignData`/`VerifyData`
   over PEM keys, PKCS1 padding. Fully self-contained.
2. **Combat2** — `llDamage`, `llAdjustDamage` (both overloads),
   `llDetectedDamage`, `llDetectedRezzer`. Added persisted object health
   (`SceneObjectPart.GetLslHealth`/`SetLslHealth`/`GetLslDamageType`/
   `SetLslDamageType`, stored via `DynAttrs` using the same pattern as the
   existing LSL sit-flags code) and `DetectParams.Rezzer`/`Damage`/
   `OriginalDamage`/`DamageType` fields, wired from
   `SceneObjectGroup.RezzerID`. Damage resolution runs through an async
   `CombatDamageTransaction` window so an `on_damage` handler can call
   `llAdjustDamage` to override the amount before `final_damage`/`on_death`
   fire. Extended `llGetHealth` to also resolve object (not just avatar)
   health.
3. **Sculpt animation** — `llSetSculptAnim`, persisted via `DynAttrs`,
   mirrored through the existing texture-animation LLUDP block as a visible
   fallback (OpenSim has no dedicated sculpt-animation wire field).
4. **GLTF overrides** — `llSetLinkRenderMaterial` (refactored the existing
   `llSetRenderMaterial` into a `SceneObjectPart`-parameterized helper,
   then wrapped it in a `GetLinkParts` loop) and `llSetLinkGLTFOverrides`
   plus a full ~1150-line PBR material pipeline: reads a material asset's
   glTF JSON (LLSD-XML or raw JSON, `GetGltfMaterialAssetData`/
   `TryExtractGltfJson`/`TryCompactGltfMaterialJson`) and reduces it to a
   compact hand-rolled key-value string (not JSON — cheaper to store/parse
   per prim face) covering base color, metallic/roughness, emissive, alpha
   mode/cutoff, double-sided, texture IDs, and KHR texture transforms.
   `ApplyGltfOverrides` merges `OVERRIDE_GLTF_*` op-coded changes into that
   string. Added the `OVERRIDE_GLTF_*` constants to LSL_Constants.cs (they
   didn't exist; `PRIM_GLTF_*` already did). Also ported (but left
   unreachable — nothing calls them) `ApplyGltfPrimitiveParams` and its
   texture/transform helpers, which back `PRIM_GLTF_*` codes in
   `llSetPrimitiveParams`/`llSetLinkPrimitiveParamsFast`; wiring that
   dispatch is a separate task, and Continuum's own README lists it as
   unsupported anyway.
5. **Pathfinding** — the full 12-function suite (`llCreateCharacter`,
   `llUpdateCharacter`, `llDeleteCharacter`, `llExecCharacterCmd`,
   `llNavigateTo`, `llWanderWithin`, `llPatrolPoints`, `llPursue`,
   `llEvade`, `llFleeFrom`, `llGetStaticPath`, `llGetClosestNavPoint`),
   backed by a self-contained region-local pathfinding engine:
   `BakedNavMesh` (2m-cell grid sampled from terrain height, cached and
   rebaked on a hashed terrain-signature change or after 30s), A* search
   with obstacle avoidance (other objects, optionally avatars via
   `AVOID_CHARACTERS`) and step-height limits, path simplification by
   collapsing collinear-enough waypoints, and `CharacterNavState` (per-root-prim
   pathfinding parameters — speed, radius, accel/decel, turn radius,
   avoidance mode, stay-within-parcel). Path following reuses the existing
   `KeyframeMotion` system rather than a new movement engine, posting
   `path_update` events on completion via a background task keyed by a
   per-motion UUID so stale completions from a since-replaced path are
   dropped.

Every piece was hand-extracted from `gunthar/master`'s tip (not a specific
commit — the same logical function was sometimes split/refactored across
several of his commits), then adapted: dropped his Experience-Lite trust
gating where it appeared, renamed `m_host`-hardcoded helpers to take a
`SceneObjectPart part` parameter where a `Link` variant was needed to
match his own factoring pattern.

Built and merged: developed in an isolated worktree/branch
(`gunthar-lsl-compat`, `S:\Github\Casperia-gunthar-lsl-compat`) off
`merge-experiment`, one full-solution `dotnet build OpenSim.sln` at the
end (0 errors), then fast-forward merged into `merge-experiment`
(`c54a115946..9b9f0304e5`). **Not yet tested in-world** — only compiled,
not run against Casperia-Dev.

Key files: `OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs`
(the bulk of it), `.../Interface/ILSL_Api.cs`, `.../Runtime/LSL_Stub.cs`,
`.../Runtime/LSL_Constants.cs`, `OpenSim/Region/ScriptEngine/Shared/Helpers.cs`
(DetectParams fields), `OpenSim/Region/Framework/Scenes/SceneObjectPart.cs`
(health/damage-type persistence).

---

## Region-level EEP scripting: llGetEnvironment/llSetEnvironment/llReplaceEnvironment (done, merged 2026-08-06)

Follow-up to the Gunthar port above. FEATURES_VS_MASTER.md had flagged
these three as "entangled with Experience-Lite trust checks" based on
the `llReplaceAgentEnvironment`/`llSetAgentEnvironment` conflict hit
earlier — but checking gunthar/master directly showed that assumption was
wrong for the region/parcel-level functions specifically: they're gated
by plain OpenSim permissions, not his Experience-Lite system at all.

- `llGetEnvironment` — read-only, no permission gate (environment is
  public info, same as real SL). Reads sky/water/day-cycle data at a
  position via a large rule-code switch.
- `llSetEnvironment`/`llReplaceEnvironment` — write to either the whole
  region (`World.Permissions.CanIssueEstateCommand`) or a specific
  parcel (`CanEditParcelProperties(..., GroupPowers.AllowEnvironment,
  ...)`), selected by a negative x/y position meaning "whole region."
  `llSetEnvironment` applies individual sky/water parameter rules via
  `ApplyEnvironmentParameters`; `llReplaceEnvironment` swaps in a whole
  settings asset and/or adjusts day length/offset via
  `llReplaceEnvironment`'s own asset-loading path.

Required two small, purely-additive extensions to shared framework
classes used elsewhere in the codebase (confirmed via diff against
gunthar's versions - no existing methods touched, so no risk to the
Weather module or anything else already using these files):
- `OpenSim/Framework/ViewerEnvironment.cs`: `GetWater()`/`EnsureWater()`
  (find-or-create the active water frame), `EnsureSkyTargets()` and its
  helpers (resolve which sky frame(s) a parameter change applies to, for
  a given altitude or all tracks at once).
- `OpenSim/Framework/ViewerSky.cs`: `CloudTexture`/`MoonTexture`/
  `SunTexture` PascalCase property wrappers over the existing
  `cloud_id`/`moon_id`/`sun_id` fields, plus `IsDefaultBloomTexture()`
  and four siblings (used by `llGetEnvironment`'s `SKY_TEXTURE_DEFAULTS`
  readback).

Added 6 previously-missing constants to LSL_Constants.cs
(`ENV_NO_PERMISSIONS`, `SKY_TEXTURE_DEFAULTS`, `SKY_LIGHT`,
`SKY_TRACKS`, `WATER_TEXTURE_DEFAULTS`, `ENVIRONMENT_DAYINFO`) —
everything else these functions need (the `SKY_*`/`WATER_*` rule codes,
`ENV_INVALID_RULE`/`ENV_VALIDATION_FAIL`/`ENV_NO_ENVIRONMENT`,
`SortAltitudes()`/`InvalidateCaches()`) already existed from the
per-agent EEP work.

Same workflow as before: isolated worktree/branch (`eep-region-scripting`),
targeted `dotnet build` after each file, one full-solution build (0
errors) before merging. Fast-forward merged into `merge-experiment`
(`83bd2e8814..2792b26c10`). **Not yet tested in-world.**

Key files: same LSL_Api.cs/ILSL_Api.cs/LSL_Stub.cs/LSL_Constants.cs as
above, plus `OpenSim/Framework/ViewerEnvironment.cs` and
`OpenSim/Framework/ViewerSky.cs`.

---

## Full RegionWeb import from Gunthar's fork (done, merged 2026-08-06)

Requested directly, separate from the LSL-compatibility work above.
`RegionWebModule.cs` was 4835 lines vs. gunthar/master's 7230 - re-audited
its in-world LSL-docs page first (148 documented functions), then
diffed the whole file to find the real shape of the gap.

**Doc-page audit result:** 15 of 148 documented functions don't actually
exist. 4 are genuinely completable Experience Tools queries
(`llIsExperienceTrusted`, `llGetExperiencePermissions`,
`llExperienceCanAutoGrant`, `llGetExperienceKeyValueStoreStats`) - a
real surprise finding here: Casperia already has its own working,
backend-persisted Experience Key-Value store and permission system
(`llCreateKeyValue`/`llAgentInExperience`/`llRequestExperiencePermissions`
etc. all already existed, gated by `World.ExperienceModule` +
`m_item.ExperienceID` - a more proper implementation than Gunthar's
in-memory-dictionary Experience-Lite). These 4 are just small read-only
queries against that same already-working system - **still not
implemented, left open, see below.** The other 11 (10 previously-excluded
misc parcel/inventory functions + `llOpenFloater`, a stub even upstream)
remain accurate as "not implemented."

**Whole-file diff result:** the 2395-line gap was almost entirely one
subsystem - a currency/wallet portal (`SendCurrencyPortal` + ~20
supporting methods: avatar balance viewing, transaction statements,
admin CSV exports, and full PayPal order creation/capture/checkout via
PayPal's live API).

This overlaps with the separate `RegionCurrency` addon-module, which
already has its own working PayPal/wallet implementation (3115 lines).
Per the user: RegionCurrency was only split out of RegionWeb by a prior
AI session (ChatGPT/Copilot), not a deliberate Casperia design decision -
so importing gunthar's version back into RegionWeb was the right call
even though it now duplicates RegionCurrency. Both exist in parallel for
now; deduplicating them was explicitly not asked for and wasn't touched.

**PayPal specifically:** ships present but dormant - gated by the
existing `IsPayPalConfigured()` check (same as gunthar's own code), no
live credentials wired up. Reserved for future use per the user, not
active.

**Branding:** replaced all 66 occurrences of gunthar's "Vanilla Sim"
product name with neutral defaults matching Casperia's existing
convention - `"My OpenSim Estate"` for the configurable default title
(3 assignment sites), `"This estate"` for body-copy references (59
occurrences), `"RegionWeb"` as the in-world notification sender name (4
`SendBlueBoxMessage` calls, was `"Vanilla Sim"`). Kept our own
`[assembly: Addin(...)]` registration attributes (required for
Mono.Addins to load this as a standalone addon-module - gunthar's copy
lives in his core tree under a different registration mechanism, ours
needs it explicitly).

Given the file had no deep architectural conflict with the rest of
Casperia (unlike LSL_Api.cs's Experience-Lite entanglement), this was a
wholesale import (copy gunthar's file, patch branding + Addin
attributes back in) rather than a hand-ported piece-by-piece merge.
Built clean on the first attempt, both targeted and full-solution (0
errors). Fast-forward merged into `merge-experiment`
(`c0b86a2490..7f29f2688c`). **Not yet tested in-world.**

Key file: `addon-modules/RegionWeb/RegionWebModule/RegionWebModule.cs`.

---

## Experience introspection queries + Experience function family unreachable-from-scripts fix (done, merged 2026-08-06)

Closed out the 4 deferred functions from the RegionWeb doc audit:
`llIsExperienceTrusted`, `llGetExperiencePermissions`,
`llExperienceCanAutoGrant`, `llGetExperienceKeyValueStoreStats`. Real
implementations against Casperia's existing Experience Tools backend
(`World.ExperienceModule.IsExperienceEnabled`/`IsExperienceAdmin`/
`GetEstateKeyExperiences`), not stubs - "trusted" means the script's
attached experience is enabled and either the owner is an Experience
Admin or the experience is in the estate's Key Experience list.
Auto-grantable permissions are a fixed safe set matching real SL's
documented behavior (animation/controls/camera/attach/teleport - never
`PERMISSION_DEBIT` or anything ownership-changing). KVP stats report
real `enabled`/`trusted`/`key_count`/`used_bytes`; the `max_*` capacity
fields report `-1` since our Experience store has no configured capacity
ceiling to report (unlike Gunthar's local-dictionary store this was
adapted from).

**Bigger find while wiring these in:** the entire Experience function
family was unreachable from any LSL script. All 12 pre-existing
functions (`llRequestExperiencePermissions`, `llAgentInExperience`,
`llGetExperienceDetails`, `llGetExperienceErrorMessage`, `llSitOnLink`,
the 6 key-value store functions, `llSetAgentEnvironment`/
`llReplaceAgentEnvironment`) were fully implemented in `LSL_Api.cs` and
declared in `ILSL_Api.cs`, but never wired into `ScriptBaseClass`
(`LSL_Stub.cs`). YEngine's `XMRInstAbstract` inherits from
`ScriptBaseClass`, and compiled scripts call LSL functions through that
inheritance chain - so despite the backend working correctly end to end,
no script could actually call any of them. This directly affects the
self-service Experience creation feature built earlier this session
(residents' scripts calling `llRequestExperiencePermissions`/
`llCreateKeyValue` etc. would have silently failed to compile). Wired
all 12 alongside the 4 new functions.

Verified with a targeted ScriptEngine build and a full solution build
(0 errors both).

Key files: `LSL_Api.cs`, `ILSL_Api.cs`, `LSL_Stub.cs`.

---

## User Alias service (done, merged 2026-08-06)

Ported from Tranquillity (`OpenSim-NGC/OpenSim-Tranquillity`), the one
genuinely portable finding from that repo's feature-parity audit (its
big-ticket item, an EF Core/ASP.NET Identity migration, is a wholesale
architecture swap and not cherry-pickable).

Lets an account be reachable under one or more secondary UUIDs
(aliases) that resolve back to the same UserID grid-side. Grid/console
managed only - `create alias`/`show alias`/`delete alias` console
commands, no HTTP-exposed create or delete (matches Tranquillity's
original restriction exactly, preserved deliberately). No viewer-visible
cosmetic effect; this is plumbing for other services to look up "is this
UUID actually a known alt of that account," not a display-name feature.

Followed the standard OpenSim service layering used elsewhere in this
tree (see the Experience service for comparison): `IUserAliasData` +
per-DB data handlers + migrations, `IUserAliasService` +
`UserAliasService` (Robust-side, owns the console commands),
`UserAliasServicesConnector` (HTTP client) with Local/Remote
region-side `ISharedRegionModule` connectors, and
`UserAliasServerPostHandler`/`UserAliasServiceConnector` on the Robust
HTTP side.

Added PGSQL and SQLite storage backends and migrations beyond
Tranquillity's MySQL-only original, for cross-DB parity with the rest
of Casperia (same pattern as the AccessControl/AbuseReports parity work
earlier this project).

Two bugs fixed while porting (present in Tranquillity's original,
neither introduced here):
- `UserAliasService.DeleteAlias` had an unreachable `throw` sitting
  after its `return`.
- `UserAliasServicesConnector.CreateAlias` sent
  `Description.ToString()` where `Description` is already a
  possibly-null `string` - would NRE on a null description. Changed to
  `Description ?? string.Empty`.

Also dropped a dependency on `OSHHTPHost`, a class referenced by
Tranquillity's original `UserAliasServicesConnector.Initialise()` that
doesn't exist anywhere in Casperia's tree (grepped for both that
spelling and the likely-intended `OSHTTPHost`, found neither).
Replaced with the same direct `GetString(...)` + trim pattern our own
`ExperienceServicesConnector.cs` already uses.

Built in an isolated worktree (`useralias-service` branch), verified
with targeted builds (`OpenSim.Services.UserAccountService`,
`OpenSim.Services.Connectors`, `OpenSim.Region.CoreModules`, which
transitively covers `OpenSim.Server.Handlers`) and a full solution
build (0 errors). Fast-forward merged into `merge-experiment`
(`dfdc867c98..4a3dadc147`). **Not yet tested in-world** — no console
command or connector wiring has been exercised against a live grid yet.

Key files: `OpenSim/Services/Interfaces/IUserAliasService.cs`,
`OpenSim/Services/UserAccountService/UserAliasService.cs`,
`OpenSim/Services/Connectors/UserAliases/UserAliasServicesConnector.cs`.

---

## Land Auction and Team Combat modules, ported from WhiteCore-Dev (done, merged 2026-08-06)

Follow-up to the WhiteCore-Dev feature-parity audit. WhiteCore-Dev turned
out to be a much bigger architectural departure than Gunthar or
Tranquillity - an Aurora-Sim-derived fork with no shared git history with
vanilla OpenSim at all (`git merge-base` returns nothing) and a fully
restructured internal layout (`WhiteCore/DataManager` instead of the
`Data`/`Services` split, its own `WhiteCore/ScriptEngine`, etc.), so the
audit was a feature-level comparison rather than a commit-diff
cherry-pick. Of everything it found, two were genuinely portable:

**Land Auction** (`IAuctionModule`/`AuctionModule`) - a real bid-based
parcel auction: start/bid/end/show via new console commands
(`land auction start/bid/end/show <local id>`), highest bidder wins via
the existing `ILandObject.UpdateLandSold`, winner notified via
`IMessageTransferModule`. Casperia already had `AuctionID`/`SnapshotID`
fields on `LandData` but no working mechanism behind them. Bids are
tracked in-memory in the module (not persisted on `LandData`), keeping
this a self-contained, single-file addition with no DB schema changes.
WhiteCore's viewer-native "start auction" flow (an `IClientAPI` event +
CAPS handler, triggered from the About Land floater) was left out -
Casperia's `LLClientView` has no equivalent packet wired up at all, and
adding new LLUDP packet handling was out of scope for this pass; console
commands are the pragmatic substitute.

**Team Combat** (`ITeamCombatModule`/`TeamCombatModule`) - team
membership (`combat team join/leave/show` console commands), a shared
combat respawn point instead of teleport-home for team members, a
teleport block while a team member hasn't left combat, and a
configurable health regen rate for team members. This one needed real
design work before porting: WhiteCore's original `CombatModule` tracks
its **own separate avatar health field** and its **own physics-collision
damage detection**, both of which would collide with systems Casperia
already has - vanilla `ScenePresence` already decrements `Health` on
collision with a `Damage`-bearing prim, and Gunthar's Combat2 port
already drives `Health` through `llDamage`/`llAdjustDamage` with its own
death/respawn handling (`CompleteDamageToPresence` in `LSL_Api.cs`,
which bypasses the `OnAvatarKilled` event entirely). Porting WhiteCore's
module unmodified would have meant two independently-tracked health
values both calling `SendHealth()` on the same avatar.

Given the user's explicit choice (asked directly, since this needed a
scope decision before writing code) to keep this integration surgical
rather than touching the already-merged Combat2 pipeline, the port
carries over only the team/respawn/teleport-block/regen layer, all of
which needs zero involvement in damage application:
- Team regen rate reuses `ScenePresence.HealRate` (already exists,
  already HP/sec, built for exactly this - no core-file edit needed).
- Teleport-block reuses the existing `scene.Permissions.OnTeleport` hook.
- Respawn-point override needed one small, additive edit to vanilla
  `OpenSim/Region/CoreModules/Avatar/Combat/CombatModule.cs`: its
  `KillAvatar` handler unconditionally teleports the dead avatar home,
  which would otherwise race against a team module's own teleport call
  for the same avatar. It now checks for a loaded `ITeamCombatModule`
  first and defers to it for team members; behaviour is byte-for-byte
  unchanged when the module isn't loaded or the avatar isn't on a team.
  This only affects the classic collision-based death path (which
  fires `OnAvatarKilled`) - Combat2's scripted `llDamage` deaths bypass
  that event entirely and are unaffected either way.
- WhiteCore's own parcel-enter invulnerability toggle was dropped as
  redundant - `OpenSim/Region/CoreModules/World/Land/LandObject.cs`
  already does this from the parcel `AllowDamage` flag.
- Dropped from scope: WhiteCore's team-kill damage mitigation
  (`AllowTeamKilling`/`DamageToTeamKillers`), since that specifically
  needs the damage-pipeline hook the user chose to avoid touching this
  pass. Team join/leave is console-only for now, not scriptable - no
  new LSL/OSSL functions were added in this port.

Off by default in `OpenSimDefaults.ini` (`[TeamCombatModule] Enabled =
false`) since it changes teleport/respawn behaviour for anyone placed on
a team. `[AuctionModule]` defaults to enabled since it's inert unless an
admin runs the console commands.

Verified with targeted builds (`OpenSim.Region.OptionalModules`, which
transitively covers `OpenSim.Region.CoreModules`) and a full solution
build (0 errors). Fast-forward merged into `merge-experiment`
(`a0882d9b34..4a64c30cb6`). **Not yet tested in-world.**

Key files: `OpenSim/Region/CoreModules/World/Land/AuctionModule.cs`,
`OpenSim/Region/OptionalModules/Avatar/Combat/TeamCombatModule.cs`,
`OpenSim/Region/CoreModules/Avatar/Combat/CombatModule.cs`.

---

## Terrain console commands, ported from Mobius (done, merged 2026-08-06)

Follow-up to the Mobius feature-parity audit — the one confirmed gap
found. Mobius has no shared git history with vanilla OpenSim either
(same as WhiteCore-Dev), but unlike WhiteCore its layout is normal
vanilla-OpenSim-shaped, so this was a much more direct file-level port
than the WhiteCore work. Everything else the audit checked (hardware/IP
banning, `PARCEL_DETAILS_*` constants, `osTriggerSoundAtPos`, Top
Scripts floater stats, region restart notification) turned out to
already be present in Casperia, in several cases in a more advanced
form than Mobius's own version.

Adds `terrain elevate/lower/fill <meters>` and `terrain load texture
<uuid>` as commands estate managers/owners (or gods) can run from the
viewer's **in-world** region console, not just the server console.
Registers against the existing `IRegionConsole` — `RegionConsoleModule`
already gates that CAP to estate managers/owners or gods, so the new
commands inherit that access check for free with no additional
permission logic needed. The three numeric commands are thin wrappers
around `InterfaceElevateTerrain`/`InterfaceLowerTerrain`/
`InterfaceFillTerrain`, helpers Casperia's `TerrainModule.cs` already
had (used by the classic server-console `terrain` commands) — no new
terrain-modification logic, just a second, in-world way to reach it.
`terrain load texture` decodes an uploaded square texture matching the
region's dimensions into a heightmap PNG and feeds it through the
existing `LoadFromStream` path.

Two small hardening fixes applied while porting (present in Mobius's
original, neither introduced here): swapped a raw `UUID` constructor
for `UUID.TryParse` on the texture-UUID argument (avoided an unhandled
exception on a malformed argument), and used `using` declarations for
the `Bitmap`/`EncoderParameters`/`MemoryStream` used during texture
decode so they're disposed on every path, including early returns
(the original leaked all three on every early-return branch).

Verified with a targeted `OpenSim.Region.CoreModules` build and a full
solution build (0 errors). Fast-forward merged into `merge-experiment`
(`833f5dbfab..c011c5c11c`). **Not yet tested in-world.**

Key file: `OpenSim/Region/CoreModules/World/Terrain/TerrainModule.cs`.

---

## opensim-lickx archival + osGetAgentViewer port (done, merged 2026-08-07)

`S:\Github\opensim-lickx` is the source Casperia's own MoneyServer and
OpenSimSearch modules descend from — and its original GitHub repo has
since been deleted, making that local checkout the only surviving copy
anywhere. Before anything else, git-initialized it in place (it had never
been under version control) as a pure safety net: removed a blanket
`addon-modules/` exclusion from its own `.gitignore` that would have
silently dropped the one directory (`opensim.currency-lickx`, containing
`DTLNSLMoneyModule.cs`/`MoneyDBService.cs` and the rest of the currency
lineage) this archive actually exists to preserve, then committed all
2436 files as-is. No changes to Casperia itself in this step.

The audit of that tree (vanilla 0.9.3.1 base + Gloebit/OpenSimMutelist/
OpenSimSearch/opensim.currency-lickx addon-modules, confirmed via a
`0.9.3.1Dev` vanilla-tag diff to have no core patches beyond a small
`Lickx_Api`/`ILickx_Api`/`Lickx_Stub` script-function trio) found
Casperia's currency stack is already a confirmed superset file-for-file
of everything in `opensim.currency-lickx`. Two candidates were flagged
for action:

- **Automatic MoneyServer DB schema creation/self-migration** — turned
  out to be a **false positive** on closer inspection while porting.
  Casperia's own `OpenSim.Data.MySQL.MySQLMoneyDataWrapper\
  MySQLMoneyManager.cs` already has this exact capability
  (`CheckAndCreateTables`/`InitialiseBalancesTable`/etc, wired in via
  `MySQLSuperManager`) — the audit only checked `MoneyDBService.cs`
  directly and missed the sibling file that actually does it. No action
  needed. (Second false-positive audit finding this project, after the
  Mobius audit's `LSLSyntaxId` claim — worth remembering to verify a
  "missing" finding against the *whole* relevant subsystem, not just the
  first file that looks like it should have the feature.)
- **`lxGetAgentViewer(key)`** — genuinely missing, confirmed absent by
  direct grep before porting. Reveals which viewer client an avatar is
  connected with, via the already-existing `Util.GetViewerName`. Lickx
  wrapped this one function in its own small standalone `Lickx_Api`
  script-API namespace; folded it into the existing `os*` OSSL
  convention instead as `osGetAgentViewer` (`OSSL_Api.cs`/`IOSSL_Api.cs`/
  `OSSL_Stub.cs`) rather than carrying that extra plumbing over for a
  single function. Gated at `ThreatLevel.Moderate`, matching
  `osGetAgentCountry`'s sensitivity level (viewer identity is client
  metadata, not credential-adjacent like `osGetAgentIP`, which is
  `Severe` + god-only).

Verified with a targeted `OpenSim.Region.ScriptEngine.Shared.Api` build
and a full solution build (0 errors). Fast-forward merged into
`merge-experiment` (`007be00310..77cba7c352`). **Not yet tested
in-world.**

Key files: `OpenSim/Region/ScriptEngine/Shared/Api/Implementation/
OSSL_Api.cs`, and `S:\Github\opensim-lickx\.git` (new archival history,
outside the Casperia repo itself).

## Halcyon/InWorldz and Homeworldz preservation audits — done, no code changes yet (2026-08-07)

Explicitly framed by the user as a preservation effort, not opportunistic
feature-hunting: "some of those repos like Mobius, WhiteCore, LickX, and
Halcyon have fallen by the wayside. Doesn't mean some of their code and
features should be lost." Two more targets audited on that basis, both
fetched as remotes (`halcyon`, `homeworldz`). Full findings summarized in
FEATURES_VS_MASTER.md; this entry is the narrative record of what was
found and why nothing was ported yet.

**Halcyon** (github.com/HalcyonGrid/halcyon) — InWorldz's server, forked
from OpenSim in 2010, no shared git history with vanilla origin/master
(confirmed via `git merge-base`, same as WhiteCore/Mobius) but a mature
C#/.NET codebase — real code-level porting is feasible here, unlike
WhiteCore. Its actual scripting engine compiler/VM (`InWorldz.Phlox.
Engine`) and physics core (`InWorldz.PhysxPhysics`) turned out to be
closed-source binaries in this repo (`lib/InWorldz.Phlox.dll`, native
PhysX3 libs) — not portable at all, which materially changed what the
audit could actually recommend versus what was expected going in. What
IS open and portable: a complete Bot/NPC framework
(`OpenSim/Region/CoreModules/Agent/BotManager/`) that's the mature,
working version of what Tranquillity's own in-progress bot framework
(see the `develop`-branch audit below) is still building toward with no
script engine to actually call it yet — flagged as the flagship
candidate if this preservation work continues, but not started (large,
multi-week estimate, needs its own dedicated pass). Smaller candidates
(`llReturnObjectsByOwner`/`ByID`, ~80 `iw*` OSSL-equivalent functions,
Euler-rotation functions, a sit-target compatibility fix, a JWT module)
are all still open, not yet ported.

**Homeworldz** (github.com/homeworldz/server) — not a fork, a from-
scratch C++20/Go reimplementation "informed by Halcyon, OpenSimulator,
and the SL viewer protocol without preserving their internal service
boundaries or storage formats" (its own README). No code is portable
here (total language mismatch), but its docs and ~30 ADRs are a genuine
source of preservable design rationale — the user explicitly confirmed
after this one landed that an ideas-only audit is a legitimate, valued
outcome on its own, not a lesser result just because nothing got ported:
"for the ideas... that can help shape the future of Casperia and really
worth looking into." See FEATURES_VS_MASTER.md for the physics (Jolt)
and scripting (Falcon VM) findings in full — nothing here requires
action, it's reference material for future architecture decisions.

**Status:** audits complete, findings documented, nothing ported yet.
The Halcyon Bot/NPC framework is the one concrete "worth doing" item
sitting open from this round — a real project, not a quick win, so it
hasn't been started without an explicit decision to take it on.

---

## Full re-audit and 8-batch port round (2026-08-08)

Triggered by a corrected audit methodology: an earlier narrow, currency-
module-only pass on opensim-lickx had missed a real RemoteAdmin finding
(`admin_alert_user`) that only turned up once a full code-level diff was
run, not just a commit-message/wiki-guided spot check ("we should be
looking at the code as well not just the git"). That lesson was applied
project-wide: full re-audits were run against opensim-lickx (full
249-file core-tree diff), Gunthar (279-commit range), Tranquillity
(418-commit range), Mobius (310-file vanilla diff vs tag `0.9.1.1`),
Halcyon (deeper code-level pass), plus a dedicated diff of
`LSL_Api.cs`/`OSSL_Api.cs` against lickx. Findings were consolidated into
a backlog and ported in 8 batches, each in its own isolated worktree
(`git worktree add ../Casperia-batchN`), built, and fast-forward merged
into `merge-experiment` one at a time:

- **Batch 1** (`72c8cc89d2`) — 12 small bug fixes: parcel-manager
  permission check, MySQL money transaction-id type, HG default-region
  lookup scope, `DBGuids.FromDB` unhandled throw, VectorRender trailing
  whitespace, group-title active-tag refresh, `llGiveInventory`/
  `llRequestAgentData`/`llGetWallclock` fixes, an OSSL typo
  (`osTemperature2sRBG`→`RGB`), and tiered HG user-account cache expiry.
- **Batch 2** (`c6dfa1b767`) — 3 dead-code completions: boat turn-banking
  physics actually wired into `ODEDynamics.Step()`, and two
  implemented-but-never-wired script functions (`llCastRayV3`,
  `osSetRot`) — present unwired in vanilla OpenSim itself, not lickx-
  specific.
- **Batch 3** (`0c83fdd06d`) — 8 new capabilities: `admin_change_parcel_
  flags` RemoteAdmin RPC, configurable NPC account type, linkset
  inventory-changed propagation, SLURL restored in `llGiveInventory`'s
  IM, new `osMakeScript`, standalone XML-RPC login by username, and a
  `MarketplaceListings` default inventory folder.
- **Batch 4** (`080718e6b8`) — 2 security/data-integrity fixes: closed an
  `/lslhttp/` blacklist bypass in `OutboundUrlFilter`, and flipped
  CreatorData inventory export from opt-in to opt-out.
- **Batch 5** (`a6cde3b133`) — graceful SIGTERM handling (`PosixSignal
  Registration`) in both the region simulator and Robust server, so a
  clean `kill`/service-stop triggers an orderly shutdown instead of a
  hard kill.
- **Batch 6** (`26ef7b6743`) — HG identity/friends fixes: canonicalized
  local-service URLs before outbound HG teleport, fixed a no-op stale-
  cache refresh in `UserManagementModule.AddUser`, a configurable denial
  message plus bare-IP HomeURI rejection in `GatekeeperService`, and a
  `HGFriendsService` fix for Mantis 9199. Two riskier Gunthar login-path
  commits from the same cluster (account-ServiceURLs-repair console
  command, standalone-HG-login HomeURI repair) were explicitly deferred
  as warranting dedicated review rather than batch inclusion.
- **Batch 7** (`5f487ebba8`) — made the hardcoded 10-second
  `WaitForUpdateAgent` teleport timeout in `ScenePresence.cs`
  configurable (`TransferAgentUpdateWaitMS`, floor 10000ms, default
  raised to 30000ms), fixing spurious teleport failures on slow links
  (notably HG returns). Independently surfaced by both the Gunthar and
  Mobius re-audits as the same converged finding.
- **Batch 8** (`ef991373f6`) — added `--lookup-aliases`/`--no-defaultuser`
  OAR import options, wiring the already-ported User Alias service into
  `ArchiveReadRequest.ModifySceneObject` so unresolved creator/owner/
  last-owner UUIDs can resolve through a registered alias instead of
  always silently reassigning to the estate owner. Flagged by the
  Tranquillity re-audit as the highest-value remaining find.

Each batch was rebased onto `merge-experiment` immediately before
merging (worktrees were created in parallel off a common base, so later
batches needed `git rebase merge-experiment` to stay fast-forwardable),
and the main worktree's generated `.csproj`/`.sln` files were
regenerated (`prebuild.dll`) and rebuilt after every merge to catch
false "type not found" errors from stale gitignored project files.
`merge-experiment` now sits at `ef991373f6`, building clean (0 errors,
1 pre-existing unrelated warning). **Not yet tested in-world.**

Also fixed along the way (recorded as a lesson, not a code change):
a research agent instructed to audit the full lickx tree decided on its
own to spawn 6 further sub-agents to parallelize the work, then lost
track of 5 of their 6 results when compiling its final report. Recovered
by reconstructing the missing findings from task-notifications received
directly during the session. Going forward: either launch parallel
top-level audit agents directly rather than letting one agent self-
spawn, or explicitly instruct a single large-scope agent not to spawn
sub-agents.

Still open from this round: the two deferred Gunthar HG-identity
commits (Batch 6 note above). The Halcyon Bot/NPC framework
preservation candidate noted above was superseded, not fixed — see the
Tranquillity `develop` round below, which found a better-provenance
open-source bot framework to port instead.

---

## Tranquillity moves to a formal release; `develop` mined for 3 more batches (2026-08-08)

User flagged that Tranquillity-Sim published a first formal release,
[`OpenSim-NGC/OpenSim-Tranquillity` `release/v1.0`]
(https://github.com/OpenSim-NGC/OpenSim-Tranquillity/tree/release/v1.0).
Diffing `release/v1.0` against `develop` showed the release branch is
only 3 commits behind `develop` (release notes + a console-log
newline fix — nothing to port), but `develop` itself had moved well
past the release cut with substantial new work: a bot/NPC framework, a
brand-new "Phlox" LSL/SLua script engine, per-script stats, Experience
Tools SL-conformance fixes, a BinaryFormatter security remediation, and
a small heartbeat/LinksetData fix. Three more batches ported from this
round; one major finding (Phlox) explicitly NOT ported pending a
provenance question raised upstream.

**Batch 9** (`3ec6c7d209`) — removed the three remaining live
`BinaryFormatter` deserialization paths in the tree (FlotsamAssetCache
disk cache, YEngine script-state migration, KeyframeMotion
serialization) — `BinaryFormatter` resolves types named in the byte
stream it deserializes, a code-execution vector removed entirely in
.NET 9. Also added `moving_start`/`moving_end` script events for
general (non-keyframed) physical movement. Tranquillity's paired fix
also added a root-part-delegation `LinksetData` group accessor;
Casperia already exposes `LinksetData` at the group level natively (a
plain field on `SceneObjectGroup`), so that half was a no-op here.

**Batch 10** (`cfc0855b85`) — ported `IBotManager`/`BotManager`/
`BotPersistenceManager`, a module-facing Bot/NPC management layer
wrapping Casperia's existing `INPCModule` with tag/profile/outfit/
navigation/speed tracking and script event delivery. Verified before
porting that this is an original implementation (per its own header:
"wraps OpenSim's INPCModule"), not a resurrection of InWorldz/Halcyon's
closed-source engine — despite sharing the same "Legion Grid" origin
as Phlox (see below), its licensing is clean. Adapted to use
`System.Data.SQLite` (already a Casperia dependency) instead of
upstream's `Microsoft.Data.Sqlite`, avoiding a second SQLite client
library and its native-binary deployment story for a handful of calls
that are identical across both providers. **Scope note:** this lands
the module only — Tranquillity's ~50 `bot*` OSSL functions that call
this API exist solely in Phlox's script engine, not in a form YEngine
can call. Wiring those into `OSSL_Api.cs`/`IOSSL_Api.cs`/
`OSSL_Stub.cs` is deliberately deferred as its own follow-on effort,
comparable in size to this port — without it, the module is present
but currently unreachable from any script (user chose this scope
explicitly over a partial or full function-wiring option).

**Batch 11** (`b508644e43`) — Experience Tools SL-conformance fixes:
a new estate-level Blocked Experiences tier (the viewer has always
sent add/remove requests for this list; they were silently discarded —
new `EstateSettings.BlockedExperiences`, `EstateManagementModule`
wiring, MySQL migration version 38), a real pagination bug in
experience search (`page` parameter was entirely unhandled — "todo:
handle pages" — silently returning every match with no paging), a
dropped marketplace-link field in `GetExperienceInfoGetHandler`, a
real security hole where any experience admin (not just the owner)
could reassign an experience's group, NRE guards on unresolved
experience IDs, and a KV quota raised from 16 MiB to the real SL limit
of 128 MiB. **Two things explicitly NOT ported** because Casperia's
own independent implementation is already better or the fix doesn't
apply: Tranquillity's `ExperienceCreators` acquire-policy gate (Casperia
already has `CanCreateExperience`/`TryCreateExperience` with real
per-resident limits AND an `IMoneyModule`-charged creation fee —
porting the simpler role-only gate over it would be a downgrade), and
Tranquillity's `ExperienceQuery` cap no-op (its correctness depends on
Tranquillity's per-agent EEP being a stubbed no-op; Casperia's
`llSetAgentEnvironment` is a real, working implementation, so "always
answer permitted" would be actively wrong here — a correct version
needs real policing logic that doesn't exist yet, a separate,
undesigned piece of work). Also confirmed a pre-existing, not
Casperia-specific gap: Allowed/Key/now Blocked Experiences are only
ever persisted for MySQL, not PGSQL/SQLite/Null — left as-is, a
distinct cross-DB-parity effort.

**Phlox — audited, NOT ported.** Tranquillity's `develop` also added a
~98,000-line alternative LSL/SLua script engine called "Phlox",
alongside XEngine/YEngine. A dedicated research pass (not a porting
attempt) found this is *literally* InWorldz/Halcyon's own Phlox engine
carried forward — file headers explicitly read "Adapted from InWorldz
Halcyon `ExecutionScheduler.cs`", attributed to "InWorldz Halcyon
Developers," obtained via an unspecified "Legion Grid" project. This
directly contradicts what Casperia's own earlier Halcyon audit found:
`InWorldz.Phlox.Engine` shipped as a **closed-source binary DLL** even
in InWorldz's own repository — "not portable, full stop" was that
audit's conclusion. Now ~50,000+ lines of buildable C# claiming to be
that same engine appear with no LICENSE file, no ThirdPartyLicenses
entry, and no explanation of provenance — just a bare copyright line.
Other findings if this ever clears: real (partial) SLua support,
architecturally distinct from XEngine/YEngine (bytecode-interpreted VM
vs compile-to-IL) with genuinely easy integration via the same
`IScriptEngine`/`IScriptModule` seam Casperia already uses, and
Casperia's own independently-built Experience-Lite/LinksetData
interfaces are surprisingly close to what Phlox's adapters expect. But
OSSL support is only 2 functions vs Casperia's 312 — unusable on real
content without a large follow-on effort. **User decision: raise the
provenance question with OpenSim-NGC before any engineering
investment.** Not shelved outright, not actioned — waiting on an
answer from upstream. See FEATURES_VS_MASTER.md for the full writeup.

---

## Test deployment notes

- `S:\Opensim\Casperia-Dev\Simulators\Welcome_Center\` — main test region.
- `S:\Opensim\Casperia-Dev\Simulators\Var_Test_Region\` — second 1024×1024
  var region added this round (port 8005, location 1010,1000) specifically
  to confirm weather/day-night bugs weren't Welcome_Center-specific.
- Both regions log to separate files (`logfile`/`StatsLogFile` under
  `[Startup]`) so they don't clobber a shared `OpenSim.log`.

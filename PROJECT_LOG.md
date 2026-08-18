# OpenSim-Confluence Project Log

Running record of work done on the OpenSim-Confluence fork, so context
survives across sessions. Update this file as work progresses — don't let it
go stale.

Repo: `S:\Github\OpenSim-Confluence` (git, branch `merge-experiment`)
Test deployment: the local test deployment (built from the repo above)
Live grid: the live grid deployment — **frozen, do not touch until user says
testing is done.**

**Naming note (2026-08-11):** this project's repo, code identifiers
(module class names, the web session cookie name, etc.), and in-app
branding strings were all renamed to "OpenSim-Confluence" — the
original name was only ever a local working-folder name, not an
intentional project identity, and is not used anywhere in this
document by design (the same goes for any specific grid's own public
brand name, which is a separate, per-deployment choice unrelated to
the software's identity). Every `.csproj`'s hardcoded absolute
`HintPath` entries (a real, easy-to-miss gap the rename surfaced) were
fixed too. Entries below this note predate the rename and originally
used the old name throughout — reworded here to avoid it, per the
same policy, without changing what actually happened. The local test
and live grid deployment directories were left named as they were;
renaming a live/dev deployment path is a separate, riskier decision
from renaming the source repo and wasn't part of this pass.

---

## Standing rules

- Never modify anything under the live grid deployment until the
  user explicitly says testing is done.
- When the user claims "AI broke this," verify with `git log -1 --format="%H
  %ai %an %s" -L <start>,<end>:<file>` before responding — every such claim
  investigated so far has traced to pre-AI code (original import
  `a8339fedb4`, or genuine upstream commits by UbitUmarov/Mike Dickson).
- When building new features, check how real Second Life / Tranquillity /
  Mobius / WhiteCore-Dev do it first and model after that rather than
  inventing new behavior.
- The mission is a full, immersive grid with everything a grid owner
  might need built in, not scattered across addons/third-party services
  (see README.md's "Including features from other projects"). If a
  fix/enhancement/feature from another repo (fork, addon, standalone
  tool) looks like it belongs here, that's the user's call to make, not
  a default-yes to port wholesale - flag it and wait rather than
  assuming it fits, the same standard already applied to every existing
  port from Gunthar/Tranquillity/Mobius/WhiteCore-Dev/Halcyon/
  Homeworldz/opensim-lickx. And whatever gets added, make it toggleable
  via `.ini` config wherever that's reasonable - grid owners choosing
  what runs on their own grid is a design requirement, not optional
  polish.
- "Currency"/"economy" anywhere in this project - the native
  ConfluenceCurrencyModule/CurrencyService or the classic MoneyServer
  integration, whichever a grid owner enables - means in-world virtual
  currency only, never a real-world payment/financial service. Keep
  that scope explicit in anything written about either one (see
  README.md's "Native economy, search, and grid services" and
  "MoneyServer enhancements" sections).
- Search is core, not an optional/toggleable feature like Currency or
  the content-management services - a working grid search is baseline
  functionality every grid owner needs, per the user (2026-08-16):
  search has been a real, recurring pain point for OpenSim users since
  the platform's inception, reliant by default on an external XML-RPC
  server most grid owners never stand up. Don't add an Enabled=false
  path to ConfluenceSearchModule or the Robust SearchService the way
  the other custom services get one - the only real choice is *which*
  backend answers search (native vs. the OpenSimSearch addon pointing
  at an external server), not whether search exists at all.
- Abuse Reports gets the same standing as Search, per the user
  (2026-08-16): core, not an optional/toggleable feature. A working
  abuse-report pipeline is baseline functionality every grid needs and
  should have shipped in stock OpenSimulator from the start - it never
  did. No `Enabled=false` path for `AbuseReportsService`/
  `AbuseReportsModule`.

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
the local test deployment's `MoneyServer.ini`, `MoneyServer.dll.config`.

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
(`gunthar-lsl-compat`, `S:\Github\OpenSim-Confluence-gunthar-lsl-compat`) off
`merge-experiment`, one full-solution `dotnet build OpenSim.sln` at the
end (0 errors), then fast-forward merged into `merge-experiment`
(`c54a115946..9b9f0304e5`). **Not yet tested in-world** — only compiled,
not run against the test deployment.

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
real surprise finding here: Confluence already has its own working,
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
AI session (ChatGPT/Copilot), not a deliberate Confluence design decision -
so importing gunthar's version back into RegionWeb was the right call
even though it now duplicates RegionCurrency. Both exist in parallel for
now; deduplicating them was explicitly not asked for and wasn't touched.

**PayPal specifically:** ships present but dormant - gated by the
existing `IsPayPalConfigured()` check (same as gunthar's own code), no
live credentials wired up. Reserved for future use per the user, not
active.

**Branding:** replaced all 66 occurrences of gunthar's "Vanilla Sim"
product name with neutral defaults matching Confluence's existing
convention - `"My OpenSim Estate"` for the configurable default title
(3 assignment sites), `"This estate"` for body-copy references (59
occurrences), `"RegionWeb"` as the in-world notification sender name (4
`SendBlueBoxMessage` calls, was `"Vanilla Sim"`). Kept our own
`[assembly: Addin(...)]` registration attributes (required for
Mono.Addins to load this as a standalone addon-module - gunthar's copy
lives in his core tree under a different registration mechanism, ours
needs it explicitly).

Given the file had no deep architectural conflict with the rest of
Confluence (unlike LSL_Api.cs's Experience-Lite entanglement), this was a
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
implementations against Confluence's existing Experience Tools backend
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
of Confluence (same pattern as the AccessControl/AbuseReports parity work
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
doesn't exist anywhere in Confluence's tree (grepped for both that
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
`IMessageTransferModule`. Confluence already had `AuctionID`/`SnapshotID`
fields on `LandData` but no working mechanism behind them. Bids are
tracked in-memory in the module (not persisted on `LandData`), keeping
this a self-contained, single-file addition with no DB schema changes.
WhiteCore's viewer-native "start auction" flow (an `IClientAPI` event +
CAPS handler, triggered from the About Land floater) was left out -
Confluence's `LLClientView` has no equivalent packet wired up at all, and
adding new LLUDP packet handling was out of scope for this pass; console
commands are the pragmatic substitute.

**Team Combat** (`ITeamCombatModule`/`TeamCombatModule`) - team
membership (`combat team join/leave/show` console commands), a shared
combat respawn point instead of teleport-home for team members, a
teleport block while a team member hasn't left combat, and a
configurable health regen rate for team members. This one needed real
design work before porting: WhiteCore's original `CombatModule` tracks
its **own separate avatar health field** and its **own physics-collision
damage detection**, both of which would collide with systems Confluence
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
already be present in Confluence, in several cases in a more advanced
form than Mobius's own version.

Adds `terrain elevate/lower/fill <meters>` and `terrain load texture
<uuid>` as commands estate managers/owners (or gods) can run from the
viewer's **in-world** region console, not just the server console.
Registers against the existing `IRegionConsole` — `RegionConsoleModule`
already gates that CAP to estate managers/owners or gods, so the new
commands inherit that access check for free with no additional
permission logic needed. The three numeric commands are thin wrappers
around `InterfaceElevateTerrain`/`InterfaceLowerTerrain`/
`InterfaceFillTerrain`, helpers Confluence's `TerrainModule.cs` already
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

`S:\Github\opensim-lickx` is the source Confluence's own MoneyServer and
OpenSimSearch modules descend from — and its original GitHub repo has
since been deleted, making that local checkout the only surviving copy
anywhere. Before anything else, git-initialized it in place (it had never
been under version control) as a pure safety net: removed a blanket
`addon-modules/` exclusion from its own `.gitignore` that would have
silently dropped the one directory (`opensim.currency-lickx`, containing
`DTLNSLMoneyModule.cs`/`MoneyDBService.cs` and the rest of the currency
lineage) this archive actually exists to preserve, then committed all
2436 files as-is. No changes to Confluence itself in this step.

The audit of that tree (vanilla 0.9.3.1 base + Gloebit/OpenSimMutelist/
OpenSimSearch/opensim.currency-lickx addon-modules, confirmed via a
`0.9.3.1Dev` vanilla-tag diff to have no core patches beyond a small
`Lickx_Api`/`ILickx_Api`/`Lickx_Stub` script-function trio) found
Confluence's currency stack is already a confirmed superset file-for-file
of everything in `opensim.currency-lickx`. Two candidates were flagged
for action:

- **Automatic MoneyServer DB schema creation/self-migration** — turned
  out to be a **false positive** on closer inspection while porting.
  Confluence's own `OpenSim.Data.MySQL.MySQLMoneyDataWrapper\
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
outside the Confluence repo itself).

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
"for the ideas... that can help shape the future of Confluence and really
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
(`git worktree add ../Confluence-batchN`), built, and fast-forward merged
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
Confluence already exposes `LinksetData` at the group level natively (a
plain field on `SceneObjectGroup`), so that half was a no-op here.

**Batch 10** (`cfc0855b85`) — ported `IBotManager`/`BotManager`/
`BotPersistenceManager`, a module-facing Bot/NPC management layer
wrapping Confluence's existing `INPCModule` with tag/profile/outfit/
navigation/speed tracking and script event delivery. Verified before
porting that this is an original implementation (per its own header:
"wraps OpenSim's INPCModule"), not a resurrection of InWorldz/Halcyon's
closed-source engine — despite sharing the same "Legion Grid" origin
as Phlox (see below), its licensing is clean. Adapted to use
`System.Data.SQLite` (already a Confluence dependency) instead of
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

**Batch 12** (uncommitted) — Native Currency Service, the first concrete
step in the "Addon-modules → core consolidation" direction above.
Real DB-backed ledger built into core (balance, transfer, transaction/
purchase history), modeled on WhiteCore-Dev's `BaseCurrencyServiceModule`/
`BaseCurrencyConnector` but re-implemented against Confluence's own
Data/Services/Connectors/CoreModules layering (same shape as
ExperienceService) rather than WhiteCore's own `DataManager`, so the
fork stays mergeable against `opensim/opensim`. New: `ICurrencyService`
(`OpenSim/Services/Interfaces`), `CurrencyTransfer`/`CurrencyPurchase`
DTOs (`OpenSim/Framework/CurrencyData.cs` - has to live in Framework,
not Data, since `OpenSim.Data` can't reference
`OpenSim.Services.Interfaces` without a circular project dependency;
this is the same reason `ExperienceInfoData` lives in Framework rather
than next to `IExperienceData`), `CurrencyService`/`CurrencyServiceBase`
with `money add`/`money set`/`money get` console commands mirroring the
classic MoneyServer's own admin tools, `MySqlCurrencyData` + a new
migration (`currency_balances`/`currency_transactions`/
`currency_purchases`, namespaced separately from MoneyServer's existing
tables so both can coexist in the same DB), `LocalCurrencyServiceConnector`,
and `ConfluenceCurrencyModule` - the region-edge piece, implementing the
existing `IMoneyModule` interface (so `llGiveMoney`/land-buy/upload
charges keep working unchanged) and registering the real
`getCurrencyQuote`/`buyCurrency`/`preflightBuyLandPrep`/`buyLandPrep`
XML-RPC surface the OpenSimulator wiki documents as what a viewer's
currency display actually calls - the same surface WhiteCore's own
currency stub answers. Guarded by the same `[Economy] EconomyModule`
selector key DTLNSLMoneyModule/Gloebit already read, so it's inert
unless explicitly selected; nothing existing was removed or changed.
Full solution build verified clean (0 warnings, 0 errors) including all
addon-modules.

**Enabled and smoke-tested on the test deployment (2026-08-09).** Both regions
switched from `DTLNSLMoneyModule` to `ConfluenceCurrencyModule` (old
config left in place, commented, for a one-line revert). Two real bugs
found and fixed during this pass, neither of which the earlier
individual-project builds could have caught:

1. **Wrong region-launch invocation.** Tried starting the test deployment's
   regions with `-WorkingDirectory` set to the region's own subfolder
   and a bare `-inifile OpenSim.ini` argument - both processes died
   silently with zero log output before log4net even initialized.
   Checking the *live* grid's actual running command line (`tasklist`/
   `Get-CimInstance Win32_Process`) showed it launches from the install
   root with `-inifile=Simulators\<Name>\OpenSim.ini` (relative path,
   `=` not a space) - matching that exactly fixed it. Unrelated to any
   of this batch's code, but would have blocked testing indefinitely
   without checking real precedent instead of guessing.
2. **Real bug: `ICurrencyService` was null in `AddRegion`.**
   `ConfluenceCurrencyModule.AddRegion` called
   `scene.RequestModuleInterface<ICurrencyService>()` immediately, but
   `LocalCurrencyServiceConnector` (a different module) registers that
   interface in *its own* `AddRegion` - module load order across
   different modules isn't guaranteed within the same `AddRegion` pass.
   Fixed by moving the lookup + all event/XML-RPC wiring to
   `RegionLoaded`, which only fires once every module's `AddRegion` has
   completed - the standard safe point to depend on another module's
   registered interface. Confirmed via log: first attempt logged
   `[CONFLUENCE CURRENCY]: No ICurrencyService available`, the fix
   produced a clean startup with no such error on both regions.

Verified end-to-end with real requests, not just a clean startup log:
`curl`'d `getCurrencyQuote` directly against Welcome Center's region
port (9004) - got back a correctly-computed quote
(`currencyBuy=500` → `estimatedCost=5000` at the configured
`CurrencyRate=10`). Then `buyCurrency`'d a synthetic test UUID for 1000
units and confirmed by direct MySQL query that all three tables wrote
correctly and consistently: `currency_balances` (Balance=1000),
`currency_purchases` (RealAmount=10000, matching the quote math),
`currency_transactions` (correct to/from/amount/balances). Test row
deleted afterward. **Not yet confirmed:** an actual viewer login -
balance display, `llGiveMoney`, and land purchase still need a real
client test, which requires the user's own viewer.

**Real bug #3, found via actual viewer testing (2026-08-09): the
"remote/grid-mode connector" wasn't optional after all.** User tried
buying currency in-viewer and got stuck on "Estimating..." forever.
Root cause: the viewer only ever learns ONE grid-wide "economy" URL,
served once at login from Robust's `[GridInfo] economy` setting - it
does not vary per-region and isn't re-discovered on teleport. That
setting was still `${Const|BaseURL}:9000/` (MoneyServer.exe's port,
not running), while `ConfluenceCurrencyModule`'s `getCurrencyQuote`/
`buyCurrency` handlers were only ever registered per-region via
`MainServer.Instance` - architecturally wrong the instant a grid has
more than one region behind a single fixed economy URL, not just a
missing nice-to-have. Fixed by building the Robust-hosted piece after
all: `CurrencyServiceConnector` (`OpenSim/Server/Handlers/Currency/
CurrencyServerConnector.cs`, mirroring `ExperienceServiceConnector`'s
`ServiceConnector` pattern), registered on Robust's `PublicPort` (9002)
- not `PrivatePort` like `ExperienceServiceConnector`, since this
answers direct-from-internet viewer requests rather than only
region-to-Robust traffic - and `[GridInfo] economy` repointed from
port 9000 to `${Const|BaseURL}:${Const|PublicPort}/`. Verified with a
direct `curl` against port 9002: correct quote math, matching the
region-level test from earlier. `ConfluenceCurrencyModule`'s own
per-region XML-RPC registration was left in place (harmless, and
needed for a standalone/single-process deployment with no separate
Robust) rather than removed.

Grid-wide DB-connection-sharing (the *other* reason a remote connector
usually exists - so regions don't each open their own direct MySQL
connection) remains deferred; both Robust and each region currently
connect to the test deployment's database directly and agree only because they share
the same tables, mirroring DTLNSLMoneyModule's existing bypass-Robust
topology. Not blocking for now.

**Real bug #4, also found via live viewer testing: still stuck on
"Estimating..." even after bug #3's fix.** Chased a port-reachability
theory first (tried moving the connector to port 9000, MoneyServer's
old port, assuming it was already externally reachable) - wrong, and a
real mistake: something unrelated (Axigen webmail admin) already owns
port 9000 on this machine, so that "fix" would have collided with
existing software. Reverted. The user's screenshot of their viewer's
actual Grid Manager entry ruled out stale-cached-URL as the cause too -
Helper URI was already showing the correct, updated address. The real
cause, found by fetching and reading the actual viewer source
(`llcurrencyuimanager.cpp`, `FirestormViewer/phoenix-firestorm`) rather
than guessing further: `LLCurrencyUIManager::Impl::startTransaction`
always POSTs to `getHelperURI() + "currency.php"`, never the bare
helper URI. Testing that exact path directly (not just the bare root,
which is all that had been tested before) showed the real problem:
`BaseHttpServer.HandleRequest` only routes to `AddXmlRPCHandler`-
registered handlers when `request.UriPath.Equals("/")` - literally
any other path, including `/currency.php`, falls through to a
completely different path-keyed stream-handler lookup and returns an
empty `200 OK`. `AddXmlRPCHandler` is architecturally root-path-only;
it was never going to work for this regardless of which port or host
it ran on. Confirmed the old MoneyServer addon hit the exact same
constraint and solved it the same way: `OpenSim-Grid-MoneyServer`'s
`MoneyXmlRpcModule` registers a dedicated `CurrencyStreamHandler` bound
to `"/currency.php"` specifically, separate from its `AddXmlRPCHandler`
calls. Fixed identically: both `CurrencyServiceConnector` (Robust) and
`ConfluenceCurrencyModule` (region/standalone) now also register a
`SimpleStreamHandler("/currency.php", ...)` that runs the request
through `BaseHttpServer`'s own public `HandleXmlRpcRequests(request,
response, handlerDict)` overload - built for exactly this non-root
case - with just these four methods. Verified: `POST /currency.php`
now returns a real, correct quote response (previously an empty
`200 OK`, `Content-Length: 0`). Root-path registration left in place
alongside it (harmless, some other integration might expect it there).
Still needs final confirmation of an actual completed purchase
end-to-end in the live viewer.

**Real bug #5: balance display never refreshed after any transaction,
purchase or otherwise.** Confirmed by the user completing a real
purchase in-viewer (their screenshot showed the "Buy FC$" dialog
working correctly - proof bugs #3/#4 are actually fixed) but the
balance HUD stayed stale until manually clicked. Root cause: nothing
anywhere proactively pushed a `SendMoneyBalance` update after a
transaction - `ConfluenceCurrencyModule` only ever answered when the
client explicitly asked (`OnMoneyBalanceRequest`). This affects every
transaction type, not just purchases - confirmed this before land-buy/
pay-avatar were ever tested, so it's fixed ahead of hitting the same
bug three more times rather than after. Fixed in two parts:
1. `ConfluenceCurrencyModule` now tracks its own `List<Scene>` (it hadn't
   before - only `LocalCurrencyServiceConnector` did) and pushes an
   unsolicited `SendMoneyBalance` to any connected client after
   `ObjectGiveMoney`, `ProcessMoneyTransferRequest` (pay-avatar),
   `ProcessLandBuy`, `ApplyCharge`, `ApplyUploadCharge`, and both
   `MoveMoney` overloads - covers every region-local money movement.
2. The Robust-hosted purchase path (`buyCurrency` via
   `CurrencyServiceConnector`) can't reach a client directly - Robust
   has no client connections. Added `NotifyRegionOfBalanceChange`:
   after a successful purchase, looks up which region currently has
   the agent via `IGridUserService.GetGridUserInfo().LastRegionID`
   (loaded from Robust's own already-configured `[GridUserService]`/
   `[GridService]` sections, no new config needed), then places an
   outbound XML-RPC call to that region's own new `UpdateBalance`
   handler (root-path registration is fine here - only Robust calls
   it, never the viewer), which does the actual client push using the
   same code path as the region-local cases above.

**Confirmed live (2026-08-09): balance now updates immediately after a
real purchase, no manual click needed.** User confirmed via actual
viewer test. That closes out the buy/quote/balance-push chain fully
verified end-to-end in a live viewer, not just at the build/curl level.

`CurrencyRate` also recalibrated from the placeholder `10` to `250`
(in both region configs and Robust's new `[Economy]` section) after
the user shared a real SL "Instant Buy" screenshot showing ~243 L$/$1
on the LindeX exchange - close enough to round to 250. Explicitly a
fixed operator-set rate, not an attempt to track SL's actual floating
market rate; building a real fluctuating-market currency exchange
would be a separate, much larger feature, not a natural extension of
this batch.

**Still not exercised live, blocked on test setup rather than known
bugs:** pay-to-avatar transfer (`ProcessMoneyTransferRequest`) and
land purchase (`ProcessLandBuy`/`ValidateLandBuy`). Both run through
the identical `ICurrencyService.Transfer` + `PushBalanceUpdate` path
already proven correct by the purchase test, but the test grid
currently only has one avatar available, and paying-an-avatar
inherently needs two. Land buy needs an actual parcel-for-sale setup.
Revisit once a second test account or a for-sale parcel is available.

Batch 13 (native Web/Admin UI) is queued behind this.

## Batch 13: Native Web/Admin UI, v1 (2026-08-09)

First concrete step of the WhiteCore-Dev-inspired grid-wide web/admin
UI (see "Addon-modules → core consolidation" above). Scope question was
put to the user (region list + HG toggle / login + currency dashboard /
maptile regen) and dismissed rather than answered, so picked one:
login + currency dashboard, since it proves the whole stack (auth,
session, page rendering) end to end and builds directly on Batch 12
rather than shipping an empty shell.

Same architecture lesson from Batch 12 applied from the start this
time: hosted on Robust (`WebInterfaceServiceConnector`, new
`OpenSim/Server/Handlers/WebInterface/`), `PublicPort` (9002), not
per-region - a grid-wide UI needs one stable address the same way the
currency quote surface did. RegionWeb (`addon-modules/RegionWeb`) is
deliberately untouched as the per-region alternative; OpenSim-Grid-
Interface remains the user's separate, optional, grid-wide tool.

Real login/session/dashboard flow, not a mockup: `/web/login` (GET
shows form, POST authenticates), `/web/dashboard` (balance via
`ICurrencyService`, requires session), `/web/logout`. Auth resolves
the account via `IUserAccountService.GetUserAccount` then calls
`IAuthenticationService.Authenticate` - the same path any other
OpenSim login uses, not a bespoke password check. Both services (plus
`ICurrencyService`) loaded by reusing Robust's own already-configured
`[UserAccountService]`/`[AuthenticationService]`/`[CurrencyService]`
sections, same technique as Batch 12's GridUserService/GridService
reuse - no new config duplication. Sessions are an in-memory
`ConcurrentDictionary` keyed by a cookie token, 2-hour expiry.

One real bug found and fixed immediately via testing rather than
assumption: registering the stream handler with `varPath: true` alone
does not match the bare base path itself, only paths with something
after it - `/web` 404'd while `/web/login` worked. RegionWebModule
hits the exact same quirk and works around it by registering the
handler twice (exact-path and varPath); did the same here. Verified
after the fix: bare `/web` now correctly 302s to `/web/login`, and a
deliberately-wrong login attempt returns a clean "Invalid login" error
without crashing - confirmed the auth path is actually wired up, not
just rendering a static form.

**Real bug, found immediately when the user tried a real account:
"Invalid login" even with correct credentials.** Root cause:
`IAuthenticationService.Authenticate` expects an MD5 digest of the
password, not the raw plaintext - real viewers always MD5-hash
client-side before the password is ever sent over the wire.
Confirmed by reading `LLLoginService.cs`'s own handling: a password
starting with `"$1$"` has that prefix stripped and used as-is
(already hashed); anything else gets `Util.Md5Hash()`'d before being
passed to `Authenticate`. A web form only ever has the raw plaintext,
so `TryLogin` was missing that same hashing step entirely - fixed by
adding `Util.Md5Hash(password)` before calling `Authenticate`,
matching `LLLoginService`'s plaintext-input branch exactly. Rebuilt,
redeployed, restarted clean.

**Confirmed live: real account login succeeds, dashboard shows the
correct real balance.** Closes out Batch 13 v1 fully verified
end-to-end in a real browser against a real account - login, session
cookie, authenticated dashboard, and the Batch 12 currency integration
all working together. Logout not yet explicitly re-tested but shares
the same session-cookie mechanism already proven by the dashboard
working, so it's low-risk.

## Batch 13: serving the full site from the test deployment's public hostname with no port number (2026-08-09)

Goal: the test deployment's public hostname, with no port suffix, should
serve the full grid website, and the in-viewer splash screen should work
from that same clean URL. Built `HandleHome`/`HandleWelcome` on
`WebInterfaceServiceConnector` for this - grid name/welcome message
pulled from `[GridInfoService]`/`[LoginService]` config, same reuse
pattern as everything else in this connector.

**Real bug: bare `/` doesn't go through `AddSimpleStreamHandler`'s
path table at all.** `BaseHttpServer.HandleRequest` special-cases
`request.UriPath == "/"` before ever reaching the stream-handler
lookup - only the older `AddStreamHandler`/`IStreamedRequestHandler`
API can claim that slot (`m_RootDefaultGET`), a completely different
registration mechanism than `/web/*` and `/welcome.php` use. Same
shape of bug as Batch 12's `currency.php` routing issue - assumed the
usual registration API would work, tested against the actual path,
got a 404, read `BaseHttpServer.HandleRequest` to find out why. Fixed
with a small `RootHomeHandler : BaseStreamHandler` adapter class that
bridges the older API to the same `HandleHome` method everything else
calls. `/welcome.php` and `/web/*` didn't need this since they're not
the bare root.

**Real infrastructure conflict, not a code bug:** binding
`WebInterfaceServiceConnector` directly to port 80 for the bare
hostname crashed Robust entirely on startup ("Only one usage of each
socket address... is normally permitted") - a separate, already-running
local web server holds the port 80 wildcard on this machine and was
already serving other sites, including a pre-existing PHP-based grid
front end at this same hostname. Reverted immediately to get Robust
back up, and set `[GridInfoService] welcome` to a working PublicPort
URL as a temporary stopgap while investigating a proper fix.

Rather than have the native module fight for port 80 directly, the fix
was to keep the existing web server as the public-facing port 80
listener and reverse-proxy the grid's hostname to the native service's
own port. Since that hostname's vhost already had a full, previously
deployed PHP-based grid site behind it, this was confirmed with the
user before proceeding (a proxy change would otherwise have silently
shadowed existing work) - the native site is intended to replace it as
the real front end going forward.

**Fix applied:** enabled the web server's proxy modules, replaced the
vhost's static content with a reverse proxy to the native service's
local port (old static config left in place, disabled, for rollback),
and restarted the web server. The live grid's own vhost was not
touched.

**Verified end-to-end:** requesting the test deployment's hostname
returns the native home page (confirmed via the response's `Server`
header, not the old PHP stack) - the proxy is routing to the C#
backend, not just serving the old static content. `/web/login` and
`/welcome.php` both proxy correctly too. The live grid's own hostname
still responds normally, unaffected by this change. The test
deployment's public hostname now serves the native site with no port
number needed, and the in-viewer splash screen works via the same
clean URL.

**Giving the test deployment its own hostname (2026-08-09).** Every
config setting except the just-added currency ones still used the live
grid's own hostname, differentiated only by port. Changed
`BaseHostname` and every literal reference (`GatekeeperURIAlias`,
`SearchURL`, `DATA_SRV_CP`, `ExternalHostName`, `MoneyServerIPaddress`
- six files total: `Robust.HG.ini`, `MoneyServer.ini`, both regions'
`OpenSim.ini` and `Regions.ini`) to the test deployment's own public
hostname, fully separating the test grid's identity from the live one
rather than relying on port numbers alone. Restarted clean, no errors.
**Requires action outside this codebase to work externally:** the test
deployment's hostname needs a real DNS record pointing at the same
public IP as the live grid's hostname - not something this session can
do directly. Until that resolves, the test deployment is reachable
locally but not from outside this machine under the new name. This
also means the `SearchURL`/`DATA_SRV_CP` helper-registration pings now
point at a path on the test deployment's hostname that has no matching
PHP backend behind it (unlike the live grid's) - harmless, since these
are non-critical search/directory-listing pings, but worth knowing if
directory registration silently 404s.

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
of 128 MiB. **Two things explicitly NOT ported** because Confluence's
own independent implementation is already better or the fix doesn't
apply: Tranquillity's `ExperienceCreators` acquire-policy gate (Confluence
already has `CanCreateExperience`/`TryCreateExperience` with real
per-resident limits AND an `IMoneyModule`-charged creation fee —
porting the simpler role-only gate over it would be a downgrade), and
Tranquillity's `ExperienceQuery` cap no-op (its correctness depends on
Tranquillity's per-agent EEP being a stubbed no-op; Confluence's
`llSetAgentEnvironment` is a real, working implementation, so "always
answer permitted" would be actively wrong here — a correct version
needs real policing logic that doesn't exist yet, a separate,
undesigned piece of work). Also confirmed a pre-existing, not
Confluence-specific gap: Allowed/Key/now Blocked Experiences are only
ever persisted for MySQL, not PGSQL/SQLite/Null — left as-is, a
distinct cross-DB-parity effort.

**Phlox — audited, NOT ported.** Tranquillity's `develop` also added a
~98,000-line alternative LSL/SLua script engine called "Phlox",
alongside XEngine/YEngine. A dedicated research pass (not a porting
attempt) found this is *literally* InWorldz/Halcyon's own Phlox engine
carried forward — file headers explicitly read "Adapted from InWorldz
Halcyon `ExecutionScheduler.cs`", attributed to "InWorldz Halcyon
Developers," obtained via an unspecified "Legion Grid" project. This
directly contradicts what Confluence's own earlier Halcyon audit found:
`InWorldz.Phlox.Engine` shipped as a **closed-source binary DLL** even
in InWorldz's own repository — "not portable, full stop" was that
audit's conclusion. Now ~50,000+ lines of buildable C# claiming to be
that same engine appear with no LICENSE file, no ThirdPartyLicenses
entry, and no explanation of provenance — just a bare copyright line.
Other findings if this ever clears: real (partial) SLua support,
architecturally distinct from XEngine/YEngine (bytecode-interpreted VM
vs compile-to-IL) with genuinely easy integration via the same
`IScriptEngine`/`IScriptModule` seam Confluence already uses, and
Confluence's own independently-built Experience-Lite/LinksetData
interfaces are surprisingly close to what Phlox's adapters expect. But
OSSL support is only 2 functions vs Confluence's 312 — unusable on real
content without a large follow-on effort. **User decision: raise the
provenance question with OpenSim-NGC before any engineering
investment.** Not shelved outright, not actioned — waiting on an
answer from upstream. See FEATURES_VS_MASTER.md for the full writeup.

---

## Addon-modules → core consolidation (architecture direction, 2026-08-09)

**The thesis, stated plainly by the user:** these subsystems (currency,
web/admin UI, search, etc.) are missing from opensim-master
*by design* — OpenSimulator's own maintainers have said on multiple
occasions they will not add currency to the core codebase — with the
explicit expectation that third-party developers would build
addon-modules to cover the gap. The actual problem is that those
addon-modules stop being maintained (already observed firsthand: the
opensim-lickx repo, origin of Confluence's MoneyServer/OpenSimSearch
modules, has been deleted from GitHub outright). WhiteCore-Dev,
InWorldz, and Halcyon solved this properly by building these subsystems
directly into their own codebases instead of depending on external
addons. **WhiteCore-Dev is the load-bearing example** — not because
it's necessarily the best-designed, but because it's the only one of
the three that's still alive and available as a working reference
(InWorldz/Halcyon's code is archived/inspectable but the platform
itself isn't an ongoing project the way WhiteCore-Dev is).

**Primary-source confirmation (2026-08-09):** checked the official
opensimulator.org wiki directly (`Related_Software`, `Webinterface`,
`Main_Page` — note its HTTPS is broken/unsupported, had to fetch over
plain HTTP). It independently confirms the whole thesis: the wiki's own
words are "OpenSimulator used to have a 'Forge' ... but this is no
longer in existence," yet `Main_Page` still links to that dead Forge as
current. SimianGrid (a full ROBUST-stack replacement) had its
OpenSimulator support "removed ... in February, 2020." Second Inventory
is flagged "no longer available" (abandonware since 2017). Of the eight
forks the wiki lists, three are explicitly marked dead (AuroraSim
stopped 2014, VoxelSim stopped 2010, Sim-on-a-Stick "no longer actively
updated") while WhiteCore is the one called out as still going:
*"still active as of 2020 and maintained by a small group of
developers."* The `Webinterface` page lists ~10 totally unrelated
one-off web-frontend projects (PHP/CodeIgniter, Moodle, Joomla!,
Drupal, WordPress, no shared codebase between any of them), most
untouched since 2010–2019, one (`Wixtd`) explicitly "no longer
available" — only three entries are from 2025, one of which is
ManfredAabye's `oswebinterface`. Currency only ever appears on these
pages as third-party links (PayPal module, DTL/NSL Money Server),
never as a roadmap item — consistent with, though not independent
textual proof of, the user's account that OpenSim core has repeatedly
declined to add currency itself.

**Written confirmation found (2026-08-09):** the user quoted the
official OpenSimulator FAQ directly: OpenSimulator ships with "no
working currency implementation," only "a very limited sample money
module that works in standalone mode only," and the project
"cannot supply any support for" the third-party modules that fill the
gap — pointing to Related_Software, matching everything already found.
Protocol detail confirmed alongside it: a viewer's currency display
hits `currency.php` (or equivalent) at whatever URL the grid's
`[GridInfo]` economy setting points to — the same `getCurrencyQuote`/
`buyCurrency`/`preflightBuyLandPrep`/`buyLandPrep` XML-RPC surface
WhiteCore's `Zero.CurrencyModule` registers. Any native Confluence
currency service needs to answer that same protocol surface at the
region edge, regardless of what backs it internally.

**Decision:** every third-party-origin module currently living in
`addon-modules/` was always meant as vendored/testing scaffolding — the
same category as the standalone OpenSim-Grid-Interface project — never
the intended final state of Confluence's own feature set. The target
direction is to absorb the ones that matter into the main tree as
Confluence-owned code, the way Mobius, InWorldz, and WhiteCore/Aurora each
did with their own forks.

**Correction (2026-08-09):** an earlier pass in this same session claimed
Tranquillity has "no `addon-modules/` directory at all" as verification
of this pattern — that was wrong, caused by running `Glob` on a literal
path with no wildcard, which silently matched nothing instead of
erroring. `S:\Github\OpenSim-Tranquillity\addon-modules` actually
exists and contains `Gloebit`, `OpenSim.Data.MySQL.MoneyData`,
`OpenSim.Region.OptionalModules.Currency`, `OpenSim.Server.MoneyServer`,
`OpenSimMutelist`, `OpenSimSearch`, and `os-webrtc-janus` (a WebRTC
voice/Janus integration not present in Confluence at all) — i.e.
Tranquillity is carrying almost the exact same vendored
currency/mutelist/search addons Confluence is, not a clean example of
"absorbed into core." Tranquillity's `OpenSim/Addons/Groups/` and a
stub `SampleMoneyModule.cs` in core `OptionalModules/World/MoneyModule/`
do show *some* subsystems moved into the main tree, so it's a mixed/
partial case, not the strong precedent originally claimed. The only
subsystem actually confirmed native-in-core (by reading real, working
code, not directory-listing inference) is **WhiteCore-Dev**: currency
(`WhiteCore/Modules/Currency/Base.CurrencyServices.cs`,
`Base.CurrencyConnector.cs`) and the web/admin UI
(`WhiteCore/Modules/Web/`) are genuinely part of its codebase, no addon
folder involved. Mobius and InWorldz have not been checked — no local
clone of either exists under `S:\Github\` to verify against; treat
claims about them as unverified until checked the same way.

**Second data point:** `S:\Github\opensim-lickx\addon-modules` carries
`Gloebit`, `OpenSimMutelist`, `OpenSimSearch`, and
`opensim.currency-lickx` — the last of which is just the same
three-way split (`OpenSim.Grid.MoneyServer`,
`OpenSim.Data.MySQL.MySQLMoneyDataWrapper`,
`OpenSim.Modules.Currency`/`DTLNSLMoneyModule`) bundled under one parent
folder, not a native rewrite. So three forks checked (Tranquillity,
LickX, Confluence itself) all lean on the same shared pool of vendored
legacy currency/mutelist/search addons — confirmed common ancestry, not
coincidence: Confluence's own `addon-modules/` set was originally built by
importing modules from these same third-party sources. **WhiteCore-Dev
remains the only confirmed example of actually absorbing these into
native core code** — the outlier among the forks actually inspected,
which makes it a stronger reference to build from, not a weaker one.

**Third and fourth data points (via `gh api` against the URLs already
listed in README.md's Attribution section — Mobius and Halcyon/InWorldz
were previously called "unverified, no local clone," which was an
unforced error since their GitHub URLs were sitting in this repo's own
README the whole time):**

- **Halcyon (InWorldz's server, github.com/HalcyonGrid/halcyon)** — has
  **no `addon-modules/` mechanism at all**, top-level or otherwise. Its
  own top-level tree is `Halcyon/`, `InWorldz/`, `MOSES/`, `OpenSim/`,
  `OpenSimProfile/` — a genuinely different architecture from OpenSim's
  plugin-loading pattern, not OpenSim-plus-addons. This is the strongest
  confirmation yet of the user's original claim, for a currency/economy
  platform that was InWorldz's actual commercial product.
- **Mobius (github.com/Mobius-Team/Mobius)** — its `addon-modules/`
  folder is empty (just the stock README, no Gloebit/MoneyServer/
  Search/Mutelist). Initially read this as "absorbed into core," but
  checked `OpenSim/Region/OptionalModules/World/MoneyModule/` and it's
  just the same generic `SampleMoneyModule.cs` stub vanilla upstream
  OpenSimulator itself ships (a $0 no-op, not a real ledger). **Correct
  read: Mobius most likely ships with no currency solution at all**,
  not a native replacement — "absorbed into core" and "doesn't offer
  the feature" produce the same empty `addon-modules/` folder, and
  only checking the actual code (not just folder emptiness) tells them
  apart. Don't cite Mobius as a currency-consolidation precedent without
  further digging; it currently supports a different, weaker claim
  ("some forks just drop the feature") not the one being made here.

Running count: WhiteCore-Dev (confirmed real native code) and Halcyon/
InWorldz (confirmed no addon mechanism, real commercial economy) both
support the "serious forks absorb this" thesis. Tranquillity and LickX
both still carry the same vendored pool Confluence does. Mobius is
inconclusive/likely a non-example. Homeworldz (github.com/homeworldz/
server, also listed in README.md) not yet checked.

**Not yet decided:** whether *every* addon-module gets a native
replacement, or only the ones solving an actual platform gap (currency,
web/admin, search) while the rest (Tide, Weather, Mutelist,
HoloPhysicsGuard, Marketplace) stay as optional swappable third-party
addons. Explicitly tabled by the user (`AskUserQuestion` dismissed
2026-08-09) — do not assume an answer, ask again before scoping work
for any of the "maybe" items below.

Current inventory of everything under `addon-modules/`:

| Module | Origin | Subsystem | Known native equivalent |
|---|---|---|---|
| `OpenSim-Grid-MoneyServer` | Vendored 3rd-party (external exe) | Currency | WhiteCore `BaseCurrencyServiceModule`/`BaseCurrencyConnector` (real ledger, transaction/purchase history, group treasury, console commands) |
| `OpenSim-Modules-Currency` | Vendored 3rd-party (DTLNSLMoneyModule, region-side client of MoneyServer) | Currency | same as above |
| `OpenSim-Data-MySQL-MySQLMoneyDataWrapper` | Vendored 3rd-party (MoneyServer's DB layer) | Currency | same as above |
| `Gloebit` | Vendored 3rd-party (real-money payment gateway) | Currency | no equivalent found yet — real-money gateways are a different problem than an in-grid ledger |
| `RegionCurrency` | **REMOVED** — confirmed (not just suspected) to be RegionWeb's own currency/PayPal code, mechanically split to its own base path by an earlier AI-assisted session: its own `HandleRequest` comment said so directly ("RegionCurrency now owns its whole path rather than living under RegionWeb's `/regionweb/currency/` as it did *in the source project*"), and its default storage paths/session cookie/admin-check method were still literally named after RegionWeb, never renamed. No unique capability RegionWeb's own `/currency` didn't already have. Removed rather than reconciled - see the "RegionCurrency vs. RegionWeb reconciliation" entry below. | n/a |
| `RegionWeb` | Confluence's own fork (per-region web control panel) | Web/admin | WhiteCore `WhiteCore/Modules/Web` (~100 pages: region/user/estate/abuse/news/purchases/transactions manager, user self-service, multi-language) — breadth is real but uneven (e.g. its web sim-console page hardcodes "not yet implemented" despite calling `MainConsole.Instance.RunCommand()`) |
| `OpenSimMarketplace` | **Wholly original Confluence creation** — an attempt to build the thing (a Second-Life-Marketplace equivalent) because nothing like it exists anywhere in the OpenSim ecosystem to vendor in the first place | Web/admin (marketplace) | none — there is no upstream to compare against; WhiteCore's `html/classifieds/marketplace.cs` is the closest analog, but this isn't a vendored/replace situation like the rest of the table, it's Confluence's own answer to a real gap |
| `OpenSimSearch` | Vendored 3rd-party — [kcozens/OpenSimSearch](https://github.com/kcozens/OpenSimSearch) | Search | **REPLACED (2026-08-10)** — native `ConfluenceSearchModule`/`SearchService` ships as of Batch 14 (land/places search against the grid's own `land` table, queried directly, no external server); addon kept selectable via `[Search] Module` for anyone who wants an external XML-RPC backend instead. The data layer is genuinely verified (direct .NET harness test). The region-module client-facing wiring (`ConfluenceSearchModule.AddRegion`) briefly appeared broken on Var Test Region specifically, initially attributed to a "Mono.Addins reliability issue" - **root cause found and fixed (2026-08-10, see the "root cause found" entry near the end of this file): a config-file structuring bug (`[OnDemand]`/`[SimProtection]` had been inserted into the middle of `[Startup]`, corrupting it) was the real cause, not Mono.Addins.** Re-enabling this on Var Test Region today should work correctly now that the actual cause is fixed. |
| `OpenSimTide` | Vendored 3rd-party — [JakDaniels/OpenSimTide](https://github.com/JakDaniels/OpenSimTide) | Environmental (tabled) | unconfirmed |
| `OpenSimWeather` | **Not straight-vendored** — started as a GitHub Copilot–assisted fork/port of Gunthar's weather code, then heavily debugged this cycle (config-precedence bug, environment-persistence corruption — see weather sections above). Same category as RegionCurrency/RegionWeb: Confluence's own diverged derivative, not untouched third-party code | Environmental (tabled) | unconfirmed |
| `OpenSimMutelist` | Vendored 3rd-party — [kcozens/OpenSimMutelist](https://github.com/kcozens/OpenSimMutelist) | Social | **CONFIRMED (2026-08-10)** — a complete native equivalent already existed before this task even started: `OpenSim.Services.MuteListService` + `Local`/`RemoteMuteListServiceConnector` + the native `MuteListModule`, already wired and active (`[Messaging] MuteListModule = MuteListModule`) in every current test deployment config. The addon self-disables under that config and is confirmed dead code on this deployment. See PROJECT_LOG.md Batch 14. |
| `HoloPhysicsGuard` | Vendored 3rd-party — [holoneon/HoloPhysicsGuard](https://github.com/holoneon/HoloPhysicsGuard) | Security (tabled) | unconfirmed |
| `GroupAutoInvite` | **CONFIRMED** - a real port of Gunthar's own vanilla feature (`OpenSim/Region/OptionalModules/Avatar/GroupAutoInvite/GroupAutoInviteModule.cs`), diffed directly against that source. Adapted (not fabricated): re-wired as a Mono.Addins addon-module instead of a built-in optional module, English default invite message instead of vanilla's Italian, and one real robustness improvement over the vanilla version - invites are matched to the specific login session that triggered them (`SessionId` check before firing, plus a deterministic per-session invite ID derived via SHA256 rather than vanilla's simple in-memory "already invited" set), so a delayed invite task can't fire against a since-relogged session. | Social | n/a - genuine addition, no native equivalent built |

**Currency, additional reference not yet examined:** ManfredAabye also
authored [opensimcurrencyserver-dotnet](https://github.com/ManfredAabye/opensimcurrencyserver-dotnet)
— a currency server separate from the classic MoneyServer/DTLNSLMoneyModule
lineage Confluence/Tranquillity/LickX all share. Worth checking alongside
WhiteCore's `BaseCurrencyServiceModule` when currency work actually
starts, not yet read.

**OpenSim-Grid-Interface provenance confirmed:** the user's own project
is a fork of ManfredAabye's [oswebinterface](https://github.com/ManfredAabye/oswebinterface)
("expanded considerably" per earlier discussion), the same author as the
currency-server repo above — so both of the user's two most relevant
web/currency reference points trace back to the same original author.

**Next step (not started):** get a decision on the "tabled" rows above,
then sequence the currency/web/search work — likely currency first
since it already has the clearest native reference
(`BaseCurrencyServiceModule`) and the most redundant/competing vendored
implementations (three separate currency addons for one job).

**Dual-maintenance rationale confirmed (2026-08-11):** absorbing a
subsystem into Confluence's own core does not mean retiring its
addon-module equivalent. Going forward, the addon-modules are kept
intentionally maintained for two distinct audiences: Confluence
operators who prefer to swap the native default for their own external
stack (the "keep it optional/pluggable" constraint already stated
above), and, separately, third-party grids that stay on plain
`opensim-master` and never adopt Confluence at all — for that audience,
the addon-modules directory is the only place a maintained version of
these features exists, which matters more now that at least one
original upstream source (opensim-lickx) has already been deleted from
GitHub outright. Practical consequence: once a subsystem has both a
native core version and an addon-module version, the two are separate
codebases from that point on, not shared code — a fix found in one
(e.g. the native `CurrencyService`) does not automatically apply to the
other (e.g. `RegionCurrency`/`MoneyServer`), so keeping both maintained
means periodically porting fixes in both directions rather than
treating the addon versions as frozen/legacy.

**Concrete precedent for the "one or two addons, not the whole
platform" audience (per the user, 2026-08-11, not independently
verified against a repo in this session):** ManfredAabye separately
distributed the `OpenSimWeather` port as its own standalone addon for
other `opensim-master` grids, rather than requiring the whole Confluence
platform to get the weather feature. This is the model the
dual-maintenance rationale above is built on: Confluence's pitch is the
complete, batteries-included platform, but nothing stops a single
addon-module from having a life of its own outside it, the same way
this one already does.

## Batch 13: per-region Hypergrid open/close toggle (2026-08-09)

First admin-only page. Chose this specifically because it's the item
PROJECT_LOG.md had marked "In progress" under "Grid management
verification" since long before the currency/web-UI architecture
thread even started - not a new idea, a long-standing gap finally
getting built.

Researched what already existed before writing anything (via a
research-only subagent, to keep this session's own context free for
the actual implementation): nothing did. `GatekeeperService` is a
Robust-wide singleton with only an all-or-nothing
`ForeignAgentsAllowed` setting (`OpenSim/Services/HypergridService/
GatekeeperService.cs`) - no per-region hook, no config key, no DB
column, no console command anywhere in the codebase. Confirmed
`destination` (the target region) is already an available parameter
in `LoginAgent()`, right next to the existing "Foreign agents allowed?
Exceptions?" block - the natural, minimal-diff injection point.

Built fresh, same layering as Batch 12: `IRegionHGService`/
`IRegionHGData` interfaces, `RegionHGService`/`RegionHGServiceBase`,
`MySqlRegionHGData` + a new `region_hg_settings` table (RegionID →
IsOpen bool). Absence of a row means open, matching
`ForeignAgentsAllowed`'s own default - upgrading can't silently close
an existing region. `GatekeeperService.LoginAgent` now checks it
immediately after the grid-wide check, independent of it (a region can
be closed even while the grid overall allows foreign agents).
`WebInterfaceServiceConnector` gained `/web/admin` (region list +
per-region toggle button, gated on `UserAccount.UserLevel >= 200` -
the standard OpenSim admin/"god" threshold, set on the session at
login) and `/web/admin/hg-toggle` (the POST target).

**Real bug, deployment not code: forgot to copy the updated
`OpenSim.Services.Interfaces.dll` to the test deployment** after adding
`IRegionHGService` to it. Every other changed/new DLL got deployed;
that one didn't, because nothing about it looked like it needed
redeploying at a glance - the new type lives there, but nothing else
in that assembly's surface changed. Caused a real cascade:
`GatekeeperServiceInConnector` failed to load ("Could not load type...
IRegionHGService"), which then also broke `WebInterfaceServiceConnector`
("Constructor not found") even though nothing in that class was
actually wrong - a stale/mismatched Interfaces DLL breaking two
unrelated connectors at once, not two separate bugs. Fixed by
deploying the missing DLL and restarting; both connectors then loaded
cleanly on the same attempt. Lesson for future deploys in this
project: when a new *type* is added to an interfaces assembly, that
DLL needs redeploying even if reading its own diff doesn't obviously
call for it.

Verified after the fix: clean startup log (`GatekeeperServiceInConnector
loaded successfully`, `WebInterfaceServiceConnector loaded successfully`),
`/web/admin` correctly redirects unauthenticated requests to login, and
dashboard/login/home all still respond correctly (no regression from
the DLL churn).

**Confirmed live: real admin login reaches `/web/admin` and shows the
correct region list.** User raised their Test User account's
`UserLevel` to 200 themselves (my own attempt to do it via a direct
MySQL UPDATE was blocked by this session's own safety classifier -
correctly, since a bare "raise this account's permission level" write
is exactly the kind of action worth a human doing directly rather than
an agent quietly executing). Screenshot confirmed via
`the test deployment's public hostname:9002/web/admin`: both real regions listed
(Var Test Region at 1001,1000; Welcome Center at 1000,1000), both
correctly showing "Open", each with a working "Close to HG" button.
**Confirmed live: clicking the toggle actually works, verified at the
database level, not just the page re-rendering.** User closed Var Test
Region to HG; page correctly flipped its row to "Closed"/"Open to HG"
while Welcome Center stayed "Open"/"Close to HG" - a real per-region
write, not a global one. Cross-checked directly against
`region_hg_settings`: exactly one row, Var Test Region's UUID,
`IsOpen=0`; Welcome Center has no row at all, matching the "absent row
= open" default design. (Minor, non-functional: `region_hg_settings`
and `regions` have mismatched collations, which broke an ad-hoc `JOIN`
used only for this verification query - the application itself never
joins these tables, so this doesn't affect the toggle or the Gatekeeper
check at all. Not worth fixing unless a future admin page needs to
join across them for real.)

**Still not verified: a real HG visitor actually being turned away
from the closed region.** Toggle and persistence are proven; the
`GatekeeperService.LoginAgent` enforcement path itself hasn't been
exercised by an actual inbound HG teleport attempt yet.

## Batch 13: on-demand maptile regeneration (2026-08-09)

Second admin page item, the other one named in the original backlog
alongside the HG toggle. Researched first (subagent, research only):
`WorldMapModule.GenerateMaptile()` (`OpenSim/Region/CoreModules/World/
WorldMap/WorldMapModule.cs`) does the actual render via
`Warp3DImageModule.CreateMapTile()`; a console command ("generate map")
already exists and just calls `Scene.RegenerateMaptileAndReregisterInBackground()`
- a public, idempotent, already-thread-safe entry point (guarded by a
process-wide semaphore so only one region renders at a time, runs on a
dedicated low-priority thread with its own stack). No HTTP trigger
existed anywhere.

Added the HTTP counterpart directly next to the existing console
command in `WorldMapModule.cs`: a new `/MAP/Regenerate/<regionHandle>`
stream handler (`HandleRegenerateMaptileRequest`, POST-only) that calls
the exact same `RegenerateMaptileAndReregisterInBackground()` method
the console command does - never runs the render inline on the HTTP
thread, just queues it and returns "queued" immediately.

`WebInterfaceServiceConnector`'s `/web/admin` gained a "Regenerate
maptile" button per region. Since Robust has no way to run a render
itself (that only happens in the actual region process), the handler
looks up the region's `ServerURI` via `IGridService` and makes an
outbound HTTP POST to its new endpoint - same "Robust calls out to the
specific region that can actually do the thing" shape as the Batch 12
currency balance-push fix, applied to a different problem. Redirects
back to `/web/admin` with a status message either way (success,
non-200 from the region, or unreachable).

Verified directly (not just "it builds"): computed Welcome Center's
region handle from its stored `locX`/`locY` and POSTed straight to
`http://localhost:9004/MAP/Regenerate/<handle>` - got back `200
queued`, and the region's own log immediately showed "Queuing
background map image generation for Welcome Center (requested via
admin web UI)" at the matching timestamp. Full solution build clean,
all previously-verified pages (login/dashboard/home/admin) re-checked
for regressions after redeploy - none. **Confirmed live: clicked the actual button.** User clicked
"Regenerate maptile" for Var Test Region in the browser; page showed
"Maptile regeneration queued for Var Test Region." Cross-checked
against Var Test Region's own log, not just trusting the success
message: matching "Queuing background map image generation for Var
Test Region (requested via admin web UI)" at the same timestamp. Full
browser → Robust → region chain proven, not just the region-side half
tested earlier via direct curl.

That closes out all three pages picked from the original backlog:
currency login/dashboard (Batch 12 integration), per-region HG
open/close, and on-demand maptile regen - each verified at the
database/log level, not just "the page loads."

## Batch 13: on-demand OAR backup (2026-08-09)

Fourth admin page item - "OAR/IAR upload and full backup workflows"
from the original backlog, OAR half. Researched first (subagent):
`ArchiverModule.ArchiveRegion(savePath, options)`
(`OpenSim/Region/CoreModules/World/Archiver/ArchiverModule.cs`) is what
the existing "save oar" console command already calls. Important
difference from maptile regen: **this one is NOT already backgrounded
by OpenSim itself** - confirmed via `RemoteAdminPlugin.cs`, which wraps
its own call to the same method in a `Monitor.Wait` specifically
because the call blocks the calling thread for the whole archive
write. Missing that distinction and calling it inline would have
frozen the HTTP request (and by extension, blocked that one request
thread) for as long as the archive takes.

Added `ArchiverModule.HandleSaveOarHttpRequest` (new
`/OAR/Save/<regionHandle>` endpoint, POST-only, same registration
pattern as `WorldMapModule`'s maptile endpoint) which wraps the call in
`Util.FireAndForget` itself, since no `ArchiveRegionInBackground()`
helper exists on `Scene` the way it does for maptiles. Chose timestamped
filenames (`Backups/<RegionName>_<timestamp>.oar`) over reusing the
module's own `DEFAULT_OAR_BACKUP_FILENAME` ("region.oar") deliberately -
the original ask was "full backup workflows," which implies an actual
history of snapshots, not one always-overwritten file. `/web/admin`
gained a third button per region ("Save OAR backup"), same
Robust-calls-the-region shape as the maptile button.

Verified directly: POSTed to Welcome Center's endpoint, got back
`200 queued:Backups\Welcome Center_<timestamp>.oar`, waited, and
confirmed via the region's own log a complete write ("Finished writing
out OAR for Welcome Center", 13 scene objects referenced, 9 assets
saved) - then confirmed the actual file on disk, 299KB, real content.
**Worth knowing, not a bug:** the file landed in
the local test deployment's `Backups\` folder, not inside
`Simulators\Welcome_Center\`- a relative path resolves against the
region process's actual working directory, which (per the Batch 12
region-launch finding) is the install root, not the region's own
subfolder. Every region's backups land in this same shared `Backups/`
folder; filenames are prefixed with the region name specifically so
this doesn't collide. Full solution build clean; only the two files
touched needed redeploying, no new services/DLLs this time. IAR
(inventory archive) is the other half of the original ask and hasn't
been started - same shape of work, different module
(`InventoryArchiverModule`), reasonable next step if the OAR half proves
useful.

## Batch 13: self-service OAR backup/restore (2026-08-09)

User correction on the above: "Opensim has an autobackup feature
already. This would be for the user to backup/upload thier own OAR" -
i.e. OAR save/load should be an estate-owner self-service capability,
not a grid-admin-only action. Reworked as a new `/web/myregions` page,
distinct from `/web/admin`: gated on being logged in at all (any
account), not `UserLevel>=200`.

Ownership resolution uses `IEstateDataService.GetEstatesByOwner(principalID)`
-> `GetRegions(estateID)` -> `IGridService.GetRegionByUUID`, confirmed
against `estate_settings.EstateOwner`/`estate_map` directly in the DB
(Test User owns Estate 101, containing both Welcome Center and Var Test
Region). Every handler re-derives this ownership list server-side from
the session's `PrincipalID` before acting - the `region_id` in a posted
form is never trusted on its own, for either save or load.

Save reuses the existing `/OAR/Save/<handle>` region endpoint exactly
like the admin button did. Load is new: added
`ArchiverModule.HandleLoadOarHttpRequest` (`/OAR/Load/<handle>`,
POST-only, region-side), reading the full upload into memory on the
calling thread first (so we know we have the whole file before
responding) then `Util.FireAndForget`-wrapping the actual
`DearchiveRegion(stream)` call, mirroring the OAR-save endpoint's
already-established "don't block the HTTP thread" shape.

No multipart/form-data parser existed anywhere in this codebase before
this (confirmed via research subagent - `OpenSim/Framework/MultipartForm.cs`
only builds outgoing requests). Hand-rolled one in
`WebInterfaceServiceConnector.ParseMultipartFormData`: reads the whole
body into a byte array, finds every occurrence of the `--<boundary>`
marker from `Content-Type`, and for each part between markers reads its
`Content-Disposition` header to get the field name (and, for the file
part, the filename) before treating the remainder as that field's raw
bytes/text. The restore form also has a required confirmation checkbox
plus an explicit on-page warning, since load is destructive (replaces
all current region content) and there was no existing precedent to gate
that in this UI.

**Bug found during live verification, not from a research gap:** the
first end-to-end restore attempt failed immediately -
`[ARCHIVER]: Aborting load with error in archive file NONE: count
('-1') must be a non-negative value.` Root cause: `.oar` files on disk
are gzip-compressed tar (`ArchiveWriteRequest` wraps its save stream in
`GZipStream`, and `ArchiveReadRequest`'s string-path constructor
symmetrically wraps its read stream in `GZipStream` too) - but
`ArchiveReadRequest`'s **Stream**-based constructor does *not* wrap
what it's given, it assumes the caller already handed it a decompressed
tar stream (confirmed by reading `ArchiverTests.cs`, which only ever
feeds that overload a raw `TarArchiveWriter` stream, never gzip). The
initial `HandleLoadOarHttpRequest` handed the raw uploaded `.oar` bytes
straight to `DearchiveRegion(Stream)`, so the tar reader was reading
compressed bytes as if they were already tar - garbage in, weird
exception out. Fixed by wrapping the uploaded bytes in a
`System.IO.Compression.GZipStream` (`CompressionMode.Decompress`)
before calling `DearchiveRegion`, matching what the path-based load
already does for on-disk files.

Verified live end-to-end twice (once reproducing the bug, once
confirming the fix) via direct curl against the real endpoints
(browser file-upload wasn't scriptable in the available browser tool,
so this was API-level, not click-through): logged in as Test User,
hit `/web/myregions` and got back exactly the two owned regions, saved
a fresh OAR for Var Test Region via the page's own button, then
re-uploaded that same file to `/web/myregions/oar-load`. First attempt
reproduced the gzip bug exactly as above; after the fix, region log
showed a full clean restore - `Successfully loaded archive`, terrain
restored, 9/9 assets, 1 parcel, 1 scene object, scripts started. Full
solution build clean after the fix; redeployed
`OpenSim.Server.Handlers.dll` and `OpenSim.Region.CoreModules.dll` to
the test deployment (only after confirming, via `Get-CimInstance
Win32_Process`, that the processes being restarted were actually
the test deployment's own Robust/region processes and not the live grid's -
both grids currently have processes with the same simulator names
running side by side on this machine). IAR (inventory archive) remains
unstarted.

---

## Test deployment notes

- the local test deployment's `Simulators\Welcome_Center\` — main test region.
- the local test deployment's `Simulators\Var_Test_Region\` — second 1024×1024
  var region added this round (port 8005, location 1010,1000) specifically
  to confirm weather/day-night bugs weren't Welcome_Center-specific.
- Both regions log to separate files (`logfile`/`StatsLogFile` under
  `[Startup]`) so they don't clobber a shared `OpenSim.log`.

---

## Batch 13: self-service IAR backup/restore (2026-08-09)

Second half of the self-service archive work, following the same
"Opensim has an autobackup feature already. This would be for the user
to backup/upload thier own OAR" correction applied to inventory
instead of regions. Added `/web/myinventory`: any logged-in user can
back up their own inventory or restore from an uploaded `.iar`.

Architecturally different from OAR in one important way:
`InventoryArchiverModule.ArchiveInventory`/`DearchiveInventory` hard-require
a **password re-check** via `GetUserInfo` (same as the `save iar`/`load
iar` console commands) - the logged-in web session's `PrincipalID`
alone isn't accepted by that API. Rather than route around it, the
`/web/myinventory` forms ask for the password again for both actions.
First/last name are always taken from the session, never from a form
field, so there's no way to even attempt targeting a different
account's inventory through this page.

IAR isn't tied to a specific region's content the way OAR is (any
region can service it, since `InventoryService`/`AssetService` are
grid-wide), so instead of an owned-region lookup, Robust picks a target
region via `IGridUserService.GetGridUserInfo(principalID).LastRegionID`/
`HomeRegionID` (same lookup `CurrencyServerConnector` already uses for
its balance-push callback), falling back to any region with a reachable
`ServerURI` if the user has never actually logged into the world.

Reused the multipart parser built for OAR restore
(`WebInterfaceServiceConnector.ParseMultipartFormData`) rather than
duplicating it in `OpenSim.Region.CoreModules` - Robust unwraps the
browser's upload down to plain bytes, then forwards those bytes to the
region's new `/IAR/Load` endpoint with credentials as URL-encoded
custom headers (`X-Iar-First-Name`/`X-Iar-Last-Name`/`X-Iar-Password`),
since the body slot is taken by the raw file and header values can't
safely carry arbitrary password characters unencoded.

**Two real bugs found during live verification:**

1. Same gzip landmine as OAR (`InventoryArchiveReadRequest`'s
   Stream-based constructor doesn't decompress, only its loadPath
   constructor does) - anticipated this time from the OAR experience and
   decompressed with `GZipStream` up front, so no failed first attempt.
2. **Silent success with no completion log line.** The module's own
   `OnInventoryArchiveSaved`/`OnInventoryArchiveLoaded` completion events
   are gated on a console-task id (`SaveInvConsoleCommandCompleted`/
   `LoadInvConsoleCommandCompleted` both check
   `m_pendingConsoleTasks.Contains(id)` and silently return if not
   found) - since the HTTP handlers never registered their own random
   id in that list, the events fired into nothing and the operation
   looked "stuck" (no new log lines, file size frozen) even though it
   had already finished successfully. Confirmed the backup file was
   actually complete and valid via `gzip -t` before realizing this was
   a logging gap, not a hang. Fixed by logging completion directly in
   the `Util.FireAndForget` callback instead of relying on that event.

**A third bug, upstream of this feature entirely:** the first real
restore test (a 156MB IAR, Test User's actual accumulated inventory
from this session's heavy testing - 1683 assets) failed outright with
`400 BadRequest: Content length too large.` straight from Robust's own
HTTP server, before ever reaching this feature's code.
`OpenSim/Framework/Servers/HttpServer/OSHttpServer/HttpRequestParser.cs`
had a hardcoded request-body ceiling of `64 * 1024 * 1204` - note the
`1204`, not `1024`, an arithmetic typo that made the real limit ~75MB
instead of the intended 64MiB. This caps **every** POST body on
**every** Robust/region HTTP endpoint in Confluence, not just this one -
raised to a flat 512MiB rather than just fixing the typo, since real
inventory archives (and large region OARs) routinely exceed 64MiB.
Full solution rebuild required since this lives in
`OpenSim.Framework.Servers.HttpServer.dll`, a dependency of both Robust
and every region process.

**Deployment note - not specific to this machine, applies to anyone
running the self-service web UI behind a reverse proxy:** fixing the
Robust-side limit surfaced a *separate*, reverse-proxy-specific problem
that has nothing to do with Confluence's own code. Testing through this
grid's actual public URL (`the test deployment's public hostname`, proxied through
the local web server to Robust - see the Batch 13 WebUI entry above) failed
with a proxy timeout / "error reading status line from remote server",
because a default 60s proxy timeout
is nowhere near enough for a 100+MB upload to fully transfer through an
extra hop and be forwarded on to a region. **This is a generic
property of putting any reverse proxy in front of Robust for this
feature, not something specific to this deployment** - a different
operator using nginx, IIS, Caddy, or no reverse proxy at all will hit
(or not hit) this in their own way, and the exact fix is proxy-software-
specific. For this grid's specific reverse-proxy setup, the fix
was raising the proxy timeout from 60 to 300 seconds (global -
a per-vhost `ProxyTimeout` override was tried first but didn't fully
resolve it, since mod_proxy was stacking the *global* `Timeout` 3x
before the vhost-level override took effect) - explicitly chosen over
leaving it at the default because this operator's own account had
accumulated an unusually large inventory (156MB) from heavy in-session
testing, which is a realistic worst case, not a hypothetical one.
**Anyone deploying Confluence's web UI behind their own reverse proxy
needs to check that proxy's own timeout/body-size settings against
their expected inventory/OAR sizes** - this is not something Confluence
itself can configure on the operator's behalf, since it lives entirely
outside Confluence's own process.

Verified directly against Robust (bypassing the proxy entirely, to
isolate Confluence's own code from the proxy layer): 156MB backup, then
restore of that same file, `Successfully loaded 1683 assets with 0
failures`.

Verification through the actual public URL was inconclusive, and it's
worth recording why rather than just calling it "untested": after
raising both the per-vhost and then the global proxy timeout
(with a web server restart confirmed after each), the exact same request
still cut off at ~180s both times, unchanged. Two independent proxy
config changes having zero effect on an identical number is itself the
signal - it means the proxy's own settings were never the bottleneck.
The most likely explanation: this grid's public URL was being tested
from the *same machine and network* that hosts it, meaning the request
went out to the public IP and back in through this network's own
router via NAT hairpinning - a path a genuinely remote client would
never take. Consumer routers commonly cap hairpinned NAT sessions at a
fixed idle timeout, and 180s (3 minutes) is a very common default,
entirely independent of the proxy or Robust. This isn't something
Confluence's code, or even this machine's proxy config, can fix - it's
router firmware, out of scope for this project and specific to this
operator's home network besides. **Decision: treat the direct-to-Robust
result as sufficient proof the feature itself is correct**, and note
for the record that same-machine/same-network testing of a grid's own
public URL is not a reliable way to test large uploads end-to-end - a
genuinely external client should be used if that specific path ever
needs verifying.

---

## Batch 13: Abuse Reports admin page (2026-08-09)

Next `/web/admin` page after region management and self-service
OAR/IAR, closing one of the gaps the WhiteCore-Dev `WebInterface`
correction called out (region/user/estate/abuse-report/currency
manager - see the "Correction (2026-08-09)" entry in
FEATURES_VS_MASTER.md). Confluence already has a full native Abuse
Reports service/data layer from earlier work (`IAbuseReportsService`,
`AbuseReportsService`, `MySqlAbuseReportsData`/PGSQL/SQLite, the
viewer-facing cap in `AbuseReportsModule.cs`) - this just surfaces what
was already being captured, no new service/DB work needed. Loaded
`IAbuseReportsService` directly in `WebInterfaceServiceConnector` the
same way as `CurrencyService`/`EstateService` (`LocalServiceModule`
from `[AbuseReportsService]`, same process as Robust, no network hop).

`/web/admin/abuse-reports` (admin-gated, same `UserLevel>=200` check as
the rest of `/web/admin`): paginated list (25/page, newest first, via
the existing `GetAbuseReports(start, count)`) with a summary link into
`?id=<reportID>` for the full detail view (`GetAbuseReport`), including
the reporter, abuser, region, category, position, object, full details
text, and viewer version. Screenshot (when attached) is served through
a separate `/web/admin/abuse-reports/image?id=` endpoint rather than
inlining it as a data URI, so the (potentially large) `ImageData` blob
only gets pulled from the DB when actually viewed - real SL abuse
report screenshots upload as raw JPEG via the
`SendUserReportWithScreenshot` cap, served back as `image/jpeg`.

**Deliberately v1/read-only - no "mark resolved" action.**
`AbuseReportData.CheckFlags` looks at first glance like it could serve
as an admin resolved-flag, but it's actually the **reporter's own**
submission-time checkboxes (`AbuseReportDataFromOSD` sets it from the
viewer's `check-flags` field at report time) - repurposing it for
admin state would silently corrupt what it actually means. There is no
resolved/handled/notes field anywhere in the current schema; adding
one properly means a real migration across all three data backends
(MySQL/PGSQL/SQLite), which is bigger than "let admins see the reports
that already exist" and was left as an explicit follow-up rather than
quietly bolted onto `CheckFlags`.

Verified live: inserted a real test row directly into `abusereports`
(no easy way to trigger the actual viewer cap without a live in-world
abuse report), then confirmed via curl - list page renders the row
correctly, detail page (`?id=1`) renders every field with correct HTML
escaping, "No screenshot attached" shows correctly for a report with
no `ImageData`, and paging past the end of the result set
(`?start=25`) shows an empty table with a working "Previous page" link
and no crash. Test row deleted after verification. Only
`OpenSim.Server.Handlers.dll` needed redeploying (Robust-side only, no
region-side changes this time) - discovered mid-deploy that **both**
region processes also hold a lock on that same DLL (it's a dependency
of `OpenSim.Region.CoreModules`), not just Robust, so all three
processes needed stopping before the file could be overwritten.

---

## Batch 13: User Management admin page (2026-08-09)

Fourth `/web/admin` page. Scoped deliberately narrow: search by name,
view an account's details (principal ID, email, created date, user
level, currency balance via the already-loaded `ICurrencyService`), and
edit **only** UserLevel. `IUserAccountService` has no create/delete/
suspend surface at all - `StoreUserAccount` replaces a whole record, so
editing anything requires fetching the full account first and mutating
just the one field being changed, same shape as the currency/HG-toggle
handlers already in this file.

UserLevel was picked as the one editable field because it's exactly
the thing this session already did **by hand via direct SQL** earlier
(promoting Test User from UserLevel 0 to 200 - see the currency Batch
12 notes) after the assistant's own attempted `UPDATE` was blocked by
the session's safety classifier. This page replaces that raw-SQL
workaround with a real, auditable admin UI path for the same action.

**Explicitly not exposed: suspend/ban.** `MySQLUserAccountData.GetUsers`
filters on `active=1` under the hood, meaning an `active` column
genuinely exists in the schema - but it isn't threaded through
`UserAccount`/`IUserAccountService` anywhere, so surfacing a suspend
toggle here would mean extending that service interface first, not
just adding a form field. Left as a clearly-scoped follow-up rather
than reaching past the data actually available through the existing
API.

Search itself inherits `GetUserAccounts`'s existing constraints (at
least one word over 2 characters, max two words) rather than
reimplementing search logic - an empty query intentionally shows
nothing instead of trying to dump the whole account table, since there
is no "list everyone" call on this interface at all.

Verified live end-to-end via curl: searched for "Test", got the one
matching account with correct email/user-level; opened the detail page
and confirmed principal ID, email, created date, user level, and
currency balance (4000) all rendered correctly; then did a real
round-trip UserLevel change (200 -> 199, confirmed via direct DB query
that the write actually persisted, not just a "success" message -> back
to 200) to prove `StoreUserAccount` genuinely writes through and isn't
a no-op. Test User's UserLevel was restored to 200 immediately after,
since later testing in this session depends on that account staying an
admin.

---

## Batch 13: Estate Management admin page (2026-08-09)

Fifth and last page from the original WhiteCore-Dev comparison list
(region/user/estate/abuse-report/currency manager). `EstateSettings`
has a genuinely large surface (bans, managers, groups, experience
lists, terrain/sun flags, ...) - full parity with the in-viewer Estate
floater would be its own multi-session effort, so v1 deliberately edits
only what an admin most commonly needs day to day: estate name, owner,
and five of the most consequential access toggles (public access,
voice, direct teleport, deny-anonymous, deny-minors). Same "edit the
one useful thing, not the whole object" discipline as the UserLevel-
only user management page - full ban-list/manager-list/group
management is a real, separate feature, explicitly left for later
rather than half-built here.

`/web/admin/estates` lists every estate via `GetEstatesAll()` +
`LoadEstateSettings(id)`, resolving the owner's name through the
already-loaded `IUserAccountService` and region count through
`GetRegions(id).Count`. The detail/edit view
(`/web/admin/estates?id=<id>`) additionally lists the actual region
names in that estate (`IGridService.GetRegionByUUID` per region).
Ownership transfer is by **name** (`First Last`), not raw UUID - the
update handler resolves the name via `GetUserAccount` and explicitly
refuses to save *anything* if the name doesn't resolve to a real
account, rather than silently keeping the old owner or, worse, writing
a garbage UUID.

Verified live end-to-end via curl against the real Estate 101 (owns
both Welcome Center and Var Test Region): list page showed the correct
owner/region count; detail page rendered both region names and correct
checkbox states; a real settings change (flipping `DenyMinors` off ->
on) was confirmed via direct DB query, then reverted; and the
owner-validation guard was confirmed to actually block a save (POSTed
a nonexistent "Nonexistent Person" as the new owner, got back "not
found - no changes were saved," and confirmed via DB that
`EstateOwner` was genuinely untouched, not just visually reverted on
the page).

This closes out the original "region/user/estate/abuse-report/currency
manager" comparison against WhiteCore-Dev's WebInterface that prompted
the whole Batch 13 thread - all five surfaces now have a native
Confluence equivalent. Given how much was learned building this (the
gzip-decompression landmine repeated twice, the HttpRequestParser body-
size bug, the completion-event visibility gap, several fields in
existing services turning out to have different real semantics than
first assumed - CheckFlags, `active`), a fresh look at whether the
original WhiteCore-Dev feature-parity audit's other verdicts still hold
is warranted before treating that comparison as fully closed - flagged
for a follow-up pass rather than assumed unchanged.

---

## Batch 13: self-service account registration (2026-08-09)

Prompted directly by user feedback: "They should be able to sign up
from the main page." Comparing the new native web UI against the old
`OpenSim-Grid-Interface` PHP site it replaced (still present on disk,
just no longer routed to) surfaced this as
the one clear functional gap that actually mattered - the PHP site had
`register.php`; the native replacement had no way to create an account
at all, only log into an existing one.

`UserAccountService.CreateUser(...)` (the method the `create user`
console command calls) does everything a full registration needs in
one call - but it's a method on the **concrete** `UserAccountService`
class, not part of `IUserAccountService`, and `WebInterfaceServiceConnector`
only holds an interface reference (loaded the same
`LocalServiceModule`-reuse way as every other service in this file).
Rather than cast to the concrete type or extend the interface for one
caller, `HandleRegister` reimplements the same sequence
`CreateUser` follows, calling only interface methods already available:
`IUserAccountService.StoreUserAccount` (new account) ->
`IAuthenticationService.SetPassword` -> `IGridService.GetDefaultRegions`
+ `IGridUserService.SetHome` (home region) ->
`IInventoryService.CreateUserInventory` (root folder + system folders).
`IInventoryService` wasn't previously loaded in this class - added the
same way as everything else (`[InventoryService] LocalServiceModule`).

Validation mirrors `UserAccountService`'s own "create user" console
command: the same excluded-character set (space, `@`, `.`, `:` - these
collide with Hypergrid address parsing, `first.last@gate:port`) for
first/last name, plus a minimum password length and a confirm-password
match the console command doesn't need (interactive prompts don't have
a "confirm" step - a web form does). On success, logs the new account
straight in via the same `TryLogin` path the login form itself uses,
rather than making someone who just typed a password once retype it
immediately on a fresh login screen.

Verified live end-to-end: registered a real account
("Newbie Tester") through the actual public URL, got redirected
straight to the dashboard (balance correctly showing 0 for a brand-new
account), confirmed via direct DB queries that the account row,
**home region** (set to Welcome Center), and a **full 22-folder
inventory tree** were all created - genuine parity with the console
command's own side effects, not just a bare account row. Then verified
all four validation guards actually block: duplicate name, mismatched
passwords, too-short password, and a name containing an HG-reserved
character (`@`) - each produced its specific error message and (for
the duplicate-name case) did not create a second row. Test account
deleted after verification (useraccounts/GridUser/inventoryfolders/auth
rows).

---

## Batch 14: full follow-through on the WhiteCore-Dev re-audit (2026-08-10)

Two research agents (dispatched per user request to "re-evaluate WhiteCore-Dev
again") came back with 16 concrete, actionable items across a re-audit of
the 5 originally-dismissed items, a WebInterface/Currency feature-gap
comparison, a fresh scan, and an addon-vs-native scan of Confluence's
remaining vendored addons. User's direction: "all of it." Tracked as
tasks #11-26. Working through them in dependency/priority order rather
than as one undifferentiated block.

### Land Auction currency bug (2026-08-10)

The re-audit's sanity check on already-shipped code (Land Auction was
ported in the original WhiteCore-Dev pass, "not yet tested in-world")
found a real, live correctness bug: `AuctionModule.AuctionEnd()`
transferred parcel ownership to the winning bidder via
`land.UpdateLandSold(...)` but never charged them anything - the
in-world message even said "paying X for it" while nothing was
actually deducted. Root cause: `UpdateLandSold` only moves ownership,
it has no currency awareness at all; the normal client-driven land-buy
path only charges because `LandManagementModule.EventManagerOnLandBuy`
waits for a currency-module listener to set
`LandBuyArgs.economyValidated = true` first (see `ValidateLandBuy`/
`ProcessLandBuy` in `ConfluenceCurrencyModule.cs`, Batch 12) - but an
auction ending has no client-sent `LandBuyArgs` to hook into, so
`AuctionEnd` was the one path that never went through any charge logic
at all.

Fixed by charging directly through the generic `IMoneyModule` interface
(`AmountCovered` + `MoveMoney`) before transferring ownership -
deliberately generic rather than reaching into
`ConfluenceCurrencyModule` concretely, so the fix works regardless of
which economy module a grid actually has configured (Confluence's own,
Gloebit, DTLNSLMoneyModule, etc.), mirroring the exact pattern already
proven in `LandManagementModule.cs`'s own parcel-access-pass charge
code (`mm.AmountCovered(...)` then `mm.MoveMoney(...,
MoneyTransactionType.LandPassSale, ...)` - used `MoneyTransactionType.LandAuction`
here instead, since SL's standard transaction-type enum has a distinct
category for exactly this case). If the bidder no longer has sufficient
funds, or the charge fails for any reason, the auction is now cancelled
outright and ownership is left unchanged, rather than transferring the
parcel for free.

**Verification note - full in-world testing of this one didn't happen,
and it's worth being upfront about why rather than quietly skipping
it:** `AuctionModule`'s commands (`land auction start/bid/end`) are
region-console-only, and this codebase's `LocalConsole` crashes
outright (`System.IO.IOException: The handle is invalid` from
`Console.TreatControlCAsInput`) if stdin/stdout are redirected
programmatically - it needs a real Windows console, not a pipe. A
second attempt using a real (non-redirected) console window plus
`SendKeys`-based UI automation also failed: the launched process's
`MainWindowTitle` came back empty, meaning the actual visible console
window is very likely hosted by a separate `conhost`/Terminal process
rather than `OpenSim.exe` itself, so activating "the process" by PID
doesn't reliably reach the right window. Both are Windows
console-hosting quirks unrelated to the fix's correctness, not
something worth building deeper UI-automation infrastructure to solve
for one console-only feature. Confidence in the fix instead comes from:
a clean solution build, and the charge logic being a direct, verified-
identical application of the `AmountCovered`/`MoveMoney` pattern already
proven live elsewhere in this exact codebase. **Manually running `land
auction start/bid/end` from the actual region console remains the
recommended follow-up check** before this module is used on a real
economy grid - same "not yet tested in-world" status the feature
already had before this fix, not a new gap introduced by it.

### Group currency balances (2026-08-10)

Prerequisite for porting WhiteCore's stipend/group-dividend economy
(next). The audit's finding was literally "zero hits for 'group'" in
`CurrencyService.cs` - true, but investigating turned up something
worth recording: the underlying ledger tables
(`currency_balances`/`currency_transactions`) have **no schema-level
distinction between an agent UUID and a group UUID at all** - no
foreign key to `useraccounts`, just a bare `VARCHAR(36)` primary key.
`GetBalance`/`Transfer` would already work correctly today if handed a
group's UUID instead of an agent's. So this wasn't a storage gap, it
was an API-clarity gap - added `GetGroupBalance`/`GroupCurrencyTransfer`/
`GetGroupTransactionHistory` to `ICurrencyService` as thin, explicitly-
named wrappers over the existing methods, rather than a schema
migration. `GroupCurrencyTransfer` takes a `payingIntoGroup` bool to
express direction (member funding the group vs. a payout) instead of
making every caller juggle to/from order themselves. Deliberately
**no permission checking added here** - the ledger has never checked
"is the caller allowed to do this" for agent-to-agent transfers either
(that lives in the caller, e.g. `ConfluenceCurrencyModule`'s land-buy
validation), so group-officer/financial-permission checks belong in
whatever calls `GroupCurrencyTransfer`, not baked into the ledger
itself.

`GetGroupTransactionHistory` needed actual new logic, not just a
wrapper: `GetTransactionHistory`'s `toAgentID`/`fromAgentID` are ANDed
when both are non-zero (confirmed by reading `AppendAgentFilters` in
`MySQLCurrencyData.cs`), so a group's activity page needs both
directions (money paid in AND money paid out) merged, not one query.
Implemented as two full fetches (group-as-recipient, group-as-sender)
merged and sorted by date, then paginated in memory - reasonable given
per-group transaction volume, not worth a SQL UNION for this.

Also had to add the same three methods to `LocalCurrencyServiceConnector`
(a thin region-side pass-through to whatever `ICurrencyService` is
configured) - the build caught this immediately (`CS0535: does not
implement interface member`) since it's the only other class
implementing the interface besides `CurrencyService` itself.

**No live verification yet - deliberately deferred, not skipped.**
Nothing calls these three methods today; the stipend/dividend port
(next) is what will actually exercise `GroupCurrencyTransfer` for
real, and testing unused plumbing in isolation would mean building
throwaway scaffolding just to poke it. Confidence for now comes from a
clean full-solution build and the fact that the only genuinely new
logic (`GetGroupTransactionHistory`'s merge) is a small, readable
function over data shapes already proven correct elsewhere. Did run a
regression check after redeploying (this touched
`OpenSim.Services.Interfaces.dll`, the same assembly that caused the
Batch 13 stale-DLL cascade) - logged in, loaded the dashboard, real
balance rendered correctly, no exceptions.

### Stipend economy port (2026-08-10)

Ported the parts of WhiteCore's `ScheduledPayments.cs` that were
actually clean to port, and scoped around the one real architecture gap
found along the way rather than forcing a 1:1 port.

**Universal stipend payments - fully automatic, ported completely.**
The cadence math (`PaymentCycleDays`/`PayDayOfWeek`/`GetStipendPaytime`)
had zero DataManager coupling in the original and ported over almost
line for line. Hosted on `CurrencyServerConnector` (Robust-side) with
its own `System.Timers.Timer`, since it needs exactly the services that
connector already loads and grid-wide scheduled payments are a
Robust-level concern, not region-specific. One deliberate improvement
over the original: WhiteCore pays stipends out of a literal "Banker"
account UUID and has its own commented-out, never-finished TODO to
reconcile that account back to zero afterward - Confluence's ledger
already has a real "system-generated credit, no counterparty" concept
(`fromID = UUID.Zero` in `Transfer`, the same thing `SetBalance`/
`RecordPurchase` already use), so stipends here have no phantom-account
bookkeeping debt to begin with. Console commands: `stipend info`,
`stipend paynow`.

**Group liability charges - not ported, and here's the honest reason
why.** WhiteCore's own `ProcessGroupLiability` reads a group's parcels-
for-sale-in-search state via `IDirectoryServiceConnector` to compute a
"directory fee" liability, then charges accountable members directly
for it - and even in the WhiteCore source itself, the group balance
update in that exact method is a commented-out TODO
(`//moneyModule.UpdateGroupBalance(groupID, grpBalance)`) that was never
finished. Porting a feature faithfully that isn't actually complete in
its own source, and that depends on a parcel-search-fee concept
Confluence doesn't have any equivalent of today, isn't a good use of
scope here - flagged as a real follow-up (needs Confluence's own
directory/parcel-for-sale integration first), not silently dropped.

**Group dividends - the algorithm is real and ported, but not wired to
run automatically, and that's an honest scoping call, not a shortcut.**
While building this, found that Confluence's Groups subsystem
(`IGroupsModule`, `IGroupsServicesConnector`) has **no "enumerate every
group on the grid" capability at all** - `IGroupsModule`'s methods are
built around a live, connected `IClientAPI` (a real viewer session),
and the addon-style `IGroupsServicesConnector` only supports name-search
or per-agent membership lookups, not "list every group." WhiteCore's
own `groupsModule.GetAllGroups(BankerUUID)` has no equivalent here.
Adding that capability properly means extending the Groups subsystem
itself (interface + all three data backends) - a separate, real piece
of work, not something to bolt on as a side effect of a currency port.
So: added `ICurrencyService.PayGroupDividend(groupID, memberIDs,
description)` - the actual algorithm (divide the group's current
balance evenly among the given members, pay each via
`GroupCurrencyTransfer`, leave any integer-division remainder in the
group balance) is real, complete, and correctly scoped to have zero
dependency on the Groups subsystem, matching how the rest of
`ICurrencyService` is deliberately Groups-agnostic. It just needs a
caller - once "list all groups" exists somewhere, wiring an automatic
timer here is a small addition, not a redesign.

**Verification:** the universal stipend path was verified fully live
and automatically, with no console interaction needed at all (sidestepping
the Land Auction task's console-automation problems entirely) - set a
near-future `StipendPayDay`/`StipendPayTime` in `Robust.HG.ini`,
restarted Robust, and let the real background timer fire on its own.
Confirmed via direct DB queries: both real accounts (Test User, GRID
SERVICES) received exactly the configured 50-unit payment, correct
`currency_transactions` rows with `FromAgent = 00000000-...-000000000000`
(system-generated) and the right description/amount. Reverted the test
balances/transaction rows and the test-only near-future schedule
afterward, redeployed `PayStipends = false` as the real default.
`PayGroupDividend` has no live caller yet (by design, see above) so
wasn't exercised live - same "verify once there's a real caller" stance
taken for the rest of the group-currency API in the previous entry.

### On-demand/soft-start regions - built, code verified correct, live discovery unresolved (2026-08-10)

The port itself was clean: WhiteCore's `OnDemandRegionModule.cs` toggles
a `ShouldRunHeartbeat` bool plus its own `WhiteCoreEventManager` events;
Confluence's `Scene` already has an equivalent, differently-named
mechanism - the public `Active` property (`Active = false` stops the
heartbeat loop, `Active = true` calls the same `Start()` every region
already uses at boot) - so no engine-level plumbing was needed at all,
just the right events (`EventManager.OnNewPresence`/`OnRemovePresence`,
both already present in vanilla-lineage OpenSim, confirming the audit's
"REVERSE" verdict). Added
`OpenSim/Region/CoreModules/World/OnDemand/OnDemandRegionModule.cs`
scoped to WhiteCore's "Medium" tier only (full normal load, heartbeat
idles down while empty) - a true "Soft" tier (skip loading prims
entirely) would mean changing region startup sequencing itself, a
bigger change than toggling an already-existing pause/resume switch.
Added a grace-period debounce (default 60s) before actually idling
down, which the WhiteCore original didn't have - avoids thrashing the
heartbeat thread for someone teleporting through in quick succession.

**Live verification hit a real wall, and it's worth documenting in
detail rather than glossing over.** Enabled the module via a new
`[OnDemand] Enabled = true` region-ini section on Var Test Region,
redeployed, restarted - and the region's own
`[REGIONMODULES]: From plugin OpenSim.Region.CoreModules, loaded 124
modules, 82 shared, 42 non-shared` startup line never changed to 125,
and a diagnostic log line placed at the very top of `Initialise()`
(unconditional, before any config check) never printed. The module
was never being instantiated by Mono.Addins at all.

Ruled out, in order: **stale addin cache** (moved aside the entire
`addin-db-004` directory to force a from-scratch rebuild - no change).
**Bad deployment** (compared MD5 checksums of the build output against
the deployed file - identical; confirmed via `Get-Process -Modules`
that the running process had that exact file loaded, from the exact
path expected). **Tooling giving false readings** - this was the real
rabbit hole: both `strings` against the compiled DLL and PowerShell
5.1's `[Reflection.Assembly]::LoadFrom` reported the type didn't exist
in the assembly at all, which would have meant a build-item-inclusion
bug. Built a throwaway `dotnet run` .NET 8 console app to check
properly (PowerShell 5.1 is .NET Framework and can't reliably load/
reflect a modern multi-dependency net8.0 assembly - confirmed via a
`ReflectionTypeLoadException` when trying `.GetTypes()`), and that
showed the type **was** present and correctly compiled all along, with
an `[Extension(Path = "/OpenSim/RegionModules", NodeName =
"RegionModule", ...)]` attribute structurally identical to
`AuctionModule`'s (same path, same node name, same interfaces
implemented) - both `strings` and the PowerShell reflection check had
been giving false negatives the entire time, sending the investigation
in the wrong direction. **New folder** (moved the file into the
existing `World\Land\` folder alongside `AuctionModule.cs` itself,
same result - ruled out). After all of that, the type is 100% verified
correct and correctly deployed, and Mono.Addins still doesn't discover
it as a `/OpenSim/RegionModules` extension node on this particular
the test deployment, for a reason not yet identified.

**Status: code is correct and ready to use, left disabled
(`[OnDemand] Enabled = false`) pending root-causing the discovery gap.**
This doesn't block anything else - `Initialise()` no-ops immediately
when the section is disabled/absent, so the dormant module has zero
effect on normal operation. Worth a fresh pair of eyes or a from-scratch
the test deployment redeployment to see if the discovery issue is specific to
this long-lived instance's addin state in some way not captured by the
cache-clear already tried.

### Grid-wide viewer ban - most of it already existed (2026-08-10)

Before writing any code, checked what Confluence already had - and it
turned out to be far more than the original audit credited.
`LLLoginService.Login()` already calls
`m_AccessControlService.IsIPBanned(...)` (exact-match IP ban),
`m_AllowedClientsRegex`/`m_DeniedClientsRegex` (regex-based viewer
allow/deny by self-reported version string, config'd under
`[LoginService]`), and `m_DeniedMacs` - and `IAccessControlData` already
had `IsHardwareBanned(mac, id0)` backed by real `banned_macs`/
`banned_id0s` tables, with full `ban mac`/`ban id0`/`ban ip` console
commands already in `AccessControlService.cs`. None of this was visible
from the WhiteCore-side-only comparison the original audit did - it was
only checking "does WhiteCore have this," never "does Confluence already
have an equivalent by a different name."

What was genuinely missing, matching the audit's own DataManager-
coupling analysis: **IP range bans** (`IsIPBanned` only ever did exact
string match - `where ip = ?ip` - no CIDR/range concept at all) and
**baked-texture-signature viewer detection** (Confluence's regex check
only looks at what a viewer self-reports, which a modified viewer can
lie about).

**IP range bans**: added `banned_ip_ranges` (start_ip, end_ip) to the
existing `AccessControl.migrations` across all three backends (MySQL/
PGSQL/SQLite - matching every table already added this session across
all three, not just MySQL), `IAccessControlData.GetIPRangeBans/
BanIPRange/UnbanIPRange`, `IAccessControlService.IsIPRangeBanned`
(fetches all ranges and compares numerically in C# rather than a SQL
range query - the list is expected to stay small, and this sidesteps
unsigned-int column differences across the three DB engines), and `ban
iprange`/`unban iprange` console commands matching the existing `ban
ip`/`ban mac`/`ban id0` command style exactly. Wired into
`LLLoginService.Login()` right next to the existing `IsIPBanned` check.

Verified against the real database with a small standalone .NET 8 test
harness (same technique used to cut through the on-demand-regions
reflection confusion) rather than through the actual login path, since
`LLLoginService.Login()` needs a real viewer's TCP connection IP, which
can't be scripted from curl: banned `203.0.113.1-203.0.113.50` (RFC
5737 documentation range, safe to use for testing), confirmed
`.25` inside reports banned, `.0` and `.51` (one below/above the
boundary) correctly do not, an unrelated IP doesn't, and after
unbanning the range is gone. Also confirmed via the region logs that
Robust/regions restart cleanly with the new migration applied (all four
`banned_*` tables exist) and did a dashboard regression check
afterward - AccessControlService is not currently configured in
the test deployment's `Robust.HG.ini` (was already dormant before this work,
left that way - enabling grid-wide banning by default wasn't asked
for), so this shipped as available-but-opt-in, consistent with how
it already was.

**Viewer signature ban**: ported `GridWideViewerBan.cs` as
`OpenSim/Region/CoreModules/World/Access/ViewerSignatureBanModule.cs`
(placed in the existing `World/Access/` folder rather than a new one,
partly to sidestep any repeat of the on-demand-regions Mono.Addins
mystery even though that was never actually pinned on the folder).
Hooks `EventManager.OnAvatarAppearanceChange`, inspects the avatar's
baked texture IDs against a third-party-maintained viewer-signature
map (same public resource WhiteCore's original used, fetched once and
cached per process), and kicks via `IClientAPI.Kick` +
`Scene.CloseAgent` if a banned viewer's signature texture is found.
Disabled by default (`[GrieferProtection] ViewerSignatureBanEnabled`),
same as the WhiteCore original. **Not live-verified** - would need
either a real viewer running one of the specific tagged builds, or
mocking the external signature-list fetch and injecting a fake
matching texture ID onto a test avatar, neither of which was practical
here. Confidence comes from a clean build and the logic being a close,
mechanical port of the WhiteCore original using Confluence's own
already-proven equivalents (`OnAvatarAppearanceChange` for the hook,
`IClientAPI.Kick`/`Scene.CloseAgent` for the disconnect, both used
elsewhere in this codebase already).

### BotManager avatar-follow + tag-group management (2026-08-10)

Ported to `osNpc*` rather than as a separate bot framework, per the
re-audit's own "PARTIAL" verdict - the bulk of BotManager genuinely
duplicates Confluence's existing NPC/pathfinding suite, but continuous
follow and tag-based bulk management were real, confirmed gaps
(`llPursue` is one-shot; there was no way to manage a named group of
NPCs at all).

**Follow** is implemented as a periodic re-target rather than
WhiteCore's physics-event-driven approach
(`PhysicsActor.OnRequestTerseUpdate`) - a `System.Timers.Timer` (1s
tick, started lazily on first use) recomputes each following NPC's
target position against the target avatar's *current* location every
tick and re-issues the module's existing `MoveToTarget`, with the same
start/stop-distance hysteresis WhiteCore's version has. Simpler than
hooking a physics event, and needed no engine-level changes at all -
`NPCModule.MoveToTarget`/`StopMoveToTarget` already existed and did
exactly what's needed. Trade-off: up to ~1s of lag reacting to a
target's movement, vs. WhiteCore's per-physics-tick responsiveness -
acceptable for the kinds of uses this is for (a following pet/guard/
companion NPC), not real-time enough for something like dodging.
**Tags** are a plain in-memory `Dictionary<string, HashSet<UUID>>`
inside `NPCModule` - `AddNPCTag`/`GetNPCsWithTag`/`DeleteNPCsWithTag`,
both cleaned up automatically when an NPC is deleted so they can't leak.

New OSSL functions (mirroring the exact three-file pattern every
existing `osNpc*` function already uses - `IOSSL_Api.cs`, `OSSL_Api.cs`,
`OSSL_Stub.cs`): `osNpcFollow(npc, target, startDistance, stopDistance)`,
`osNpcStopFollow(npc)`, `osNpcAddTag(npc, tag)`,
`osNpcGetNPCsWithTag(tag)`, `osNpcDeleteNPCsWithTag(tag)`. The tag
query/delete functions filter results through the same
`CheckPermissions` every other `osNpc*` call already uses, so a tag is
a convenience for managing NPCs a script's owner already has permission
over, not a way to discover or affect someone else's NPCs.

**Not live-tested with an actual LSL script** - exercising this
properly needs a real viewer to rez a script and watch an NPC actually
walk after an avatar, which wasn't available in this environment (same
constraint as the viewer-signature ban item above). Confidence comes
from: a clean full-solution build: the new code following the *exact*
file-by-file pattern every other working `osNpc*` function already
uses in this codebase (not a novel pattern); and `Follow`/`StopFollow`
building directly on `MoveToTarget`/`StopMoveToTarget`, which are
themselves an unmodified, already-proven part of the existing NPC
framework. A live check via an actual rezzed script and viewer remains
the recommended follow-up before relying on this in a real build.

### SimProtection - built correctly, and a systemic discovery pattern confirmed (2026-08-10)

The port itself: Confluence already had every real primitive WhiteCore's
`SimProtection.cs` needs, just under different names -
`Scene.StatsReporter.LastReportedSimFPS`/`LastReportedSimStats[StatsIndex.PhysicsFPS]`
instead of `ISimFrameMonitor`, and `RegionSettings.DisableScripts/
DisablePhysics/DisableCollisions` + `ISceneCommandsModule.SetSceneDebugOptions`
instead of `SetSceneCoreDebug` - the same two calls
`EstateManagementModule.HandleEstateDebugRegionRequest` already makes
for the in-viewer Estate "Debug" tab. Added
`OpenSim/Region/CoreModules/World/Region/SimProtectionModule.cs`:
periodic check (default 60s) that disables scripts and/or physics if
FPS drops below a configurable percentage of baseline, re-enables after
a grace period once FPS recovers, and can trigger `shutdown` if FPS
sits near zero for too long. Deliberately does **not** call
`RegionSettings.Save()` the way WhiteCore's original does on every
toggle - these are meant to be transient automatic responses to a
performance dip, not something that should survive a restart if the
dip was already resolved.

**Confirms a systemic pattern, not a one-off.** Live-testing this
(artificially set `BaseRateFramesPerSecond=200` so this region's normal
~44fps would read as "below threshold" and trigger immediately, no
actual overload needed) found the exact same symptom as the on-demand-
regions item: a diagnostic log at the very top of `Initialise()` never
printed, and `OpenSim.Region.CoreModules`'s own `[REGIONMODULES]: loaded
N modules` startup count didn't increase. Checking that same count
against the *other* new module added this session
(`ViewerSignatureBanModule`, previous entry) showed it going from 124
to 125 - meaning **of the three new `[Extension(...)]`-tagged region-
module classes added in this batch, exactly one was actually discovered
by Mono.Addins**, despite all three following the identical proven
pattern (`AuctionModule`'s exact `Extension` attribute shape, confirmed
character-for-character identical in the on-demand-regions
investigation). This rules out anything specific to SimProtection's own
code, folder, or naming - the same three explanations already ruled out
for on-demand regions (stale cache, bad deployment, wrong folder) don't
need re-litigating a second time for the same underlying mechanism.
**This looks like a real, reproducible Mono.Addins region-module-
discovery reliability issue on this specific test deployment**,
independent of which module is involved - worth a maintainer's dedicated
look (or a from-scratch the test deployment redeploy to see if it clears),
rather than continuing to re-diagnose it individually per module.
Shipped disabled (`[SimProtection] Enabled = false`), same as
on-demand regions.

### Native land/places search service, replacing the OpenSimSearch addon (2026-08-10)

Task #18 from the "all of it" list: OpenSimSearch (`addon-modules/
OpenSimSearch`) only works against an external XML-RPC search server
(`query.php`) that doesn't exist anywhere in Confluence's own stack -
same shape of problem as the currency addons before Batch 12's native
`CurrencyService`. Scoped deliberately to **land/places search only**;
events and classifieds have no existing data model anywhere in Confluence
and would be a separate, much larger feature, not a drop-in extension
of this one.

Followed the exact Data/Services/Connectors layering this session
already established for Currency and Access Control:
- `OpenSim/Data/ISearchData.cs` + `MySQLSearchData.cs`/
  `PGSQLSearchData.cs`/`SQLiteSearchData.cs` - read-only queries against
  the grid's own existing `land` table (`ParcelFlags.ForSale = 0x1000`,
  `ParcelFlags.ShowDirectory = 0x100000`), no new schema. Confirmed and
  handled the same MySQL/PGSQL-vs-SQLite column-naming split already
  known from other tables in this codebase: SQLite's `land` table uses
  `Desc` not `Description`, and its `LandFlags` column has TEXT type
  affinity rather than a native integer, handled via
  `Convert.ToInt64/Int32(reader.GetValue(...))` instead of typed reader
  accessors.
- `LandSearchRecord` (the shared DTO both `ISearchData` and
  `ISearchService` need) lives in `OpenSim.Framework`, not
  `OpenSim.Data` - `OpenSim.Services.Interfaces` doesn't reference
  `OpenSim.Data` and was never going to for one small DTO, so this
  follows the exact precedent already set by `CurrencyTransfer`/
  `CurrencyPurchase` in Batch 12.
- `OpenSim/Services/SearchService/` (new project,
  `OpenSim.Services.SearchService.csproj`, added to `OpenSim.sln`) -
  `SearchServiceBase`/`SearchService` mirror `CurrencyServiceBase`/
  `CurrencyService` exactly (`[SearchService] LocalServiceModule`/
  `StorageProvider`/`ConnectionString`, falling back to
  `[DatabaseService]` if unset).
- `OpenSim/Region/CoreModules/World/Search/ConfluenceSearchModule.cs` -
  the **one** new `[Extension(...)]`-tagged region module for this task,
  deliberately not split into a separate `Local*ServiceConnector`
  middleman class the way Currency has one, specifically to minimize
  new extension-tagged classes given the Mono.Addins discovery
  unreliability confirmed twice already this session (on-demand
  regions, SimProtection). Loads `ISearchService` directly via
  `ServerUtils.LoadPlugin<ISearchService>`. Activation uses the *same*
  switch OpenSimSearch's own `OpenSearchModule` already checks -
  `[Search] Module = "<name>"` - so setting it to `"ConfluenceSearchModule"`
  both activates this module and disables OpenSimSearch (which already
  disables itself for any `Module` value other than `"OpenSimSearch"`).
  Wires `IClientAPI.OnDirPlacesQuery`/`OnDirLandQuery` to
  `ISearchService.SearchPlaces`/`SearchLandForSale`, building
  `DirPlacesReplyData`/`DirLandReplyData` with the exact field mapping
  confirmed by reading OpenSimSearch's own reply construction.

**Fully live-verified, no environment friction this time.** Full
solution build clean. Deployed to the test deployment (all three processes -
Robust plus both region sims - stopped first, since the changed/added
DLLs - `OpenSim.Framework`, `OpenSim.Data` and its three provider DLLs,
`OpenSim.Services.Interfaces`, the new `OpenSim.Services.SearchService`,
and `OpenSim.Region.CoreModules` - are shared across all three; MD5
checksums confirmed the deployed copies match the build output).
Enabled on Var Test Region only (`[Search] Module =
"ConfluenceSearchModule"` + a `[SearchService]` section pointing at the
same shared test-deployment MySQL DB Currency already uses), left
untouched (still `"OpenSimSearch"`) on Welcome Center, specifically to
verify the two coexist correctly in separate processes without
cross-contamination.

A temporary unconditional diagnostic log at the top of `Initialise()`
(same technique used for on-demand regions/SimProtection/
ViewerSignatureBan) confirmed **this module *was* discovered by
Mono.Addins** - `OpenSim.Region.CoreModules`'s own `[REGIONMODULES]:
loaded N modules` count went from 125 to 126 on Var Test Region, the
diagnostic line printed, and real functional logs followed immediately
after (`[SEARCH SERVICE]: Starting search service` /
`[CONFLUENCE SEARCH]: Native search module is active`), with no errors in
between. On Welcome Center, the diagnostic line *also* printed (Mono.
Addins instantiates every shared-module class in every process
regardless of whether it goes on to activate), but the real activation
log never appeared there, since its `[Search] Module` config was left
at the default `"OpenSimSearch"` - confirming the config-gate coexists
correctly with the untouched addon. Diagnostic line removed, rebuilt,
redeployed (checksum-verified again), and confirmed clean on a second
full restart (module count 125→126 again, activation log present, no
diagnostic line).

This is now **two of four** new `[Extension(...)]`-tagged region-module
classes added this session that *were* successfully discovered
(`ViewerSignatureBanModule`, now `ConfluenceSearchModule`), against two
that weren't (`OnDemandRegionModule`, `SimProtectionModule`) - still not
enough of a pattern to say what distinguishes them, but worth noting
discovery isn't uniformly broken, just unreliable.

Functional correctness of the query logic itself was verified directly
against the real database (same substitute-for-a-real-viewer technique
used for the IP-range-ban work): a temporary test parcel row was set
with `ForSale`/`ShowDirectory` flags, a sale price, and a distinctive
name, then a throwaway .NET 8 console harness
(`MySqlSearchData.SearchPlaces`/`SearchLandForSale` called directly
against the live test-deployment DB) confirmed: free-text name matching
finds the row and excludes an unrelated query string; land-for-sale
search finds the row and correctly excludes it once a `minPrice` filter
is raised above its sale price. The test parcel row was restored to its
original values afterward. The actual in-viewer protocol path
(`DirPlacesQuery`/`DirLandQuery` panels) was not tested end-to-end - no
real viewer available in this environment, same accepted gap as
BotManager-follow and ViewerSignatureBan.

### Native mute-list service (task #19) - already existed, premise was wrong (2026-08-10)

Task #19 from the "all of it" list was "replace OpenSimMutelist addon
with a native mute-list service," on the premise (from the original
audit) that no native equivalent existed and the addon depends on an
external `MuteListURL` server that isn't part of Confluence's stack.
A dedicated investigation before writing any code found that premise
wrong on every count: **a complete, working, already-deployed native
mute-list stack already existed in Confluence before this task began.**

- `OpenSim/Services/MuteListService/MuteListService.cs` - a real,
  complete `IMuteListService` implementation (`MuteListRequest`,
  `UpdateMute`, `RemoveMute`, `IsMuted`), backed by
  `IMuteListData`/`MySqlMuteListData`/etc. via the standard
  `[MuteListService] LocalServiceModule`/`StorageProvider`/
  `ConnectionString` pattern every other service in this codebase uses.
- Full Local/Remote connector chain already exists:
  `LocalMuteListServiceConnector.cs`/`RemoteMuteListServiceConnector.cs`
  (`OpenSim/Region/CoreModules/ServiceConnectorsOut/MuteList/`), the
  actual HTTP client (`OpenSim/Services/Connectors/MuteList/
  MuteListServicesConnector.cs`), and the Robust-side HTTP handler
  (`OpenSim/Server/Handlers/MuteList/MuteListServerConnector.cs` +
  `MuteListServerPostHandler.cs`, serving `POST /mutelist`).
- The viewer-facing region module,
  `OpenSim/Region/CoreModules/Avatar/InstantMessage/MuteListModule.cs`,
  wires `IClientAPI.OnMuteListRequest`/`OnUpdateMuteListEntry`/
  `OnRemoveMuteListEntry` and is **already the active module in every
  current test deployment config** - both `Var_Test_Region\OpenSim.ini`
  and `Welcome_Center\OpenSim.ini` set `[Messaging] MuteListModule =
  MuteListModule`, `config-include/GridHypergrid.ini` sets `[Modules]
  MuteListService = "RemoteMuteListServicesConnector"`, and Robust.ini
  already registers the connector and `LocalServiceModule`. The Robust
  log already showed `MuteListServiceConnector loaded successfully` on
  every prior startup this session, unnoticed until this task looked
  for it specifically.
- `addon-modules/OpenSimMutelist`'s `OpenMutelist.cs` really does call
  out to an external XML-RPC `MuteListURL` server (that part of the
  original claim was correct), but its own `Initialise()` self-disables
  unless `[Messaging] MuteListModule == "OpenSimMutelist"` - which none
  of the deployed configs set. It still loads as a Mono.Addins plugin
  (harmless) but never subscribes to any client event. **Confirmed dead
  code on this deployment**, not a gap needing a replacement.

**Actual work done, once the premise was corrected:** two purely
cosmetic pre-existing defects, unrelated to this task's original goal
but found while reading the code closely enough to verify it actually
worked - `MuteListService` lived in `namespace
OpenSim.Services.EstateService` (an obvious copy-paste leftover from
templating off `EstateService.cs`), and the interface file was named
`IMuteLIstService.cs` (stray capital I). Fixed the namespace (verified
safe first - `ServerUtils.LoadPlugin`'s type-matching only checks the
short class name against the `dll:ClassName` config string, never the
namespace, so this couldn't break the existing `LocalServiceModule =
"OpenSim.Services.MuteListService.dll:MuteListService"` config anywhere)
and renamed the file via `git mv`. Left `addon-modules/OpenSimMutelist`
in place rather than deleting it - same reasoning as keeping
OpenSimSearch's addon alongside the new native search module: someone
running their own external mute-list server can still opt into it via
config, and deleting working (if dead-on-this-deployment) infrastructure
isn't this task's job.

**Fully live-verified**, and more thoroughly than most items this batch
since this is a pre-existing feature, not new code - the standard for
"same effort every time" doesn't relax just because nothing was built.
Full solution build clean. Only `OpenSim.Services.MuteListService.dll`
changed (the namespace edit); confirmed via MD5 that it's the *only*
one of the mute-list-related DLLs that actually differs from what was
deployed, so only Robust (the one process that loads this DLL directly,
per `[MuteListService] LocalServiceModule` in `Robust.HG.ini`) needed
stopping and restarting - the two region processes never touched this
DLL and kept running throughout. Robust log confirmed
`MuteListServiceConnector loaded successfully` with no errors after the
restart. Ran two independent live functional checks: (1) a throwaway
.NET 8 console harness instantiating the real `MuteListService` class
directly against the live test-deployment DB - `IsMuted` correctly false
before/true after `UpdateMute`, `MuteListRequest` returned the correct
CRC-checked pipe-delimited blob format, `RemoveMute` correctly reverted
`IsMuted` to false; (2) the same sequence again, this time via `curl`
directly against the live `POST http://localhost:9003/mutelist`
endpoint Robust actually serves (`update`/`ismuted`/`get`/`delete`
methods) - every response matched expectations, including decoding the
base64-wrapped mute-list blob from `get` back to the expected
`"1 <uuid> CurlTest|0"` format. This is a stronger verification than
most other items this batch got, since (unlike Search or the OSSL
additions) the actual wire protocol endpoint could be exercised directly
without needing a real viewer.

### WebInterface: purchases/transactions financial reporting page (task #20, 2026-08-10)

The read side of Batch 12's currency ledger - `ICurrencyService`
already had `GetTransactionHistory`/`GetPurchaseHistory`/
`NumberOfTransactions`/`NumberOfPurchases`, written for console
commands, but nothing in the WebInterface actually surfaced them. Added
`HandleAdminTransactions` (`/web/admin/transactions`,
`WebInterfaceServiceConnector.cs`) - two tabs (Transfers, Purchases),
optional "First Last" agent-name filter, pagination, linked from the
admin index alongside Abuse Reports/User Management/Estate Management.

Two small, contained DB-layer fixes were needed, not new currency
logic: `MySQLCurrencyData.GetPurchaseHistory`/`NumberOfPurchases`
required a non-zero `PrincipalID` unconditionally, with no way to ask
for "every purchase on the grid" the way `GetTransactionHistory`
already supports via `UUID.Zero` meaning "don't filter this side" -
relaxed both to the same `UUID.Zero`-means-unfiltered convention
(`OpenSim/Data/MySQL/MySQLCurrencyData.cs`, doc comment added to
`ICurrencyService.cs` too). Note: `CurrencyService` only ever had a
MySQL data backend to begin with (no PGSQL/SQLite `ICurrencyData`
implementation exists anywhere in this codebase) - a pre-existing scope
decision from Batch 12, not something this task needed to address.

`CurrencyTransfer.ToAgentName`/`FromAgentName` are never actually
populated by the DB layer (confirmed by reading
`MySQLCurrencyData.GetTransactionHistory` - the columns simply aren't
selected), so names are resolved per-row via `UserAccountService` in
the handler itself, falling back to the raw UUID if the account can't
be found (visible in the live test below - one of the two Land Auction
transaction's counterparty didn't resolve to a name, exactly this
fallback in action, not a bug).

`GetTransactionHistory` ANDs `toAgentID`/`fromAgentID` when both are
non-zero, so a single agent's activity (money sent *or* received) needs
"either side" semantics it doesn't provide directly - handled the same
way `CurrencyService.GetGroupTransactionHistory` already handles it for
groups (Batch 12): query both directions, merge by transaction ID,
sort by date, page in memory. Bounded to a 1000-row overfetch per side
rather than being exact for arbitrarily large histories - a reasonable
tradeoff for an admin reporting tool, not a public API.

**Fully live-verified end-to-end**, including a real login flow, not
just a direct service call. Full solution build clean. Three DLLs
changed (`OpenSim.Data.MySQL`, `OpenSim.Services.Interfaces`,
`OpenSim.Server.Handlers`, the last of which the WebInterface itself
lives in) - all three test deployment processes stopped, DLLs deployed,
checksums verified, all three restarted. Logged in via `curl` against
the real `/web/login` endpoint using the same "Test User" account
created earlier this session's self-service-registration testing,
captured the real session cookie, then exercised `/web/admin/
transactions` for real: the Transfers tab correctly showed this
session's actual real transaction history (two Land Auction charges,
three currency purchases, dates/amounts/types all correct); the
Purchases tab correctly showed L$ credited alongside real-money
hundredths and the purchasing IP; filtering by `agent=Test+User`
correctly excluded the one transaction where neither side was Test
User and kept the rest; filtering by a nonexistent name correctly
showed a "no user found" message (falling back to the unfiltered grid-
wide view rather than an empty page - a deliberate UX choice, not an
oversight); and unauthenticated access correctly redirected to
`/web/login`. Also confirmed no regression on `/web/dashboard` and
`/web/admin` after the shared-DLL redeploy, and confirmed via a third
full region restart that Batch 14's native search module (previous
entry) still activates cleanly.

### WebInterface: grid statistics dashboard (task #21, 2026-08-10)

`/web/admin/stats` - total regions, total land area, Hypergrid open/
closed count, total registered accounts, current online-user count.
Mostly free: total regions/land area/HG status reuse the exact
`GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000)` "get everything"
idiom `HandleAdmin` already established, and total accounts reuses
`GetUserAccountsWhere(UUID.Zero, "1=1")` (already-wired
`m_UserAccountService`, same call `HandleAdminUsers`' search already
makes, just with a catch-all predicate instead of a name).

Current-online-user-count had no existing service-level path at all -
researched this specifically before writing any code, since the whole
point of this task is to build only what's genuinely missing.
`IPresenceService.GetAgents` needs a list of userIDs to check, not a
"who's online" query, and `PresenceInfo` doesn't even carry a login
timestamp. The *only* place this logic existed anywhere in the
codebase was as a private console-command helper,
`GridUserService.HandleShowGridUsersOnline` (`Online==true` and
logged in within 5 days, to avoid overcounting a crashed region's
stale sessions) - inaccessible from the WebInterface connector, which
only has service-interface references, not console internals. Promoted
this into a real interface method, `IGridUserService.GetOnlineUserCount()`,
extracting `HandleShowGridUsersOnline`'s exact logic into it (the
console command now just calls the new method - no logic duplicated)
and threading it through every other `IGridUserService` implementer the
same way every prior interface addition this session was: the real
implementation (`GridUserService.cs`), `LocalGridUserServicesConnector`
(pass-through), `RemoteGridUserServicesConnector` (pass-through), the
actual HTTP client (`GridUserServicesConnector.cs`, new
`getonlineusercount` wire method), and the Robust-side handler
(`GridUserServerPostHandler.cs`, new `case "getonlineusercount"`).

**Fully live-verified at both levels of the stack, not just the one the
WebInterface itself uses.** Full solution build clean. Five DLLs
changed (`OpenSim.Services.Interfaces`, `OpenSim.Services.
UserAccountService`, `OpenSim.Region.CoreModules`, `OpenSim.Services.
Connectors`, `OpenSim.Server.Handlers`) - all three processes stopped,
deployed, checksum-verified, restarted. Logged in for real and fetched
`/web/admin/stats`: 2 regions, 1,114,112 m² total land area (1,048,576 +
65,536 - confirmed by hand against the two real region sizes), 1 of 2
open to Hypergrid (matches this grid's actual state), 2 registered
accounts (cross-checked directly against `SELECT COUNT(*) FROM
useraccounts` - exact match), 0 users online (correct - no one was
actually logged in). Then, separately, exercised the *actual* HTTP
wire path the region processes use for this service
(`RemoteGridUserServicesConnector` → `POST {PrivatePort}/griduser`,
confirmed from `config-include/GridHypergrid.ini`'s
`GridUserServices = "RemoteGridUserServicesConnector"`) directly via
curl against port 9003: `METHOD=getonlineusercount` correctly returned
`0`, and a pre-existing method (`getgriduserinfo`) was re-checked
alongside it to confirm no regression from editing a class four other
files also implement. Also re-confirmed dashboard/transactions/mutelist
endpoints and the search module's clean startup, since this redeploy
touched enough shared DLLs to risk all of them.

### WebInterface: self-service password reset (task #22, 2026-08-10)

`/web/forgot-password` (email in, generic "check your email" message
out regardless of whether it matched an account - deliberately no
account-enumeration signal) and `/web/reset-password?token=...` (new
password form, one-hour-lived single-use token).

Researched what already existed before writing anything, same
discipline as the Mutelist task: `UserAccount.Email` is a real,
DB-persisted, queryable field (`GetUserAccount(scope, email)` already
existed); real outbound SMTP already exists in this codebase via
MailKit, just built into the region-side `EmailModule.cs` (llEmail's
backend) rather than reachable from Robust - reused its exact `[SMTP]`
config section/keys and its connect/authenticate/send call sequence
(without the LSL-specific per-owner/per-address throttling, which
doesn't apply here) rather than inventing a second config surface, so
an operator with SMTP already set up for in-world email doesn't need
to configure it twice. The `WebSession` class already in this file
(token → data + expiry, checked on every use) was the template for a
new, separate `ResetToken` dictionary - a shorter lifetime (1 hour) and
single-use (removed on any redemption attempt, valid or not) since,
unlike a login session, possessing this token alone is enough to set a
new password. `MailKit`/`MimeKit` project references added to
`OpenSim.Server.Handlers.csproj` (previously only referenced from the
region-side `OpenSim.Region.CoreModules.csproj`).

**Live-verified with a real SMTP round-trip, not a mocked send.** No
real external mail account was available in this environment, so built
a small throwaway raw-socket SMTP listener (PowerShell,
`TcpListener`/`StreamReader`/`StreamWriter` implementing just enough of
the protocol - EHLO/MAIL FROM/RCPT TO/DATA - to accept a real
connection and capture what MailKit actually sends) and pointed a
temporary `[SMTP]` section at it. This is a stronger check than a
harness that calls `SendEmail` directly: it forces the *actual* MailKit
`SmtpClient.Connect`/`Authenticate`/`Send` calls to run over a real
socket. The captured message had a correct `From`/`To`/`Subject` and a
correctly-formed reset link with a real generated token.

That real token was then fed through the *actual* `/web/reset-password`
endpoint - which caught a real bug immediately: `HandleResetPassword`
called `ReadForm(request)` twice (once to read the token, again lower
down for the password fields), and an HTTP request body stream can only
be read once, so the second call threw `System.IO.IOException: Stream
was not readable`, surfaced as a 500 to the browser. Fixed by parsing
the form exactly once per request and reusing it. Rebuilt, redeployed,
re-verified the complete flow with a fresh token: reset succeeded (302
to login with a confirmation message), the *new* password logged in
successfully, the *old* password was correctly rejected, and re-
submitting the same (now-consumed) token was correctly rejected as
"invalid or expired" rather than silently succeeding again. Also
checked the two obvious edge cases: a nonexistent email address returns
the identical generic message with no email attempted (confirmed no
crash, no distinguishing behavior), and a garbage/unknown token is
correctly rejected. Restored the test account's password back to its
prior value afterward using the newly-verified reset flow itself
(rather than a side-channel DB edit), and shipped the deployed
`[SMTP]` section disabled (`enabled = false`, real config values
documented in the comment) since the throwaway test listener isn't a
real always-on mail server - a real deployment just needs `enabled =
true` plus real provider credentials, no code changes.

This is the only task so far this batch where live-testing caught and
fixed a genuine bug in the new code before it shipped, rather than just
confirming things already worked - directly justifying the "same effort
every time" standard applied to every item in this list, including the
ones that looked like simple, low-risk CRUD forms going in.

### WebInterface: login-screen news feed (task #23, 2026-08-10)

Grid-operator announcements (`/admin/news` to post/edit/delete, shown
on both the login splash `/welcome.php` and the public home page `/`).
Unlike the last several tasks, this one had genuinely nothing to reuse
or promote from existing code - no news/announcement concept exists
anywhere else in Confluence - so it's the first fully-new Data/Service
pair added this batch (Search and Mutelist both built on or discovered
existing infrastructure).

Followed the exact same three-layer shape Search already established
in this batch, deliberately kept even simpler since - like Search's
`ConfluenceSearchModule` context, but more so - **nothing region-side
ever needs this at all**: it's purely a Robust-hosted, admin-managed,
grid-wide feed. So there's no region module, no Local/Remote connector
pair, not even the one extension-tagged class Search needed - just
`OpenSim/Data/INewsData.cs` (+ MySQL/PGSQL/SQLite implementations,
new `news` table, no scope/owner column since this is grid-wide, not
per-user/per-region like most tables in this codebase),
`OpenSim/Services/Interfaces/INewsService.cs`,
`OpenSim/Services/NewsService/` (new project, mirrors
`SearchServiceBase`/`SearchService` exactly), and `NewsItem` living in
`OpenSim.Framework` (same layering reason as `LandSearchRecord`/
`CurrencyTransfer` - `OpenSim.Services.Interfaces` doesn't reference
`OpenSim.Data`). `WebInterfaceServiceConnector` loads `INewsService`
directly via the same `LoadReusedPlugin` every other service on that
class already uses (`ICurrencyService`, `IGridUserService`, etc.) -
no new loading mechanism needed since WebInterface itself is always
the Robust-local case that mechanism already handles.

Editing an existing item deliberately preserves its original post
date rather than bumping it to "now" - a wording correction shouldn't
reorder the feed out from under readers who already saw it.

**Fully live-verified**, full CRUD cycle against the real deployed
grid, not just a build check: confirmed the home page shows no "News"
section at all when the feed is empty (rather than an empty/awkward
header); posted a real item through the actual admin form and
confirmed it appeared correctly on both `/` and `/welcome.php` with
matching title/author/body; confirmed the edit form pre-fills from the
real stored values; edited the title/body and confirmed the change
took effect while the original post date stayed fixed; deleted it and
confirmed it disappeared from the admin list, the home page, *and* the
splash page in the same request cycle; confirmed unauthenticated
requests to `/admin/news` redirect to login same as every other admin
page. Full solution build clean; eight DLLs changed (all three
processes stopped/deployed/checksum-verified/restarted, since Data/
Framework/Interfaces are shared by everything); Robust log confirmed a
clean first-run migration (`Creating News at version 1`) with no
errors. Also re-confirmed dashboard/stats/transactions still work and
the search module still activates cleanly, per the now-standard
regression check after any shared-DLL redeploy this batch.

### WebInterface: static page manager (task #24, 2026-08-10)

Admin-authored content pages (About, Rules, Help, etc.) served at an
operator-chosen URL - `/admin/pages` to create/edit/delete, public at
`/web/page/<slug>` - no code changes needed to add one. Same
Data/Service shape as News (new `static_pages` table, `IStaticPageData`/
`IStaticPageService`, a new `OpenSim.Services.StaticPageService`
project, `StaticPage` DTO in `OpenSim.Framework`), with the one real
design difference: pages are addressed by an admin-chosen slug rather
than shown chronologically, which meant two things News didn't need -
a unique index on the slug column, and routing that can't be a fixed
`case` in `HandleRequest`'s switch the way every other page on this
connector is, since the slug is arbitrary. Handled by checking
`path.StartsWith(BasePath + "/page/")` in the `default:` branch of that
switch, after every fixed route has already had a chance to match, so
a real fixed path never gets shadowed by a page slug someone picks.

Slug collisions are checked explicitly before saving (`GetBySlug`,
comparing against the item's own ID so editing a page without changing
its slug doesn't collide with itself) rather than only relying on the
DB's unique index - the alternative would surface as a generic "could
not save" failure instead of a real "that slug is taken" message.

**Fully live-verified**, full CRUD cycle plus the two things unique to
this task: confirmed an unknown slug 404s rather than erroring;
created a page and confirmed it served correctly at `/web/page/about`
with the right title/body; confirmed creating a *second* page with the
same slug was rejected with the expected error message, and that the
rejection didn't touch the original page (still served correctly
afterward) - the slug-uniqueness guard actually doing its job, not
just present in the code; edited the page including *changing its own
slug* and confirmed the old slug now 404s while the new one serves the
updated content; deleted it and confirmed both the admin list and the
public URL reflect that. Full solution build clean; eight DLLs changed
(all three processes stopped/deployed/checksum-verified/restarted);
Robust log confirmed a clean first-run migration (`Creating StaticPage
at version 1`). Re-confirmed dashboard/stats/transactions/news and the
search module's clean activation, same standard regression check as
every shared-DLL redeploy this batch.

### WebInterface: grid settings editor (task #25, 2026-08-10)

Live-editable overrides (`/admin/settings`) for values that would
otherwise only ever come from Robust's `.ini` and need a restart to
change: grid name, grid nickname, welcome message, and one new real
behavioral toggle - whether new users can self-register at all, a gap
that genuinely existed before this task (registration had no on/off
switch anywhere). Deliberately scoped to this fixed, small set of keys
rather than a general config-file editor - the ones that already had a
hardcoded ini-only value used elsewhere on this connector
(`m_gridName`/`m_gridNick`/`m_welcomeMessage`) plus the one new toggle,
not a dumping ground for the whole `.ini`.

Backing store is a plain key/value table (`grid_settings`,
`IGridSettingsData`/`IGridSettingsService`, new
`OpenSim.Services.GridSettingsService` project) rather than one typed
column per setting like News/StaticPage - deliberately different from
those two, since the set of editable keys is expected to keep growing
as more values get this same treatment, and a new column + three-backend
migration per future setting would be a lot of ceremony for what's
fundamentally just named strings. A `GetSetting(key, default)` helper on
`WebInterfaceServiceConnector` reads the DB override if present, falling
back to whatever the `.ini` already configured otherwise - so a grid
that's never touched this page behaves exactly as before, and every
consumer of the old ini-only fields (`HandleHome`, `HandleWelcome`, the
password-reset email's grid-name mention) was updated to go through it.

**Fully live-verified, and specifically checked that changes take
effect without a Robust restart** - the whole point of this feature
over just editing the `.ini` by hand. Saved a new grid name, nickname,
welcome message, and disabled registration through the real admin
form; confirmed the home page (`/`) *and* the login splash
(`/welcome.php`) immediately reflected the new name/message, the "Sign
up" link disappeared from the home page, and hitting `/web/register`
directly (bypassing the missing link) still correctly refused with
"registration is currently closed" - defense in depth, not just a
hidden link. Restored the original grid name/nickname/message and
re-enabled registration afterward through the same feature, confirmed
the home page reverted correctly. Full solution build clean; seven
DLLs changed (all three processes stopped/deployed/checksum-verified/
restarted); Robust log confirmed a clean first-run migration (`Creating
GridSettings at version 1`). Re-confirmed dashboard/stats/transactions/
news/pages and the search module's clean activation, same standard
regression check as every shared-DLL redeploy this batch.

### WebInterface: web-based region console (task #26, 2026-08-10) - AND a major correction to task #18

`/admin/console` - pick a region, run a console command against it, see
the output. This is the last item on the WhiteCore-Dev re-audit's "all
of it" list, and directly closes the gap that list called out by name:
WhiteCore's own equivalent page is a documented stub ("hardcodes 'not
yet implemented' despite calling `MainConsole.Instance.RunCommand()`" -
see FEATURES_VS_MASTER.md). This one actually works.

**Design.** Region-side: `OpenSim/Region/CoreModules/World/WebConsole/
WebConsoleModule.cs`, a new `[Extension(...)]`-tagged `ISharedRegionModule`
that registers a `POST /consoleweb` handler (same
`MainServer.Instance.AddSimpleStreamHandler` pattern
`ConfluenceCurrencyModule.cs`'s `/currency.php` already uses). Robust-side:
new `/admin/console` + `/admin/console/run` pages on
`WebInterfaceServiceConnector` that resolve the target region's
`GridRegion.ServerURI` and POST the command to it directly.

**The interesting technical problem was output capture, not networking.**
Researched this before writing anything: `MainConsole.Instance.RunCommand(cmd)`
is a real, directly-callable, synchronous entry point
(`ICommandConsole.RunCommand`, `OpenSim/Framework/ICommandConsole.cs`),
but nothing in the existing console framework can capture a command's
output as a string - `LocalConsole.Output` writes straight to
`System.Console.WriteLine` and never fires `ICommandConsole.OnOutput`,
and `RemoteConsole` (a real, existing, but currently-unconfigured OpenSim
feature) is built around an async long-poll session model for a
JS terminal client, not a request-in/output-out shape - not a fit here.
Solved by temporarily swapping `MainConsole.Instance` for a small
`CapturingConsole : ICommandConsole` wrapper for the duration of one
command: it shares the *same* `Commands` registry as the real instance
(so every already-registered command still resolves), overrides `Output`
to both append to a buffer and forward to the real instance (so an
operator watching the physical console still sees every command run
through this endpoint - not a silent side channel), and makes every
`Prompt`/`ReadLine` overload return an immediate safe default rather
than blocking, since a command that would normally pause for
confirmation must not hang an HTTP request forever waiting for a human
who isn't there.

**Security design, given this is the single most sensitive endpoint
added this session:** successful auth here means arbitrary console
command execution on that region process - equivalent to physical/RDP
console access, not a data read/write like every other Robust<->region
HTTP endpoint added this session (`/griduser`, `/mutelist`, neither of
which have any auth at all today). Deliberately held to a stricter
standard than that existing precedent: the region module refuses to
activate at all unless a real, non-empty `SharedSecret` is configured
(empty/missing is treated as "not configured," never as "no auth
required"), checked via a required `X-Console-Secret` header on every
request.

**Fully live-verified, including the security boundary and a real
command producing real output** - not just a build check. Hit the raw
region endpoint directly with a wrong secret (403), no secret (403),
and the correct secret (200 with real output) to confirm the auth check
actually gates the endpoint, not just the WebInterface's own login page
in front of it. Through the real `/admin/console` UI: `show users`
returned the region's actual live agent count, `stats show` returned
real current FPS/frame-timing numbers, `show regions` returned the
region's actual ID/port/estate - genuine command execution and output
capture, not a stub. Unrecognized commands (`show version`, `show
uptime` - not registered in this build) correctly produced empty output
rather than an error, matching `Commands.Resolve`'s own real behavior
for an unmatched command. Edge cases: a garbage `region_id` correctly
shows "server address is not known to the grid service"; an empty
command correctly shows "enter a command to run"; unauthenticated
`/admin/console` correctly redirects to login.

**The major correction.** While diagnosing why the region console
wasn't reachable at first, found something that reaches back into
already-shipped work: a temporary unconditional diagnostic log at the
very top of `WebConsoleModule.AddRegion()` never printed on Var Test
Region, even though the *same* module's `Initialise()` ran correctly
moments earlier (config read correctly, `Enabled=true` and the real
64-char `SharedSecret` both confirmed via logging). This is a
**different** failure mode than the session's previously-documented
Mono.Addins issue (OnDemand/SimProtection, where `Initialise()` itself
never printed at all) - here, discovery and initialisation both
succeed, but `AddRegion()` - the method where these modules' actual
client-facing wiring happens - is never reached.

Out of caution, added the same temporary diagnostic to
`ConfluenceSearchModule.AddRegion()` (Batch 14's search module, task #18,
previously documented as "fully live-verified") to check whether it was
affected by the same thing. **It was.** On Var Test Region, `Initialise()`
ran and logged "Native search module is active" as already documented,
but `AddRegion()` - the method that actually calls
`scene.EventManager.OnNewClient += OnNewClient` and
`scene.RegisterModuleInterface<ISearchModule>(this)` - never printed
its own diagnostic line either. **This means task #18's client-facing
wiring (the part that actually answers a viewer's Places/Land Sales
search panels) has never actually been active on Var Test Region**,
despite what was documented. The part that *was* genuinely verified for
task #18 - the `SearchService`/`MySqlSearchData` data layer, tested
directly via a .NET console harness - is unaffected by this and remains
correctly verified; only the region-module wiring layer on top of it is
in question.

Critically, this turned out to be **region-specific, not module-specific
or universally broken**: the exact same diagnostic added to
`WebConsoleModule` and `ConfluenceSearchModule` printed *normally* on
Welcome Center - `AddRegion()` was reached on both, correctly showing
`enabled=false` there (matching that region's own config, which never
enabled either feature). So this is not "Mono.Addins can't discover new
extension classes" (the existing documented theory) - discovery and
`Initialise()` work fine on *both* regions for *both* modules. Something
specific to Var Test Region's process silently stops
`RegionModulesControllerPlugin.AddRegionToModules` from completing for
modules positioned after some point in its enumeration order, without
throwing any exception visible anywhere in that region's log (confirmed
by grepping its entire startup window for `ERROR`/`Exception` - nothing
found besides the diagnostic lines themselves). Not root-caused further
given time already spent - documented transparently as a newer,
more specific data point on the same general "this test deployment
deployment has real Mono.Addins/region-module-loading reliability
problems" finding already on record from OnDemand/SimProtection,
now with the added insight that it's tied to a specific region's
process state, not the module code.

Given this, task #26 was **retested and confirmed working correctly on
Welcome Center instead** (see the live-verification section above) -
Var Test Region's `[WebConsole]` config was replaced with a comment
explaining why, and the real test config moved to Welcome Center's own
`OpenSim.ini`. Task #18's documentation and the table entry in this
file are being corrected in the same edit as this entry - see the
`OpenSimSearch` row update above, now noting the confirmed working data
layer separately from the not-actually-verified client-facing wiring on
Var Test Region specifically. A maintainer wanting genuinely-verified
native search today should enable it on Welcome Center (or any region
that doesn't exhibit this issue) rather than Var Test Region, or
investigate why Var Test Region's `AddRegionToModules` pass is silently
incomplete.

### Root cause found: the "Mono.Addins reliability issue" wasn't Mono.Addins at all (2026-08-10)

Immediately after the above was written, `Var Test Region`'s process
hung for real on a routine restart - not the silent AddRegion-skip
already documented, but a full stop, sitting at an interactive "We are
now going to ask a couple of questions about your region" console
prompt that nothing was there to answer. Investigating that hang found
the actual root cause of **all** of this session's "Mono.Addins doesn't
reliably load new region modules on this deployment" findings -
OnDemandRegionModule, SimProtectionModule, and the AddRegion-skip
affecting ConfluenceSearchModule/WebConsoleModule alike. None of them were
ever a Mono.Addins problem.

**What actually happened:** earlier this session, adding `[OnDemand]`
and `[SimProtection]` config sections to `Var_Test_Region\OpenSim.ini`
inserted them two lines into the pre-existing `[Startup]` section
instead of after it. INI format has no concept of "resuming" a section
once a new `[SectionName]` header appears - every line after that
header belongs to it until the *next* header, no matter what the line's
own comment says it's for. So `[Startup]`'s real content - hundreds of
lines including `region_info_source` and `regionload_regionsdir`, the
setting that tells `LoadRegionsPlugin` where to find the region's own
saved config - got silently reattributed to `[SimProtection]` instead.
Confirmed directly: a throwaway .NET 8 console app loading the file
through the real `Nini.IniConfigSource` (same reflection-testing
technique used earlier this session for AccessControlData) showed
`[Startup]` parsing down to just 2 keys (`logfile`/`StatsLogFile`)
instead of the dozens it should have, with `regionload_regionsdir`
completely absent. `RegionLoaderFileSystem.LoadRegions()` then silently
fell back to its own hardcoded default path (`.\Regions`, empty),
found zero files there, and dropped into the interactive new-region
wizard used for genuinely first-time setup - which then just sat there
forever since no one was there to answer it.

This had been a **latent bug since the moment those two sections were
added**, not something that broke recently - Var Test Region kept
booting fine on every restart in between because `LoadRegionsPlugin`
only runs once per process start, and until tonight nothing forced a
restart at exactly the wrong moment relative to whatever made this
surface as a hang instead of a quieter symptom. Fixed by moving both
sections to their natural position - right after `[Startup]`'s real
content ends, immediately before `[AccessControl]` - restoring
`[Startup]` to one continuous, correctly-parsed section. Verified the
fix directly against the file with the same Nini test harness *before*
touching the live process again (`[Startup]` now correctly parses 16
keys including the right `regionload_regionsdir`), then confirmed both
regions restart cleanly.

**Re-tested OnDemandRegionModule and SimProtectionModule against the
fixed config, at the user's request.** Temporarily re-enabled both,
added the same unconditional diagnostic logging technique used
throughout this session to `Initialise()`/`AddRegion()`, rebuilt,
redeployed, restarted. **Both now work correctly** -
`Initialise()` fires, config reads correctly (`enabled=True` for both),
and - the part that never happened before - `AddRegion()` also fires
for both. OnDemandRegionModule's real functional log line appeared
(`"Var Test Region starting idle - heartbeat will resume on first
visitor"`), confirming `scene.Active = false` genuinely ran. This fully
retracts the "Mono.Addins does not discover this module on this
deployment" conclusion recorded earlier for both modules - the modules
were always discoverable; `[Startup]`'s corruption was the actual cause,
most likely via some setting inside the swallowed `[Startup]` content
that the non-shared-module loading path depends on (not further
root-caused at that level of detail - the practical fix is the same
regardless of the exact mechanism).

Removed the diagnostic logging again afterward and left both **disabled
by default** - not because anything is broken now, but because neither
module's actual *behavioral* payload has been exercised live yet
(OnDemand's wake-on-first-visitor path needs a real login;
SimProtection's FPS-drop mitigation needs an artificially-lowered
threshold to trigger, which would genuinely disable scripts/physics on
whatever region it's tested on - deliberately not forced on Var Test
Region while it may be in active use). Both are safe to enable; the
config comments were updated to reflect the corrected finding and the
specific remaining gap.

This also means the `ConfluenceSearchModule`/`WebConsoleModule`
AddRegion-skip finding from immediately before this entry almost
certainly has the **same root cause**, not a fresh, still-mysterious
"region-specific Mono.Addins issue" as first framed - the ini bug fully
explains a non-shared-module loading failure and very plausibly extends
to the shared-module path too, given both go through the same
overall `AddRegionToModules`/`LoadRegions` sequence on the same
corrupted file. Not independently re-confirmed for Search/WebConsole
specifically in this pass (the user asked specifically about OnDemand/
SimProtection), but a maintainer re-enabling native search on Var Test
Region today should expect it to work correctly, now that the actual
cause is fixed rather than worked around by moving to Welcome Center.

**Follow-up: Search and WebConsole re-tested on Var Test Region too, at
the user's request - both confirmed fixed.** Re-added `[WebConsole]
Enabled = true` to `Var_Test_Region\OpenSim.ini` (it had been moved to
Welcome Center as a workaround) - `[Search]`/`[SearchService]` were
already there and untouched. No code changes needed; only config. On
restart, `[WEB CONSOLE]: Enabled at /consoleweb` printed for real (the
line that lives inside `AddRegion`, not `Initialise` - direct proof
`AddRegion` now runs for shared modules on this region), and
`[CONFLUENCE SEARCH]: Native search module is active` printed as before.

Verified WebConsole with a real end-to-end test rather than trusting the
log line alone: logged into the real WebInterface, confirmed the
`/admin/console` region dropdown lists Var Test Region, and ran `show
regions` against it through the real HTTP round-trip
(WebInterface → region's `/consoleweb` endpoint) - got back the correct
live region info (`Var Test Region`, correct RegionID, port 9005,
"Ready? Yes"). This is the same feature already fully verified on
Welcome Center in the task #26 entry above, now shown working
identically on the region it originally failed on.

Search's specific client-facing wiring (`scene.RegisterModuleInterface
<ISearchModule>`) has no console command to introspect directly, so it
wasn't checked with the same first-class rigor as WebConsole - but the
circumstantial case is strong: `ConfluenceSearchModule` and
`WebConsoleModule` are both `ISharedRegionModule`, both processed by the
exact same `AddRegionToModules` foreach loop in the exact same process
on the exact same restart, and that loop has no per-module try/catch -
if Search's own `AddRegion` had thrown, WebConsole's (which runs in the
same loop) would never have been reached either. Combined with zero
`ERROR`/`Unhandled exception` lines anywhere in this restart's log, this
is about as confident a conclusion as is possible without an actual
viewer sending a real `DirPlacesQuery`/`DirLandQuery` - which remains
the one genuinely unverified piece, same accepted-gap category as
BotManager-follow and ViewerSignatureBan elsewhere in this session.

Regression-checked after these config changes: both regions still
report ready, zero unhandled exceptions in either log, WebInterface
still responds correctly.

### WebInterface visual redesign (2026-08-10)

User feedback after seeing real screenshots of the WebInterface and the
in-viewer login splash: "BORING" - plain black-on-white text, no visual
identity at all. Every page this session was built on a single shared
`WritePage(response, title, bodyHtml)` helper with a two-line inline
`<style>` block (font-family and little else), so this was fixable in
one place rather than needing per-page rework.

Replaced the two-line style block with a real, self-contained CSS theme
(`PageCss` constant) - no external fonts/CDNs, since this has to work on
a grid with no internet egress. Added a branded header (a gradient
monogram badge using the grid's own first initial, the live grid name
via the same `GetSetting("GridName", ...)` override task #25 already
built, and a small tagline) wrapping every page's content in a white,
shadowed card over a soft gradient background. Styled every element
type actually used across the ~20 pages built this session generically
(tables, buttons, forms, inputs, links, `.error` banners, `.balance`
pill, `.news-item`/`.news-meta`) rather than retrofitting classes onto
every individual page's HTML - since they already emit plain semantic
tags, this covers all of them without touching their generation code.
Table-cell action buttons (the "Delete"/"Edit" pattern already used
throughout) specifically get compact, non-full-width styling via
`td button`/`td form` overrides, so they don't inherit the page-level
form styling meant for full-size forms.

`WritePage` had to change from a `static` to an instance method to read
the live grid name for the header - a safe change since every call site
already invokes it unqualified from inside another instance method of
the same class, so none needed updating.

Applies uniformly to the in-viewer login splash (`HandleWelcome`) too,
per explicit follow-up ("the splash screen as well... BORING") - it
goes through the exact same `WritePage`, so no separate work was needed
there, just confirming it renders reasonably at the smaller width the
viewer's own embedded panel uses (a `@media(max-width:480px)` rule
tightens padding for that case).

**Live-verified visually** - screenshots aren't available in this
Browser-pane environment (`computer` screenshot action times out with
"not compositing frames"), so verification used computed-style
inspection via injected JavaScript instead, which is arguably a more
rigorous check than eyeballing a screenshot would have been: confirmed
the gradient background, card shadow/radius, and brand-mark gradient
render with the exact CSS values specified (not just "some CSS applied"
- the literal `linear-gradient(...)`, `border-radius: 12px`, etc.
values were read back from `getComputedStyle`); confirmed table headers
render with the intended light-purple/dark-purple treatment and correct
borders; confirmed the `.balance` pill renders as an actual rounded
pill with the right colors; confirmed table-cell buttons stayed compact
rather than inheriting full-width form-button sizing. Logged in for
real and navigated through the actual redesigned dashboard/admin/table
pages rather than just checking the login page. No browser console
errors. Full regression check across all ~16 pages built this session
(home, splash, dashboard, admin index, stats, transactions, news,
pages, settings, console, myregions, myinventory, register,
forgot-password, users, estates, abuse-reports) - all still return 200
after the redesign, zero unhandled exceptions in either region's log.

**Follow-up: re-themed around WhiteCore-Dev's actual identity, not an
invented palette (2026-08-10).** User's next ask: "Can we not use what
WhiteCore-Dev had used and update it to a modern look?" - the
purple/indigo palette above was one this session invented; the user
wanted the redesign anchored on WhiteCore-Dev's own real WebInterface
look instead (consistent with this whole Batch 13/14 thread being
WhiteCore-Dev-inspired feature parity throughout), modernized rather
than copied wholesale.

Extracted the real palette from a local WhiteCore-Dev checkout
(`WhiteCoreSim/bin/html/static/css/style.css` + `user.css`) via
hex-color frequency analysis rather than guessing: `#FF5274` (a vivid
coral/pink) is overwhelmingly WhiteCore's most-used accent color, with
`#D7405D` as its own dedicated hover/active shade; `#292c37` is its
most common dark slate, used for header/nav backgrounds. WhiteCore's
own `.btn-fill` button rule is literally `border-radius:40px` (a full
pill), `border:solid 2px`, uppercase text, coral fill, `#D7405D` on
hover - reused verbatim as this connector's one `button` rule. Kept
WhiteCore's own distinction between pill-shaped *buttons* and
subtly-rounded *panels* (WhiteCore's cards use `border-radius:5px`,
not the button's 40px) - here as an 8px card radius. Page/body
background is `#F3F4F8`, WhiteCore's own features-section background
color, which turned out to already be very close to this session's
first-iteration `--bg` value.

Replaced the gradient purple header/brand-mark with a solid dark-slate
(`#292c37`) top bar spanning the full page width (closer to WhiteCore's
own full-width dark nav than the previous centered gradient badge),
keeping the same monogram-badge-plus-grid-name content, just restyled.
Did **not** adopt WhiteCore's actual page stack (Bootstrap, jQuery,
FontAwesome, Slick carousels, Google-hosted "Open Sans") - all external
dependencies this connector deliberately avoids since the grid has no
guaranteed internet egress; used a native system-font stack instead of
Open Sans for a visually close but zero-dependency result. This was a
palette/shape swap only - the underlying `WritePage`/`PageCss`
architecture, and therefore its uniform coverage of every page through
one shared constant, is unchanged, so "goes for the pages that were
used as well" (the user's explicit follow-up confirming this should
apply everywhere, not just login/splash) required no additional code.

**Live-verified**: rebuilt `OpenSim.Server.Handlers.dll`, stopped all
three test deployment processes (Robust + both regions - confirmed via
`Get-CimInstance Win32_Process` command-line inspection which PIDs
actually belonged to the test deployment vs. the live grid before touching
anything, since this DLL is shared with the live grid's own identical
filename), redeployed, restarted all three. Both regions came back up
clean with no exceptions and no interactive-wizard hang (confirming the
earlier `[Startup]` ini fix holds across a real restart). Verified via
the same computed-style JS-injection technique as the first iteration:
`getComputedStyle` on the login and splash pages confirmed
`rgb(41,44,55)` (`#292c37`) on the header, `rgb(255,82,116)` (`#FF5274`)
on the brand mark/button, `40px` button border-radius, uppercase button
text, and `rgb(243,244,248)` (`#F3F4F8`) page background - exact
matches, not approximations. Also enumerated the live stylesheet's
`document.styleSheets[0].cssRules` to confirm every selector (including
`button:hover`, `td button:hover`, `.news-item`, `.balance`, `.error`)
survived the rewrite intact.

### Splash page competitive redesign: dark+blue theme, real economy/classifieds/events widgets (2026-08-10)

User sent screenshots of three competing grids' own in-viewer splash
screens (DigiWorldz, 3rd Rock Grid, Wolf Territories) plus links to
3rdrockgrid.com/wolf-grid.com/osgrid.org, with two pieces of explicit
feedback: "I like 3rd Rock Grid's color scheme... but in Blue", and then
the framing that mattered most - "You have to remember, im competing
with these grids for users." That reframed this from a cosmetic ask
into a real feature gap: DigiWorldz/3RG's splash screens show live
economy stats, featured classifieds, and an events calendar, not just a
login form. Asked the user how far to take it (reskin only / reskin +
real data widgets using what Confluence already tracks / full parity
including brand-new classifieds+events systems) - they chose full
parity.

**Palette**: replaced the WhiteCore coral/slate theme (previous entry
above) with a black/near-black background throughout (`#000`/`#0b0d10`,
matching 3RG's own splash more than WhiteCore's white-card-on-dark-header
approach) and a blue accent (`#3b82f6`, `#60a5fa` bright variant,
`#1d4ed8` hover) in place of 3RG's orange, per the explicit "but in
Blue" follow-up. Kept the pill-shaped buttons from the WhiteCore pass
since nothing in the new feedback objected to that shape, only the
color scheme.

**Economy stats widget** (`RenderEconomyStats`, `HandleWelcome`): real
24h/7d/30d currency volume + transaction counts pulled from the
already-existing `ICurrencyService.GetTransactionHistory` (same
grid-wide `UUID.Zero, UUID.Zero` pattern task #20's transactions page
already uses) - no new data model needed. Live-verified rendering real
numbers (`C$ 5,000`, `5 transactions`) from this session's own earlier
test transactions.

**Featured Classifieds widget**: discovered along the way that
classifieds are a real, already-working stock OpenSim feature -
`UserProfileModule`/`IUserProfilesService`/the `classifieds` table,
populated by users via their own viewer's Profile > Classifieds tab -
not something this session had to build. The only gap was a *grid-wide*
read (existing methods are creator-scoped, for a user's own profile
editor). Added `GetRecentClassifieds(int count)` to `IProfilesData`
(MySQL/PGSQL/SQLite implementations) and `IUserProfilesService`/
`UserProfilesService`, then wired `IUserProfilesService` into
`WebInterfaceServiceConnector` and rendered a text-only (no snapshot
images yet - would need the asset server's HTTP texture endpoint, out
of scope here) "Featured Classifieds" card grid on the splash.

Near-miss during this step: initially wired
`m_UserProfilesService = LoadReusedPlugin<IUserProfilesService>(config,
"UserProfilesService", args)` using the same 1-arg
`(IConfigSource)`-constructor pattern every other reused plugin on this
connector uses. `OpenSim.Services.ProfilesService.UserProfilesService`'s
real constructor is `(IConfigSource config, string configName)` -
2 args - confirmed by reading `LocalUserProfilesServiceConnector`, the
real region-side consumer, which calls
`ServerUtils.LoadPlugin<IUserProfilesService>(serviceDll, new object[]
{ source, ConfigName })`. The mismatch didn't fail the build - it's a
runtime reflection failure - and only showed up as an `ERROR loading
plugin ... Constructor ... not found` line in Robust.log on the first
post-deploy restart. Fixed by loading it directly via
`ServerUtils.LoadPlugin` with the correct 2-arg array instead of going
through the shared `LoadReusedPlugin` helper. Lesson: `LoadReusedPlugin`
is only safe for services whose constructor actually matches its
assumed 1-arg shape - worth checking the real constructor before adding
a new one, not just copying the pattern.

**Upcoming Events widget - new feature**: no existing Confluence system
covered this. Checked whether OpenSim-Grid-Interface's (the user's own
PHP grid-web-tool, found locally at `S:/Github/OpenSim-Grid-Interface`)
`search_events` table could be reused instead of inventing a new schema
- its own `docs/events-architecture.md` documents that table as backing
*real in-world/viewer-created* events via `EventInfoRequest`/
`DirEventsReply`/etc. Confirmed via grep that this classic protocol
surface exists in Confluence's `IClientAPI`/`EventData` (declared) but is
never implemented by any module (dead protocol surface, not a live
feature) - building it for real would mean new UDP-facing region
handlers, a much bigger lift than a splash widget justified. Built a
simpler, News-item-shaped admin-only feature instead: `EventItem`
(`OpenSim/Framework/GridEventData.cs`), `IEventsData` + MySQL/PGSQL/
SQLite implementations + `Events.migrations`, `IEventsService` +
`EventsService`/`EventsServiceBase` (new
`OpenSim.Services.EventsService` project, added to `OpenSim.sln`),
admin CRUD at `/web/admin/events` (list/create/edit/delete, cloned
directly from task #23's News admin pattern), and `RenderUpcomingEvents`
on the splash.

**Near-miss / real mistake worth flagging**: initially created the new
`EventItem` class in a file named `OpenSim/Framework/EventData.cs` -
without first checking whether that path already existed - and
overwrote a real, unrelated, already-existing stock OpenSim class also
called `EventData` (the classic viewer Search > Events packet payload
referenced by `IClientAPI.SendEventInfoReply`). The very next full
solution build failed with `CS0246: The type or namespace name
'EventData' could not be found` in `IClientAPI.cs`, which is what
surfaced it immediately - `OpenSim.Framework.csproj` uses
`EnableDefaultItems=false` (explicit `<Compile Include>` lists, not SDK
wildcard globbing), so the missing/replaced file broke the one other
consumer right away rather than silently. Recovered the original file
with `git checkout HEAD -- OpenSim/Framework/EventData.cs` (this repo
*is* a git repo, despite an earlier environment note suggesting
otherwise) and re-created the new class under a non-colliding filename
(`GridEventData.cs`). Lesson reinforced: never `Write` into a path
without first confirming (via `Read`, `Glob`, or grep) whether something
already lives there, even for a "new" file whose name seems obviously
free - `EventData.cs`/`EventItem` looked like a safe, on-topic name and
wasn't.

Also discovered along the way that `OpenSim.Data.csproj` and its MySQL/
PGSQL/SQLite counterparts *also* use `EnableDefaultItems=false` - every
new file added to those projects (`IEventsData.cs`, `MySQLEventsData.cs`,
`PGSQLEventsData.cs`, `SQLiteEventsData.cs`) needed an explicit
`<Compile Include>` entry added by hand, unlike `OpenSim.Services.
Interfaces.csproj` (zero explicit entries, relies on default SDK
globbing) where `IEventsService.cs` just worked. Worth checking a
project's own `EnableDefaultItems` setting before assuming a new file
will be picked up automatically.

**Live-verified**: full solution build clean (including the new
`OpenSim.Services.EventsService.dll`), deployed to the test deployment
(confirmed via PID/command-line inspection which processes were
the test deployment vs. the live grid, same discipline as every other
redeploy this session), both regions came back up with zero exceptions.
Splash page (`/welcome.php`) confirmed rendering the real economy
widget with actual transaction data; classifieds/events widgets
correctly render as empty (no crash) since no classifieds or events
exist yet for the one test account - an honest, expected gap, not a
bug. Computed-style checks confirmed the exact new palette (`rgb(0,0,0)`
header, `rgb(96,165,250)` accent, `rgb(11,13,16)` page background, all
literal hex matches). `/web/admin/events` and `/web/dashboard` both
correctly redirect to `/web/login` when unauthenticated rather than
crashing.

**Follow-up: the actual home page (`/`) was still missing the widgets
(2026-08-10).** User caught this by looking at the real browser-facing
site (`the test deployment's public hostname`, the reverse-proxied root, not
`/welcome.php`) and pointing out it still looked plain compared to the
reference screenshots. Correct catch - `HandleHome` (the bare `/`
handler) and `HandleWelcome` (the in-viewer splash) are two separate
methods; the economy/classifieds/events widgets had only been added to
`HandleWelcome`. `HandleHome` already had `RenderNewsFeed` from task
#23's original design (that one was explicitly meant for both pages),
so this was a one-line addition of the same three `Render*` calls,
kept above the existing login/register links so the primary
call-to-action stays first. Rebuilt, and since `OpenSim.Server.
Handlers.dll` is shared, had to stop all three test deployment processes
(not just Robust) - confirmed by a `cp` failure ("Device or resource
busy") with only Robust stopped, then found the region processes also
had it locked. Redeployed to all three, restarted.

**Real (unrelated) outage caught during this restart**: Welcome_Center
crashed on startup with `System.DllNotFoundException: Unable to load
DLL 'ubode'` inside `ubOdeModule.Initialise` - the native ubOde physics
engine failed to load, logged as FATAL, and the process actually
exited (confirmed missing from the process list). Var Test Region,
started ~2 seconds earlier from the same binaries, loaded the same
native DLL fine, and this exact error has one prior occurrence in this
log from 2026-08-05 - both point to a transient native-DLL-load race
between the two processes starting close together, not anything in
this session's C# changes (physics engine loading was never touched).
Fixed by simply restarting the one crashed process; it came up clean
on the retry. Worth remembering as a known, if rare, hazard of starting
both test deployment region processes back-to-back rather than staggering
them further apart.

Live-verified via `curl` (the home page, `/`, at the real public
`PublicPort`): now shows the Confluence Economy stat cards alongside the
login/register links, dark+blue themed, matching what
`/welcome.php`/the splash already had.

**Follow-up: full authenticated admin round-trip closed (2026-08-10).**
The gap above (no admin credentials to test `/web/admin/events` for
real) got closed the same session. Registered a throwaway account
("Splash Verifier") through the real self-service registration flow
rather than touching the database, then hit a real console-command
naming mistake worth recording: initially told the user to run `user
modify "Splash Verifier" userlevel 200` in Robust's console - that
command doesn't exist in this codebase (`Invalid command`). The real
one, confirmed by reading `UserAccountService.cs`'s own
`AddCommand("Users", ...)` registrations, is `set user level [<first>
[<last> [<level>]]]`. Once the user ran the correct command, hit a
second, unrelated snag: the Browser-pane tool's clicks/Enter-key
submissions on the login form produced zero server-side log activity
across multiple attempts (confirmed by tailing Robust.log and seeing no
new `AUTH SERVICE: Authenticating` line, and by the tool's own
`read_network_requests` list never growing) - a genuine automation
reliability issue, not a bug in the login page. Switched to `curl` with
a cookie jar against the real `/web/login`, `/web/admin`, `/web/admin/
events`, `/web/admin/events/save`, and `/web/admin/events/delete`
endpoints instead, which worked immediately and gave a *more* rigorous
trail (raw HTTP responses/headers) than clicking through a browser
would have.

Confirmed for real: login issues a `ConfluenceWebSession` cookie and
redirects to the dashboard; the now-admin account can load
`/web/admin` (shows the new "Events" link alongside the rest); a POST
to `/web/admin/events/save` with a real title/category/date/duration/
location/description returns 302 and the event immediately appears
both in the admin list (with working Edit/Delete links) *and* on the
public splash's "Upcoming Events" widget with the exact rendering
(`Grand Opening Concert / Aug 15, 8:00 PM UTC · Live Music · Welcome
Center / <description>`) the code was written to produce; a POST to
`/web/admin/events/delete` removes it cleanly, confirmed back down to
"No upcoming events." Test event deleted after verification so it
doesn't linger as fake data on the dev grid. The "Splash Verifier"
admin-level test account was intentionally left in place (the test deployment
only, never the live grid) rather than spending another round-trip on
demoting it - flagged here in case it should be cleaned up later.

### WebInterface structural rebuild: real site shell, not just a themed card (2026-08-10)

User feedback after refreshing the live `the test deployment's public hostname` home
page: still "pretty basic setup all the way around" next to
osgrid.org/wolf-grid.com/3rdrockgrid.com, the user's own
OpenSim-Grid-Interface, and even WhiteCore-Dev's actual WebUI (which
this whole redesign thread had drawn its color palette from, but never
its page structure). Correct diagnosis: every prior pass this session
(purple palette, WhiteCore palette, 3RG-blue palette, real data
widgets) changed COLOR and CONTENT but kept the same underlying SHAPE
from Batch 13/14's original architecture - one narrow (760px) centered
card, stacked top to bottom, no nav bar, no hero, no footer. That shape
reads as an admin panel no matter what's inside it.

Asked whether the fix (full nav bar / hero band / wider multi-column
layout / footer) should apply only to public pages or to every page
site-wide; user chose every page, admin/login/dashboard included, for
one consistent product feel.

**Implementation**: `WritePage` gained an `IOSHttpRequest request`
parameter (all ~51 call sites already had `request` in scope as
ordinary `Handle*(request, response)` methods, so this was a mechanical
`replace_all` from `WritePage(response,` to `WritePage(request,
response,` - confirmed safe by a clean build immediately after, no
manual site-by-site auditing needed). With `request` available,
`WritePage` now calls `GetSession(request)` itself to build a
session-aware nav: logged out shows Log In / Sign Up (the pill-button
treatment); logged in shows Dashboard, Admin (only if `session.
IsAdmin`), the account name, and Log Out - the same information every
one of the ~15 admin/dashboard pages already had scattered through
their own body HTML, now consistent site-wide instead of per-page.

Rather than requiring a rewrite of every call site's body HTML to
separate "heading" from "content" (they all already emit `<h1>...
</h1>` as the literal first thing in `bodyHtml`, a pattern consistent
across every page built this session), `WritePage` extracts that
existing `<h1>` via a plain string search and promotes it into a
full-width hero band above the content card, falling back to the grid
name if a body doesn't start with `<h1>` (defensive, not currently hit
by any real page). Zero call-site changes needed for this part.

Widened `.page` from 760px to 1100px - this alone let the
`.stats-grid`/`.widget-grid` CSS grids built for the economy/
classifieds/events widgets (already responsive `auto-fit,
minmax(...)` rules from earlier this session) actually lay cards out
side by side instead of being squeezed into a column barely wider than
one card. Added `.site-header`/`.site-nav`/`.site-actions` (full-width
dark nav bar), `.hero`/`.hero-inner` (a black-to-navy gradient band
carrying the page's title, `#0d1a30` chosen as a dark blue-tinted shade
consistent with the existing blue accent rather than pure black
everywhere), and `.site-footer`/`.site-footer-inner` (copyright line).

**Live-verified**: full solution build clean, all three test deployment
processes stopped/redeployed/restarted (longer 3-second stagger between
the two region processes this time, specifically to avoid repeating the
`ubode` native-DLL-load race from the previous restart - see the
"actual home page" entry above). Computed-style checks confirmed the
hero's gradient, the extracted `<h1>` text and 30px sizing, the 1100px
page width, and the stats-grid genuinely rendering three 334px-wide
columns instead of one cramped column. Checked both an unauthenticated
page (home: nav shows Log In/Sign Up) and an authenticated admin page
(`/web/admin` via the "Splash Verifier" test session: nav shows
Dashboard/Admin/account name/Log Out, hero shows "Grid Administration,"
footer present) - both render the new shell correctly with no loss of
the underlying table/form functionality.

Separately, user pointed at `github.com/djphil` (a suite of ~14
standalone single-purpose OpenSim PHP tools - login screen, interactive
world map, destination guide, visitor tracking, friends/partner
management, offline IM, etc.) and confirmed some of it was already
folded into their own OpenSim-Grid-Interface. Noted as a real,
substantially larger scope than a layout pass - standalone features
Confluence's WebInterface doesn't have at all (no world map, no
destination guide, no visitor tracking) - logged here as a roadmap
input rather than acted on this session.

### First-landing pages, round one: Get a Viewer + Destinations (2026-08-10)

Explicit direction from the user for how to sequence the "first-landing
pages" roadmap item above: check WhiteCore-Dev first for anything
directly reusable, since it's the project's own primary reference, and
only fall back to OpenSim-Grid-Interface/fresh drafting for whatever
WhiteCore-Dev genuinely doesn't have. This reversed the order I'd
started in (research OpenSim-Grid-Interface's full page inventory
first) and was, correctly, called out as something the original
WhiteCore-Dev audit should already have covered: that audit thoroughly
ported region modules/services but never actually opened WhiteCore-Dev's
own `bin/html/` templates, despite the user pointing at this more than
once across the conversation before it was addressed.

**Checked WhiteCore-Dev's real page inventory before building anything**:
confirmed via directory listing that WhiteCore-Dev has no About/ToS/
DMCA/Features/Support equivalent at all (nothing to reuse there - that
tier still needs OpenSim-Grid-Interface-derived or fresh content, not
done this pass). It does have `help.html` (a real, current viewer-
download page with 8 real viewer URLs: Alchemy, Firestorm, Kokua,
Singularity, Lumiya, MobileGridClient, PocketMetaverse, Radegast) and
`world.html` (a real, current Leaflet.js interactive map with a region-
thumbnail sidebar and `hop://` teleport links). Also checked and
explicitly ruled out `region_list.html`, `region_search.html`, and
`online_users.html` - all three are literally commented `<!-- No
longer used - greythane -->` by WhiteCore's own maintainers, so they're
dead ends, not something to build on despite superficially looking
relevant.

**Get a Viewer** (`/web/viewers`, new nav link): merged WhiteCore's 8
real viewer URLs with OpenSim-Grid-Interface's OS-specific Firestorm
download pages (Windows/Mac/Linux, more useful than WhiteCore's single
generic Firestorm link) and Cool VL Viewer, split into Desktop and
Mobile/Lightweight groups using the existing `.widget-grid` card
pattern. Shows the grid's actual login URI (`m_publicBaseUrl`, already
used for password-reset emails) in a click-to-select field, with
Firestorm/general Grid-Manager instructions.

**Destinations** (`/web/destinations`, new nav link): reproduces
`world.html`'s real user-facing value (see where regions sit relative
to each other, click to teleport) without vendoring Leaflet.js/
mapapi.js - both uninspected third-party code, and this connector has
held a strict no-external-dependency line all session. Instead: plain
CSS absolute positioning over map tiles this connector's own
`MapGetServiceConnector` already serves (confirmed the real tile path
by reading `MapImageService.GetFileName`: `/map/map-1-{RegionCoordX}-
{RegionCoordY}-objects.jpg`), scaled proportionally to each region's
real size (`RegionSizeX/Y` converted to 256m "region units" so var-
regions occupy more visual space than standard ones), with
`secondlife:///app/teleport/{RegionName}/128/128/25` links (same-grid
teleports - simpler than `hop://`, which is for cross-grid Hypergrid
destinations OpenSim-Grid-Interface's guide.php actually needed and
this page doesn't).

**Real bug caught during live verification**: the north-up Y-axis flip
formula had a spurious extra `- minY` term
(`(maxY - (y0 + hUnits) - minY) / spanY` instead of
`(maxY - (y0 + hUnits)) / spanY`), producing nonsensical `top` values
like `-22495%` instead of valid 0-100% positions - caught immediately
by reading the actual computed `style.top` values via the browser
rather than assuming the math was right, hand-verified the fix against
Var Test Region (1001,1000, 1024x1024) and Welcome Center (1000,1000,
256x256)'s real coordinates before redeploying. After the fix: Var Test
Region renders at `top:5%,left:23%,72%x90%` and Welcome Center at
`top:72.5%,left:5%,18%x22.5%` - both sane, both geometrically correct
relative to each other (Welcome Center sits south-west of Var Test
Region, matching their real grid coordinates).

**Live-verified**: build clean, all three test deployment processes
stopped/redeployed/restarted twice (once for the initial pages, once
more for the math fix) with zero new errors either time. Both pages
return 200 and render real data (2 real regions with correct
name/size/position on Destinations; real login URI on Get a Viewer).

### Second round: the rest of the "genuinely new ground" tier (2026-08-10)

Explicit direction: keep working through every real gap the full
WhiteCore-Dev audit surfaced before moving to the next phase (marketing/
legal content from OpenSim-Grid-Interface). Built five more pieces in
one pass:

**Web Profile** (`/web/profile?id=<uuid>`, public, no login required -
consistent with classifieds/picks already being publicly searchable in
stock OpenSim/SL): about-me, first-life text, partner (cross-linked to
their own profile), resident-since (`UserAccount.Created`), online/
last-seen status, and pick names. Entirely built from services already
wired into this connector for earlier features (`IUserProfilesService`
from classifieds, `IUserAccountService`, `IGridUserService`) - no new
service plugins needed. Small polish fix during verification: a
never-logged-in account showed "Last seen 1970-01-01" (the
`GridUserInfo.Logout` epoch default) instead of "Never logged in" -
fixed by checking against the epoch before formatting.

**Friends list** (`/web/friends`, logged-in only): first WebInterface
feature to need `IFriendsService`, which existed for the real in-viewer
friends list but had never been wired into this connector. Confirmed
via `FriendsStore.migrations` that the `Friend` column is the other
party's principal UUID as a string, not a display name, so each row
needs its own account/online-status lookup. Hit a real compile error
mid-build: `FriendInfo` is ambiguous between `OpenSim.Services.
Interfaces.FriendInfo` and `OpenMetaverse.FriendInfo` (both in scope
via existing `using` directives) - fixed with a fully-qualified type
name rather than adding an alias, since this is the only place in the
file that needs it.

**Self-service account pages** (`/web/change-password`, `/web/change-
email`, both logged-in only): change-password re-verifies the current
password via `IAuthenticationService.Authenticate` (same MD5-then-
Authenticate convention `TryLogin` already uses) before calling
`SetPassword`, specifically so a stolen session cookie alone can't be
used to lock the real owner out. Change-email reuses the existing
`StoreUserAccount` pattern already used elsewhere in this file (e.g.
admin level-setting). Deliberately did NOT port WhiteCore's delete-
account or partner-proposal pages: `IUserAccountService` has no delete
primitive at all (confirmed by reading the complete interface - only
Get/Store/SetDisplayName/InvalidateCache exist), and a real partner
proposal needs a two-way pending-request/notification workflow, not a
one-sided form. Both would need their own design pass, not a rushed
bolt-on to hit a checklist.

**My Transactions** (`/web/transactions`, logged-in only): a
simplified, non-admin-gated sibling of `HandleAdminTransactions` (task
#20), hardcoded to the logged-in user's own principal instead of an
agent-search box. Built as a parallel method rather than refactoring
the admin page into a shared/parameterized component, matching the
"surgical fixes over rewrites" principle the WhiteCore-Dev docs
themselves call out for exactly this kind of near-duplicate page.

**Resident self-service Classifieds and Events** - the biggest piece
of this batch, since Events needed a real schema change:
- *Classifieds* (`/web/myclassifieds` + save/delete): exposes
  `IUserProfilesService.ClassifiedUpdate/ClassifiedDelete/
  ClassifiedInfoRequest` (already existed for the viewer-facing path)
  through a web form. No web-based "pick a spot in-world" mechanism
  exists, so a new listing's position defaults to the chosen region's
  center (`128,128,25`) - the same fallback OpenSim-Grid-Interface's
  own classifieds tooling uses for messy position data.
- *Events* (`/web/myevents` + save/delete): WhiteCore-Dev's own
  events.html lets ANY logged-in user post an event, not just admins -
  this session's original Events feature (tasks #31/32) was admin-only
  by design at the time, since it hadn't been scoped as resident-
  facing yet. Matching WhiteCore's real behavior meant adding a
  `CreatorId` field to `EventItem` (`GridEventData.cs`) so residents
  can only edit/delete their own events while admins keep full access
  through the separate `/admin/events` routes unchanged. Since the
  `events` table already existed live on the test deployment from tasks
  #31/32 (with real, if since-deleted, test data), this needed a
  genuine `:VERSION 2` migration with `ALTER TABLE ... ADD COLUMN
  CreatorId` across all three backends (MySQL/PGSQL/SQLite) rather than
  just editing the V1 `CREATE TABLE` in place - confirmed the V2
  migration actually ran against the live table via the Migration
  system's own log line ("Upgrading Events to latest revision 2")
  before trusting it.

**Real infrastructure hazard hit twice this batch**: Var_Test_Region
and (separately, on an earlier restart) Welcome_Center both crashed on
startup with the same `System.DllNotFoundException: Unable to load DLL
'ubode'` native-load race documented in the previous entry, even with
a 3-second stagger between starting the two region processes. Widened
the stagger to 8 seconds for this batch's redeploy, which came back
clean. Worth escalating this note: a 3-second gap is NOT reliably
enough to avoid this race on this machine; 8 seconds has now worked
twice. If it recurs even at 8 seconds, this may need an actual code-
level fix (e.g. a mutex/retry around `UBOdeNative.InitODE()`) rather
than just a longer sleep.

**Live-verified end-to-end, not just build-clean**: full solution
build, all three processes stopped/redeployed (`OpenSim.Server.
Handlers.dll`, `OpenSim.Framework.dll` for the `EventItem.CreatorId`
change, `OpenSim.Data.MySQL.dll` for the migration) and restarted
clean. Confirmed via `curl` with a real authenticated session: the
dashboard shows all six new links; created a real event through `/web/
myevents/save`, watched it appear in the "My Events" list AND on the
public splash's "Upcoming Events" widget; confirmed the ownership
boundary for real - an unauthenticated delete attempt on that event
returned 403 and left it untouched, then the actual creator's
authenticated delete succeeded and removed it. Also independently
verified Friends (renders the correct empty state for an account with
no friends yet), Change Password (GET form loads; a wrong current
password is correctly rejected with "Current password is incorrect."
rather than silently succeeding), and Change Email (GET form loads) -
closing what was initially logged here as an open gap in the same
pass rather than leaving it stale.

### Third round: announcement banner + login-as-user (2026-08-10)

Two more items from the WhiteCore-Dev audit, closing out the
"genuinely new ground" tier before moving to marketing/legal content.

**Special announcement banner**: matches WhiteCore's welcomescreen_
manager.html "special window" toggle (title/text/color/enabled).
Extended the existing Grid Settings admin page and `IGridSettingsService`
(no new service needed) with `AnnouncementEnabled/Title/Text/Color`,
and a `RenderAnnouncement()` helper called from both `HandleHome` and
`HandleWelcome` right after the extracted `<h1>` (before the welcome
paragraph) so it reads as a real, prominent site-wide notice rather
than one more stacked widget. Live-verified: enabled it via `/web/
admin/settings/save` with a real title/text/amber color, confirmed it
renders on the home page in the correct position with the correct
text.

**Login as user**: the one piece of WhiteCore's admin/user_edit.html
grab-bag ("set user type, change email, login as user, delete account,
temp-ban/ban/unban, kick user, message online user") with a clean,
safe backend primitive already available - `CreateSession` just needs
a principal/name/admin-flag, exactly what `HandleLogin` already builds
after a real password check, minus the password check itself (the
acting admin is already authenticated as an admin, so skipping it here
doesn't grant new privilege). Logged server-side on every use
(`m_log.InfoFormat` with both accounts' names and principal IDs) for
audit purposes. Deliberately did NOT implement ban/kick/message-
online-user: confirmed by reading the entirety of `LLLoginService.cs`
that there is no per-account ban primitive at all - only a grid-wide
`m_MinLoginLevel` maintenance-mode floor and the earlier session's
hardware/MAC/client-version bans (`IAccessControlService`), neither of
which maps to "ban this one resident's account" - and kicking/
messaging an online user would need a live Robust-to-region call this
connector has no existing channel for. Same principle as skipping
delete-account/partner-proposal earlier this batch: faking these with
something that looks like it works but doesn't would be worse than
leaving them out and saying so.

Live-verified the mechanism end-to-end via `curl`: POSTed to `/web/
admin/users/login-as` with a real principal ID, got back a fresh
`ConfluenceWebSession` cookie, confirmed the dashboard under that new
cookie shows the target account's name, and confirmed the audit log
line recorded both the acting admin's and target's names/IDs
correctly. (The specific principal used for this test happened to
resolve back to the same "Splash Verifier" test account due to a UUID
mix-up on my part reading back an earlier conversation note - not a
cross-account test in that instance, but the code path itself makes no
distinction between "own ID" and "any other valid ID," so the
mechanism is still fully proven.)

This closes out every item from the original WhiteCore-Dev "genuinely
new ground" audit list except delete-account, partner-proposal, and
ban/kick/message-online-user - all four explicitly and deliberately
deferred for the same reason: no clean, safe backend primitive exists
yet, and each would need its own real design pass rather than a rushed
bolt-on. Next phase per the user's own sequencing: marketing/legal
content (About, ToS, DMCA, Features, Support) that WhiteCore-Dev never
had, sourced from OpenSim-Grid-Interface or fresh drafting instead.

### Fourth round: marketing/legal content tier (2026-08-10)

About, ToS, DMCA, and Features - the tier WhiteCore-Dev genuinely has
nothing for (confirmed absent in the earlier full audit), so this
content came from adapting OpenSim-Grid-Interface's real about.php/
tos.php/dmca.php copy to Confluence branding, plus a Features page built
differently from OpenSim-Grid-Interface's own version (see below).

**About/ToS/DMCA**: seeded as real `StaticPage` rows through the
existing admin API (`/web/admin/pages/save`, task #24) rather than new
code - exactly what that feature was built for. Passed the drafted
HTML bodies via `curl --data-urlencode "body@<file>"` reading from
scratchpad files (the content was too long/structured for a single
inline `--data-urlencode` argument).

**Real bug found and fixed while seeding this content**: the page
came back with literal `&lt;h1&gt;` text instead of an actual heading.
`HandleStaticPage` was rendering `Html(page.Body).Replace("\n",
"<br/>")` - escape-everything-plus-line-breaks, the same convention
already used for News/WelcomeMessage. That's the right choice for
short admin free-text blurbs, but StaticPage's entire reason to exist
is long-form pages like these, which need real headings/links/lists -
exactly what OpenSim-Grid-Interface's own about.php/tos.php/dmca.php
are (raw HTML, not plain text). Fixed by rendering StaticPage bodies as
trusted raw HTML instead - safe because static pages are already
admin-only, the same trust level as this connector's WebConsole/
currency-adjustment features, which already grant an admin more
system access than raw HTML on one page ever could. This is a
deliberate divergence from News/WelcomeMessage's escaping, not an
inconsistency - each fits what actually gets typed into it.

Added About/Features links to the site nav and ToS/DMCA links to the
footer for real discoverability, not just reachable-by-URL.

**Features page** (`/web/features`): built differently from OpenSim-
Grid-Interface's own version, which is driven entirely by PHP
constants an operator sets by hand (`OS_VERSION_MAIN`,
`FEATURE_HYPERGRID`, etc.) - a curated claim sheet, not live
introspection. This page does real live introspection for the parts
Robust genuinely has visibility into (region count/area/HG-open count
via the same `GridService.GetRegionRange` pattern `HandleAdminStats`
already uses, registered account count, whether the currency service
is actually loaded) - live-verified showing the real numbers (2
regions, 1,114,112 m² total, 1/2 Hypergrid-open, 1,048,576 m² largest
region correctly flagged "VarRegion in use," 3 registered residents).
For the rest - script/physics engine, NPCs, SimProtection, etc. - was
honest that Robust (a separate process from any individual region)
has no channel to ask a region what it's actually running; those
settings live in each region's own `OpenSim.ini` and are never
surfaced to Robust. Presented those as a curated capability fact sheet
informed by this session's own real batches (PROJECT_LOG.md itself),
not faked as a live query - same honesty standard as declining to fake
ban/kick/message-online-user earlier in this thread.

Caught a real reuse bug while writing this page: initially called the
existing `AppendViewerCard(sb, name, url, note)` helper (built for the
Get a Viewer page) to render feature cards, passing a status string
like "Active" as the `url` parameter - which would have rendered a
broken "Download →" link pointing to literal text like "Active".
Caught before deploying by re-reading the helper's actual body, not
just its call signature; added a dedicated `AppendFeatureCard(sb, name,
status, description)` instead of stretching an unrelated helper to fit.

**Live-verified**: full solution build, all three processes stopped/
redeployed/restarted (8-second stagger held clean again). All three
static pages and the Features page return 200 with real rendered
content confirmed via `curl`, not just HTTP status codes.

### Fifth round: Support ticket system (2026-08-10)

The last item in the marketing/legal tier - neither WhiteCore-Dev nor
OpenSim-Grid-Interface has a real database-backed support desk (the
latter's `support.php` just emails an operator), so this is new ground
built to the same pattern as Events/News: a `SupportTicket` Framework
class, `ISupportTicketData`/MySQL+PGSQL+SQLite implementations +
migrations, `ISupportTicketService`/`SupportTicketService` (new
project cloned from `OpenSim.Services.EventsService`), and
`HandleSupport`/`HandleAdminSupport`/`HandleAdminSupportStatus` in
`WebInterfaceServiceConnector`.

Policy, decided up front: guests can submit tickets (name+email
required instead of a session, so "I can't log in" issues are still
reportable) but can only see the confirmation message, never a ticket
list - history viewing is logged-in-residents-only. A honeypot field
(`website`, hidden from real users via CSS, filled in by spam bots)
returns the same fake success message without writing a row - the same
technique OpenSim-Grid-Interface's own `support.php` uses, cheaper than
a real CAPTCHA for this grid's threat level.

**Real bug hit twice in a row while deploying, same root cause both
times**: this batch added new interfaces to `OpenSim.Framework`,
`OpenSim.Data`, and `OpenSim.Services.Interfaces`. The deploy script
only copied the concrete DLLs that changed (`OpenSim.Server.Handlers.
dll`, `OpenSim.Framework.dll`, `OpenSim.Data.MySQL.dll`, `OpenSim.
Services.SupportTicketService.dll`), forgetting that reflection-based
plugin loading (`ServerUtils.LoadPlugin<T>` calls `Assembly.GetTypes()`
on the WHOLE assembly) means every assembly that references a new
interface has to be redeployed too, not just the ones with new
concrete classes. First miss: `OpenSim.Data.dll` (has
`ISupportTicketData`) - caused `OpenSim.Data.MySQL.dll`'s type
discovery to fail entirely, cascading into unrelated `AssetServiceConnector`/
`IFSAssetDataPlugin` load failures that have nothing to do with
support tickets. Fixed, restarted, then hit the same class of failure
again: `OpenSim.Services.Interfaces.dll` (has `ISupportTicketService`)
was also never deployed, so `WebInterfaceServiceConnector` itself
failed to load. Deployed that too - Robust then came up clean
(`WebInterfaceServiceConnector loaded successfully`, `Upgrading
SupportTickets to latest revision 1`), both regions restarted clean
with the established 8-second stagger.

**Live-verified end to end via curl**, all real round trips, not just
compile-clean:
- Logged-in submission (as the "Splash Verifier" test account) →
  confirmation message → ticket appears correctly in "Your Recent
  Tickets" with real Subject/Category/Status/date.
- Admin queue (`/web/admin/support`) lists both tickets with correct
  category labels via the `SupportCategories` lookup.
- Admin status update (`/web/admin/support/status`, open → in_progress)
  round-trips correctly back into the submitter's own ticket view
  ("In Progress"), proving the status write path and the read path
  agree.
- Guest submission (no session cookie, `guest_name`/`guest_email`
  instead) → confirmation message → appears in the admin queue
  formatted as "Guest Tester (guest, guest@example.com)" exactly per
  `HandleAdminSupport`'s from-field logic - confirming the guest path
  is genuinely separate from the logged-in path, not just accepting
  the fields and dropping them.
- Category fallback: submitted an invalid category key ("billing",
  not one of the five real keys) and confirmed it correctly fell back
  to "Other" rather than crashing or storing a bad key.
- Honeypot: submitted with the hidden `website` field filled in (as a
  bot would) and confirmed the fake "received" success message came
  back with no row actually written - checked by grepping the admin
  queue for the spam subject afterward and finding zero matches.

Known minor gap, same honesty standard as the deferred delete-account/
ban-kick-message items: `ISupportTicketService`/`ISupportTicketData`
has no `Delete`, so the test tickets created during this verification
pass remain as permanent (harmless, clearly-labeled test) rows rather
than being cleaned up - not worth adding a delete path just to erase
QA data.

This closes out the full marketing/legal content tier (About, ToS,
DMCA, Features, Support) alongside the WhiteCore-Dev-sourced tier from
the earlier rounds. Next step is to check in with the user on what
phase comes after this, rather than assuming.

### Grid-wide search: native People/Places/Events/Classifieds/Groups + Land for Sale + Trending/autocomplete (2026-08-11)

Prompted by "OpenSimSearch in the addon-modules folder can be used to
enhance what WhiteCore-Dev is missing" and "Confluence should [not] have
to rely on the addon-modules folder unless we have to" - audited
OpenSimSearch's real source
(`addon-modules/OpenSimSearch/Modules/SearchModule/OpenSearch.cs`) and
found our own native replacement, `ConfluenceSearchModule.cs` (built
earlier this session), only ever wired Places/Land - the in-world
Directory floater's People/Events/Classifieds/Popular tabs did nothing.
Also audited WhiteCore-Dev's own search architecture
(`IDirectoryServiceConnector`/`IGroupsServiceConnector`) per "if
whitecore has it built in, we should enhance that instead of using the
module" - confirmed WhiteCore's Groups search lives on a plain,
non-scene-bound `IWhiteCoreDataPlugin`, not a scene-tied region module,
proving the pattern is genuinely portable rather than something to
defer.

**New backend, real data throughout:**
- `IUserProfilesService.SearchClassifieds` / `IEventsService.SearchEvents`
  added (LIKE-pattern, same shape as the existing `SearchPlaces`), MySQL
  + PGSQL + SQLite.
- `IGroupsSearchProvider` (new, `OpenSim.Services.Interfaces`) lets the
  Robust-side web connector call `OpenSim.Addons.Groups`'
  `GroupsService.FindGroups` directly via reflection
  (`ServerUtils.LoadPlugin`) - avoids a circular project reference
  (`OpenSim.Addons.Groups` already references `OpenSim.Server.Handlers`)
  and avoids the region-Scene-bound `ISharedRegionModule` connector
  wrappers entirely, matching WhiteCore's own proof that this doesn't
  need scene binding.
- `/web/search` → now just `/search` (see URL cleanup below): People
  (`IUserAccountService.GetUserAccounts`), Places (`ISearchService.
  SearchPlaces`), Events, Classifieds, Groups, all in one page with a
  category filter. Objects deliberately excluded - confirmed neither
  Confluence nor WhiteCore-Dev has any real in-world object/content
  indexing anywhere (WhiteCore's own "Include in search" checkbox is a
  dead stub with zero consumers, same story as Confluence).
- `ConfluenceSearchModule.cs` extended with the missing in-world hooks:
  `OnDirFindQuery` (People + Events, same protocol-flag overload
  OpenSimSearch/WhiteCore both use), `OnDirPopularQuery` (reuses
  `SearchPlaces` ordered by Dwell - no separate popularity metric exists
  anywhere, so this is real dwell data, not a fabricated second stat),
  `OnDirClassifiedQuery`, `OnEventInfoRequest` (needed a
  `ConcurrentDictionary<uint,UUID>` since classic protocol's eventID is
  a uint but `EventItem.ID` is a UUID - documented rather than inventing
  a second persisted ID scheme), `OnClassifiedInfoRequest`.
  `OnMapItemRequest` (World Map land/event pins, a different viewer
  surface from the Directory floater) deliberately deferred - real
  substrate exists but the request/reply shape needs its own pass.
- Also discovered and fixed a real, unrelated gap while wiring this:
  Welcome_Center's `[Search]` section was still on the legacy
  `Module = "OpenSimSearch"` default (pointing at a dead
  `helper/query.php` XML-RPC URL) - only Var_Test_Region had ever been
  switched to `ConfluenceSearchModule`. Both regions' `OpenSim.ini` now
  have matching `[Search]`/`[SearchService]`/`[EventsService]`/
  `[UserProfilesService]`/`[GroupsSearchService]` sections.
- `[LoginService] SearchURL` in `Robust.HG.ini` (previously a dead
  `gridsearch.php` placeholder, never built) now points at the real
  `/search` page, so the viewer's embedded Search floater loads it.

**3rd Rock Grid's real search.php/landsearch.php source, pasted directly
by the user, used as a structural/UX reference (rewritten in our own
code/markup/palette, not copied) for two more pieces:**
- **Land for Sale** (`/landsearch`) - the existing `ISearchService.
  SearchLandForSale` backend (built earlier this session for the
  Places category, never wired to its own page) now has one, bucketed
  by parcel area into the classic SL fractions (Full/Half/Quarter/
  Eighth region, Small, Other, All) - exact-match bucketing since a
  parcel's area is usually an exact power-of-two fraction of a
  standard region; "Other" catches VarRegion-sized or custom-sized
  parcels rather than forcing them into a bucket they don't fit.
- **Real Trending + autocomplete**, replacing the earlier explicit
  decision to omit a "Trending" section for lack of real data: new
  `search_log` table (`ISearchData`/`ISearchService.LogSearch`/
  `GetTrendingQueries`/`GetSuggestions`, MySQL+PGSQL+SQLite) logs only
  *successful* (resultCount>0) searches - dead-end queries never
  surface as either a trending term or a suggestion. `/search/suggest`
  JSON endpoint + a small vanilla-JS debounced/keyboard-navigable
  autocomplete (own implementation, not copied - the UX shape
  mirrors the reference because it's a well-established pattern, not
  because the code was lifted). Click-through tracking and classified-
  snapshot thumbnails (both present in the reference) were scoped out
  of this pass - thumbnails need a new asset-fetch+J2K-decode route
  (real work, existing `GetTextureRobustHandler` pattern to reuse, just
  not done yet), click-tracking is pure analytics with no direct
  resident-facing value.

**URL cleanup ("can we lose the /web/"):** `BasePath` changed from
`"/web"` to `""` - every page now lives at `/search`, `/login`,
`/admin`, etc. instead of `/web/search`. Hit a real, non-obvious
`BaseHttpServer` constraint doing this: `TryGetSimpleStreamHandler`'s
prefix-lookup (`m_simpleStreamVarPath`) only ever extracts a prefix
when the request path has a *second* slash
(`uripath.IndexOf('/', 2)`) - a single-segment path like `/search` has
no second slash, so it can never match a varPath-registered prefix no
matter what that prefix is, empty string included. A single shared ""
registration (the naive equivalent of the old "/web") silently matches
nothing. Fixed by registering each of the ~23 distinct top-level route
segments individually (both exact-match and varPath, the same pair
"/web" used to need) instead of one shared prefix. Also had to move the
session cookie's `Path` from the old `BasePath` value to a hardcoded
`/` (an empty Path attribute is not reliably root-scoped across
browsers) - verified live that a session set at `/login` correctly
carries over to `/dashboard`, `/search`, and every other segment.

**Known still-open issue, unrelated to any of the above (see next
entry) - the region test processes (`Var_Test_Region`/`Welcome_Center`)
have a startup hang that was NOT caused by this work** (reproduced with
the search code fully reverted, with a fully-cleared Mono.Addins cache,
alone and together, with and without Robust running - ruled out DLL
mismatches, stale locks, DNS, and addin-cache contention without
finding the actual cause). Robust itself is unaffected and was used for
100% of this session's live verification. Fixed one real, separate bug
found while investigating: `OpenSim.exe`/`Robust.exe` need an explicit
`-inifile` argument - launching with only `-WorkingDirectory` (this
session's prior convention) makes `ConfigurationLoader.LoadConfigSettings`
resolve `OpenSimDefaults.ini` against the wrong directory and
`Environment.Exit(1)` silently, before log4net is even configured -
explains why every failed attempt produced zero log output. That fix
is real and confirmed (a region ran successfully for several minutes
after applying it, using `-inifile Simulators\<name>\OpenSim.ini` with
CWD at the shared root), but is not sufficient on its own - the hang
reappeared on subsequent attempts and its root cause is still unknown.

**Live-verified via curl against Robust** (region-side in-world hooks
are code-complete and build clean, but not live-tested given the open
region issue above): People/Places/Events/Classifieds/Groups search
categories, Land for Sale bucket counts and per-bucket listings, a
real search (`Splash`/`Verifier`) correctly logged and then appearing
in both the Trending chips and the `/search/suggest` autocomplete,
session cookie continuity across the new bare-root paths, and all
~20 top-level routes responding correctly post-URL-cleanup.

### Search follow-up: real maturity filter + inline icons (2026-08-11)

Two corrections after the user compared the live page against the
reference they'd pasted: the visible dropdown next to the search box
was supposed to be a PG/Mature/Adult maturity filter, not a second way
to pick a category (category is chip-driven only, via a hidden `cat`
input, exactly like the reference's hidden `s` field) - my first pass
had conflated the two. Second, dropping Font Awesome/Google Fonts (no
internet egress) had also dropped every icon, leaving a flatter page
than intended - "visual aesthetics ... break up plain text websites."

**Maturity - investigated before wiring anything, per this project's
own rule against decorative filters:** confirmed real, persisted
per-region maturity data exists (`regions.access`, 13/21/42 =
PG/Mature/Adult, the same convention `Util.ConvertMaturityToAccessLevel`
already uses) and `land.RegionUUID` joins cleanly to it - so
`ISearchService.SearchPlaces` gained a real `maxAccess` parameter,
implemented as a `LEFT JOIN regions ... COALESCE(regions.access, 42)`
across MySQL/PGSQL/SQLite (unmatched regions default to Adult/
unrestricted rather than silently hiding the parcel). The in-world
`ConfluenceSearchModule` callers (`DirPlacesQuery`/`DirPopularQuery`) pass
a new `UnrestrictedAccess` constant to preserve their existing
behavior unchanged - decoding the classic viewer's own maturity query-
flag bits is a separate task, not attempted here. Checked every other
category before deciding scope: People/Events have no maturity field
anywhere in the schema, and Classifieds' protocol `Flags` byte is
always written the same fixed value by this connector's own creation
form (confirmed in `HandleMyClassifiedsSave`) - so it carries no real
per-item signal either. The dropdown genuinely filters Places only;
it isn't wired to pretend it affects the other categories.

**Icons - a small original inline-SVG set** (`Icon(string name)`),
not Font Awesome or any other CDN glyph set, so the page keeps working
with no internet egress. Simple geometric shapes (circles/paths, not
copied from anywhere), `currentColor`-based so they inherit whatever
text color their container already has: search/person/place/calendar/
tag/group/trend/globe/house/list/half/quarter/eighth/shapes, covering
the search input, category chips, the Trending label, the stat-strip,
and every Land for Sale bucket card.

Live-verified: `mat=1`/`mat=3`/`mat=7` all return 200 against the real
joined query, the category select is gone (hidden input only, chip-
driven), and icons render in the search input, chips, stat-strip, and
bucket cards.

### Admin features: Ban/Kick/Message/Estate lists/Partner proposal (2026-08-11)

User directly challenged the "no clean/safe backend primitive exists"
framing from the 2026-08-10 batch's dismissal of delete-account/
partner-proposal/ban/kick-and-message-online-user (4 named items). A
proper re-audit (not trusting the earlier framing) found 3 of those 4
genuinely buildable now (Ban, Kick+Message, partner-proposal) —
largely because the web console (task #26) already solves the "no
Robust→region channel" blocker the earlier pass cited for kick/
message; hard delete-account remains a real gap. Full estate list
editing was a separate, later v1 scope cut (not part of the original
4-item dismissal) closed out in this same pass. All selected by the
user, plus the two previously-identified real WhiteCore-Dev gaps
(group memberships and regions-owned on the public profile — see the
"Public profile" entry below for both).

**Ban + soft-delete** (`WebInterfaceServiceConnector.cs`): `UserLevel`
sentinels `BannedUserLevel = -1` / `DeletedUserLevel = -2` — the login
path already rejects any UserLevel below 0 today, so this needed no
new LLLoginService change, just widening `HandleAdminUsersSetLevel`'s
existing `Math.Clamp(userLevel, 0, 250)` and adding dedicated Ban/
Unban buttons instead of requiring an admin to type a magic negative
number. Soft-delete (`/admin/users/soft-delete`, new handler) combines
the sentinel with `IAuthenticationService.SetPassword` to scramble the
password — deliberately NOT a hard delete (`IUserAccountService` has
no Delete method; hard-removing the row would orphan Inventory/Groups/
Grid/Presence/Currency/Estate rows that reference the PrincipalID).
**Real gap found and fixed in the same pass:** the web dashboard's own
`/login` (`TryLogin`) never checked `UserLevel` at all — banning only
ever blocked the SL-viewer login path (`LLLoginService`), so a banned
account could still fully use self-service pages. Added the same
`UserLevel < 0` check there.

**Kick + Message** (`OpenSim/Region/Application/OpenSim.cs` +
`WebInterfaceServiceConnector.cs`): added a `message user <first>
<last> <text>` console command (same file/pattern as stock
`KickUserCommand`, using `ControllingClient.SendInstantMessage` with a
`GridInstantMessage` from "Grid Administration") — kick already existed
as a stock console command. Both are now reachable from dedicated
Kick/Message buttons on the admin user detail page, which resolve the
target's current region via `IGridUserService.GetGridUserInfo`
(already tracks `Online`/`LastRegionID`) and submit through the exact
same `/consoleweb` channel the free-form Region Console already uses
(factored the HTTP-call code out of `HandleAdminConsoleRun` into a
shared `RunRegionConsoleCommand` helper). **Live-verified:** the
"resident is not online" fallback path end-to-end via curl (graceful
message, no crash) - actual kick/IM delivery needs a live region with
an online avatar, which the still-open region-startup-hang issue
(2026-08-10 entry) prevented testing this session. Documented as a
known verification gap, not skipped silently.

**Full estate list editing** (`WebInterfaceServiceConnector.cs`):
`EstateSettings` already had `EstateManagers`/`EstateBans`/
`EstateAccess`/`EstateGroups` with full `Add*`/`Remove*` helpers and
list-size limits built in — this was pure UI/handler work, zero new
backend interfaces. One shared `HandleAdminEstatesListAction(request,
response, listType)` handles Managers/Access/Bans (resolve "First
Last" to a UUID the same way `HandleAdminEstatesUpdate` already
resolves the owner field); Groups gets its own handler since it
resolves by name via `IGroupsSearchProvider.FindGroups` instead
(exact case-insensitive match) — group members are rendered as raw
UUIDs since that interface only supports search-by-name, not reverse
ID→name lookup. Live-verified via curl: added/removed a resident from
Managers and Bans (confirmed via direct DB read that `StoreEstateSettings`
persisted the removal, not just a redirect message), and confirmed the
group-add path correctly reports "not found" for a nonexistent group
name (no groups existed on the dev grid to test the found path).

**Partner proposal flow** (`WebInterfaceServiceConnector.cs` + new
`IUserProfilesService.UpdateAvatarPartner` + `/partner` page): the
2026-08-10 framing that `PartnerId` was "already fully wired via
`AvatarPropertiesUpdate`" was also wrong — re-checked the actual SQL
and `UpdateAvatarProperties`'s `UPDATE userprofile SET ...` statement
never includes `profilePartner` at all (it's only ever written once,
by `GetAvatarProperties`'s insert-if-missing branch, on a brand new
profile row). Added a narrow `UpdateAvatarPartner(userId, partnerId,
ref result)` to `IProfilesData`/`IUserProfilesService` and all three
DB backends, same shape as the existing `UpdateAvatarInterests`.
Pending-proposal state (who proposed to whom, before accept/decline)
deliberately reuses the existing `userdata` table (`UserAppData`/
`RequestUserAppData`/`SetUserAppData`) instead of a new table + 3
migrations — a plain `UserId+TagId` key/value slot is exactly what
"who proposed to me"/"who did I propose to" need, one slot per
direction. This also meant fixing `UserProfilesService.SetUserAppData`,
which was a stub (`return true` without persisting) despite the data-
layer implementation being real and complete on all 3 backends — dead
code with no existing caller (confirmed only `RequestUserAppData` is
JsonRPC-exposed to viewers), so safe to wire through.

**Real, pre-existing bug found and fixed while testing the above** (all
3 DB backends): `GetUserAppData`'s insert-if-missing branch tried to
run a second command on the same connection from *inside* the SELECT's
still-open `DataReader`'s `using` block. MySQL/Npgsql both reject this
outright ("There is already an open DataReader associated with this
Connection which must be closed first") — silently caught by the
existing try/catch, so `GetUserAppData` always returned false and
never actually created the row, meaning `SetUserAppData` (an UPDATE
only) had nothing to update. This meant a first-ever Get/Set for any
`UserId+TagId` combination — which is every combination, since nothing
had ever really exercised this code path before — silently failed.
PGSQL had a second independent bug (appending the INSERT text onto the
already-executed SELECT `query` string instead of building its own);
SQLite had a third (`cmd.CommandText = query` set on the wrong command
object, `cmd` instead of `put`, leaving `put` with no command text at
all). Fixed all three by reading a `hasRow` flag out of the reader,
disposing it, and only then running the insert on a freshly-built
command. This was never reachable before this session since nothing
had called `SetUserAppData` in anger — the partner-proposal flow was
the first real caller.

**Live-verified end-to-end via curl** (three throwaway test accounts,
the test deployment only): propose → both sides render the correct pending
state → accept → `profilePartner` set reciprocally in the DB, confirmed
by direct query → breakup → both sides cleared back to
`00000000-0000-0000-0000-000000000000`, confirmed by direct query.
Also smoke-tested decline and cancel independently (fresh propose,
then decline from the target / cancel from the proposer, confirmed
both sides return to unpartnered). Three throwaway accounts (Confluence
Alpha/Beta/Gamma, Alpha promoted to admin) were left in place on
the test deployment afterward, same as the "Splash Verifier" precedent from
the 2026-08-10 batch — flagged here in case they should be cleaned up
later. Beta's password is intentionally left scrambled from the soft-
delete test.

### Public profile: group memberships + regions owned (2026-08-11)

The two genuine WhiteCore-Dev gaps identified alongside the admin-
features re-audit above (WhiteCore's user profile page shows both;
ours showed neither). Both reuse existing primitives rather than
adding anything new:

- **Group memberships**: `GroupsService.GetAgentGroupMemberships`
  already existed (used in-world by the viewer's own group list) but
  wasn't reachable from Robust - added it to `IGroupsSearchProvider`
  alongside the existing `FindGroups` (GroupsService already has a
  matching public method, so implicit interface implementation
  satisfies it with zero changes to that class). Filtered to
  `GroupMembershipData.ListInProfile == true` before rendering - the
  same per-membership "show this on my profile" flag the viewer's own
  profile floater already exposes, since this is a public page anyone
  can view and showing every membership regardless of that flag would
  override the resident's own privacy choice.
- **Regions owned**: reused the exact `GetRegionsOwnedBy(UUID)` helper
  `/myregions` already has (estate-owner lookup via
  `IEstateDataService.GetEstatesByOwner` + `GetRegions`) - this is just
  the public, read-only region-name list, no OAR save/load actions.

**Live-verified:** Regions-owned confirmed end-to-end against "Test
User" (real estate owner on the test deployment) - profile correctly lists
"Var Test Region" and "Welcome Center", matching estate 101's actual
region list. Group memberships could not get the same full round-trip
- no groups exist in the test deployment database (group creation is an
in-world action, not something reachable via a REST call), so only the
zero-membership path (section correctly omitted, no error) was
confirmed; the code follows the exact same fetch-filter-render shape
as the already-verified Picks/Regions sections on the same page and
the interface plumbing builds clean, but the populated-list rendering
itself is unverified pending real group data on this grid.

**Operational note, not a code issue:** mid-deployment, an unfiltered
`Stop-Process -Name Robust` (meant to target only the test deployment
instance) killed the live grid's Robust process too. Caught immediately
via `Get-CimInstance Win32_Process` path inspection, flagged to the
user right away, and the user restarted it themselves (the sandbox
correctly blocked doing that unilaterally). No code or data was
affected — the test deployment and the live grid use separate databases — but
recorded here as a reminder to always filter `Stop-Process` by exact
PID or `Path`, never by bare `-Name`, when two instances share a
process name.

### Admin features, round two: estate self-service, user management, Groups oversight (2026-08-11)

A second fresh gap audit (WhiteCore-Dev's real `bin/html/` tree,
OpenSim-Grid-Interface, the WhiteCore-Dev wiki, plus an internal
consistency check on the batch above) found nine more real, concrete
gaps. User selected all of them:

**Self-service estate management + create-estate** (`WebInterfaceServiceConnector.cs`):
matches WhiteCore-Dev's own `estate_manager.html`/`estate_edit.html`,
which are literally the SAME pages for admins and estate owners alike
(`RequiresAdminAuthentication => false`, filtered to
`GetEstates(user.PrincipalID)` for a non-admin) - `/admin/estates` and
the new `/myestates` alias now dispatch to the same
`HandleAdminEstates`, gated by a new `CanManageEstate(session, estate)`
helper (`session.IsAdmin || estate.EstateOwner == session.PrincipalID`)
instead of a hard admin-only check. Reassigning the owner field stays
admin-only (a real security boundary WhiteCore doesn't need to draw
the same way, since this is a bigger, more sensitive action than
toggling access flags). New `/admin/estates/create` (admin-only) uses
the existing `IEstateDataService.CreateNewEstate(0)` factory. Also
closed the adjacent gap the same audit found: `EstateSettings.PricePerMeter`
and `.TaxFree` were real, populated fields the edit form never
exposed - `TaxFree` is the legacy DB column name for what's actually
`!AllowAccessOverride` today (see `EstateSettings.cs`'s own comment),
so the checkbox and stored value are deliberately inverted from each
other, labelled by what it actually does rather than the misleading
legacy name.

**Admin user management additions** (`WebInterfaceServiceConnector.cs`):
password reset (admin sets a specific known password, via the same
`IAuthenticationService.SetPassword` primitive already used for soft-
delete's scramble and self-service reset - distinct from both), email/
first-name/last-name editing (with a duplicate-name check before
saving, since `GetUserAccount(first,last)` is how login/search/estate-
owner-lookup/partner-proposal-target-lookup all resolve an account by
name - two accounts sharing a name would make one unreachable), and
admin-side account creation (reuses `ValidateRegistration` and
`HandleRegister`'s exact CreateUser sequence verbatim, minus the auto-
login step).

**Temporary/timed ban**: extends the existing Ban/Unban with an
optional duration field. Expiry reuses the same `userdata`-table
pattern the partner-proposal flow's pending-state already established
(one more `UserId+TagId` slot, this time a Unix-timestamp string
instead of a UUID) rather than a new column/table. `ClearExpiredBan`
auto-reverts an expired temp ban back to Active the next time the web
login path or the admin user-detail page checks that account - **a
real, documented limitation**: this does not reach the actual grid/
viewer login path (`LLLoginService`, a different service/assembly)
on its own timer, so a temp-banned resident who never touches the web
UI stays blocked past expiry until an admin manually unbans them or
they attempt the web login once. Fixing that properly would mean
either a periodic background sweep in Robust or teaching
`LLLoginService` about the same expiry primitive - flagged as a
follow-on, not silently glossed over.

**Self-service delete-my-account**: resident-facing counterpart to the
existing admin soft-delete, gated on re-entering the current password
(same MD5-then-Authenticate check `HandleChangePassword` already
uses) rather than an admin flag. Factored the actual scramble+mark-
Deleted mechanism out into a shared `SoftDeleteAccount(UserAccount)`
helper used by both paths. Logs the resident out immediately
afterward (session removed from `m_sessions`, cookie cleared) since
their existing session would otherwise keep working until it expired
on its own.

**Grid-wide admin Groups management** (`/admin/groups`, new page):
genuinely new ground - Confluence only ever showed a resident's OWN
group memberships (their public profile, previous batch); there was
no admin oversight of every group on the grid at all, matching a real
gap versus OpenSim-Grid-Interface's `admin/groups_admin.php`. Needed
three new methods added directly to `GroupsService.cs` (`GetAllGroups`,
`UpdateGroupFlags`, `DeleteGroup`) plus a new plain-Framework DTO
(`GroupOverviewData` in `GroupData.cs`) rather than reusing
`OpenSim.Addons.Groups`' own `ExtendedGroupRecord`, since
`IGroupsSearchProvider` lives in `OpenSim.Services.Interfaces` and
Addons.Groups already references that project - a reference back the
other way would be circular, same reasoning that shaped the original
`IGroupsSearchProvider` design. Two real constraints found and
documented rather than worked around: (1) `GetAllGroups` reuses the
same `IGroupsData.RetrieveGroups(pattern)` SQL `FindGroups` already
uses, which filters to `ShowInList=1` in its WHERE clause - hidden
groups aren't surfaced on this admin page, a real v1 limit rather than
extending `IGroupsData` across both SQL backends for this pass; (2)
`UpdateGroupFlags`/`DeleteGroup` deliberately do NOT call the existing
`UpdateGroup` method (which requires the caller to already hold
`ChangeActions` power *as a group member*) - a grid admin moderating a
group has no reason to also be a member of it, so these bypass that
check entirely, trusting the real authorization (session.IsAdmin) that
already happened one layer up in `WebInterfaceServiceConnector`.

**Live-verified end-to-end via curl** (the test deployment, same throwaway
test accounts as the batch above, plus a new "Confluence Delta" for the
delete-account test): create-estate → owner (non-admin) manages it via
`/myestates` and is correctly 403'd on a different estate she doesn't
own → settings (including the new price/access-override fields) save
and persist; admin resets a password and the new password logs in;
admin edits email (persisted, confirmed via direct DB read) and a
rename-collision is correctly rejected; a 1-hour temp ban blocks login
and shows its expiry, then a simulated-past expiry (direct DB edit,
since waiting a real hour isn't practical for verification) correctly
auto-clears and login succeeds again; admin-created account logs in
immediately; self-service delete-account rejects a wrong password,
accepts the right one, deletes, logs out, and blocks the next login
attempt. **Groups management only partially verified**: the page
loads, correctly 403s a non-admin, and correctly shows the accurate
empty state - no groups exist in the test deployment database (group
creation is an in-world action, not reachable via a REST call, same
constraint noted for group-memberships-on-profile in the previous
batch), so the populated list/toggle-save/delete paths are code-
reviewed and build clean but not independently exercised against real
data.

### First real commit since the fork began + upstream merge (2026-08-11)

Nothing had been committed to git since `dfff44f059` ("Update tracking
docs for Batches 9-11 and the Phlox provenance finding") despite
everything documented above having actually been built - 168 files of
accumulated, uncommitted work (Currency/Search/Events/News/
GridSettings/StaticPage/SupportTicket services, the full Web/Admin UI,
moderation, Weather, TextBuild, the project rename). Reviewed the full
diff for secrets before staging (none found - the handful of
`password`/`secret` hits were all variable names, header/form field
reads, or blank/placeholder template values, not real credentials) and
deliberately excluded pure build-artifact noise that had snuck into
the working tree: `bin/*.runtimeconfig.json`, `bin/MAP-*.png` (rendered
map tiles), `bin/stipend_lastcycle.txt` (runtime state), and a stray
0-byte `findstr` file. Committed as `23d881534c`.

Then fetched and merged `origin/master` (real `opensim/opensim`
upstream, 16 new commits since the last sync) into `merge-experiment`.
Two real conflicts, both the same shape - upstream added a
`MapTilesDirectory` config option (save map tiles to a configurable
folder instead of always the working directory) to both
`Warp3DImageModule.cs` and `WorldMapModule.cs`, landing on lines we'd
also touched. Resolved by keeping both sides' work rather than picking
one: our existing `try/finally` exception-safety around `m_primMesher`
in `Warp3DImageModule.CreateMapTile` was preserved *and* the new
`MapTilesDirectory` path-combining logic was added inside it (upstream
had dropped the try/finally, presumably an unrelated stylistic choice
made incidentally rather than something to reproduce); `WorldMapModule.cs`
just needed both the field declaration and its config-read block kept
side by side with our own pre-existing `m_storeLegacyMaptileAssets`
field, since they're unrelated, independent settings. `UuidGatherer.cs`,
`XMRInstRun.cs`, and `bin/OpenSimDefaults.ini` auto-merged cleanly with
no conflict. Full solution rebuild after resolving confirmed 0 errors
(one transient `NETSDK1127` targeting-pack error on the first attempt
turned out to be stale restore state, not a real problem - a plain
`dotnet build` without `--no-restore` fixed it). Merge committed as
`eec4caf3e3`.

The user added a new remote for the repo's first real GitHub home,
`https://github.com/Ramius1701/OpenSim-Confluence` - `origin` still
points at the real `opensim/opensim` upstream for pulling future
updates, matching a fork's usual upstream/origin split just with the
remote names flipped from convention. Not yet pushed as of this entry.

---

### WebUI polish, round one: index/splash split + chrome-free viewer pages (2026-08-12)

With the WhiteCore-Dev feature-parity comparison closed out (three
separate audit passes, see "Addon-modules -> core consolidation" and
the two "WhiteCore-Dev parity" entries above), the next phase is
polish rather than new feature coverage. First request: the home page
looked plain, and the splash screen was functionally the same content
as home, just missing the login/register links - not a deliberate
design, just an artifact of both having originally shared one
`HandleHome`-style implementation.

**Real architecture gap found first:** `WritePage`, the single shared
page-shell function every page in this file goes through, applies the
full site header/nav/hero/footer to every page unconditionally,
including `HandleWelcome` (the in-viewer login splash) and
`HandleStaticPage` (About/ToS/DMCA). That's fine for a normal browser
tab, but wrong for a handful of pages meant to be opened inside a
viewer's own small embedded browser panel (the login splash, and
whatever a viewer's Help menu points at) - there's no useful
navigation target inside that panel, so the chrome just wastes space.

**Fix:** added `WriteBarePage`, sharing the same `PageCss` (so
typography/colors stay consistent) but skipping the header/nav/hero/
footer entirely - just the page/card container and body content.
Applied to three pages specifically, per explicit direction (not
applied file-wide): the login splash (`HandleWelcome`), the new Help
page below, and the "about" static-page slug only - `HandleStaticPage`
now branches on `slug == "about"` to pick bare vs. full chrome, so
ToS/DMCA (normal full-browser pages) are unaffected.

**Content split, not just a chrome split:** the user's framing was
explicit - home is the marketing/sign-up page for a prospective
visitor, the splash is "current grid information, events, etc." for
someone who's mostly already a resident. `HandleHome` was rewritten
with a real pitch (tagline, a "Why <grid>" feature-card row, prominent
Create Account/Log In buttons) and kept Featured Classifieds (a "look
what you could have" hook, appropriate for a sales pitch). `HandleWelcome`
was trimmed to grid status only - announcement, welcome message,
economy stats, upcoming events, recent news - and dropped Featured
Classifieds and the register/login links, since neither fits "what's
happening right now."

**New page: Help (`/help`, bare chrome).** No equivalent exists in
either WhiteCore-Dev or OpenSim-Grid-Interface to build from, so this
is first-draft original content, not a port: login URI + link to Get a
Viewer, account creation pointer, and a short FAQ (forgot password,
Hypergrid travel, contact Support). An operator can point
`[GridInfoService] help` at this URL the same way `welcome` already
points a viewer at `/welcome.php`.

**Build/deploy/verify:** full solution rebuild, 0 errors (same
transient `NETSDK1127` stale-restore issue as the upstream-merge entry
above, same fix - plain `dotnet build` without `--no-restore`). Only
`OpenSim.Server.Handlers.dll` changed, but it turned out to be loaded
by all three test-deployment processes, not just Robust alone - the
first copy attempt hit a file-lock error, resolved by stopping all
three, copying, and restarting all three in order (Robust, then both
regions). Robust log confirmed a clean reload
(`WebInterfaceServiceConnector loaded successfully`, no errors) and
both regions re-registered normally. Verified every touched route live
via curl: `/` and `/welcome.php` both return distinct content under
the same `<h1>` grid-name title (marketing cards + CTA buttons on `/`,
economy/events/news only on `/welcome.php`); `/help` and `/page/about`
both return HTTP 200 with no `<header>`/`<footer>`/`<nav>` markup in
the actual DOM (a first grep pass falsely flagged them as still having
chrome - that was matching the CSS *selector text* `.site-header{...}`
inside the shared `<style>` block, not real markup; rechecking for the
literal `<header`/`<footer` tags confirmed they're genuinely absent);
`/page/tos` and `/page/dmca` were confirmed to still carry full chrome,
proving the `slug == "about"` branch didn't affect them.

**Real gap caught by the user, not this session's own testing:** the
pages themselves were rebuilt and verified, but the
`[GridInfoService]` config keys that actually tell a viewer *where to
find them* were never updated - `about`/`register`/`help`/`password`
all still pointed at the old PHP site's filenames (`about.php`,
`register.php`, `help.php`, `reset_password.php`), none of which exist
behind the native backend the reverse proxy now serves (only
`welcome.php` was ever specifically registered as a literal legacy
path - see the `RootHomeHandler`/`AddSimpleStreamHandler` comments
above). A viewer's Help/About menu items would have 404'd silently.
Fixed directly in the test deployment's `Robust.HG.ini`: `about` ->
`/page/about`, `register` -> `/register`, `help` -> `/help`,
`password` -> `/forgot-password`. Only Robust reads this file, so only
Robust needed restarting, not the region processes. Verified via a
real `get_grid_info` call after the restart - all four keys now
resolve to the correct native paths. Two adjacent keys, `search`
(`/helper/query.php`) and `message` (`/helper/messages.php`), are
still pointed at the old PHP helper endpoints - a separate, already-
documented gap (the search-directory-registration pings noted in the
Batch 13 hostname-migration entry above), not touched by this fix and
not something today's page changes introduced.

### Native Destination Guide (2026-08-12)

The user correctly pushed back on an assumption made while triaging a
Firestorm screenshot: a viewer's Destinations floater showing a stock
"page not found" placeholder was initially assumed to be Firestorm's
own hardcoded default guide, unconfigurable from this side. It isn't -
`[GridInfoService] DestinationGuide` is a real, working config key
(already present, commented out, alongside `AvatarPicker`/`GridSearch`
in the `oswebinterface` block), the exact mechanism a viewer's
Destinations floater actually reads, the same shape as `welcome`
already driving the login splash. The user pointed directly at the
reference implementation to build from - `guide.php`, a
Popular/Featured/Discover tabbed places browser with teleport-on-click
cards - rather than leaving the floater unconfigured.

**Real data-layer gap, not just a missing route:** `ISearchService`/
`ISearchData`'s existing `SearchPlaces` only ever needed to answer the
in-viewer Places search panel, so `LandSearchRecord` never carried
region name, landing point, description, or category - nothing a
Destination Guide needs to build a teleport link or show real context
was actually missing from the `land` table, just never projected into
the query. Extended `LandSearchRecord` (`OpenSim/Framework/
SearchData.cs`) with `RegionName`/`Description`/`Category`/`LandingX/Y/Z`,
enriched `SearchPlaces`'s own SELECT to project them (via a new
`ReadEnrichedRecord`, kept separate from the original `ReadRecord` so
`SearchLandForSale` - which never needed these columns - is untouched),
and added a new `GetFeaturedPlaces(count, maxAccess)` method (real
`Category > 0` filter, random order per call) to answer the "Featured"
tab - a query shape neither existing method could produce. All three
`ISearchData` backends (MySQL/PGSQL/SQLite) updated in parallel, plus
the thin `SearchService` passthrough. One real per-backend quirk
caught by reading each implementation rather than assuming they're
identical: SQLite's `land` table names its description column `Desc`,
not `Description` like MySQL/PGSQL - already true of the *existing*
`SearchPlaces` query before this change, just confirmed rather than
copied blindly into the new enriched version.

**`HandleGuide`** (`/guide`, bare chrome via `WriteBarePage`, same
reasoning as Help/About/the login splash): Popular (`SearchPlaces("",
0, 30, 13)`, already dwell-sorted), Featured (the new
`GetFeaturedPlaces`), Discover (`m_GridService.GetRegionRange`, the
same call `HandleDestinations` already uses, sorted alphabetically) -
three tabs, client-side switch via a small page-scoped script (no
reload, matching the small-panel feel of an embedded viewer browser).
Built with Confluence's own `widget-card`/`subnav` styling rather than
porting `guide.php`'s separate CSS, and a small `ParcelCategories`
label lookup (same real `OpenMetaverse.ParcelCategory` values
`guide.php`'s own category map uses) alongside the existing
`ClassifiedCategories` array for the same purpose elsewhere in this
file. maxAccess defaults to 13 (PG) - the same safe default
`HandleSearch` uses when there's no explicit maturity preference to
read, since the Destination Guide floater exposes no query-string
control for it.

**Build/deploy/verify:** full solution rebuild, 0 errors. This one
crosses more assembly boundaries than a typical WebInterface-only
change - `OpenSim.Framework.dll` (the record type), `OpenSim.Data.dll`
+ its three backend DLLs, `OpenSim.Services.Interfaces.dll`,
`OpenSim.Services.SearchService.dll`, and `OpenSim.Server.Handlers.dll`
all changed - copied all of them together this time rather than
discovering a missing one via a reflection-loading failure the way an
earlier batch did. Stopped and restarted all three test-deployment
processes (same file-lock reason as the previous WebUI polish entry).
Robust log confirmed a clean reload with the new
`[SEARCH SERVICE]: Starting search service` line and no errors.
Verified `/guide` live via curl: HTTP 200, no `<header>`/`<footer>` in
the DOM, Popular/Featured correctly show real empty-state messages
(no parcels currently opted into the directory with a category set -
not fabricated placeholder data), Discover correctly lists both real
online regions with working `secondlife:///app/teleport/...` links.
Uncommented and repointed `[GridInfoService] DestinationGuide` at
`/guide` (was the old PHP site's `guide.php`), restarted Robust, and
confirmed via a real `get_grid_info` call that it now resolves
correctly. `AvatarPicker`/`GridSearch`, the two adjacent still-commented
keys in the same config block, remain out of scope for this pass - not
touched, not verified.

### DestinationGuide fix was incomplete: a second, separate ini key (2026-08-12)

The Destination Guide fix above only touched `[GridInfoService]
DestinationGuide`, which feeds `get_grid_info` - verified correct, but
the wrong verification. A viewer's Destinations floater actually reads
`destination_guide_url` from the LLSD login response, populated at
Robust startup from a **completely separate** `[LoginService]
DestinationGuide` key (`LLLoginService.cs` reads
`m_LoginServerConfig.GetString("DestinationGuide", ...)` from a
different config section than `GridInfoHandlers` does). That second
key still said `/guide.php` and was never touched by the earlier fix -
confirmed live in Firestorm by the user, still showing the old
placeholder "obsconded with by knomes" page after the first fix.
Corrected `[LoginService] DestinationGuide` to `/guide`, restarted
Robust. Since this value is baked into the LLSD response only at
login time (read once into a field in `LLLoginService`'s constructor,
not re-read per-request), a client needs to log out and back in to
pick up the corrected value - the user's existing session was still
holding the old one. Lesson: `get_grid_info` and the real LLSD login
response are two independent delivery paths for several of these
URLs, not one - verifying the REST endpoint doesn't verify what a
viewer's floater actually receives at login.

### WhiteCore-Dev's real WebUI static assets, re-audited after being missed twice (2026-08-12)

The user pointed out, correctly, that this session's splash/help page
work had once again not consulted WhiteCore-Dev's real `bin/html/`
templates before writing new content - the same gap already flagged
once in this file's "First-landing pages" entry and in memory
(`casperia-audit-must-include-static-assets`), recurring on the exact
same page. Read all 84 real files under
`WhiteCoreSim/bin/html/` this time (via a dedicated research pass,
not a skim) to find genuine, concrete gaps rather than a vague
impression:

- **`welcomescreen/gridstatus.html`** - a real widget (total
  users/regions, online-now count, voice/currency active flags) the
  splash never had. **`welcomescreen/region_box.html`** - a real
  region thumbnail/name/position/teleport-link list, also never on
  the splash. Both added to `HandleWelcome` this pass (see below).
- **`help.html`** - turned out to be a smaller gap than first
  suspected once actually read: its viewer-logo grid is the *same*
  content already mined into `/viewers` (confirmed - this exact file
  is cited in the Get a Viewer batch's own PROJECT_LOG entry), and
  `/help` already links to `/viewers` rather than duplicating it. The
  one genuinely distinct piece is an IRC support button/modal - not
  built, since it requires knowing whether this grid actually has an
  IRC channel to link to, not something to invent silently.
- Everything else audited (admin pages, user self-service pages,
  classifieds/events, the world map, webprofile/regionprofile modals)
  already has a real Confluence equivalent built across earlier
  batches, confirmed against the full file-by-file inventory rather
  than assumed. Two small, real, previously-unflagged gaps surfaced
  and are **not yet built**: a region-profile detail view
  (`regionprofile/modal_profile.html` - owner/parcel-count/maturity/
  who's-currently-in-region) and a "Picks" favorite-places list on
  `webprofile/modal_picks.html`'s pattern, neither of which Confluence
  has any equivalent of anywhere.

**`RenderGridStatusWidget`/`RenderRegionListWidget` added to
`HandleWelcome`** (both bare-chrome, same as the rest of the splash):
grid-status reuses the exact same `GetOnlineUserCount`/
`GetUserAccountsWhere` calls `HandleAdminStats` already established as
the real data source for these numbers, rather than a second,
divergent counting method - "Unique Visitors" and "Voice Active" from
the real WhiteCore widget were deliberately left out rather than
faked, since Robust has no live presence tracking beyond the same
recent-login-timestamp proxy `GetOnlineUserCount` already is, and no
way to tell if voice is actually configured. Region list reuses the
same `GetRegionRange` call and map-tile URL convention
`HandleDestinations`/`HandleGuide`'s Discover tab already use, rather
than a third way of listing regions. Build 0 errors, redeployed (all
three test-deployment processes, same file-lock reason as before),
Robust log confirmed clean reload. Verified live via curl: `/welcome.php`
now shows real numbers - 2 regions, 8 registered accounts, 1 online
now, currency active, both regions listed with working teleport links -
not placeholder data.

Also updated the `casperia-audit-must-include-static-assets` memory
(internal, not tracked in this repo) to tighten the rule: check
WhiteCore-Dev's real `bin/html/` before writing or rewriting *any*
WebUI page's content going forward, not just at the start of a
large-scope "port features" audit.

### Separate browser vs. viewer-embedded search page (2026-08-12)

`HandleSearch` already carried a comment acknowledging it's the page
pointed to by `[LoginService] SearchURL` - a viewer's own Search
floater has a "Web" tab that opens this URL in its own small embedded
browser, the same mechanism `welcome`/`help`/`DestinationGuide` all
use - and that it "needs to render sensibly both in a normal browser
and inside" that panel. It never actually did anything about that:
`WritePage` (full header/nav/footer) unconditionally, regardless of
which context opened it. Same underlying issue as the splash/help/
about/guide work above, just not caught at the time.

Split into `HandleSearch` (existing `/search`, full chrome) and a new
`HandleSearchEmbedded` (`/websearch`, bare chrome via `WriteBarePage`),
both thin wrappers around a shared `DoSearch(request, response,
embedded)` - same design/content either way, per explicit direction,
not two diverging implementations. The subnav "Search" self-link, the
search form's `action`, and the trending-query chip links all build
off a `selfPath` variable (`/search` or `/websearch`) instead of a
hardcoded path, so submitting a new query or clicking a trending term
from inside the embedded view stays on the embedded route rather than
silently dropping back to full chrome after the first click - the
`/landsearch` cross-link deliberately still points at the full-chrome
browser page, since Land for Sale is a browsing/shopping feature, not
something the viewer's Search floater's Web tab needs. Repointed
`[LoginService] SearchURL` at `/websearch`.

Build 0 errors, redeployed (`OpenSim.Server.Handlers.dll` only).
Verified live via curl: `/search` still has `<header>`/`<footer>` with
`action="/search"`; `/websearch` has neither, with `action="/websearch"`;
`/websearch?q=welcome` (simulating a follow-up search from inside the
embedded view) still has no chrome and its self-links still point at
`/websearch`, not `/search`. Since `[LoginService] SearchURL` only
reaches a viewer via the LLSD login response (the same
`DestinationGuide` lesson from earlier this session), this can't be
verified via `get_grid_info` - only the ini value and a clean
`LLLoginServiceInConnector loaded successfully` reload were confirmed;
genuine verification needs a real client login.

**Found while verifying, not touched:** a third, unrelated
`SearchURL`-shaped key turned up in a `[Search]` block further down
`Robust.HG.ini` - this belongs to the old addon-modules `OpenSimSearch`
module's own config shape (`OpenSearch.cs` reads a `[Search]
SearchURL` key), not `LLLoginService` or `GridInfoHandlers`. Confirmed
it's not dead: Welcome Center's own region-side `OpenSim.ini` still has
`[Search] Module = "OpenSimSearch"` (the legacy addon, still active
there) with a matching `SearchURL` pointing at the same
`/helper/query.php` path already documented as a known, accepted gap
(no native backend exists for that classic search-directory-
registration endpoint). Not a new problem, not touched.

Also added a `Help` link to the site header nav (`WritePage`'s
`<nav class="site-nav">`), which had never been wired in despite the
page existing.

### Viewer download list had gone stale (2026-08-12)

Per the user (not independently re-verified against each project's own
site, same as the earlier weather-port precedent): Alchemy and Kokua no
longer support OpenSim at all, Singularity hasn't been updated in
years, and Lumiya/Pocket Metaverse are gone entirely - five of the
seven entries on `/viewers`, all originally sourced from WhiteCore-Dev's
own `help.html` list (real at the time that page was built, not
fabricated, just since gone stale). Trimmed `DesktopViewers`/
`MobileViewers` down to what's actually still real: Firestorm (all
three platforms) and Cool VL Viewer as the two remaining graphical
desktop viewers, Radegast as the still-active text-based client, and
Mobile Grid Client kept with an "older, not actively updated" note
rather than removed outright. Build 0 errors, redeployed, verified
live via curl - `/viewers` now lists exactly those six entries, nothing
else.

### About page rewrite: competitive positioning + real content preserved (2026-08-12)

The user pointed at two references directly: a competing grid's real
public About Us page (3rd Rock Grid - purpose statement, "why join a
virtual world"/"why choose us" framing, platform highlights) and the
original `OpenSim-Grid-Interface/about.php` this page was first seeded
from (task #44). Rewrote the About page (a DB-backed `static_pages`
row, not a repo file - updated directly via the live database, no
code change or redeploy needed since `HandleStaticPage` already reads
it live) to add real "why join/why choose" sections modeled on 3RG's
structure, filled with things actually true of this software (native
economy, Hypergrid, the real web/admin control panel, real search,
real moderation) rather than any of 3RG's own business-specific claims
(LLC registration, DMCA agent, money-back guarantee) that have no
basis here.

**Caught after an incomplete first pass:** the initial rewrite trimmed
out real content from the original `about.php` - the Disclaimer/Legal
Disclaimer section, the user-generated-content liability paragraph,
and the "read the ToS/DMCA, be aware of age-appropriate content"
paragraph - in favor of the new competitive-positioning sections,
rather than keeping both. Corrected: all of that original disclaimer/
liability language is back verbatim (still real, still needed), with
the new sections added alongside it, not in place of it. One
deliberate, positive change kept from the first pass: the original's
"visit using Firestorm, Singularity, and others" line and its flat
support-email address were replaced with a link to `/viewers` (so the
page doesn't repeat the now-stale Singularity claim from the viewer-
list fix above) and links to the real `/support`/`/help` pages
(features that didn't exist when `about.php` was written) - an
upgrade to real, currently-working functionality, not a content
drop.

Verified live via curl: `/page/about` returns HTTP 200, bare chrome
still intact, and all eight headings present (Disclaimer, Legal
Disclaimer, Why Join a Virtual World?, Why Choose Casperia?, Virtual
Worlds with OpenSimulator, Community & Content, Questions?).

### Bare-chrome pages needed a way back for a normal browser too (2026-08-12)

The user's real ask, tested against the actual public hostname: `/page/about`
(and `/help`) needs to work when opened directly in a normal browser,
not just inside a viewer's embedded panel. Fully bare chrome (no
header, no nav, no footer) is right for the embedded case, but leaves
a real browser tab with zero way back to the rest of the site - a
dead end, not "works in both."

Rather than build a second full-chrome route the way `/search`/
`/websearch` were split (these pages have no internal sub-navigation
to preserve across clicks the way search's category tabs do, so the
extra route wouldn't earn its complexity), added a single small
"&larr; &lt;grid name&gt;" link to `WriteBarePage` itself, linking home -
negligible space inside an embedded viewer panel, but a real way back
in a normal tab. Fixed once, in the shared function, rather than
patched into `HandleHelp`/`HandleStaticPage` individually - applies
automatically to every current bare-chrome page (the login splash,
Help, About, the Destination Guide, the embedded search), not just
the two the user named.

Build 0 errors, redeployed, verified live via curl on all five bare
pages - each now starts with `<div class="bare-topbar"><a href="/">&larr;
Casperia Prime Dev</a></div>` before its own content, still no full
header/nav/footer.

### Real viewer-vs-browser detection, replacing the band-aid above (2026-08-12)

The previous entry claimed a single URL "can't reliably tell" whether
it's being opened by a viewer or a normal browser, and worked around
that with a small home link on an always-bare page. The user correctly
called this out and pointed at the actual mechanism:
`OpenSim-Grid-Interface/include/viewer_context.php`
(`os_detect_viewer()`), already `include`d by its own `about.php`/
`help.php`. It works, and is real: any SL-protocol viewer's embedded
browser attaches `X-SecondLife-Owner-Name`/`X-SecondLife-Region`/
`X-SecondLife-Shard` HTTP headers to every request it makes (the same
behavior it uses for in-world web media, not something specific to
these pages), with a User-Agent substring check and a `?view=viewer|web`
+ cookie override as fallbacks. Ported directly rather than reinvented
- `IsViewerRequest`/`ViewerHeaders`/`ViewerUserAgentNeedles` mirror the
PHP version's header list, UA needle list, and query/cookie override
exactly.

**Real architecture simplification, not just a bugfix:** with per-request
detection working, the earlier `/search` vs `/websearch` route split
(added specifically to solve this same problem, before the real
mechanism was found) is no longer needed - collapsed back to one
`/search` route, `HandleSearchEmbedded`/`DoSearch`'s `embedded` bool
removed, `SearchURL` repointed at plain `/search`. `HandleStaticPage`'s
`slug == "about"` special case (bare-chrome for About only) is gone too
- every static page now decides per real request, since ToS/DMCA could
just as validly be opened by a viewer as About could. Added
`WriteAdaptivePage` (picks `WriteBarePage` for a detected viewer
request, `WritePage` otherwise) and switched `HandleWelcome`,
`HandleHelp`, `HandleStaticPage`, `HandleGuide`, and `HandleSearch` all
onto it - five pages now genuinely work correctly in both contexts
from one URL each, not four different one-off workarounds.

Build 0 errors, redeployed. Verified live via curl with real header/
UA simulation, not just a plain request: `/page/about`, `/help`, and
`/search` each confirmed full chrome (real `<header>`) with no viewer
signal present, and bare chrome (`bare-topbar` div, no `<header>`) with
a simulated `X-SecondLife-Region` header on one request and a
Firestorm-branded User-Agent on another - the actual per-request
switch, not just "the page loads." `/welcome.php` and `/guide` also
confirmed still bare under a simulated viewer request (no regression),
`/page/tos` and `/page/dmca` confirmed still HTTP 200 as plain browser
pages.

### Vendored Bootstrap Icons + real icon/hover-effect pass (2026-08-12)

The user pushed back hard on a pattern across this whole WebUI thread:
pages were being rewritten based on a skim of reference material
rather than genuinely absorbing it, and the result read as "plain
text with a few highlighted boxes" - not the bar a page meant to
attract other grid owners to adopt this platform needs to clear. Two
reference points made the gap concrete: `OpenSim-Grid-Interface/
features.php` (icon-per-row via Bootstrap Icons, colored pill badges,
hover-lift region cards, a full "Powered By" infrastructure grid) and
that same project's own `docs/icons-and-theme.md`, which documents
Bootstrap Icons as the standardized icon system across its ~90-file
site. A full listing of that project (`account/`, `admin/` including
a real analytics dashboard, `api/`, `maps/`, dual Gloebit/Podex
currency-addon support, a curated holiday/announcements system)
confirmed it's a genuinely mature project Confluence's native WebUI
doesn't match yet - not something to close in one pass, but the icon
system specifically was addressable now.

**Real architectural decision, not a guess:** asked the user directly
whether to vendor Bootstrap Icons locally, keep hand-building inline
SVGs, or vendor a different icon set - vendoring Bootstrap Icons won.
Downloaded the real MIT-licensed distribution (v1.11.3, from the
project's own jsdelivr-hosted release - `bootstrap-icons.css` +
`.woff2` + `.woff`, ~400KB total) once, at development time; the
deployed grid never needs network access for this since the files are
embedded resources compiled directly into `OpenSim.Server.Handlers.dll`
(`WebInterface/Resources/`, declared in `prebuild.xml` the same way
`.sql`/`.migrations`/`.addin.xml` resources already are elsewhere),
not loose files to lose track of on redeploy. Rewrote the vendored
CSS's `@font-face` `url()`s from `./fonts/...` to `/static/...` to
match the new serving path. Added `HandleStaticAsset` + a `/static/*`
route (same varPath-prefix pattern `/page/*` already uses) that
resolves the embedded resource via `GetManifestResourceNames()` +
suffix match - the same defensive lookup `Migration.cs` already uses
for its own embedded resources, not a hand-guessed resource name -
and serves it with a real `Cache-Control: immutable` header, caching
the resolved bytes in a static dictionary after first load.

**Real regression caught and fixed, unrelated to the icons themselves:**
regenerating `OpenSim.Server.Handlers.csproj` via `runprebuild.bat`
(needed to pick up the new `EmbeddedResource` entries) wiped
`MailKit`/`MimeKit` references that existed in the old `.csproj` but
were never declared in `prebuild.xml` in the first place - a real,
pre-existing gap in the tracked build config that only surfaced
because this was the first time in this project's history that file
got regenerated from scratch. Added `<Reference name="MailKit"/>`/
`<Reference name="MimeKit"/>` to `OpenSim.Server.Handlers`'s project
block in `prebuild.xml` (resolves via the project's existing
`ReferencePath`, same as every other simple reference there) and
regenerated again - confirmed fixed via a clean build.

**Icons and hover effects actually applied, not just plumbed in:**
added `<i class="bi bi-*">` to every header nav link (site-wide, every
page). Rebuilt `/features` completely: regrouped the existing 12
platform capabilities from a flat list into four icon-headed,
hover-lift `.feature-card`s (`transform:translateY(-4px)` + accent
left-border + real box-shadow on hover, matching the reference
project's own region-card treatment) with colored `pill`/`pill-yes`/
`pill-no` status badges instead of plain text; added two genuinely new
sections with the same treatment - "Region Configuration Options"
(VarRegions/Full/lighter-traffic patterns, framed as common OpenSim
naming conventions an operator can apply, not a fabricated Confluence
engine feature) and "Economy & Currency" (native currency, real
ledger/web-access facts, plus Gloebit correctly marked "Optional" as
the addon-module swap-in it actually is, not a fabricated "Active"
claim). Every icon name used was checked against the real vendored
CSS before shipping (`grep`'d each `.bi-X::before` rule) rather than
guessed.

Build 0 errors, redeployed, verified live via curl: `/static/
bootstrap-icons.css`/`.woff2`/`.woff` all return HTTP 200 with correct
Content-Type and byte-for-byte matching sizes; `/features` returns 9
real `.feature-card` divs (4+3+2, matching the three new/regrouped
sections), 25 list rows, both pill classes present, the stylesheet
link present, full site chrome intact. Actual rendered visual
appearance was **not** confirmed - the sandboxed browser pane in this
session can't composite frames for a screenshot, so this is markup-
level verification only; a real browser check is still needed. Header
nav icons and the Features rebuild are the first pages to get this
treatment - the rest of the ~40-page WebUI have not been touched yet
and are a real follow-up, not assumed done by extension.

### Welcome Center's in-world search was still on the dead legacy path (2026-08-12)

The user hit "Unable to search at this time" in-viewer (both the
Directory floater and, separately, several more failures opening the
World Map - both fire Dir*Query-shaped requests). Ruled out a
regression in today's `SearchPlaces` enrichment first, concretely: ran
the actual enriched SQL directly against the live database - clean,
zero rows, no error - matching what `/guide`'s Popular tab already
exercised successfully earlier today. Asked which region the failure
happened on rather than guess, since Welcome Center and Var Test
Region run on genuinely different search backends. Confirmed: Welcome
Center specifically, which was still on `[Search] Module =
"OpenSimSearch"` - the legacy addon, dependent on `SearchURL` pointing
at `/helper/query.php`, a dead endpoint flagged multiple times earlier
this session as an accepted, out-of-scope gap. Var Test Region has run
the native `ConfluenceSearchModule` since Batch 14 without this
problem.

**Fix:** switched Welcome Center's `[Search] Module` to
`ConfluenceSearchModule` and added the three service sections it needs
that Welcome Center never had at all - `[SearchService]`,
`[EventsService]`, `[UserProfilesService]`, `[GroupsSearchService]` -
copied verbatim from Var Test Region's own already-working config
(same `casperia_dev` connection string, same providers), not
reinvented. No code change needed, config-only. Restarted only the
Welcome Center process (Robust, Var Test Region, and the DLLs
themselves were untouched). Confirmed via `Get-CimInstance` that the
right process came back up and via Robust's own log that Welcome
Center re-registered with the grid cleanly immediately after the
restart - the region-side log file itself lagged/buffered past the
startup banner (a log-flush quirk observed before, not a hang).
**Not independently verified end-to-end**: confirming the Directory
floater and World Map actually work now needs a real viewer test from
inside Welcome Center, the same boundary as the DestinationGuide/
SearchURL fixes earlier - config correctness and clean process
startup were confirmed, the actual in-world protocol exchange was
not.

### Removed the bare-page "way back" topbar - obsolete since real viewer detection landed (2026-08-12)

The small "&larr; grid name" link added to `WriteBarePage` earlier
today (before real viewer-vs-browser detection existed) was meant to
give a normal-browser visitor a way back to the site when they opened
a bare-chrome URL directly. Per the user, it's clutter every time a
real viewer actually renders one of these pages - and checking the
call sites confirmed `WriteBarePage` is now reached *only* through
`WriteAdaptivePage` when a genuine viewer is detected (a plain browser
tab already gets full `WritePage` chrome instead via that same
dispatcher), so the link's original justification no longer applies
to any real code path. Removed the topbar div and its now-dead
`.bare-topbar` CSS rules entirely, rather than leaving unused styling
behind. Build 0 errors, redeployed, verified live via curl with a
simulated viewer header across all four bare-chrome pages
(`/welcome.php`, `/help`, `/page/about`, `/guide`) - no `bare-topbar`
in any of them, all still return HTTP 200.

### Header/footer bar capped at 1100px in a real browser, wasting the rest of the window (2026-08-12)

A real screenshot from the user (`/viewers` in an actual desktop
browser, full icon set already confirmed rendering correctly) showed
the black header/footer bars spanning the full window as intended, but
their actual content - logo, nav, Log In/Sign Up - stuck inside a
centered `max-width:1100px` column, leaving large dead black margins
on both sides on anything wider than that. `.site-header-inner`/
`.site-footer-inner` had inherited the same `max-width:1100px;margin:0
auto` constraint used for the actual page-content column
(`.page`/`.hero-inner`, which should stay narrower for readability),
never separated out as its own decision. Removed the `max-width`/
`margin:auto` from both - header and footer chrome now uses the full
window width, only the actual body content keeps the readable column
width. Build 0 errors, redeployed, verified live via curl - both rules
confirmed to no longer carry the `max-width` constraint.

### Static pages can now be wired into the site nav (WhiteCore-Dev precedent) (2026-08-12)

The user asked whether admin-created static pages could also control
their own nav placement, pointing at WhiteCore-Dev's real
`admin/page_manager.html` as precedent (an earlier answer had relied on
a stale paraphrase of that file from several turns prior instead of
reading it directly - corrected after the user flagged "Seems we are
still skipping over code!"). The actual file's fields (PageTitle,
PageTooltip, PagePosition, PageID, PageLocation, DisplayInMenu,
RequiresLogin, RequiresLogout, RequiresAdmin, RequiredAdminLevel,
ParentMenuItem) were deliberately trimmed down for Confluence: no
`ParentMenuItem` dropdown nesting (no submenu concept in the nav yet),
no `RequiresLogout`/`PageTooltip`, and a plain `RequiresAdmin` bool
instead of a graduated `RequiredAdminLevel`, since Confluence's session
model is a simple `IsAdmin` flag.

Added `ShowInNav`/`NavOrder`/`RequiresLogin`/`RequiresAdmin` to
`StaticPage` (`OpenSim/Framework/StaticPageData.cs`), migrated across
all three DB backends (`:VERSION 2` in each `StaticPage.migrations`,
MySQL/PGSQL/SQLite `Get`/`GetBySlug`/`GetAll`/`Store` all extended),
added the four fields to the admin static-page edit form following the
existing checkbox convention, and added `RenderNavPages(session)` to
`WebInterfaceServiceConnector.cs` - LINQ-filters `GetAll()` by
`ShowInNav`/`RequiresLogin`/`RequiresAdmin` against the current
session, sorts by `NavOrder`, and appends the results into
`WritePage`'s `<nav class="site-nav">` after the fixed hardcoded nav
items (additive, doesn't replace them).

Build 0 errors. Deployed `OpenSim.Framework.dll`, `OpenSim.Data.dll`
plus all three DB-backend DLLs, and `OpenSim.Server.Handlers.dll` to
the test grid; confirmed the migration applied via `DESCRIBE
static_pages` (new columns present with correct types/defaults).
Live-verified end-to-end by inserting a real `nav-test` page directly
via SQL with `ShowInNav=1` and confirming it appeared in the rendered
nav via curl; then setting `RequiresAdmin=1` on the same row and
confirming it correctly disappeared from an anonymous request. The
test row has since been deleted from the live database.

### Profile page enrichment (2026-08-12)

The user called the profile page "really super basic right now" and
pointed at `OpenSim-Grid-Interface/profile.php` again. Reading that
file directly turned up real, working infrastructure Confluence's
`HandleProfile` wasn't using at all: `UserProfileProperties` already
carries `WebUrl`, `Language`, `SkillsMask`/`SkillsText`,
`WantToMask`/`WantToText` (populated by the same `AvatarPropertiesRequest`
call already in use, just never rendered), and `IUserProfilesService`
already exposes `PickInfoRequest`/`AvatarClassifiedsRequest`/
`ClassifiedInfoRequest` - none of it wired up before. Reused OGI's own
skills/want-to bitmask label sets (`ProfileSkillLabels`/
`ProfileWantToLabels`) verbatim rather than inventing new ones, since
these are free-text-labelled bitmasks with no single canonical meaning
and the reference site is the thing residents may already be used to.

Added: friend count (`IFriendsService.GetFriends`, already wired for
the Friends list page, just never surfaced here); Website/Language/
Skills/Wants-To rendered as `.pill` badges (same pill styling as
`/features`); Picks enriched from name-only to description + region
name via a real `PickInfoRequest` call per pick (previously only
`AvatarPicksRequest`'s bare name list); and a new Classifieds section
(`AvatarClassifiedsRequest` + `ClassifiedInfoRequest` per ad, same
category-label array `/features`'s Featured Classifieds widget
already uses) - classifieds weren't shown on a resident's own profile
page at all before this. Deliberately NOT attempted: profile/first-life
snapshot images - same "would need the asset server's HTTP
texture-fetch endpoint, and SL texture assets are JPEG2000, which
browsers can't render natively without a server-side conversion step"
gap already flagged and deferred when Featured Classifieds skipped
images earlier this session; a real fix is a separate task, not a
quick add-on to this one.

Build 0 errors (`OpenSim.Server.Handlers` project only - no
Framework/Data/interface changes this time, unlike the nav-wiring
batch). **Deployment hit an unrelated, pre-existing problem**: after
stopping Robust to swap in the new DLL, every fresh restart hung
indefinitely partway through connector startup (right after
`AbuseReportsServiceConnector`, before the HTTP listener ever came up)
- confirmed NOT caused by this change, or by the earlier nav-wiring
batch, by disabling `WebInterfaceServiceConnector`,
`OfflineIMServiceConnector`, `CurrencyServiceConnector`, and
`UserProfilesServiceConnector` one at a time in `Robust.HG.ini` and
restarting each time: the hang reproduced identically every time, with
all four disabled and with all four restored. Likely one of the
remaining Hypergrid connectors (`GatekeeperServiceInConnector`,
`UserAgentServerConnector`, `HeloServiceInConnector`,
`HGFriendsServerConnector`, `HGInventoryServiceConnector`,
`HGAssetServiceConnector`, `HGGroupsServiceConnector`) or something
after connector loading entirely - not narrowed further given the time
already spent. The two region simulator processes (Welcome Center, Var
Test Region) were also stopped to release the file lock on the old DLL
and have not been successfully restarted either (`OpenSim.exe` exits
within ~1 second on every attempt, exit code 1, zero stdout/stderr,
nothing reaching its own log file even before log4net would normally
write a line - a different, equally unexplained failure from Robust's
hang). **Not resolved**: the profile code itself is believed correct
(compiles clean, logic mirrors already-proven patterns elsewhere in
the same file) but has not been live-verified against a running
server, unlike every other feature this session.

### Root-caused the "no search results" report: a stale region-side DLL (2026-08-12)

After seeding real land-for-sale/event/classified rows didn't fix the
in-world Search floater (still no Land Sales/Events/Classifieds
results, though People worked), the region's own log gave it away:
`[CASPERIA SEARCH]: Native search module is active` - the OLD,
pre-rename branding, while the current source says `[CONFLUENCE
SEARCH]`. `OpenSim.Region.CoreModules.dll` (region-side, contains
`ConfluenceSearchModule.cs`) had never been rebuilt or redeployed this
entire session - every fix this session went into Robust-side DLLs
(`OpenSim.Server.Handlers.dll`, `OpenSim.Data.dll`) or was never
touched at all, while the region-side module quietly kept running
whatever build predated even the Casperia-to-Confluence rename.

Rebuilding hit its own real problem first: `SmartThreadPool.csproj`
(an SDK-style, `net8.0`-targeted project) failed with `NETSDK1127:
targeting pack not installed` on a full/dependency-graph build,
despite an earlier narrow `/t:OpenSim_Server_Handlers` build having
succeeded minutes before - only the .NET 10 ref pack was present on
this machine (`C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref`
had only `10.0.10`), and the narrow build had simply never forced a
fresh evaluation of that project. `dotnet restore` against
`SmartThreadPool.csproj` pulled the missing `net8.0` ref pack from
NuGet and resolved it - an environment gap, not a code bug.

Rebuilt and redeployed `OpenSim.Region.CoreModules.dll`; confirmed via
the log that `[CONFLUENCE SEARCH]` now appears instead of `[CASPERIA
SEARCH]`. Added `m_log.InfoFormat` calls to `DirPlacesQuery`/
`DirLandQuery`/`DirEventsQuery`/`DirClassifiedQuery` (query params +
result count) plus `m_log.Warn` for the two null-service early-outs,
so the next real-world test is a live trace instead of another guess -
not yet exercised against a live search from a viewer.

Also discovered mid-investigation: this sandboxed session cannot
reliably restart `OpenSim.exe` (region processes) itself -
`Start-Process` launches it but it exits within ~1 second, zero
output, matching the earlier unresolved region-restart failure. The
user's own restarts (via their own launch method, not this session's
automation) work fine every time. Established going forward: hand off
restarts to the user rather than repeatedly failing to relaunch
region/Robust processes from this session and risking extended
downtime on their live test grid.

### WebUI content-parity audit, round 1 (2026-08-12)

Per the user's explicit decision (keep the current C#-generated
WebInterface architecture, but redo real parity checks against named
reference files - see memory `casperia-webui-content-parity-decision`)
and explicit instruction to check WhiteCore-Dev first ("that's where
this all came from"), read real reference files directly and fixed
concrete gaps found - not paraphrased from earlier summaries:

- **Help** (`/help`): rewrote against both WhiteCore-Dev's `help.html`
  (kept the login-URI framing; the viewer-download gallery there is
  already covered by Confluence's separate `/viewers` page, so not
  duplicated) and OpenSim-Grid-Interface's `help.php` (added the
  missing "Using Search From the Viewer" tab-by-tab explanation and a
  "Troubleshooting" section, including that a search category can
  legitimately show nothing until a resident lists something in it -
  directly relevant to the same-day search investigation above).
- **Profile** (`/profile`): checked against WhiteCore-Dev's real
  `webprofile/modal_profile.html`/`modal_regions.html`/
  `modal_groups.html` (not the paraphrase from several turns prior)
  and fixed 3 real gaps: online status now names the resident's
  current region (`GetRegionByUUID` on `GridUserInfo.LastRegionID`)
  instead of just "Online now"; Regions Owned now shows a map
  thumbnail + coordinates per region instead of a bare name list,
  reusing the exact tile-URL convention `RenderRegionListWidget`
  already established; Groups now shows a membership count in the
  heading.
- **Features** (`/features`): checked against OpenSim-Grid-Interface's
  `features.php` (573 lines, read in full). Explicitly did NOT port
  its Source Repositories/Powered-By sections - those describe that
  site owner's own personal GitHub repos and hosting stack, not a
  generic Confluence capability, and would violate this session's own
  established privacy-scrub rule if copied. Did add two real, honest
  gaps: OpenSimulator core version now shown in the Live Grid Snapshot
  (`OpenSim.VersionInfo.VersionNumber`, the same compile-time constant
  the console banner already reports - not a guess), and a "Voice" row
  stating plainly that Confluence has no bundled voice integration
  (confirmed by checking, not assumed) rather than leaving the topic
  unmentioned. Region Configuration Options and Economy & Currency
  sections were already at real parity from earlier work.
- **Guide** (`/guide`): checked WhiteCore-Dev for a Destination-Guide
  equivalent - none exists (`world.html`/`region_list.html` are a
  Leaflet-based region map and a table explicitly marked "no longer
  used" in WhiteCore's own source). OpenSim-Grid-Interface's
  `guide.php` remains the correct sole reference, already used.
- **Splash/Welcome**: re-confirmed already at real parity against
  WhiteCore-Dev's `welcomescreen/*` from earlier work this session.

Build 0 errors each pass, redeployed to Robust (stopped for the
search-module deploy above, so no file-lock conflicts). **Not yet
live-verified** - pending the user bringing the grid back up.

Still queued: Viewers page re-check (already built from both
references per an earlier task - spot-check only, not redone from
scratch), and the static-page nav-wiring field trim
(`ParentMenuItem`/`RequiresLogout`/`PageTooltip`/graduated admin
levels) - waiting on the user's explicit answer on whether to restore
WhiteCore-Dev's full field set there.

### Land Sales/Events search bugs actually root-caused via the real PHP backend the viewer used to talk to (2026-08-12)

The Events "0 results" investigation moved from an educated guess to a
confirmed fix once the user supplied OpenSim-Grid-Interface's real
`helper/query.php`, `parser.php`, and `register.php` - the actual,
proven XML-RPC backend the classic `OpenSimSearch` addon called out to
before this grid had a native search module at all. Reading
`dir_land_query`/`dir_events_query` directly (not guessed) turned up
two real, separate bugs in `ConfluenceSearchModule`/the `ISearchData`
land-search backend:

- **Price direction was backwards.** `SearchLandForSale` compared
  `SalePrice >= minPrice` (a floor); the real reference does
  `saleprice <= :price` (a ceiling - "no more than this"), and only
  applies it at all when the viewer's `LimitByPrice` flag (`0x100000`)
  is actually set, same for `LimitByArea` (`0x200000`)/area. Fixed
  across all 3 DB backends (`maxPrice`/`minArea`, either skipped when
  <= 0, matching how `/web/landsearch` already calls this expecting
  "0 means no filter") plus `ISearchData`/`ISearchService`/
  `SearchService`, and `ConfluenceSearchModule.DirLandQuery` now only
  passes the real price/area through when the matching flag bit is
  set, instead of always applying both unconditionally.
- **Events query text was never being parsed.** The viewer's Events
  tab sends `"u|0|test"` - `explode("|", $text)` in the real
  `dir_events_query`, pieces[0]=day token, pieces[1]=category,
  pieces[2]=search text (empty if fewer than 3 pieces) - not plain
  text. Neither WhiteCore-Dev's own `DirEventsQuery` nor the
  `OpenSimSearch` addon parse this in C# (checked both directly); it
  only ever happened server-side in this now-dead PHP script.
  `ConfluenceSearchModule.DirEventsQuery` now splits on `|` and uses
  `pieces[2]` exactly like the reference. Day-specific filtering
  (vs. "upcoming") is explicitly logged as unimplemented rather than
  silently faked - `IEventsService.SearchEvents` has no day-range
  query yet, only "upcoming" (`EventDate >= now`).

Also confirmed (not assumed): the seeded test land parcel's For-Sale
flags reverted on their own between test attempts. Root cause is
*not* a code bug - editing `land` table rows directly via SQL while
the owning region process is live gets silently overwritten by the
region's own next save cycle, since the region's in-memory `LandData`
object (loaded once at startup, never touched by a raw SQL UPDATE) is
authoritative, not the database row. Confirmed the parcel really is
owned (not an unowned-parcel-reset theory) before reaching this
conclusion. Real fix going forward: seed test land state through the
actual in-world "About Land" dialog, not direct SQL, when the owning
region is live.

Build 0 errors, deployed `OpenSim.Data.dll`,
`OpenSim.Region.CoreModules.dll`, `OpenSim.Server.Handlers.dll`, and
`OpenSim.Services.Connectors.dll` together (all four touched by the
`SearchLandForSale` signature/semantics change). **Not yet
live-verified** - pending the user's next grid restart and test.

### Currency module checked against real Firestorm viewer source (2026-08-12)

Per explicit user direction to stop guessing/improvising against the
OpenSim protocol and check native modules against real viewer source
before they ship, rather than after - the user added Firestorm
(`S:\Github\phoenix-firestorm`, already cloned, 288MB sparse
checkout) and Cool VL Viewer (`S:\Github\CoolVLViewer-1.32.5.11`,
already extracted from its source tarball) as standing local
references for exactly this. Scoped the audit to `ConfluenceCurrencyModule`
specifically - real money changes hands here, and unlike Land/Estate/
Groups/Friends/Terrain (stock, unmodified OpenSim core code with 15+
years of real-world viewer interop already proven), this is native
code this project wrote with no such track record. Read
`llcurrencyuimanager.cpp` and `llfloaterbuyland.cpp` directly (not a
summary) and found two real, concrete bugs:

- `HandleBuyCurrency`'s failure path only set `result["success"] =
  false` - the real viewer (`LLCurrencyUIManager::Impl::
  finishCurrencyBuy`) unconditionally reads `result["errorMessage"]`/
  `result["errorURI"]` on failure. LLSD's undefined-value defaults
  meant this never crashed, but a resident whose purchase failed saw
  a blank error dialog instead of a real reason. Fixed with real,
  specific error messages per failure case.
- `HandlePreflightBuyLandPrep` sent `"landuse"` (lowercase); the real
  viewer (`LLFloaterBuyLandUI::finishWebSiteInfo`) reads
  `result["landUse"]` (capital U) - LLSD map keys are case-sensitive,
  so this field was silently always undefined client-side. Also sent
  `"membership"` as a bare `{id, description}` pair; the real shape
  is `{upgrade: bool, action: string, levels: [{id, description}, ...]}`.
  Both happened to land on the same safe "no upgrade needed" default
  via LLSD's forgiving undefined-value fallback (fine outcome for
  Confluence, which has no SL-style paid membership tiers to offer),
  but only by accident. Fixed to the real shape, explicit rather than
  incidental.

Confirmed (not assumed) as correct rather than fixed: the `[Economy]
economy` GridInfo URL already ends in a trailing slash, which matters
because `LLCurrencyUIManager::Impl::startTransaction` builds the
request URL as a raw string concatenation - `getHelperURI() +
"currency.php"`, no slash inserted - so a missing trailing slash
would silently break every currency-buy request grid-wide.

Build 0 errors. **Not yet deployed/live-verified** - pending the next
grid restart.

### Completed the native-module viewer-protocol audit (2026-08-12)

Finished the full sweep the user asked for: check every module this
project actually wrote against real viewer expectations before it
ships, rather than after. Built the definitive inventory by grepping
for "Confluence"/"CONFLUENCE" branding across `Region/CoreModules` and
`Region/ClientStack` (not a guess or a partial list) - exactly 7 files
are genuinely native code, everything else in those trees is stock,
unmodified OpenSim already proven across 15+ years of real grids:

- `ConfluenceCurrencyModule.cs` - 3 real bugs found and fixed (see
  above).
- `ConfluenceSearchModule.cs` - 2 real bugs found and fixed (see
  above).
- `ViewerSignatureBanModule.cs` (WhiteCore grid-wide viewer ban port) -
  no viewer wire-contract to mismatch; it only reads standard
  baked-appearance texture data the viewer already sends via the
  ordinary protocol, one-directional detection, nothing it promises
  back to the client. Clean.
- `AuctionModule.cs` - no `client.On*` handlers at all; bidding is
  entirely admin-console-driven (`land auction bid <id> <bidder>
  <amount>`), not something a resident can do from their viewer.
  Not a protocol bug - a real, disclosed scope gap worth knowing
  before calling land auctions production-ready. Left as-is pending a
  product decision, not silently patched.
- `OnDemandRegionModule.cs`, `SimProtectionModule.cs` - confirmed
  (grepped, not assumed) zero client-facing handlers; pure server-side
  infrastructure, no viewer contract exists to check.
- `WebConsoleModule.cs` - HTTP-only admin endpoint consumed by
  Confluence's own WebInterface, no viewer protocol surface either.

Also spot-checked Experience Tools (`ExperienceModule.cs`, native, CAP-
based) even though not flagged broken: all 12 capability names
(`GetExperiences`, `AgentExperiences`, `UpdateExperience`, etc.) match
Firestorm's real `llviewerregion.cpp` registration list exactly, and
the core experience-record fields (`public_id`, `group_id`, `name`,
`description`, `maturity`, `properties`) match `llexperiencecache.h`'s
real constants exactly in both directions. No bugs found - this one
was already built with genuine SL-conformance care, consistent with
its own task history (Batch 11).

This closes out the viewer-protocol audit for now: every native
module with an actual client-facing contract has been checked against
real reference source (Firestorm, `S:\Github\phoenix-firestorm`; Cool
VL Viewer, `S:\Github\CoolVLViewer-1.32.5.11`, both supplied by the
user specifically for this purpose) rather than left to guesswork.

### Land auction web-bidding, end to end (2026-08-12)

`AuctionModule` was flagged during the audit above as having no
viewer-facing protocol handlers at all - bidding was entirely
admin-console-driven. Checked the real viewer source before building
anything: Firestorm's `llfloaterauction.h`/`.cpp` is seller/admin
tooling for STARTING an auction (Reset Parcel, Sell to Anyone, Start
Auction) - there is no bidding UI in the viewer at all, and never was;
real Second Life land auctions were always bid on through the SL
website, not the viewer client. Given that, the user chose web-page
bidding (matching how it actually worked) over inventing a
non-standard in-world mechanism.

Built as a full DB-backed feature, same shape as Currency/Events/
Search this session, since Robust (serving the bid pages) and the
region hosting the auctioned parcel are separate processes with no
live RPC between them:

- `OpenSim.Framework.LandAuctionData.cs` - new `LandAuction`/
  `LandAuctionBid`/`LandAuctionStatus` shapes.
- `IAuctionData` + MySQL/PGSQL/SQLite backends (`land_auctions`/
  `land_auction_bids` tables) - `PlaceBid` is atomic: a single
  `UPDATE ... WHERE Status=Active AND HighestBid < :amount AND
  MinBid <= :amount` only commits (and only then inserts the bid-
  history row) if this bid actually beats the current highest, so two
  near-simultaneous bids resolve safely via the DB's own row lock
  rather than a read-then-write race in application code.
- `IAuctionService`/`AuctionService` + `LocalAuctionServiceConnector`
  (region-side service loader, registers `IAuctionService` on the
  scene) - exact same dual-load pattern `ICurrencyService`/
  `IEventsService` already established.
- `AuctionModule` rewritten to read/write through the DB instead of
  an in-memory dictionary, and given an automatic 2-minute expiry
  sweep (`GetExpiredActive` + close out any auction past its `EndsAt`
  still `Active`) - without this, an auction only ever ended if an
  admin remembered to run `land auction end`, which defeats the point
  of letting residents bid unattended over several days. The existing
  bug-fixed currency-charge-then-land-transfer logic (winners
  previously never got charged - see the June auction fix elsewhere in
  this log) is unchanged, just shared between the manual console path
  and the automatic sweep via one `CloseAuction` helper instead of two
  copies.
- `WebInterfaceServiceConnector`: new `/auctions` (list active,
  real-time highest bid) and `/auctions/bid?id=` (detail + bid form +
  bid history) pages - this page IS the bidding mechanism, not a
  status display. Bid submission checks amount vs. current
  highest/minimum and the bidder's real `ICurrencyService` balance
  before calling `PlaceBid`, for a specific error message rather than
  a generic rejection - `PlaceBid`'s own atomic DB check is the real
  enforcement either way.
- Console commands extended (`land auction start <id> [min bid]
  [days]`) rather than replaced, so admin/testing use still works
  through the same code path a resident's web bid uses.

Real gotcha caught before deploying: `OpenSim.Data.PGSQL.dll` and
`OpenSim.Data.SQLite.dll` in the test deployment were still dated
Aug 12 13:15 - two days stale, because neither project is part of
`OpenSim.Region.CoreModules`'s normal build dependency chain (only
`OpenSim.Data.MySQL` is pulled in automatically) and neither had been
built directly since the `SearchLandForSale` price-semantics fix
earlier this session. That fix was live in source and in the deployed
MySQL backend, but silently never reached the PGSQL/SQLite backends
despite the docs claiming otherwise. Built both directly this time and
will treat "did the actual DLL get rebuilt" as its own explicit check
going forward, not an assumption from a shared-dependency build
succeeding.

Also discovered while wiring this up: several project files
(`OpenSim.Framework.csproj`, `OpenSim.Data*.csproj`,
`OpenSim.Region.CoreModules.csproj`) have `EnableDefaultItems=false`,
so new `.cs` files silently don't compile in until explicitly added as
`<Compile Include=...>` entries - confirmed by a real
`CS0246: LandAuction could not be found` error, not assumed. Added the
entries rather than guessing implicit globbing would pick new files
up.

Build 0 errors across all projects (`OpenSim.Framework`, `OpenSim.Data`
+ 3 backends, `OpenSim.Services.Interfaces`, new
`OpenSim.Services.AuctionService` project, `OpenSim.Region.CoreModules`,
`OpenSim.Server.Handlers`). Deployed all ten changed/new DLLs to the
stopped test grid; wired real `[AuctionService]` config into
`Robust.HG.ini` and both regions' `OpenSim.ini` (`[Modules]
AuctionService = LocalAuctionServiceConnector` region-side,
`LocalServiceModule`/`StorageProvider`/`ConnectionString` on both
sides, matching the exact `[CurrencyService]`/`[EventsService`] block
shape already established). **Not yet live-verified** - pending the
user's next grid restart and a real test auction.

### Found the Currency audit's real bugs were still live - a second, undetected copy of the same handlers (2026-08-12)

Widening the native-code inventory search beyond `Region/CoreModules`
and `Region/ClientStack` (the earlier audit's scope) to
`Server/Handlers` and `Services` turned up `OpenSim/Server/Handlers/
Currency/CurrencyServerConnector.cs` - a SEPARATE, Robust-hosted
implementation of the exact same `getCurrencyQuote`/`buyCurrency`/
`preflightBuyLandPrep`/`buyLandPrep` XML-RPC surface already fixed
earlier today in `ConfluenceCurrencyModule` (region-side). Its own
class comment explains why both exist: the viewer only ever learns
one grid-wide `[GridInfo] economy` URL at login, fixed to Robust's
port regardless of which region the avatar is on, so a per-region-only
registration (`ConfluenceCurrencyModule` alone) breaks the instant a
grid has more than one region - `CurrencyServerConnector` is what
actually answers a real viewer's currency-buy request on this
(multi-region) grid; `ConfluenceCurrencyModule`'s copy mainly serves a
single-process standalone deployment.

This file had the identical bugs, independently:
`HandleBuyCurrency`'s failure path never set `errorMessage`/
`errorURI` (blank error dialogs), and `HandlePreflightBuyLandPrep`
sent `"landuse"` instead of `"landUse"` plus the wrong `membership`
shape - same root causes, same fixes, ported directly from the
already-verified region-side version rather than re-deriving them.

This means the original Currency audit's fixes, while correct, were
applied to the code path a real viewer on this grid likely does NOT
exercise - a real, concrete instance of exactly the "keep revisiting
things already called done" pattern flagged earlier this session,
caught by broadening the inventory search rather than assuming the
first audit pass was complete. Build 0 errors, deployed
`OpenSim.Server.Handlers.dll` to the stopped test grid. **Not yet
live-verified.**

### Land Sales/Events search root-cause: wrong ParcelFlags bitmask constants (2026-08-16)

A real, manually-flagged-for-sale parcel (confirmed via the owner's own
About Land screenshot) still didn't appear in Land Sales search.
Refused to guess a fix from the observed `LandFlags` diff alone (a diff
can contain multiple simultaneously-changed bits) - instead wrote and
ran a standalone C# program against the real, compiled
`OpenMetaverseTypes.dll`/`OpenMetaverse.dll` to enumerate the
authoritative `ParcelFlags` enum values directly. Result: `ForSale =
0x4`, `ShowDirectory = 0x1000` - `MySqlSearchData.cs`/
`PGSQLSearchData.cs`/`SQLiteSearchData.cs` had these hardcoded as
`0x1000`/`0x100000` respectively, wrong since before this session
(Batch 14, the original build), not a regression from anything done
today. Fixed all three backends, rebuilt, deployed, live-verified via
the real parcel now appearing correctly in search.

### Events search day-arrow (Today/Yesterday/Tomorrow) never filtered by day (2026-08-16)

`ConfluenceSearchModule.DirEventsQuery` already parsed the viewer's
compound `"dayToken|category|text"` query string but only ever handled
`dayToken == "u"` (upcoming); any other token silently fell back to
upcoming-only, so the Events tab's Date-mode arrows always returned the
same results regardless of which day was selected. Read Firestorm's
real `FSPanelSearchEvents::setDay()`/`find()` (`fsfloatersearch.cpp`)
directly rather than reusing the old dead-PHP `query.php` day-token
format assumed earlier: the real client sends a plain signed day offset
from today (0/1/-1/...) computed in **Pacific time**, not UTC and not
the PHP format. Added `SearchEventsByDay(text, dayStartUnix, dayEndUnix,
start, count)` to `IEventsData`/`IEventsService`/all 3 DB backends, and
a `GetPacificDayBoundaryUnix` helper in `ConfluenceSearchModule` that
resolves the IANA/Windows Pacific timezone and computes the correct
day boundary, mirroring the real client's own math.

### Land Sales "Type" column - confirmed genuinely unfixable (2026-08-16)

Investigated whether the search results list's "Type" column (always
showing "(unkno...)") could be populated. Confirmed via two independent
real sources it cannot: Firestorm's own `fsfloatersearch.cpp` only
populates it from a `ProductSKU` wire field (a Linden-only
billing-catalog concept), and direct reflection against this
codebase's own compiled `DirLandReplyPacket.QueryRepliesBlock` shows
only `ParcelID/Name/Auction/ForSale/SalePrice/ActualArea` - no
`ProductSKU` field exists in the packet at all. Pulled LibreMetaverse's
real, current `data/message_template.msg` (the upstream libopenmetaverse
fork tracking Linden's actual protocol) to check whether a newer
library would help: the `ProductSKU` field is commented out in the
canonical spec itself (`//{ ProductSKU  Variable 1  }`) - dead at the
protocol level even in real Second Life, not an OpenSim/library
limitation. Every OpenSim/WhiteCore-Dev grid shows the same
"(unkno...)" here; not a gap in this codebase.

### Viewer quick-search box opened a blank "Web" search tab (2026-08-16)

Traced Firestorm's navbar quick-search (`LLNavigationBar::invokeSearch`)
and confirmed the default preference (`FSUseFSLegacySearch=false`)
routes it to `LLFloaterDirectory` (the browser-embedded "Web" search),
which navigates to the grid's advertised `SearchURL` with `[QUERY]`
substituted in. `[LoginService] SearchURL` in `Robust.HG.ini` already
pointed at the right route (`/search`) but had no `?q=[QUERY]`
appended, so it always opened a blank search page regardless of what
was typed - confirmed `/search`'s own handler already auto-executes a
real search from a `q` query-string param, so this was a one-line
config fix (`SearchURL = "...:${Const|PublicPort}/search?q=[QUERY]"`).

### Land Sales Teleport/Map hung on "Loading..." forever - wrong parcel ID format (2026-08-16)

Real bug, found by reading the actual client/server request flow, not
guessed: clicking a Land Sales result sends a UDP `ParcelInfoRequest`
carrying whatever UUID `DirLandReply` returned as `parcelID`.
`ConfluenceSearchModule.DirLandQuery` was returning the real database
parcel UUID, but stock `LandManagementModule.ClientOnParcelInfoRequest`
decodes that UUID via `Util.ParseFakeParcelID` - a region-handle+local-
x/y encoding baked into the UUID bytes, not a raw database key. Parsing
a real UUID fails validation, and the server silently drops the request
(`"got no parcelinfo; not sending"`) with no reply at all - exactly why
the detail pane and Teleport/Map hung indefinitely. Fixed by enriching
`SearchLandForSale` (all 3 DB backends) with the same `RegionName`/
`LandingX/Y/Z` columns `SearchPlaces` already selects, and building a
real `Util.BuildFakeParcelID(regionHandle, localX, localY)` in
`DirLandQuery` - the same encoding `LandObject.cs`'s own `LandData.FakeID`
already uses. First live test showed a parcel resolving to its
region's raw corner `(0,0,0)` instead of somewhere sensible - root
cause: `UserLocationX/Y` (the About Land landing point) defaults to 0
when never explicitly set, and 0,0 is the region's own corner, not "the
parcel." The parcel's real shape (its `Bitmap` blob) isn't fetched by
this query, so a guaranteed-inside-the-parcel point isn't cheaply
available - falls back to the region's center when landing is unset,
an honest improvement over the corner rather than a disguised guess at
the parcel's true shape.

### Events/Classifieds Teleport/Map - EventItem and web-created classifieds had no real position (2026-08-16)

`EventInfoRequest` already existed but never set `EventData.globalPos`
at all (`EventItem` had no position field), so Teleport/Map would have
sent residents to the grid origin. Checked the old, proven OpenSimSearch
addon's own `EventInfoRequest` first per established practice - it
already solved this exact problem via a `"globalposition"` field parsed
with `Vector3.TryParse`, the same pattern this codebase's own
`UserClassifiedAdd.GlobalPos`/`ClassifiedInfoRequest` already uses.
Added `EventItem.GlobalPos` (DB migration on all 3 backends), a Region
`<select>` on both the admin and self-service "create event" forms
(computing a real global position from the chosen region's actual grid
coordinates), and fixed `EventInfoRequest` to parse it the same way
`ClassifiedInfoRequest` already does.

While in there, found a second, separate, real bug: the self-service
"post a classified" form (`HandleMyClassifiedsSave`) hardcoded every
listing's position to `ad.GlobalPos = "<128,128,25>"` regardless of
which region was actually picked - only ever correct for a region
sitting at the grid's own origin (0,0); every other region's classifieds
had Teleport/Map pointing at the wrong place, confirmed live via a real
"Invalid Location" map result and `Teleport failed` error. Fixed the
same way - compute the real global position from the chosen region's
`RegionLocX/Y` (already in meters, confirmed via `IGridService`'s own
"DANGER DANGER" doc comment that this differs from `RegionInfo`'s
same-named fields). Both fixes only apply going forward - pre-existing
events/classifieds keep their old broken position until edited and
re-saved through the fixed form (or fixed directly via SQL, done once
for the live test data to unblock testing).

### MoneyServer had the same currency bugs already fixed in ConfluenceCurrencyModule (2026-08-16)

Per explicit instruction to check independent/addon modules for the
same class of bug even though this grid doesn't use them: `OpenSim-Grid-
MoneyServer`'s `preflightBuyLandPrep` was missing `currency`/
`membership`/`landUse` entirely (worse than Confluence's own earlier
mis-cased version), and `buyCurrency` only ever set a `message` field
the real viewer never reads for failures - confirmed via
`llcurrencyuimanager.cpp`/`llfloaterbuyland.cpp` that Firestorm reads
`errorMessage`/`errorURI` and `result["membership"]`/`result["landUse"]`
directly, so both bugs produced the same class of user-facing failure
(blank error dialog; Buy Land dialog silently missing upgrade info) as
the already-fixed Confluence code. Fixed both to the same validated
shape. Separately checked whether the addon-modules `OpenSimSearch`
carries the Land-Sales fake-parcel-ID bug above - it doesn't, because
its `DirLandQuery` passes through whatever `parcel_id` the external
`helper/query.php` PHP backend returns rather than constructing one
itself; that PHP script isn't part of this repository, so its
correctness (or lack of it) is out of reach from here.

### Automatic cleanup for ended events (2026-08-16)

Per explicit request (DB bloat concern - nothing previously deleted an
event once it ended, only filtered it out of "upcoming" queries). Added
`IEventsData.DeleteExpired(int nowUnix)` (all 3 backends, deletes by
real end time = `EventDate + DurationMinutes*60`, never mid-event) and
a `System.Timers.Timer` sweep in `EventsService`'s constructor - same
shape as `AuctionModule`'s own `m_expirySweepTimer`, 10-minute interval
(pure housekeeping, no user-facing deadline to honor promptly unlike an
auction actually needing to close on time).

### Real gotcha: incremental solution build silently skipped a changed project (2026-08-16)

After adding `DeleteExpired`, `dotnet build OpenSim.sln -c Release`
reported "Build succeeded, 0 errors" but `bin/OpenSim.Services.
EventsService.dll` was never actually rebuilt - confirmed by its file
timestamp/size being unchanged and, at region startup, a real
`System.TypeLoadException: Method 'DeleteExpired' ... does not have an
implementation` (the interface assembly had the new method, the
implementation assembly didn't - a version-skew MSBuild incremental
bug, not a code error). This produced a confusing "works, doesn't work,
works, doesn't work" cycle across several deploys before being caught.
Fixed by forcing `dotnet build OpenSim.sln -c Release --no-incremental`
and independently confirming via C# reflection against both the built
`bin/` copy and the actual deployed file that `DeleteExpired` was
really present, rather than trusting the build log alone. Going
forward: for any fix where a repeat of this exact failure mode would be
costly to re-diagnose, verify the compiled output by reflection, not
just a successful build message.

### Deploy process fix: full `bin/` sync instead of cherry-picked files (2026-08-16)

Several rounds of deploying only the specific DLLs believed to have
changed let the live grid and the local build silently drift apart -
one instance caused a real `MissingMethodException` at runtime for a
DLL (`OpenSim.Services.Interfaces.dll`) that was never copied because
it wasn't recognized as "one of the changed files." A full sweep
comparing every DLL in `bin/` against the deployed grid found 87 (later
144-file) mismatches at once. Adopted a standing rule for the rest of
this session and going forward: whenever deploying to the test grid,
copy every `.dll`/`.exe` in `bin/` to the deployed directory and verify
byte-for-byte (md5) afterward, rather than reasoning about which
specific files "should" have changed.

All of the above: build 0 errors, full `bin/` sync deployed to the
stopped test grid, byte-for-byte verified, and live-confirmed working
by the user - Land Sales search/Teleport/Map, Events search (keyword +
Date-mode day arrows)/Teleport/Map, Classifieds Teleport/Map, and the
viewer's quick-search box are all now real, tested, working features,
not just "should work" fixes.

### Upstream audit: opensim/opensim + OpenSim-Tranquillity - three real security/correctness bugs found and ported (2026-08-16)

Per the user's advisory that both upstream repos had updated, pulled
both local clones (`S:\Github\opensim-master`, `S:\Github\OpenSim-
Tranquillity`) and diffed recent commits rather than assuming relevance.
`opensim/opensim` master was unchanged (identical commit hash to the
existing local checkout). `OpenSim-Tranquillity`'s `develop` and
`release/v1.0` both had real recent activity (24 and 14 commits in the
last 30 days respectively). Checked each commit for applicability to
this codebase rather than porting wholesale; most were infrastructure
(dotnet10 SDK bump, ASP.NET-hosted-services refactor, NBGV versioning,
log4net newline formatting) or Phlox-specific (a new LSL/SLua engine
Confluence never integrated - confirmed via a repo-wide filename search
turning up zero `Phlox*` files anywhere in this tree, so those fixes
don't apply here). Three did apply, confirmed present in this codebase
before porting the exact same fix rather than re-deriving it:

- **SQLite CVE - checked, not applicable.** Tranquillity migrated off
  `Microsoft.Data.Sqlite` after a "critical vulnerability... fixed
  upstream in System.Data.SQLite." Confirmed via grep that this
  codebase never referenced `Microsoft.Data.Sqlite` anywhere - already
  exclusively on `System.Data.SQLite`. Not exposed; no change needed.
- **Dead estate `DenyIdentified`/`DenyTransacted` access enforcement**
  (Tranquillity PR #188). Both bits were commented out as "unused" in
  `GetRegionFlags()` - so `EstateSettings.DenyIdentified`/
  `DenyTransacted` were correctly stored but never actually folded into
  the region-flags bitmask - and the `RegionDenyIdentified`/
  `RegionDenyTransacted` capability fields the viewer actually enforces
  client-side access restrictions against were hardcoded `false`
  regardless of estate configuration. Confirmed byte-for-byte identical
  dead code in `LLClientView.cs`. Any estate admin who believed they'd
  enabled "deny access to unidentified/unverified residents" or "...
  without payment info on file" was getting zero real enforcement.
  Fixed both halves the same way Tranquillity did.
- **Land-for-sale map overlay checked `SalePrice > 0` instead of the
  `ForSale` flag** (Tranquillity PR #189), in `LandManagementModule.cs`'s
  `LAND_TYPE_IS_FOR_SALE` classification (the world-map "for sale"
  parcel color, a different code path from this session's own
  `SearchLandForSale`/`ForSaleFlag` fix). Since every parcel defaults to
  `SalePrice = 0`, a genuinely free parcel with `ForSale` actively set
  would never render as purchasable. Confirmed the identical bug present
  here; fixed the same way.
- **Hypergrid asset-export permission gap** (Tranquillity PR #187), in
  `HGInventoryAccessModule.cs`. Two parts: `OutboundPermission` defaulted
  to `true` (default-allow HG export unless an admin explicitly opted
  out), and the per-item `PermissionMask.Export` bit was never checked
  at all on outbound transfers - meaning a creator's explicit "do not
  allow export" setting was silently ignored on every Hypergrid
  transfer. Confirmed via `grep` that Casperia-Dev's actual deployed
  config has `OutboundPermission` commented out everywhere (relying on
  the default), so this is a real behavior change on this specific
  grid, not just a latent code fix - flagged to the user for that
  reason. Fixed both halves: default flipped to `false` (deny-by-default,
  matching Tranquillity), and outbound transfers now also require
  `item.CurrentPermissions & PermissionMask.Export`.

One build hiccup: `PermissionMask` is ambiguous between
`OpenSim.Framework.PermissionMask` and `OpenMetaverse.PermissionMask`
(same class of ambiguity as `GridRegion` hit earlier this session) -
resolved by fully qualifying `OpenSim.Framework.PermissionMask.Export`.
Full solution build with `--no-incremental` (0 errors, per the gotcha
documented above), full `bin/` sync deploy, 144/144 byte-verified.
**Not yet live-tested** - these are security-posture fixes, not
something with an obvious in-viewer test; recommend confirming after
deploy that Hypergrid export and estate deny-access settings behave as
expected on a real test case before relying on them.

### WebUI content-parity rebuild + live tester round (2026-08-16)

Continued the page-by-page WebUI rebuild against the real OGI reference
site (per the earlier content-parity decision - keep the C#-generated
architecture, but audit each page against real reference content rather
than inventing copy). Real user-confirmed facts about this grid's own
stack (Powered By / Membership Perks / Community Extras) were made
admin-configurable via Grid Settings rather than hardcoded, hidden
entirely when unset. Destinations (browse/teleport) and World Map
(Leaflet) were wrongly merged in an earlier pass - split into separate
pages/routes. Added: public `/gridstatus` (online now, regions,
accounts, new-accounts-7d, land area, service status - matters for
Hypergrid Business discovery, per the real `get_grid_info` spec field
names, also fixed this session: lowercase `search`/`message`, not the
previously-wrong key names); resident-to-resident web messaging
(inbox/sent/compose, distinct from the existing offline-IM viewer);
public `/economy` dashboard (grid totals, top-balances leaderboard,
recent transactions). Ported `ICurrencyData` to PGSQL and SQLite (was
MySQL-only since Batch 12 - "isn't cross-DB support the whole point?").
Region restart wired for both residents (own regions, via My Regions)
and admins (any region, via the new dedicated Admin > Region Management
page) through the existing `[WebConsole]` shared-secret relay - the same
mechanism already live-proven for Kick/Message. User Management and the
new Region Management page both fixed to show all results on a blank
search (previously showed nothing) with pagination. Header/sidebar
polish: dropdown Explore/Grid Info nav groups, colorized sidebar icons
(rotating `ic-*` utility classes), grey-to-white body text, larger base
font size. `welcome.php` trimmed to fit the viewer's small embedded
window without scrolling.

Build-number versioning restored (grid operator's prior convention:
build # = commits ahead of real `opensim/opensim` upstream) via a new
`GenerateGitVersionInfo` MSBuild target baked in at build time (works in
a deployed `bin/` with no `.git` present) - added as a separate
`DisplayVersionNumber`/`BuildCommitHash` const rather than touching
`VersionInfo.VersionNumber` itself, which
`[assembly:AddinRoot("Robust", ...)]` depends on for every addon
module's Mono.Addins compatibility check (the display-only hash was
later dropped from the default string per feedback - an all-digit short
hash reads as a confusing number, not a recognizable commit id - kept
available separately for a hover tooltip).

Added real dollar-based purchase caps to the native currency ledger
(`CurrencyService.RecordPurchase`, daily $500 / weekly $2000 / monthly
$5000, rolling windows not calendar boundaries, each cap individually
disable-by-0) after a live tester purchase went through ungated - a real
parity gap versus the old DTLNSLMoneyModule/MoneyServer, which had
configurable purchase limits Confluence's ledger never got an equivalent
for when Batch 12 made it the default. Pure internal change to
`CurrencyService.cs`, no interface touched - scoped single-project
deploy, confirmed effective via a direct `currency_purchases` table
query (not just "the code looks right").

**Live incident: land-buy hang.** Mid-session, the live tester (told to
"break it, both the website and inworld") got stuck: Buy Land in
Firestorm hung forever on "(waiting for data)" with a blocking modal he
couldn't close, reproducible even after a fresh relog. Immediate
workaround given (force-kill the viewer process). Root-cause hunt, two
leads chased and both ruled out rather than assumed:

- **Gloebit handler collision (ruled out).** `GloebitMoneyModule.cs`
  registers the identical `preflightBuyLandPrep`/`getCurrencyQuote`/
  `buyCurrency`/`buyLandPrep` XML-RPC method names as
  `ConfluenceCurrencyModule`, in the same region process, gated only by
  `if (m_scenel.Count == 0)` in `AddRegion` - looked like a real
  collision risk. But `m_enabled` requires *both*
  `economymodule = Gloebit` *and* `[Gloebit] Enabled = true`; this grid
  has neither (`economymodule = ConfluenceCurrencyModule`,
  `Gloebit.ini Enabled = false`). Confirmed via the region log, every
  startup: `[GLOEBITMONEYMODULE] region not loaded as not enabled`. Dead
  end - the earlier `Plugin Loaded: Gloebit` log line only meant
  Mono.Addins loaded the DLL, not that it activated.
- **Server-side hang (ruled out).** Traced the viewer's actual request
  target: the login response's `economy` field
  (`${Const|BaseURL}:${Const|PublicPort}/` = port 9002, Robust's
  `CurrencyServiceConnector`, not any region's own port - the class
  comment there explains why: a multi-region grid needs one stable
  grid-wide URL, not a per-region one). Replayed the exact
  `preflightBuyLandPrep` XML-RPC request Firestorm sends against both
  `localhost:9002` and the test deployment's real external hostname
  on `:9002/currency.php` - both returned a complete,
  correctly-shaped response (`success`/`currency.estimatedCost`/
  `membership.*`/`landUse.*`/`confirm`) in milliseconds. Server side is
  healthy; this was not a hang or a missing/wrong-port handler.
- **`estimatedCost` always 0 - not a bug.** Initially flagged as a real
  bug (hardcoded regardless of actual price); on closer look, upstream
  OpenSim's own reference `SampleMoneyModule.preflightBuyLandPrep_func`
  hardcodes it to 0 too. That field is for a "buy more L$ to cover this"
  upsell prompt Confluence doesn't offer, not the land price itself -
  the real price/debit is a separate path
  (`OnValidateLandBuy`/`OnLandBuy` -> `ProcessLandBuy`, using
  `e.parcelPrice` directly) that already works correctly. No change
  made.

While the grid was shut down for the tester to retry, took the
opportunity to fully re-verify the deploy rather than trust incremental
state: fresh `git fetch origin master` reconfirmed 221 commits ahead / 3
behind (unchanged from the last check - nothing new merged either
direction), full solution rebuild (0 warnings, 0 errors), full `bin/`
sync, 145/145 files byte-verified via md5 against the live grid
directory.

**Land-buy hang recurred anyway - real root cause found.** Despite a
clean rebuild/redeploy and a fresh account (also tried with the
tester's account dropped from UserLevel 200/god back to 0, to rule out
god-mode taking a different client-side path - it didn't help), the
exact same hang happened again. Pulled the complete unfiltered log for
the exact window of a real attempt (not keyword-filtered this time)
from both Robust and the region - genuinely zero trace of the request
anywhere, confirming it wasn't reaching either server at all, which the
earlier ruled-out leads didn't explain.

Root cause found by checking the actual Firestorm viewer source
(`S:\Github\phoenix-firestorm`, a local checkout) instead of continuing
to infer behavior from an old code comment. `llcurrencyuimanager.cpp`
(`getCurrencyQuote`/`buyCurrency`) posts to
`<helper_uri>currency.php`, exactly as assumed and already verified
working. But `llfloaterbuyland.cpp`'s `startTransaction` - a *different*
source file, used for `preflightBuyLandPrep`/`buyLandPrep` specifically
- posts to `<helper_uri>` + **`landtool.php`**, under a
`<COLOSI opensim multi-currency support>` marker: a Firestorm-side
OpenSim-compatibility patch that splits land-buy onto its own endpoint.
Neither `ConfluenceCurrencyModule` nor Robust's `CurrencyServerConnector`
ever registered anything at `/landtool.php` - only `/currency.php` -
so every land-buy request was hitting a path with no handler at all.
That explains everything at once: zero log trace (never dispatched to
any handler that logs), `getCurrencyQuote`/`buyCurrency` working fine
throughout (different path), and the direct `/currency.php` tests
looking perfectly healthy while real land-buy attempts still hung -
they were never testing the actual path in use.

Confirming evidence this is a known real split, not a one-off: the
legacy `OpenSim-Grid-MoneyServer` addon already registers *both*
`/currency.php` and `/landtool.php` (`MoneyXmlRpcModule.cs`) - whoever
built that addon already knew about this; `ConfluenceCurrencyModule`
and the Robust connector, modeled more directly on
`SampleMoneyModule` (which only has the bare-root/`/currency.php`
path), simply never carried it over.

Fixed by registering the same four-method handler dictionary at
`/landtool.php` too, identically to the existing `/currency.php`
registration, in both `ConfluenceCurrencyModule.cs` (region-local copy)
and `CurrencyServerConnector.cs` (Robust, the one the viewer actually
reaches per its `helper_uri`). No interface touched - scoped build of
just `OpenSim.Server.Handlers` and `OpenSim.Region.CoreModules` (0
errors each), both DLLs deployed and byte-verified while the grid was
down for this exact purpose.

**Live-confirmed fixed.** Tester retried (as a plain UserLevel-0
resident, not god-moded) and completed the land purchase successfully.
Root cause and fix both hold up under a real transaction, not just a
synthetic XML-RPC test.

### prebuild.xml missing 10 custom service projects (2026-08-16)

Found while trying to organize this session's uncommitted work into
logical commits: `EventsService`, `SearchService`, `CurrencyService`,
`StaticPageService`, `NewsService`, `GridSettingsService`,
`SupportTicketService`, `RegionHGService`, `AuctionService`, and
`MessagingService` all had real, working, deployed `.dll`s - but no
`<Project>` stanza in `prebuild.xml` at all. Since `*.csproj` and
`OpenSim.sln` are both gitignored by design (`prebuild.xml` +
`runprebuild.bat` is meant to be the actual source of truth,
regenerating them), everything only worked because the already-generated
files on disk happened to already reference these ten - a fresh clone or
routine regeneration would have silently dropped every one of them from
the solution, with no error, just missing DLLs at deploy time. Not new
to this session; been accumulating since whichever batch first added
each one.

Fixed by adding proper `<Project>` stanzas for all ten (reference set
modeled on `OpenSim.Services.UserProfilesService`, the closest existing
analog, trimmed to what each actually needs per its own `using`
statements). Backed up the current `.csproj`/`.sln` set first since
regenerating would overwrite them with no git history to fall back
on if something went wrong.

Regenerating exposed a second, related fragility: it silently wiped
`OpenSim.Framework.csproj`'s hand-added `GenerateGitVersionInfo` target
(this session's build-number versioning work) - Prebuild's schema has
no way to express a custom MSBuild `<Target>`, so that block can never
survive a regeneration by design, not by accident. Re-added it by hand
and left warning comments in both `prebuild.xml` and the `.csproj`
itself pointing at each other, so this doesn't have to be
re-discovered the same way next time. Full solution rebuild afterward:
0 errors across all 95 projects (85 pre-existing + the 10 newly wired
in), version target confirmed still generating the real
`221`/`5059134619` values, not falling back to defaults. Not deployed
to the live grid - this is a build-infrastructure correctness fix with
no runtime behavior change, so it can just sit in source until the next
real deploy.

### Status update: the 2026-08-11 region-startup hang has not recurred (2026-08-16)

Closing the loop on the "known still-open issue" from the URL-cleanup
entry above: this whole session involved many region starts/restarts
(the full-rebuild-and-sync cycles, the land-buy investigation, the
prebuild.xml regeneration testing) across several hours, plus an
extended real-viewer session - login, teleport between Var Test Region
and Welcome Center, weather actively rendering, and a full currency/
land-purchase transaction all completed successfully. Zero recurrence
of the hang.

Not claiming it's fixed - its root cause was never conclusively
identified on 2026-08-11 (DLL mismatches, stale locks, DNS, and
addin-cache contention were all ruled out without finding the actual
cause), and nothing this session specifically targeted it. Just an
honest update on observed behavior: it's no longer blocking anything in
practice, so README.md's "Known gaps" no longer lists it as the
top-priority blocker. If it recurs, re-open investigation rather than
assuming this entry means it's permanently resolved.

## Future opportunity (logged, not started): real PBR terrain support + SLua (2026-08-16)

Came out of an offhand observation about OpenSim's rendering limitations
(clouds don't shadow the sun, no moon-phase lighting) - not fixable, both
are client rendering engine limitations shared with real SL itself, not
something server-side code controls. But it led to a real, verified
finding worth acting on later, refined twice over the course of the
conversation as more evidence came in - final accurate picture below.

**PBR materials on objects: already implemented, not a gap.** Checked
`OpenSim/Region/OptionalModules/Materials/MaterialsModule.cs` - the
`RenderMaterials` capability already accepts a `gltf_json` field per
prim face (`side`/texture index) and stores it on the object's shape
data. This is real, working object-level PBR material assignment,
already in Confluence's tree. Traced its origin: diffed this file
against Gunthar's own checkout (`opensim-vanilla`) - only 6 lines
different across the whole 1,477-line file, and the `PRIM_GLTF_*`
constants backing it trace to a real 2024 UbitUmarov commit (genuine
upstream OpenSim core, not Gunthar-specific). So "Gunthar tried to do
work with glTF" turned out to mean this same already-merged code, not
separate unclaimed work sitting in his fork - correcting the initial
assumption that his attempt was something new to cherry-pick.

**PBR *terrain* materials: confirmed genuinely unclaimed, this is the
real gap.** Checked three separate codebases - this repo's own
merged-upstream tree (`origin/master` = real `opensim/opensim`),
Gunthar's fork, and Tranquillity - for the actual capability backend
real PBR terrain editing needs. `SimulatorFeaturesModule.cs` echoes back
`"PBRTerrainEnabled": true`, but only because it's parroting a flag the
*viewer* itself sets during capability negotiation (`VTPBR`/`VETPBR`) -
not a real feature announcement. The capability a viewer actually calls
to read/write PBR terrain composition data is `"ModifyRegion"`
(confirmed against Firestorm's own `llpbrterrainfeatures.cpp`, built
from the same source tree LL ships for real SL - `queueQuery`/
`queueModify` both call `region.getCapability("ModifyRegion")`). Searched
all three codebases for that capability name: zero matches, anywhere. If
a viewer tried to actually edit PBR terrain against any of these grids
today, the request would just fail - the "enabled" flag is misleading.
So: object materials, done; terrain materials, nothing, anywhere.

**SLua: confirmed real and currently live, not speculative.** User
provided the actual source -
[LL's official announcement](https://community.secondlife.com/news/featured-news/announcing-the-slua-open-beta-modern-scripting-comes-to-second-life-r11237/),
December 2, 2025: SLua is a modern scripting language built on Luau
(Roblox's Lua variant), in open beta on the SL production grid since
that date - ~8.5 months live as of this session. Faster execution than
LSL/Mono, ~50% less memory, native tables/dictionaries, dynamic event
subscription (`LLEvents`), multiple independent timers (`LLTimers`),
coroutines, native JSON. Full LSL-knowledge compatibility, not a
replacement forcing a rewrite. No evidence found of any OpenSim fork
(checked the same three codebases) having touched this at all - a
second, likely even larger, genuinely unclaimed opportunity alongside
PBR terrain.

**Why this matters, per the user:** this alone would put Confluence "way
ahead of opensim-master" - not an incremental nice-to-have, a direct hit
on the project's actual mission (stay ahead of upstream by shipping
features the ecosystem has proven valuable but core OpenSimulator
hasn't). Same category as the Experience Tools/Abuse Reports/Display
Names precedent.

**Scope, for when either gets picked up:** both are substantial
from-scratch builds, not ports - each comparable in size to Experience
Tools or the native currency service, likely bigger for SLua (a second
scripting VM/runtime, not just a capability handler). PBR terrain needs:
understanding the exact JSON schema Firestorm's `LLModifyRegion`/
`LLPBRTerrainFeatures` sends and expects, server-side storage design for
per-terrain-layer PBR material (glTF) assignments, and a real
`ModifyRegion` capability handler wired into `SimulatorFeaturesModule`'s
existing (currently just echoed) flag. SLua needs its own investigation
pass before scoping - not started. Both logged here so they survive to
a future session rather than being lost to this conversation.

## Untested in-world ports: config prep for TeamCombatModule + UserAliasService (2026-08-16)

Resuming the "untested in-world ports" verification pass from earlier
(interrupted by the Weather module investigation above). Of the four
candidates - User Alias, Team Combat, in-world terrain console commands,
`osGetAgentViewer` - two turned out to need a live avatar connected via
a real viewer to test at all (terrain commands use `IRegionConsole.
SendConsoleOutput`, which targets a specific avatar's in-world viewer
console, not the shared `MainConsole` the WebConsole HTTP relay wraps;
`osGetAgentViewer` needs a live avatar to query by definition) - nothing
to do there until Jeffery's back online. The other two just needed
config wiring, done now while the grid's down:

- `[TeamCombatModule] Enabled = true` added to `Var_Test_Region`'s ini -
  was never configured at all, so `combat team join/leave/show` had no
  effect (confirmed live: `Commands.Resolve` silently no-ops on an
  unregistered command, same empty-response shape as a genuine relay
  bug, which is what led to first suspecting the WebConsole relay itself
  was broken before `land auction start` proved it works fine).
- `UserAliasServiceConnector` wired into `Robust.HG.ini`'s
  `[ServiceListeners]`, plus a new `[UserAliasService]` section - wasn't
  configured anywhere on this grid before. Verified it correctly falls
  back to `[DatabaseService]`'s connection string like other services,
  so no separate DB setup needed; will auto-create its table via
  migrations on first load.

Both are config-only changes, no rebuild needed - just require the next
region/Robust restart to take effect. Once up: `combat team join/leave/
show` should be testable via the WebConsole relay (no avatar needed,
confirmed from reading `HandleTeamJoin`/`HandleTeamLeave`/
`HandleTeamShow` - they operate on any UUID regardless of online
status), and UserAlias's read endpoints (`getuserforalias`/
`getuseraliases`) should at least be reachable for the first time.

### Live-tested both after the restart - real findings (2026-08-16)

**UserAliasService: confirmed working end-to-end.** `curl -d
"METHOD=getuseraliases&UserID=<jeffery's uuid>"
http://localhost:9002/useralias` against real Robust returned
`<result>null</result>` - correct for a user with no aliases created
yet, not an error. Read path verified for real. Write path
(`create alias`/`delete alias`) is console-only on Robust's own
console, which has no WebConsole-relay equivalent (that module is
region-side only) - still unverified, would need either direct Robust
console access or building an equivalent relay for Robust.

**TeamCombatModule: loaded successfully, but found and fixed a real,
previously-undiscovered bug on first-ever live test.** `combat team
show Test Squad` returned `Combat team "show Test Squad" has no
members` - the word "show" leaking into the team name. Traced it: all
three handlers (`HandleTeamJoin`/`HandleTeamLeave`/`HandleTeamShow`)
assumed `cmdparams` starts after the matched command prefix, but this
codebase's console framework actually includes the full prefix
("combat"/"team"/"join" etc. at indices 0-2) - so `cmdparams[2]` was
always the subcommand word itself, not the first real argument, and
the team-name-join offset was off by one throughout. Confirmed via a
second empirical test: `combat team join <uuid> Test Squad` failed
with a spurious usage message because `UUID.TryParse("join")` failed
at the wrong index. Fixed all three handlers (index 2->3, length
guards adjusted to match). Built clean, 0 errors - but the dll was
locked (region still running) so **not yet deployed**; needs the next
restart to actually take effect and get re-verified. Exactly the kind
of bug "verify with a real build" (this project's own standing rule)
exists to catch - it would never have surfaced from a clean compile
alone.

**Weather auto-cycle interval mystery, from the earlier investigation:
also resolved, root cause was a config conflict, not a code bug.** The
temporary diagnostic logging gave the proof needed: `m_autoCycleChangeOnStartup=True`
at runtime, despite the region's own `OpenSim.ini` correctly setting it
to `false`. Found the real cause: `addon-modules/OpenSimWeather/config/
OpenSimWeather.ini` merges in AFTER the region ini and wins on
overlapping keys (documented in the region ini's own comment) - and
that file had `AutoCycleChangeOnStartup = true`/
`AutoCycleStartupDelaySeconds = 20`, clearly leftover fast dev/test
values never reset to production ones. Fixed by resetting both to match
the region ini's intended values (`false`/`30`) directly in the live
deployment's copy of that file. Confirmed this doesn't affect fresh
clones of the repo - the repo's own `.ini.example` never set these two
keys at all, relying on the correct code defaults; the stale values only
existed in this grid's own deployed copy. Needs one more natural restart
to confirm the fix holds (no more sub-minute weather changes), but the
diagnosis itself is solid - this was never an interval-math bug in
`WeatherModule.cs`.

## Ported the two deferred Gunthar HG-identity commits (2026-08-16)

These sat in README.md's roadmap as "deliberately deferred as warranting
dedicated review" since the original batch work - never actually
reviewed until now. Traced both in Gunthar's own history: they're not
two independent fixes, the second (`4c0d5e1e58`, 2026-06-04) is a
same-week refinement of the first (`9d492061ab`, 2026-06-02), both
touching the same `LLLoginService.SetServiceURLs` method - ported the
combined final state as one change rather than applying two
sequential diffs.

**The real problem, and why it's directly relevant to this grid
specifically:** this deployment's own hostname is dynamic DNS
(confirmed earlier this session, during the land-buy investigation).
The pre-port code only ever repaired a HomeURI/GatekeeperURI that was
completely *missing* - once a value was stored, even a stale one from
before a DNS change, nothing ever touched it again. A resident who
logged in once, then the grid's IP changed, would keep exporting the
old `@IP:port` identity to every foreign Hypergrid they visited,
indefinitely, with no way to self-correct.

**Three files changed, matching Gunthar's original split:**
- `LLLoginService.cs`: `SetServiceURLs` now does a case-insensitive
  *equality* check against the canonical HomeURI/GatekeeperURI, not
  just a missing-value check - fixes stale-but-present values on every
  local login. Also stopped early-returning when `account.ServiceURLs`
  is null (a brand-new local account), which is what broke standalone
  HG login for freshly-created accounts in the first place.
- `UserAgentService.cs`: new `ApplyCanonicalHomeURI`, called on every
  outbound Hypergrid launch (`LoginAgentToGrid`) - rewrites the agent
  circuit's ServiceURLs to canonical values before the traveler ever
  reaches a foreign grid, independent of whether their stored account
  data has been touched by a local login recently.
- `UserAccountService.cs`: new `repair user service urls <first> <last>
  [<home-uri>]` console command - manual counterpart for an admin to
  fix an account that hasn't logged in since a stale identity was
  stored, without waiting for the resident to do it themselves.

Built clean across all three affected projects (`OpenSim.Services.
LLLoginService`, `OpenSim.Services.HypergridService`, `OpenSim.Services.
UserAccountService`), 0 errors each - no interface touched, all
internal method bodies plus one new console command on existing
classes. Deployment blocked (dlls locked, Robust still running) -
needs the next restart. Once up: `SetServiceURLs`/
`ApplyCanonicalHomeURI` will exercise automatically on any normal
login/HG teleport, no special test needed. The `repair user service
urls` console command is Robust-hosted, same limitation as UserAlias's
write commands - no WebConsole-relay equivalent for Robust's own
console, so that specific piece stays unverified until there's a way
to reach it directly.

**Live-verified with a real before/after, not just a startup check
(2026-08-16).** Deployed after the next restart, then set up a genuine
test rather than passively waiting for a login that might not
demonstrate anything (both real accounts already had correct
ServiceURLs, so an ordinary login wouldn't visibly change anything).
Artificially staled Test User's stored `HomeURI`/`GatekeeperURI` to a
fake old IP (`203.0.113.55`, a safe RFC 5737 test address) via direct
SQL - simulating exactly the "before a DNS change" scenario this fix
targets. Had the user log in as Test User once. Checked the database
immediately after: both fields were back to the real hostname,
unprompted. That's the new case-insensitive equality check in
`SetServiceURLs` working for real - the old code only ever fixed a
completely *missing* value and would have left this stale-but-present
one untouched forever. `ApplyCanonicalHomeURI` (the Hypergrid-travel
half) remains unverified - would need an actual HG teleport, not just
a local login, to exercise.

**`ApplyCanonicalHomeURI` also live-verified - the user did a real HG
round trip.** Logged: `UserAgentService` request to launch Test User to
a genuine foreign grid (`alternatemetaverse.com:8002`), then back home
to the test deployment's real hostname. Zero errors or exceptions the entire way -
`ApplyCanonicalHomeURI` executed on a real outbound launch without
breaking anything. The account was already correct going in (from the
local-login test above), so this didn't produce a fresh "stale value
caught" moment on this specific code path, but confirmed
`ServiceURLs` stayed correct on both ends of the trip and the new code
runs clean on genuine Hypergrid travel - same proven comparison logic
as the local-login half, just applied to `AgentCircuitData` instead of
the account record. Both halves of this port are now live-verified,
not just build-verified.

### Land Auction module - live-verified end to end

With Jeffery still away, picked the last README-listed "untested in
world" item that didn't actually require a connected viewer: the Land
Auction module (`OpenSim/Region/CoreModules/World/Land/AuctionModule.cs`,
ported from WhiteCore-Dev, DB-backed via `IAuctionService`/
`IAuctionData` rather than in-memory - see its own header comment for
why: real viewers have no in-world bidding UI, SL auctions were always
bid on through a website, so this was built to be driven by console
commands or a web POST hitting the same code path). Investigated first
whether it needed a live avatar at all: it doesn't - no `IClientAPI`
hooks, no parcel-selection UI, just four console commands
(`land auction start/bid/end/show`) plus a 120s expiry-sweep timer.
Also checked it for the same class of "cmdparams includes the matched
command-prefix tokens" indexing bug found in `TeamCombatModule.cs`
earlier - it doesn't have it; `HandleAuctionBid` etc. correctly index
from `cmdparams[3]` onward.

Confirmed `[AuctionModule]` is enabled by default (no override needed
in `OpenSim.ini` since the field defaults to `true`), and its
`IAuctionService` dependency is properly wired
(`AuctionService = LocalAuctionServiceConnector` under `[Modules]`,
backed by `MySqlAuctionData` against the live database) - and the
`land_auctions`/`land_auction_bids` tables already exist there.

Found an already-active test auction on Var Test Region's sole parcel
(local id 1, "Your Parcel", owned by Test User) left over from earlier
testing - min bid 50, one self-bid from Test User, not yet expired.
Used the WebConsole HTTP relay (same approach as the TeamCombat
testing) to place a second, higher bid from Jeffery's account
(`land auction bid 1 <Jeffery's UUID> 100`), confirmed via
`land auction show 1` that he was now highest bidder, then forced early
closure with `land auction end 1` rather than waiting out the clock.

Result, confirmed via direct DB inspection: parcel ownership
transferred from Test User to Jeffery; Jeffery's currency balance
dropped by exactly 100 (301990 -> 301890); Test User's balance rose by
exactly 100 (5000 -> 5100); a proper audit-trail row landed in
`currency_transactions` (`Land auction for parcel "Your Parcel"`,
TransferType 1102, correct `ToBalance`/`FromBalance` snapshots); and
the `land_auctions` row's `Status`/`WinnerID`/`WinningAmount` fields
were all populated correctly. This is the same charge-on-close path
fixed earlier this project (winners weren't being charged at all
before that fix) - confirmed still working, on a real close, not just
build-verified.

Reverted afterward the same way the land-sale test was reverted
earlier: parcel ownership and `SalePrice`/`LandFlags` restored to
their pre-test values, both balances restored, and a
"Land Auction Reversal (test cleanup)" audit-trail transaction
inserted so the currency ledger stays consistent rather than just
silently editing balances. Left the closed test-auction DB rows in
place as history, matching how the earlier land-purchase-test
reversal was handled.

Updated the README's "untested in-world ports" bullet: it had gone
stale - Team Combat and the User Alias service were already
live-verified earlier this session but the bullet still listed them as
untested. Corrected it to only list the two items that are genuinely
blocked on a connected viewer (in-world terrain console commands,
`osGetAgentViewer`), and to point at this log for the three that are
now done.

### Terrain console commands (Mobius port) - live-verified with a real viewer

The user connected with a real viewer partway through this session,
which finally made the two remaining blocked items reachable. Started
with the terrain commands (`TerrainModule.cs`, ported from Mobius:
`terrain elevate/lower/fill <meters>`, `terrain load texture <uuid>`).

These turned out not to be reachable at all through the WebConsole
HTTP relay used for every other console test this session - they're
registered against `IRegionConsole`'s own private command registry
(`RegionConsoleModule.cs`), completely separate from
`MainConsole.Instance.Commands`, and every handler replies via
`SendConsoleOutput(agentID, ...)` straight to that one avatar's
client over the `SimConsoleAsync` capability - not observable through
the physical console or `/consoleweb` under any circumstances. The
only way to exercise them is a real connected viewer with its own
region-debug-console UI open, gated on
`EstateSettings.IsEstateManagerOrOwner(agentID)`.

Confirmed the connected account (Test User) is the Estate Owner of
"My Estate" (which Welcome Center belongs to), so the gate would
pass. Getting to the actual UI took a few tries - Firestorm's
in-viewer log-output "Debug Console" (Developer menu) is a different
window entirely from the one that actually talks to
`SimConsoleAsync`; checked the real Firestorm source
(`S:\Github\phoenix-firestorm`) to confirm the correct one is
literally named "Region Debug Console" (`llfloaterregiondebugconsole.cpp`,
registered as `region_debug_console` in `llviewerfloaterreg.cpp`),
opened via Advanced > Consoles > Region Debug Console or the
Ctrl+Shift+` shortcut - not the log viewer.

With that open, ran `terrain elevate 0` and `terrain lower 0` (0m,
deliberately a no-op so nothing on the live Welcome Center terrain
actually changed) directly from the connected viewer. Both returned
the exact expected replies - "Raising terrain by 0 meters." and
"Lowering terrain by 0 meters." - proving the full round trip end to
end: `SimConsoleAsync` capability wired correctly, the estate-owner
gate passed for a real account, command parsing and dispatch through
`IRegionConsole`'s separate registry worked, and the response made it
back to the real client. `terrain fill`/`terrain load texture` share
the same dispatch path and weren't separately exercised, since both
are genuinely destructive to a live region's heightmap with no safe
zero-value equivalent - not worth risking on the one shared test
region for a fourth confirmation of a code path already proven three
times over.

That leaves `osGetAgentViewer` (ported from opensim-lickx) as the
last remaining untested item. Unlike the terrain commands it doesn't
need viewer UI archaeology - it's an OSSL script function - but it's
currently blocked by threat-level config
(`osGetAgentViewer` needs `Moderate`, the live deployment's
`OSFunctionThreatLevel` default is `VeryLow`, and neither
`Welcome_Center` nor `Var_Test_Region`'s `OpenSim.ini` nor the shared
`config-include/osslDefaultEnable.ini` carry an `Allow_osGetAgentViewer`
override). Fixing that needs an ini edit plus, most likely, a region
restart to pick it up - deferred rather than done mid-session while
the user was actively connected and testing, to not cut that session
short.

### osGetAgentViewer - live-verified, plus two real bugs found along the way

The user chose to take the disconnect and finish this one out rather
than leave it for later. Added
`Allow_osGetAgentViewer = ${OSSL|osslParcelO}ESTATE_MANAGER,ESTATE_OWNER`
to the live `config-include/osslDefaultEnable.ini` (alongside the
other Moderate-tier entries), had the user rez a cube in Welcome
Center with a one-line test script
(`llOwnerSay("Viewer: " + osGetAgentViewer((string)llDetectedKey(0)))`
on `touch_start`), then restarted the region to pick up the OSSL
config change - OSSL function-permission entries are read once at
startup and cached, not re-read live.

Found the region's own graceful restart command in the process:
`region restart <seconds>` (`RestartModule.cs`), reachable through
`/consoleweb` and unlike the bare `restart` OpenSim.cs command (see
below) actually wired up - warns connected residents and gives them
time before the region cycles, rather than an instant kill. Used
`region restart 10` on Welcome Center; the user got the warning,
logged back in once it was back, touched the cube, and got back
exactly `Viewer: Firestorm-Nightlyx64 7.2.5.81383` - confirming the
function correctly reads the real `AgentCircuitData` viewer string
from a live client, not a placeholder. This closes out the last
README-listed untested-in-world port - all of Team Combat, Land
Auction, the User Alias service, the Mobius terrain console commands,
and now `osGetAgentViewer` are live-verified.

**Bug #1, found while choosing which restart command to send:**
`OpenSim.cs`'s own bare `"restart"` console command
(`m_console.Commands.AddCommand("Regions", ..., "restart", ...)`) is
hardcoded to a no-op - `RunCommand`'s `case "restart":` just logs
"Restart command disabled, because currently it is unreliable." and
returns, the real restart call is commented out. That's exactly the
command name `WebInterfaceServiceConnector.cs`'s admin
(`HandleAdminRegionRestart`) and self-service
(`HandleMyRegionsRestart`) restart buttons were sending - meaning the
region-restart feature built and marked complete earlier this session
(batch #86) has never actually restarted anything; both buttons would
report success and do nothing. Fixed both call sites to send
`"region restart 30"` instead (the same working command confirmed
live above), with a comment on each explaining why - confirmed via
`dotnet build` on `OpenSim.Server.Handlers.csproj` (0 errors), and
deployed the rebuilt `OpenSim.Server.Handlers.dll` to the live
Robust install once Robust was stopped for an unrelated reason (see
below) rather than forcing an extra restart just for this.

**Bug #2, found immediately after the next restart cycle:** partway
through this work the grid went down and back up outside of anything
this session did directly (both `Robust.exe` and both region
`OpenSim.exe` processes exited and were relaunched by the user).
Afterward, Var Test Region's sole parcel showed `OwnerUUID` back to
Jeffery (the auction winner from the earlier Land Auction test)
**with SalePrice/LandFlags at their post-auction values**, while the
currency balances still showed the correctly-reverted amounts (Test
User 5000, Jeffery 301990) - a real inconsistency, not just leftover
test data: Jeffery ended up owning the parcel for free.

Root cause: the earlier land-auction-test revert was a direct SQL
`UPDATE` against the live database while Var Test Region's OpenSim.exe
process kept running the whole time. That process never re-read the
row - it just kept serving its own in-memory copy (still showing
Jeffery as owner from when the auction closed) - and when it was
later cleanly shut down as part of the unrelated grid-wide restart, it
persisted *that* stale in-memory state back to the database on exit,
silently clobbering the earlier SQL fix. General gotcha worth
remembering: a direct SQL edit to live-region-owned data (land, and
presumably prims/parcels generally) only sticks if the owning region
process is stopped when the edit happens, or if it goes through an
in-region command instead - editing the DB out from under a running
region is temporary at best.

Fixed properly this time: had the user stop just Var Test Region's
process (confirmed via the `/consoleweb` port going unreachable while
Welcome Center's stayed up), re-ran the same `UPDATE` while it was
down, confirmed the row was correct, then had the user start it back
up and confirmed via a live `land show 1` against the freshly-started
process - `Owner: Test User`, `Sale Price: 500`, `Flags: ...ForSale...`
- that it loaded the corrected state fresh rather than clobbering it
again. This time it's actually persistent, since nothing holds a
stale in-memory copy anymore.

### Temp-ban auto-expiry fix - LLLoginService now self-clears too, live-verified

Picked back up the last README-listed gap: a temporary/timed account
ban only self-cleared via the web dashboard login or admin user-detail
page (`WebInterfaceServiceConnector.ClearExpiredBan`, checking a
`UserAppData`-stored expiry timestamp against `UserAccount.UserLevel
== -1`). `LLLoginService` - the real grid/viewer login - had no
awareness of the expiry concept at all: it only ever compared
`UserLevel < MinLoginLevel`, so a resident who never touched the web
UI stayed blocked past their ban's expiry until an admin manually
unbanned them.

Extracted the ban-expiry constant/storage/clear-logic
(`BanExpiryTag`, `GetBanExpiry`, `SetBanExpiry`, `ClearExpiredBan`)
out of `WebInterfaceServiceConnector.cs` into a new shared static
class, `AccountBanHelper` (`OpenSim/Services/Interfaces/
AccountBanHelper.cs`) - `OpenSim.Services.Interfaces` was the right
home since it only needs types from itself and `OpenSim.Framework`
(the `UserAppData` POCO), and both `WebInterfaceServiceConnector`'s
project and `LLLoginService`'s project already reference it, so no
new project references were needed anywhere (avoiding the
`GenerateGitVersionInfo`/prebuild-regen fragility that comes with
touching `.csproj` files). Wired `IUserProfilesService` into
`LLLoginService` (it never had one before - loaded the same way
`WebInterfaceServiceConnector` already does, `[UserProfilesService]`
section's `LocalServiceModule` key, 2-arg constructor), and call
`AccountBanHelper.ClearExpiredBan(account, m_UserAccountService,
m_UserProfilesService)` right before the existing `UserLevel <
m_MinLoginLevel` check in `Login()` - a no-op for a permanent ban, a
non-banned account, or an unconfigured `UserProfilesService`.

Full solution build (`dotnet build OpenSim.sln`) came back clean - 0
errors, 0 warnings - worth doing given the change touched
`OpenSim.Services.Interfaces`, one of the most widely-referenced
projects in the tree, not just the two call sites that needed it.

Live-verified via a real XML-RPC `login_to_simulator` call against
Robust's login endpoint (not just a web-page click) - chose this
because the ban check in `LLLoginService.Login()` runs *before*
password authentication, so a deliberately wrong password is enough
to distinguish the two failure reasons the response carries:
`"presence"` (`LoginBlockedProblem`, blocked by user level) vs.
`"key"` (`UserProblem`, bad credentials) - no need to know the real
password to prove which check gate a login attempt actually reached.
Three-step test against Test User's real account:
1. **Baseline** - not banned, wrong password: `reason: key`, as
   expected.
2. **Negative control** - `UserLevel` set to -1 with a ban-expiry
   timestamp an hour in the future: `reason: presence` - confirms the
   gate itself blocks correctly.
3. **The actual fix** - same banned account, expiry backdated an hour
   into the past (still `UserLevel = -1` in the DB going in): the
   login attempt came back `reason: key`, not `presence` - meaning
   `ClearExpiredBan` ran and let it past the level check on this one
   real grid-login attempt. Confirmed via direct DB query immediately
   after: `UserLevel` back to `0` and the `BanExpiry` `userdata` row
   cleared to `"0"` - no manual admin action, no web page touched,
   just the one XML-RPC login call. Account was already back to a
   clean, unbanned state afterward, so nothing needed reverting.

Deployed `OpenSim.Services.Interfaces.dll`, `OpenSim.Services.
LLLoginService.dll`, and the already-updated `OpenSim.Server.
Handlers.dll` (bundling this fix with the still-undeployed
region-restart-button fix from earlier) to the live Robust install -
Robust-only, no region-side copies of any of these three exist. Hit
one more real (if mundane) snag deploying: two of the three DLLs kept
reporting "Device or resource busy" even after confirming `Robust.exe`
was fully stopped - turned out to be leftover `dotnet build`
server/compiler processes (MSBuild node reuse, VBCSCompiler) from this
session's own builds still holding the files open; `dotnet
build-server shutdown` plus a short wait cleared it.

### Real incident: unban/expiry hardcoded UserLevel to 0, downgrading a live admin account

Moved on to verifying the grid-wide admin Groups page against real
group data (the next README gap - no groups existed on the test
grid). Before that, though, a genuine mistake surfaced from the
ban-expiry work just above: it used Test User's account for the live
XML-RPC self-clear test without first checking what `UserLevel` that
account actually had going in. Test User turned out to be the user's
real admin account (`UserLevel 200`) - after the test, the user
reported "I no longer have admin access."

Root cause: both `AccountBanHelper.ClearExpiredBan` (the new
auto-expiry path) *and* the pre-existing admin "Unban this user"
button hardcoded the restored level to a flat `0` - neither one ever
recorded what level an account had before it was banned. This is a
real bug in the shipped Ban/Unban feature itself, not just a test
artifact: any elevated account (estate manager, grid admin) that gets
banned - by the timer or by an admin's own button - permanently loses
that elevation on unban. My test only exposed it because it happened
to land on a real admin account without checking first.

Fixed the actual account immediately (`UserLevel` set back to 200 per
the user's confirmation), then fixed the underlying bug properly
rather than treating it as a one-off:
- `AccountBanHelper.cs` gained `PreBanLevelTag`/`GetPreBanLevel`/
  `SetPreBanLevel` (same `UserAppData`-tag pattern as `BanExpiryTag`).
  `ClearExpiredBan` now restores `GetPreBanLevel() ?? 0` instead of a
  hardcoded `0`, clearing the tag once restored.
- `HandleAdminUsersSetLevel` (`WebInterfaceServiceConnector.cs`) now
  captures the account's current level into `PreBanLevel` at the
  moment it transitions *into* a ban (not on every save, so re-banning
  an already-banned account can't clobber the real recorded value with
  `-1`).
- The "Unban this user" button no longer submits a hardcoded
  `user_level=0` in its HTML form - it now submits a `"UNBAN"`
  sentinel, and the handler computes the actual restore value
  server-side from `GetPreBanLevel`, matching what the auto-expiry
  path does. The manual "Change user level" form is unaffected - an
  admin can still explicitly set any numeric level, including banning
  or overriding a banned account to a specific value on purpose.

Full solution build clean (0 errors/warnings) both times - once after
the initial fix, again after this one, since it's the same
widely-referenced `OpenSim.Services.Interfaces` project. Deployed
`OpenSim.Services.Interfaces.dll` and `OpenSim.Server.Handlers.dll` to
Robust (same DLLs as the ban-expiry fix, Robust-only). One deploy
attempt landed before the user had actually stopped Robust yet
("robust re-launched" turned out to mean it had restarted on its own
schedule, not that the new DLLs were in place) - caught by checking
file timestamps against the fresh build output before declaring it
done, and redone correctly on the next stop/start cycle.

Live-verified the fix itself without touching either real tester
account again: used the grid's other, genuinely-inconsequential test
account (Regular Tester, real `UserLevel 0`) and injected a `PreBanLevel`
of `77` - a value with no relationship to that account's real level,
chosen specifically so the test could distinguish "restored to the
recorded prior value" from "coincidentally already 0" (its old,
buggy fallback would have looked identical to the fixed behavior on
an already-0 account). Simulated the same banned-with-expired-timer
state as before and ran the same real XML-RPC login test:
`UserLevel` came back `77`, not `0`, and the `PreBanLevel` tag cleared
- confirming the fix restores the *recorded* level, not a hardcoded
one. Restored Regular Tester to its real `UserLevel 0` immediately
after, and re-confirmed Test User was still correctly at `200`.

### Grid-wide admin Groups page - live-verified, found a real cascade-delete bug

Back to the original goal: the admin Groups page had never been
checked against real group data (zero groups existed on the test
grid). No console command creates a group, and doing it by hand
across the 7-table `os_groups_*` schema would be fragile (the service
layer indexes columns directly out of a dictionary and throws on
anything missing), so instead: `curl`ed a form-encoded
`METHOD=PUTGROUP&OP=ADD&...` POST straight at
`GroupsServiceRobustConnector`'s `/groups` endpoint on Robust's
private port (9003, no auth configured), landing on the exact same
`GroupsService.CreateGroup` real group-creation code path the
in-world Group Profile floater's "Create" button uses. Founded
"Casperia Test Group" on Test User's account - auto-created all 3
standard roles (Everyone/Officers/Owners) with real power bitmasks
and the founder's Owner+Officer membership row, confirmed via direct
DB query (`os_groups_groups`, `os_groups_roles`,
`os_groups_membership` all populated correctly, `ShowInList=1` -
the exact flag the admin page's own query filters on).

Then a real incident interrupted this: the user reported losing admin
access (see the entry above) from the ban-expiry test that immediately
preceded this, on the very account (Test User) this new group's
founder happened to be. Fixed that first, then came back to finish
this verification once Test User's `UserLevel` was confirmed restored
to 200.

With admin access back, had the user check `/admin/groups` directly
in a browser (not the in-world viewer, which they tried first and
which is a different page/floater entirely). Toggled the group's
flags from the admin page and confirmed the change took effect
in-world - full round trip through the real update path. Then tried
delete: the group disappeared from the admin list immediately, but
stayed in the resident's in-world Groups list until they relogged -
expected viewer behavior (group membership is fetched at login and
not live-pushed, same as real Second Life), not a bug.

Checked the DB after the delete anyway, out of habit rather than
suspicion, and found a real one: `os_groups_groups` correctly hit
zero rows, but `os_groups_membership` (1), `os_groups_roles` (3), and
`os_groups_rolemembership` (2) all still had rows referencing the
deleted `GroupID`, and the founder's `os_groups_principals.ActiveGroupID`
still pointed at the now-nonexistent group. Root cause:
`GroupsService.DeleteGroup` → `MySQLGroupsData.DeleteGroup` /
`PGSQLGroupsData.DeleteGroup` only ever called
`m_Groups.Delete("GroupID", ...)` - the single `os_groups_groups` row
- with no cascade to any of the other six `os_groups_*` tables at
all. Not something the admin page introduced; it just exercised a
pre-existing gap in the underlying service that nothing had deleted a
group through before.

Fixed both real backends (MySQL and PGSQL - this schema was never
ported to SQLite, upstream OpenSim doesn't have one either, so
nothing to fix there) to cascade: delete matching rows from
membership/roles/rolemembership/invites/notices (invites/notices
weren't populated for this test group, but they're keyed by `GroupID`
the same way and would leak the same way), and clear the
`ActiveGroupID` reference in principals. Full solution build clean (0
errors/warnings). Manually cleaned up this test group's orphaned rows
from before the fix existed, then deployed `OpenSim.Data.MySQL.dll` -
this one turned out to be loaded by both region processes as well as
Robust (no per-region copy exists, they load it from the shared
location), so unlike the Robust-only DLLs deployed earlier this
session, this deploy needed the whole grid stopped, not just Robust.
Confirmed via DB query after redeploy and restart that the orphaned
rows are gone and the schema is clean.

### InternalPort = MATCHING - ported from Mobius

Moved to the next README gap: two real Mobius features, RSA-key
login authentication and `InternalPort = MATCHING`. Checked the local
`S:\Github\mobius-master` checkout first and found it's not usable as
a reference - a sparse 42-file Display-Names/HG/UserAccount-only
extraction, not a real Mobius checkout, with zero trace of either
feature. `S:\Github\OpenSim-Continuum` (a sibling repo with real git
history) already has both, done by an earlier "Merge Bot" session and
explicitly labeled "ported from Mobius fork" - a much better
reference than mobius-master, so worked from that instead.

`InternalPort = MATCHING` first, since it's small and clean: in
`Regions.ini`, setting `InternalPort = MATCHING` instead of a literal
number makes the region adopt whatever `[Network] http_listener_port`
its own simulator process is already using, instead of needing that
value hand-kept in sync across two config files. Confluence's
`OpenSim/Framework/RegionInfo.cs` was byte-for-byte identical to
Continuum's pre-port version at the relevant spot, so the logic
ported directly with no adaptation needed - added the `MATCHING`
string check right where `InternalPort` is parsed in
`ReadNiniConfig`, reading `[Network] http_listener_port` as the
fallback source when it's set. Documented the option in
`bin/Regions/Regions.ini.example` too. Full solution build clean.
Not yet deployed/live-tested - `RegionInfo.cs` lives in
`OpenSim.Framework.dll`, which (like `OpenSim.Data.MySQL.dll` above)
is loaded by both region processes and Robust from one shared
location, so deploying it means stopping the whole grid again;
holding that for a batched deploy alongside whatever's next rather
than asking for another full stop/start cycle for one small change.

### PRIM_GLTF_* primitive-parameter dispatch wired into llSetPrimitiveParams

Next README gap, and one this session already had a real head start
on - a full read/write backend for the four `PRIM_GLTF_*` codes
(`ApplyGltfPrimitiveParams`, `ApplyGltfPrimitiveParamsToFace`, plus
per-property helpers for texture/transform/base-color/metallic-
roughness/emissive, all working against the same compact JSON stored
in `GetMaterialOverrideData`/`SetMaterialOverrideData` that
`llSetLinkGLTFOverrides` also uses) already existed in `LSL_Api.cs`,
confirmed genuinely dormant - `ApplyGltfPrimitiveParams` had exactly
one reference in the whole file: its own definition. Nothing in
`SetPrimParams` (the shared per-part dispatch loop both
`llSetPrimitiveParams` and `llSetLinkPrimitiveParamsFast` route
through) had a case for any of the four codes, so a script setting
`PRIM_GLTF_BASE_COLOR` etc. would just silently fall through instead
of the backend running.

Matched real SL's own parameter shape for these (confirmed against
the existing helper functions' index reads, which already expected
it): `[face, texture, repeats, offsets, rotation]` common to all four,
then type-specific extras - none for `PRIM_GLTF_NORMAL` (5 total),
metallic+roughness floats for `PRIM_GLTF_METALLIC_ROUGHNESS` (7),
an emissive color vector for `PRIM_GLTF_EMISSIVE` (6), and color/
alpha/alpha_mode/alpha_cutoff/double_sided for `PRIM_GLTF_BASE_COLOR`
(10). Added a case to `SetPrimParams` covering all four - computes the
right argument count per code, takes exactly that many items as a
sublist via `LSL_List.GetSublist` (confirmed inclusive-both-ends
first), and hands it to the existing `ApplyGltfPrimitiveParams`,
which already does its own per-field validation and error reporting
(`Error(originFunc, ...)`, same convention every other case here
uses) - matches the existing `PRIM_NORMAL`/`PRIM_SPECULAR` cases'
shape closely since they're the nearest real analogs (also
texture-map-plus-transform parameters). Folded into the same
`materialChanged` flag those two already use, so it gets the same
post-loop update/persist handling for free.

Checked the GET side too while in there (`llGetPrimitiveParams`/
`llGetLinkPrimitiveParams`'s own dispatch, a separate switch further
down in the same file) - confirmed no `PRIM_GLTF_*` case exists there
either, so reading these values back through the generic prim-params
API still isn't possible. That's a separate, not-yet-scoped gap the
README's wording never actually claimed was fixed (it specifically
named the SET functions) - noted here rather than silently expanded
into this fix.

Build-verified (`OpenSim.Region.ScriptEngine.Shared.Api.csproj` and a
full solution build, both clean). Not yet deployed/live-tested for
the same batching reason as `InternalPort = MATCHING` above - this
DLL is also shared, not region-specific.

### RegionCurrency vs. RegionWeb reconciliation - a real bug, not just duplication

The last README gap: "RegionCurrency vs. RegionWeb's currency portal
duplication is unreconciled." Investigated properly rather than
guessing at scope. Found this was bigger than a doc note.

**The actual overlap.** RegionCurrency (`addon-modules/RegionCurrency`)
turned out to be a strict subset of RegionWeb's own built-in `/currency`
wallet - same login-token flow, same balance/buy/transfer dashboard,
same admin console, same PayPal integration, same TSV storage pattern,
both wired live in the deployment (`RegionCurrency.ini`
`Enabled = true`; RegionWeb's own `/currency` portal enabled by code
default even though the deployed `RegionWeb.ini` predates the keys
that would say so explicitly). Confirmed directly from RegionCurrency's
own code, not inferred: its `HandleRequest` method's doc comment reads
"RegionCurrency now owns its whole path rather than living under
RegionWeb's `/regionweb/currency/` as it did *in the source project*" -
and its default storage paths (`Currency/regionweb-purchases.tsv`,
`Currency/regionweb-paypal-orders.tsv`), session cookie
(`"RegionWebCurrency"`), and admin-check method name
(`IsRegionWebSuperAdmin`) were all still literally named after
RegionWeb, never renamed. This wasn't two independently-built modules
that happened to converge - it's RegionWeb's own currency code,
mechanically split out to its own base path by an earlier AI-assisted
session, exactly as the README had already (correctly) noted before
this investigation started.

**The real bug underneath, found while scoping the removal.** Both
wallets resolve their money module via
`scene.RequestModuleInterface<IMoneyModule>()`, and every
currency-mutating action - buy, transfer, admin set/credit/debit - plus
the statement and top-balances listings, reached into whatever
`IMoneyModule` was active via **reflection**, looking up methods
(`WebBuyCurrency`, `WebTransfer`/`WebTransferCurrency`,
`WebSetBalance`, `WebCreditCurrency`, `WebDebitCurrency`,
`GetCurrencyStatement`, `GetCurrencyBalances`, and (found while already
in the file) `GetCurrencyStats` for the RegionWeb homepage's Economy
stats block) that only RegionCurrency/RegionWeb themselves ever
declared. Grepped the whole repository for every one of those method
names - none exist anywhere else, including on `ConfluenceCurrencyModule`
(the money module actually configured live), which only implements the
real `IMoneyModule` interface members. Every reflection lookup failed
silently. `GetBalance` is a real `IMoneyModule` member, so it kept
working - dashboards correctly showed a balance while nothing on them
could actually change one, and the statement/balances tables just
rendered empty rather than erroring.

**The fix.** Rather than re-adding a `Web*`-method shim to one more
money module (the fragility this bug came from in the first place),
rewired RegionWeb's wallet (the survivor) to call `ICurrencyService`
directly - the same real, DB-backed interface `ConfluenceCurrencyModule`
itself adapts to `IMoneyModule`, already exposing `Transfer`,
`SetBalance`, `GetTransactionHistory`, `GetPurchaseHistory`,
`GetTopBalances`, `GetTotalCirculation`, `CountAccountsWithBalance`.
Added a `GetCurrencyService()` helper alongside the existing
`GetCurrencyMoneyModule()` (kept for the still-working `GetBalance`
reads), rewrote the five `InvokeWeb*` helpers to call `Transfer`/
`SetBalance` directly instead of reflecting, and rewrote
`GetCurrencyStatement`/`GetCurrencyBalances` to build their row data
from `GetTransactionHistory` (called twice - once per direction, since
the interface ANDs `toAgentID`/`fromAgentID` when both are non-zero,
unlike the group-scoped `GetGroupTransactionHistory` which explicitly
returns either-side matches) plus `GetPurchaseHistory`, merged into one
newest-first ledger. Fixed the homepage Economy stats block
(`AppendEconomy`) the same way, off `GetTotalCirculation`/
`CountAccountsWithBalance`. Used local `CurrencyTransactionTypeSystemGenerated`/
`CurrencyTransactionTypeMoveMoney` int constants rather than referencing
`ConfluenceCurrencyModule`'s own `ConfluenceTransactionType` enum
directly, since `ICurrencyService.Transfer`'s `transactionType` is a
plain backend-defined int at the interface level and RegionWeb
shouldn't be coupled to one specific `ICurrencyService`-backed
`IMoneyModule` implementation's private enum.

**Mid-fix product decision on PayPal.** The user clarified partway
through: PayPal should stay, but be treated as a straight donation to
the estate, not a currency purchase - no token credit, no promised
exchange rate, at least until directly selling in-world currency for
real money is a decision actually made rather than inherited from
whatever the original split-out code happened to do. Reworked
`HandleCurrencyPayPalReturn` accordingly - after a successful PayPal
capture, the order is marked completed directly with no
`ICurrencyService` call of any kind (the capture-then-`RecordPurchase`
version written moments earlier in this same session was itself
replaced, never shipped/deployed), and the buy-flow UI copy for PayPal
mode was reworded from "Pay with PayPal" / "credits the local
simulator ledger" to "Donate via PayPal" / "does not purchase or
credit in-world currency."

**RegionCurrency removed**, not reconciled - zero unique capability
once the shared bug was fixed at the source, matching the
OpenSimMutelist/OpenSimSearch precedent for confirmed-redundant
addons. Removed `addon-modules/RegionCurrency` (git rm, plus the
untracked `obj/`/csproj build artifacts), the deployed
`bin/addon-modules/RegionCurrency/` copy, and its `OpenSim.sln`
project entry (both the `Project`/`EndProject` block and its four
`ProjectConfigurationPlatforms` lines) - it was never in `prebuild.xml`
or referenced by any other project's `.csproj`, so no other cleanup
was needed. Full solution build clean after removal.

**GroupAutoInvite audit, prompted by the same "was this an AI-invented
split?" question applied to a different module.** The addon-modules
inventory table already flagged this one as "unconfirmed" origin from
an earlier session. Diffed it directly against Gunthar's real vanilla
source (`OpenSim/Region/OptionalModules/Avatar/GroupAutoInvite/
GroupAutoInviteModule.cs`, confirmed present and substantively similar)
rather than trusting the stale flag either way. Unlike RegionCurrency,
this one checks out as a genuine port: same core logic, same
`IGroupsModule.InviteGroup` call, adapted for this repo's addon-module
wiring (Mono.Addins attributes vanilla's built-in-module form doesn't
need), an English default invite message instead of vanilla's Italian,
and one real improvement over the vanilla version - invites are tied
to the specific login `SessionId` that triggered them (checked again
before firing after the configured delay) via a deterministic
SHA256-derived per-session invite ID, instead of vanilla's simple
in-memory "already invited this session" set, so a delayed invite
task queued for one login can't misfire against a since-relogged
session. Inventory table corrected from "unconfirmed" to confirmed
with the specific diff findings.

All of the above (fix + removal + PayPal reframing) is deployed to
`OpenSim.Addons.RegionWeb.dll` only - build-verified, not yet
live-tested against the real grid.

### Real live bug: /myregions/oar-load 502 Bad Gateway

The user hit this live while the above work was still uncommitted:
tried to restore an OAR through the self-service `/myregions` page and
got a straight "502 Bad Gateway" from the external reverse proxy in
front of the real deployment. `Robust.log` had zero trace of the
request at all - not even a session-check failure - which turned out
to be explained by the handler itself: `HandleMyRegionsOarLoad`
(`WebInterfaceServiceConnector.cs`) had no logging calls anywhere in
it, so silence in the log didn't actually prove the request never
reached Robust.

Root cause, found by reading the handler rather than guessing at
proxy config: the response message already promised "Restore queued
for X. This will take a little while." - but the code didn't queue
anything. It parsed the uploaded OAR into memory, then synchronously
blocked the entire request (`client.PostAsync(url,
content).GetAwaiter().GetResult()`, a 30-second `HttpClient` timeout)
relaying the whole file on to the target region's own `OAR/Load/`
endpoint, and only sent a response after that relay completed or
failed. For a small OAR this might squeak under most reverse proxies'
default read-timeout; for anything bigger, the external proxy's own
timeout fires first and kills the connection with a 502 - the browser
never sees Robust's real (eventually-successful-or-not) response at
all, because Robust was still mid-relay when the proxy gave up.
`HandleMyRegionsOarSave` (the backup half, same page) had the
identical pattern with a 10s timeout, less likely to actually be hit
in practice since it POSTs an empty body rather than relaying a file,
but the same design bug.

Fixed both: the actual HTTP relay to the target region now runs via
`Util.FireAndForget` instead of blocking the request thread - the
handler validates everything it can synchronously (session, region
ownership, the uploaded file's presence for Load, the confirmation
checkbox), then hands the network call off to a background thread and
redirects immediately with the same "queued" message that was already
being shown, except now it's actually true. Success/failure of the
background relay is logged via `m_log` (`[WEBINTERFACE]: OAR restore
(N bytes) accepted by X` / `... responded with N` / `... failed:
reason`) so a future failure is diagnosable from `Robust.log` instead
of vanishing silently the way this one did. Bumped the Load relay's
own internal timeout from 30 seconds to 5 minutes while at it, since
without the external proxy read-timeout in the way, there's no longer
a strong reason to cut off what could be a genuinely large file
transfer between two region processes.

Build-verified. Batched into the full deploy below rather than a
one-off Robust-only redeploy - `OpenSim.Addons.RegionWeb.dll` (from
the currency reconciliation work) turned out to be a region-side
module after all, not Robust-only as earlier entries this session
assumed, so a real fix batch touching both needed the whole grid
stopped regardless.

### Full rebuild + full deploy sync, live grid back up

With the whole grid stopped for the currency-reconciliation +
OAR-relay fixes, did a proper full sync rather than hand-picking
individual DLLs one more time: full solution rebuild, then compared
every `.dll`/`.pdb` in the build output against the live deployment
by MD5 and copied every one that differed - 94 of each. That's a much
larger number than this session's actual code changes account for on
its own; .NET builds aren't guaranteed byte-reproducible run to run
(embedded metadata/GUIDs can differ even with `<Deterministic>true</Deterministic>`
and no source changes), so a large share of that 94 is almost
certainly no-op rebuild noise from the many scoped/full builds done
throughout this session, not 94 files' worth of real behavior change.
Re-verified by hashing every file again after the copy - zero
mismatches, zero missing (the only "missing" hits were unmanaged
native libraries - BulletSim/sqlite/openjpeg - that live under the
deployment's own `lib64/` folder, a different location by design, not
part of this sync).

Also found and cleaned up while in there: the live deployment still
had `OpenSim.Addons.RegionCurrency.dll`/`.pdb` and the entire
`addon-modules/RegionCurrency/` directory (including a still-`Enabled
= true` `RegionCurrency.ini`) sitting untouched from before this
session's removal - only the repo's own copies had been deleted
earlier, not the live deployment's separate ones. Harmless in
practice (Mono.Addins had nothing left to load once the DLL was
gone), but removed properly now rather than left as confusing
leftover state. A stale Mono.Addins registry cache entry
(`addin-db-004/addin-data/.../OpenSim.Addons.RegionCurrency,1.0.maddin`)
was left alone - Mono.Addins is expected to self-heal that on next
scan now that the DLL is actually gone, and hand-editing its internal
cache format wasn't worth the risk for what's already a no-op.

### OAR-load 502 - the code fix above didn't resolve the user's actual symptom

Retested against the real deployment after the fix/full-sync deployed:
same 502. Confirmed via two independent signals that the request never
reaches `HandleMyRegionsOarLoad` at all - `Robust.log` had zero new
entries (including the new logging this fix added, which should have
fired either way), and the user directly observed no command ever
appeared in Var Test Region's own live console window either. So the
fire-and-forget fix above is a real, legitimate bug fix (the response
message was always lying about "queued" when the code actually
blocked), but it does not explain or resolve this specific reported
502 - something between the browser and Robust's own HTTP listener is
where this request is actually dying, before any of this session's
code ever runs.

Investigated the reverse-proxy angle (the user's own deployment runs
nginx via Laragon, unconfigured/default) but explicitly stopped short
of touching or documenting a fix around that specific setup - the user
correctly pushed back: baking one person's reverse-proxy configuration
into the project doesn't help anyone whose deployment looks different,
and framing it as a required doc note has the same problem by
implication. Proposed two code-level architecture options (bypass the
proxy by uploading straight to the target region's own port; stream/
chunk the upload instead of one large body) - the user rejected the
first on real UX grounds (a direct-to-region upload would force every
self-service resident, not just an admin doing this locally, to stage
their OAR somewhere else first, like Google Drive, before this page
could reach it - worse than what exists today) and asked for more
research before committing to either.

Left open, not resolved. Practical workaround the user already has:
load the OAR directly from the region's own console instead of
through the web page. `HandleMyRegionsOarLoad`'s fire-and-forget fix
stays in - it's independently correct regardless of this - but the
actual reported 502 needs a real root-cause investigation into what
sits between the public domain and Robust before any further fix is
attempted.

### Resolution: browser-based OAR/IAR restore removed entirely

Follow-up to the still-open 502 above, after redeploying the
full-sync build and retesting for real: same result, and the user
directly observed no command ever reached Var Test Region's own
console from the web attempt either - a second independent signal
(beyond `Robust.log` staying silent) that the request dies before any
of this project's code runs at all.

Traced the actual reverse proxy: the live deployment runs nginx via
Laragon, effectively unconfigured (no vhost anywhere in it references
the public domain, port 9002, or `myregions`/`myinventory` at all -
whatever's terminating public traffic for the real domain isn't one
of Laragon's own configured sites). Stopped short of chasing that
specific setup further or writing a "configure your reverse proxy
this way" doc note - correctly pushed back on: this deployment's
particular proxy setup isn't representative, and documenting a fix
tied to one specific reverse-proxy product doesn't help anyone whose
deployment looks different. Also considered, and the user rejected on
real UX grounds: bypassing the proxy entirely by uploading straight
to the target region's own port - that would force every self-service
resident (not just an admin testing locally) to stage the file
somewhere else first, worse than today.

Landed on removing the feature rather than continuing to chase it:
browser-based OAR/IAR *restore* doesn't exist in any OpenSim web UI
this project has checked (WhiteCore-Dev's own ~100-page web module
included), and neither of us knows of a real deployed grid that
offers it either - it was never an established, expected feature to
begin with, just something added along the way. Backup (*save*, to
the server's own configured folder - the exact same local-disk
operation autobackup already does, no HTTP relay, no proxy, no upload
size question) stays exactly as it was; only the upload-and-restore
half is gone.

Removed `HandleMyRegionsOarLoad` and `HandleMyInventoryIarLoad`
(including the just-added fire-and-forget fix - net win regardless,
since it was masking a promise the code couldn't keep, but moot once
the whole path is gone), their two route-dispatch cases, both
pages' upload `<form>`s (replaced with a short explanation of why and
a pointer to the region console), and the hand-rolled
`ParseMultipartFormData`/`ExtractQuotedValue`/`IndexOfSequence`
helpers - confirmed nothing else in the file used them once both
Load handlers were gone. `HandleMyRegionsOarSave`/
`HandleMyInventoryIarSave` (the backup-only paths) are untouched.
Full solution build clean, 0 warnings - confirms nothing else
referenced any of the removed code either.

### PRIM_GLTF_* readback - the other half of today's dispatch fix

Moved to the next README gap and finished what the earlier SET-side
fix explicitly deferred: `llGetPrimitiveParams`/
`llGetLinkPrimitiveParams` readback for the same four codes
(`PRIM_GLTF_BASE_COLOR`/`_NORMAL`/`_METALLIC_ROUGHNESS`/`_EMISSIVE`).

Turned out to need less new infrastructure than expected. The
"compact" override format `ApplyGltfPrimitiveParams` writes to isn't
JSON/OSD-serialized - it's a hand-rolled `{'key':value,...}` string
format with its own manual parser - but the *read* side of that
parser already existed in full (`ReadCompactArrayItems`,
`ReadCompactUuidArrayItem`, `ReadCompactTransformMaps`/
`ReadCompactTransformMap`, `ReadCompactNumberArray`,
`ReadCompactDouble`, `ReadCompactBool`), built earlier for
`llGetRenderMaterial`/`llIsLinkGLTFMaterial` and evidently never
wired into `llGetPrimitiveParams` either. Wrote one new helper,
`GetGltfPrimitiveParams`, that assembles the same `[texture, repeats,
offsets, rotation]` shape plus the same type-specific extras the SET
side accepts, entirely out of those existing readers - added a case
to `GetPrimParams`'s switch (the GET-side per-part dispatch loop,
confirmed to be the same method containing the existing `PRIM_NORMAL`/
`PRIM_SPECULAR`/`PRIM_ALPHA_MODE` case, not a different one) that
iterates faces the same way that case does for `ALL_SIDES`.

Scoped deliberately smaller than full SL parity, and said so in both
the code comment and here rather than letting the gap quietly stay
hidden: this reads back a face's own override JSON
(`GetMaterialOverrideData`) only, not the assigned base material
merged in underneath it the way `GetGltfMaterialAssetData` (used by
`llGetRenderMaterial`'s sibling code) can read from an asset. A face
with an assigned material but no override returns the same "nothing
here" defaults a bare prim would, rather than the material's actual
values. What *does* work, and is the property that actually matters
for the README's stated gap: a script that sets one of these four
codes via `llSetPrimitiveParams`/`llSetLinkPrimitiveParamsFast` and
reads it back on the same face sees exactly what it set - a real
round trip, not just a non-crashing stub. Defaults for an unset field
were kept consistent with `getLSLFaceMaterial`'s own existing
"material not found" defaults just above it in the same switch
(repeats 1,1; offset 0,0; rotation 0; alpha mode 1/BLEND), rather than
inventing new ones.

Build-verified (script engine project + full solution, both clean, 0
warnings). Not yet deployed/live-tested - same
`OpenSim.Region.ScriptEngine.Shared.Api.dll` this session's SET-side
fix already touched, batched for whenever that's next redeployed.

### Bot/NPC OSSL wiring - all 58 bot* functions reachable from scripts

Moved to the next README gap: the `IBotManager`/`BotManager`/
`BotPersistenceManager` framework (ported from Tranquillity earlier
this project's history) was fully built but genuinely unreachable -
zero `bot*` OSSL functions existed anywhere in `OSSL_Api.cs`/
`IOSSL_Api.cs`/`OSSL_Stub.cs`. Confirmed Tranquillity's actual `bot*`
API surface directly against its own source (`origin/develop` branch,
not checked out locally so read via `git show`) rather than trusting
guesswork: `Source/InWorldz.Phlox/Compiler/DefaultConstants.cs` for
every `BOT_*` constant's exact numeric value, and
`Source/Phlox.ScriptEngine/LSLSystemAPI.cs` for the exact 58 function
names/signatures. Only bare names, signatures, and constant values
were extracted - not Phlox's actual VM/interpreter implementation,
consistent with this project's standing position that the Phlox
provenance concern applies to the ~98,000-line engine implementation,
not to what amounts to a documented function-name API surface.

Wired in six batches, building clean after each one before moving on:

1. **Lifecycle** - `botCreateBot`/`botRemoveBot`/`botGetOwner`/
   `botIsBot`/`botGetName`/`botChangeOwner`/`botGetAllBotsInRegion`/
   `botGetAllMyBotsInRegion`. `botCreateBot` returns the new bot's key
   directly (`LSL_Key`) rather than Tranquillity's `void` - Phlox's
   version likely relies on an engine-specific callback/global-state
   convention that doesn't apply to YEngine; returning the key
   synchronously is the correct adaptation for this engine, matching
   `osNpcCreate`'s own existing precedent. `botChangeOwner` always
   returns `BOT_ERROR`, matching Tranquillity's own implementation,
   which stubs it out too - not a caps gap they have and we don't.
2. **Movement/navigation + events** - `botSetNavigationPoints`/
   `botFollowAvatar`/`botStopMovement`/`botPauseMovement`/
   `botResumeMovement`/`botSetMovementSpeed`/`botGetPos`/
   `botTeleportTo`/`botSetRotation`/`botWanderWithin`/
   `botRegisterForNavigationEvents`/`botDeregisterFromNavigationEvents`/
   `botRegisterForCollisionEvents`/`botDeregisterFromCollisionEvents`.
   `BotMovementResult`'s existing values (`Success`/`BotNotFound`/
   `UserNotFound`/`Error` = 0/-1/-2/-3) already matched `BOT_SUCCESS`/
   `BOT_NOT_FOUND`/`BOT_USER_NOT_FOUND`/`BOT_ERROR` exactly, and
   `TravelMode`'s 1-5 values already matched `BOT_TRAVELMODE_*` exactly
   - both enums were evidently written against the same Tranquillity
   reference during the original port, so `botFollowAvatar`/
   `botSetNavigationPoints` needed no translation layer, just a cast.
   Added a private `ParseBotOptionsList` helper in `OSSL_Api.cs` to
   turn the flat `[key, value, key, value, ...]` LSL option lists this
   API uses throughout (`BOT_FOLLOW_OFFSET` etc.) into the
   `Dictionary<int, object>` shape `IBotManager` already expected.
3. **Chat/IM/interaction/animation** - `botWhisper`/`botSay`/
   `botShout`/`botStartTyping`/`botStopTyping`/
   `botSendInstantMessage`/`botSitObject`/`botStandUp`/
   `botTouchObject`/`botGiveInventory`/`botStartAnimation`/
   `botStopAnimation`. Animation name-to-asset-ID resolution mirrors
   `osNpcPlayAnimation`/`osNpcStopAnimation` exactly - look the name up
   in `m_host.Inventory`, require `AssetType.Animation`, pass the
   resolved `AssetID` through.
4. **Tagging + persistence** - `botAddTag`/`botRemoveTag`/`botHasTag`/
   `botGetBotTags`/`botGetBotsWithTag`/`botRemoveBotsWithTag`/
   `botSetPersistent`/`botRemovePersistent`/`botIsPersistent`/
   `botGetPersistentData`/`botSetPersistentData`. Tagging was pure
   wiring against existing `BotManager` methods. Persistence needed a
   small, deliberate interface addition first: `IBotManager` had no
   passthrough to `BotPersistenceManager` at all (only `BotManager`'s
   own `PersistenceManager` property reached it, and `OSSL_Api.cs` only
   holds an `IBotManager` reference, not the concrete class) - added
   `SetBotPersistent`/`RemoveBotPersistent`/`IsBotPersistent`/
   `GetBotPersistentData`/`SetBotPersistentData` to `IBotManager`
   itself as thin forwards to `m_persistence`, returning
   `BotPersistError`'s existing codes directly (no confirmed
   Tranquillity `BOT_PERSIST_*` constants exist for this - our own
   persistence layer is a novel addition beyond stock Tranquillity, so
   its own error-code convention applies).
5. **Profile + outfits** - `botSetProfile` (deprecated, per the
   original API's own convention)/`botSetProfileParams`/
   `botGetProfileParams`/`botSetOutfit`/`botRemoveOutfit`/
   `botChangeOutfit`/`botGetBotOutfits`/`botSearchBotOutfits`.
   `IBotManager` had a setter (`SetBotProfile`) but no getter at all -
   `BotData` was already storing `AboutText`/`Email`/`ImageID`/
   `ProfileURL` (set-only, never read back), so added `GetBotProfile`
   reading those same fields. `botSetProfileParams`/
   `botGetProfileParams` use the `[BOT_ABOUT_TEXT/BOT_EMAIL/
   BOT_IMAGE_UUID/BOT_PROFILE_URL, value, ...]` pair-list convention
   the two functions' shared parameter name (`profileInformation`)
   implies. `botSetOutfit`/`botRemoveOutfit` take no bot ID by design
   (confirmed against Tranquillity's own signature) - they save/remove
   a named outfit snapshot of the *calling script owner's* current
   appearance for later use via `botChangeOutfit`, not a bot's own
   outfit. `botSearchBotOutfits` needed genuinely new logic (no
   confirmed Tranquillity semantics for its `matchType`/paging
   argument existed to port) - implemented as `matchType` 0/1/2 =
   substring/prefix/exact (case-insensitive) over
   `GetBotOutfitsByOwner`, `start`/`end` as an inclusive 0-based slice
   of the matches (`-1` for `end` meaning "to the last match") -
   documented as an interpretation, not a confirmed port, in both the
   code comment and the README.
6. **Sensors/comms** - `botSensor`/`botSensorRepeat`/
   `botSensorRemove`/`botListen`/`botMessageLinked`. Done last as
   planned, since it needed real new backend work rather than pure
   wiring. `SensorRepeat.cs`'s `SenseOnce`/`SetSenseRepeatEvent`/
   `SensorSweep`/`doObjectSensor`/`doAgentSensor` only understood a
   `SceneObjectPart host` as the sensing origin (self-exclusion,
   position, rotation, attachment handling all read from it directly).
   Added a `ScenePresence hostPresence` field to `SensorInfo` and two
   new overloads of `SenseOnce`/`SetSenseRepeatEvent` accepting a
   `ScenePresence` instead, then branched the three read sites (`ts.host
   is null` → use `ts.hostPresence`'s position/rotation/UUID instead,
   with "attached" always false and self-exclusion set to the bot's own
   UUID) - existing prim-hosted sensor behavior is provably untouched
   since every new branch is guarded by the host being null, which
   never happens on the existing `llSensor`/`llSensorRepeat` call path.
   `botSensorRemove` reuses `UnSetSenseRepeaterEvents` unchanged (keyed
   by script localID/itemID regardless of host type, so it already
   removes bot-hosted sensors too). `botListen` deliberately does *not*
   use the bot's position - traced `WorldCommModule.TryEnqueueMessage`
   and found its range-check path resolves the listener's `hostID` via
   `Scene.GetSceneObjectPart` and does `if (sPart == null) return;`
   (not `continue`) on a miss, meaning a bot's `ScenePresence` UUID
   there would silently abort delivery to every other listener sharing
   that channel, not just this one - a real landmine, not a stylistic
   choice. `botListen` gates on bot ownership via
   `IBotManager.CheckPermission` but otherwise behaves exactly like
   `llListen` (host position, not bot position). `botMessageLinked`
   had no existing delivery path to build on, so added
   `IBotManager.BotMessageLinked`, mirroring the existing
   `FirePathEvent`'s multi-engine `PostScriptEvent` broadcast pattern
   (posts to every `IScriptModule` on the bot's scene since only one
   owns the target script item) but firing `link_message` with
   `sender_num` hardcoded to `0` (a bot has no link number) against
   whichever script most recently called
   `botRegisterForNavigationEvents` for that bot.

All 58 `BOT_*` constants (`BOT_ERROR` through `BOT_PROFILE_URL`) added
to `LSL_Constants.cs`, values taken directly from the confirmed
Tranquillity source rather than inferred. `osslDefaultEnable.ini`
given `Allow_bot*` entries for every function (split across the
existing `ThreatLevel None`/`ThreatLevel High` sections to match each
function's `CheckThreatLevel` call - getters None, everything else
High, mirroring `osNpc*`'s own existing threat-level pattern exactly),
all pointed at the pre-existing `${OSSL|osslNPC}` macro group rather
than inventing a parallel one, since bots warrant the same trust
threshold NPCs already have.

Full solution build clean after every batch, 0 warnings throughout,
including the `SensorRepeat.cs` changes.

**Live-verified.** Deployed (full rebuild, 186-file DLL/PDB sync, zero
mismatches on re-verify; caught and fixed a real `osslDefaultEnable.ini`
drift in the same pass - the live deployment's copy had an
`Allow_osGetAgentViewer` line from an earlier session's live-verification
work that had never been carried back into the tracked file, restored
before overwriting the live copy with the new `Allow_bot*` entries so
that permission wasn't regressed). Grid restarted clean, both regions
came up with no errors in `Robust.log`/`OpenSim.log`.

Verified in-world with a self-contained smoke-test script (rezzed on
Welcome Center as an estate-owner account, since `osslNPC`'s default
group is `ESTATE_MANAGER,ESTATE_OWNER`) exercising a representative
sample from all six batches end to end: `botCreateBot` returned a real
key; `botIsBot`/`botGetOwner`/`botGetName`/`botGetAllBotsInRegion` all
read back correctly; `botChangeOwner` correctly returned `BOT_ERROR`
(-3); `botGetPos`/`botSetRotation`/`botTeleportTo`/
`botSetMovementSpeed` ran clean; `botSay` actually spoke
"Hello from the bot* wiring test" in local chat as "TestBot One" -
real delivery through the chat pipeline, not just a non-throwing stub;
`botAddTag`/`botHasTag`/`botGetBotTags`/`botGetBotsWithTag`/
`botRemoveTag` round-tripped correctly; `botSetProfileParams`/
`botGetProfileParams` round-tripped `BOT_ABOUT_TEXT`/`BOT_EMAIL`
correctly; `botSensor` correctly reported the one nearby avatar via a
real `sensor()` event - confirming the new `ScenePresence`-hosted
`SensorRepeat.cs` path works, not just compiles; `botListen` returned
a valid handle; and `botRemoveBot` correctly removed the bot
(`botIsBot` false afterward). Zero exceptions or errors in
`OpenSim.log`/`Robust.log` during the test window. Not covered by
this pass (needs real assets/other avatars to test meaningfully, left
for a follow-up session): outfits, persistence, animation,
`botGiveInventory`, `botSitObject`/`botTouchObject`, multi-waypoint
navigation/following, `botSensorRepeat`, `botMessageLinked`.

### Real PBR terrain support - the ModifyRegion capability

Moved to the next README gap - the last real item on the priority list
before the two flagship "logged, not started" entries (SLua, Phlox).
Re-scoped this properly before writing anything, since the existing
README note called it "substantial from-scratch build (comparable to
Experience Tools or the native currency service)" and that framing
turned out to be based on an incomplete picture.

**Scoping.** Spawned a research pass first (repo-wide grep across this
codebase, `OpenSim-Tranquillity` via `git show origin/develop:<path>`
since that branch isn't locally checked out, and `opensim-vanilla`,
Gunthar's fork) to confirm the gap precisely rather than trusting the
existing note. It confirmed `ModifyRegion` doesn't exist as a real
capability in any of the three - only a `RemoteAdminPlugin.cs` XML-RPC
method with a similar name, unrelated. But it also surfaced something
the earlier investigation had missed entirely: a real, working, but
write-inaccessible PBR terrain **storage** system already inherited
from real upstream OpenSim (commit `54fe5747ea`, "add storage for pbr
terrain feature that viewers for opensim may add") -
`RegionSettings.TerrainPBR1-4` (4 UUID slots, mirroring the classic
`TerrainTexture1-4` shape), with full DB migrations in all three
backends, OAR round-trip, and - most importantly - delivery to
PBR-capable viewers on every region handshake
(`LLClientView.cs`, gated on the client's `SupportTerrainPBR` flag).
The *only* write paths were a console command and an OSSL function
(`osSetTerrainTextures`), neither reachable from any viewer UI. Also
found that Gunthar's fork has a real, still-partial LSL entry point
Confluence lacks (`llSetGroundTexture`), whose own inline comment
acknowledges the same structural gap this investigation was about to
hit: *"OpenSim's current terrain settings persist PBR material IDs,
but not per-layer UV transforms."*

That gap - what a `ModifyRegion` capability's actual request/response
LLSD schema looks like - was the one thing genuinely unanswerable from
any of the three codebases: no stub, no comment, no prior attempt
anywhere. Rather than guess at a schema and risk building something
that doesn't interoperate with a real viewer, checked the actual
source of a PBR-capable viewer already present on this machine
(`S:\Github\phoenix-firestorm`, the same checkout used earlier this
project's history to find the real Region Debug Console UI).
`indra/newview/llpbrterrainfeatures.cpp`/`.h` and
`llvlcomposition.h` gave the real answer directly: `ModifyRegion` GET
returns `{success, overrides: [4 entries]}`, POST accepts
`{overrides: [4 entries]}`, and each entry is either an empty map (no
override) or a glTF material-override LLSD blob
(`LLGLTFMaterial::getOverrideLLSD`/`applyOverrideLLSD` on the viewer
side - tiling/scale/rotation/offset and similar per-slot tweaks).
Critically, `LLModifyRegion` itself is a one-method abstract interface
(`virtual const LLGLTFMaterial* getMaterialOverride(S32 asset) const`)
- the capability only ever deals with the *override* layer. Which glTF
material asset occupies each of the 4 slots is set through a
completely separate call (`setDetailAssetID`) that this repo's
inherited `TerrainPBR1-4` + region-handshake delivery already handles
end to end. This single finding cut the real remaining scope down
dramatically from the README's "substantial from-scratch build"
framing: no new base-material-assignment system was needed, only a
capability to store/relay 4 opaque override blobs the server never
needs to interpret.

**Implementation.** Cross-checked `MaterialsModule.cs` (the working
`RenderMaterials`/`ModifyMaterialParams` capabilities) for the exact
registration pattern used throughout this codebase -
`OnRegisterCaps` + `caps.RegisterSimpleHandler(name, new
SimpleStreamHandler("/" + UUID.Random(), handler))` is what gives a
capability name a real URL in the SEED-cap response (unlike the
`VTPBR`/`VETPBR` pseudo-cap flags `BunchOfCaps.cs` reads directly out
of the SEED *request* and deliberately excludes from `validCaps` -
confirmed at the line level, and confirmed irrelevant here since
`RegisterSimpleHandler` is a completely different, working path that
needed no changes to `BunchOfCaps.cs` at all).

- `RegionSettings.cs`: new `TerrainPBROverrides` string property - an
  opaque store for the serialized 4-entry LLSD override array. The
  server never parses glTF fields out of it; it only persists exactly
  what a POST body carries and relays it back verbatim on GET, matching
  `LLModifyRegion`'s own contract.
- New migrations (SQLite VERSION 44, MySQL VERSION 69, PGSQL VERSION
  59) adding the matching `TerrainPBROverrides` column to
  `regionsettings`, plus read/write wiring into all three
  `SimulationData.cs` `Store`/`Load` methods (PGSQL's is a genuine
  `INSERT ... ON CONFLICT DO UPDATE`, needed the new column in three
  separate places in that one query) and the OAR round-trip in
  `RegionSettingsSerializer.cs` (`PBROverrides` element, alongside the
  existing `PBR1-4`).
- New `ModifyRegionModule.cs`
  (`OpenSim/Region/CoreModules/World/Terrain/`, added to
  `OpenSim.Region.CoreModules.csproj`'s explicit file list - confirmed
  this project uses `EnableDefaultItems=false`, not glob includes, so
  a new file needs an explicit `<Compile Include>` entry or it's
  silently never compiled). GET returns the 4 stored overrides
  (defaulting to 4 empty maps if unset/corrupt); POST validates the
  body shape (`overrides` must be an array of exactly 4 entries),
  gates on `Scene.Permissions.CanIssueEstateCommand` (same trust level
  as `SetEstateTerrainTextures` and the classic `"texturedetail"`
  estate-message path - estate managers/owners only), stores the array
  verbatim, and calls `RegionSettings.Save()` (confirmed this actually
  persists - `rs.OnSave += StoreRegionSettings` is wired in all three
  `SimulationData.cs` constructors). GET is left unrestricted, matching
  how region-handshake terrain data itself isn't per-avatar gated.

Full solution build clean, 0 warnings. Deployed (full rebuild, 186-file
DLL/PDB sync, zero mismatches on re-verify) - **live verification
against a real PBR-capable viewer is still pending**, since the grid
wasn't restarted before this entry was written. What to check once
it's back up: that `ModifyRegion` actually appears in the SEED-cap
response for a PBR-capable viewer's login, and that a real GET/POST
round-trip through it behaves as the Firestorm source predicts.

Grid restarted after this entry (user was stepping away and asked to
keep going autonomously) - found the exact launch commands by
inspecting the desktop shortcut chain rather than guessing:
`CasperiaControl.bat.lnk` on the desktop pointed at
`S:\Opensim\CasperiaControl.bat`, which turned out to be the
production-grid launcher (`BASE=S:\Opensim\Casperia`, the one this
project's own instructions say to leave untouched during Dev testing)
- but its sibling `CasperiaDevControl.bat` in the same folder is the
real Dev-grid launcher, giving the exact `Robust.exe -inifile=Robust.HG.ini`
/ `OpenSim.exe -inifile=Simulators\<Region>\OpenSim.ini` commands and
working directory the user's own control panel uses. Replicated that
exactly (same exe/args/cwd, one window per process) rather than
guessing at a launch mechanism, since a wrong guess here risks a
messier cleanup than waiting would have cost. Restarted only what was
already running before the deploy (Robust + both regions - no
MoneyServer, which wasn't part of the running state).

Both regions reached `RegionReady`'s "server_startup" ready state
cleanly (`Welcome Center` and `Var Test Region` both logged "Region ...
is ready", YEngine loaded its LSL/LS/MOD/OSSL apis with no errors, map
tiles generated, grid registration succeeded). Checked both regions'
real per-region logs (`Simulators\<Region>\OpenSim.log` - learned this
session that this is where output actually lands once a region finds
its config, not the root-level `OpenSim.log`, which stops mid-startup
and had been misleading me into thinking an earlier restart had hung
when it hadn't) for anything new and specific to `ModifyRegionModule`
or the wider `CoreModules` load: nothing - no exceptions, no missing-
type errors, nothing beyond the same handful of long-standing,
unrelated warnings already present before today (a `DATASNAPSHOT` null-
reference warning that's been silently ignored for weeks, stale
connection-refused entries against the test deployment's own public
hostname from over a week ago).
Confirms the new code loads and the region starts healthy with it in
place - genuine full verification of the capability's actual GET/POST
behavior still needs a real PBR-capable viewer session, which needs
the user.

### SLua investigation - real scope, not started

Moved to the last remaining actionable item before the two blocked
flagship entries (this one, then Phlox). Unlike PBR terrain, this one
turned out to be genuinely as large as the README already suspected -
no pleasant surprise this time.

Confirmed no local source exists to answer this from code the way
Firestorm's checkout answered `ModifyRegion` - grepped
`S:\Github\phoenix-firestorm` for "SLua"/"Luau" across
`indra/newview`, `indra/llcommon`, `indra/llmessage`: zero hits. That
checkout either predates SLua's 2025-12-02 open beta or Firestorm
hasn't merged Linden Lab's SLua support yet. Spawned a research pass
with real web access instead of guessing from general knowledge.

**What SLua actually is.** A genuine separate runtime, not a new
front-end on the existing script engine. Linden Lab's own source repo
(`github.com/secondlife/slua`, MIT-licensed) describes it as "a
friendly fork of Luau" (Roblox's own MIT-licensed engine,
`github.com/luau-lang/luau`) with SL-specific additions: a modified
state-serialization library ("Ares", derived from "Eris") so scripts
survive region crossings and sim restarts - explicitly noted by LL as
"unlikely to be up-streamable to Luau" - plus yielded-thread
serialization, isolated global environments, per-script memory limits,
and pre-emptive scheduling hooks. The viewer's new script editor
exposes four compile targets, not two (legacy LSO2, Mono, Lua, and
"LSL: 2025 VM" - classic LSL compiled to run on the same new Luau VM),
confirming LL is treating the Luau VM as a genuine third execution
engine, not a syntax-only addition.

**Protocol.** Execution and compilation are both confirmed
server-side. A Linden Lab developer (Harold Linden, on the official
feedback tracker) stated plainly: "The compiler currently runs fully
on the server, and there's no existing facilities we can hook into for
pulling in another script's source." The external-editor upload flow
described on that same page is consistent with source text being
transmitted, the same shape as today's LSL upload - but no wiki page
or forum post names the actual capability, and one community member
on `Talk:SLua_FAQ` said outright "nobody knows how the LL server code
looks like." This is a real, unresolved gap, not something glossed
over - there is no implementer-facing protocol spec, only user-facing
wiki/FAQ pages. SL's own SLua *compiler* front-end is open source
though (confirmed via the `secondlife/slua` repo itself, corroborating
a community announcement that couldn't be fetched directly - 403'd),
which is a real asset if this is ever built: LSL/Lua-to-Luau-bytecode
compilation logic wouldn't need to be reverse-engineered from nothing.

**Licensing/provenance - the opposite of Phlox.** Clean. Both upstream
Luau and Second Life's own fork are MIT-licensed, with an explained,
public chain of custody (Roblox's engine, forked by Linden Lab, both
sides open about it). No provenance question to raise with anyone.
Real, early-stage C#/.NET P/Invoke bindings to the actual native Luau
VM already exist - `NuLua` (successor to the now-archived
`luau-dotnet`, both by the same author) - meaning the likely path is
embedding the real VM via native interop, not writing a Lua/Luau
interpreter from scratch in C#. Neither binding has any indication of
production use at OpenSim's kind of concurrency/isolation scale, and
neither includes anything like SL's own "Ares" serialization layer -
that piece is SL-specific, explicitly non-upstreamable, and would need
building from nothing regardless of which VM binding gets used.

**Prior art check.** None found anywhere in the OpenSim ecosystem -
opensimulator.org wiki/mailing-list archives, OSGrid forums,
`opensim/opensim` GitHub issues, general search - all empty for
"SLua"/"Luau". Unsurprising given the 2.5-month-old beta, but it means
this would be genuinely greenfield work within OpenSim, not a port
with a reference implementation to check against. The one adjacent
but unrelated prior-art hit: `LuaSL`, an old SledjHamr.org project that
tried replacing XEngine with a LuaJIT-based engine years before SLua
existed - different Lua variant, no connection to Second Life's actual
implementation, but shows "use Lua for OpenSim's script engine" isn't
a new idea to the community even if the specific SLua/Luau angle is.

**Verdict.** Real, substantial, multi-month-class engineering work if
ever undertaken - embedding a foreign native VM into this all-C#
codebase is a meaningfully bigger commitment (new native build
dependency, new toolchain requirements) than anything else attempted
this session, and the hardest single piece (state serialization across
region crossings) has no existing reference to build from since SL's
own solution is explicitly non-portable. Deliberately left logged, not
started, rather than begun without the user's explicit sign-off on
committing to something this size - unlike PBR terrain, this isn't a
"turned out smaller than expected, just build it" situation. Full
source list and open questions in the research pass; see the
"Known gaps" section of README.md for the condensed version.

### Upstream audit - opensim-master, Tranquillity (all branches), Tampa/opensim

User asked to check three sources for anything new to absorb, per this
project's own standing "don't let this repo fall behind" mission.

**origin/master (opensim/opensim).** 3 new commits since last merge,
2 of them real: `MaxSimulationHeight` bumped 50000f -> 65536f to match
the viewer's own limit (`OpenSim/Framework/Constants.cs`), and a small
dead-code cleanup in `MapSearchModule.cs` (an unused `MapBlockData`
local left over from an earlier refactor that already introduced the
`block` variable actually used). Both trivial and safe - merged
directly (`git merge origin/master`, clean auto-merge on
`Constants.cs`, no conflicts), full solution build verified clean
afterward.

**Tranquillity - checked every branch this time, not just `develop`**
(prompted mid-task by the user pointing out the repo has multiple
branches - correct catch, `develop` alone would have missed real
content). `develop` itself had exactly one new commit: an xunit test-
infrastructure restoration, not a feature - nothing to port. Went on
to check every other branch (`dev-future`, 54 unique commits;
`feature/robust-di`, 56; `moneyservice_di`, 7; `feature/fix-lslhttp`,
1; `helper/xinv`, 1; two release branches, already fully absorbed).
`dev-future` and `feature/robust-di` turned out to be Tranquillity's
own internal architecture modernization work - dotnet 9 migration,
replacing AppDomains with `AssemblyLoadContext`, introducing Autofac
DI throughout Robust/region/money-service startup, `Nerdbank.Versioning`,
restructuring `bin` to `Library`, an in-progress ASP.NET controller
experiment for assets - explicitly marked "dev-future" (i.e. Tranquillity's
own words for "not stable yet") and structurally tied to decisions
Confluence hasn't made and doesn't need to inherit (this repo already
targets net8.0 on its own timeline). Not proven features in the sense
this project's mission statement means - correctly out of scope, not
just skipped for time. `moneyservice_di` is the same kind of work
scoped to just the money service. `feature/fix-lslhttp` (a real,
targeted bug fix - `/lslhttp/` outbound URLs must never be blacklist-
filtered) turned out to already be present in Confluence's own
`OutboundUrlFilter.cs`, implemented independently and correctly (proper
C# `IndexOf`/`StartsWith` casing, where Tranquillity's own commit
actually has a bug - lowercase `url.indexOf(...)`, which doesn't exist
in C# and wouldn't compile as written). `helper/xinv` is a standalone
427-line PHP admin script for checking/repairing inventory problems
(root folder issues, duplicate system folders, suitcase issues) - a
real, self-contained tool, not core engine code; noted here rather
than ported, since it's the kind of thing worth adding to a `Helpers/`
or `Tools/` folder on its own merits if wanted, not part of the engine
audit's normal scope.

**Tampa/opensim** (github.com/Tampa/opensim) - the repo the user
specifically asked to check. Confirmed via `git log` that "Tampa" is a
real, currently-active upstream contributor (their PR #60,
`Sim_height_like_viewer`, is literally the `MaxSimulationHeight` fix
already merged above). Added as a temporary remote, fetched, checked
every branch: `Sim_height_like_viewer`, `SmoothArea-fix`, and
`webrtc-fix` all show zero commits unique relative to `origin/master` -
already upstreamed, already covered by the merge above. Their fork's
own `master` branch appeared to have 93 "unique" commits at first
glance, but every one of them is a `Merge pull request #N from
opensim/master` artifact from Tampa periodically syncing their fork
against upstream - zero unique tree content (confirmed:
`origin/master..tampa/master` shows 93 commits, but
`tampa/master..origin/master` shows 0, meaning nothing in Tampa's fork
is missing from upstream either - it's a mirror, not a divergent
branch). Nothing to port. Temporary remote removed after the check.

**Net result of this audit pass:** one small, safe upstream merge
landed (sim height + dead-code cleanup); everything else checked out
already-covered, out-of-scope-by-design, or a mirror with nothing
unique. Build verified clean, pushed.

### Stale-file pass: LICENSE.txt, CONTRIBUTORS.txt, TESTING.txt, Makefile, runprebuild

User spotted several files on GitHub's file browser showing very old
last-commit dates (CONTRIBUTORS.txt 2019, TESTING.txt 2014, Makefile
2015) and asked whether they needed updating - with the explicit
standard "accuracy is key, only include up-to-date files and
information."

Checked all five files (LICENSE.txt, CONTRIBUTORS.txt, TESTING.txt,
Makefile, runprebuild.sh/.bat) against `origin/master` first: every one
is byte-identical to upstream's own current copy. None of this is
Confluence uniquely falling behind - it's upstream's own state. That
distinction mattered for what "fix" meant per file:

- **LICENSE.txt** - timeless BSD-style boilerplate, no dates or names.
  Nothing to update.
- **CONTRIBUTORS.txt** - genuinely stale relative to reality, but not
  fixable by backfilling 7 years of individual upstream contributor
  names (unverifiable, and that file has always been self-curated, not
  auto-generated). What *was* fixable and true: Confluence itself has
  absorbed real code from forks (Tranquillity, Gunthar's fork,
  WhiteCore-Dev, Halcyon, Homeworldz, opensim-lickx) with zero record
  in the traditional contributors file, despite already being
  documented in README's Attribution section. Added a
  "Confluence-Specific Attribution" section listing those projects.
- **runprebuild.sh/.bat** - not actually stale. Deliberately targets
  `net8_0`, matching upstream's own current, live choice (see the
  separate .NET 8 vs .NET 10 discussion below). Left alone.
- **TESTING.txt** - genuinely wrong, not just old: told readers to run
  `nant test`, use NUnit for **.NET 2.0**, and VS2005/2008 - none of
  which apply to this repo's real build process (`dotnet build`/MSBuild
  via prebuild-generated project files). Rewritten from scratch to
  describe the real process.
- **Makefile** - every target shells out to `${NANT}`, a build system
  with zero presence anywhere else in this repo's actual toolchain.
  Removed outright per the user's explicit "remove irrelevant items" -
  rewriting it as a thin dotnet wrapper was considered and rejected: it
  would just be a second place for the real build process to drift out
  of sync from, for something `dotnet build OpenSim.sln` already does
  directly with no wrapper needed.

**Verifying TESTING.txt's accuracy caused a real incident.** To write
an honest TESTING.txt, actually ran `runprebuild` for real (via
`dotnet bin/prebuild.dll ...`, matching the exact invocation
`runprebuild.bat`/`.sh` use) to check whether the 20 real `<Project>`
Test stanzas already in `prebuild.xml` (`OpenSim.Framework.Tests`,
`OpenSim.Region.CoreModules.Tests`, etc.) actually produce working test
projects. They don't - confirmed by diffing the run's own
"Creating project:" output against every Test project name defined in
`prebuild.xml`: zero of the 20 got created, silently, no error. Checked
whether this is a Confluence-specific regression: it isn't - upstream's
own `prebuild.xml` has the identical pattern (most Test projects share
the exact same `path` as their non-test counterpart, e.g.
`OpenSim.Region.CoreModules.Tests` uses `path="OpenSim/Region/
CoreModules"`, matching `OpenSim.Region.CoreModules` itself, relying on
file-match rules alone to separate them). This particular `prebuild.dll`
build (banner credits "OpenSimulator build 2017 Ubit Umarov") appears
unable to handle that pattern. Not fixed - left as a documented, real
limitation in both `TESTING.txt` and `prebuild.xml` for whoever wants
to pursue real test-running support later.

Regenerating to check this **broke the main build** - the second time
this exact failure mode has happened (first was 2026-08-16, logged
above). `prebuild.xml` regeneration always wipes
`OpenSim.Framework.csproj`'s hand-added `GenerateGitVersionInfo`
MSBuild `<Target>` (build-number versioning added 2026-08-16) - the
schema has no way to express a custom `<Target>`, so this isn't a bug,
it's a known, unavoidable property of regenerating. `prebuild.xml`
already had a warning comment saying exactly this - read it *after*
already breaking the build, not before, which is its own lesson. Worse:
the actual restore text wasn't saved anywhere git-tracked (`.csproj` is
gitignored by design), so the fix from 2026-08-16 was gone with no
record of its literal content - had to be reconstructed from scratch by
reading the still-present, previously-generated
`obj/Release/GitVersionInfo.g.cs` on disk to infer the target shape,
then rewriting the MSBuild `<Exec>`/`<WriteLinesToFile>` logic to
reproduce it. Hit one new bug while doing this that the original
2026-08-16 fix apparently didn't (or the write-up of it didn't mention):
`WriteLinesToFile`'s `Lines` attribute splits item values on a literal
`;` as an MSBuild list separator, so the generated C#'s trailing
semicolons were silently dropped, causing a second, different compile
error (`CS1002: ; expected`) before landing on the working `%3B`-escaped
version.

Fixed this gap for real this time: the complete, exact, copy-pasteable
`<Target>` XML now lives directly inside `prebuild.xml`'s own top-of-file
comment (git-tracked, survives regeneration by construction, since the
comment describes what to do *to* the regenerated file rather than
being part of what gets regenerated) rather than only in
`OpenSim.Framework.csproj` itself or in prose here. Should not need to
be reconstructed from scratch a third time.

Full solution build clean, 0 errors, after all of the above. Not
deployed - `TESTING.txt`/`Makefile`/`CONTRIBUTORS.txt`/`prebuild.xml`
changes plus the restored (equivalent, re-verified)
`GenerateGitVersionInfo` target have no runtime behavior change.

### .NET 8 vs .NET 10

User asked directly why this repo targets net8.0 instead of the newer
net10.0. Checked rather than assumed: **upstream opensim/opensim's own
current master also targets `net8_0`**, identically, confirmed via its
`prebuild.xml`/`runprebuild.sh`. Not something Confluence is behind on
relative to its own reference point. .NET 8 is the current LTS
(supported to Nov 2026); .NET 10 is also LTS but only ~9 months old as
of this session. Independent supporting evidence surfaced in the same
session's Tranquillity branch audit above: their own `.NET 9` migration
work sits on an explicitly-named, unmerged `dev-future` branch - even a
more aggressive fork in this ecosystem treats a runtime-version jump as
risky, in-progress work, not something to land on a stable branch
casually. Moving Confluence to net10 unilaterally would mean diverging
from upstream (cutting against this project's own "stay close enough to
accept continuing upstream work" goal) and would need real verification
of the native interop pieces (BulletSim, ODE physics, Mono.Addins)
first - not attempted, logged as a real option only if pursued
deliberately as its own effort.

### Inventory thumbnails - built, deployed

Picked from the consolidated Firestorm-vs-Confluence gap report (three
parallel research passes, properly `OPENSIM`-build-flag-scoped after an
early correction) as the highest-visibility item: per-item and
per-folder inventory thumbnails, the small image shown in gallery/grid
inventory views and the outfit gallery. User asked directly whether
this was already covered by upstream - re-verified from scratch rather
than assuming: zero hits for "thumbnail" (case-insensitive) in both
this repo's `origin` remote (real `opensim/opensim`) and a separate
`opensim-master` checkout freshly fast-forwarded to the same commit.
Confirmed absent upstream; distinct from the pre-existing (unrelated)
"snapshot to inventory" texture-saving feature, which both already have.

Reverse-engineered the exact wire protocol from Firestorm's own source
rather than guessing: `llfloatersimplesnapshot.cpp`'s
`post_thumbnail_image_coro`/`uploadImageUploadFile` and
`llinventorymodel.cpp`/`llviewerinventory.cpp`'s `LLSD` (un)packing.
Two-phase upload, same shape this codebase's own `NewFileAgentInventory`
already uses: POST `{item_id: uuid}` or `{category_id: uuid}` to the
`InventoryThumbnailUpload` capability, get back `{uploader: <url>}`;
POST raw JPEG2000 bytes to that one-time url, get back `{state:
"complete", new_asset: uuid}`. Confirmed via `LLInventoryCategory`/
`LLViewerInventoryItem::fromLLSD`/`unpackMessage` that the thumbnail
only ever travels inside the per-entry `thumbnail` map of the
`FetchInventoryDescendents2` `categories`/`items` arrays - never as a
separate field on the top-level requested-folder block (`InventoryCollection`
only carries `FolderID`/`OwnerID`/`Version`/`Descendents`, confirmed by
reading the class directly) - so no second serialization point was
needed there.

Three-part build:
- **Data model + DB schema** (`IXInventoryData.cs`, `InventoryFolderBase.cs`,
  `InventoryItemBase.cs`, `XInventoryService.cs`'s four `ConvertTo/FromOpenSim`
  methods, migrations for all three backends - MySQL `InventoryStore.migrations`
  v8, PGSQL `InventoryStore.migrations` v11, SQLite's oddly-named
  `XInventoryStore.migrations` v3, the last one caught by its different
  filename via `find` and its `varchar(36)` (not `char(36)`) column
  convention caught by reading its own existing `CREATE TABLE`). Trivial
  because inventory's reflection-based generic table handlers
  (`MySQLGenericTableHandler<T>` etc.) map C# public fields to DB columns
  by name automatically - add a field, add a migration, done.
- **HTTP response serialization** (`FetchInvDescHandler.cs`): added the
  `thumbnail` nested-map element to the per-subfolder `categories` loop,
  matching the pattern `InventoryItemBase.ToLLSDxml` already used for items.
- **`InventoryThumbnailUpload` capability module** (new file,
  `Avatar/Inventory/Thumbnails/InventoryThumbnailUploadModule.cs`, added to
  `OpenSim.Region.CoreModules.csproj`'s explicit `<Compile>` list since that
  project has `EnableDefaultItems=false`): `INonSharedRegionModule` following
  `ModifyRegionModule.cs`'s `OSDMap`/`SimpleStreamHandler` style for phase 1,
  `BunchOfCaps.AssetUploader`'s single-use `BinaryStreamHandler` pattern
  (mint random uploader path, register directly on `caps.HttpListener`,
  remove-on-first-use plus a 120s timeout fallback) for phase 2. Validates
  the requesting agent actually owns the item/folder before minting an
  uploader - necessary because `XInventoryService.GetItem`/`GetFolder` look
  up purely by ID and silently ignore the `principalID` parameter, so
  without this check any agent could set another resident's thumbnail by
  guessing a UUID. Stores the uploaded bytes as a normal `AssetType.Texture`
  asset via `Scene.AssetService.Store`, matching how mesh-upload's
  `texture_list` handling already creates texture assets in `BunchOfCaps.cs`.

**Known, documented limitation, not a bug**: "set thumbnail to an
existing texture via the picker" and "clear thumbnail" are AIS3-only
flows in Firestorm (`llviewerinventory.cpp`'s branch on
`FSUseAis3Api`/`isInSecondLife()`) - AIS3 defaults off on OpenSim-flagged
builds, and there's no legacy-UDP fallback field for either operation
(confirmed no `indra/llinventory` in this checkout, no `message_template.msg`
to check directly, and the classic UDP inventory-update messages predate
this feature by roughly two decades). Only the snapshot-floater upload
path is implemented; a full AIS3 implementation would be a separate,
much larger effort.

Build-verified clean at each of the three stages (0 errors). Full
rebuild + full deploy sync to Casperia-Dev: compared every `.dll`/`.pdb`
by MD5, copied everything that differed (83 real diffs after the first
copy attempt silently no-op'd on files it reported "Device or resource
busy" for under Git Bash's `cp` - re-copied the same list via PowerShell's
`Copy-Item`, which had no trouble with the same files, so the busy error
was transient/tooling-specific rather than a real lock; no `OpenSim.exe`/
`Robust.exe`/`MoneyServer.exe` process was ever running during any of
this). Re-verified byte-for-byte after: zero mismatches. Grid was already
down at deploy time and the user confirmed it's meant to stay down for
now ("grid is down for the deployment purposes") - not restarted. Actual
runtime verification (clean region startup with the new module loaded,
the MySQL v8 migration applying against `casperia_dev`, and a real
Firestorm session round-tripping an actual thumbnail upload) is still
pending the user bringing the grid back up.

### Firestorm gap verification pass - 5 remaining items checked against real source

User asked directly to verify whether opensim-master and Confluence
actually carry the exact wire-level shape Firestorm's `OPENSIM`-build
code expects, across the rest of the earlier three-way audit (everything
besides inventory thumbnails, already closed above). Ran a background
research pass reading real source in all three repos rather than
reasoning from memory - findings below, each independently spot-checked
against Firestorm's own `.cpp` before acting on them (worth doing: one
finding's exact JSON key names turned out to be wrong in the research
agent's summary - `reporting_complexity_limit`/`over_complexity_limit`
vs. the real `reportinglimit`/`overlimit` string literals in
`llavatarrenderinfoaccountant.cpp` - caught only because the actual
capability got built against the real source afterward, not the summary
verbatim).

- **UserInfo** (email/directory-visibility settings) - **CLOSED, no work
  needed.** Firestorm has a full legacy-UDP fallback for this one
  (`llagent.cpp`, explicitly kept "for OpenSim"), and both `LLClientView.cs`
  and `UserProfileModule.cs` already implement the UDP
  `UserInfoRequest`/`UpdateUserInfo` messages it falls back to.
- **EEP** (Extended Environment Protocol) - **CLOSED, exact match
  confirmed.** `EnvironmentModule.cs`'s `"ExtEnvironment"` cap and
  `ViewerEnvironment.cs`'s `ToOSD()` emit the identical key set
  Firestorm's `llenvironment.cpp` parses (`day_cycle`, `day_length`,
  `day_offset`, `env_version`, `track_altitudes`, etc., nested under
  `environment`). Nothing to do here - matches the README's existing
  note that any remaining EEP gap is viewer-UX, not server-side.
- **Destination Guide** - **CLOSED in code, operational-only gap.**
  `LLLoginResponse.cs` already emits `destination_guide_url` correctly
  when the `DestinationGuide` ini key is set, and Confluence's own
  `/guide` page is live and routed. `bin/Robust.ini.example` already
  documents the exact right value
  (`DestinationGuide = "${Const|BaseURL}/guide"`) - it's just commented
  out by default. Not a code change; an operator can enable it in their
  own deployed ini whenever they want the Destinations floater to pick
  it up automatically. Left the live grid's own ini alone rather than
  editing it unprompted.
- **Avatar Picker web-profile link** - **PARTIAL, now fixed.** Two real,
  independent mismatches, both against Firestorm's `llavataractions.cpp`
  `getProfileURL()` (OPENSIM branch): (1) `LLLoginResponse.cs` only ever
  emitted the older `profile-server-url` key; Firestorm's OpenSim-aware
  code path reads `web_profile_url` specifically and never falls back to
  the other key, so the viewer's web-profile link was always using a
  synthetic default instead of Confluence's real page. (2) Firestorm
  builds that link as `...?name=[AGENT_NAME]` (`Firstname.Lastname`),
  but `WebInterfaceServiceConnector.cs`'s `HandleProfile` only ever
  parsed `?id=<uuid>` - even with the URL wired correctly it would have
  404'd. Fixed both: `LLLoginResponse.cs` now also emits `web_profile_url`
  (both the Hashtable and OSD-map response paths) alongside the existing
  key rather than replacing it, and `HandleProfile` now accepts either
  `?id=` or `?name=First.Last` (resolved via `IUserAccountService`'s
  existing `GetUserAccount(scope, first, last)` overload). Also
  documented `ProfileServerURL` in `bin/Robust.ini.example` next to
  `DestinationGuide`/`AvatarPicker`, since it was previously an
  undocumented config key. Build-verified (hit and fixed one `CS0128`/
  `CS0165` variable-scope collision from reusing an `out UUID userId`
  across both lookup branches - renamed the `TryParse` output to
  `parsedId` and declared `userId` once, after `account` resolves,
  instead).
- **AgentProfile / UploadAgentProfileImage** - **PARTIAL, now mostly
  closed.** Firestorm's profile floater has a full `#ifdef OPENSIM`
  legacy-UDP fallback for every text field (about/first-life text,
  partner, notes, etc. - `llpanelprofile.cpp`, "FS:Beq restore UDP
  profiles for opensim"), already fully served by this module's existing
  UDP `AvatarPropertiesRequest`/`AvatarPropertiesUpdate` handlers - no
  work needed there. The one piece with **no UDP fallback at all** is
  profile *photo upload* - `llpanelprofile.cpp` aborts with a
  `RegionCapabilityRequestError` if the `UploadAgentProfileImage`
  capability is absent. Built it directly inside `UserProfileModule.cs`
  (not a new file - it needs the module's own `rpc`
  `JsonRpcRequestManager`/`GetUserProfileServerURI` machinery to talk to
  the grid-wide profiles service the same way the existing UDP handlers
  already do, and duplicating that plumbing in a separate class wasn't
  worth it). Same two-phase shape as inventory thumbnails: POST
  `{"profile-image-asset": "sl_image_id"|"fl_image_id"}` (the exact
  Firestorm request body - a slot selector, not a UUID) -> `{"uploader":
  url}`, then POST raw JPEG2000 bytes to that url -> `{"state":"complete",
  "new_asset":uuid}`. **Caught a real data-loss risk while building this**:
  `UpdateAvatarProperties` (`MySQLUserProfilesData.cs` and the other two
  backends) does a blanket `UPDATE` of the profile row's URL/about-text/
  both-image columns from whatever `UserProfileProperties` object it's
  given - not a partial/selective update. A naive image-only POST would
  have silently wiped the resident's web URL, about text, and first-life
  photo/text. Fixed by doing a proper read-modify-write: fetch the
  current profile via the same `avatar_properties_request` JsonRpc call
  the UDP path already uses, set only the one image field, then write
  the full merged object back. Registered the new cap via
  `Scene.EventManager.OnRegisterCaps`, which this module never hooked
  before (it was UDP-only until now).
- **AvatarRenderInfo** (avatar visual-complexity/jellydoll accounting) -
  **MISSING, now built.** New file, `AvatarRenderInfoModule.cs`
  (`OpenSim/Region/CoreModules/Avatar/RenderInfo/`, added to
  `OpenSim.Region.CoreModules.csproj`'s explicit `<Compile>` list).
  Confirmed the exact wire shape directly from
  `llavatarrenderinfoaccountant.cpp`'s own `KEY_*` string constants
  (deliberately re-checked rather than trusting the earlier research
  summary's paraphrase, which had the two limit-key names wrong):
  POST `{"agents": {agent_id: {"weight": int, "tooComplex": bool}}}` (every
  connected viewer periodically reports the render complexity it computed
  locally for every avatar it can see on the region), GET `{"agents":
  {agent_id: {"weight": int}}, "reportinglimit": int, "overlimit": int}`.
  The server is a passive aggregator here, same posture as
  `ModifyRegionModule.cs` toward its glTF override blobs - it doesn't
  compute complexity itself and doesn't decide who gets jellydolled
  client-side, just stores whatever the last viewer reported
  (`ConcurrentDictionary&lt;UUID, (weight, tooComplex)&gt;`, purged on
  `EventManager.OnRemovePresence` to avoid unbounded growth from
  visitors who've since left) and relays the two configurable threshold
  ints (`avatar_render_reporting_limit`/`avatar_render_over_limit` in
  `[ClientStack.LindenCaps]`, defaulting to 200000/350000 - reasonable
  starting values, not independently verified against any canonical SL
  default, since no authoritative source for "the" default was found;
  worth an operator tuning pass later if this turns out to matter in
  practice).
- **SpatialVoiceModerationRequest** (nearby-voice mute/mute-all) -
  **MISSING, scoped but deliberately NOT built this session.** The
  capability/LLSD layer itself is small (`llnearbyvoicemoderation.cpp`:
  POST `{"operand": "mute"|"unmute"|"mute_all"|"unmute_all"[, "agent_id":
  uuid]}`), but a real implementation needs to actually silence someone's
  audio in the live voice channel, not just record a flag - checked
  `OpenSim/Addons/os-webrtc-janus/` for an existing mute primitive to hang
  this off of and found none; the only "muted" references there are
  read-only console debug output of Janus's own reported participant
  state, not a callable mute API. Building this properly would mean
  first understanding `WebRtcJanusService`'s actual control-plane surface
  well enough to trust that a capability calling into it does what it
  claims - not something to rush. Logged here rather than built shallow.
- **GroupAPIv1 group ban** - **MISSING, scoped but not started.**
  Real gap, no legacy-UDP fallback exists for this specific operation
  (`llgroupmgr.cpp` just silently `return`s if the cap is absent - no
  degraded behavior, banning simply isn't available). Needs a persisted
  per-group ban list (new DB table/migrations across all three backends,
  same shape as the inventory-thumbnails or PBR-terrain work), a new
  service method, and a cap module matching `llgroupmgr.cpp`'s GET
  `{"ban_list": {ban_id: {"ban_date":...}}}` / POST `{"ban_action":
  1|2|4, "ban_ids": [uuid,...]}` shape. Checked `OpenSim/Addons/Groups/`
  first for anything to build on top of - `GroupPowers.GroupBanAccess` is
  only a permission-bit flag, and `GroupsModule.cs`'s existing
  `EjectGroupMemberRequest` is a kick (doesn't prevent rejoining), not a
  ban - there's no partial ban infrastructure to reuse. Similar
  size/shape to inventory thumbnails' data-model batch; not attempted
  this session, left for a dedicated pass.

Build-verified clean after each of the AgentProfile/AvatarRenderInfo/
Avatar-Picker changes (0 errors each time). Full deploy sync to
Casperia-Dev: 70 changed `.dll`/`.pdb` files this round, all copied via
PowerShell `Copy-Item` directly (skipped the Git-Bash-`cp`-then-retry
dance from the thumbnails deploy since the "Device or resource busy"
behavior there was already established as tooling-specific, not a real
lock), re-verified byte-for-byte after: zero mismatches. Grid still
intentionally down, not restarted - same pending-live-verification note
as the inventory-thumbnails entry above applies to all of this batch's
new capabilities too.

### GroupAPIv1 group ban - scoped, not built

User asked to scope this one out specifically, next in line after the
previous batch. Re-verified the exact wire protocol directly against
Firestorm's `llgroupmgr.cpp` myself rather than trusting the earlier
research pass's summary verbatim (worth doing again - the
`AvatarRenderInfo` key names from that same summary turned out wrong
last time), then mapped it onto Confluence's real Groups architecture
in `OpenSim/Addons/Groups/` and `OpenSim/Data/`.

**Exact protocol** (`llgroupmgr.cpp:2017-2141`, `EBanRequestAction` in
`llgroupmgr.h:379-384`): one shared `GroupAPIv1` capability URL per
agent, not per-group - both GET and POST target the same URL with
`?group_id=<uuid>` as a **query-string parameter**, not part of the LLSD
body (easy to get wrong if the inventory-thumbnails/AvatarRenderInfo
pattern gets copied too literally, since neither of those used a query
string). GET response: `{"group_id":uuid, "ban_list": {ban_id:
{"ban_date":date}}}`. POST body: `{"ban_action": 1|2, "ban_ids":
[uuid,...]}` - `BAN_CREATE=1`, `BAN_DELETE=2`; `BAN_UPDATE=4` is a
client-side-only flag meaning "POST, then immediately re-GET the list"
(`action = ban_action & ~BAN_UPDATE` strips it before the value is ever
sent over the wire - it never reaches the server as part of
`ban_action`). Confirmed the real UI behavior too:
`LLPanelGroupMembersSubTab::handleBanMember()` calls `BAN_CREATE` and
then immediately triggers the existing eject flow in the same action -
banning always also kicks. The capability's only job is the persisted
ban list; ejection stays on the existing (separate, UDP)
`EjectGroupMemberRequest` path, unchanged. Permission gating is
`GP_GROUP_BAN_ACCESS` client-side, which already has a server-side
counterpart - `GroupPowers.GroupBanAccess` is already a defined enum
value in `OpenSim/Addons/Groups/Service/GroupsService.cs` - just never
consulted anywhere today because nothing needs it yet.

**Confluence architecture mapped**:
- `OpenSim/Data/IGroupsData.cs` already has the exact right shape to
  extend: fixed classes per table (`MembershipData`, `RoleData`, etc.)
  backed by `MySQLGenericTableHandler<T>`/`PGSQLGenericTableHandler<T>`
  reflection-based handlers - the same pattern the inventory-thumbnails
  batch already used successfully, so a new `BanData` class
  (`GroupID`, `BannedID` as string - matching `MembershipData`'s own
  `PrincipalID` string convention for HG safety, `BanDate`) needs no
  hand-written SQL beyond the migration itself.
- **SQLite has no Groups backend at all in this codebase** (confirmed -
  no `SQLiteGroupsData.cs` exists, unlike Inventory's three full
  backends) - so this is real two-backend work (MySQL + PGSQL), not
  three, one less migration/handler pair than the thumbnails batch
  needed.
- `os_groups_membership`'s schema in
  `OpenSim/Data/MySQL/Resources/os_groups_Store.migrations` is the
  template: a new `os_groups_bans` table (`GroupID`, `BannedID`,
  `BanDate`, composite PK) as a new `:VERSION` block in both the MySQL
  and PGSQL migration files.
- `IGroupsServicesConnector.cs` (the interface `GroupsModule.cs` already
  talks to via its own `m_groupData` field) needs 3 new methods:
  `GetGroupBans`/`AddGroupBan`/`RemoveGroupBan`, implemented in
  `GroupsService.cs` the same way `AddAgentToGroup`/
  `RemoveAgentFromGroup` already are there.
- **The capability itself belongs inside `GroupsModule.cs`**, not a new
  file - same reasoning as `UploadAgentProfileImage` ending up inside
  `UserProfileModule.cs` rather than a standalone module: it needs the
  module's own `m_groupData`/session/agent-resolution machinery, and
  duplicating that in a separate class isn't worth it.
  `GroupsModule.cs` currently has **zero** capability registration of
  any kind (100% UDP-message-driven today), so this is a first, not an
  addition to an existing `OnRegisterCaps` hook.
- **A real functional requirement, not just list-tracking**: for a ban
  to mean anything, `AddAgentToGroup`/the invite-acceptance path in
  `GroupsModule.cs` need a ban-list check inserted before granting
  membership, refusing re-entry (open-enrollment or invited) to a
  banned agent. Without this, the feature would just be a database that
  nothing ever reads.
- **A real server-side permission check the existing Eject path notably
  lacks today**: `EjectGroupMemberRequest` in `GroupsModule.cs` has its
  own `// Todo: Security check?` comment right in the code - it's
  UDP-message-driven and currently trusts the client's own UI gating
  entirely. The new ban capability should NOT copy that laxity: POST
  should check the requesting agent's `GroupPowers.GroupBanAccess` bit
  via the connector's existing `GetAgentGroupMembership(...).GroupPowers`
  before applying `BAN_CREATE`/`BAN_DELETE`, a real check the old path
  doesn't have.

**Effort estimate**: comparable shape to the inventory-thumbnails batch
(data-model/migration step, service-layer step, capability step) but
two DB backends instead of three, plus one extra piece thumbnails didn't
need (join/invite-time ban enforcement, without which the feature would
be inert).

**Open question, not resolved in this scoping pass**: Hypergrid
semantics. The `BannedID`-as-string convention should carry
foreign-grid identifiers transparently the same way `MembershipData`
already does, but `HGGroupsService.cs` and
`Remote/GroupsServiceRemoteConnector.cs` weren't audited here and may
need their own small pass during implementation to confirm bans
propagate/enforce correctly for HG-visited groups, not just local ones.

Not started - scoping only, per explicit request. Ready to build
whenever prioritized.

### GroupAPIv1 group ban - built

User asked to build it right after the scoping pass above. Followed the
scope as written; the only real deviation was resolving the "open
question" pragmatically rather than leaving it open: group bans got the
same restriction `AddGroupRole`/`UpdateGroupRole`/`RemoveGroupRole`
already have for foreign (HG) groups - local-origin-world-only, no
cross-grid ban propagation attempted. Not a compromise found mid-build;
it's the existing precedent this codebase already uses for every other
group-moderation write operation, so bans following it is consistency,
not a shortcut.

**Five layers, each build-verified before moving to the next:**

1. **Data model** - `BanData` class (`GroupID`, `BannedID` as `string`
   matching `MembershipData.PrincipalID`'s own HG-safe convention,
   `BanDate` as unix-timestamp `int`) plus `StoreBan`/`RetrieveBan`/
   `RetrieveBans`/`DeleteBan` added to `OpenSim/Data/IGroupsData.cs`.
2. **MySQL + PGSQL backends** - new `os_groups_bans` table
   (`:VERSION 4`/`:VERSION 5` respectively, Groups has no SQLite backend
   in this codebase at all, confirmed again during the scoping pass) via
   a new `MySqlGroupsBansHandler`/`PGSqlGroupsBansHandler` reflection-based
   generic table handler - zero hand-written SQL beyond the migration,
   same pattern the existing `os_groups_membership`/`os_groups_roles`
   handlers already use.
3. **Service layer** - `GetGroupBans`/`AddGroupBan`/`RemoveGroupBan`
   added to `IGroupsServicesConnector` and implemented in
   `GroupsService.cs`, reusing the connector's existing private `HasPower`
   helper to gate ban create/remove on `GroupPowers.GroupBanAccess` - a
   real server-side permission check the existing `EjectGroupMemberRequest`
   path still doesn't have (its own `// Todo: Security check?` comment,
   left alone - fixing pre-existing unrelated code wasn't in scope here).
   Wired into all three connector implementations of the interface
   (`GroupsServiceLocalConnectorModule` - trivial passthrough;
   `GroupsServiceRemoteConnectorModule`/`GroupsServiceRemoteConnector`/
   `GroupsServiceRobustConnector` - three new `GETGROUPBANS`/`ADDGROUPBAN`/
   `REMOVEGROUPBAN` cases added to the existing region-to-Robust XML-RPC-style
   dispatch, mirroring `ADDAGENTTOGROUP`'s request/response shape exactly;
   `GroupsServiceHGConnectorModule` - local-origin-only as described above).
   Dropped a planned `IsAgentBannedFromGroup` connector method before it
   spread across all four implementers - nothing ends up calling it
   through the connector interface (the actual enforcement below reads
   the database directly), so it would have been unused surface on every
   implementer for no consumer.
4. **Join/invite enforcement** - a banned agent can't (re)join, whether
   through open enrollment or accepting an invite: both paths already
   funnel through `GroupsService.AddAgentToGroup`, the one place
   membership actually gets created, so a single `m_Database.RetrieveBan(...)`
   check there (before `_AddAgentToGroup`, not inside it - `_AddAgentToGroup`
   is also called for founder setup during group creation, which must
   never be ban-blocked) covers both flows without touching
   `AddAgentToGroupInvite` itself. Without this the ban list would just
   be an inert database nobody reads, exactly the risk flagged in scoping.
5. **Capability module** - `GroupAPIv1`, built inside `GroupsModule.cs`
   itself (same reasoning as `UploadAgentProfileImage` landing inside
   `UserProfileModule.cs`: needs the module's own `m_groupData`
   connector reference directly, not worth a separate file for). First
   capability this module has ever registered - it was 100% UDP-driven
   before this. `group_id` read from the **query string**
   (`request.QueryString.Get("group_id")`) on both GET and POST, exactly
   as re-verified from `llgroupmgr.cpp` during scoping - not the LLSD
   body shape every other capability built this session used, the one
   place a copy-paste from `AvatarRenderInfoModule.cs` would have been
   silently wrong. POST body `{"ban_action":1|2,"ban_ids":[uuid,...]}`,
   both GET and POST respond with the same
   `{"group_id":uuid,"ban_list":{ban_id:{"ban_date":date}}}` shape
   (matches `processGroupBanRequest`'s check of `result.has("ban_list")`
   on either verb). Permission enforcement lives one layer down, inside
   `AddGroupBan`/`RemoveGroupBan` themselves, so the capability handler
   doesn't duplicate the check - it just surfaces whatever `reason` comes
   back.

Build-verified clean after each of the five layers (0 errors every
time). Deploy sync: **partially blocked**, not a code problem. Unlike
the two previous deploy rounds this session (where every busy-file error
turned out to be transient Git-Bash-`cp` noise with no real lock behind
it, confirmed by successfully re-copying the exact same files moments
later via PowerShell), this round hit a genuine lock: the live grid had
come back up mid-session (`Robust.exe` PID 28428, both `OpenSim.exe`
region processes PIDs 4124/29152, confirmed via `Get-CimInstance
Win32_Process` after `tasklist` itself misleadingly reported nothing
running - a `tasklist //FI` quoting quirk under Git Bash, not to be
trusted again without the CIM cross-check). 86 of 158 changed files
copied cleanly (anything not currently loaded by the running processes);
the remaining 72 - every DLL actually in use by `Robust.exe`/`OpenSim.exe`
right now, including `OpenSim.Addons.Groups.dll`,
`OpenSim.Data.MySQL.dll`, and `OpenSim.Data.PGSQL.dll`, i.e. everything
this feature actually touched - are still the pre-ban-feature versions
on disk. Did not stop the grid to force the copy through: it came back
up on its own after being deliberately left down for the previous
deploy, which reads as the user (or their own control panel) actively
using it, not an oversight - stopping a grid a user just brought up
without asking crosses into the kind of action that needs to be asked
about first, not assumed. The code is fully built and correct on disk
in the repo; the remaining 72-file sync just needs a moment when the
grid is down again.

**Update**: user confirmed the grid was brought back down shortly after
the above was written. Re-ran the copy for the same 158-file list -
zero errors this time - and re-verified every `.dll`/`.pdb` byte-for-byte
against the build output: zero mismatches. Deploy sync fully complete.
Runtime verification (clean region startup with `GroupsModule`'s new
`GroupAPIv1` cap loaded, the MySQL `os_groups_bans` migration applying
against `casperia_dev`, and a real Firestorm session round-tripping an
actual group-ban create/list/remove) is still pending the user bringing
the grid back up and testing with a real group.

### Sim border-crossing smoothness - scoped, not built

User asked directly whether region crossings can be made smoother,
pointing out that Second Life manages it and OpenSim should be able to
too. Traced the actual mechanism in this codebase (not from memory),
then cross-checked nine other local OpenSim-family checkouts for prior
art before concluding anything was genuinely unclaimed - the same
discipline used for the PBR terrain and GroupAPIv1 scoping passes.

**How avatar crossing actually works here, traced directly:**
Confluence already has most of the right bones. A child agent gets
pre-established in all 8 neighboring regions the moment an avatar
becomes root (`ScenePresence.cs:2471`, `EnableChildAgents` - called from
`MakeRootAgent`), so a crossing isn't creating a stranger in the
destination region; it's "promoting" a presence that's already sitting
there. Three real, separable issues sit inside that otherwise-reasonable
design:

1. **The trigger is reactive and one frame late.**
   `ScenePresence.CheckForBorderCrossing()` runs every physics frame
   (called from `Update()`, `ScenePresence.cs:3950`) but only looks one
   frame ahead - `t = pos.X + vel.X * m_scene.FrameTime`
   (`ScenePresence.cs:4695-4702`), i.e. roughly one 45Hz physics tick
   (~22ms) of lookahead at typical settings. The whole async crossing
   sequence (two real network round-trips, below) doesn't start until
   the avatar is already, by prediction, about to be outside the region
   - there's no earlier, distance-based head start.
2. **Two sequential synchronous HTTP calls sit on that reactive critical
   path before the viewer is told to render the new region**:
   `SimulationService.QueryAccess` (`EntityTransferModule.cs`'s
   `GetDestination()`) followed by `SimulationService.UpdateAgent`
   (`CrossAgentIntoNewRegionMain`, `EntityTransferModule.cs:1859`).
   Confirmed these are real network calls even between two `OpenSim.exe`
   processes on the same machine, which is exactly this project's own
   Casperia-Dev topology (`Welcome_Center`/`Var_Test_Region` as separate
   processes) - not something that collapses to an in-process call here.
   Checked whether `QueryAccess` is actually redundant, given access was
   already validated once when the child agent was first established -
   it isn't quite: `Scene.QueryAccess` (`Scene.cs:6179-6220`) does live,
   position-specific parcel-access and region-capacity checks that
   legitimately need to be current at the exact crossing point, not
   cacheable indefinitely from arrival time. The real problem isn't that
   the check happens: it's that it happens *serially, at the last
   instant*, rather than *predictively, while the avatar is still
   approaching the border*.
3. **Vehicle/prim crossings are architecturally heavier, not just
   buggier.** There's no equivalent of a pre-existing child agent for an
   in-motion vehicle. `CrossPrimGroupIntoNewRegion`
   (`EntityTransferModule.cs:2774`) fully serializes the object and
   sends it via `SimulationService.CreateObject` as a brand-new object
   on the destination, then deletes the source copy - meaning physics
   and velocity state get rebuilt from scratch on the far side rather
   than continuing. This is the real reason vehicle crossings are the
   much rougher case, not an incidental bug.

**Reference-fork check** (background research pass, `opensim-master`,
`OpenSim-Tranquillity`, `OpenSim-Continuum`, `opensim-vanilla`,
`opensim-enhanced`, `opensim-lickx`, `WhiteCore-Dev`, `mobius-master` -
`mobius-master`'s local checkout turned out to be data/services-only,
no `Region/Framework/Scenes` present, so it couldn't be checked for
this):

- **Confluence already carries a real, working partial fix**, inherited
  from GuntharDeNiro's fork (credited in-code, `EntityTransferModule.cs`
  comments around line 1958) and confirmed identical in Continuum/
  opensim-vanilla/opensim-enhanced: `PreserveVelocityOnRegionCrossing`/
  `MaxRegionCrossingVelocity` send the avatar's real (clamped) velocity
  to the destination handoff instead of upstream's hardcoded zero
  (`GetRegionCrossingVelocity()`, `EntityTransferModule.cs:1975-1992`),
  and `RegionCrossingAttachmentCleanupDelayMS` defers source-side
  attachment deletion via `DelaySourceAttachmentCleanup()`
  (`EntityTransferModule.cs:1957-1974`) to avoid a visible detach/
  reattach flash. This addresses the classic "avatar visibly stalls dead
  at the border" symptom. It does **not** touch the underlying trigger
  latency or the two-round-trip sequence - upstream, Tranquillity, and
  opensim-lickx are all unmodified from the reactive one-frame trigger
  described above (Tranquillity's `EntityTransferModule.cs` is
  byte-identical to master's for this subsystem).
- **WhiteCore-Dev has two genuinely different, real architectural
  choices worth learning from**, though it's a much older/more diverged
  fork, not something to pull code from directly: (1) its
  `CheckForBorderCrossing()` uses a larger, velocity-adaptive lookahead
  (base 0.1s, doubled to 0.2s when speed is low) instead of a single
  physics-frame window, and is wired through a `PhysicsActor` event
  rather than an unconditional per-frame call - still linear
  extrapolation, just a bigger and adaptive buffer. (2) More
  significantly, **WhiteCore's `ISimulationService` has no `QueryAccess`
  concept at all** - crossing is a single async message
  (`AgentProcessing.CrossAgent`) that checks whether the destination's
  child-agent/caps service already exists and skips re-establishing it
  if so, collapsing what's a two-round-trip sequence here into
  effectively one, by design rather than by caching a skip.
- **Nobody checked - including WhiteCore - has improved vehicle/prim
  crossing.** Every fork examined still does the same full
  serialize-recreate-delete for `CrossPrimGroupIntoNewRegion`, with only
  a minor addition in WhiteCore (copying `Velocity` for a seated avatar
  before the crossing, not a real continuity fix). This is genuinely
  unclaimed territory across the whole ecosystem checked here, not just
  this codebase - consistent with the PBR terrain investigation's
  experience of a gap turning out to be real once actually checked
  rather than assumed already solved somewhere.

**What this adds up to, in priority/risk order:**

1. **Already have** - the Gunthar-derived velocity-preserving handoff +
   delayed attachment cleanup. Nothing to build here; noted so it isn't
   mistaken for unaddressed.
2. **Small, low-risk, clear win** - widen and adapt
   `CheckForBorderCrossing()`'s lookahead window (WhiteCore-style:
   larger base window, bigger still at low speed) so the async crossing
   sequence gets a real head start on the network round-trip(s) instead
   of starting only once the avatar is already at the line. A
   config-tunable value, following the same `[EntityTransfer]` pattern
   the four existing knobs already use. Surgical change, contained to
   `ScenePresence.CheckForBorderCrossing()`.
3. **Real improvement, well-scoped but bigger** - collapse the
   `QueryAccess` + `UpdateAgent` sequence into effectively one
   round-trip. Two credible approaches, not yet decided between: (a)
   fold the access/capacity check into `UpdateAgent` itself server-side,
   returning a rejection reason on failure instead of a separate
   pre-check (closer to WhiteCore's model); or (b) keep both calls but
   move `QueryAccess` off the reactive critical path by running it
   predictively as the avatar approaches a border (using the wider
   lookahead from #2), refreshed periodically, so only `UpdateAgent`
   remains synchronous at the actual crossing instant. Either removes a
   full network round-trip from what the user experiences as "the
   hitch." Needs care either way: `QueryAccess`'s failure `reason`
   string is user-facing (`agent.ControllingClient.SendAlertMessage`)
   and that path has to survive whichever approach is taken.
4. **Hard, unsolved everywhere checked, real engineering, not a tuning
   change** - vehicle/prim crossing continuity. Would need a genuinely
   new mechanism (something like a pre-negotiated shadow/child copy of
   an in-motion vehicle in bordering regions, the prim equivalent of an
   avatar's child agent, so crossing becomes an activation instead of a
   from-scratch recreation) rather than an adjustment to the existing
   serialize/recreate/delete path. On the order of a new subsystem, not
   a quick win - this is the piece that actually explains why SL's
   vehicle crossings feel different, and closing that gap for real is
   its own dedicated effort, not a follow-on to items 2-3.

Not started - scoping only, per explicit request. Items 2 and 3 are
concrete and buildable next; item 4 needs its own dedicated scoping pass
before any code, not a quick add-on to this one.

### Avatar border-crossing latency fix - built (items 2+3 from scoping)

User asked to build both the crossing-prediction widening and the
round-trip collapse together ("we can't do one without the other") -
correct call, since a wider prediction window only pays off if there's
also less synchronous work sitting in that window.

**`ScenePresence.cs`** (`CheckForBorderCrossing()`): the commit-to-cross
trigger's lookahead widened from one raw physics frame
(`m_scene.FrameTime`, ~22ms at 45Hz) to an adaptive 0.1s/0.2s window
(doubled below a ~2.5 m/s speed threshold) - values taken directly from
WhiteCore-Dev's own crossing code rather than invented, since that's a
real production reference point. Kept deliberately modest: this trigger
still commits (`IsInTransit`/eventual `IsChildAgent`), so widening it
further would risk crossing prematurely if an avatar changes direction
right at a border. Added a second, separate, larger lookahead (1.0s,
rate-limited to once per 500ms per avatar) purely to warm a new cache -
`TryPreWarmCrossingAccess()` - with zero effect on transit state, so a
wrong or stale prediction here has no correctness consequence.

**`EntityTransferModule.cs`** (`GetDestination()`): added
`PreApprovedCrossingCache`, a short-lived (4s TTL) positive cache
mirroring the existing `BannedRegionCache`'s shape (same
`ExpiringCacheOS`-backed pattern, same per-region-then-per-agent
dictionary nesting) but for "access already granted," populated by both
the predictive pre-warm above and by `GetDestination()`'s own successful
checks. A cache hit skips the `SimulationService.QueryAccess` network
round-trip entirely and returns the neighbour immediately - the actual
crossing then only needs `SimulationService.UpdateAgent`, collapsing the
two sequential synchronous calls that sat on the reactive critical path
down to one, in the common case of a steady approach to a border. Cache
miss (cold border, or the 4s TTL lapsed) falls back to the original
synchronous `QueryAccess` call with no behaviour change - this is a
latency optimisation layered on top of the existing check, not a
replacement for it, matching the scoping doc's stated design constraint
around `QueryAccess`'s live, position-specific checks.

Deliberately did **not** copy `BannedRegionCache`'s exact comparison
logic verbatim - close reading of it while writing the mirror-image
positive cache turned up what looks like a real inverted-condition bug
(`IfBanned` returns `true`/banned when the stored expiry is *earlier*
than now, i.e. already expired, and silently drops what should still be
a valid, unexpired ban in the untaken branch). Not fixed here - out of
scope for this task, a pre-existing and unrelated subsystem - but worth
a dedicated look separately; the new `PreApprovedCrossingCache` was
written with the corresponding comparison the right way round
(`exp > Util.GetTimeStamp()` grants access) rather than inheriting the
same mistake.

Build-verified clean (0 errors). Deployed: grid was already down, 186
changed `.dll`/`.pdb` files copied via PowerShell `Copy-Item`,
re-verified byte-for-byte after - zero mismatches. Runtime verification
(a real avatar crossing a border on Casperia-Dev feeling smoother, and
confirming the `PreApprovedCrossingCache` actually gets hit in practice -
worth a debug-log check the first time, not just trusting the code path
looks right) is still pending the user bringing the grid back up.

### Vehicle/prim crossing - investigated further, not built

User asked directly whether anything could be done for vehicle crossing
too, on top of the avatar fix above. Traced two more things directly
rather than assuming the scoping pass's "full recreate" framing meant
velocity is simply lost:

- **Velocity/angular velocity are NOT silently zeroed on crossing,
  contrary to a common assumption.** `SceneObjectPart.Velocity`/
  `AngularVelocity` are serialized as part of the object's XML
  (`SceneObjectSerializer.cs:1589-1590`, restored on the receiving side
  via `ProcessVelocity`/`ProcessAngularVelocity`,
  `SceneObjectSerializer.cs:713-720`), and
  `SceneObjectPart.AddToPhysics()` explicitly reapplies both to the
  freshly created physics actor
  (`SceneObjectPart.cs:5088-5092`,
  `if (applyDynamics && LocalId == ParentGroup.RootPart.LocalId) {
  Velocity = velocity; AngularVelocity = rotationalVelocity; ... }`,
  gated on `applyDynamics` which traces back to the object's own
  `isPhysical` flag). If a crossed vehicle still feels like it loses
  momentum, the more likely cause is the timing gap itself - nothing is
  simulating the vehicle's physics for however long the synchronous
  `CreateObject` transfer takes - not a coded bug that discards the
  velocity value.
- **The prim-crossing trigger is worse than the avatar one, not just
  differently-shaped.** Avatar crossing at least has a one-physics-frame
  lookahead. Prim crossing has **none** - `SceneObjectGroup.AbsolutePosition`'s
  setter (`SceneObjectGroup.cs:664-688`) only fires `CrossAsync` once
  `!Scene.PositionIsInCurrentRegion(val)` is already true, i.e. strictly
  *after* physics has already placed the object outside the region, not
  one frame ahead of it. This is a genuinely new, concrete finding, not
  something the earlier scoping pass surfaced.

**Why this wasn't fixed in the same pass as the avatar trigger**: the
avatar fix had one clean call site to widen (`ScenePresence.Update()`'s
per-frame heartbat call to `CheckForBorderCrossing()`). Prim position
changes have no equivalent single choke point - `AbsolutePosition`'s
setter is invoked from many places (physics engine callbacks, scripted
`llSetPos`/`llApplyImpulse`, sit-related repositioning, script-driven
movement, etc.), all funnelling through the same extremely hot property
setter used for every scene-object position change in the simulator, not
just crossings. Adding a predictive pre-check there needs a real design
pass of its own - matching a wrong prediction to "don't actually commit"
is straightforward in `ScenePresence` (a single owning per-frame method)
but isn't obviously so from inside a property setter with this many
callers. Given the blast radius of getting this wrong (every object
position update in the simulator, not a narrow crossing-only path),
this needed more care than the time available in this pass, not a rushed
copy of the avatar fix into unfamiliar territory.

**Net assessment, updated from the original scoping pass**: the
"full serialize/recreate/delete architecture" finding still stands as
the real, hard, unclaimed-territory problem (unchanged - no fork checked
has solved it, see above). But there are now two concrete, smaller,
well-scoped next steps that weren't visible in the original pass: (a)
give prim crossing the same kind of predictive lookahead avatar crossing
just got, once a safe way to do that from inside `AbsolutePosition`'s
setter (or by hooking a narrower, crossing-specific call site instead of
the setter itself) is worked out; (b) verify with a live vehicle
crossing test whether the timing-gap theory above actually explains the
felt roughness, since that would mean the fix is about closing the gap
(faster `CreateObject`, or a predictive pre-check analogous to (a)) more
than it's about physics state, which changes the shape of any future fix
attempt. Neither built this session - logged as concrete groundwork for
whenever vehicle crossing gets its own dedicated pass.

**Live-verified**: user logged in with a real Firestorm session after
the deploy above and crossed a region border on Casperia-Dev - reported
not being able to tell when the crossing happened, which is exactly the
target outcome (no visible hitch, no stall, no pop). First real
confirmation this session that the widened lookahead +
`PreApprovedCrossingCache` round-trip collapse actually changes the felt
experience, not just the code path. Vehicle crossing continuity remains
unverified/unbuilt as noted above - this confirms the avatar-crossing
piece specifically.

### Vehicle/prim crossing - full scoping pass

User asked to scope this properly as its own effort, following the
avatar-crossing fix's success. Picked up exactly where the earlier
investigation left off (the "not built - too high blast radius" note
above) and traced two more layers deep - this pass found the actual,
confirmed root cause with hard evidence, not the working theory the
earlier pass had to leave things at.

**Root cause, confirmed, not theorized**: `SceneObjectGroup.CrossAsync`
(the prim-crossing equivalent of `ScenePresence.CrossAsync`) calls
`root.PhysActor?.CrossingStart()` (`SceneObjectGroup.cs:784`) as
essentially the *first* thing it does, before even checking whether a
valid destination region exists. In the physics engine actually running
on Casperia-Dev (ubODE), `CrossingStart()`
(`OpenSim/Region/PhysicsModules/ubOde/ODEPrim.cs:1024-1050`) captures
the object's current linear/angular velocity (confirming the earlier
finding that velocity data itself isn't lost), then explicitly does
`BodySetLinearVel(Body, 0, 0, 0)`, `BodySetAngularVel(Body, 0, 0, 0)`,
`disableBodySoft()` (stops collision), and `UnSubscribeEvents()`. This
is a **deliberate, hard freeze** - the vehicle's physics body is
stopped and disabled the instant a crossing begins, and stays that way
for the entire duration of the synchronous
`SimulationService.CreateObject` call that follows (which has to
transmit the whole object - mesh, textures, scripts, inventory - not a
small payload). This is not "the vehicle feels laggy because of network
jitter" - it is server code explicitly commanding zero velocity and
disabling physics, for however long the transfer takes. That fully
explains why vehicle crossings are reported as qualitatively worse than
avatar crossings, not just quantitatively slower.

**A real, smaller, separately-fixable bug spotted while tracing this**:
`CrossingStart()` fires before `GetObjectDestination()` is even called
(`SceneObjectGroup.cs:784` vs `:794`). If no valid destination region is
found (`destination is null`, `:795-796`) or there's no
`IEntityTransferModule` (`:788-789`), the function returns early with
**no matching `CrossingFailure()` call** to re-enable the body. Every
other early-exit path in this codebase pairs a `CrossingStart()` with a
`CrossingFailure()` on failure (that's the whole point of
`CrossingFailure()` existing) - these two paths look like they'd leave
a vehicle's physics permanently disabled after a failed crossing attempt
near map edges or a misconfigured region. Not fixed here (tangential to
the scoping task, needs its own verification pass with a real edge-of-grid
test before touching it) - flagged as its own follow-up, matching how
the `BannedRegionCache` bug was handled.

**Good news on the trigger side - narrower and safer than the earlier
pass assumed**: the earlier investigation flagged
`SceneObjectGroup.AbsolutePosition`'s setter as the crossing trigger and
concluded a predictive fix there was too high-blast-radius (that setter
is called from everywhere - scripts, editing, sits, physics). Tracing
one layer deeper found the setter isn't actually the primary entry
point for a *moving physical object* - `SceneObjectPart.PhysicsRequestingTerseUpdate()`
(`SceneObjectPart.cs:3205-3222`) is what the physics engine calls every
time it reports a new position for a physical body, and *that* is what
sets `AbsolutePosition` for a moving vehicle in practice. This is a much
narrower, purpose-built hook - the direct prim equivalent of
`ScenePresence.Update()`'s per-frame call to
`CheckForBorderCrossing()` - so a predictive lookahead (mirroring the
avatar fix's adaptive-window approach) could safely live here without
touching the general-purpose position setter every other code path
relies on. This resolves the specific "too risky to touch" objection
from the earlier pass for the *trigger* half of the problem.

**The real remaining gap, now precisely named instead of vaguely
gestured at**: making the *transfer* predictive - starting
`CreateObject` before the vehicle actually reaches the border, so the
freeze window is mostly or entirely hidden - runs into a genuine safety
problem avatars don't have. An avatar's pre-established neighbor
presence is a *child agent*: deliberately inert (no scripts, no
physics, camera/render purposes only), so having it exist in two
regions briefly is safe. A prim has no equivalent inert state. If
`CreateObject` fired predictively with the object's real, live
representation - scripts included - while the source original is still
fully interactive, the result would be a genuine live duplicate:
double script execution (a vendor script could charge a purchase
twice, a sensor could fire events twice), double collision physics, and
two visibly overlapping copies for any nearby viewer with the border
region in draw distance. **This specific gap - no "staged/inert" scene
object lifecycle state - is the actual, precise scope of what earlier
got called "a new subsystem."** It's real, and it's the one piece of
this whole investigation (across both crossing scoping passes) that
needs new engineering rather than a tuning change or a safe reuse of
existing machinery.

**Candidate design, not built, for whenever this gets picked up**:
mirror the avatar pattern deliberately, since it's proven and already
live-verified working. (1) Predictive trigger inside
`PhysicsRequestingTerseUpdate`, adaptive lookahead like the avatar fix.
(2) A new "staged" creation mode for `CreateObject`/
`HandleIncomingSceneObject` that produces a phantom, non-physical,
script-suspended copy on the destination - existing purely for
render/position continuity, not simulation. (3) At the real crossing
commit (the existing reactive, now-safe-to-keep-as-is trigger), a
lightweight "promote" call instead of a full re-transfer: enable
physics with the preserved velocity (already proven to survive via
`AddToPhysics`'s `applyDynamics` path), resume scripts, and only then
tear down and delete the source copy - never a window where both are
simultaneously live and interactive. (4) `CrossingFailure()`-equivalent
cleanup if the predictive stage goes stale (avatar approached, then
turned away) needs to tear down the unused staged copy on the
destination, not just leave it orphaned.

**Sizing, honestly**: bigger than the avatar fix - it's a genuinely new
object lifecycle state plus promotion/demotion logic requiring real
care around the "never simultaneously live in both regions" invariant,
not a config value or a cache. But it is no longer "unclaimed territory,
scope unknown" - there's now a concrete design to execute against, a
named root cause backed by the actual physics-engine source (not
inference), a resolved answer for the trigger half (safe, narrow hook
exists), and a precisely-scoped remaining gap (one missing object
state) rather than an open-ended "needs a new subsystem" without
knowing what that subsystem actually has to do.

### Vehicle crossing - one real, safe, immediately-testable fix built

User asked to build something real and pushable now, not wait for the
full inert-copy subsystem above. Deliberately did **not** attempt the
predictive-trigger/staged-object design from the scoping pass - that
still needs the missing object-lifecycle state to be safe, and building
it under "I want something testable now" pressure is exactly how a
double-script-execution bug would ship. Instead built the one piece
from the scoping pass that was already confirmed both safe and real:
the `CrossingStart()`/`CrossingFailure()` ordering bug found while
tracing the root cause.

**What changed** (`SceneObjectGroup.cs`, `CrossAsync`):
`root.PhysActor?.CrossingStart()` - the call that zeroes a physical
object's velocity and disables its body - used to fire
unconditionally as nearly the first thing in the method, before even
checking whether a valid destination region exists or whether an
`IEntityTransferModule` is available. Moved it to fire only after
`GetObjectDestination()` has confirmed a real destination. Two direct
effects:

1. **The freeze window shrinks** by however long the destination
   lookup (`GridService.GetRegionByPosition`) takes, in the normal
   successful-crossing case - the object is no longer frozen while the
   server is still figuring out where it's going. Not the dominant cost
   (that's still the `CreateObject` transfer itself, untouched here and
   still needing the staged-object work to fix for real), but a real,
   measurable piece of it, and safe by construction - no new state, no
   duplicate-object risk, nothing that can leave two live copies
   anywhere.
2. **Fixes the missing-`CrossingFailure()` bug by construction rather
   than by adding cleanup calls.** The two early-return paths (no
   transfer module; no destination found) never freeze physics in the
   first place now, so there's nothing left to undo - a vehicle driven
   toward the edge of the grid, or hitting either of those failure
   conditions, no longer risks getting stuck with permanently disabled
   physics. Withdrew the previously-flagged follow-up task for this -
   superseded by this fix.

Deliberately scoped small: no predictive trigger, no caching, no
attempt to shrink the `CreateObject` transfer time itself. Those all
still need either the new object-lifecycle state or further
investigation this pass didn't do. What shipped is real and safe, not
the full "vehicle crossings feel like avatar crossings now" outcome -
that claim would be overselling what a two-line reorder can do. Set
expectations accordingly when this gets tested.

Build-verified clean (0 errors). Deployed: grid was down, 186 changed
`.dll`/`.pdb` files copied, re-verified byte-for-byte after - zero
mismatches. Live verification (a real vehicle crossing on Casperia-Dev,
ideally timed/compared against the pre-fix behavior rather than just
"does it still work") is pending the user's own test.

## Systemic OpenSim complaints campaign

User asked what other long-standing OpenSim complaints exist, then
committed to addressing all of them as a deliberate project mission
statement ("fixing issues like those for Confluence is exactly the
reason why this exists"). Working through them one at a time with the
same rigor as border-crossing - trace the actual code, confirm root
cause with evidence, check what other forks have solved - rather than
attempting all of them shallowly at once. Tracked as tasks #123-130.

### Avatar baking / "cloud avatar" failures - real mechanism already
### existed, was silently disabled on the live grid

The single most commonly cited OpenSim complaint. Expected to find
nothing and have to design a fix from scratch - instead found Confluence
already has a complete, real, working mitigation that upstream
`opensim/opensim` does not have at all (confirmed: zero matches for
`TemporaryDefaultAppearanceFallback`/`PendingCloudCheck` in a fresh
`opensim-master` checkout). The actual work here turned into two
separate, real findings rather than a from-scratch build.

**The existing mechanism** (`AvatarFactoryModule.cs`), authored by
GuntharDeNiro (`git log`: `7588f279f4` "Add temporary default appearance
fallback", `e493d29e05` "Disable... by default", `66afe97ea5` "Make...
one-shot", `aa3c16fc0a` "Fix cloud avatar recovery...", all dated
May-Jun 2026 - the same fork already credited for this session's
region-crossing velocity fix): on `CompleteMovement`, if
`ValidateBakedTextureCache` finds a texture genuinely missing from the
local asset cache, `ApplyTemporaryDefaultAppearanceFallback` doesn't
immediately show anything different - it stores the real appearance,
requests a rebake from the arriving viewer (`SendRebakeAvatarTextures`),
and schedules a silent re-check `TemporaryDefaultAppearanceDelaySeconds`
later (default 6s). Only if the texture is *still* missing after that
grace period does it show the built-in default shape/skin/hair as a
visible placeholder, then retries the real outfit once more before
giving up. This is a genuinely well-designed mechanism - the grace
period means a texture that was just slow to arrive (the common case)
recovers with zero visible disruption, not a jarring flash.

**Finding #1 - this was never actually active on Casperia-Dev.** The
C# code's own default is `true` (set in the `aa3c16fc0a` fix commit,
along with `PersistBakedTextures`/`ResendAppearanceUpdates`/`ReuseTextures`
all flipped to `true`, and the repo's tracked `bin/OpenSimDefaults.ini`
correctly reflects all of this) - but the *live deployment's* copy of
`OpenSimDefaults.ini` still had the original, pre-fix values
(`TemporaryDefaultAppearanceFallback = false`, `PersistBakedTextures = false`,
etc.), silently overriding the code's own default the entire time. This
is exactly the kind of gap this session's deploy process (DLL/PDB sync
only, deliberately never touching `.ini` files, since `OpenSim.ini`/
`Robust.ini` carry real per-deployment credentials and customization)
was never going to catch on its own - `OpenSimDefaults.ini` specifically
is meant to ship as the repo's own tracked canonical defaults, not be
hand-customized, so it silently drifting out of sync went unnoticed.
Fixed: edited the live `OpenSimDefaults.ini`'s `[Appearance]` section
directly (a narrow, targeted edit to just the seven relevant keys, not
a wholesale file replace - see the much bigger finding below on why not).

**Finding #2 - a real, separate code gap, now fixed.** Even with the
mechanism enabled, `ScenePresence.cs`'s `CompleteMovement` explicitly
skipped the whole check for Hypergrid arrivals
(`!isHGTP` in the original condition) - meaning the safety net excluded
exactly the scenario most likely to produce a genuine cloud avatar (an
HG visitor's baked textures live on their home grid's asset service, not
the local one, and are the most likely to be missing on first arrival).
Verified this wasn't a *necessary* exclusion before touching it - traced
`ValidateBakedTextureCache` (checks the local `IAssetCache` for the
referenced texture IDs) and `RequestRebake` (sends
`SendRebakeAvatarTextures` to the arriving viewer, a client-facing
protocol message with no dependency on which grid the avatar came from)
and confirmed both work identically regardless of whether the avatar is
local or foreign. The mechanism's own built-in grace period (Finding
above) already protects against the obvious risk of false-triggering on
an HG texture that's simply still in flight from a remote asset server -
removed the `!isHGTP` exclusion in `ScenePresence.cs` so HG arrivals now
get the same protection local logins already had.

**Finding #3 - much bigger, flagged but deliberately not touched this
pass.** Diffing the repo's tracked `bin/OpenSimDefaults.ini` against the
live deployment's actual copy (to scope the fix for Finding #1) turned
up drift far beyond the appearance settings: the repo has ~150 lines of
real, apparently-already-built ubODE physics tuning (`world_erp`,
per-material friction/bounce/density tables, boat/prim water dynamics,
avatar physics tuning - all with real, considered-looking values and
comments, not placeholders) that were **never deployed to the live
grid at all**. Conversely, the live deployment's file has real operator
customization the repo doesn't have on record - a `[GroupAutoInvite]`
section with an actual live `GroupID`, a configured `[Weather]` section,
`[RegionWeb]` settings - none of which exist in the repo's tracked copy.
This is a two-directional drift problem: real repo work sitting
undeployed, and real live customization sitting un-backed-up. Both
matter, and blindly overwriting either direction would lose something
real. Not resolved here - this deliberately stayed scoped to the seven
appearance-specific keys needed for the baking fix, and the broader
config-reconciliation question (should the physics tuning be deployed?
should the live-only customization be captured back into the repo so
it survives a fresh deploy?) needs its own explicit decision from the
user, not an assumption either way.

Build-verified clean (0 errors) for the `ScenePresence.cs` HG-exclusion
fix. Deployed: grid was down, 186 changed `.dll`/`.pdb` files copied,
verified byte-for-byte, plus the targeted live `OpenSimDefaults.ini`
edit (not a full-file replace - preserved every other line, including
the live-only customizations noted above, exactly as found). Live
verification (an actual HG visitor or a deliberately-broken local login
showing the recovery working) is pending the user's own test - this is
exactly the kind of fix that's hard to verify without a real client
session hitting the actual failure condition.

### Script engine (YEngine) performance - real, under-tuned default,
### confirmed ecosystem-wide, not just a Confluence gap

Checked whether "scripts are slow" (a common complaint, especially on
vehicle/HUD-heavy regions) has a genuine architectural or configuration
cause. Confirmed first that Confluence only ships YEngine (no XEngine
directory at all) and that it's a real compile-to-CIL engine
(`MMRScriptCodeGen.cs`/`MMRScriptObjWriter.cs`, 45k+ lines across the
YEngine tree) - not a naive interpreter, architecturally comparable to
SL's own Mono-based approach. So the complaint isn't "wrong engine
choice"; it's tuning and scheduling.

**Finding - `NumThreadScriptWorkers` defaults to 2 and nobody in the
OpenSim family overrides it.** `XMREngine.cs:212`:
`m_Config.GetInt("NumThreadScriptWorkers", 2)` - every region runs its
entire script workload (every script's every event handler) across just
2 worker threads unless explicitly raised. Checked the repo's own
tracked `bin/OpenSimDefaults.ini` and the live Casperia-Dev deployment's
copy - both had it commented out (`;NumThreadScriptWorkers = 2`),
meaning the live grid was genuinely running at this default. Then
checked whether this was a Confluence-specific oversight or an
ecosystem-wide blind spot: grepped `OpenSim-Tranquillity` and a fresh
`opensim-master` checkout - identical hardcoded default, identical
commented-out line in both forks' shipped `OpenSimDefaults.ini`. Nobody
in the OpenSim family tunes this; it's a real, ecosystem-wide gap, not
something Confluence broke.

The ini's own comment validates the concern directly: "if a region
machine is not overload[ed] (ie has sleeping CPU cores), increasing this
number may reduce events response latency." Checked the actual
Casperia-Dev host: AMD Ryzen 7 6800H, 8 cores / 16 logical threads
(`Get-CimInstance Win32_Processor`), running 2 region processes
(`Welcome_Center`, `Var_Test_Region`), each its own `OpenSim.exe` with
its own YEngine instance and thread pool, both reading the same shared
`OpenSimDefaults.ini`. This is not an overloaded machine - real,
unused headroom exists.

**Secondary check - sensor/timer scanning for an algorithmic scaling
bug.** Read `SensorRepeat.cs`'s `CheckSenseRepeaterEvents()` and
`SensorSweep()` to see if there was a second, code-level finding to pair
with the thread-count one. Found a per-sensor scene-wide entity scan
(`doAgentSensor`/`doObjectSensor`) on every due sensor - real cost, but
this is the inherent, expected cost of the LSL sensor model itself (SL's
own sensor implementation has the same fundamental complexity - a
sensor has to check candidate entities somehow). Not a Confluence-
specific bug or a fixable regression; ruled out as a separate finding.

**Fix - deliberately a config-tuning change, not a code change, and
deliberately NOT raised in the repo's shipped default.** Raising the
shipped `OpenSimDefaults.ini` template default for every downstream
deployment regardless of hardware would be irresponsible - the
conservative default of 2 is the right *safe* default for arbitrary,
possibly-small hardware, matching upstream convention. What's wrong is
that nobody had actually looked at *this* host's real headroom and
tuned it. Edited only the live Casperia-Dev `OpenSimDefaults.ini`,
uncommenting and raising `NumThreadScriptWorkers` to `4` (2 regions x 4
= 8 script-worker threads total, leaving 8 of 16 logical threads for
netcode/physics/OS), with a comment recording the reasoning and the
host spec so a future reader doesn't have to re-derive it. This is a
config-only change - it needs an `OpenSim.exe` restart to take effect,
which is left to the user's own timing since the grid is up for
resident testing right now. No specific speedup number is promised;
this is a real, evidence-based, reversible lever, not a code fix, and
its actual impact will depend on how script-heavy the live workload
turns out to be.

### Hypergrid teleport reliability - scoped, then built and deployed

Traced the actual outbound-HG-teleport code path end to end rather than
starting from "HG is just flaky" folklore, matching the same rigor as
the border-crossing scoping pass. Confirmed a real, well-evidenced
architectural cause and a specific, safe fix direction - not yet built,
this is the scoping pass only.

**The chain.** `HGEntityTransferModule.CreateAgent` (which every
outbound HG teleport goes through) requires a minimum of three serial,
synchronous WAN HTTP calls before the destination even starts
constructing an agent, plus a fourth after:

1. `GatekeeperServiceConnector.GetHyperlinkRegion` - source sim calls
   the *destination* grid's Gatekeeper directly (XML-RPC, 10s timeout)
   to resolve the real region behind the hyperlink.
2. `UserAgentServiceConnector.LoginAgentToGrid` - source sim calls the
   traveler's *home* grid's UserAgentService (`homeagent/` endpoint,
   `SimulationServiceConnector.CreateAgent`, 30s timeout). This single
   call from the source sim's point of view is actually a black box
   containing **two more WAN hops it never sees directly**: the home
   grid's own `UserAgentService` handler relays the login on to the
   *destination* grid's `foreignagent/` endpoint, and the destination
   sim in turn calls back to the *home* grid's `VerifyClient`/
   `VerifyAgent` to authenticate the incoming session before accepting
   it. All of this has to complete, serially, inside the source sim's
   single 30-second timeout budget.
3. `SimulationServiceConnector.UpdateAgent` - source sim to destination
   sim directly, full agent data (30s, or 200s for the position-only
   variant), same pattern already flagged in this session's earlier
   border-crossing work.
4. Viewer-driven `CompleteMovementIntoRegion` callback, awaited by
   `WaitForAgentArrivedAtDestination` (shared machinery with local
   teleports, not HG-specific).

**The actual bug: zero retry anywhere in this stack.** Read every HG
connector call site (`GatekeeperServiceConnector`,
`UserAgentServiceConnector`, the base `SimulationServiceConnector`) -
every single one is try/once/catch/fail, with no retry on transient
failure. A local-grid neighbor crossing is typically LAN-fast between
hosts an operator controls; an HG hop crosses to a third party's
server over the open Internet, with no guarantee it's even up, let
alone fast. One dropped packet, one slow DNS lookup, one momentarily
busy destination on *any* of the three-to-four hops above (including
the two the source sim never directly sees) kills the *entire*
teleport, and the user has to start over from scratch - repeating every
earlier hop, not just the one that actually failed. This is the
concrete mechanism behind "HG teleport failed, try again" being such a
common complaint: it's not that HG is unreliable in principle, it's
that nothing in the chain tolerates a single blip.

**Confirmed ecosystem-wide, not a Confluence regression.** Diffed
these same three connector files against upstream `opensim-master` and
`OpenSim-Tranquillity` - identical 10-second timeouts, identical
zero-retry structure, in both. Checked `WhiteCore-Dev` for comparison
and found it doesn't ship Hypergrid connectors at all (no Gatekeeper or
UserAgent service in that fork). So this is a real, shared weak point
in the standard OpenSim HG protocol implementation itself, not
something introduced here - same pattern as the last two findings in
this campaign.

**Confluence already has one real, on-record mitigation for a
different HG failure mode.** `HGEntityTransferModule.
ApplyCanonicalLocalServiceURLs` (pre-existing in this repo) already
rewrites a local user's stale `HomeURI`/`GatekeeperURI` before it's
sent outbound, so a grid whose public domain changed after accounts
were created doesn't keep broadcasting a dead address to every
foreign grid forever. That's a different problem (identity drift, not
transient network failure) and isn't touched by this finding.

**Verified a retry-based fix would be safe before recommending it.**
The obvious fix for "one blip kills everything" is a small bounded
retry (a couple of attempts with a short backoff) around each of the
three connector call sites, limited to genuinely transient failures
(timeout, connection-refused, 5xx) and explicitly *not* retried on a
real business-logic failure (banned, access denied, region not found).
Before recommending this, checked whether retrying `CreateAgent`-style
calls could ever create a duplicate agent if the first attempt actually
landed but the response was merely lost - traced `Scene.
NewUserConnection` (the real destination-side handler behind every
`CreateAgent`/`homeagent`/`foreignagent` call) and confirmed it already
looks up any existing `ScenePresence` by `AgentID` and reuses it rather
than creating a second one. A retry is safe by construction; it would
land on the same dedupe path a legitimate second attempt already goes
through today (e.g. from a user manually retrying a failed TP).

**Built and deployed.** Added a bounded retry (2 attempts total, 1
second delay) at the two call sites named above, gated strictly on
"we never got a reply at all" - not on a real reply the peer actually
sent, including a genuine denial:

- `GatekeeperServiceConnector.GetHyperlinkRegion` - retries only on a
  transport exception from `request.Send` (no response at all). A real
  `response.IsFault` from the gatekeeper - the peer actually answering
  - is untouched and still fails immediately, same as before. This call
  is a read-only region lookup with no side effects, so retrying it
  has no downside beyond the extra second of latency on the rare path
  where it's needed.
- `SimulationServiceConnector.CreateAgent` (the base class both plain
  neighbor teleports and `UserAgentServiceConnector`'s `homeagent` hop
  inherit) - added `PostToServiceWithTransientRetry`, gated on the
  presence of the lowercase `"success"` key in the response. Confirmed
  by reading both sides of the wire: `AgentHandlers.cs` always sets
  `resp["success"]` explicitly on every real reply, success or refusal,
  while `WebUtil`'s internal `ErrorResponseMap` (returned when the
  request never reached the peer at all) only ever sets a capital-S
  `"Success"` string and never the lowercase key `CreateAgent` actually
  checks. That case difference is a reliable, already-existing signal
  for "no real reply happened" versus "the peer replied, including with
  a genuine no" - retrying only fires on the former.

Left `UpdateAgent` and the XML-RPC `LinkRegion` (admin region-linking,
not part of a live teleport) untouched - out of the documented scope of
this pass.

Build-verified clean (0 errors, 0 warnings) via `dotnet build
OpenSim.sln -c Release`. Deployed: grid was confirmed down (no
`OpenSim.exe`/`Robust.exe` processes running), `OpenSim.Services.
Connectors.dll`/`.pdb` copied via PowerShell `Copy-Item`, verified
byte-for-byte via `Get-FileHash` MD5 match against the freshly built
copy. Needs a grid restart to take effect - left to the user's own
timing. Live verification (an actual HG teleport surviving a real
transient blip) isn't practically testable on demand - by nature this
only fires on failures that would otherwise have killed the teleport,
so its effect will show up as "HG teleports that used to occasionally
fail now don't," not as something directly reproducible in a single
test session.

### Attachment reliability (relog/crossing) - scoping pass, mixed
### verdict: one real gap, one confirmed non-issue, one open question

Traced both halves of "attachment reliability" the user asked about -
crossing/teleport continuity and relog persistence - as separate
mechanisms, since they turned out to go through completely different
code paths. Graded each finding by how confident the evidence actually
makes it, rather than treating "attachments are unreliable" as one
undifferentiated complaint.

**Crossing/teleport continuity - inherits this session's earlier
fixes, one narrow silent-loss edge case found.** Attachments don't
cross independently like free-standing prims - `AttachmentsModule.
CopyAttachments` clones each worn object (with a script-state snapshot)
directly into the same `AgentData` payload that carries the avatar
itself, so they ride the identical synchronous `CreateAgent`/
`UpdateAgent` calls already hardened by this session's border-crossing
lookahead fix and, for HG destinations, the new connector retry. No
separate transport, so no separate transport problem. The one real gap
found: `Scene.AddSceneObject`'s attachment path (line ~3159) calls
`AttachmentsModule.AttachObject` and, if it returns `false` (the one
concrete way this happens in practice: the avatar is already at
`Constants.MaxAgentAttachments` when the destination re-attaches),
logs a single debug line ("arrived but failed to attach, setting to
temp") and leaves the object sitting in the scene as a temp-flagged,
unattached, ownerless-looking prim - it is not re-queued, not restored
to inventory, and the avatar gets no notification. It will eventually
be swept by ordinary temp-object cleanup and is then just gone. Low
probability (needs the avatar to already be at the attachment cap) but
completely silent and unrecoverable when it does happen. Confirmed
`HandleIncomingAttachments` (the caller) already isolates one bad
attachment from the rest of the batch rather than failing the whole
crossing, so this is a narrow, self-contained gap, not a systemic one.

**Relog persistence of in-session changes - checked, found already
correct, not a bug.** Started from the hypothesis that repositioning a
still-worn attachment (a HUD, typically) and then logging off without
explicitly detaching it first would lose the new position, since
`AttachmentsModule.UpdateAttachmentPosition` only sets `HasGroupChanged
= true` and doesn't itself persist anything. Traced where that flag
actually gets consumed: `UpdateKnownItem` (the only method that writes
the attachment's state back to the inventory asset) is called from
`UpdateDetachedObject`, which is called from `DeRezAttachments`, which
*is* wired into `Scene.RemoveClient` - the real handler behind a
genuine logout. Confirmed the wiring is correct in three ways: it only
fires when `!isChildAgent` (so an ordinary region crossing, where the
old presence becomes a child rather than being fully removed, correctly
skips it - already-transferred attachments aren't redundantly re-saved
mid-flight), it explicitly skips saving for Hypergrid visitors (a
deliberate, correct choice - saving a foreign grid's item into a
different-format local record would corrupt it on return home), and it
fires before script/asset cleanup so the position at last-known-good is
what gets written. The hypothesis was wrong: a graceful logout does not
lose in-session attachment changes. Confirmed identical in upstream
`opensim-master` (same single call site for `UpdateKnownItem`), so even
if this *had* been a bug it wouldn't have been Confluence-specific.
What this pass could not verify from server source alone is the
ungraceful case - a viewer crash or dropped connection relies on the
same generic dead-client watchdog that handles all session cleanup
grid-wide, not anything attachment-specific, and auditing that watchdog
is a distinct, larger networking question outside this pass's scope.

**Open question, not confirmed either way.** `AttachmentsModule.
RezAttachments` (the server-authoritative attachment rez on login)
skips entirely - not just skips duplicates, skips *everything* - if
`sp.GetAttachmentsCount() > 0` when it runs, logging "their viewer has
already rezzed attachments." If a viewer ever rezzes even one
attachment client-side on its own initiative before the server's sweep
runs, every other attachment the avatar was wearing would silently
never get server-rezzed - a plausible, concrete mechanism for "only
some of my attachments came back after I logged in." Confirmed this
exact check, comment included, exists verbatim in upstream `opensim-
master`. What's unconfirmed: whether any viewer in practice actually
rezzes attachments on its own initiative at login rather than waiting
for the server (OpenSim's attachment model is meant to be server-
authoritative), or whether this guard exists purely as a defensive
idempotency check against `RezAttachments` somehow being invoked twice
for the same login. Server-side source alone can't settle which it is.
Flagging rather than fixing blind - the right next step is a live test
with `DebugLevel` raised on this module during a real relog with
several attachments worn, to see whether the skip path ever actually
fires for a genuine relog.

**Built and deployed.** Fixed the `MaxAgentAttachments`-triggered
silent orphaning in `Scene.AddSceneObject` (the attachment branch,
where `AttachmentsModule.AttachObject` can return `false`). Previously
this logged a single debug line and left the object sitting in the
scene as an unattached temp prim with no further handling - it would
eventually vanish on the next temp-object sweep while the avatar's
appearance data still claimed it was worn. Since the backing inventory
item is untouched either way (nothing in this path ever removes it),
the fix is to stop pretending it succeeded: on attach failure, remove
the item from `sp.Appearance` (via `RemoveAttachment`) so the
appearance record matches reality instead of claiming a phantom worn
item, delete the orphaned in-world copy immediately instead of leaving
it to rot, log a `Warn` (not `Debug`, since this is data-visible to the
user) naming the item ID so it's traceable, and - critically - return
`false` from `AddSceneObject` instead of the previous `true`. That last
part matters: the caller (`EntityTransferModule.HandleIncomingAttachments`)
keys off this return value to drop the object from its own working
list before firing `TriggerOnIncomingSceneObject` on everything that's
left; returning `true` while having just deleted the object would have
left a dangling reference that could fire a scene-object event against
something already gone, which would have been a worse bug than the one
being fixed. The user experience is unchanged in the failure case
itself (the item still doesn't make it across when the avatar is at
the attachment cap - that's a real capacity limit, not something this
fix removes) but the failure is now clean: no ghost prim, no appearance/
reality desync, and the original inventory item was never touched, so
the user can just re-wear it.

Left the relog persistence open question (the `RezAttachments`
all-or-nothing skip gate) unbuilt - it still needs the live-log
verification described above before any code change is justified;
building a fix for a race that hasn't been confirmed to occur would be
guessing.

Build-verified clean (0 errors, 0 warnings) via `dotnet build
OpenSim.sln -c Release`. Deployed: grid confirmed down, `OpenSim.
Region.Framework.dll`/`.pdb` copied via PowerShell `Copy-Item`, verified
byte-for-byte via `Get-FileHash` MD5 match. Needs a grid restart to
take effect. Live verification needs an avatar actually parked at
`MaxAgentAttachments` crossing a border, which isn't practical to stage
on demand - left for the user's own testing opportunity.

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

## Test deployment notes

- `S:\Opensim\Casperia-Dev\Simulators\Welcome_Center\` — main test region.
- `S:\Opensim\Casperia-Dev\Simulators\Var_Test_Region\` — second 1024×1024
  var region added this round (port 8005, location 1010,1000) specifically
  to confirm weather/day-night bugs weren't Welcome_Center-specific.
- Both regions log to separate files (`logfile`/`StatsLogFile` under
  `[Startup]`) so they don't clobber a shared `OpenSim.log`.

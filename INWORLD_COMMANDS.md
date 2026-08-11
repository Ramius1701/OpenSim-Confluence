# In-World Chat Commands — Index

Every module in this repo that lets an avatar control something by typing
chat (not LSL scripts, not console commands). Compiled by auditing every
`OnChatFromClient` subscription across `OpenSim/` and `addon-modules/`.

## Active command modules

| Module | Commands file | Default channel | Permission gate |
|---|---|---|---|
| **OpenSimWeather** | [`addon-modules/OpenSimWeather/COMMANDS.md`](addon-modules/OpenSimWeather/COMMANDS.md) | `89` (private; channel 0 explicitly rejected) | `EstateManagerOnly` (default `true`) |
| **TextBuild** | [`OpenSim/Region/OptionalModules/World/TextBuild/COMMANDS.md`](OpenSim/Region/OptionalModules/World/TextBuild/COMMANDS.md) | `90` (private; channel 0 explicitly rejected, hardened from the original public-chat default) | `EstateManagerOnly` (default `true`); terrain commands additionally require a `build confirm`/`build cancel` step before anything is written |

TextBuild is present in the codebase but has no `[TextBuild]` config section
anywhere in Casperia-Dev, so it's currently inactive (defaults to disabled
with no config present). OpenSimWeather is active and configured.

## Everything else checked, with zero avatar-typed commands found

Audited every `OnChatFromClient`/`IChatModule`/chat-adjacent hook across the
whole repo. None of the following let an avatar type words to control a
feature — they're either pure chat relay, an automated non-typed protocol, an
LSL API backend, or simply don't listen to chat at all:

- **Core relay/infrastructure** (not commands, just chat plumbing):
  `OpenSim/Region/CoreModules/Avatar/Chat/ChatModule.cs`,
  `OpenSim/Region/OptionalModules/Avatar/Concierge/ConciergeModule.cs` (its
  `concierge_channel` config key is read but never actually used),
  the IRC bridge (`RegionState.cs` / `IRCClientView.cs`).
- **Automated non-typed protocols**: `DynamicFloaterModule.cs` (viewer
  floater-UI protocol, channel `427169570`), Gloebit's `GMMDialog.cs`
  (per-transaction random negative channel for `llDialog`-style button
  clicks, not free text).
- **NPC outbound speech**: `NPCAvatar.cs` (lets `osNpcSay` make an NPC talk;
  not a way for a *human* avatar to control anything).
- **LSL API backend, not a module feature**: `WorldCommModule.cs` — this is
  the `llListen()`/`llSay()` implementation scripts use; excluded because a
  script author, not a typed convention, defines any resulting "commands."
- **Addons with zero chat hooks of any kind**: RegionCurrency, RegionWeb,
  GroupAutoInvite, OpenSimMarketplace, OpenSimMutelist, OpenSimSearch,
  HoloPhysicsGuard.
- **OpenSimTide**: broadcasts tide-level announcements outward via
  `SimChatBroadcast`, but never subscribes to `OnChatFromClient` — it can
  speak, but nothing an avatar types back reaches it.

## Regenerating this audit

If a new module is added that listens to chat, grep the repo for
`OnChatFromClient` (both the `+=` subscription and the handler body) across
`OpenSim/` and `addon-modules/`, then update this file and add/update that
module's own `COMMANDS.md`.

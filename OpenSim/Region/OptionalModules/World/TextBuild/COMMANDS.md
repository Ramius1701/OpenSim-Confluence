# TextBuild Chat Commands

> **Status in this deployment**: `TextBuildModule.cs` exists in the codebase
> but has **no `[TextBuild]` section** configured anywhere in Casperia-Dev
> (or, as far as this audit covers, the live grid). With no config present,
> `Enabled` defaults to `false` and the module never subscribes to chat —
> it is currently **inactive**. This document describes what it does if/when
> it's turned on.

TextBuild lets an estate manager type a plain-English (or Italian) sentence
to rez a pre-built object, or to **reshape the entire region's terrain**.

## Hardened before first use

Two risks identified during review have been fixed in code (not yet tested
live, since the module has never been enabled):

- **Channel 0 no longer works implicitly.** The old code accepted a command
  on the configured `CommandChannel` **or** channel 0 (public local chat)
  unconditionally, and defaulted `CommandChannel` itself to `0` — meaning out
  of the box it listened on ordinary public chat. It now behaves like Weather:
  default channel is `90` (a private channel, distinct from Weather's `89`),
  and if an operator explicitly sets `CommandChannel = 0` the module logs a
  warning and forces it back to `90` instead. Only the exact configured
  channel is accepted.
- **Terrain commands now require confirmation.** Reshaping the whole region
  heightmap no longer happens on the first matching phrase. The module replies
  with a description of what it's about to do and requires typing
  `build confirm` (or `costruisci conferma`) within `TerrainConfirmationTimeoutSeconds`
  (default 30) to actually apply it, or `build cancel` (`build annulla`) to
  cancel. The pending request is tracked per-avatar and expires automatically.
- Terrain matching is still checked **before** object matching — if a sentence
  contains both an object word and a terrain word, terrain wins — so the
  confirmation step is the main safety net against an accidental match.

## How to send a command

```text
/90 build car
```

Replace `90` with `CommandChannel` if it's been changed. The message must
start with one of: `build `, `create `, `make `, `costruisci `,
`costruiscimi `, `crea `.

## Who can use it

`EstateManagerOnly` (default `true`) — non-managers get: *"TextBuild: only
estate managers can use automatic building here."*

## Object commands

| Say "build ..." + | Aliases | Creates |
|---|---|---|
| `car` | `machine`, `macchina`, `auto` | A small sport car |
| `boat` | `barca`, `yacht`, `sailboat`, `vela` | A small sailboat |
| `house` | `home`, `casa` | A cottage |
| `gazebo` | `pavilion`, `padiglione` | A gazebo |
| `portal` | `portale`, `gate`, `teleport` | A glowing portal arch |
| `tree` | `albero` | A tree |
| `fountain` | `fontana` | A fountain |
| `lamp` | `streetlight`, `lampione`, `lanterna` | A street lamp |
| `sofa` | `couch`, `divano` | A sofa |
| `dock` | `pier`, `molo`, `pontile` | A dock |
| `table` | `tavolo` | A table |

Object is rezzed a few meters in front of the avatar, facing the direction
they're facing. Denied if the template's part count exceeds `MaxParts`
(default 64 — none of the built-in templates come close) or if
`CanRezObject` permission fails at that spot.

Unrecognized text after the verb replies with a help line listing all
supported object and terrain keywords.

## Terrain commands (⚠️ requires confirmation, see above)

| Say "build ..." + | Aliases | Result |
|---|---|---|
| `ring`, `atoll`, `hole`, `lagoon` | `anello`, `atollo`, `buco`, `laguna` | Ring-shaped island with a lagoon hole (default 100m; add e.g. `150m` to resize) |
| `volcano`, `crater` | `vulcano`, `cratere` | Volcanic island with a crater (default 62m) |
| `archipelago` | `arcipelago` | Tropical archipelago (multiple small islands) |
| `canyon` | — | Canyon landscape with a river channel |
| `mountain`, `snow` | `montagna`, `montagne`, `neve` | Snowy mountains |
| `island`, `tropical` | `isola`, `tropicale` | Single tropical island |
| `terrain`, `flat`, `grass` | `terreno`, `piatto`, `erboso` | Flat grassy terrain (generic fallback) |

Size syntax for ring island / volcanic island: append a number + `m` (or
`meter`/`meters`/`metro`/`metri`), e.g. `build ring island 150m`.

Any terrain phrase above only **queues** the change — the module replies with
what it's about to do and waits for `build confirm` (within the configured
timeout) or `build cancel`. Nothing is written to the region until confirmed.

## Replies

Sent privately to the sender, from an object-style chat source named
`TextBuild` (not `Weather`).

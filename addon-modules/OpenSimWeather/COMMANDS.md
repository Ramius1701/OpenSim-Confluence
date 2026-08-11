# OpenSimWeather Chat Commands

Quick reference for estate owners/managers and anyone else allowed to control
weather in-world. For configuration and installation, see `README.md`.

## How to send a command

Weather commands must be sent on the module's private command channel, not
plain local chat. The default channel is `89` (set by `CommandChannel` in
`config/OpenSimWeather.ini`).

In Firestorm and most viewers, type into the chat bar:

```text
/89 weather status
```

Replace `89` with your grid's actual `CommandChannel` if it's been changed.

## Who can use these commands

Controlled by `EstateManagerOnly` in `config/OpenSimWeather.ini`:

- `EstateManagerOnly = true` (default) — only the estate owner or an estate
  manager for that region can use any weather command, including `status`.
  Anyone else gets: *"Only estate managers can change weather here."*
- `EstateManagerOnly = false` — anyone in the region can use these commands.

## Commands

| Command | Aliases | Effect |
|---|---|---|
| `weather status` | `weather`, `status` (alone) | Reports current conditions — see below. Does not change anything. |
| `weather sunny` | `sun`, `sereno`, `sole` | Clear skies, warmest temperature band, no precipitation. |
| `weather rain` | `pioggia`, `piove` | Light-to-moderate rain. |
| `weather storm` | `temporale`, `tempesta` | Heavy rain, lightning, and (if configured) thunder. |
| `weather snow` | `neve`, `nevica` | Snowfall. |
| `weather blizzard` | `snowstorm`, `bufera` | Heavy snow with strong wind — the snow equivalent of `storm`. |
| `weather clear` | `stop`, `asciutto` | Stops any active precipitation. Does **not** disable auto-cycle — the next scheduled change will still happen. |

`meteo` works everywhere `weather` does (e.g. `/89 meteo storm`). Commands are
matched as exact tokens, so ordinary chat containing a word like "rain" on the
wrong channel is never picked up.

## What you'll see

- **Command replies** (including `weather status`) are sent privately to
  whoever typed the command, from an object named `Weather`.
- **Weather-change announcements**: whenever the weather actually changes
  (by command or automatic cycling), the same style of report is broadcast
  publicly to everyone in the region, if `AnnounceWeatherChangesInChat = true`
  (default on). Checking `weather status` yourself never triggers this
  broadcast — only an actual change does.
- **Entry notification**: if `SendWeatherIMOnEntry = true`, you'll also get a
  private instant message a few seconds after entering the region, worded
  from `WeatherIMMessage`.

## Example report

```text
Weather report for Var Test Region: A blizzard is raging, with heavy snow
and wind. -14.1C and bitterly cold. Coverage: region (361 emitters). Next
forecast: in 5 hours 39 minutes.
```

Format: narrative conditions, temperature with a plain-language descriptor
(bitterly cold / freezing / cold / cool / mild / warm / hot), emitter coverage
(only shown for active precipitation), ground wetness/snow percentage (only
shown if `SurfaceEffectsEnabled = true`), active exclusion volumes (only shown
if any exist), and time until the next auto-cycle change (or "manual updates
only" if auto-cycle is off).

## Temperature

Each weather kind has its own base temperature (`SunnyTemperatureC`,
`RainTemperatureC`, `StormTemperatureC`, `SnowTemperatureC`,
`BlizzardTemperatureC`, `ClearTemperatureC`) plus a random `TemperatureVarianceC`
jitter. The reading is re-rolled only when the weather actually changes —
checking `weather status` repeatedly between changes always reports the same
value.

## Troubleshooting

- **No response at all**: you're probably on the wrong channel (must match
  `CommandChannel`, default 89) or the module is disabled (`Enabled = false`).
- **"Only estate managers can change weather here."**: you're not registered
  as the estate owner or an estate manager for this region.
- **"Use weather rain, weather storm, ... or weather status."**: the text
  after `weather` wasn't recognized — check spelling/aliases above.

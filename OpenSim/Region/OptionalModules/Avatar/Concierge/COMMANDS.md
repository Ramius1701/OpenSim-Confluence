# Concierge chat commands

The concierge answers a few questions typed on its chat channel. Replies are
private: only the avatar who asked receives them.

- **Channel:** `[Concierge] concierge_channel`, default `4242` (chosen to stay
  clear of the common HUD channels; channel 0 is not used).
- **Who can use them:** anyone in a region where the concierge is active. They
  only read information, so there is no permission gate.
- **Rate limit:** one command every two seconds per avatar; faster ones are
  ignored.
- **Turning it off for a region:** switch the region's concierge off in the web
  portal (My Regions > Concierge), or leave the module disabled.

| Type | You get |
|---|---|
| `/4242 help` (or any unknown word) | The list of commands |
| `/4242 who` | Everyone in the region, by display name; visitors from other grids are marked `(visitor)` |
| `/4242 info` | Region name and size, maturity rating, people here now, estate name and owner, grid name |
| `/4242 rules` | The region's rules, as set in the web portal (or a note that none are posted) |
| `/4242 welcome` | The welcome message again, chosen for you the same way as on arrival |
| `/4242 staff` | The estate owner and managers who are online right now, and which region each is in; nobody is listed if offline |

`rules` and `welcome` use the same fill-in details and audience sections as the
welcome text: `{displayname}`, `{name}`, `{firstname}`, `{region}`, `{count}`,
`{grid}`, `{estate}`, `{owner}`, `{concierge}`, and `[new]`, `[trial]`, `[hg]`
sections.

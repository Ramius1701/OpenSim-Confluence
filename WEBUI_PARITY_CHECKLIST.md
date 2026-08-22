# WebUI Content-Parity Checklist

Tracks the audit required by the standing decision documented in
`PROJECT_LOG.md` and memory: Confluence's WebUI keeps its current
architecture (hand-built HTML strings in
`OpenSim/Server/Handlers/WebInterface/WebInterfaceServiceConnector.cs`),
but every page's actual structure/content must be checked against its
real named reference file — WhiteCore-Dev's `bin/html/*` (84 files,
this repo's own `WhiteCore-Dev` checkout) — not invented from scratch.

**Status legend**: ☐ not audited · 🔶 audited, gap found (not yet fixed)
· ✅ audited and matches/fixed

Reference root: `WhiteCore-Dev/WhiteCoreSim/bin/html/`

**Fidelity standard (confirmed by the user 2026-08-21, welcome.php as
the test case)**: match the reference's real *structure* - layout
(column split, box placement), what content lives where, and any
functional behavior (e.g. background-image-on-body, per-section
boxing) - not a literal port of WhiteCore-Dev's actual markup. Keep
Confluence's own existing CSS variables, color palette, and class
naming (`PageCss`'s `--accent`/`--card-bg`/etc., the site's own
`.welcome-*`-style naming), not WhiteCore's Bootstrap 4 classes
(`col-3`, `container-fluid`, `form-row`), literal ids (`#regionbox`,
`#gridstatus`), icon `<img>` tags, or color values (their aqua
`#4ddfc4`, `bg_white_01.png` translucency). The bar for "done" is: does
this page's real *shape* (what's where, how content is grouped, what
mechanisms drive it) match WhiteCore-Dev's actual reference file, read
in full - not "did I take inspiration from it" and not "did I copy its
literal code."

## Welcome / public splash

| Status | Confluence route | WhiteCore-Dev reference | Notes |
|---|---|---|---|
| ✅ | `/`, `/welcome.php` | `welcomescreen/index.html` + `region_box.html` + `news.html` + `gridstatus.html` + `info_box.html` | Done 2026-08-21: full-viewport background, real 2-column split, translucent boxes |
| ✅ | `/login` | `login.html` | Done 2026-08-23: real reference is a minimal 2-field form (no illustration column needed - decorative, not structural). Found and fixed two real gaps: the visible H1 hardcoded "Confluence Grid Login" (a leftover the earlier title-only regex sweep missed, since it has no " - " separator), and missing auto-focus on the first field (reference does `$("#login_input").focus()`; used plain `autofocus` instead of adding a jQuery dependency). First/Last name (vs. reference's single username field) is a correct divergence, not a gap - OpenSim's real identity model needs both. |
| ☐ | `/register` | `register.html`, `admin/user_register.html` | |
| ☐ | `/forgot-password` | `forgot_pass.html` | |
| ☐ | `/logout` | `logout.html` | |
| ☐ | `/help` | `help.html` | |
| ☐ | `/viewers` | `static/viewers/index.html` | |
| ☐ | `/worldmap` | `world.html` | `map/` subdir exists but is empty in this checkout |
| ☐ | `/search`, `/search/suggest` | `region_search.html`, `user_search.html` | |

## Grid status / economy / features

| Status | Confluence route | WhiteCore-Dev reference | Notes |
|---|---|---|---|
| ☐ | `/gridstatus` | `welcomescreen/gridstatus.html` | Standalone full page, not just the splash widget |
| ☐ | `/economy` | *(no exact match)* | Closest: `admin/statistics.html`, `admin/transactions.html` |
| ☐ | `/features` | *(no match — Confluence-specific)* | Not a WhiteCore concept; low priority for this audit |
| ☐ | `/destinations`, `/guide` | *(no match — Confluence-specific)* | |
| ☐ | `/landsearch` | `user/buyland.html` | |
| ☐ | `/auctions`, `/auctions/bid` | *(no match found)* | Land-auction bidding; WhiteCore reference list has nothing obvious |

## Messaging (no WhiteCore equivalent found)

| Status | Confluence route | WhiteCore-Dev reference | Notes |
|---|---|---|---|
| ☐ | `/offline-messages`, `/messages`, `/messages/sent`, `/messages/compose`, `/messages/send`, `/messages/view`, `/messages/delete` | *(none found)* | WhiteCore's reference set has no web inbox; confirm with a broader search before assuming, don't just take this list's word for it |

## Logged-in user pages

| Status | Confluence route | WhiteCore-Dev reference | Notes |
|---|---|---|---|
| ☐ | `/dashboard` | `userhome.html`, `user/userhome.html` | |
| ☐ | `/profile` | `user/profile.html`, `webprofile/modal_profile.html` | |
| ☐ | `/friends` | `user/friends.html` | |
| ☐ | `/partner` | `user/partnership.html` | |
| ☐ | `/change-password` | `user/password.html` | |
| ☐ | `/change-email` | `user/email.html` | |
| ☐ | `/delete-account` | `user/deleteaccount.html` | |
| ☐ | `/transactions` | `user/transactions.html` | |
| ☐ | `/support`, `/admin/support`, `/admin/support/status` | `user/contact.html` | |
| ☐ | `/myclassifieds`, `/myclassifieds/save`, `/myclassifieds/delete` | `user/classifieds.html`, `classifieds/add_classified.html`, `classifieds/classifieds.html` | Category off-by-one already fixed 2026-08-20; layout/structure still unaudited |
| ☐ | `/myevents`, `/myevents/save`, `/myevents/delete` | `user/events.html`, `events/add_event.html`, `events/events.html` | |
| ☐ | `/myestates` | `user/estate_manager.html`, `user/estate_edit.html` | |
| ☐ | `/myregions`, `/myregions/oar-save`, `/myregions/restart` | `user/region_manager.html`, `user/region_edit.html` | |
| ☐ | `/myland`, `/myland/toggle` | `user/mainland.html`, `user/groupland.html`, `user/landfees.html` | |
| ☐ | `/myinventory`, `/myinventory/iar-save` | *(no match found)* | Web-based inventory/IAR export; nothing obvious in WhiteCore's set |

## Admin pages

| Status | Confluence route | WhiteCore-Dev reference | Notes |
|---|---|---|---|
| ☐ | `/admin` | *(no single index found)* | Check for an admin landing page under a name not yet searched |
| ☐ | `/admin/users`, `/admin/users/set-level`, `/admin/users/edit-details`, `/admin/users/reset-password`, `/admin/users/create`, `/admin/users/login-as`, `/admin/users/soft-delete`, `/admin/users/kick`, `/admin/users/message` | `admin/user_manager.html`, `admin/user_edit.html`, `admin/user_password.html`, `admin/user_register.html` | |
| ☐ | `/admin/estates`, `/admin/estates/create`, `/admin/estates/update`, `/admin/estates/managers`, `/admin/estates/access`, `/admin/estates/bans`, `/admin/estates/groups` | `admin/estate_manager.html`, `admin/estate_edit.html` | |
| ☐ | `/admin/groups`, `/admin/groups/update`, `/admin/groups/delete` | *(no admin-side match found)* | `webprofile/modal_groups.html` is per-user, not admin management |
| ☐ | `/admin/regions`, `/admin/regions/restart`, `/admin/regions/group-auto-invite`, `/admin/maptile-regen`, `/admin/oar-save` | `admin/region_manager.html`, `admin/region_edit.html` | |
| ☐ | `/admin/abuse-reports`, `/admin/abuse-reports/image` | `admin/abuse_manager.html`, `admin/abuse_report.html` | |
| ☐ | `/admin/transactions` | `admin/transactions.html` | |
| ☐ | `/admin/stats` | `admin/statistics.html` | |
| ☐ | `/admin/news`, `/admin/news/save`, `/admin/news/delete` | `admin/news_manager.html`, `admin/news_add.html`, `admin/news_edit.html` | |
| ☐ | `/admin/events`, `/admin/events/save`, `/admin/events/delete` | *(no dedicated admin page found)* | `events/events.html`, `events/add_event.html` may be reused, unconfirmed |
| ☐ | `/admin/pages`, `/admin/pages/save`, `/admin/pages/delete` | `admin/page_manager.html` | |
| ☐ | `/admin/settings`, `/admin/settings/save`, `/admin/hg-toggle` | `admin/settings_manager.html`, `admin/gridsettings_manager.html` | |
| ☐ | `/admin/console`, `/admin/console/run` | `admin/sim_console.html` | |

## WhiteCore-Dev pages with no current Confluence route

Not necessarily gaps to fill — some may be intentionally out of scope —
but worth a deliberate look rather than silent omission:

- `admin/purchases.html`, `admin/factory_reset.html`, `admin/welcomescreen_manager.html`
- `online_users.html`, `noregistrations.html`, `maintenance.html`
- `http_404.html`, `http_500.html` (Confluence may already handle these differently — check)
- `tweets.html`, `irc_chat.html`
- `news_list.html`, `region_list.html`, `userindex.html`
- `regionprofile/modal_parcels.html`, `regionprofile/modal_profile.html`
- `webprofile/modal_picks.html`, `webprofile/modal_regions.html`

## How to use this list

For each row: read the real reference file(s) in full (not a summary
from memory), compare field-by-field against what Confluence's handler
currently renders for that route, fix real gaps, mark the row ✅. If a
route's best-guess reference above turns out wrong once actually
checked, correct the table — this is a working document, not a fixed
plan. Rows marked "no match found" need a real search before being
trusted as gaps (this pass matched by filename/purpose, not exhaustive
content reading of all 84 files).

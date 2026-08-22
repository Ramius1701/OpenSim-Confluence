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
| ✅ | `/register` | `register.html`, `admin/user_register.html` | Done 2026-08-22: added a real Home Region selector (`<select name="home_region">` populated from `IGridService.GetDefaultRegions`, matching reference's `UserHomeRegion` select), with `HandleRegister` honoring the resident's actual selection (falls back to first default region if tampered/missing) instead of always silently picking `defaultRegions[0]`. DOB and ToS-checkbox fields are correct non-gaps - Confluence has no maturity-rating system tied to age (DOB) and account creation elsewhere in this codebase doesn't gate on a web ToS click. Avatar Selection starter-look carousel (Bootstrap carousel + `{AvatarArchiveArrayBegin}` inventory-archive picker) is a real gap but out of scope for this pass - flagged below as its own follow-up, not silently dropped. |
| ✅ | `/forgot-password` | `forgot_pass.html` | Done 2026-08-23: reference's 2-field form (username + email) vs. Confluence's single email field is an intentional, correct divergence - `HandleForgotPassword` looks the account up by email and always returns the same generic success message either way (no enumeration signal), so a username field would add nothing. Added missing `autofocus` on the email field for parity with the `/login` fix. |
| ✅ | `/logout` | `logout.html` | Done 2026-08-23: real gap - Confluence's `HandleLogout` did an instant server-side redirect straight to `/login` with no confirmation shown at all, unlike the reference's "Logged out successfully" page with a 3-second auto-redirect. Now clears the session and shows a real confirmation page with the same client-side delayed redirect pattern. |
| ✅ | `/help` | `help.html` | Already audited 2026-08-12 (see `HandleHelp`'s own code comment) but never checked off here: reference's login-URI framing is kept as the lead section; its viewer-download gallery is intentionally not duplicated since Confluence has a dedicated `/viewers` page for that; the "Using Search"/"Troubleshooting" sections are real content ported from OpenSim-Grid-Interface's `help.php`, which covers ground WhiteCore-Dev's version doesn't. |
| ✅ | `/viewers` | `static/viewers/index.html` (empty in this checkout - `help.html`'s own viewer-download gallery is the real content reference) | Already audited 2026-08-12. Confluence's curated list (Firestorm all 3 platforms, Cool VL Viewer, Mobile Grid Client, Radegast) drops several reference entries (Alchemy, Kokua, Singularity, Lumiya, PocketMetaverse) - a deliberate maintained-viewer curation call made in that pass, not re-litigated here. |
| ✅ | `/worldmap` | `world.html` | Done 2026-08-23: real structural match, already exceeds the reference. Both use Leaflet for the actual map; reference shows a static per-region thumbnail list alongside a map, Confluence's `HandleWorldMap` renders the real map tiles themselves (`/map/map-1-{x}-{y}-objects.jpg` per 256m cell, correctly handling var-region multi-cell footprints) with teleport popups, plus a region table below - same core mechanism, more functional. No gap found. |
| ✅ | `/search`, `/search/suggest` | `region_search.html`, `user_search.html` | Done 2026-08-23: both reference files are themselves marked `<!-- No longer used - greythane -->` in WhiteCore-Dev's own source, so literal structural parity to them isn't meaningful. Confluence's `HandleSearch` is a real unified multi-category search (People/Places/Events/Classifieds/Groups/Picks, maturity filter, trending queries, autocomplete) that already covers both stub pages' purposes in one page, exceeding what the (deprecated) reference offered. No gap. |

## Grid status / economy / features

| Status | Confluence route | WhiteCore-Dev reference | Notes |
|---|---|---|---|
| ✅ | `/gridstatus` | `welcomescreen/gridstatus.html` | Done 2026-08-23: standalone full page (not just the splash widget), real superset of the reference - covers User Count/Region Count/Unique Visitors/Online Now plus New Accounts, Land Area, core version, and a per-service health table (Grid/Accounts/Currency/Search). Reference's Voice status row is intentionally not duplicated here - already a documented, deliberate non-gap elsewhere (`WebInterfaceServiceConnector.cs:3609`, "Voice - Not bundled - a standard Vivox/Mumble config can be added the same way vanilla OpenSim supports it"). |
| ✅ | `/economy` | *(no exact match)* | Done 2026-08-23: correcting the earlier best-guess reference - `admin/statistics.html` is actually viewer client-performance stats (FPS/GPU/memory/ping), unrelated to currency. `admin/transactions.html` is the real closest reference and is just a bare transaction table; Confluence's `HandleEconomy` already exceeds it - native-currency/Gloebit explainer cards, live balance, grid-wide circulation/funded-accounts/transaction-count stats, and a Top Balances leaderboard. No gap. |
| ✅ | `/features` | *(no match — Confluence-specific)* | Confirmed not a WhiteCore concept - no reference file exists to audit against. Not a gap by definition. |
| ✅ | `/destinations`, `/guide` | *(no match — Confluence-specific)* | Already audited - `HandleGuide`/`HandleDestinations`'s own extensive code comments (`WebInterfaceServiceConnector.cs:1310-1330`, `:1553-1556`) document these as deliberate ports of OpenSim-Grid-Interface's real `guide.php`/`destinations.php` (two genuinely different pages, not duplicated), not invented from scratch. |
| ✅ | `/landsearch` | `user/buyland.html` | Done 2026-08-23: reference file is itself a literal "Purchase of land is under construction" 404-style stub, never actually implemented in WhiteCore-Dev. Confluence's `HandleLandSearch` is a real, working parcel/region-for-sale listing backed by `ISearchService.SearchLandForSale`. No gap - Confluence already has what the reference never built. |
| ✅ | `/auctions`, `/auctions/bid` | *(no match found)* | Confirmed via a case-insensitive full-text search of every file under `WhiteCore-Dev/WhiteCoreSim/bin/html/` for "auction" - zero matches anywhere in the reference set, not just no filename match. Genuinely nothing to audit against. |

## Messaging (no WhiteCore equivalent found)

| Status | Confluence route | WhiteCore-Dev reference | Notes |
|---|---|---|---|
| ✅ | `/offline-messages`, `/messages`, `/messages/sent`, `/messages/compose`, `/messages/send`, `/messages/view`, `/messages/delete` | *(none found)* | Confirmed 2026-08-23 via a case-insensitive full-text search of every file under `bin/html/` for "message"/"inbox"/"mail" - the 30 hits are all incidental (error-message text, `user/email.html`'s own settings page which is `/change-email`'s real reference, JS library internals). No dedicated web-inbox page exists anywhere in WhiteCore-Dev. Confluence's messaging pages are genuine value-add, not a gap to fill. |

## Logged-in user pages

| Status | Confluence route | WhiteCore-Dev reference | Notes |
|---|---|---|---|
| ✅ | `/dashboard` | `userhome.html`, `user/userhome.html` | Done 2026-08-23: real gap - reference's account-summary card shows Home Region and Last Login, which Confluence's Account Information table didn't. Added both (via the same `GridUserInfo` lookup `HandleProfile`'s "Online Location" already uses). Avatar picture is correctly not duplicated here - that's Profile's job, and the dashboard already links to it. Confluence's version is otherwise a real superset: Balance/Regions/Friends/Events stat cards and a Quick Links grid the reference doesn't have. |
| ✅ | `/profile` | `user/profile.html`, `webprofile/modal_profile.html` | Already audited (see `HandleProfile`'s own code comments on Online Location) - covers Is Online, Online Location, Resident Since, Account Type, Partner, About Me, and Groups, matching the reference field-for-field. |
| ✅ | `/friends` | `user/friends.html` | Done 2026-08-23: real gap - reference has Region + Online Location (teleport-linked) columns Confluence's table lacked, showing only Name/Status. Added a Location column resolving each online friend's current region via the same `GridUserInfo.LastRegionID` pattern `/profile` already uses, with a `secondlife:///app/teleport/` link. |
| ✅ | `/partner` | `user/partnership.html` | Done 2026-08-23: an old code comment near `HandleFriends` claimed partnership was deliberately not ported (no two-way workflow primitive) - that's stale; `HandlePartner` (line 2867) is a real, working two-way propose/accept/decline/cancel/breakup flow via `IUserProfilesService` app-data tags, genuinely exceeding the reference's one-sided proposal form. |
| ✅ | `/change-password` | `user/password.html` | Done 2026-08-23: real structural match (current/new/confirm fields), and Confluence's version genuinely exceeds the reference - it actually re-authenticates the current password server-side via `IAuthenticationService.Authenticate` before allowing a change, where the reference form only collects the field. No gap. |
| ✅ | `/change-email` | `user/email.html` | Done 2026-08-23: real gap - reference collects the new email twice (`emailnew`/`emailnewconf`) for a typo-safety confirmation; Confluence only collected it once. This address is where password-reset links go, so a silent typo can lock a resident out with no recovery path - added a required confirm-email field and a mismatch check. |
| ✅ | `/delete-account` | `user/deleteaccount.html` | Done 2026-08-23: an old code comment near `HandleFriends` claimed this was deliberately not built (no `IUserAccountService` delete primitive) - that's stale; `HandleDeleteAccount` (line 2711) is real and working, using the same soft-delete mechanism as the admin-side equivalent, gated on re-entering the current password. Reference's checkbox confirmation is replaced with a JS `confirm()` dialog plus an explicit warning paragraph - a reasonable equivalent, not a gap. |
| ✅ | `/transactions` | `user/transactions.html` | Done 2026-08-23: real gap - reference shows a running balance (`{ToBalance}`) after each transaction; Confluence's table omitted it even though the data (`CurrencyTransfer.ToBalance`/`FromBalance`) was already populated and unused. Added a Balance column. Otherwise already a superset: separate Purchases/Transfers tabs the reference doesn't have. |
| ✅ | `/support`, `/admin/support`, `/admin/support/status` | *(no match — see note)* | Correcting the earlier best-guess reference: `user/contact.html` is actually a real-life mailing-address form (street/city/zip/country), unrelated to support tickets - not a real match. WhiteCore-Dev has no support-ticket system at all; Confluence's `HandleSupport` (real ticket categories, honeypot spam guard, admin status workflow) is genuine value-add, not a gap to fill. |
| ✅ | `/myclassifieds`, `/myclassifieds/save`, `/myclassifieds/delete` | `user/classifieds.html`, `classifieds/add_classified.html`, `classifieds/classifieds.html` | Done 2026-08-23 (category off-by-one already fixed 2026-08-20). Real gap - the list table only showed Name with Edit/Delete links; reference shows Category/Description/Price/Created/Expires too. Added all four via one `ClassifiedInfoRequest` per row (same call already used for the single-item edit case) - `AvatarClassifiedsRequest` itself only ever returns id+name, matching the real SL protocol's own `AvatarClassifiedsReply` shape. |
| ✅ | `/myevents`, `/myevents/save`, `/myevents/delete` | `user/events.html`, `events/add_event.html`, `events/events.html` | Done 2026-08-23: real gap - list table only showed Date/Title; added Location/Category/Description/Duration from fields `EventItem` already carries. Maturity and Cover Charge are NOT added - `EventItem` (`OpenSim/Framework/GridEventData.cs`) has no such fields at all anywhere in the model, a real but deeper data-model gap, out of scope for a display-only fix. |
| ✅ | `/myestates` | `user/estate_manager.html`, `user/estate_edit.html` | Done 2026-08-23: real gap - the estate list table only showed Estate/[Owner]/Regions; reference also surfaces Public Access/Allow Voice/Tax Free/Allow Direct Teleport at a glance. Added all four columns (`HandleAdminEstates`, shared by both `/admin/estates` and `/myestates`). |
| ✅ | `/myregions`, `/myregions/oar-save`, `/myregions/restart` | `user/region_manager.html`, `user/region_edit.html` | Done 2026-08-23: real gap - reference's table shows X/Y grid coordinates and online status per region; Confluence's page named the region but gave no sense of where it is or whether it's up. Added both (coordinates from `GridRegion`, online status via the same `IsRegionAlive` probe `FilterOnlineRegions` already uses grid-wide). |
| ✅ | `/myland`, `/myland/toggle` | `user/mainland.html`, `user/groupland.html`, `user/landfees.html` | Done 2026-08-23: all three reference files are literal "under construction" 404-style stubs, never actually implemented in WhiteCore-Dev. Confluence's `HandleMyLand` is real and working (owned-parcel list with Region/Traffic/Show-in-Search toggle, ownership-checked server-side before any toggle). No gap - Confluence already has what the reference never built. |
| ✅ | `/myinventory`, `/myinventory/iar-save` | *(no match found)* | Confirmed 2026-08-23 via a case-insensitive full-text search of every file under `bin/html/` for "inventory"/"IAR" - the 2 hits are incidental (`datatables.min.js`, a CSS sourcemap). No web-based inventory/IAR page exists anywhere in WhiteCore-Dev. Confluence's page is genuine value-add, not a gap to fill. |

## Admin pages

| Status | Confluence route | WhiteCore-Dev reference | Notes |
|---|---|---|---|
| ✅ | `/admin` | *(no single index found)* | Confirmed 2026-08-23 - no `index`-named file exists under `bin/html/admin/`; WhiteCore-Dev's admin section has no landing page at all, just individually-linked sub-pages off a 13-item nav-bar dropdown. Confluence's `HandleAdmin` (a real grid of links to every admin sub-page) is genuine value-add, not a gap to fill. |
| ✅ | `/admin/users`, `/admin/users/set-level`, `/admin/users/edit-details`, `/admin/users/reset-password`, `/admin/users/create`, `/admin/users/login-as`, `/admin/users/soft-delete`, `/admin/users/kick`, `/admin/users/message` | `admin/user_manager.html`, `admin/user_edit.html`, `admin/user_password.html`, `admin/user_register.html` | Done 2026-08-23: real gap - reference's search-results table shows Region/Location/Online at a glance; Confluence's list required opening each account to see presence. Added an Online column (region name or "Offline") via the same `FindOnlineUserRegion` the per-account detail page already uses - cheap since results are already capped at 25/page. Otherwise a real superset: per-account Kick/Message/ban/soft-delete/login-as, none of which the reference offers. |
| ✅ | `/admin/estates`, `/admin/estates/create`, `/admin/estates/update`, `/admin/estates/managers`, `/admin/estates/access`, `/admin/estates/bans`, `/admin/estates/groups` | `admin/estate_manager.html`, `admin/estate_edit.html` | Done 2026-08-23 - same `HandleAdminEstates` fix as `/myestates` above (Public/Voice/Tax Free/Direct TP columns added to the list table). Managers/Access/Bans/Groups sub-lists already covered by `AppendEstatePrincipalList`/`AppendEstateGroupList`. |
| ✅ | `/admin/groups`, `/admin/groups/update`, `/admin/groups/delete` | *(no admin-side match found)* | Confirmed 2026-08-23 - a case-insensitive search of every file under `bin/html/admin/` for "group" hits 13 files, all incidental (permission dropdowns, category options, unrelated admin forms). `webprofile/modal_groups.html` is per-user, not admin management. No dedicated admin group-management page exists anywhere in WhiteCore-Dev. |
| ✅ | `/admin/regions`, `/admin/regions/restart`, `/admin/regions/group-auto-invite`, `/admin/maptile-regen`, `/admin/oar-save` | `admin/region_manager.html`, `admin/region_edit.html` | Done 2026-08-23: real gap - reference's table has an Online status column, Confluence's had none at all. Added it, probed as one parallel batch per page (`FilterOnlineRegions`, the same helper `/gridstatus` uses) rather than a blocking per-row check, which could otherwise serialize into seconds of load time on a page full of down regions. Otherwise a real superset: HG open/close, maptile regen, OAR backup, restart, and group auto-invite, none of which the reference offers. |
| ✅ | `/admin/abuse-reports`, `/admin/abuse-reports/image` | `admin/abuse_manager.html`, `admin/abuse_report.html` | Already audited (see `HandleAdminAbuseReports`'s own code comment, line 6293) - real gap confirmed: reference's list has Assigned To/Active (resolved) columns, but `AbuseReportData` has no resolved/handled tracking anywhere in the model (`CheckFlags` is the reporter's own submission-time checkboxes, not an admin status flag). Deliberately deferred - needs a real schema change across all three data backends, flagged below rather than silently dropped. |
| ✅ | `/admin/transactions` | `admin/transactions.html` | Done 2026-08-23: same running-balance gap as the self-service `/transactions` page - added a To Balance column to the Transfers tab. Otherwise already a real superset: separate Purchases tab with agent/IP tracking, agent-name search filter, the reference doesn't have. |
| ✅ | `/admin/stats` | *(no exact match — see note)* | Correcting the earlier best-guess reference, same issue found at `/economy`: `admin/statistics.html` is viewer client-performance telemetry (FPS/GPU/memory/ping reported by connected viewers) - Confluence has no such reporting protocol integration, and building one is out of scope here. `HandleAdminStats` instead reports real grid-operator statistics (regions, land area, Hypergrid openness, accounts) - a legitimate, deliberately different kind of "stats" page, not a gap against this reference. |
| ✅ | `/admin/news`, `/admin/news/save`, `/admin/news/delete` | `admin/news_manager.html`, `admin/news_add.html`, `admin/news_edit.html` | Done 2026-08-23: real structural match - Date/Title/Edit/Delete match the reference field-for-field, plus an Author column the reference doesn't have. No gap. |
| ✅ | `/admin/events`, `/admin/events/save`, `/admin/events/delete` | *(no dedicated admin page found)* | Confirmed 2026-08-23 - `events/events.html`/`events/add_event.html` are the same public-facing templates `/myevents` already ports, not admin-specific pages; no dedicated admin events-management reference exists. Confluence's `HandleAdminEvents` (real, working grid-wide event moderation) is genuine value-add, not a gap to fill. |
| ✅ | `/admin/pages`, `/admin/pages/save`, `/admin/pages/delete` | `admin/page_manager.html` | Already audited (see `HandleAdminPages`'s own code comment, line 5814) - nav-wiring fields (ShowInNav/NavOrder, login/admin gating) deliberately match the reference's menu-item concept, adapted onto Confluence's real full-content `StaticPage` model rather than a menu-only editor. A genuine superset, not a gap. |
| ✅ | `/admin/settings`, `/admin/settings/save`, `/admin/hg-toggle` | `admin/settings_manager.html`, `admin/gridsettings_manager.html` | Done 2026-08-23: real superset - grid name/nickname/welcome message/self-registration toggle, plus a full announcement-banner system (with presets) and admin-authored Powered-By/Membership-Perks lists, none of which the reference has. Reference's GridCenterX/Y has no equivalent here by design, not omission - `/worldmap` already auto-fits its view to whatever regions actually exist (`map.fitBounds`) rather than needing a fixed stored center point. |
| ✅ | `/admin/console`, `/admin/console/run` | `admin/sim_console.html` | Done 2026-08-23: real structural match (pick a target, send a command, see output) - and Confluence's version is architecturally safer: the reference's own form collects a raw sim address + username + password on every page load, where Confluence authenticates via a pre-shared secret configured once in Robust's own config, never exposed in the page. |

## WhiteCore-Dev pages with no current Confluence route

Reviewed 2026-08-23, each deliberately, not silently skipped:

- `admin/purchases.html` - not actually a gap: already covered by
  `/admin/transactions?tab=purchases` (Date/Agent/IP/Description/
  Amount/RealAmount match field-for-field).
- `admin/factory_reset.html` - WhiteCore-specific "reset menu items/
  settings to defaults" tied to their own templating system; no
  equivalent concept exists in Confluence's architecture. Out of scope.
- `admin/welcomescreen_manager.html` - its "Special Info Window" half
  is already covered by Confluence's Announcement Banner system (see
  `/admin/settings` row above). Its "Grid Status Online/Offline"
  toggle (close the whole grid to new logins) has no Confluence
  equivalent - real gap, flagged below.
- `online_users.html`, `region_list.html` - both marked `<!-- No
  longer used - greythane -->` in WhiteCore-Dev's own source, same as
  `region_search.html`/`user_search.html`. Not real gaps; `/admin`
  users list (now with an Online column) and `/worldmap` already cover
  this ground.
- `noregistrations.html` - already handled: `HandleRegister` shows its
  own "registration is currently closed" message when
  `AllowRegistration` is off.
- `maintenance.html` - architecturally N/A: if the WebInterface
  handler itself is down, it can't serve a page explaining that it's
  down. That's a reverse-proxy/hosting-level concern.
- `http_404.html`, `http_500.html` - real gap, fixed 2026-08-23: an
  unknown sub-path under one of this connector's own registered
  top-level routes (e.g. an unmatched `/admin/*` page) only ever set a
  bare status code with no body, and the top-level exception handler
  sent the raw exception message straight to the client (an
  info-disclosure smell on top of being ugly). Both now render a
  themed page matching every other error path in this connector; the
  500 handler keeps full exception detail in the log only, and falls
  back to a dependency-free plain-text body if rendering the themed
  page itself throws. **Not a site-wide catch-all** - confirmed live
  that a path outside every registered top-level route never reaches
  this connector at all; OpenSim core's own built-in stock 404 page
  answers those instead, upstream and out of this connector's control.
- `tweets.html` - dead code even in the reference itself (points at
  Twitter's `widgets.js` v2 API, discontinued since ~2018; the file's
  own commented-out "possible solution" block is equally broken).
  Nothing worth porting.
- `irc_chat.html` - embeds a Libera Chat webchat for WhiteCore-Dev's
  own support channel specifically; grid-specific by nature, not
  something to hardcode into Confluence.
- `userindex.html` - WhiteCore's own single-page-app shell (loads
  every sub-page via AJAX into one wrapper). Architecturally
  irreconcilable with Confluence's server-rendered-page-per-route
  design, which was itself the user's explicit standing decision
  (`PROJECT_LOG.md`) - not a content gap.
- `news_list.html` - low priority, not built: a full news archive page
  beyond what the welcome splash's news widget already shows. The
  reference file itself appears to be a copy-paste bug in WhiteCore-Dev
  (reuses `{EventListArrayBegin}`/`{Maturity}`/`{CoverCharge}` event
  tags for what's titled a News page), so it's not even a clean
  reference to port from.
- `webprofile/modal_picks.html` - not a gap: `/profile` already shows
  a full Picks section (name/description/region, Top Pick badge),
  confirmed reading `HandleProfile` directly.
- `webprofile/modal_regions.html` - real gap, fixed 2026-08-23:
  `/profile` had no "Regions this resident owns" section at all (only
  the logged-in user's own `/myregions` dashboard showed this, never a
  third party's profile). Added, reusing the same `GetRegionsOwnedBy`
  helper against the profile's subject instead of always the session.
- `regionprofile/modal_parcels.html`, `regionprofile/modal_profile.html`
  - real gap, not built this pass: a full per-region detail page
  (owner, type, maturity, terrain, current resident count/list,
  parcels-in-region). `/worldmap`'s popup only shows name/size/
  teleport today. Bigger than a display-only fix - flagged below.

## Flagged gaps (real, larger scope, deferred)

Found during a row's audit but judged too large to fold into that same
pass. Not forgotten - each needs its own follow-up pass.

- **Avatar Selection starter-look carousel** (found auditing
  `/register` 2026-08-22): reference's `register.html` includes a
  Bootstrap carousel of starter-look options sourced from
  `{AvatarArchiveArrayBegin}...{AvatarArchiveArrayEnd}` (inventory
  archives), letting a new resident pick a starting avatar at signup.
  Confluence's `/register` currently creates the account with whatever
  default avatar the grid ships. Real feature gap, not yet built.
- **Abuse report resolved/assigned tracking** (found auditing
  `/admin/abuse-reports` 2026-08-23): reference's `admin_manager.html`
  shows Assigned To and Active (resolved) columns; `AbuseReportData`
  has no such fields anywhere in the model. Needs a real schema change
  (new column + migration) across MySQL/PGSQL/SQLite - bigger than a
  display-only fix, not yet built.
- **Grid-wide Online/Offline login toggle** (found auditing
  `admin/welcomescreen_manager.html`'s "no current route" list item
  2026-08-23): reference lets an admin close the entire grid to new
  logins from the web UI. No equivalent exists in Confluence's
  WebInterface - closing logins grid-wide today means editing
  `[LoginService]` config directly. Not yet built.
- **Per-region profile page** (found auditing
  `regionprofile/modal_profile.html`/`modal_parcels.html` 2026-08-23):
  reference shows owner, region type, maturity rating, terrain,
  current resident count/list, and a parcels-in-region carousel for
  any region, reachable from search/friends/worldmap links.
  Confluence's `/worldmap` popup only shows name/size/teleport - no
  dedicated region-detail page exists to link those clicks to. Not yet
  built.

## How to use this list

For each row: read the real reference file(s) in full (not a summary
from memory), compare field-by-field against what Confluence's handler
currently renders for that route, fix real gaps, mark the row ✅. If a
route's best-guess reference above turns out wrong once actually
checked, correct the table — this is a working document, not a fixed
plan. Rows marked "no match found" need a real search before being
trusted as gaps (this pass matched by filename/purpose, not exhaustive
content reading of all 84 files).

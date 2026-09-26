# Security hardening guide

Operator notes for the control-plane hardening in this repository (work
originally prompted by a responsible disclosure against stock OpenSimulator,
ported and adapted from OpenSim-Tranquillity's `security/control-plane-hardening`
branch). Read this before upgrading a production grid. For the feature list see
`FEATURES.md`; for how it was built and what went wrong along the way see
`PROJECT_LOG.md`.

## Control-plane trust

Some HTTP endpoints exist for servers to talk to each other, but are served on
the same public ports as viewer and Hypergrid traffic. The endpoints listed
below now only accept callers that are one of your own servers.

**Nothing needs configuring for a grid whose regions and Robust run on one
machine** - including behind a home router. These are trusted automatically:

- loopback;
- this machine's own network interfaces and their default gateways (a router's
  NAT loopback rewrites a same-box call's source to one of these);
- whatever `[Const] BaseHostname` resolves to.

Only set `ControlPlaneTrustedHosts` when Robust or other regions run on
**different machines**. Put it in `[Network]` (or `[Security]`) of every region
and of Robust:

```ini
[Network]
    ControlPlaneTrustedHosts = "203.0.113.10, region2.example.com"
```

Entries may be IPv4/IPv6 addresses, hostnames, or URLs, separated by commas,
semicolons, pipes or whitespace. Hostnames are resolved once at startup - restart
the process if an address changes. List the real *outbound* address of each
region host and each Robust host. Do not list public client networks, foreign
grids or broad ranges.

### Protected endpoints

| Endpoint | Where | Untrusted caller gets |
|---|---|---|
| `POST /agent/...` (create agent - **grid login uses this**) | region | 403 |
| `POST /object/...` (object crossing/creation) | region | 403 |
| `POST /region/...` (neighbour hello) | region | 404 (deliberately, so scanners can't confirm it exists) |
| `POST /friends` (inter-region friendship state) | region | 403 |
| privileged instant-message dialogs (forced/silent teleport, god kick) | region, Robust | delivery refused |
| Hypergrid groups `POSTGROUP`, `ADDNOTICE` | Robust | 403 |
| Hypergrid logout, unless the session is a real tracked one | Robust | `false` result |

Any request carrying an `X-SecondLife-Shard` header is refused on these paths
even from a trusted address, so an in-world `llHTTPRequest` cannot use a trusted
region as a proxy. Query, update and delete on `/agent/` and `/object/` keep their
protocol behavior (Hypergrid teleport handoff needs them).

### Diagnosing a refusal

Every refusal logs a warning naming the address to add, at most once a minute per
source address and endpoint:

```
[CONTROL PLANE ACCESS]: Refusing POST /agent from 203.0.113.7: source address is not a trusted control-plane host. ...
```

At startup each process logs `N trusted control-plane addresses`; set the log
level to Debug to see the list.

- **Login fails with "Service request failed: [403]"** - credentials were fine;
  the login service (Robust) could not create your root agent on the destination
  region because the region did not trust Robust's address. On a single machine
  this should not happen; on several, add Robust's outbound address to that
  region's `ControlPlaneTrustedHosts`.
- **Teleport or region crossing fails with 403** - add the *source* region's
  outbound address to the *destination* region.
- **"Exception on DoHelloNeighbourCall ... 404"** - the first region named is the
  HTTP destination; add the *other* region's outbound address to it.
- **A third-party service that POSTs straight to `/agent/` or `/object/`** gets 403
  until its stable outbound address is listed. Confirm the exact endpoint and
  addresses with the provider first. Services using normal viewer login, CAPS,
  inventory and asset APIs are unaffected (the bundled marketplace delivers via the
  inventory service and its own separately-authenticated endpoints).

## Reverse proxies and client IPs

`X-Forwarded-For` is honored only when the direct TCP peer is loopback (a proxy
on the same host). From any other peer the socket address is used, so a forged
header cannot fake a trusted or unblocked source. A proxy on another machine will
therefore be seen as the proxy's address.

## Hypergrid behavior

- **Returning home** to this grid is refused unless it comes from a fresh login;
  users must log in again. A visited grid could otherwise replay an avatar circuit.
- **Egress filtering**: caller-supplied Hypergrid HomeURI/gatekeeper URLs are
  refused before any outbound verification or agent transfer if they resolve to
  loopback, private, link-local, CGNAT, unique-local, multicast or reserved
  addresses. Your own gatekeeper stays allowed. The check validates DNS at request
  time only - it does not pin the address for the connection or constrain
  redirects - so keep firewall egress rules in place as well.
- A foreign duplicate-presence claim only displaces an existing foreign session
  when the stored session's home matches the claimed HomeURI.
- Hypergrid friendship deletion and presence notifications require an exact
  friend UUID and the complete shared secret.

## Defaults and logging

- The OpenID connector is commented out in `Robust.ini.example` and
  `Robust.HG.ini.example`. It is a legacy OpenID 2.0 identity provider, is not used by
  viewer login, and has no rate limiting. Enable it only if you deliberately run a
  consumer site that needs it, and then also set `[LoginService] OpenIDServerURL`.
- Reusable web login keys are no longer logged, and neither is the Hypergrid
  service token.
- Map-tile requests always release their process-wide lock; one bad request can no
  longer wedge every later tile request.

## Gloebit add-on

Transaction callbacks now require a random per-transaction key, and OAuth linking
validates a persisted one-shot `state`. Back up the database first: the migrations
add `CallbackKey` (`GloebitTransactions`) and `PendingAuthState` (`GloebitUsers`).

## Profile JSON-RPC gate

The sensitive profile methods are refused unless the caller is a trusted control-plane
host: private notes (read and write), preferences/email, profile and interests writes,
picks and classifieds writes and deletes, and user-data writes. A refusal answers
"Method not found", so a scanner learns nothing about which methods exist, and the
log gets one throttled `[CONTROL PLANE ACCESS] Refusing JSON-RPC ...` line per
source and method per minute. Public profile *reads* (properties, picks, classifieds,
interests, image assets) stay open, so profiles remain viewable from other grids.

The calls are made by each region's profile module for the viewer, not by viewers, so
on a normal grid the callers are your own region processes and are covered by the same
trusted-host discovery as the rest of this page. Requests carrying the in-world-script
HTTP marker are refused even from a trusted address.

**Trade-off:** a resident of this grid visiting *another* grid has that foreign region
call your profile server, from an address you do not trust. Their private notes and
preferences are unavailable, and profile edits made there do not save, until they are
back on a trusted region. This matches upstream Tranquillity's choice.

## Deployment checklist

1. Back up the database (Gloebit migrations).
2. On a multi-machine grid, list every region and Robust outbound address in
   `ControlPlaneTrustedHosts` on every host.
3. Replace the binaries, then restart Robust **and** every region - a running
   process keeps the old assemblies loaded.
4. Test: login, teleport between regions, region crossing, object crossing,
   friends, and Hypergrid travel. Watch the logs for `Refusing` lines.
5. Keep service ports behind firewall rules as well as the application allowlist.
6. Tell users that Hypergrid return-home now requires a fresh login.

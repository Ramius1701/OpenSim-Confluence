# Marketplace — setup and usage

The native, viewer-integrated DirectDelivery marketplace: browse and buy
from a browser, merchants manage listings from the web too (not the
viewer — see "Known limitation" below). Not to be confused with
`addon-modules/OpenSimMarketplace`, a separate, older service-to-service
v2 HTTP API for an external website — that one keeps working unchanged,
this is the primary path going forward. See `PROJECT_LOG.md`'s
"Marketplace rebuild" entries for the full design/build history.

## Known limitation: the viewer's own Marketplace Listings floater is blocked

Firestorm (and AyaneStorm, same codebase lineage) hard-code a check in
`LLSLMMenuUpdater::checkMerchantStatus` (`llviewermenu.cpp`) that hides the
"Marketplace Listings" menu item entirely outside real Second Life,
*before* ever asking the region about it:

```cpp
// <FS:Ansariel> Don't show merchant outbox or SL Marketplace stuff outside SL
if (!LLGridManager::getInstance()->isInSecondLife())
{
    gMenuHolder->getChild<LLView>("MarketplaceListings")->setVisible(false);
    return;
}
```

This means the region's own `DirectDelivery` capability
(`DirectDeliveryModule.cs`) is registered and protocol-correct, but no
mainstream viewer build will ever call it — the floater (and the
"Merchant Outbox" inventory folder, which is only auto-created downstream
of a successful merchant-status check) simply never appear. This is not a
caps/config problem; there is no known workaround short of a patched
viewer build. `DirectDeliveryModule.cs` is left in place, untouched and
dormant, so it works immediately if a non-blocking viewer is ever used —
no code changes needed on that side, only a viewer patch.

Because of this, **inventory association is done entirely through the
web** (`/marketplace/manage`) instead, calling the same
`MarketplaceInventoryOperations.SnapshotListingItem` logic the floater's
own associate-inventory action would have triggered — just invoked from
Robust instead of from the region cap, and operating on a single item
rather than a folder. Everything else (organizing listings, browsing,
buying, delivery) is unaffected by this limitation.

## Server setup

Two config sections, both required, both must use the *same*
`ServiceAccountUUID` (a real, non-zero local account UUID acting as the
neutral custodian for in-transit inventory — use a dedicated system
account, not a real resident's, e.g. Casperia's own "GRID SERVICES"
account):

**`Robust.HG.ini`** (or wherever `[DatabaseService]` lives — StorageProvider/
ConnectionString can be omitted here and inherited from `[DatabaseService]`,
same as `[CurrencyService]`):

```ini
[MarketplaceService]
    LocalServiceModule = "OpenSim.Services.MarketplaceService.dll:MarketplaceListingsService"
    ServiceAccountUUID = "<a real, dedicated non-resident account UUID>"
```

**Every region's `OpenSim.ini`** (direct-to-DB, same bypass-Robust topology
as `[CurrencyService]`/`[AuctionService]`):

```ini
[Modules]
    MarketplaceService = LocalMarketplaceListingsServiceConnector

[MarketplaceService]
    LocalServiceModule = "OpenSim.Services.MarketplaceService.dll:MarketplaceListingsService"
    StorageProvider = "OpenSim.Data.MySQL.dll:MySqlMarketplaceListingsData"
    ConnectionString = "<same DB connection string as CurrencyService/AuctionService>"
    ServiceAccountUUID = "<must match Robust.HG.ini's value exactly>"
    MaxInventoryNodes = 5000
```

Also needs a working `[CurrencyService]` on the **Robust** side (not just
region-side) for the web checkout to actually charge anyone —
`WebInterfaceServiceConnector` reads `ICurrencyService` from Robust's own
config, independently of each region's copy. See `bin/Robust.HG.ini.example`
for the full annotated block of both sections.

Nothing else to install — the `marketplace_listings`/`marketplace_deliveries`
tables are created automatically (additive migration) the first time Robust
boots against the configured database.

## Merchant workflow (listing something for sale)

1. **Organize inventory** — completely ordinary folder management, works
   on any viewer, not gated at all. `Inventory > Marketplace Listings` is
   auto-created the first time you visit `/marketplace/manage`. Drag the
   item you want to sell directly into it (no per-listing subfolder - one
   item there is one sellable thing). This is a deliberate simplification
   from real SL, which nests one folder per listing under `Marketplace
   Listings` so a listing can bundle several items together - not
   supported here; each listing is exactly one item. Copy and Transfer
   permissions required.
2. Log into the web portal, go to **My Listings** (`/marketplace/manage`),
   create a listing: title, description, price, and stock (unlimited, or
   a specific count-on-hand).
3. Open that listing's edit page and use the **Associate Inventory**
   section: pick the item from the dropdown, click Associate. This
   snapshots the item (content-addressed, fingerprinted) as what the
   listing will actually deliver — editing/removing the original item
   afterward doesn't change already-associated listings; re-associate to
   update what's delivered.
4. Check the "Listed" box and save. It's now visible on `/marketplace`.

Re-associating (picking an item again, same or different) at any time
replaces what the listing delivers going forward; anything already
delivered to a buyer is unaffected.

## Buyer workflow

Browse `/marketplace`, open a listing, click Buy. Payment (ConfluenceCurrency
only — no Gloebit) goes directly to the seller, not the grid. Delivery
lands in the buyer's own `Marketplace Purchases` folder (top-level,
auto-created on first delivery), whether or not they're logged in at the
time.

A finite-stock listing that sells out is rejected cleanly before any
currency moves; a payment that fails after stock was provisionally
reserved automatically releases it back.

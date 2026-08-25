using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // Admin-managed catalog of purchasable Store items (prim-capacity packs
    // and self-service region orders) - see IStoreService/IStoreData for the
    // design rationale. Not user-generated; residents only ever read this
    // table via the active-items list.
    public class StoreCatalogItem
    {
        public UUID ID = UUID.Zero;

        // "PrimPack" | "RegionOrder" - plain string, same rationale as
        // SupportTicket.Status: a future item type can be added without a
        // schema change.
        public string ItemType = string.Empty;

        public string Name = string.Empty;
        public string Description = string.Empty;

        // PrimPack only, 0 otherwise.
        public int PrimAmount = 0;

        // RegionOrder only, 0 otherwise.
        public int RegionSizeX = 0;
        public int RegionSizeY = 0;

        // 0 = not offered in this currency.
        public int PriceConfluence = 0;
        public int PriceGloebits = 0;

        // 0 = never expires.
        public int DurationDays = 0;

        public bool IsActive = true;
        public int SortOrder = 0;

        public DateTime Created = DateTime.UtcNow;
        public DateTime Updated = DateTime.UtcNow;
    }

    // One row per resident purchase - OrderType/ResidentName are
    // denormalized copies taken at purchase time (from StoreCatalogItem/the
    // resident's own account) so a later catalog edit or name change can't
    // retroactively change what an existing order means, and so the admin
    // queue never needs a join just to render a row.
    public class StoreOrder
    {
        public UUID ID = UUID.Zero;

        public UUID CatalogItemID = UUID.Zero;

        // Denormalized copy of StoreCatalogItem.ItemType at purchase time.
        public string OrderType = string.Empty;

        public UUID ResidentAvatarID = UUID.Zero;
        public string ResidentName = string.Empty;

        // "Confluence" | "Gloebit" - the resident's choice at checkout.
        public string CurrencyUsed = string.Empty;
        public int AmountCharged = 0;

        // Our ICurrencyService.Transfer transaction ID, or our own
        // StoreGloebitTransaction.ID - whichever currency was used.
        public string PaymentTransactionID = string.Empty;

        // "PendingPayment" | "Paid" | "Fulfilled" | "PaymentFailed" |
        // "AwaitingStart" | "Active" | "Expired" | "Cancelled"
        public string Status = "PendingPayment";

        // PrimPack only - which of the resident's own regions got boosted.
        // Null for RegionOrder items.
        public UUID? TargetRegionID = null;

        // RegionOrder only - filled in at purchase time by the port/
        // location allocators, before any .ini file is written. Null for
        // PrimPack items.
        public string RequestedRegionName = null;
        public int? AllocatedLocationX = null;
        public int? AllocatedLocationY = null;
        public int? AllocatedPort = null;
        public string SimulatorFolderName = null;

        // RegionOrder only - the resident's checkout choice: specific grid
        // coordinates within the admin-configured region-order block,
        // re-verified as still free and in-bounds at fulfillment time (not
        // just at checkout - another order can claim it in between). Null
        // for both means "pick any free spot" (AllocateRegionOrderLocation's
        // existing scan behavior); only one of the two set is invalid and
        // rejected at checkout.
        public int? RequestedLocationX = null;
        public int? RequestedLocationY = null;

        // RegionOrder only - the resident's checkout choice. A value here
        // means "join this estate I already own" (re-verified server-side
        // against the resident's own GetEstatesByOwner list at checkout
        // time - never trusted from the form alone); null means "create a
        // new estate," using RequestedEstateName if given or else
        // "<ResidentName>'s Estate". Null for PrimPack items.
        public int? RequestedEstateID = null;
        public string RequestedEstateName = null;

        // Set once, the moment the admin's "Start Region" click actually
        // fires Process.Start - guards the button from firing twice.
        public DateTime? StartedAt = null;

        // Null = never expires (DurationDays was 0 on the catalog item at
        // purchase time).
        public DateTime? ExpiresAt = null;

        // Admin free text - renewal/extension history. Null until an admin
        // first writes to it.
        public string Notes = null;

        public DateTime Created = DateTime.UtcNow;
        public DateTime Updated = DateTime.UtcNow;
    }

    // One row per avatar's Gloebit OAuth2 authorization. Deliberately
    // independent of the region-side Gloebit addon's own GloebitUsers
    // table - see the Store plan's Gloebit integration section for why -
    // so a resident authorizes once for the portal even if they've also
    // used Gloebit in-world.
    public class StoreGloebitAuth
    {
        public UUID AvatarPrincipalID = UUID.Zero;

        public string GloebitID = null;
        public string AccessToken = null;
        public bool Authorized = false;

        public DateTime Created = DateTime.UtcNow;
        public DateTime Updated = DateTime.UtcNow;
    }

    // One row per Gloebit transaction hold - ID is the value embedded in
    // the asset-enact/consume/cancel-hold-url callbacks Gloebit calls back
    // into. Enacted/Consumed/Cancelled are idempotency guards against
    // repeated webhook delivery, the same pattern the region-side
    // GloebitTransaction class uses.
    public class StoreGloebitTransaction
    {
        public UUID ID = UUID.Zero;
        public UUID StoreOrderID = UUID.Zero;
        public UUID AvatarPrincipalID = UUID.Zero;
        public int Amount = 0;

        // "Submitted" | "EnactGloebit" | "EnactAsset" | "ConsumeGloebit" |
        // "ConsumeAsset" | "Cancelled" | "Failed"
        public string Stage = "Submitted";

        public bool Enacted = false;
        public bool Consumed = false;
        public bool Cancelled = false;

        // Set when Gloebit's webhook reports a non-success reason. Null
        // otherwise.
        public string ResponseReason = null;

        public DateTime Created = DateTime.UtcNow;
        public DateTime Updated = DateTime.UtcNow;
    }
}

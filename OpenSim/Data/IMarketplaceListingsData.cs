using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for MarketplaceListingsService - see MarketplaceListing/
    // DeliveryReceipt (OpenSim.Framework) for the row shapes, and
    // IDeliveryLedger (OpenSim.Region.CoreModules.Framework.Marketplace) for
    // why deliveries are stored via this same interface: this is the DB-backed
    // implementation of that contract, replacing the old v2 addon's JSONL-file
    // DeliveryLedger for the native DirectDelivery path.
    public interface IMarketplaceListingsData
    {
        MarketplaceListing GetListing(int id);

        List<MarketplaceListing> GetListingsBySeller(UUID sellerId);

        // IsListed = true only, newest first.
        List<MarketplaceListing> GetListedListings(int start, int count);

        // Ignores listing.ID; returns the new auto-increment ID.
        int InsertListing(MarketplaceListing listing);

        // Full-row update by listing.ID. False if no row matched.
        bool UpdateListing(MarketplaceListing listing);

        bool SetListedState(int id, bool isListed);

        // Atomically reserves one unit of stock: for a finite-stock listing
        // (CountOnHand not null), decrements it and returns true only if it
        // was > 0 beforehand (a genuine row-locked conditional UPDATE, safe
        // under concurrent buyers racing for the last unit); for an unlimited
        // listing (CountOnHand null), always returns true without writing
        // anything. Returns false if the listing doesn't exist, isn't listed,
        // or (finite stock) is already at zero.
        bool TryReserveStock(int id);

        // Compensating action for a reservation that turned out not to lead
        // to a completed sale (e.g. TryReserveStock succeeded but the
        // subsequent currency charge failed) - increments CountOnHand back
        // by one. A no-op for an unlimited listing (CountOnHand null),
        // since TryReserveStock never decremented anything for one.
        void ReleaseStock(int id);

        bool TryGetDelivery(string deliveryId, out DeliveryReceipt receipt);

        // Plain insert, not upsert - returns false on a delivery_id primary
        // key conflict (the caller, MarketplaceInventoryOperations.Deliver,
        // already checked TryGetDelivery first; a conflict here means a
        // genuine concurrent race on the same delivery_id).
        bool TryInsertDelivery(DeliveryReceipt receipt);
    }
}

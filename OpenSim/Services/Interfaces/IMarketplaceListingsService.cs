using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    // Listing metadata + stock for the native DirectDelivery marketplace -
    // pure CRUD against MarketplaceListing rows, no Scene/inventory
    // dependency (that's MarketplaceInventoryOperations, in
    // OpenSim.Region.CoreModules.Framework.Marketplace). Backed by
    // IMarketplaceListingsData; Robust-hosted (LocalServiceModule =
    // MarketplaceListingsService) and also reachable region-side with no
    // Robust round-trip via LocalMarketplaceListingsServiceConnector, same
    // pattern as ICurrencyService/IAuctionService.
    public interface IMarketplaceListingsService
    {
        MarketplaceListing GetListing(int id);

        List<MarketplaceListing> GetListingsBySeller(UUID sellerId);

        List<MarketplaceListing> GetListedListings(int start, int count);

        // Always created unlisted (IsListed = false) - a listing only
        // becomes visible once SetInventoryAssociation has run at least
        // once (DirectDeliveryModule's PUT /associate_inventory/<id>) and
        // the merchant explicitly lists it.
        MarketplaceListing CreateListing(UUID sellerId, string title, string description, int price, int? countOnHand);

        bool UpdateListing(MarketplaceListing listing);

        bool SetListed(int id, bool isListed);

        bool SetInventoryAssociation(int id, UUID snapshotFolderId, UUID listingFolderId, UUID versionFolderId);

        // See IMarketplaceListingsData.TryReserveStock - call this before
        // charging currency for a purchase, not after.
        bool TryReserveStock(int id);

        // Compensating action if TryReserveStock succeeded but the purchase
        // did not complete (e.g. the currency charge failed) - see
        // IMarketplaceListingsData.ReleaseStock.
        void ReleaseStock(int id);
    }
}

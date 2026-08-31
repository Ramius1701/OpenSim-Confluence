using System.Collections.Generic;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.MarketplaceService
{
    // Native marketplace listings ledger - pure CRUD/stock bookkeeping, no
    // Scene/inventory dependency (see MarketplaceInventoryOperations in
    // OpenSim.Region.CoreModules.Framework.Marketplace for that side).
    public class MarketplaceListingsService : MarketplaceListingsServiceBase, IMarketplaceListingsService, IDeliveryLedger
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public MarketplaceListingsService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[MARKETPLACE LISTINGS SERVICE]: Starting marketplace listings service");
        }

        public MarketplaceListing GetListing(int id)
        {
            return m_Database.GetListing(id);
        }

        public List<MarketplaceListing> GetListingsBySeller(UUID sellerId)
        {
            return m_Database.GetListingsBySeller(sellerId);
        }

        public List<MarketplaceListing> GetListedListings(int start, int count)
        {
            return m_Database.GetListedListings(start, count);
        }

        public MarketplaceListing CreateListing(UUID sellerId, string title, string description, int price, int? countOnHand)
        {
            MarketplaceListing listing = new MarketplaceListing
            {
                SellerID = sellerId,
                Title = title ?? string.Empty,
                Description = description ?? string.Empty,
                Price = price,
                CountOnHand = countOnHand,
                IsListed = false
            };

            listing.ID = m_Database.InsertListing(listing);
            return listing;
        }

        public bool UpdateListing(MarketplaceListing listing)
        {
            return m_Database.UpdateListing(listing);
        }

        public bool SetListed(int id, bool isListed)
        {
            return m_Database.SetListedState(id, isListed);
        }

        public bool SetInventoryAssociation(int id, UUID snapshotFolderId, UUID listingFolderId, UUID versionFolderId)
        {
            MarketplaceListing listing = m_Database.GetListing(id);
            if (listing == null)
                return false;

            listing.SnapshotFolderID = snapshotFolderId;
            listing.ListingFolderID = listingFolderId;
            listing.VersionFolderID = versionFolderId;
            return m_Database.UpdateListing(listing);
        }

        public bool TryReserveStock(int id)
        {
            return m_Database.TryReserveStock(id);
        }

        public void ReleaseStock(int id)
        {
            m_Database.ReleaseStock(id);
        }

        public bool TryGet(string deliveryId, out DeliveryReceipt receipt)
        {
            return m_Database.TryGetDelivery(deliveryId, out receipt);
        }

        public bool TryRecord(DeliveryReceipt receipt, out string error)
        {
            if (m_Database.TryInsertDelivery(receipt))
            {
                error = string.Empty;
                return true;
            }

            error = "Delivery ID is already bound to different delivery data.";
            return false;
        }
    }
}

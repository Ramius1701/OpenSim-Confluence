using log4net;
using Mono.Addins;
using Nini.Config;
using System;
using System.Collections.Generic;
using System.Reflection;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Marketplace
{
    // Direct-to-DB region connector for MarketplaceListingsService - each
    // region talks to the [MarketplaceService] database directly, no Robust
    // round-trip per call, same pattern as LocalCurrencyServiceConnector/
    // LocalAuctionServiceConnector. Exposes both IMarketplaceListingsService
    // (used by DirectDeliveryModule's viewer-facing cap routes) and
    // IDeliveryLedger (passed into MarketplaceInventoryOperations.Deliver) -
    // the underlying MarketplaceListingsService instance implements both, so
    // this just casts rather than loading it twice.
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LocalMarketplaceListingsServiceConnector")]
    public class LocalMarketplaceListingsServiceConnector : ISharedRegionModule, IMarketplaceListingsService, IDeliveryLedger
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private List<Scene> m_Scenes = new List<Scene>();
        protected IMarketplaceListingsService m_service = null;
        protected IDeliveryLedger m_ledger = null;

        private bool m_Enabled = false;

        #region ISharedRegionModule

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public string Name
        {
            get { return "LocalMarketplaceListingsServiceConnector"; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];

            if (moduleConfig == null)
                return;

            string name = moduleConfig.GetString("MarketplaceService", "");
            if (name != Name)
                return;

            IConfig userConfig = source.Configs["MarketplaceService"];
            if (userConfig == null)
            {
                m_log.Error("[MARKETPLACE LOCALCONNECTOR]: MarketplaceService missing from configuration");
                return;
            }

            string serviceDll = userConfig.GetString("LocalServiceModule", String.Empty);

            if (serviceDll == String.Empty)
            {
                m_log.Error("[MARKETPLACE LOCALCONNECTOR]: No LocalServiceModule named in section MarketplaceService");
                return;
            }

            Object[] args = new Object[] { source };
            try
            {
                m_service = ServerUtils.LoadPlugin<IMarketplaceListingsService>(serviceDll, args);
            }
            catch
            {
                m_log.Error("[MARKETPLACE LOCALCONNECTOR]: Failed to load marketplace listings service");
                return;
            }

            if (m_service == null)
            {
                m_log.Error("[MARKETPLACE LOCALCONNECTOR]: Can't load marketplace listings service");
                return;
            }

            m_ledger = m_service as IDeliveryLedger;
            if (m_ledger == null)
            {
                m_log.Error("[MARKETPLACE LOCALCONNECTOR]: Loaded marketplace listings service does not implement IDeliveryLedger");
                return;
            }

            m_Enabled = true;
            m_log.Info("[MARKETPLACE LOCALCONNECTOR]: Enabled!");
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            lock (m_Scenes)
            {
                m_Scenes.Add(scene);
                scene.RegisterModuleInterface<IMarketplaceListingsService>(this);
                scene.RegisterModuleInterface<IDeliveryLedger>(this);
            }
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void PostInitialise()
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            lock (m_Scenes)
            {
                if (m_Scenes.Contains(scene))
                {
                    m_Scenes.Remove(scene);
                    scene.UnregisterModuleInterface<IMarketplaceListingsService>(this);
                    scene.UnregisterModuleInterface<IDeliveryLedger>(this);
                }
            }
        }

        #endregion ISharedRegionModule

        #region IMarketplaceListingsService

        public MarketplaceListing GetListing(int id)
        {
            return m_service.GetListing(id);
        }

        public List<MarketplaceListing> GetListingsBySeller(UUID sellerId)
        {
            return m_service.GetListingsBySeller(sellerId);
        }

        public List<MarketplaceListing> GetListedListings(int start, int count)
        {
            return m_service.GetListedListings(start, count);
        }

        public MarketplaceListing CreateListing(UUID sellerId, string title, string description, int price, int? countOnHand)
        {
            return m_service.CreateListing(sellerId, title, description, price, countOnHand);
        }

        public bool UpdateListing(MarketplaceListing listing)
        {
            return m_service.UpdateListing(listing);
        }

        public bool SetListed(int id, bool isListed)
        {
            return m_service.SetListed(id, isListed);
        }

        public bool SetInventoryAssociation(int id, UUID snapshotFolderId, UUID listingFolderId, UUID versionFolderId)
        {
            return m_service.SetInventoryAssociation(id, snapshotFolderId, listingFolderId, versionFolderId);
        }

        public bool TryReserveStock(int id)
        {
            return m_service.TryReserveStock(id);
        }

        #endregion IMarketplaceListingsService

        #region IDeliveryLedger

        public bool TryGet(string deliveryId, out DeliveryReceipt receipt)
        {
            return m_ledger.TryGet(deliveryId, out receipt);
        }

        public bool TryRecord(DeliveryReceipt receipt, out string error)
        {
            return m_ledger.TryRecord(receipt, out error);
        }

        #endregion IDeliveryLedger
    }
}

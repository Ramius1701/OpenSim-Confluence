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

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Auction
{
    // Same shape as LocalCurrencyServiceConnector - loads IAuctionService
    // from config and registers it on the scene so AuctionModule (and
    // anything else in-process) can request it via
    // scene.RequestModuleInterface<IAuctionService>() rather than each
    // module loading its own separate instance.
    //
    // To enable:
    //   [Modules]
    //       AuctionService = LocalAuctionServiceConnector
    //
    //   [AuctionService]
    //       LocalServiceModule = "OpenSim.Services.AuctionService.dll:AuctionService"
    //       StorageProvider = "OpenSim.Data.MySQL.dll:MySqlAuctionData"
    //       ConnectionString = "<same connection string as the rest of the grid's MySQL>"
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LocalAuctionServiceConnector")]
    public class LocalAuctionServiceConnector : ISharedRegionModule, IAuctionService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private List<Scene> m_Scenes = new List<Scene>();
        protected IAuctionService m_service = null;

        private bool m_Enabled = false;

        #region ISharedRegionModule

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public string Name
        {
            get { return "LocalAuctionServiceConnector"; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];

            if (moduleConfig == null)
                return;

            string name = moduleConfig.GetString("AuctionService", "");
            if (name != Name)
                return;

            IConfig userConfig = source.Configs["AuctionService"];
            if (userConfig == null)
            {
                m_log.Error("[AUCTION LOCALCONNECTOR]: AuctionService missing from configuration");
                return;
            }

            string serviceDll = userConfig.GetString("LocalServiceModule", String.Empty);

            if (serviceDll == String.Empty)
            {
                m_log.Error("[AUCTION LOCALCONNECTOR]: No LocalServiceModule named in section AuctionService");
                return;
            }

            Object[] args = new Object[] { source };
            try
            {
                m_service = ServerUtils.LoadPlugin<IAuctionService>(serviceDll, args);
            }
            catch
            {
                m_log.Error("[AUCTION LOCALCONNECTOR]: Failed to load auction service");
                return;
            }

            if (m_service == null)
            {
                m_log.Error("[AUCTION LOCALCONNECTOR]: Can't load auction service");
                return;
            }

            m_Enabled = true;
            m_log.Info("[AUCTION LOCALCONNECTOR]: Enabled!");
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
                scene.RegisterModuleInterface<IAuctionService>(this);
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
                    scene.UnregisterModuleInterface<IAuctionService>(this);
                }
            }
        }

        #endregion ISharedRegionModule

        #region IAuctionService

        public LandAuction Get(UUID id)
        {
            return m_service.Get(id);
        }

        public LandAuction GetActiveForParcel(UUID regionId, int localId)
        {
            return m_service.GetActiveForParcel(regionId, localId);
        }

        public List<LandAuction> GetActive()
        {
            return m_service.GetActive();
        }

        public List<LandAuction> GetExpiredActive(DateTime now)
        {
            return m_service.GetExpiredActive(now);
        }

        public bool Store(LandAuction auction)
        {
            return m_service.Store(auction);
        }

        public bool PlaceBid(UUID auctionId, UUID bidderId, int amount)
        {
            return m_service.PlaceBid(auctionId, bidderId, amount);
        }

        public List<LandAuctionBid> GetBidHistory(UUID auctionId, int count)
        {
            return m_service.GetBidHistory(auctionId, count);
        }

        public bool EndAuction(UUID auctionId, LandAuctionStatus finalStatus, UUID winnerId, int winningAmount)
        {
            return m_service.EndAuction(auctionId, finalStatus, winnerId, winningAmount);
        }

        #endregion IAuctionService
    }
}

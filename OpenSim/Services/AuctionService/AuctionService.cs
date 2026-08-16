using System;
using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.AuctionService
{
    // Backing service for web-bidding on land auctions - see
    // IAuctionService/IAuctionData for the design rationale. Loaded both
    // region-side (AuctionModule, to start/close auctions and run the
    // expiry sweep) and Robust-side (WebInterfaceServiceConnector, to
    // list auctions and accept bids), same dual-load pattern as
    // ICurrencyService/IEventsService/ISearchService already established.
    public class AuctionService : AuctionServiceBase, IAuctionService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public AuctionService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[AUCTION SERVICE]: Starting auction service");
        }

        public LandAuction Get(UUID id)
        {
            return m_Database.Get(id);
        }

        public LandAuction GetActiveForParcel(UUID regionId, int localId)
        {
            return m_Database.GetActiveForParcel(regionId, localId);
        }

        public List<LandAuction> GetActive()
        {
            return m_Database.GetActive();
        }

        public List<LandAuction> GetExpiredActive(DateTime now)
        {
            return m_Database.GetExpiredActive(now);
        }

        public bool Store(LandAuction auction)
        {
            return m_Database.Store(auction);
        }

        public bool PlaceBid(UUID auctionId, UUID bidderId, int amount)
        {
            return m_Database.PlaceBid(auctionId, bidderId, amount);
        }

        public List<LandAuctionBid> GetBidHistory(UUID auctionId, int count)
        {
            return m_Database.GetBidHistory(auctionId, count);
        }

        public bool EndAuction(UUID auctionId, LandAuctionStatus finalStatus, UUID winnerId, int winningAmount)
        {
            return m_Database.EndAuction(auctionId, finalStatus, winnerId, winningAmount);
        }
    }
}

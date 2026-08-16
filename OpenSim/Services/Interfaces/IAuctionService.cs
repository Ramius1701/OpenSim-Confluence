using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    public interface IAuctionService
    {
        LandAuction Get(UUID id);
        LandAuction GetActiveForParcel(UUID regionId, int localId);
        List<LandAuction> GetActive();
        List<LandAuction> GetExpiredActive(DateTime now);
        bool Store(LandAuction auction);

        // See OpenSim.Data.IAuctionData.PlaceBid for the atomicity
        // guarantee - only the caller that actually beats the current
        // highest bid gets true back.
        bool PlaceBid(UUID auctionId, UUID bidderId, int amount);

        List<LandAuctionBid> GetBidHistory(UUID auctionId, int count);
        bool EndAuction(UUID auctionId, LandAuctionStatus finalStatus, UUID winnerId, int winningAmount);
    }
}

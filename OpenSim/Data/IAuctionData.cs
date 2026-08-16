using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for web-bidding on land auctions - see
    // OpenSim.Framework.LandAuctionData for the design rationale.
    public interface IAuctionData
    {
        LandAuction Get(UUID id);

        // One row per (RegionID, LocalID) with Status=Active - a parcel
        // already up for auction can't be started again while one is
        // still running.
        LandAuction GetActiveForParcel(UUID regionId, int localId);

        List<LandAuction> GetActive();

        // Auctions whose EndsAt has passed but are still Status=Active -
        // the region-side sweep that actually closes them out (transfers
        // land, charges the winner) reads this list.
        List<LandAuction> GetExpiredActive(DateTime now);

        bool Store(LandAuction auction);

        // Atomic: only succeeds (and only then inserts a bid-history row)
        // if the auction is still Active and amount is strictly higher
        // than the current HighestBid - same "highest bid wins, ties don't
        // count" rule a real auction uses. Returns false on any rejection
        // (auction ended, bid too low) rather than throwing, so a caller
        // can show "someone outbid you" instead of a generic error.
        bool PlaceBid(UUID auctionId, UUID bidderId, int amount);

        List<LandAuctionBid> GetBidHistory(UUID auctionId, int count);

        bool EndAuction(UUID auctionId, LandAuctionStatus finalStatus, UUID winnerId, int winningAmount);
    }
}

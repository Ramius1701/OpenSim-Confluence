using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // Web-bidding backing shape for land auctions (see AuctionModule's
    // class-level comment for why this exists - the real viewer has no
    // in-world bidding UI at all, confirmed against Firestorm's own
    // llfloaterauction.h/.cpp: that floater is seller/admin tooling for
    // STARTING an auction, never for bidding on one - real SL auctions
    // were always bid on through the website). Lives in OpenSim.Framework,
    // not OpenSim.Data, for the same layering reason as EventItem/
    // LandSearchRecord - OpenSim.Services.Interfaces needs this shape too
    // and doesn't reference OpenSim.Data.
    public enum LandAuctionStatus
    {
        Active = 0,
        Ended = 1,
        Cancelled = 2
    }

    public class LandAuction
    {
        public UUID ID = UUID.Zero;
        public UUID RegionID = UUID.Zero;
        public string RegionName = string.Empty;
        public int LocalID = 0;
        public string ParcelName = string.Empty;
        public UUID SnapshotID = UUID.Zero;
        public UUID OwnerID = UUID.Zero;
        public int MinBid = 0;
        public DateTime StartedAt = DateTime.UtcNow;
        public DateTime EndsAt = DateTime.UtcNow;
        public LandAuctionStatus Status = LandAuctionStatus.Active;
        public int HighestBid = 0;
        public UUID HighestBidderID = UUID.Zero;
        public UUID WinnerID = UUID.Zero;
        public int WinningAmount = 0;
    }

    public class LandAuctionBid
    {
        public UUID ID = UUID.Zero;
        public UUID AuctionID = UUID.Zero;
        public UUID BidderID = UUID.Zero;
        public int Amount = 0;
        public DateTime BidTime = DateTime.UtcNow;
    }
}

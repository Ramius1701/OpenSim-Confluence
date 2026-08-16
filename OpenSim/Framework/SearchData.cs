using OpenMetaverse;

namespace OpenSim.Framework
{
    // Lives in OpenSim.Framework rather than OpenSim.Data (which would be the
    // more obvious home) for the same reason CurrencyTransfer/CurrencyPurchase
    // do - OpenSim.Services.Interfaces doesn't reference OpenSim.Data, and
    // both ISearchData (OpenSim.Data) and ISearchService
    // (OpenSim.Services.Interfaces) need this same shape, so it has to live
    // somewhere both projects already reference.
    public class LandSearchRecord
    {
        public UUID ParcelID;
        public string Name;
        public bool ForSale;
        public bool Auction;
        public int SalePrice;
        public int Area;
        public float Dwell;

        // Populated only by the enriched queries (SearchPlaces,
        // GetFeaturedPlaces) that a Destination Guide-style caller needs to
        // build a teleport SLURL and show real context - left at their
        // default (empty/zero) by SearchLandForSale, which never needed
        // them. Category is the raw stored `land.Category` int
        // (OpenMetaverse.ParcelCategory) rather than a parsed enum, since
        // every caller so far only wants a display label, not the enum type.
        public string RegionName;
        public string Description;
        public int Category;
        public float LandingX;
        public float LandingY;
        public float LandingZ;
    }
}

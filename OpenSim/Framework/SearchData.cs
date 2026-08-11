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
    }
}

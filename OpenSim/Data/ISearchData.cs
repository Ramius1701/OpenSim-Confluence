using System.Collections.Generic;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Native land/places search backend (see PROJECT_LOG.md Batch 14) -
    // queries the grid's own existing `land` table directly rather than
    // relying on an external XML-RPC search server (the addon-modules
    // OpenSimSearch's only data path). Scoped to land/places search only -
    // events and classifieds have no existing data model anywhere in
    // Confluence and are a separate, larger feature, not covered here.
    //
    // LandSearchRecord itself lives in OpenSim.Framework, not here, because
    // OpenSim.Services.Interfaces.ISearchService needs the same shape and
    // that project doesn't reference OpenSim.Data (same reasoning as
    // CurrencyTransfer/CurrencyPurchase).
    public interface ISearchData
    {
        // Places search (viewer's "Places"/"All" tab) - free-text match
        // against parcel name/description, restricted to parcels the owner
        // opted into the directory (ParcelFlags.ShowDirectory).
        // maxAccess is a real filter against the region's own persisted
        // access level (13/21/42 = PG/Mature/Adult, same convention
        // Util.ConvertMaturityToAccessLevel uses) - parcels don't carry
        // their own maturity in this schema, only the region does.
        List<LandSearchRecord> SearchPlaces(string queryText, int start, int count, int maxAccess);

        // Land-for-sale search (viewer's "Land Sales" tab) - restricted to
        // parcels flagged ForSale (and ShowDirectory), optionally filtered by
        // minimum price/area.
        List<LandSearchRecord> SearchLandForSale(int minPrice, int minArea, int start, int count);

        // Search logging - backs real (not fabricated) Trending chips and
        // autocomplete suggestions on /web/search. Only successful
        // (resultCount>0) searches are logged.
        void LogSearch(string query, string category, int resultCount);
        List<string> GetTrendingQueries(int count);
        List<string> GetSuggestions(string prefix, int count);
    }
}

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    // Native land/places search - see OpenSim.Data.ISearchData for the
    // design rationale (replaces the addon-modules OpenSimSearch's
    // dependency on an external XML-RPC server, land/places only - see
    // PROJECT_LOG.md Batch 14).
    public interface ISearchService
    {
        // maxAccess: real per-region maturity ceiling (13/21/42 =
        // PG/Mature/Adult) - see ISearchData for the rationale.
        List<LandSearchRecord> SearchPlaces(string queryText, int start, int count, int maxAccess);
        // maxPrice = ceiling, minArea = floor, either skipped when <= 0 -
        // see ISearchData.SearchLandForSale for the full rationale.
        List<LandSearchRecord> SearchLandForSale(int maxPrice, int minArea, int start, int count);

        // Destination Guide "Featured" tab - see ISearchData for the
        // rationale (real category set, random order).
        List<LandSearchRecord> GetFeaturedPlaces(int count, int maxAccess);

        // Search logging - backs real Trending chips and autocomplete
        // suggestions on /web/search (see ISearchData for the rationale).
        void LogSearch(string query, string category, int resultCount);
        List<string> GetTrendingQueries(int count);
        List<string> GetSuggestions(string prefix, int count);

        // /myland self-service source - see ISearchData for the rationale.
        List<LandSearchRecord> GetParcelsByOwner(UUID ownerID);
    }
}

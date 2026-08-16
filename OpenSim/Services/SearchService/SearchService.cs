using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.SearchService
{
    // Native land/places search - see ISearchService/ISearchData for the
    // design rationale (replaces the addon-modules OpenSimSearch's
    // dependency on an external XML-RPC search server).
    public class SearchService : SearchServiceBase, ISearchService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public SearchService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[SEARCH SERVICE]: Starting search service");
        }

        public List<LandSearchRecord> SearchPlaces(string queryText, int start, int count, int maxAccess)
        {
            return m_Database.SearchPlaces(queryText, start, count, maxAccess);
        }

        public List<LandSearchRecord> SearchLandForSale(int maxPrice, int minArea, int start, int count)
        {
            return m_Database.SearchLandForSale(maxPrice, minArea, start, count);
        }

        public List<LandSearchRecord> GetFeaturedPlaces(int count, int maxAccess)
        {
            return m_Database.GetFeaturedPlaces(count, maxAccess);
        }

        public void LogSearch(string query, string category, int resultCount)
        {
            m_Database.LogSearch(query, category, resultCount);
        }

        public List<string> GetTrendingQueries(int count)
        {
            return m_Database.GetTrendingQueries(count);
        }

        public List<string> GetSuggestions(string prefix, int count)
        {
            return m_Database.GetSuggestions(prefix, count);
        }
    }
}

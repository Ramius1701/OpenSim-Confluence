using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.NewsService
{
    // Backing service for the WebInterface login-screen/home-page news
    // feed - see ISearchService's sibling design rationale in
    // INewsService/INewsData; grid operator announcements only.
    public class NewsService : NewsServiceBase, INewsService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public NewsService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[NEWS SERVICE]: Starting news service");
        }

        public NewsItem Get(UUID id)
        {
            return m_Database.Get(id);
        }

        public List<NewsItem> GetNews(int start, int count)
        {
            return m_Database.GetNews(start, count);
        }

        public bool Store(NewsItem item)
        {
            return m_Database.Store(item);
        }

        public bool Delete(UUID id)
        {
            return m_Database.Delete(id);
        }
    }
}

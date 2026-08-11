using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    // Backing service for the WebInterface login-screen/home-page news
    // feed - see OpenSim.Data.INewsData for the design rationale. Grid
    // operator announcements shown to everyone, admin-managed only.
    public interface INewsService
    {
        NewsItem Get(UUID id);
        List<NewsItem> GetNews(int start, int count);
        bool Store(NewsItem item);
        bool Delete(UUID id);
    }
}

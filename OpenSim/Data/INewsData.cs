using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for the WebInterface login-screen/home-page news feed
    // (see PROJECT_LOG.md Batch 14) - grid operator announcements shown to
    // everyone, not a per-user or per-region feature, so this deliberately
    // has no scope/owner column the way most other tables in this codebase
    // do.
    public interface INewsData
    {
        NewsItem Get(UUID id);

        // Most recent first.
        List<NewsItem> GetNews(int start, int count);

        // Insert if ID is new, update in place if it already exists.
        bool Store(NewsItem item);

        bool Delete(UUID id);
    }
}

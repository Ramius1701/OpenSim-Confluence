using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for the WebInterface static page manager (see
    // PROJECT_LOG.md Batch 14) - admin-authored content pages (About,
    // Rules, Help, etc.) served at operator-chosen URLs, no code changes
    // needed to add one.
    public interface IStaticPageData
    {
        StaticPage Get(UUID id);
        StaticPage GetBySlug(string slug);

        // Alphabetical by slug - this is an admin-facing management list,
        // not a chronological feed like News.
        List<StaticPage> GetAll();

        // Insert if ID is new, update in place if it already exists.
        // Callers are responsible for checking slug uniqueness themselves
        // (GetBySlug) before calling this for a new page - Store itself
        // doesn't enforce it beyond the DB's own unique index, which would
        // surface as a generic failure, not a friendly "slug taken" message.
        bool Store(StaticPage page);

        bool Delete(UUID id);
    }
}

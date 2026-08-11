using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    // Backing service for the WebInterface static page manager - see
    // OpenSim.Data.IStaticPageData for the design rationale.
    public interface IStaticPageService
    {
        StaticPage Get(UUID id);
        StaticPage GetBySlug(string slug);
        List<StaticPage> GetAll();
        bool Store(StaticPage page);
        bool Delete(UUID id);
    }
}

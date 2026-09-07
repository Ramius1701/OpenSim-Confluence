using System.Collections.Generic;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    // Backing service for the WebUI /register starter-look carousel and its
    // /admin/starter-looks management page. See ROADMAP.md's "Avatar-
    // selection starter-look carousel" entry for the design.
    public interface IStarterLookService
    {
        /// <summary>Enabled looks, sorted by SortOrder - what /register shows.</summary>
        List<StarterLookData> GetEnabledLooks();

        /// <summary>Every look regardless of Enabled, sorted by SortOrder - the admin page's list.</summary>
        List<StarterLookData> GetAllLooks();

        StarterLookData GetLook(int lookID);
        bool AddLook(StarterLookData look);
        bool UpdateLook(StarterLookData look);
        bool DeleteLook(int lookID);
    }
}

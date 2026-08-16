using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // Lives in OpenSim.Framework, not OpenSim.Data, for the same layering
    // reason as NewsItem/LandSearchRecord/CurrencyTransfer.
    public class StaticPage
    {
        public UUID ID = UUID.Zero;

        // URL-safe identifier the page is served at (/web/page/<Slug>) -
        // lowercase, unique. Distinct from Title, which is free-text and
        // shown as the page heading.
        public string Slug = string.Empty;
        public string Title = string.Empty;
        public string Body = string.Empty;
        public DateTime Updated = DateTime.UtcNow;

        // Nav-wiring fields - matches WhiteCore-Dev's real admin/
        // page_manager.html (pages ARE nav items there, with position and
        // visibility rules, not just content). ParentMenuItem-style
        // dropdown nesting and a numeric admin level were left out of this
        // pass - Confluence's nav has no submenu concept yet and its
        // session model is a plain IsAdmin bool, not graduated levels.
        public bool ShowInNav = false;
        public int NavOrder = 0;
        public bool RequiresLogin = false;
        public bool RequiresAdmin = false;
    }
}

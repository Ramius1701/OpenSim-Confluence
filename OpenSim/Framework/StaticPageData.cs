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
    }
}

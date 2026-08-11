using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // Lives in OpenSim.Framework, not OpenSim.Data, for the same layering
    // reason as LandSearchRecord/CurrencyTransfer - OpenSim.Services.Interfaces
    // needs this shape too and doesn't reference OpenSim.Data.
    public class NewsItem
    {
        public UUID ID = UUID.Zero;
        public string Title = string.Empty;
        public string Body = string.Empty;
        public string Author = string.Empty;
        public DateTime Date = DateTime.UtcNow;
    }
}

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    public interface IEventsService
    {
        EventItem Get(UUID id);
        List<EventItem> GetUpcoming(int start, int count);

        // Grid-wide keyword search (title/description/location) - backs
        // the /web/search page and the in-world Directory floater's
        // Events tab (see ConfluenceSearchModule).
        List<EventItem> SearchEvents(string queryText, int start, int count);
        bool Store(EventItem item);
        bool Delete(UUID id);
    }
}

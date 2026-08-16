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

        // Same search, restricted to a single Pacific-time day boundary -
        // backs the viewer Events tab's Date mode (see ConfluenceSearchModule
        // DirEventsQuery / Firestorm's FSPanelSearchEvents::setDay).
        List<EventItem> SearchEventsByDay(string queryText, int dayStartUnix, int dayEndUnix, int start, int count);
        bool Store(EventItem item);
        bool Delete(UUID id);

        // Housekeeping sweep - see IEventsData.DeleteExpired.
        int DeleteExpired(int nowUnix);
    }
}

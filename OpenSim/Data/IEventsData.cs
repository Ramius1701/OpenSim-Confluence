using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for the WebInterface splash page's "Upcoming Events"
    // widget - see OpenSim.Framework.EventItem for the design rationale.
    // Admin-managed grid announcements, same shape as INewsData.
    public interface IEventsData
    {
        EventItem Get(UUID id);

        // Soonest first, EventDate >= now only - past events aren't
        // "upcoming" so they're excluded rather than just sorted last.
        List<EventItem> GetUpcoming(int start, int count);

        // Grid-wide keyword search (title/description/location) - backs the
        // /web/search page. Also restricted to upcoming events only.
        List<EventItem> SearchEvents(string queryText, int start, int count);

        // Insert if ID is new, update in place if it already exists.
        bool Store(EventItem item);

        bool Delete(UUID id);
    }
}

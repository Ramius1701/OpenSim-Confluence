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

        // Same keyword search, but restricted to a single day's Pacific-time
        // boundary [dayStartUnix, dayEndUnix) instead of "upcoming" - backs
        // the in-world Events tab's Date mode (Today/Yesterday/Tomorrow
        // arrows), which asks for one specific day, not everything upcoming.
        List<EventItem> SearchEventsByDay(string queryText, int dayStartUnix, int dayEndUnix, int start, int count);

        // Insert if ID is new, update in place if it already exists.
        bool Store(EventItem item);

        bool Delete(UUID id);

        // Housekeeping sweep (see EventsService's timer) - deletes events
        // whose real end time (EventDate + DurationMinutes) has already
        // passed, not just ones with a past start time, so an event still
        // in progress is never removed out from under it. Returns the
        // number of rows deleted, for logging only.
        int DeleteExpired(int nowUnix);
    }
}

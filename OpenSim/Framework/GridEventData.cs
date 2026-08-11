using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // Lives in OpenSim.Framework, not OpenSim.Data, for the same layering
    // reason as NewsItem - OpenSim.Services.Interfaces needs this shape too
    // and doesn't reference OpenSim.Data. Admin-managed grid events (splash
    // page "Upcoming Events" widget) - deliberately NOT the same thing as
    // OpenSim-Grid-Interface's search_events table, which backs real
    // in-world/viewer-created events via its own UDP-protocol-facing
    // tooling. Building that would mean actually wiring up the classic
    // OpenSim.Framework.EventData/SendEventInfoReply/DirEventsReply search
    // protocol (declared in IClientAPI but never implemented by any
    // module in this codebase, confirmed by grep - a dead protocol
    // surface, not a live feature) - a much bigger lift than the splash
    // page needed. This is a simpler, News-item-shaped admin-only feed.
    // Named GridEventData (not EventData) specifically to avoid colliding
    // with that real, already-existing OpenSim.Framework.EventData class.
    //
    // CreatorId added when this became resident-self-service as well as
    // admin-managed (matching WhiteCore-Dev's own events.html, which lets
    // any logged-in user post an event, not just admins) - lets the web
    // UI restrict edit/delete to an event's own creator while admins keep
    // full access through the separate /admin/events routes. UUID.Zero
    // means "created before this field existed" / "admin-created with no
    // meaningful owner" - never used to grant access, only admin routes
    // bypass the ownership check entirely.
    public class EventItem
    {
        public UUID ID = UUID.Zero;
        public UUID CreatorId = UUID.Zero;
        public string Title = string.Empty;
        public string Category = string.Empty;
        public string Description = string.Empty;
        public DateTime EventDate = DateTime.UtcNow;
        public int DurationMinutes = 60;
        public string Location = string.Empty;
    }
}

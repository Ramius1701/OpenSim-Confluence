using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // Lives in OpenSim.Framework, not OpenSim.Data, for the same layering
    // reason as NewsItem - OpenSim.Services.Interfaces needs this shape too
    // and doesn't reference OpenSim.Data. Admin-managed grid events (splash
    // page "Upcoming Events" widget), also resident-self-service. Named
    // GridEventData (not EventData) to avoid colliding with the real
    // OpenSim.Framework.EventData class - the classic viewer search
    // protocol (SendEventInfoReply/DirEventsReply) IS wired up to this,
    // via ConfluenceSearchModule (see EventInfoRequest/DirEventsQuery).
    //
    // CreatorId added when this became resident-self-service as well as
    // admin-managed (matching WhiteCore-Dev's own events.html, which lets
    // any logged-in user post an event, not just admins) - lets the web
    // UI restrict edit/delete to an event's own creator while admins keep
    // full access through the separate /admin/events routes. UUID.Zero
    // means "created before this field existed" / "admin-created with no
    // meaningful owner" - never used to grant access, only admin routes
    // bypass the ownership check entirely.
    //
    // GlobalPos mirrors UserClassifiedAdd.GlobalPos exactly (a Vector3
    // ToString(), parsed back with Vector3.TryParse) - the same shape the
    // real, proven OpenSimSearch addon's EventInfoRequest already used
    // (its "globalposition" field) for the exact same purpose: giving
    // EventInfoReply a real position so the viewer's Teleport/Map buttons
    // (entirely client-side once GlobalPos is populated) actually work.
    // Empty string means "no location captured" - EventInfoRequest leaves
    // globalPos at its zero default rather than guessing one.
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
        public string GlobalPos = string.Empty;
    }
}

using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // A real, purpose-built admin accountability trail - deliberately
    // separate from WebActivityEntry (web_activity_log), which already
    // exists but only ever records a resident's own self-service events
    // (login, registration, avatar import, purchases). This table
    // captures the opposite: what an admin did, to what, and what it
    // changed - structured enough to answer "show me every ban" or "what
    // did the grid name change from" without parsing free text. Rows are
    // never updated or deleted once written (see IAdminAuditData - no
    // Update/Delete at all) - an audit trail that could be edited after
    // the fact isn't one.
    public class AdminAuditEntry
    {
        public UUID ID = UUID.Zero;

        // The acting admin's own avatar account, and a name SNAPSHOT taken
        // at write time - deliberately not resolved live the way the
        // homepage's Testimonials/Grid Team features do. Those want the
        // current name because they display right now; an audit log wants
        // what was true at the time, so a later rename or account deletion
        // never rewrites history.
        public UUID ActorAccountID = UUID.Zero;
        public string ActorName = string.Empty;

        // Short, filterable category - "user.ban", "gridsettings.update",
        // "estate.update", etc. - not a free-text sentence.
        public string Action = string.Empty;

        // What was acted on. TargetID is a string rather than a UUID field
        // since targets vary in shape (an account/estate UUID, but a grid
        // setting's target is just its key name).
        public string TargetType = string.Empty;
        public string TargetID = string.Empty;
        public string TargetName = string.Empty;

        // Optional before/after snapshot - empty when an action has no
        // single meaningful "value" (e.g. deleting a group).
        public string OldValue = string.Empty;
        public string NewValue = string.Empty;

        public string IPAddress = string.Empty;
        public DateTime Created = DateTime.UtcNow;
    }
}

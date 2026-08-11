using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // Support ticket system - the last item in the "first-landing/legal"
    // tier this session's WhiteCore-Dev-and-beyond web audit called for.
    // Matches OpenSim-Grid-Interface's own support.php: guests can submit
    // (name+email required, so "I can't log in" issues can still be
    // reported), ticket history is only visible to the logged-in resident
    // who filed it, and admins see everything through a separate queue.
    public class SupportTicket
    {
        public UUID ID = UUID.Zero;

        // UUID.Zero means a guest submission - ContactEmail is then how
        // we'd reply, since there's no account to notify in-world.
        public UUID UserId = UUID.Zero;
        public string UserName = string.Empty;
        public string ContactEmail = string.Empty;

        public string Category = "other";
        public string Subject = string.Empty;
        public string Message = string.Empty;

        // "open", "in_progress", "closed" - plain strings rather than an
        // enum so a future status can be added without a schema change,
        // matching how Category is handled elsewhere in this session's
        // admin-managed features (Events).
        public string Status = "open";

        public DateTime Created = DateTime.UtcNow;
        public DateTime Updated = DateTime.UtcNow;
    }
}

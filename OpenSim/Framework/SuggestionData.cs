using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // Resident feedback system for the WebInterface Suggestion Box -
    // same shape as SupportTicket (see that class), just without the
    // required-contact-info constraint: a suggestion can be fully
    // anonymous (SubmitterAvatarID = UUID.Zero, SubmitterName blank).
    public class Suggestion
    {
        public UUID ID = UUID.Zero;

        public UUID SubmitterAvatarID = UUID.Zero;
        public string SubmitterName = string.Empty;

        public string Subject = string.Empty;
        public string Message = string.Empty;

        // "new", "reviewed", "closed" - plain string, same rationale as
        // SupportTicket.Status.
        public string Status = "new";

        public DateTime Created = DateTime.UtcNow;
    }
}

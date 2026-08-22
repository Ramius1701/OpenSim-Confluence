using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // A resident's master account for the WebInterface's multi-avatar
    // system - deliberately separate from OpenSim's own UserAccount/avatar
    // identity, but NOT a separate credential: the account you register or
    // log in with first (via the same avatar-name+in-world-password login
    // that's always existed) IS the master account, auto-linked the moment
    // it has a real email (see AutoProvisionWebAccount). One WebAccount can
    // own multiple avatars (see WebAccountAvatarLink) - Email exists purely
    // for the "two avatars, same email = same person, link them" matching
    // logic, not as a login credential or a separate display concept from
    // whichever avatar's own email is currently active.
    public class WebAccount
    {
        public UUID ID = UUID.Zero;
        public string Email = string.Empty;
        public DateTime Created = DateTime.UtcNow;
        public DateTime Updated = DateTime.UtcNow;
    }

    // One row per linked avatar. AvatarPrincipalID is the table's real
    // primary key (see MySqlWebAccountData) - an avatar can be linked to
    // at most one WebAccount, ever, enforced by the database itself rather
    // than a check-then-hope race in the service layer.
    public class WebAccountAvatarLink
    {
        public UUID WebAccountID = UUID.Zero;
        public UUID AvatarPrincipalID = UUID.Zero;
        public DateTime LinkedDate = DateTime.UtcNow;

        // "Created" | "Imported" | "AutoProvisioned" - plain string, same
        // rationale as SupportTicket.Status: a future link type can be
        // added without a schema change.
        public string LinkType = "AutoProvisioned";
        public bool IsDefault = false;
    }

    // One row per Recent Activity entry (login, registration, avatar
    // linking, etc). AvatarPrincipalID is nullable in spirit - UUID.Zero
    // means the event isn't about one specific avatar.
    public class WebActivityEntry
    {
        public UUID ID = UUID.Zero;
        public UUID WebAccountID = UUID.Zero;
        public UUID AvatarPrincipalID = UUID.Zero;
        public string EventType = string.Empty;
        public string Description = string.Empty;
        public string IPAddress = string.Empty;
        public DateTime Created = DateTime.UtcNow;
    }
}

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    public interface IWebAccountService
    {
        WebAccount GetById(UUID id);
        WebAccount GetByEmail(string email);
        bool Store(WebAccount account);

        // All avatars linked to one WebAccount.
        List<WebAccountAvatarLink> GetLinkedAvatars(UUID webAccountId);

        // The link row for one avatar, or null if it isn't linked to
        // anything yet - the primary way to ask "does this avatar already
        // have a portal account?"
        WebAccountAvatarLink GetLinkForAvatar(UUID avatarPrincipalId);

        // Throws if avatarPrincipalId is already linked to a DIFFERENT
        // WebAccountID (the table's own primary key enforces this) -
        // callers should catch and turn that into a clean rejection
        // message rather than let it propagate as a raw DB error.
        bool LinkAvatar(UUID webAccountId, UUID avatarPrincipalId, string linkType, bool isDefault);

        // Most recent first.
        List<WebActivityEntry> GetRecentActivity(UUID webAccountId, int count);
        bool LogActivity(WebActivityEntry entry);
    }
}

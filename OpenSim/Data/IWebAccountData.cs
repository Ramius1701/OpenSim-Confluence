using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for the WebInterface's portal-account system - see
    // OpenSim.Framework.WebAccount/WebAccountAvatarLink/WebActivityEntry
    // for the design rationale.
    public interface IWebAccountData
    {
        WebAccount GetById(UUID id);
        WebAccount GetByEmail(string email);
        bool Store(WebAccount account);

        List<WebAccountAvatarLink> GetLinkedAvatars(UUID webAccountId);
        WebAccountAvatarLink GetLinkForAvatar(UUID avatarPrincipalId);
        bool LinkAvatar(WebAccountAvatarLink link);

        // Moves an existing link row to a different WebAccountID (used to
        // absorb a solo master account's one avatar into another account -
        // see WebInterfaceServiceConnector.HandleImportAvatar). Re-parents
        // in place rather than delete+insert since AvatarPrincipalID is the
        // table's primary key.
        bool ReassignAvatar(UUID avatarPrincipalId, UUID newWebAccountId, string linkType, bool isDefault);

        // Re-points activity history to the surviving account after a
        // merge, so the absorbed account's audit trail isn't just orphaned.
        bool ReassignActivity(UUID oldWebAccountId, UUID newWebAccountId);

        // Deletes a WebAccount row once a merge has moved every avatar off
        // it - never called while it still owns any link.
        bool DeleteAccount(UUID webAccountId);

        List<WebActivityEntry> GetRecentActivity(UUID webAccountId, int count);
        bool LogActivity(WebActivityEntry entry);
    }
}

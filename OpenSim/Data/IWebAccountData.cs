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

        List<WebActivityEntry> GetRecentActivity(UUID webAccountId, int count);
        bool LogActivity(WebActivityEntry entry);
    }
}

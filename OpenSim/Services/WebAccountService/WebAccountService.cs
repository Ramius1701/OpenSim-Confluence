using System;
using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.WebAccountService
{
    // Backing service for the WebInterface's multi-avatar account system -
    // see IWebAccountService/IWebAccountData for the design rationale.
    public class WebAccountService : WebAccountServiceBase, IWebAccountService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public WebAccountService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[WEB ACCOUNT SERVICE]: Starting web account service");
        }

        public WebAccount GetById(UUID id)
        {
            return m_Database.GetById(id);
        }

        public WebAccount GetByEmail(string email)
        {
            return m_Database.GetByEmail(NormalizeEmail(email));
        }

        public bool Store(WebAccount account)
        {
            account.Email = NormalizeEmail(account.Email);
            return m_Database.Store(account);
        }

        public List<WebAccountAvatarLink> GetLinkedAvatars(UUID webAccountId)
        {
            return m_Database.GetLinkedAvatars(webAccountId);
        }

        public WebAccountAvatarLink GetLinkForAvatar(UUID avatarPrincipalId)
        {
            return m_Database.GetLinkForAvatar(avatarPrincipalId);
        }

        public bool LinkAvatar(UUID webAccountId, UUID avatarPrincipalId, string linkType, bool isDefault)
        {
            return m_Database.LinkAvatar(new WebAccountAvatarLink
            {
                WebAccountID = webAccountId,
                AvatarPrincipalID = avatarPrincipalId,
                LinkedDate = DateTime.UtcNow,
                LinkType = linkType,
                IsDefault = isDefault
            });
        }

        public List<WebActivityEntry> GetRecentActivity(UUID webAccountId, int count)
        {
            return m_Database.GetRecentActivity(webAccountId, count);
        }

        public bool LogActivity(WebActivityEntry entry)
        {
            entry.ID = UUID.Random();
            entry.Created = DateTime.UtcNow;
            return m_Database.LogActivity(entry);
        }

        // Lowercased/trimmed everywhere an email is looked up or stored -
        // "Jane@Example.com" and "jane@example.com" must resolve to the
        // same master account.
        private static string NormalizeEmail(string email)
        {
            return (email ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.MessagingService
{
    // Backing service for the WebInterface resident-to-resident web mail
    // (inbox/sent/compose) - see IMessagingService/IMessagingData for the
    // design rationale.
    public class MessagingService : MessagingServiceBase, IMessagingService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public MessagingService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[MESSAGING SERVICE]: Starting messaging service");
        }

        public WebMessage Get(UUID id)
        {
            return m_Database.Get(id);
        }

        public List<WebMessage> GetInbox(UUID userID, int count)
        {
            return m_Database.GetInbox(userID, count);
        }

        public List<WebMessage> GetSent(UUID userID, int count)
        {
            return m_Database.GetSent(userID, count);
        }

        public bool Store(WebMessage message)
        {
            return m_Database.Store(message);
        }

        public bool MarkRead(UUID id)
        {
            return m_Database.MarkRead(id);
        }

        public bool DeleteForUser(UUID id, UUID userID)
        {
            return m_Database.DeleteForUser(id, userID);
        }
    }
}

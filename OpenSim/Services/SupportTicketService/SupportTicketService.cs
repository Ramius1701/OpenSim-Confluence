using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.SupportTicketService
{
    // Backing service for the WebInterface support ticket system - see
    // ISupportTicketService/ISupportTicketData for the design rationale.
    public class SupportTicketService : SupportTicketServiceBase, ISupportTicketService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public SupportTicketService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[SUPPORT TICKET SERVICE]: Starting support ticket service");
        }

        public SupportTicket Get(UUID id)
        {
            return m_Database.Get(id);
        }

        public List<SupportTicket> GetByUser(UUID userId, int start, int count)
        {
            return m_Database.GetByUser(userId, start, count);
        }

        public List<SupportTicket> GetAll(int start, int count)
        {
            return m_Database.GetAll(start, count);
        }

        public bool Store(SupportTicket ticket)
        {
            return m_Database.Store(ticket);
        }
    }
}

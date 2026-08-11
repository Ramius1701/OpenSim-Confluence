using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    public interface ISupportTicketService
    {
        SupportTicket Get(UUID id);
        List<SupportTicket> GetByUser(UUID userId, int start, int count);
        List<SupportTicket> GetAll(int start, int count);
        bool Store(SupportTicket ticket);
    }
}

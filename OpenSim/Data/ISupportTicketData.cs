using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for the WebInterface support ticket system - see
    // OpenSim.Framework.SupportTicket for the design rationale.
    public interface ISupportTicketData
    {
        SupportTicket Get(UUID id);

        // Most recent first, scoped to one resident's own tickets.
        List<SupportTicket> GetByUser(UUID userId, int start, int count);

        // Most recent first, every ticket - the admin queue.
        List<SupportTicket> GetAll(int start, int count);

        bool Store(SupportTicket ticket);
    }
}

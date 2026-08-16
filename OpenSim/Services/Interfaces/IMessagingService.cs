using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    // Backing service for the WebInterface resident-to-resident web mail
    // (inbox/sent/compose) - see OpenSim.Data.IMessagingData for the
    // design rationale.
    public interface IMessagingService
    {
        WebMessage Get(UUID id);
        List<WebMessage> GetInbox(UUID userID, int count);
        List<WebMessage> GetSent(UUID userID, int count);
        bool Store(WebMessage message);
        bool MarkRead(UUID id);
        bool DeleteForUser(UUID id, UUID userID);
    }
}

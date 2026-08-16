using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for the WebInterface resident-to-resident web mail
    // (inbox/sent/compose) - see OpenSim.Framework.WebMessage for the
    // design rationale.
    public interface IMessagingData
    {
        WebMessage Get(UUID id);

        // Most recent first. Excludes messages the receiver has deleted
        // from their own Inbox.
        List<WebMessage> GetInbox(UUID userID, int count);

        // Most recent first. Excludes messages the sender has deleted
        // from their own Sent view.
        List<WebMessage> GetSent(UUID userID, int count);

        bool Store(WebMessage message);

        bool MarkRead(UUID id);

        // Soft-deletes on whichever side userID matches (Sender or
        // Receiver) - the other party's copy is unaffected.
        bool DeleteForUser(UUID id, UUID userID);
    }
}

using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // Resident-to-resident web mail (inbox/sent/compose) - lives in
    // OpenSim.Framework for the same layering reason as NewsItem/
    // LandSearchRecord: OpenSim.Services.Interfaces needs this shape too
    // and doesn't reference OpenSim.Data. Unlike offline IMs (real viewer
    // protocol, IOfflineIMService), this is a web-only feature with no
    // viewer-side counterpart - same status OpenSim-Grid-Interface's own
    // message.php has (its own bespoke ws_messages table, not a stock
    // OpenSim service). SenderDeleted/ReceiverDeleted are independent
    // soft-delete flags so removing a message from your own Inbox/Sent
    // view doesn't remove the other party's copy.
    public class WebMessage
    {
        public UUID ID = UUID.Zero;
        public UUID SenderID = UUID.Zero;
        public UUID ReceiverID = UUID.Zero;
        public string Subject = string.Empty;
        public string Body = string.Empty;
        public DateTime Created = DateTime.UtcNow;
        public bool IsRead;
        public bool SenderDeleted;
        public bool ReceiverDeleted;
    }
}

using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.EventsService
{
    // Backing service for the WebInterface splash page's "Upcoming Events"
    // widget - see IEventsService/IEventsData for the design rationale;
    // grid operator announcements only.
    public class EventsService : EventsServiceBase, IEventsService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public EventsService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[EVENTS SERVICE]: Starting events service");
        }

        public EventItem Get(UUID id)
        {
            return m_Database.Get(id);
        }

        public List<EventItem> GetUpcoming(int start, int count)
        {
            return m_Database.GetUpcoming(start, count);
        }

        public List<EventItem> SearchEvents(string queryText, int start, int count)
        {
            return m_Database.SearchEvents(queryText, start, count);
        }

        public bool Store(EventItem item)
        {
            return m_Database.Store(item);
        }

        public bool Delete(UUID id)
        {
            return m_Database.Delete(id);
        }
    }
}

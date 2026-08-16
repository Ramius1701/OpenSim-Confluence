using System;
using System.Collections.Generic;
using System.Reflection;
using System.Timers;
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

        // Same expiry-sweep-timer shape as AuctionModule's own
        // m_expirySweepTimer - ended events otherwise sit in the table
        // forever (nothing else ever deletes them), which was flagged as a
        // real DB-bloat concern. 10 minutes rather than Auction's 2 - this
        // is pure housekeeping with no user-facing deadline to honor
        // promptly, unlike an auction actually needing to close on time.
        // Every process that loads this service (each region via
        // ConfluenceSearchModule, Robust via WebInterfaceServiceConnector)
        // runs its own instance/timer against the same shared DB - sweeping
        // already-deleted rows is a harmless no-op, same tradeoff Auction's
        // sweep already accepts.
        private readonly Timer m_expirySweepTimer;

        public EventsService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[EVENTS SERVICE]: Starting events service");

            m_expirySweepTimer = new Timer(600000);
            m_expirySweepTimer.Elapsed += (sender, e) => SweepExpiredEvents();
            m_expirySweepTimer.AutoReset = true;
            m_expirySweepTimer.Start();
        }

        private void SweepExpiredEvents()
        {
            try
            {
                int deleted = m_Database.DeleteExpired(Util.UnixTimeSinceEpoch());
                if (deleted > 0)
                    m_log.InfoFormat("[EVENTS SERVICE]: Swept {0} ended event(s)", deleted);
            }
            catch (Exception e)
            {
                m_log.Error("[EVENTS SERVICE]: Expired-event sweep failed", e);
            }
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

        public List<EventItem> SearchEventsByDay(string queryText, int dayStartUnix, int dayEndUnix, int start, int count)
        {
            return m_Database.SearchEventsByDay(queryText, dayStartUnix, dayEndUnix, start, count);
        }

        public bool Store(EventItem item)
        {
            return m_Database.Store(item);
        }

        public bool Delete(UUID id)
        {
            return m_Database.Delete(id);
        }

        public int DeleteExpired(int nowUnix)
        {
            return m_Database.DeleteExpired(nowUnix);
        }
    }
}

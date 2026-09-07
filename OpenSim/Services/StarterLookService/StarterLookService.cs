using System.Collections.Generic;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.StarterLookService
{
    public class StarterLookService : StarterLookServiceBase, IStarterLookService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public StarterLookService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[STARTER LOOK SERVICE]: Starting starter look service");
        }

        public List<StarterLookData> GetEnabledLooks()
        {
            // Filtered in C#, not via Get("Enabled", "1") - Enabled is a
            // bool-typed column and its on-the-wire representation differs
            // per backend (MySQL tinyint, PGSQL boolean, SQLite integer);
            // fetching everything and filtering here avoids relying on each
            // backend's own string-to-bool coercion for a WHERE clause.
            List<StarterLookData> all = GetAllLooks();
            all.RemoveAll(look => !look.Enabled);
            return all;
        }

        public List<StarterLookData> GetAllLooks()
        {
            StarterLookData[] all = m_Database.Get("1=1");
            return Sorted(all);
        }

        private static List<StarterLookData> Sorted(StarterLookData[] all)
        {
            if (all == null || all.Length == 0)
                return new List<StarterLookData>();

            List<StarterLookData> sorted = new List<StarterLookData>(all);
            sorted.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : a.LookID.CompareTo(b.LookID));
            return sorted;
        }

        public StarterLookData GetLook(int lookID)
        {
            StarterLookData[] found = m_Database.Get("LookID", lookID.ToString());
            if (found == null || found.Length == 0)
                return null;

            return found[0];
        }

        public bool AddLook(StarterLookData look)
        {
            return m_Database.Store(look);
        }

        public bool UpdateLook(StarterLookData look)
        {
            return m_Database.Update(look);
        }

        public bool DeleteLook(int lookID)
        {
            return m_Database.Delete("LookID", lookID.ToString());
        }
    }
}

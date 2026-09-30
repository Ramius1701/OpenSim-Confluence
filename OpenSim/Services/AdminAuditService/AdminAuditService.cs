using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.AdminAuditService
{
    public class AdminAuditService : AdminAuditServiceBase, IAdminAuditService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public AdminAuditService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[ADMIN AUDIT SERVICE]: Starting admin audit service");
        }

        public bool LogAction(AdminAuditEntry entry)
        {
            entry.ID = UUID.Random();
            entry.Created = DateTime.UtcNow;
            return m_Database.LogAction(entry);
        }

        public List<AdminAuditEntry> GetRecent(int count)
        {
            return m_Database.GetRecent(count);
        }
    }
}

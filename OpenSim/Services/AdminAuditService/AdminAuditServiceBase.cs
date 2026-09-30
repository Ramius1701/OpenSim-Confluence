using System;
using Nini.Config;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.AdminAuditService
{
    public class AdminAuditServiceBase : ServiceBase
    {
        protected IAdminAuditData m_Database = null;

        public AdminAuditServiceBase(IConfigSource config)
            : base(config)
        {
            string dllName = string.Empty;
            string connString = string.Empty;
            string realm = "AdminAuditLog";

            IConfig dbConfig = config.Configs["DatabaseService"];
            if (dbConfig != null)
            {
                if (dllName == string.Empty)
                    dllName = dbConfig.GetString("StorageProvider", string.Empty);
                if (connString == string.Empty)
                    connString = dbConfig.GetString("ConnectionString", string.Empty);
            }

            // [AdminAuditService] overrides [DatabaseService], if it exists
            IConfig settingsConfig = config.Configs["AdminAuditService"];
            if (settingsConfig != null)
            {
                dllName = settingsConfig.GetString("StorageProvider", dllName);
                connString = settingsConfig.GetString("ConnectionString", connString);
                realm = settingsConfig.GetString("Realm", realm);
            }

            if (dllName.Equals(string.Empty))
                throw new Exception("No StorageProvider configured");

            m_Database = LoadPlugin<IAdminAuditData>(dllName, new object[] { connString });
            if (m_Database == null)
                throw new Exception("Could not find a storage interface in the given module " + dllName);
        }
    }
}

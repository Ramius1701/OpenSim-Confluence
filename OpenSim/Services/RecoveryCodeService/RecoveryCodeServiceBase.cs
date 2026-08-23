using System;
using Nini.Config;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.RecoveryCodeService
{
    public class RecoveryCodeServiceBase : ServiceBase
    {
        protected IRecoveryCodeData m_Database = null;

        public RecoveryCodeServiceBase(IConfigSource config)
            : base(config)
        {
            string dllName = string.Empty;
            string connString = string.Empty;

            IConfig dbConfig = config.Configs["DatabaseService"];
            if (dbConfig != null)
            {
                if (dllName == string.Empty)
                    dllName = dbConfig.GetString("StorageProvider", string.Empty);
                if (connString == string.Empty)
                    connString = dbConfig.GetString("ConnectionString", string.Empty);
            }

            // [RecoveryCodeService] overrides [DatabaseService], if it exists
            IConfig recoveryConfig = config.Configs["RecoveryCodeService"];
            if (recoveryConfig != null)
            {
                dllName = recoveryConfig.GetString("StorageProvider", dllName);
                connString = recoveryConfig.GetString("ConnectionString", connString);
            }

            if (dllName.Equals(string.Empty))
                throw new Exception("No StorageProvider configured");

            m_Database = LoadPlugin<IRecoveryCodeData>(dllName, new object[] { connString });
            if (m_Database == null)
                throw new Exception("Could not find a storage interface in the given module " + dllName);
        }
    }
}

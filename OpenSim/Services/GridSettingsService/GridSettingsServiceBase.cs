using System;
using Nini.Config;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.GridSettingsService
{
    public class GridSettingsServiceBase : ServiceBase
    {
        protected IGridSettingsData m_Database = null;

        public GridSettingsServiceBase(IConfigSource config)
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

            // [GridSettingsService] overrides [DatabaseService], if it exists
            IConfig settingsConfig = config.Configs["GridSettingsService"];
            if (settingsConfig != null)
            {
                dllName = settingsConfig.GetString("StorageProvider", dllName);
                connString = settingsConfig.GetString("ConnectionString", connString);
            }

            if (dllName.Equals(string.Empty))
                throw new Exception("No StorageProvider configured");

            m_Database = LoadPlugin<IGridSettingsData>(dllName, new object[] { connString });
            if (m_Database == null)
                throw new Exception("Could not find a storage interface in the given module " + dllName);
        }
    }
}

using System;
using Nini.Config;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.WebSessionService
{
    public class WebSessionServiceBase : ServiceBase
    {
        protected IWebSessionData m_Database = null;

        public WebSessionServiceBase(IConfigSource config)
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

            // [WebSessionService] overrides [DatabaseService], if it exists
            IConfig sessionConfig = config.Configs["WebSessionService"];
            if (sessionConfig != null)
            {
                dllName = sessionConfig.GetString("StorageProvider", dllName);
                connString = sessionConfig.GetString("ConnectionString", connString);
            }

            if (dllName.Equals(string.Empty))
                throw new Exception("No StorageProvider configured");

            m_Database = LoadPlugin<IWebSessionData>(dllName, new object[] { connString });
            if (m_Database == null)
                throw new Exception("Could not find a storage interface in the given module " + dllName);
        }
    }
}

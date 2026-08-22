using System;
using Nini.Config;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.WebAccountService
{
    public class WebAccountServiceBase : ServiceBase
    {
        protected IWebAccountData m_Database = null;

        public WebAccountServiceBase(IConfigSource config)
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

            // [WebAccountService] overrides [DatabaseService], if it exists
            IConfig accountConfig = config.Configs["WebAccountService"];
            if (accountConfig != null)
            {
                dllName = accountConfig.GetString("StorageProvider", dllName);
                connString = accountConfig.GetString("ConnectionString", connString);
            }

            if (dllName.Equals(string.Empty))
                throw new Exception("No StorageProvider configured");

            m_Database = LoadPlugin<IWebAccountData>(dllName, new object[] { connString });
            if (m_Database == null)
                throw new Exception("Could not find a storage interface in the given module " + dllName);
        }
    }
}

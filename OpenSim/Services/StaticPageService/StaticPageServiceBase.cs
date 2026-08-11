using System;
using Nini.Config;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.StaticPageService
{
    public class StaticPageServiceBase : ServiceBase
    {
        protected IStaticPageData m_Database = null;

        public StaticPageServiceBase(IConfigSource config)
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

            // [StaticPageService] overrides [DatabaseService], if it exists
            IConfig pageConfig = config.Configs["StaticPageService"];
            if (pageConfig != null)
            {
                dllName = pageConfig.GetString("StorageProvider", dllName);
                connString = pageConfig.GetString("ConnectionString", connString);
            }

            if (dllName.Equals(string.Empty))
                throw new Exception("No StorageProvider configured");

            m_Database = LoadPlugin<IStaticPageData>(dllName, new object[] { connString });
            if (m_Database == null)
                throw new Exception("Could not find a storage interface in the given module " + dllName);
        }
    }
}

using System;
using Nini.Config;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.MarketplaceService
{
    public class MarketplaceListingsServiceBase : ServiceBase
    {
        protected IMarketplaceListingsData m_Database = null;

        public MarketplaceListingsServiceBase(IConfigSource config)
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

            // [MarketplaceService] overrides [DatabaseService], if it exists
            IConfig marketplaceConfig = config.Configs["MarketplaceService"];
            if (marketplaceConfig != null)
            {
                dllName = marketplaceConfig.GetString("StorageProvider", dllName);
                connString = marketplaceConfig.GetString("ConnectionString", connString);
            }

            if (dllName.Equals(string.Empty))
                throw new Exception("No StorageProvider configured");

            m_Database = LoadPlugin<IMarketplaceListingsData>(dllName, new object[] { connString });
            if (m_Database == null)
                throw new Exception("Could not find a storage interface in the given module " + dllName);
        }
    }
}

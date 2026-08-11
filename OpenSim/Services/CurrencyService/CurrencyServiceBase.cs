using System;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.CurrencyService
{
    public class CurrencyServiceBase : ServiceBase
    {
        protected ICurrencyData m_Database = null;

        public CurrencyServiceBase(IConfigSource config)
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

            // [CurrencyService] overrides [DatabaseService], if it exists
            IConfig currencyConfig = config.Configs["CurrencyService"];
            if (currencyConfig != null)
            {
                dllName = currencyConfig.GetString("StorageProvider", dllName);
                connString = currencyConfig.GetString("ConnectionString", connString);
            }

            if (dllName.Equals(string.Empty))
                throw new Exception("No StorageProvider configured");

            m_Database = LoadPlugin<ICurrencyData>(dllName, new object[] { connString });
            if (m_Database == null)
                throw new Exception("Could not find a storage interface in the given module " + dllName);
        }
    }
}

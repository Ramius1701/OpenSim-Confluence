using System;
using Nini.Config;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.MessagingService
{
    public class MessagingServiceBase : ServiceBase
    {
        protected IMessagingData m_Database = null;

        public MessagingServiceBase(IConfigSource config)
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

            // [MessagingService] overrides [DatabaseService], if it exists
            IConfig messagingConfig = config.Configs["MessagingService"];
            if (messagingConfig != null)
            {
                dllName = messagingConfig.GetString("StorageProvider", dllName);
                connString = messagingConfig.GetString("ConnectionString", connString);
            }

            if (dllName.Equals(string.Empty))
                throw new Exception("No StorageProvider configured");

            m_Database = LoadPlugin<IMessagingData>(dllName, new object[] { connString });
            if (m_Database == null)
                throw new Exception("Could not find a storage interface in the given module " + dllName);
        }
    }
}

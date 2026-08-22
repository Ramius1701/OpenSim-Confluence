using System;
using Nini.Config;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.SuggestionService
{
    public class SuggestionServiceBase : ServiceBase
    {
        protected ISuggestionData m_Database = null;

        public SuggestionServiceBase(IConfigSource config)
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

            // [SuggestionService] overrides [DatabaseService], if it exists
            IConfig suggestionConfig = config.Configs["SuggestionService"];
            if (suggestionConfig != null)
            {
                dllName = suggestionConfig.GetString("StorageProvider", dllName);
                connString = suggestionConfig.GetString("ConnectionString", connString);
            }

            if (dllName.Equals(string.Empty))
                throw new Exception("No StorageProvider configured");

            m_Database = LoadPlugin<ISuggestionData>(dllName, new object[] { connString });
            if (m_Database == null)
                throw new Exception("Could not find a storage interface in the given module " + dllName);
        }
    }
}

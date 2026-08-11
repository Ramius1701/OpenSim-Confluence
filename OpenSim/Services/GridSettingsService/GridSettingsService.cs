using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.GridSettingsService
{
    public class GridSettingsService : GridSettingsServiceBase, IGridSettingsService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public GridSettingsService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[GRID SETTINGS SERVICE]: Starting grid settings service");
        }

        public string Get(string key)
        {
            return m_Database.Get(key);
        }

        public Dictionary<string, string> GetAll()
        {
            return m_Database.GetAll();
        }

        public bool Set(string key, string value)
        {
            return m_Database.Set(key, value);
        }
    }
}

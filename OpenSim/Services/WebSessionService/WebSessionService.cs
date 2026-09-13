using System.Reflection;
using Nini.Config;
using log4net;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.WebSessionService
{
    public class WebSessionService : WebSessionServiceBase, IWebSessionService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public WebSessionService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[WEB SESSION SERVICE]: Starting web session service");
        }

        public WebSessionRecord Get(string token)
        {
            return m_Database.Get(token);
        }

        public bool Store(WebSessionRecord session)
        {
            return m_Database.Store(session);
        }

        public bool Delete(string token)
        {
            return m_Database.Delete(token);
        }
    }
}

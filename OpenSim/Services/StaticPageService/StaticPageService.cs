using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.StaticPageService
{
    public class StaticPageService : StaticPageServiceBase, IStaticPageService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public StaticPageService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[STATIC PAGE SERVICE]: Starting static page service");
        }

        public StaticPage Get(UUID id)
        {
            return m_Database.Get(id);
        }

        public StaticPage GetBySlug(string slug)
        {
            return m_Database.GetBySlug(slug);
        }

        public List<StaticPage> GetAll()
        {
            return m_Database.GetAll();
        }

        public bool Store(StaticPage page)
        {
            return m_Database.Store(page);
        }

        public bool Delete(UUID id)
        {
            return m_Database.Delete(id);
        }
    }
}

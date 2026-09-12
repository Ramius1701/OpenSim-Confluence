using System;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Handlers.WebInterface;

namespace OpenSim.Region.CoreModules.Web.StandaloneWebInterface
{
    // Confluence's own WebUI (registration, dashboard, admin panel, Store,
    // currency - everything WebInterfaceServiceConnector serves) only ever
    // ran inside Robust.exe, loaded through Robust's own [ServiceListeners]
    // config - nothing hosted it for a standalone deployment (a single
    // OpenSim.exe, no separate Robust process at all), leaving standalone
    // operators with only the old console-driven admin tools. Found via a
    // real fresh-clone test, 2026-09-13, and confirmed as a genuine gap
    // against this project's own "easy setup for any grid owner" mission -
    // Casperia itself only ever runs grid mode, so this was never exercised
    // there either.
    //
    // WebInterfaceServiceConnector's own constructor turned out to already
    // be hosting-agnostic - it only ever touches (IConfigSource, IHttpServer,
    // string) and resolves every dependency through LoadReusedPlugin against
    // whatever IConfigSource it's handed, exactly like every other
    // IServiceConnector. Standalone.ini/StandaloneCommon.ini already
    // configure Local* connectors for every service it depends on, so
    // instantiating it here - against this region process's own
    // MainServer.Instance instead of Robust's HTTP server - needed no
    // changes to that class at all, just a real caller.
    //
    // Opt-in only via [WebInterface] Enabled = true (set in
    // Standalone.ini/StandaloneHypergrid.ini, deliberately NOT in
    // Grid.ini/GridHypergrid.ini) - grid-mode Casperia already gets the
    // WebUI from Robust, and loading a second copy region-side would either
    // crash on a duplicate route registration or behave unpredictably.
    // Static start-once guard because RegionLoaded fires once per region,
    // but this must only ever construct the connector a single time even
    // with multiple regions in one standalone process (VarRegion setups,
    // or a standalone operator running more than one region for testing).
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "StandaloneWebInterfaceModule")]
    public class StandaloneWebInterfaceModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object m_startLock = new object();
        private static bool m_started = false;

        private IConfigSource m_config;
        private bool m_enabled = false;

        public string Name => "Standalone WebInterface Module";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            m_config = source;
            IConfig webConfig = source.Configs["WebInterface"];
            m_enabled = webConfig != null && webConfig.GetBoolean("Enabled", false);
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_startLock)
            {
                if (m_started)
                    return;
                m_started = true;

                try
                {
                    new WebInterfaceServiceConnector(m_config, MainServer.Instance, "WebInterfaceService");
                    m_log.Info("[STANDALONE WEB INTERFACE]: Web UI started on this region's own HTTP server.");
                }
                catch (Exception e)
                {
                    m_log.Error("[STANDALONE WEB INTERFACE]: Failed to start the Web UI", e);
                }
            }
        }
    }
}

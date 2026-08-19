using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using log4net;
using Nini.Config;
using Mono.Addins;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.ubOde
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ubODEPhysicsScene")]
    class ubOdeModule : INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        ODEScene m_odeScene = null;

        private IConfigSource m_config;
        private string m_libVersion = string.Empty;
        private bool m_Enabled = false;

        #region INonSharedRegionModule

        public string Name
        {
            get { return "ubODE"; }
        }

        public string Version
        {
            get { return "1.0"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        // Self-heals a real, observed startup race: when two region
        // processes on the same host both load lib64/ubode.dll for the
        // first time within the same second or two, one of them can hit a
        // transient DllNotFoundException even though the file is present
        // and correct - most likely Windows Defender (or another AV)
        // briefly locking the file for on-access scanning. Since the two
        // processes share no memory, the only thing that can make one
        // process's native load fail because of the other is contention
        // on the file itself; a short wait and retry is the appropriate
        // fix for that specific failure mode. Confirmed live: relaunching
        // the crashed process by hand, seconds later, always succeeded
        // immediately - this automates exactly that recovery instead of
        // needing a human to notice and relaunch it.
        private const int InitOdeMaxAttempts = 3;
        private const int InitOdeRetryDelayMs = 2000;

        private void InitODEWithRetry()
        {
            for (int attempt = 1; attempt <= InitOdeMaxAttempts; attempt++)
            {
                try
                {
                    UBOdeNative.InitODE();
                    return;
                }
                catch (DllNotFoundException e) when (attempt < InitOdeMaxAttempts)
                {
                    m_log.Warn(
                        $"[ubODE] Native library failed to load on attempt {attempt}/{InitOdeMaxAttempts} " +
                        $"({e.Message}) - likely transient (e.g. antivirus scanning the file on first access " +
                        $"while another region process loads it at the same time). Retrying in {InitOdeRetryDelayMs}ms.");
                    Thread.Sleep(InitOdeRetryDelayMs);
                }
            }

            // Final attempt: let a genuine failure (missing/corrupt file,
            // wrong architecture, etc.) throw and fail startup loudly, same
            // as before this retry existed - only the transient case is
            // masked, not a real missing dependency.
            UBOdeNative.InitODE();
        }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["Startup"];
            if (config != null)
            {
                string physics = config.GetString("physics", string.Empty);
                if (physics == Name)
                {
                    m_config = source;
                    string mesher = config.GetString("meshing", string.Empty);
                    
                    if (string.IsNullOrEmpty(mesher) || !mesher.Equals("ubODEMeshmerizer"))
                    {
                        m_log.Error("[ubODE] Opensim.ini meshing option must be set to \"ubODEMeshmerizer\"");
                        //throw new Exception("Invalid physics meshing option");
                    }

                    DllmapConfigHelper.RegisterAssembly(typeof(ubOdeModule).Assembly);

                    InitODEWithRetry();

                    string ode_config = UBOdeNative.GetConfiguration();
                    if (string.IsNullOrEmpty(ode_config))
                    {
                        m_log.Error("[ubODE] Native ode library version not supported");
                        return;
                    }

                    int indx = ode_config.IndexOf("ODE_OPENSIM");
                    if (indx < 0)
                    {
                        m_log.Error("[ubODE] Native ode library version not supported");
                        return;
                    }
                    indx += 12;
                    if (indx >= ode_config.Length)
                    {
                        m_log.Error("[ubODE] Native ode library version not supported");
                        return;
                    }
                    m_libVersion = ode_config.Substring(indx);
                    if (string.IsNullOrEmpty(m_libVersion))
                    {
                        m_log.Error("[ubODE] Native ode library version not supported");
                        return;
                    }
                    m_libVersion.Trim();
                    if(m_libVersion.StartsWith("OS"))
                        m_libVersion = m_libVersion.Substring(2);

                    m_log.InfoFormat("[ubODE] ode library configuration: {0}", ode_config);
                    m_Enabled = true;
                }
            }
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_odeScene = new ODEScene(scene, m_config, Name, Version + "-" + m_libVersion);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            // a odescene.dispose is called later directly by scene.cs
            // since it is seen as a module interface

            m_odeScene = null;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_odeScene?.RegionLoaded();

        }
        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Connectors;

using OpenMetaverse;
using log4net;
using Mono.Addins;
using Nini.Config;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.AbuseReports
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RemoteAbuseReportsServicesConnector")]
    public class RemoteAbuseReportsServicesConnector : ISharedRegionModule, IAbuseReportsService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        #region ISharedRegionModule

        private bool m_Enabled = false;

        private IAbuseReportsService m_remoteConnector;

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public string Name
        {
            get { return "RemoteAbuseReportsServicesConnector"; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig == null)
                return;

            string name = moduleConfig.GetString("AbuseReportsService", "");
            if (name != Name)
                return;

            m_remoteConnector = new AbuseReportsServicesConnector(source);
            m_Enabled = true;

            m_log.Info("[ABUSE REPORTS CONNECTOR]: Remote AbuseReportsService enabled");
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            scene.RegisterModuleInterface<IAbuseReportsService>(this);
            m_log.InfoFormat("[ABUSE REPORTS CONNECTOR]: Enabled for region {0}", scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;
        }

        #endregion

        #region IAbuseReportsService
        public bool ReportAbuse(AbuseReportData report)
        {
            if (!m_Enabled)
                return false;
            return m_remoteConnector.ReportAbuse(report);
        }

        // No region-side code has a legitimate reason to retrieve abuse
        // reports - that's an admin-tool concern, served directly by the
        // Robust getreports/getreport HTTP endpoints. Honest stubs only.
        public List<AbuseReportData> GetAbuseReports(int start, int count)
        {
            return new List<AbuseReportData>();
        }

        public AbuseReportData GetAbuseReport(int reportID)
        {
            return null;
        }
        #endregion IAbuseReportsService

    }
}

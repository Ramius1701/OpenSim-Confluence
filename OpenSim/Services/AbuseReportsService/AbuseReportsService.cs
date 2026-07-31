using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Data;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

namespace OpenSim.Services.AbuseReportsService
{
    public class AbuseReportsService : AbuseReportsServiceBase, IAbuseReportsService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public AbuseReportsService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[ABUSE REPORTS SERVICE]: Starting abuse reports service");
        }

        public bool ReportAbuse(AbuseReportData report)
        {
			if (!m_Database.Store(report))
				return false;

            return true;
        }

        public List<AbuseReportData> GetAbuseReports(int start, int count)
        {
            AbuseReportData[] all = m_Database.Get("1=1");
            if (all == null || all.Length == 0)
                return new List<AbuseReportData>();

            List<AbuseReportData> sorted = new List<AbuseReportData>(all);
            sorted.Sort((a, b) => b.ReportID.CompareTo(a.ReportID));

            if (start < 0)
                start = 0;
            if (start >= sorted.Count)
                return new List<AbuseReportData>();

            int take = count;
            if (take < 0 || start + take > sorted.Count)
                take = sorted.Count - start;

            return sorted.GetRange(start, take);
        }

        public AbuseReportData GetAbuseReport(int reportID)
        {
            AbuseReportData[] found = m_Database.Get("ReportID", reportID.ToString());
            if (found == null || found.Length == 0)
                return null;

            return found[0];
        }
    }
}

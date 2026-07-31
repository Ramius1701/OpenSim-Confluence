using System;
using System.Collections.Generic;
using OpenSim.Framework;
using OpenMetaverse;

namespace OpenSim.Services.Interfaces
{
    public interface IAbuseReportsService
    {
        bool ReportAbuse(AbuseReportData report);

        /// <summary>
        /// Admin-tool retrieval. Region-side code has no legitimate reason
        /// to call these; only the Local connector delegates for real.
        /// </summary>
        List<AbuseReportData> GetAbuseReports(int start, int count);
        AbuseReportData GetAbuseReport(int reportID);
    }
}

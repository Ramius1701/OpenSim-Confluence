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

        /// <summary>
        /// Admin-tool update - changes an existing report's Active/AssignedTo/
        /// Notes fields. Same admin-only reasoning as GetAbuseReports/
        /// GetAbuseReport above; region-side code has no legitimate reason to
        /// call this either.
        /// </summary>
        bool UpdateAbuseReport(AbuseReportData report);

        /// <summary>
        /// Admin-tool deletion, deliberately restricted by the caller to
        /// already-closed (Active == false) reports only - an open report
        /// is still evidence of something unresolved and shouldn't be
        /// removable by accident. Same admin-only reasoning as
        /// GetAbuseReports/UpdateAbuseReport above.
        /// </summary>
        bool DeleteAbuseReport(int reportID);
    }
}

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    public interface IAbuseReportsData
    {
        bool Store(AbuseReportData data);

        /// <summary>
        /// Already provided by MySQLGenericTableHandler&lt;AbuseReportData&gt;
        /// for any implementation that derives from it (e.g. MySqlAbuseReportsData) -
        /// declared here so the service layer can call it through the interface.
        /// </summary>
        AbuseReportData[] Get(string field, string key);
        AbuseReportData[] Get(string where);
    }
}

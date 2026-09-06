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

        /// <summary>
        /// Updates an existing report's admin-tool fields (Active/AssignedTo/
        /// Notes) by ReportID. NOT the same as Store() - each backend's own
        /// Store() override either always INSERTs (PGSQL/SQLite, which
        /// deliberately exclude ReportID so their auto-increment still
        /// works) or is a MySQL-only REPLACE INTO upsert, so calling Store()
        /// again on an existing report is not a portable way to update one.
        /// </summary>
        bool Update(AbuseReportData data);
    }
}

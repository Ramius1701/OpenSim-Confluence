using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using OpenMetaverse;
using OpenSim.Framework;
using MySql.Data.MySqlClient;
using System.Reflection;

namespace OpenSim.Data.MySQL
{
    public class MySqlAbuseReportsData : MySQLGenericTableHandler<AbuseReportData>, IAbuseReportsData
    {
        public MySqlAbuseReportsData(string connectionString)
                : base(connectionString, "AbuseReports", "AbuseReports")
        {
        }


        public override bool Store(AbuseReportData row)
        {
            using (MySqlCommand cmd = new MySqlCommand())
            {
                string query = "";
                List<String> names = new List<String>();
                List<String> values = new List<String>();

                foreach (FieldInfo fi in m_Fields.Values)
                {
                    names.Add(fi.Name);
                    values.Add("?" + fi.Name);

                    // Several fields (ImageData, Category, Details, Summary,
                    // Version, ...) are only conditionally set by the viewer
                    // caps handler (e.g. reports submitted without a
                    // screenshot never set ImageData), so null here is
                    // expected rather than exceptional - fall back to safe
                    // empty defaults instead of throwing.
                    object value = fi.GetValue(row);

                    if (fi.Name == "ImageData")
                        cmd.Parameters.Add("ImageData", MySqlDbType.Blob).Value = value ?? Array.Empty<byte>();
                    // Every other field goes through value?.ToString() below,
                    // which for a real bool produces "True"/"False" - not a
                    // valid value for the TINYINT(1) column Active is stored
                    // as, and MySQL's non-strict-mode coercion would silently
                    // turn either string into 0. Found this while adding
                    // Active - converted explicitly instead, same as the
                    // existing ImageData special case just above.
                    else if (value is bool b)
                        cmd.Parameters.AddWithValue(fi.Name, b ? 1 : 0);
                    else
                        cmd.Parameters.AddWithValue(fi.Name, value?.ToString() ?? string.Empty);
                }

                query = String.Format("replace into {0} (`", m_Realm) + String.Join("`,`", names.ToArray()) + "`) values (" + String.Join(",", values.ToArray()) + ")";

                cmd.CommandText = query;

                if (ExecuteNonQuery(cmd) > 0)
                    return true;

                return false;
            }
        }

        // Store() above is an INSERT (via REPLACE INTO, keyed on ReportID) -
        // fine for a brand new report, but PGSQL/SQLite's own Store()
        // overrides deliberately exclude ReportID so their auto-increment
        // still works, meaning calling Store() again on an existing report
        // there would insert a second row, not update the first. Update()
        // exists so all three backends have one real, portable way to
        // change an existing report's admin-tool fields (Active/AssignedTo/
        // Notes) without relying on REPLACE INTO's MySQL-only upsert
        // behavior.
        public bool Update(AbuseReportData row)
        {
            using (MySqlCommand cmd = new MySqlCommand())
            {
                cmd.CommandText = "UPDATE `AbuseReports` SET `Active` = ?Active, `AssignedTo` = ?AssignedTo, `Notes` = ?Notes WHERE `ReportID` = ?ReportID";
                cmd.Parameters.AddWithValue("Active", row.Active ? 1 : 0);
                cmd.Parameters.AddWithValue("AssignedTo", row.AssignedTo ?? string.Empty);
                cmd.Parameters.AddWithValue("Notes", row.Notes ?? string.Empty);
                cmd.Parameters.AddWithValue("ReportID", row.ReportID);

                return ExecuteNonQuery(cmd) > 0;
            }
        }
    }
}

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
    }
}

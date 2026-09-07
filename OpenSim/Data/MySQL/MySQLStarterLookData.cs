using System;
using System.Collections.Generic;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    public class MySqlStarterLookData : MySQLGenericTableHandler<StarterLookData>, IStarterLookData
    {
        public MySqlStarterLookData(string connectionString)
                : base(connectionString, "StarterLooks", "StarterLooks")
        {
        }

        // LookID is AUTO_INCREMENT and always excluded from the INSERT so
        // MySQL assigns it - explicitly inserting 0 would just store the
        // literal value 0 rather than triggering autoincrement.
        public override bool Store(StarterLookData row)
        {
            using (MySqlCommand cmd = new MySqlCommand())
            {
                List<string> names = new List<string>();
                List<string> values = new List<string>();

                foreach (FieldInfo fi in m_Fields.Values)
                {
                    if (fi.Name == "LookID")
                        continue;

                    names.Add(fi.Name);
                    values.Add("?" + fi.Name);

                    object value = fi.GetValue(row);
                    if (value is bool b)
                        cmd.Parameters.AddWithValue(fi.Name, b ? 1 : 0);
                    else
                        cmd.Parameters.AddWithValue(fi.Name, value?.ToString() ?? string.Empty);
                }

                cmd.CommandText = String.Format("insert into `{0}` (`", m_Realm) + String.Join("`,`", names.ToArray()) + "`) values (" + String.Join(",", values.ToArray()) + ")";

                return ExecuteNonQuery(cmd) > 0;
            }
        }

        public bool Update(StarterLookData row)
        {
            using (MySqlCommand cmd = new MySqlCommand())
            {
                cmd.CommandText = "UPDATE `StarterLooks` SET `Name` = ?Name, `ModelAccountID` = ?ModelAccountID, `SortOrder` = ?SortOrder, `Enabled` = ?Enabled WHERE `LookID` = ?LookID";
                cmd.Parameters.AddWithValue("Name", row.Name ?? string.Empty);
                cmd.Parameters.AddWithValue("ModelAccountID", row.ModelAccountID.ToString());
                cmd.Parameters.AddWithValue("SortOrder", row.SortOrder);
                cmd.Parameters.AddWithValue("Enabled", row.Enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("LookID", row.LookID);

                return ExecuteNonQuery(cmd) > 0;
            }
        }
    }
}

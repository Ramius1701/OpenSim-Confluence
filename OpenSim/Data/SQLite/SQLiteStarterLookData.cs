using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Reflection;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    public class SQLiteStarterLookData : SQLiteGenericTableHandler<StarterLookData>, IStarterLookData
    {
        public SQLiteStarterLookData(string connectionString)
                : base(connectionString, "StarterLooks", "StarterLooks")
        {
        }

        // LookID is an INTEGER PRIMARY KEY AUTOINCREMENT column and is
        // always excluded from the INSERT so SQLite assigns it.
        public override bool Store(StarterLookData row)
        {
            using (SQLiteCommand cmd = new SQLiteCommand())
            {
                List<string> names = new List<string>();
                List<string> values = new List<string>();

                foreach (FieldInfo fi in m_Fields.Values)
                {
                    if (fi.Name == "LookID")
                        continue;

                    object value = fi.GetValue(row);
                    if (fi.FieldType == typeof(string))
                        value ??= string.Empty;

                    names.Add(fi.Name);
                    values.Add(":" + fi.Name);
                    cmd.Parameters.Add(new SQLiteParameter(":" + fi.Name, value));
                }

                cmd.CommandText = "insert into " + m_Realm + " (`" +
                        String.Join("`,`", names.ToArray()) +
                        "`) values (" + String.Join(",", values.ToArray()) + ")";

                return ExecuteNonQuery(cmd, m_Connection) > 0;
            }
        }

        public bool Update(StarterLookData row)
        {
            using (SQLiteCommand cmd = new SQLiteCommand())
            {
                cmd.CommandText = "update `" + m_Realm + "` set `Name` = :Name, `ModelAccountID` = :ModelAccountID, `SortOrder` = :SortOrder, `Enabled` = :Enabled where `LookID` = :LookID";
                cmd.Parameters.Add(new SQLiteParameter(":Name", row.Name ?? string.Empty));
                cmd.Parameters.Add(new SQLiteParameter(":ModelAccountID", row.ModelAccountID.ToString()));
                cmd.Parameters.Add(new SQLiteParameter(":SortOrder", row.SortOrder));
                cmd.Parameters.Add(new SQLiteParameter(":Enabled", row.Enabled));
                cmd.Parameters.Add(new SQLiteParameter(":LookID", row.LookID));

                return ExecuteNonQuery(cmd, m_Connection) > 0;
            }
        }
    }
}

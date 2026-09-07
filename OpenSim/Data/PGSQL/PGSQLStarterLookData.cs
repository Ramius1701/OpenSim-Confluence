using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLStarterLookData : PGSQLGenericTableHandler<StarterLookData>, IStarterLookData
    {
        public PGSQLStarterLookData(string connectionString)
            : base(connectionString, "StarterLooks", "StarterLooks")
        {
        }

        // LookID is a Postgres "serial" column and is always excluded from
        // the INSERT so the sequence assigns it.
        public override bool Store(StarterLookData row)
        {
            List<string> names = new List<string>();
            List<string> values = new List<string>();

            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                foreach (FieldInfo fi in m_Fields.Values)
                {
                    if (fi.Name == "LookID")
                        continue;

                    object value = fi.GetValue(row);
                    if (fi.FieldType == typeof(string))
                        value ??= string.Empty;

                    names.Add(fi.Name);
                    values.Add(":" + fi.Name);

                    if (m_FieldTypes.TryGetValue(fi.Name, out string ftype))
                        cmd.Parameters.Add(m_database.CreateParameter(fi.Name, value, ftype));
                    else
                        cmd.Parameters.Add(m_database.CreateParameter(fi.Name, value));
                }

                cmd.CommandText = string.Format("INSERT INTO {0} (\"{1}\") VALUES ({2})",
                        m_Realm, string.Join("\",\"", names.ToArray()), string.Join(",", values.ToArray()));

                return ExecuteNonQuery(cmd) > 0;
            }
        }

        public bool Update(StarterLookData row)
        {
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                cmd.CommandText = "UPDATE " + m_Realm + " SET \"Name\" = :Name, \"ModelAccountID\" = :ModelAccountID, \"SortOrder\" = :SortOrder, \"Enabled\" = :Enabled WHERE \"LookID\" = :LookID";
                cmd.Parameters.Add(m_database.CreateParameter("Name", row.Name ?? string.Empty));
                cmd.Parameters.Add(m_database.CreateParameter("ModelAccountID", row.ModelAccountID));
                cmd.Parameters.Add(m_database.CreateParameter("SortOrder", row.SortOrder));
                cmd.Parameters.Add(m_database.CreateParameter("Enabled", row.Enabled));
                cmd.Parameters.Add(m_database.CreateParameter("LookID", row.LookID));

                return ExecuteNonQuery(cmd) > 0;
            }
        }
    }
}

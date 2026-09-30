using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    // Backing store for the admin accountability trail - see
    // OpenSim.Data.IAdminAuditData for the design rationale.
    public class MySqlAdminAuditData : MySqlFramework, IAdminAuditData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        private const string AuditColumns =
                "ID, ActorAccountID, ActorName, Action, TargetType, TargetID, TargetName, OldValue, NewValue, IPAddress, Created";

        public MySqlAdminAuditData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "AdminAudit");
                m.Update();
                dbcon.Close();
            }
        }

        public bool LogAction(AdminAuditEntry entry)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "INSERT INTO admin_audit_log (" + AuditColumns + ") " +
                        "VALUES (?ID, ?ActorAccountID, ?ActorName, ?Action, ?TargetType, ?TargetID, ?TargetName, ?OldValue, ?NewValue, ?IPAddress, ?Created)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", entry.ID.ToString());
                    cmd.Parameters.AddWithValue("?ActorAccountID", entry.ActorAccountID.ToString());
                    cmd.Parameters.AddWithValue("?ActorName", entry.ActorName);
                    cmd.Parameters.AddWithValue("?Action", entry.Action);
                    cmd.Parameters.AddWithValue("?TargetType", entry.TargetType);
                    cmd.Parameters.AddWithValue("?TargetID", entry.TargetID);
                    cmd.Parameters.AddWithValue("?TargetName", entry.TargetName);
                    cmd.Parameters.AddWithValue("?OldValue", entry.OldValue);
                    cmd.Parameters.AddWithValue("?NewValue", entry.NewValue);
                    cmd.Parameters.AddWithValue("?IPAddress", entry.IPAddress);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(entry.Created));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public List<AdminAuditEntry> GetRecent(int count)
        {
            List<AdminAuditEntry> results = new List<AdminAuditEntry>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + AuditColumns + " FROM admin_audit_log ORDER BY Created DESC LIMIT ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 100 : count);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadEntry(reader));
                    }
                }
            }

            return results;
        }

        private static AdminAuditEntry ReadEntry(IDataReader reader)
        {
            return new AdminAuditEntry
            {
                ID = UUID.Parse(reader.GetString(0)),
                ActorAccountID = UUID.Parse(reader.GetString(1)),
                ActorName = reader.GetString(2),
                Action = reader.GetString(3),
                TargetType = reader.GetString(4),
                TargetID = reader.GetString(5),
                TargetName = reader.GetString(6),
                OldValue = reader.GetString(7),
                NewValue = reader.GetString(8),
                IPAddress = reader.GetString(9),
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(10)))
            };
        }
    }
}

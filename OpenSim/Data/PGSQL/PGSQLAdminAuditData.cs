using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    // Backing store for the admin accountability trail - see
    // OpenSim.Data.IAdminAuditData for the design rationale.
    public class PGSQLAdminAuditData : IAdminAuditData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        private const string AuditColumns =
                "\"ID\", \"ActorAccountID\", \"ActorName\", \"Action\", \"TargetType\", \"TargetID\", \"TargetName\", \"OldValue\", \"NewValue\", \"IPAddress\", \"Created\"";

        public PGSQLAdminAuditData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "AdminAudit");
                m.Update();
            }
        }

        public bool LogAction(AdminAuditEntry entry)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO admin_audit_log (" + AuditColumns + ") " +
                    "VALUES (:id, :actoraccountid, :actorname, :action, :targettype, :targetid, :targetname, :oldvalue, :newvalue, :ip, :created)", conn))
            {
                cmd.Parameters.AddWithValue(":id", entry.ID.ToString());
                cmd.Parameters.AddWithValue(":actoraccountid", entry.ActorAccountID.ToString());
                cmd.Parameters.AddWithValue(":actorname", entry.ActorName);
                cmd.Parameters.AddWithValue(":action", entry.Action);
                cmd.Parameters.AddWithValue(":targettype", entry.TargetType);
                cmd.Parameters.AddWithValue(":targetid", entry.TargetID);
                cmd.Parameters.AddWithValue(":targetname", entry.TargetName);
                cmd.Parameters.AddWithValue(":oldvalue", entry.OldValue);
                cmd.Parameters.AddWithValue(":newvalue", entry.NewValue);
                cmd.Parameters.AddWithValue(":ip", entry.IPAddress);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(entry.Created));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public List<AdminAuditEntry> GetRecent(int count)
        {
            List<AdminAuditEntry> results = new List<AdminAuditEntry>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + AuditColumns + " FROM admin_audit_log ORDER BY \"Created\" DESC LIMIT :count", conn))
            {
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 100 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadEntry(reader));
                }
            }

            return results;
        }

        private static AdminAuditEntry ReadEntry(NpgsqlDataReader reader)
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
                Created = Utils.UnixTimeToDateTime((uint)reader.GetInt32(10))
            };
        }
    }
}

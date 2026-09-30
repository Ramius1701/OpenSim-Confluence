using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // Backing store for the admin accountability trail - see
    // OpenSim.Data.IAdminAuditData for the design rationale.
    public class SQLiteAdminAuditData : IAdminAuditData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        private const string AuditColumns =
                "ID, ActorAccountID, ActorName, Action, TargetType, TargetID, TargetName, OldValue, NewValue, IPAddress, Created";

        public SQLiteAdminAuditData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "AdminAudit");
            m.Update();
        }

        public bool LogAction(AdminAuditEntry entry)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT INTO admin_audit_log (" + AuditColumns + ") " +
                        "VALUES (:id, :actoraccountid, :actorname, :action, :targettype, :targetid, :targetname, :oldvalue, :newvalue, :ip, :created)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", entry.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":actoraccountid", entry.ActorAccountID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":actorname", entry.ActorName));
                    cmd.Parameters.Add(new SQLiteParameter(":action", entry.Action));
                    cmd.Parameters.Add(new SQLiteParameter(":targettype", entry.TargetType));
                    cmd.Parameters.Add(new SQLiteParameter(":targetid", entry.TargetID));
                    cmd.Parameters.Add(new SQLiteParameter(":targetname", entry.TargetName));
                    cmd.Parameters.Add(new SQLiteParameter(":oldvalue", entry.OldValue));
                    cmd.Parameters.Add(new SQLiteParameter(":newvalue", entry.NewValue));
                    cmd.Parameters.Add(new SQLiteParameter(":ip", entry.IPAddress));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(entry.Created)));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public List<AdminAuditEntry> GetRecent(int count)
        {
            List<AdminAuditEntry> results = new List<AdminAuditEntry>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + AuditColumns + " FROM admin_audit_log ORDER BY Created DESC LIMIT :count", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 100 : count));

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

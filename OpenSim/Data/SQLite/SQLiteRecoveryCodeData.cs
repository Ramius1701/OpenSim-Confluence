using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // Backing store for the WebInterface's account-recovery codes - see
    // OpenSim.Data.IRecoveryCodeData for the design rationale.
    public class SQLiteRecoveryCodeData : IRecoveryCodeData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteRecoveryCodeData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "RecoveryCode");
            m.Update();
        }

        private const string Columns = "ID, PrincipalID, CodeHash, CodeSalt, Used, Created";

        public List<RecoveryCode> GetByPrincipal(UUID principalID)
        {
            List<RecoveryCode> results = new List<RecoveryCode>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Columns + " FROM recovery_codes WHERE PrincipalID = :principalid", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":principalid", principalID.ToString()));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadItem(reader));
                    }
                }
            }

            return results;
        }

        public bool Store(RecoveryCode code)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO recovery_codes (" + Columns + ") " +
                        "VALUES (:id, :principalid, :hash, :salt, :used, :created)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", code.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":principalid", code.PrincipalID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":hash", code.CodeHash));
                    cmd.Parameters.Add(new SQLiteParameter(":salt", code.CodeSalt));
                    cmd.Parameters.Add(new SQLiteParameter(":used", code.Used));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(code.Created)));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool DeleteAllForPrincipal(UUID principalID)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "DELETE FROM recovery_codes WHERE PrincipalID = :principalid", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":principalid", principalID.ToString()));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool MarkUsed(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "UPDATE recovery_codes SET Used = 1 WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static RecoveryCode ReadItem(IDataReader reader)
        {
            return new RecoveryCode
            {
                ID = UUID.Parse(reader.GetString(0)),
                PrincipalID = UUID.Parse(reader.GetString(1)),
                CodeHash = reader.GetString(2),
                CodeSalt = reader.GetString(3),
                Used = Convert.ToBoolean(reader.GetValue(4)),
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(5)))
            };
        }
    }
}

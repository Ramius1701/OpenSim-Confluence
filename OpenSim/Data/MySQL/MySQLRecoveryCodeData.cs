using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    // Backing store for the WebInterface's account-recovery codes - see
    // OpenSim.Data.IRecoveryCodeData for the design rationale.
    public class MySqlRecoveryCodeData : MySqlFramework, IRecoveryCodeData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlRecoveryCodeData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "RecoveryCode");
                m.Update();
                dbcon.Close();
            }
        }

        private const string Columns = "ID, PrincipalID, CodeHash, CodeSalt, Used, Created";

        public List<RecoveryCode> GetByPrincipal(UUID principalID)
        {
            List<RecoveryCode> results = new List<RecoveryCode>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + Columns + " FROM recovery_codes WHERE PrincipalID = ?PrincipalID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?PrincipalID", principalID.ToString());

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
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO recovery_codes (" + Columns + ") " +
                        "VALUES (?ID, ?PrincipalID, ?CodeHash, ?CodeSalt, ?Used, ?Created)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", code.ID.ToString());
                    cmd.Parameters.AddWithValue("?PrincipalID", code.PrincipalID.ToString());
                    cmd.Parameters.AddWithValue("?CodeHash", code.CodeHash);
                    cmd.Parameters.AddWithValue("?CodeSalt", code.CodeSalt);
                    cmd.Parameters.AddWithValue("?Used", code.Used);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(code.Created));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool DeleteAllForPrincipal(UUID principalID)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "DELETE FROM recovery_codes WHERE PrincipalID = ?PrincipalID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?PrincipalID", principalID.ToString());

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool MarkUsed(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "UPDATE recovery_codes SET Used = 1 WHERE ID = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());

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

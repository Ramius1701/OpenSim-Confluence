using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    // Backing store for the WebInterface's account-recovery codes - see
    // OpenSim.Data.IRecoveryCodeData for the design rationale.
    public class PGSQLRecoveryCodeData : IRecoveryCodeData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLRecoveryCodeData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "RecoveryCode");
                m.Update();
            }
        }

        private const string Columns =
                "\"ID\", \"PrincipalID\", \"CodeHash\", \"CodeSalt\", \"Used\", \"Created\"";

        public List<RecoveryCode> GetByPrincipal(UUID principalID)
        {
            List<RecoveryCode> results = new List<RecoveryCode>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + Columns + " FROM recovery_codes WHERE \"PrincipalID\" = :principalid", conn))
            {
                cmd.Parameters.AddWithValue(":principalid", principalID.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadItem(reader));
                }
            }

            return results;
        }

        public bool Store(RecoveryCode code)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO recovery_codes (" + Columns + ") " +
                    "VALUES (:id, :principalid, :hash, :salt, :used, :created) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET \"Used\" = :used", conn))
            {
                cmd.Parameters.AddWithValue(":id", code.ID.ToString());
                cmd.Parameters.AddWithValue(":principalid", code.PrincipalID.ToString());
                cmd.Parameters.AddWithValue(":hash", code.CodeHash);
                cmd.Parameters.AddWithValue(":salt", code.CodeSalt);
                cmd.Parameters.AddWithValue(":used", code.Used);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(code.Created));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool DeleteAllForPrincipal(UUID principalID)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "DELETE FROM recovery_codes WHERE \"PrincipalID\" = :principalid", conn))
            {
                cmd.Parameters.AddWithValue(":principalid", principalID.ToString());
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool MarkUsed(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "UPDATE recovery_codes SET \"Used\" = true WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static RecoveryCode ReadItem(NpgsqlDataReader reader)
        {
            return new RecoveryCode
            {
                ID = UUID.Parse(reader.GetString(0)),
                PrincipalID = UUID.Parse(reader.GetString(1)),
                CodeHash = reader.GetString(2),
                CodeSalt = reader.GetString(3),
                Used = reader.GetBoolean(4),
                Created = Utils.UnixTimeToDateTime((uint)reader.GetInt32(5))
            };
        }
    }
}

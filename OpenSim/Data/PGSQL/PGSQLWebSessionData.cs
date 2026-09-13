using System;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLWebSessionData : IWebSessionData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLWebSessionData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "WebSession");
                m.Update();
            }
        }

        public WebSessionRecord Get(string token)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"Token\", \"PrincipalID\", \"Name\", \"IsAdmin\", \"WebAccountID\", \"Expires\" " +
                    "FROM web_sessions WHERE \"Token\" = :token", conn))
            {
                cmd.Parameters.AddWithValue(":token", token);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new WebSessionRecord
                    {
                        Token = reader.GetString(0),
                        PrincipalID = UUID.Parse(reader.GetString(1)),
                        Name = reader.GetString(2),
                        IsAdmin = reader.GetBoolean(3),
                        WebAccountID = UUID.Parse(reader.GetString(4)),
                        Expires = reader.GetDateTime(5)
                    };
                }
            }
        }

        public bool Store(WebSessionRecord session)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO web_sessions (\"Token\", \"PrincipalID\", \"Name\", \"IsAdmin\", \"WebAccountID\", \"Expires\") " +
                    "VALUES (:token, :principalId, :name, :isAdmin, :webAccountId, :expires) " +
                    "ON CONFLICT (\"Token\") DO UPDATE SET \"PrincipalID\" = :principalId, \"Name\" = :name, " +
                    "\"IsAdmin\" = :isAdmin, \"WebAccountID\" = :webAccountId, \"Expires\" = :expires", conn))
            {
                cmd.Parameters.AddWithValue(":token", session.Token);
                cmd.Parameters.AddWithValue(":principalId", session.PrincipalID.ToString());
                cmd.Parameters.AddWithValue(":name", session.Name ?? string.Empty);
                cmd.Parameters.AddWithValue(":isAdmin", session.IsAdmin);
                cmd.Parameters.AddWithValue(":webAccountId", session.WebAccountID.ToString());
                cmd.Parameters.AddWithValue(":expires", session.Expires);
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool Delete(string token)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("DELETE FROM web_sessions WHERE \"Token\" = :token", conn))
            {
                cmd.Parameters.AddWithValue(":token", token);
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }
    }
}

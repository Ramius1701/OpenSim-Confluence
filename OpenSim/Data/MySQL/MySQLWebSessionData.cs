using System;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    public class MySqlWebSessionData : MySqlFramework, IWebSessionData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlWebSessionData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "WebSession");
                m.Update();
                dbcon.Close();
            }
        }

        public WebSessionRecord Get(string token)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT Token, PrincipalID, Name, IsAdmin, WebAccountID, Expires FROM web_sessions WHERE Token = ?Token", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Token", token);

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return null;

                        return new WebSessionRecord
                        {
                            Token = reader.GetString(0),
                            PrincipalID = UUID.Parse(reader.GetString(1)),
                            Name = reader.GetString(2),
                            IsAdmin = reader.GetInt32(3) != 0,
                            WebAccountID = UUID.Parse(reader.GetString(4)),
                            Expires = reader.GetDateTime(5)
                        };
                    }
                }
            }
        }

        public bool Store(WebSessionRecord session)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO web_sessions (Token, PrincipalID, Name, IsAdmin, WebAccountID, Expires) " +
                        "VALUES (?Token, ?PrincipalID, ?Name, ?IsAdmin, ?WebAccountID, ?Expires)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Token", session.Token);
                    cmd.Parameters.AddWithValue("?PrincipalID", session.PrincipalID.ToString());
                    cmd.Parameters.AddWithValue("?Name", session.Name ?? string.Empty);
                    cmd.Parameters.AddWithValue("?IsAdmin", session.IsAdmin ? 1 : 0);
                    cmd.Parameters.AddWithValue("?WebAccountID", session.WebAccountID.ToString());
                    cmd.Parameters.AddWithValue("?Expires", session.Expires);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool Delete(string token)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand("DELETE FROM web_sessions WHERE Token = ?Token", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Token", token);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }
    }
}

using System;
using System.Reflection;
using System.Data.SQLite;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    public class SQLiteWebSessionData : IWebSessionData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteWebSessionData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "WebSession");
            m.Update();
        }

        public WebSessionRecord Get(string token)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT Token, PrincipalID, Name, IsAdmin, WebAccountID, Expires FROM web_sessions WHERE Token = :token", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":token", token));

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return null;

                        return new WebSessionRecord
                        {
                            Token = reader.GetString(0),
                            PrincipalID = UUID.Parse(reader.GetString(1)),
                            Name = reader.GetString(2),
                            IsAdmin = reader.GetInt64(3) != 0,
                            WebAccountID = UUID.Parse(reader.GetString(4)),
                            Expires = DateTime.Parse(reader.GetString(5))
                        };
                    }
                }
            }
        }

        public bool Store(WebSessionRecord session)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO web_sessions (Token, PrincipalID, Name, IsAdmin, WebAccountID, Expires) " +
                        "VALUES (:token, :principalId, :name, :isAdmin, :webAccountId, :expires)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":token", session.Token));
                    cmd.Parameters.Add(new SQLiteParameter(":principalId", session.PrincipalID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":name", session.Name ?? string.Empty));
                    cmd.Parameters.Add(new SQLiteParameter(":isAdmin", session.IsAdmin ? 1 : 0));
                    cmd.Parameters.Add(new SQLiteParameter(":webAccountId", session.WebAccountID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":expires", session.Expires.ToString("o")));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool Delete(string token)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("DELETE FROM web_sessions WHERE Token = :token", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":token", token));
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }
    }
}

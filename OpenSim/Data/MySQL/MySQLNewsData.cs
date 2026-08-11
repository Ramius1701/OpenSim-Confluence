using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    // Backing store for the WebInterface login-screen/home-page news feed -
    // see OpenSim.Data.INewsData for the design rationale.
    public class MySqlNewsData : MySqlFramework, INewsData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlNewsData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "News");
                m.Update();
                dbcon.Close();
            }
        }

        public NewsItem Get(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, Title, Body, Author, Created FROM news WHERE ID = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadItem(reader);
                    }
                }
            }

            return null;
        }

        public List<NewsItem> GetNews(int start, int count)
        {
            List<NewsItem> results = new List<NewsItem>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, Title, Body, Author, Created FROM news ORDER BY Created DESC LIMIT ?start, ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?start", start < 0 ? 0 : start);
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 20 : count);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadItem(reader));
                    }
                }
            }

            return results;
        }

        public bool Store(NewsItem item)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO news (ID, Title, Body, Author, Created) VALUES (?ID, ?Title, ?Body, ?Author, ?Created)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", item.ID.ToString());
                    cmd.Parameters.AddWithValue("?Title", item.Title);
                    cmd.Parameters.AddWithValue("?Body", item.Body);
                    cmd.Parameters.AddWithValue("?Author", item.Author);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(item.Date));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool Delete(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand("DELETE FROM news WHERE ID = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static NewsItem ReadItem(IDataReader reader)
        {
            return new NewsItem
            {
                ID = UUID.Parse(reader.GetString(0)),
                Title = reader.GetString(1),
                Body = reader.GetString(2),
                Author = reader.GetString(3),
                Date = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(4)))
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLNewsData : INewsData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLNewsData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "News");
                m.Update();
            }
        }

        public NewsItem Get(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"ID\", \"Title\", \"Body\", \"Author\", \"Created\" FROM news WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return ReadItem(reader);
                }
            }

            return null;
        }

        public List<NewsItem> GetNews(int start, int count)
        {
            List<NewsItem> results = new List<NewsItem>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"ID\", \"Title\", \"Body\", \"Author\", \"Created\" FROM news ORDER BY \"Created\" DESC LIMIT :count OFFSET :start", conn))
            {
                cmd.Parameters.AddWithValue(":start", start < 0 ? 0 : start);
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 20 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadItem(reader));
                }
            }

            return results;
        }

        public bool Store(NewsItem item)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO news (\"ID\", \"Title\", \"Body\", \"Author\", \"Created\") VALUES (:id, :title, :body, :author, :created) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET \"Title\" = :title, \"Body\" = :body, \"Author\" = :author, \"Created\" = :created", conn))
            {
                cmd.Parameters.AddWithValue(":id", item.ID.ToString());
                cmd.Parameters.AddWithValue(":title", item.Title);
                cmd.Parameters.AddWithValue(":body", item.Body);
                cmd.Parameters.AddWithValue(":author", item.Author);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(item.Date));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool Delete(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("DELETE FROM news WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static NewsItem ReadItem(NpgsqlDataReader reader)
        {
            return new NewsItem
            {
                ID = UUID.Parse(reader.GetString(0)),
                Title = reader.GetString(1),
                Body = reader.GetString(2),
                Author = reader.GetString(3),
                Date = Utils.UnixTimeToDateTime((uint)reader.GetInt32(4))
            };
        }
    }
}

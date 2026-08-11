using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    public class SQLiteNewsData : INewsData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteNewsData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "News");
            m.Update();
        }

        public NewsItem Get(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT ID, Title, Body, Author, Created FROM news WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));

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

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT ID, Title, Body, Author, Created FROM news ORDER BY Created DESC LIMIT :count OFFSET :start", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":start", start < 0 ? 0 : start));
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 20 : count));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO news (ID, Title, Body, Author, Created) VALUES (:id, :title, :body, :author, :created)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", item.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":title", item.Title));
                    cmd.Parameters.Add(new SQLiteParameter(":body", item.Body));
                    cmd.Parameters.Add(new SQLiteParameter(":author", item.Author));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(item.Date)));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool Delete(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("DELETE FROM news WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));
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
                Date = Utils.UnixTimeToDateTime(System.Convert.ToUInt32(reader.GetValue(4)))
            };
        }
    }
}

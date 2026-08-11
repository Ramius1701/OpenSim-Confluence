using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    public class SQLiteStaticPageData : IStaticPageData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteStaticPageData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "StaticPage");
            m.Update();
        }

        public StaticPage Get(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT ID, Slug, Title, Body, Updated FROM static_pages WHERE ID = :id", m_conn))
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

        public StaticPage GetBySlug(string slug)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT ID, Slug, Title, Body, Updated FROM static_pages WHERE Slug = :slug", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":slug", slug));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadItem(reader);
                    }
                }
            }

            return null;
        }

        public List<StaticPage> GetAll()
        {
            List<StaticPage> results = new List<StaticPage>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT ID, Slug, Title, Body, Updated FROM static_pages ORDER BY Slug ASC", m_conn))
                {
                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadItem(reader));
                    }
                }
            }

            return results;
        }

        public bool Store(StaticPage page)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO static_pages (ID, Slug, Title, Body, Updated) VALUES (:id, :slug, :title, :body, :updated)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", page.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":slug", page.Slug));
                    cmd.Parameters.Add(new SQLiteParameter(":title", page.Title));
                    cmd.Parameters.Add(new SQLiteParameter(":body", page.Body));
                    cmd.Parameters.Add(new SQLiteParameter(":updated", Utils.DateTimeToUnixTime(page.Updated)));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool Delete(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("DELETE FROM static_pages WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static StaticPage ReadItem(IDataReader reader)
        {
            return new StaticPage
            {
                ID = UUID.Parse(reader.GetString(0)),
                Slug = reader.GetString(1),
                Title = reader.GetString(2),
                Body = reader.GetString(3),
                Updated = Utils.UnixTimeToDateTime(System.Convert.ToUInt32(reader.GetValue(4)))
            };
        }
    }
}

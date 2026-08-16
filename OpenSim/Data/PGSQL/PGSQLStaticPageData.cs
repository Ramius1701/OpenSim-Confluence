using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLStaticPageData : IStaticPageData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLStaticPageData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "StaticPage");
                m.Update();
            }
        }

        public StaticPage Get(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"ID\", \"Slug\", \"Title\", \"Body\", \"Updated\", \"ShowInNav\", \"NavOrder\", \"RequiresLogin\", \"RequiresAdmin\" FROM static_pages WHERE \"ID\" = :id", conn))
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

        public StaticPage GetBySlug(string slug)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"ID\", \"Slug\", \"Title\", \"Body\", \"Updated\", \"ShowInNav\", \"NavOrder\", \"RequiresLogin\", \"RequiresAdmin\" FROM static_pages WHERE \"Slug\" = :slug", conn))
            {
                cmd.Parameters.AddWithValue(":slug", slug);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return ReadItem(reader);
                }
            }

            return null;
        }

        public List<StaticPage> GetAll()
        {
            List<StaticPage> results = new List<StaticPage>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"ID\", \"Slug\", \"Title\", \"Body\", \"Updated\", \"ShowInNav\", \"NavOrder\", \"RequiresLogin\", \"RequiresAdmin\" FROM static_pages ORDER BY \"Slug\" ASC", conn))
            {
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadItem(reader));
                }
            }

            return results;
        }

        public bool Store(StaticPage page)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO static_pages (\"ID\", \"Slug\", \"Title\", \"Body\", \"Updated\", \"ShowInNav\", \"NavOrder\", \"RequiresLogin\", \"RequiresAdmin\") " +
                    "VALUES (:id, :slug, :title, :body, :updated, :shownav, :navorder, :reqlogin, :reqadmin) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET \"Slug\" = :slug, \"Title\" = :title, \"Body\" = :body, \"Updated\" = :updated, " +
                    "\"ShowInNav\" = :shownav, \"NavOrder\" = :navorder, \"RequiresLogin\" = :reqlogin, \"RequiresAdmin\" = :reqadmin", conn))
            {
                cmd.Parameters.AddWithValue(":id", page.ID.ToString());
                cmd.Parameters.AddWithValue(":slug", page.Slug);
                cmd.Parameters.AddWithValue(":title", page.Title);
                cmd.Parameters.AddWithValue(":body", page.Body);
                cmd.Parameters.AddWithValue(":updated", (int)Utils.DateTimeToUnixTime(page.Updated));
                cmd.Parameters.AddWithValue(":shownav", (short)(page.ShowInNav ? 1 : 0));
                cmd.Parameters.AddWithValue(":navorder", page.NavOrder);
                cmd.Parameters.AddWithValue(":reqlogin", (short)(page.RequiresLogin ? 1 : 0));
                cmd.Parameters.AddWithValue(":reqadmin", (short)(page.RequiresAdmin ? 1 : 0));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool Delete(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("DELETE FROM static_pages WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static StaticPage ReadItem(NpgsqlDataReader reader)
        {
            return new StaticPage
            {
                ID = UUID.Parse(reader.GetString(0)),
                Slug = reader.GetString(1),
                Title = reader.GetString(2),
                Body = reader.GetString(3),
                Updated = Utils.UnixTimeToDateTime((uint)reader.GetInt32(4)),
                ShowInNav = reader.GetInt16(5) != 0,
                NavOrder = reader.GetInt32(6),
                RequiresLogin = reader.GetInt16(7) != 0,
                RequiresAdmin = reader.GetInt16(8) != 0
            };
        }
    }
}

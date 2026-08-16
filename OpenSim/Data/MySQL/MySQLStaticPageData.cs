using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    public class MySqlStaticPageData : MySqlFramework, IStaticPageData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlStaticPageData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "StaticPage");
                m.Update();
                dbcon.Close();
            }
        }

        public StaticPage Get(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, Slug, Title, Body, Updated, ShowInNav, NavOrder, RequiresLogin, RequiresAdmin FROM static_pages WHERE ID = ?ID", dbcon))
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

        public StaticPage GetBySlug(string slug)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, Slug, Title, Body, Updated, ShowInNav, NavOrder, RequiresLogin, RequiresAdmin FROM static_pages WHERE Slug = ?Slug", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Slug", slug);

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

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, Slug, Title, Body, Updated, ShowInNav, NavOrder, RequiresLogin, RequiresAdmin FROM static_pages ORDER BY Slug ASC", dbcon))
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
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO static_pages (ID, Slug, Title, Body, Updated, ShowInNav, NavOrder, RequiresLogin, RequiresAdmin) " +
                        "VALUES (?ID, ?Slug, ?Title, ?Body, ?Updated, ?ShowInNav, ?NavOrder, ?RequiresLogin, ?RequiresAdmin)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", page.ID.ToString());
                    cmd.Parameters.AddWithValue("?Slug", page.Slug);
                    cmd.Parameters.AddWithValue("?Title", page.Title);
                    cmd.Parameters.AddWithValue("?Body", page.Body);
                    cmd.Parameters.AddWithValue("?Updated", Utils.DateTimeToUnixTime(page.Updated));
                    cmd.Parameters.AddWithValue("?ShowInNav", page.ShowInNav);
                    cmd.Parameters.AddWithValue("?NavOrder", page.NavOrder);
                    cmd.Parameters.AddWithValue("?RequiresLogin", page.RequiresLogin);
                    cmd.Parameters.AddWithValue("?RequiresAdmin", page.RequiresAdmin);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool Delete(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand("DELETE FROM static_pages WHERE ID = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());
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
                Updated = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(4))),
                ShowInNav = Convert.ToBoolean(reader.GetValue(5)),
                NavOrder = Convert.ToInt32(reader.GetValue(6)),
                RequiresLogin = Convert.ToBoolean(reader.GetValue(7)),
                RequiresAdmin = Convert.ToBoolean(reader.GetValue(8))
            };
        }
    }
}

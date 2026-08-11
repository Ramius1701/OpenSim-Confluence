using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    // Backing store for the WebInterface splash page's "Upcoming Events"
    // widget - see OpenSim.Data.IEventsData for the design rationale.
    public class MySqlEventsData : MySqlFramework, IEventsData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlEventsData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "Events");
                m.Update();
                dbcon.Close();
            }
        }

        public EventItem Get(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, Title, Category, Description, EventDate, DurationMinutes, Location, CreatorId FROM events WHERE ID = ?ID", dbcon))
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

        public List<EventItem> GetUpcoming(int start, int count)
        {
            List<EventItem> results = new List<EventItem>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, Title, Category, Description, EventDate, DurationMinutes, Location, CreatorId FROM events " +
                        "WHERE EventDate >= ?Now ORDER BY EventDate ASC LIMIT ?start, ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Now", Util.UnixTimeSinceEpoch());
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

        // Grid-wide keyword search for /web/search - same LIKE shape as
        // MySqlSearchData.SearchPlaces, restricted to upcoming events same
        // as GetUpcoming above (a past event isn't useful to surface here).
        public List<EventItem> SearchEvents(string queryText, int start, int count)
        {
            List<EventItem> results = new List<EventItem>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, Title, Category, Description, EventDate, DurationMinutes, Location, CreatorId FROM events " +
                        "WHERE EventDate >= ?Now AND (Title LIKE ?query OR Description LIKE ?query OR Location LIKE ?query) " +
                        "ORDER BY EventDate ASC LIMIT ?start, ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Now", Util.UnixTimeSinceEpoch());
                    cmd.Parameters.AddWithValue("?query", "%" + (queryText ?? string.Empty) + "%");
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

        public bool Store(EventItem item)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO events (ID, Title, Category, Description, EventDate, DurationMinutes, Location, CreatorId) " +
                        "VALUES (?ID, ?Title, ?Category, ?Description, ?EventDate, ?DurationMinutes, ?Location, ?CreatorId)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", item.ID.ToString());
                    cmd.Parameters.AddWithValue("?Title", item.Title);
                    cmd.Parameters.AddWithValue("?Category", item.Category);
                    cmd.Parameters.AddWithValue("?Description", item.Description);
                    cmd.Parameters.AddWithValue("?EventDate", Utils.DateTimeToUnixTime(item.EventDate));
                    cmd.Parameters.AddWithValue("?DurationMinutes", item.DurationMinutes);
                    cmd.Parameters.AddWithValue("?Location", item.Location);
                    cmd.Parameters.AddWithValue("?CreatorId", item.CreatorId.ToString());

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool Delete(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand("DELETE FROM events WHERE ID = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static EventItem ReadItem(IDataReader reader)
        {
            return new EventItem
            {
                ID = UUID.Parse(reader.GetString(0)),
                Title = reader.GetString(1),
                Category = reader.GetString(2),
                Description = reader.GetString(3),
                EventDate = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(4))),
                DurationMinutes = reader.GetInt32(5),
                Location = reader.GetString(6),
                CreatorId = UUID.Parse(reader.GetString(7))
            };
        }
    }
}

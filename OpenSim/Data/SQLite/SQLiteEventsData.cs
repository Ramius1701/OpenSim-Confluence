using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // Backing store for the WebInterface splash page's "Upcoming Events"
    // widget - see OpenSim.Data.IEventsData for the design rationale.
    public class SQLiteEventsData : IEventsData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteEventsData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "Events");
            m.Update();
        }

        public EventItem Get(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT ID, Title, Category, Description, EventDate, DurationMinutes, Location, CreatorId FROM events WHERE ID = :id", m_conn))
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

        public List<EventItem> GetUpcoming(int start, int count)
        {
            List<EventItem> results = new List<EventItem>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT ID, Title, Category, Description, EventDate, DurationMinutes, Location, CreatorId FROM events " +
                        "WHERE EventDate >= :now ORDER BY EventDate ASC LIMIT :count OFFSET :start", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":now", Utils.DateTimeToUnixTime(DateTime.UtcNow)));
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

        // Grid-wide keyword search for /web/search - same LIKE shape as
        // SQLiteSearchData.SearchPlaces, restricted to upcoming events same
        // as GetUpcoming above.
        public List<EventItem> SearchEvents(string queryText, int start, int count)
        {
            List<EventItem> results = new List<EventItem>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT ID, Title, Category, Description, EventDate, DurationMinutes, Location, CreatorId FROM events " +
                        "WHERE EventDate >= :now AND (Title LIKE :query OR Description LIKE :query OR Location LIKE :query) " +
                        "ORDER BY EventDate ASC LIMIT :count OFFSET :start", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":now", Utils.DateTimeToUnixTime(DateTime.UtcNow)));
                    cmd.Parameters.Add(new SQLiteParameter(":query", "%" + (queryText ?? string.Empty) + "%"));
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

        public bool Store(EventItem item)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO events (ID, Title, Category, Description, EventDate, DurationMinutes, Location, CreatorId) " +
                        "VALUES (:id, :title, :category, :description, :eventdate, :duration, :location, :creatorid)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", item.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":title", item.Title));
                    cmd.Parameters.Add(new SQLiteParameter(":category", item.Category));
                    cmd.Parameters.Add(new SQLiteParameter(":description", item.Description));
                    cmd.Parameters.Add(new SQLiteParameter(":eventdate", Utils.DateTimeToUnixTime(item.EventDate)));
                    cmd.Parameters.Add(new SQLiteParameter(":duration", item.DurationMinutes));
                    cmd.Parameters.Add(new SQLiteParameter(":location", item.Location));
                    cmd.Parameters.Add(new SQLiteParameter(":creatorid", item.CreatorId.ToString()));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool Delete(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("DELETE FROM events WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));
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

using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    // Backing store for the WebInterface splash page's "Upcoming Events"
    // widget - see OpenSim.Data.IEventsData for the design rationale.
    public class PGSQLEventsData : IEventsData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLEventsData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "Events");
                m.Update();
            }
        }

        public EventItem Get(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"ID\", \"Title\", \"Category\", \"Description\", \"EventDate\", \"DurationMinutes\", \"Location\", \"CreatorId\", \"GlobalPos\" FROM events WHERE \"ID\" = :id", conn))
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

        public List<EventItem> GetUpcoming(int start, int count)
        {
            List<EventItem> results = new List<EventItem>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"ID\", \"Title\", \"Category\", \"Description\", \"EventDate\", \"DurationMinutes\", \"Location\", \"CreatorId\", \"GlobalPos\" FROM events " +
                    "WHERE \"EventDate\" >= :now ORDER BY \"EventDate\" ASC LIMIT :count OFFSET :start", conn))
            {
                cmd.Parameters.AddWithValue(":now", (int)Utils.DateTimeToUnixTime(DateTime.UtcNow));
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

        // Grid-wide keyword search for /web/search - same ILIKE shape as
        // PGSQLSearchData.SearchPlaces, restricted to upcoming events same
        // as GetUpcoming above.
        public List<EventItem> SearchEvents(string queryText, int start, int count)
        {
            List<EventItem> results = new List<EventItem>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"ID\", \"Title\", \"Category\", \"Description\", \"EventDate\", \"DurationMinutes\", \"Location\", \"CreatorId\", \"GlobalPos\" FROM events " +
                    "WHERE \"EventDate\" >= :now AND (\"Title\" ILIKE :query OR \"Description\" ILIKE :query OR \"Location\" ILIKE :query) " +
                    "ORDER BY \"EventDate\" ASC LIMIT :count OFFSET :start", conn))
            {
                cmd.Parameters.AddWithValue(":now", (int)Utils.DateTimeToUnixTime(DateTime.UtcNow));
                cmd.Parameters.AddWithValue(":query", "%" + (queryText ?? string.Empty) + "%");
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

        // Single-day version of SearchEvents, for the viewer Events tab's
        // Date mode (Today/Yesterday/Tomorrow arrows) - dayStartUnix/
        // dayEndUnix are a Pacific-time day boundary computed by the caller
        // (ConfluenceSearchModule), matching Firestorm's own
        // FSPanelSearchEvents::setDay day-offset math exactly rather than
        // guessing a UTC-day boundary instead.
        public List<EventItem> SearchEventsByDay(string queryText, int dayStartUnix, int dayEndUnix, int start, int count)
        {
            List<EventItem> results = new List<EventItem>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"ID\", \"Title\", \"Category\", \"Description\", \"EventDate\", \"DurationMinutes\", \"Location\", \"CreatorId\", \"GlobalPos\" FROM events " +
                    "WHERE \"EventDate\" >= :dayStart AND \"EventDate\" < :dayEnd AND (\"Title\" ILIKE :query OR \"Description\" ILIKE :query OR \"Location\" ILIKE :query) " +
                    "ORDER BY \"EventDate\" ASC LIMIT :count OFFSET :start", conn))
            {
                cmd.Parameters.AddWithValue(":dayStart", dayStartUnix);
                cmd.Parameters.AddWithValue(":dayEnd", dayEndUnix);
                cmd.Parameters.AddWithValue(":query", "%" + (queryText ?? string.Empty) + "%");
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

        public bool Store(EventItem item)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO events (\"ID\", \"Title\", \"Category\", \"Description\", \"EventDate\", \"DurationMinutes\", \"Location\", \"CreatorId\", \"GlobalPos\") " +
                    "VALUES (:id, :title, :category, :description, :eventdate, :duration, :location, :creatorid, :globalpos) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET \"Title\" = :title, \"Category\" = :category, \"Description\" = :description, " +
                    "\"EventDate\" = :eventdate, \"DurationMinutes\" = :duration, \"Location\" = :location, \"CreatorId\" = :creatorid, \"GlobalPos\" = :globalpos", conn))
            {
                cmd.Parameters.AddWithValue(":id", item.ID.ToString());
                cmd.Parameters.AddWithValue(":title", item.Title);
                cmd.Parameters.AddWithValue(":category", item.Category);
                cmd.Parameters.AddWithValue(":description", item.Description);
                cmd.Parameters.AddWithValue(":eventdate", (int)Utils.DateTimeToUnixTime(item.EventDate));
                cmd.Parameters.AddWithValue(":duration", item.DurationMinutes);
                cmd.Parameters.AddWithValue(":location", item.Location);
                cmd.Parameters.AddWithValue(":creatorid", item.CreatorId.ToString());
                cmd.Parameters.AddWithValue(":globalpos", item.GlobalPos ?? string.Empty);
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool Delete(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("DELETE FROM events WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        // Real end time (start + duration), not just start time - an event
        // still in progress must never be swept away mid-event.
        public int DeleteExpired(int nowUnix)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "DELETE FROM events WHERE (\"EventDate\" + \"DurationMinutes\" * 60) < :now", conn))
            {
                cmd.Parameters.AddWithValue(":now", nowUnix);
                conn.Open();

                return cmd.ExecuteNonQuery();
            }
        }

        private static EventItem ReadItem(NpgsqlDataReader reader)
        {
            return new EventItem
            {
                ID = UUID.Parse(reader.GetString(0)),
                Title = reader.GetString(1),
                Category = reader.GetString(2),
                Description = reader.GetString(3),
                EventDate = Utils.UnixTimeToDateTime((uint)reader.GetInt32(4)),
                DurationMinutes = reader.GetInt32(5),
                Location = reader.GetString(6),
                CreatorId = UUID.Parse(reader.GetString(7)),
                GlobalPos = reader.GetString(8)
            };
        }
    }
}

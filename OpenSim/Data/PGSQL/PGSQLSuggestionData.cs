using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    // Backing store for the WebInterface Suggestion Box - see
    // OpenSim.Data.ISuggestionData for the design rationale.
    public class PGSQLSuggestionData : ISuggestionData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLSuggestionData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "Suggestions");
                m.Update();
            }
        }

        private const string Columns =
                "\"ID\", \"SubmitterAvatarID\", \"SubmitterName\", \"Subject\", \"Message\", \"Status\", \"Created\"";

        public Suggestion Get(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + Columns + " FROM suggestions WHERE \"ID\" = :id", conn))
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

        public List<Suggestion> GetByUser(UUID userId, int start, int count)
        {
            List<Suggestion> results = new List<Suggestion>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + Columns + " FROM suggestions WHERE \"SubmitterAvatarID\" = :userid " +
                    "ORDER BY \"Created\" DESC LIMIT :count OFFSET :start", conn))
            {
                cmd.Parameters.AddWithValue(":userid", userId.ToString());
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

        public List<Suggestion> GetAll(int start, int count)
        {
            List<Suggestion> results = new List<Suggestion>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + Columns + " FROM suggestions " +
                    "ORDER BY (\"Status\" = 'closed'), \"Created\" DESC LIMIT :count OFFSET :start", conn))
            {
                cmd.Parameters.AddWithValue(":start", start < 0 ? 0 : start);
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 50 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadItem(reader));
                }
            }

            return results;
        }

        public bool Store(Suggestion suggestion)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO suggestions (" + Columns + ") " +
                    "VALUES (:id, :avatarid, :name, :subject, :message, :status, :created) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET \"Status\" = :status", conn))
            {
                cmd.Parameters.AddWithValue(":id", suggestion.ID.ToString());
                cmd.Parameters.AddWithValue(":avatarid", suggestion.SubmitterAvatarID.ToString());
                cmd.Parameters.AddWithValue(":name", suggestion.SubmitterName);
                cmd.Parameters.AddWithValue(":subject", suggestion.Subject);
                cmd.Parameters.AddWithValue(":message", suggestion.Message);
                cmd.Parameters.AddWithValue(":status", suggestion.Status);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(suggestion.Created));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static Suggestion ReadItem(NpgsqlDataReader reader)
        {
            return new Suggestion
            {
                ID = UUID.Parse(reader.GetString(0)),
                SubmitterAvatarID = UUID.Parse(reader.GetString(1)),
                SubmitterName = reader.GetString(2),
                Subject = reader.GetString(3),
                Message = reader.GetString(4),
                Status = reader.GetString(5),
                Created = Utils.UnixTimeToDateTime((uint)reader.GetInt32(6))
            };
        }
    }
}

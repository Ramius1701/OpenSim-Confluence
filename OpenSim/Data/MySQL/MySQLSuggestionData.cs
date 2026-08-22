using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    // Backing store for the WebInterface Suggestion Box - see
    // OpenSim.Data.ISuggestionData for the design rationale.
    public class MySqlSuggestionData : MySqlFramework, ISuggestionData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlSuggestionData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "Suggestions");
                m.Update();
                dbcon.Close();
            }
        }

        private const string Columns =
                "ID, SubmitterAvatarID, SubmitterName, Subject, Message, Status, Created";

        public Suggestion Get(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + Columns + " FROM suggestions WHERE ID = ?ID", dbcon))
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

        public List<Suggestion> GetByUser(UUID userId, int start, int count)
        {
            List<Suggestion> results = new List<Suggestion>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + Columns + " FROM suggestions WHERE SubmitterAvatarID = ?SubmitterAvatarID " +
                        "ORDER BY Created DESC LIMIT ?start, ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?SubmitterAvatarID", userId.ToString());
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

        public List<Suggestion> GetAll(int start, int count)
        {
            List<Suggestion> results = new List<Suggestion>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + Columns + " FROM suggestions " +
                        "ORDER BY (Status = 'closed'), Created DESC LIMIT ?start, ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?start", start < 0 ? 0 : start);
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 50 : count);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadItem(reader));
                    }
                }
            }

            return results;
        }

        public bool Store(Suggestion suggestion)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO suggestions (" + Columns + ") " +
                        "VALUES (?ID, ?SubmitterAvatarID, ?SubmitterName, ?Subject, ?Message, ?Status, ?Created)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", suggestion.ID.ToString());
                    cmd.Parameters.AddWithValue("?SubmitterAvatarID", suggestion.SubmitterAvatarID.ToString());
                    cmd.Parameters.AddWithValue("?SubmitterName", suggestion.SubmitterName);
                    cmd.Parameters.AddWithValue("?Subject", suggestion.Subject);
                    cmd.Parameters.AddWithValue("?Message", suggestion.Message);
                    cmd.Parameters.AddWithValue("?Status", suggestion.Status);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(suggestion.Created));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static Suggestion ReadItem(IDataReader reader)
        {
            return new Suggestion
            {
                ID = UUID.Parse(reader.GetString(0)),
                SubmitterAvatarID = UUID.Parse(reader.GetString(1)),
                SubmitterName = reader.GetString(2),
                Subject = reader.GetString(3),
                Message = reader.GetString(4),
                Status = reader.GetString(5),
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(6)))
            };
        }
    }
}

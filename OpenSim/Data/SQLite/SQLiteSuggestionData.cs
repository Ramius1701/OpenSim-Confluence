using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // Backing store for the WebInterface Suggestion Box - see
    // OpenSim.Data.ISuggestionData for the design rationale.
    public class SQLiteSuggestionData : ISuggestionData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteSuggestionData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "Suggestions");
            m.Update();
        }

        private const string Columns =
                "ID, SubmitterAvatarID, SubmitterName, Subject, Message, Status, Created";

        public Suggestion Get(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Columns + " FROM suggestions WHERE ID = :id", m_conn))
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

        public List<Suggestion> GetByUser(UUID userId, int start, int count)
        {
            List<Suggestion> results = new List<Suggestion>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Columns + " FROM suggestions WHERE SubmitterAvatarID = :userid " +
                        "ORDER BY Created DESC LIMIT :count OFFSET :start", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":userid", userId.ToString()));
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

        public List<Suggestion> GetAll(int start, int count)
        {
            List<Suggestion> results = new List<Suggestion>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Columns + " FROM suggestions " +
                        "ORDER BY (Status = 'closed'), Created DESC LIMIT :count OFFSET :start", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":start", start < 0 ? 0 : start));
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 50 : count));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO suggestions (" + Columns + ") " +
                        "VALUES (:id, :avatarid, :name, :subject, :message, :status, :created)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", suggestion.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":avatarid", suggestion.SubmitterAvatarID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":name", suggestion.SubmitterName));
                    cmd.Parameters.Add(new SQLiteParameter(":subject", suggestion.Subject));
                    cmd.Parameters.Add(new SQLiteParameter(":message", suggestion.Message));
                    cmd.Parameters.Add(new SQLiteParameter(":status", suggestion.Status));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(suggestion.Created)));

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

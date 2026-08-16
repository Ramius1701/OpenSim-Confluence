using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    // Backing store for the WebInterface resident-to-resident web mail
    // (inbox/sent/compose) - see OpenSim.Data.IMessagingData for the
    // design rationale.
    public class PGSQLMessagingData : IMessagingData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLMessagingData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "WebMessages");
                m.Update();
            }
        }

        private const string Columns =
                "\"ID\", \"SenderID\", \"ReceiverID\", \"Subject\", \"Body\", \"Created\", \"IsRead\", \"SenderDeleted\", \"ReceiverDeleted\"";

        public WebMessage Get(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + Columns + " FROM webmessages WHERE \"ID\" = :id", conn))
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

        public List<WebMessage> GetInbox(UUID userID, int count)
        {
            List<WebMessage> results = new List<WebMessage>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + Columns + " FROM webmessages WHERE \"ReceiverID\" = :userid AND \"ReceiverDeleted\" = 0 " +
                    "ORDER BY \"Created\" DESC LIMIT :count", conn))
            {
                cmd.Parameters.AddWithValue(":userid", userID.ToString());
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 200 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadItem(reader));
                }
            }

            return results;
        }

        public List<WebMessage> GetSent(UUID userID, int count)
        {
            List<WebMessage> results = new List<WebMessage>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + Columns + " FROM webmessages WHERE \"SenderID\" = :userid AND \"SenderDeleted\" = 0 " +
                    "ORDER BY \"Created\" DESC LIMIT :count", conn))
            {
                cmd.Parameters.AddWithValue(":userid", userID.ToString());
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 200 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadItem(reader));
                }
            }

            return results;
        }

        public bool Store(WebMessage message)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO webmessages (" + Columns + ") " +
                    "VALUES (:id, :senderid, :receiverid, :subject, :body, :created, :isread, :senderdeleted, :receiverdeleted) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET \"Subject\" = :subject, \"Body\" = :body, \"IsRead\" = :isread, " +
                    "\"SenderDeleted\" = :senderdeleted, \"ReceiverDeleted\" = :receiverdeleted", conn))
            {
                cmd.Parameters.AddWithValue(":id", message.ID.ToString());
                cmd.Parameters.AddWithValue(":senderid", message.SenderID.ToString());
                cmd.Parameters.AddWithValue(":receiverid", message.ReceiverID.ToString());
                cmd.Parameters.AddWithValue(":subject", message.Subject);
                cmd.Parameters.AddWithValue(":body", message.Body);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(message.Created));
                cmd.Parameters.AddWithValue(":isread", message.IsRead ? 1 : 0);
                cmd.Parameters.AddWithValue(":senderdeleted", message.SenderDeleted ? 1 : 0);
                cmd.Parameters.AddWithValue(":receiverdeleted", message.ReceiverDeleted ? 1 : 0);
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool MarkRead(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("UPDATE webmessages SET \"IsRead\" = 1 WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool DeleteForUser(UUID id, UUID userID)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "UPDATE webmessages SET " +
                    "\"SenderDeleted\" = CASE WHEN \"SenderID\" = :userid THEN 1 ELSE \"SenderDeleted\" END, " +
                    "\"ReceiverDeleted\" = CASE WHEN \"ReceiverID\" = :userid THEN 1 ELSE \"ReceiverDeleted\" END " +
                    "WHERE \"ID\" = :id AND (\"SenderID\" = :userid OR \"ReceiverID\" = :userid)", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                cmd.Parameters.AddWithValue(":userid", userID.ToString());
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static WebMessage ReadItem(NpgsqlDataReader reader)
        {
            return new WebMessage
            {
                ID = UUID.Parse(reader.GetString(0)),
                SenderID = UUID.Parse(reader.GetString(1)),
                ReceiverID = UUID.Parse(reader.GetString(2)),
                Subject = reader.GetString(3),
                Body = reader.GetString(4),
                Created = Utils.UnixTimeToDateTime((uint)reader.GetInt32(5)),
                IsRead = reader.GetInt32(6) != 0,
                SenderDeleted = reader.GetInt32(7) != 0,
                ReceiverDeleted = reader.GetInt32(8) != 0
            };
        }
    }
}

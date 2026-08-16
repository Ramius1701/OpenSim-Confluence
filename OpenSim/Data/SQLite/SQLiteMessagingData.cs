using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // Backing store for the WebInterface resident-to-resident web mail
    // (inbox/sent/compose) - see OpenSim.Data.IMessagingData for the
    // design rationale.
    public class SQLiteMessagingData : IMessagingData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteMessagingData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "WebMessages");
            m.Update();
        }

        private const string Columns =
                "ID, SenderID, ReceiverID, Subject, Body, Created, IsRead, SenderDeleted, ReceiverDeleted";

        public WebMessage Get(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Columns + " FROM webmessages WHERE ID = :id", m_conn))
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

        public List<WebMessage> GetInbox(UUID userID, int count)
        {
            List<WebMessage> results = new List<WebMessage>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Columns + " FROM webmessages WHERE ReceiverID = :userid AND ReceiverDeleted = 0 " +
                        "ORDER BY Created DESC LIMIT :count", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":userid", userID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 200 : count));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadItem(reader));
                    }
                }
            }

            return results;
        }

        public List<WebMessage> GetSent(UUID userID, int count)
        {
            List<WebMessage> results = new List<WebMessage>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Columns + " FROM webmessages WHERE SenderID = :userid AND SenderDeleted = 0 " +
                        "ORDER BY Created DESC LIMIT :count", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":userid", userID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 200 : count));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadItem(reader));
                    }
                }
            }

            return results;
        }

        public bool Store(WebMessage message)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO webmessages (" + Columns + ") " +
                        "VALUES (:id, :senderid, :receiverid, :subject, :body, :created, :isread, :senderdeleted, :receiverdeleted)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", message.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":senderid", message.SenderID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":receiverid", message.ReceiverID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":subject", message.Subject));
                    cmd.Parameters.Add(new SQLiteParameter(":body", message.Body));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(message.Created)));
                    cmd.Parameters.Add(new SQLiteParameter(":isread", message.IsRead ? 1 : 0));
                    cmd.Parameters.Add(new SQLiteParameter(":senderdeleted", message.SenderDeleted ? 1 : 0));
                    cmd.Parameters.Add(new SQLiteParameter(":receiverdeleted", message.ReceiverDeleted ? 1 : 0));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool MarkRead(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("UPDATE webmessages SET IsRead = 1 WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool DeleteForUser(UUID id, UUID userID)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "UPDATE webmessages SET " +
                        "SenderDeleted = CASE WHEN SenderID = :userid THEN 1 ELSE SenderDeleted END, " +
                        "ReceiverDeleted = CASE WHEN ReceiverID = :userid THEN 1 ELSE ReceiverDeleted END " +
                        "WHERE ID = :id AND (SenderID = :userid OR ReceiverID = :userid)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":userid", userID.ToString()));
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static WebMessage ReadItem(IDataReader reader)
        {
            return new WebMessage
            {
                ID = UUID.Parse(reader.GetString(0)),
                SenderID = UUID.Parse(reader.GetString(1)),
                ReceiverID = UUID.Parse(reader.GetString(2)),
                Subject = reader.GetString(3),
                Body = reader.GetString(4),
                Created = Utils.UnixTimeToDateTime(System.Convert.ToUInt32(reader.GetValue(5))),
                IsRead = System.Convert.ToInt32(reader.GetValue(6)) != 0,
                SenderDeleted = System.Convert.ToInt32(reader.GetValue(7)) != 0,
                ReceiverDeleted = System.Convert.ToInt32(reader.GetValue(8)) != 0
            };
        }
    }
}

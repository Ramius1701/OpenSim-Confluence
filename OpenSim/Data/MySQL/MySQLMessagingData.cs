using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    // Backing store for the WebInterface resident-to-resident web mail
    // (inbox/sent/compose) - see OpenSim.Data.IMessagingData for the
    // design rationale.
    public class MySqlMessagingData : MySqlFramework, IMessagingData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlMessagingData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "WebMessages");
                m.Update();
                dbcon.Close();
            }
        }

        public WebMessage Get(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, SenderID, ReceiverID, Subject, Body, Created, IsRead, SenderDeleted, ReceiverDeleted " +
                        "FROM webmessages WHERE ID = ?ID", dbcon))
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

        public List<WebMessage> GetInbox(UUID userID, int count)
        {
            List<WebMessage> results = new List<WebMessage>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, SenderID, ReceiverID, Subject, Body, Created, IsRead, SenderDeleted, ReceiverDeleted " +
                        "FROM webmessages WHERE ReceiverID = ?UserID AND ReceiverDeleted = 0 ORDER BY Created DESC LIMIT ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?UserID", userID.ToString());
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 200 : count);

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

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, SenderID, ReceiverID, Subject, Body, Created, IsRead, SenderDeleted, ReceiverDeleted " +
                        "FROM webmessages WHERE SenderID = ?UserID AND SenderDeleted = 0 ORDER BY Created DESC LIMIT ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?UserID", userID.ToString());
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 200 : count);

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
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO webmessages (ID, SenderID, ReceiverID, Subject, Body, Created, IsRead, SenderDeleted, ReceiverDeleted) " +
                        "VALUES (?ID, ?SenderID, ?ReceiverID, ?Subject, ?Body, ?Created, ?IsRead, ?SenderDeleted, ?ReceiverDeleted)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", message.ID.ToString());
                    cmd.Parameters.AddWithValue("?SenderID", message.SenderID.ToString());
                    cmd.Parameters.AddWithValue("?ReceiverID", message.ReceiverID.ToString());
                    cmd.Parameters.AddWithValue("?Subject", message.Subject);
                    cmd.Parameters.AddWithValue("?Body", message.Body);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(message.Created));
                    cmd.Parameters.AddWithValue("?IsRead", message.IsRead ? 1 : 0);
                    cmd.Parameters.AddWithValue("?SenderDeleted", message.SenderDeleted ? 1 : 0);
                    cmd.Parameters.AddWithValue("?ReceiverDeleted", message.ReceiverDeleted ? 1 : 0);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool MarkRead(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand("UPDATE webmessages SET IsRead = 1 WHERE ID = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool DeleteForUser(UUID id, UUID userID)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "UPDATE webmessages SET " +
                        "SenderDeleted = IF(SenderID = ?UserID, 1, SenderDeleted), " +
                        "ReceiverDeleted = IF(ReceiverID = ?UserID, 1, ReceiverDeleted) " +
                        "WHERE ID = ?ID AND (SenderID = ?UserID OR ReceiverID = ?UserID)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());
                    cmd.Parameters.AddWithValue("?UserID", userID.ToString());
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
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(5))),
                IsRead = Convert.ToInt32(reader.GetValue(6)) != 0,
                SenderDeleted = Convert.ToInt32(reader.GetValue(7)) != 0,
                ReceiverDeleted = Convert.ToInt32(reader.GetValue(8)) != 0
            };
        }
    }
}

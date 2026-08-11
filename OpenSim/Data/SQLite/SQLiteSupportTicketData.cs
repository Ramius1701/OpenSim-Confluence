using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // Backing store for the WebInterface support ticket system - see
    // OpenSim.Data.ISupportTicketData for the design rationale.
    public class SQLiteSupportTicketData : ISupportTicketData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteSupportTicketData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "SupportTickets");
            m.Update();
        }

        private const string Columns =
                "ID, UserId, UserName, ContactEmail, Category, Subject, Message, Status, Created, Updated";

        public SupportTicket Get(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Columns + " FROM support_tickets WHERE ID = :id", m_conn))
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

        public List<SupportTicket> GetByUser(UUID userId, int start, int count)
        {
            List<SupportTicket> results = new List<SupportTicket>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Columns + " FROM support_tickets WHERE UserId = :userid " +
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

        public List<SupportTicket> GetAll(int start, int count)
        {
            List<SupportTicket> results = new List<SupportTicket>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + Columns + " FROM support_tickets " +
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

        public bool Store(SupportTicket ticket)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO support_tickets (" + Columns + ") " +
                        "VALUES (:id, :userid, :username, :email, :category, :subject, :message, :status, :created, :updated)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", ticket.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":userid", ticket.UserId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":username", ticket.UserName));
                    cmd.Parameters.Add(new SQLiteParameter(":email", ticket.ContactEmail));
                    cmd.Parameters.Add(new SQLiteParameter(":category", ticket.Category));
                    cmd.Parameters.Add(new SQLiteParameter(":subject", ticket.Subject));
                    cmd.Parameters.Add(new SQLiteParameter(":message", ticket.Message));
                    cmd.Parameters.Add(new SQLiteParameter(":status", ticket.Status));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(ticket.Created)));
                    cmd.Parameters.Add(new SQLiteParameter(":updated", Utils.DateTimeToUnixTime(ticket.Updated)));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static SupportTicket ReadItem(IDataReader reader)
        {
            return new SupportTicket
            {
                ID = UUID.Parse(reader.GetString(0)),
                UserId = UUID.Parse(reader.GetString(1)),
                UserName = reader.GetString(2),
                ContactEmail = reader.GetString(3),
                Category = reader.GetString(4),
                Subject = reader.GetString(5),
                Message = reader.GetString(6),
                Status = reader.GetString(7),
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(8))),
                Updated = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(9)))
            };
        }
    }
}

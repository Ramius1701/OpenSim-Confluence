using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    // Backing store for the WebInterface support ticket system - see
    // OpenSim.Data.ISupportTicketData for the design rationale.
    public class MySqlSupportTicketData : MySqlFramework, ISupportTicketData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlSupportTicketData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "SupportTickets");
                m.Update();
                dbcon.Close();
            }
        }

        private const string Columns =
                "ID, UserId, UserName, ContactEmail, Category, Subject, Message, Status, Created, Updated";

        public SupportTicket Get(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + Columns + " FROM support_tickets WHERE ID = ?ID", dbcon))
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

        public List<SupportTicket> GetByUser(UUID userId, int start, int count)
        {
            List<SupportTicket> results = new List<SupportTicket>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + Columns + " FROM support_tickets WHERE UserId = ?UserId " +
                        "ORDER BY Created DESC LIMIT ?start, ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?UserId", userId.ToString());
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

        public List<SupportTicket> GetAll(int start, int count)
        {
            List<SupportTicket> results = new List<SupportTicket>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + Columns + " FROM support_tickets " +
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

        public bool Store(SupportTicket ticket)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO support_tickets (" + Columns + ") " +
                        "VALUES (?ID, ?UserId, ?UserName, ?ContactEmail, ?Category, ?Subject, ?Message, ?Status, ?Created, ?Updated)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", ticket.ID.ToString());
                    cmd.Parameters.AddWithValue("?UserId", ticket.UserId.ToString());
                    cmd.Parameters.AddWithValue("?UserName", ticket.UserName);
                    cmd.Parameters.AddWithValue("?ContactEmail", ticket.ContactEmail);
                    cmd.Parameters.AddWithValue("?Category", ticket.Category);
                    cmd.Parameters.AddWithValue("?Subject", ticket.Subject);
                    cmd.Parameters.AddWithValue("?Message", ticket.Message);
                    cmd.Parameters.AddWithValue("?Status", ticket.Status);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(ticket.Created));
                    cmd.Parameters.AddWithValue("?Updated", Utils.DateTimeToUnixTime(ticket.Updated));

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

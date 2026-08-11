using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    // Backing store for the WebInterface support ticket system - see
    // OpenSim.Data.ISupportTicketData for the design rationale.
    public class PGSQLSupportTicketData : ISupportTicketData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLSupportTicketData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "SupportTickets");
                m.Update();
            }
        }

        private const string Columns =
                "\"ID\", \"UserId\", \"UserName\", \"ContactEmail\", \"Category\", \"Subject\", \"Message\", \"Status\", \"Created\", \"Updated\"";

        public SupportTicket Get(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + Columns + " FROM support_tickets WHERE \"ID\" = :id", conn))
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

        public List<SupportTicket> GetByUser(UUID userId, int start, int count)
        {
            List<SupportTicket> results = new List<SupportTicket>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + Columns + " FROM support_tickets WHERE \"UserId\" = :userid " +
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

        public List<SupportTicket> GetAll(int start, int count)
        {
            List<SupportTicket> results = new List<SupportTicket>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + Columns + " FROM support_tickets " +
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

        public bool Store(SupportTicket ticket)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO support_tickets (" + Columns + ") " +
                    "VALUES (:id, :userid, :username, :email, :category, :subject, :message, :status, :created, :updated) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET \"UserId\" = :userid, \"UserName\" = :username, \"ContactEmail\" = :email, " +
                    "\"Category\" = :category, \"Subject\" = :subject, \"Message\" = :message, \"Status\" = :status, \"Updated\" = :updated", conn))
            {
                cmd.Parameters.AddWithValue(":id", ticket.ID.ToString());
                cmd.Parameters.AddWithValue(":userid", ticket.UserId.ToString());
                cmd.Parameters.AddWithValue(":username", ticket.UserName);
                cmd.Parameters.AddWithValue(":email", ticket.ContactEmail);
                cmd.Parameters.AddWithValue(":category", ticket.Category);
                cmd.Parameters.AddWithValue(":subject", ticket.Subject);
                cmd.Parameters.AddWithValue(":message", ticket.Message);
                cmd.Parameters.AddWithValue(":status", ticket.Status);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(ticket.Created));
                cmd.Parameters.AddWithValue(":updated", (int)Utils.DateTimeToUnixTime(ticket.Updated));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static SupportTicket ReadItem(NpgsqlDataReader reader)
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
                Created = Utils.UnixTimeToDateTime((uint)reader.GetInt32(8)),
                Updated = Utils.UnixTimeToDateTime((uint)reader.GetInt32(9))
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    // Backing store for the WebInterface's portal-account system - see
    // OpenSim.Data.IWebAccountData for the design rationale.
    public class PGSQLWebAccountData : IWebAccountData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLWebAccountData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "WebAccount");
                m.Update();
            }
        }

        private const string AccountColumns =
                "\"ID\", \"Email\", \"Created\", \"Updated\"";

        public WebAccount GetById(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + AccountColumns + " FROM web_accounts WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return ReadAccount(reader);
                }
            }

            return null;
        }

        public WebAccount GetByEmail(string email)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + AccountColumns + " FROM web_accounts WHERE \"Email\" = :email", conn))
            {
                cmd.Parameters.AddWithValue(":email", email);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return ReadAccount(reader);
                }
            }

            return null;
        }

        public bool Store(WebAccount account)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO web_accounts (" + AccountColumns + ") " +
                    "VALUES (:id, :email, :created, :updated) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET \"Email\" = :email, \"Updated\" = :updated", conn))
            {
                cmd.Parameters.AddWithValue(":id", account.ID.ToString());
                cmd.Parameters.AddWithValue(":email", account.Email);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(account.Created));
                cmd.Parameters.AddWithValue(":updated", (int)Utils.DateTimeToUnixTime(account.Updated));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static WebAccount ReadAccount(NpgsqlDataReader reader)
        {
            return new WebAccount
            {
                ID = UUID.Parse(reader.GetString(0)),
                Email = reader.GetString(1),
                Created = Utils.UnixTimeToDateTime((uint)reader.GetInt32(2)),
                Updated = Utils.UnixTimeToDateTime((uint)reader.GetInt32(3))
            };
        }

        private const string LinkColumns =
                "\"WebAccountID\", \"AvatarPrincipalID\", \"LinkedDate\", \"LinkType\", \"IsDefault\"";

        public List<WebAccountAvatarLink> GetLinkedAvatars(UUID webAccountId)
        {
            List<WebAccountAvatarLink> results = new List<WebAccountAvatarLink>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + LinkColumns + " FROM web_account_avatars WHERE \"WebAccountID\" = :webaccountid " +
                    "ORDER BY \"LinkedDate\" ASC", conn))
            {
                cmd.Parameters.AddWithValue(":webaccountid", webAccountId.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadLink(reader));
                }
            }

            return results;
        }

        public WebAccountAvatarLink GetLinkForAvatar(UUID avatarPrincipalId)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + LinkColumns + " FROM web_account_avatars WHERE \"AvatarPrincipalID\" = :avatarid", conn))
            {
                cmd.Parameters.AddWithValue(":avatarid", avatarPrincipalId.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return ReadLink(reader);
                }
            }

            return null;
        }

        // Deliberately a plain INSERT with no ON CONFLICT clause -
        // AvatarPrincipalID is the table's primary key, so a second link
        // attempt for an already-linked avatar throws a PostgresException
        // rather than silently overwriting which WebAccount owns it. The
        // WebInterface connector catches this and turns it into a clean
        // "already linked" rejection message.
        public bool LinkAvatar(WebAccountAvatarLink link)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO web_account_avatars (" + LinkColumns + ") " +
                    "VALUES (:webaccountid, :avatarid, :linkeddate, :linktype, :isdefault)", conn))
            {
                cmd.Parameters.AddWithValue(":webaccountid", link.WebAccountID.ToString());
                cmd.Parameters.AddWithValue(":avatarid", link.AvatarPrincipalID.ToString());
                cmd.Parameters.AddWithValue(":linkeddate", (int)Utils.DateTimeToUnixTime(link.LinkedDate));
                cmd.Parameters.AddWithValue(":linktype", link.LinkType);
                cmd.Parameters.AddWithValue(":isdefault", link.IsDefault);
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool ReassignAvatar(UUID avatarPrincipalId, UUID newWebAccountId, string linkType, bool isDefault)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "UPDATE web_account_avatars SET \"WebAccountID\" = :newid, \"LinkType\" = :linktype, \"IsDefault\" = :isdefault " +
                    "WHERE \"AvatarPrincipalID\" = :avatarid", conn))
            {
                cmd.Parameters.AddWithValue(":newid", newWebAccountId.ToString());
                cmd.Parameters.AddWithValue(":linktype", linkType);
                cmd.Parameters.AddWithValue(":isdefault", isDefault);
                cmd.Parameters.AddWithValue(":avatarid", avatarPrincipalId.ToString());
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool ReassignActivity(UUID oldWebAccountId, UUID newWebAccountId)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "UPDATE web_activity_log SET \"WebAccountID\" = :newid WHERE \"WebAccountID\" = :oldid", conn))
            {
                cmd.Parameters.AddWithValue(":newid", newWebAccountId.ToString());
                cmd.Parameters.AddWithValue(":oldid", oldWebAccountId.ToString());
                conn.Open();

                cmd.ExecuteNonQuery();
                return true;
            }
        }

        public bool DeleteAccount(UUID webAccountId)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "DELETE FROM web_accounts WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", webAccountId.ToString());
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static WebAccountAvatarLink ReadLink(NpgsqlDataReader reader)
        {
            return new WebAccountAvatarLink
            {
                WebAccountID = UUID.Parse(reader.GetString(0)),
                AvatarPrincipalID = UUID.Parse(reader.GetString(1)),
                LinkedDate = Utils.UnixTimeToDateTime((uint)reader.GetInt32(2)),
                LinkType = reader.GetString(3),
                IsDefault = reader.GetBoolean(4)
            };
        }

        private const string ActivityColumns =
                "\"ID\", \"WebAccountID\", \"AvatarPrincipalID\", \"EventType\", \"Description\", \"IPAddress\", \"Created\"";

        public List<WebActivityEntry> GetRecentActivity(UUID webAccountId, int count)
        {
            List<WebActivityEntry> results = new List<WebActivityEntry>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + ActivityColumns + " FROM web_activity_log WHERE \"WebAccountID\" = :webaccountid " +
                    "ORDER BY \"Created\" DESC LIMIT :count", conn))
            {
                cmd.Parameters.AddWithValue(":webaccountid", webAccountId.ToString());
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 10 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadActivity(reader));
                }
            }

            return results;
        }

        public bool LogActivity(WebActivityEntry entry)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO web_activity_log (" + ActivityColumns + ") " +
                    "VALUES (:id, :webaccountid, :avatarid, :eventtype, :description, :ip, :created)", conn))
            {
                cmd.Parameters.AddWithValue(":id", entry.ID.ToString());
                cmd.Parameters.AddWithValue(":webaccountid", entry.WebAccountID.ToString());
                cmd.Parameters.AddWithValue(":avatarid", entry.AvatarPrincipalID.ToString());
                cmd.Parameters.AddWithValue(":eventtype", entry.EventType);
                cmd.Parameters.AddWithValue(":description", entry.Description);
                cmd.Parameters.AddWithValue(":ip", entry.IPAddress);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(entry.Created));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static WebActivityEntry ReadActivity(NpgsqlDataReader reader)
        {
            return new WebActivityEntry
            {
                ID = UUID.Parse(reader.GetString(0)),
                WebAccountID = UUID.Parse(reader.GetString(1)),
                AvatarPrincipalID = UUID.Parse(reader.GetString(2)),
                EventType = reader.GetString(3),
                Description = reader.GetString(4),
                IPAddress = reader.GetString(5),
                Created = Utils.UnixTimeToDateTime((uint)reader.GetInt32(6))
            };
        }
    }
}

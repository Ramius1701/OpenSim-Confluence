using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    // Backing store for the WebInterface's portal-account system - see
    // OpenSim.Data.IWebAccountData for the design rationale.
    public class MySqlWebAccountData : MySqlFramework, IWebAccountData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlWebAccountData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "WebAccount");
                m.Update();
                dbcon.Close();
            }
        }

        private const string AccountColumns = "ID, Email, Created, Updated";

        public WebAccount GetById(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + AccountColumns + " FROM web_accounts WHERE ID = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadAccount(reader);
                    }
                }
            }

            return null;
        }

        public WebAccount GetByEmail(string email)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + AccountColumns + " FROM web_accounts WHERE Email = ?Email", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Email", email);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadAccount(reader);
                    }
                }
            }

            return null;
        }

        public bool Store(WebAccount account)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO web_accounts (" + AccountColumns + ") " +
                        "VALUES (?ID, ?Email, ?Created, ?Updated)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", account.ID.ToString());
                    cmd.Parameters.AddWithValue("?Email", account.Email);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(account.Created));
                    cmd.Parameters.AddWithValue("?Updated", Utils.DateTimeToUnixTime(account.Updated));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static WebAccount ReadAccount(IDataReader reader)
        {
            return new WebAccount
            {
                ID = UUID.Parse(reader.GetString(0)),
                Email = reader.GetString(1),
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(2))),
                Updated = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(3)))
            };
        }

        private const string LinkColumns = "WebAccountID, AvatarPrincipalID, LinkedDate, LinkType, IsDefault";

        public List<WebAccountAvatarLink> GetLinkedAvatars(UUID webAccountId)
        {
            List<WebAccountAvatarLink> results = new List<WebAccountAvatarLink>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + LinkColumns + " FROM web_account_avatars WHERE WebAccountID = ?WebAccountID " +
                        "ORDER BY LinkedDate ASC", dbcon))
                {
                    cmd.Parameters.AddWithValue("?WebAccountID", webAccountId.ToString());

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadLink(reader));
                    }
                }
            }

            return results;
        }

        public WebAccountAvatarLink GetLinkForAvatar(UUID avatarPrincipalId)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + LinkColumns + " FROM web_account_avatars WHERE AvatarPrincipalID = ?AvatarPrincipalID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?AvatarPrincipalID", avatarPrincipalId.ToString());

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadLink(reader);
                    }
                }
            }

            return null;
        }

        // Deliberately INSERT, not REPLACE - AvatarPrincipalID is the
        // table's primary key, so a second link attempt for an
        // already-linked avatar throws a duplicate-key MySqlException
        // rather than silently overwriting which WebAccount owns it.
        // Callers (IWebAccountService.LinkAvatar) let this propagate; the
        // WebInterface connector catches it and turns it into a clean
        // "already linked" rejection message.
        public bool LinkAvatar(WebAccountAvatarLink link)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "INSERT INTO web_account_avatars (" + LinkColumns + ") " +
                        "VALUES (?WebAccountID, ?AvatarPrincipalID, ?LinkedDate, ?LinkType, ?IsDefault)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?WebAccountID", link.WebAccountID.ToString());
                    cmd.Parameters.AddWithValue("?AvatarPrincipalID", link.AvatarPrincipalID.ToString());
                    cmd.Parameters.AddWithValue("?LinkedDate", Utils.DateTimeToUnixTime(link.LinkedDate));
                    cmd.Parameters.AddWithValue("?LinkType", link.LinkType);
                    cmd.Parameters.AddWithValue("?IsDefault", link.IsDefault);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static WebAccountAvatarLink ReadLink(IDataReader reader)
        {
            return new WebAccountAvatarLink
            {
                WebAccountID = UUID.Parse(reader.GetString(0)),
                AvatarPrincipalID = UUID.Parse(reader.GetString(1)),
                LinkedDate = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(2))),
                LinkType = reader.GetString(3),
                IsDefault = Convert.ToBoolean(reader.GetValue(4))
            };
        }

        private const string ActivityColumns =
                "ID, WebAccountID, AvatarPrincipalID, EventType, Description, IPAddress, Created";

        public List<WebActivityEntry> GetRecentActivity(UUID webAccountId, int count)
        {
            List<WebActivityEntry> results = new List<WebActivityEntry>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + ActivityColumns + " FROM web_activity_log WHERE WebAccountID = ?WebAccountID " +
                        "ORDER BY Created DESC LIMIT ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?WebAccountID", webAccountId.ToString());
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 10 : count);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadActivity(reader));
                    }
                }
            }

            return results;
        }

        public bool LogActivity(WebActivityEntry entry)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "INSERT INTO web_activity_log (" + ActivityColumns + ") " +
                        "VALUES (?ID, ?WebAccountID, ?AvatarPrincipalID, ?EventType, ?Description, ?IPAddress, ?Created)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", entry.ID.ToString());
                    cmd.Parameters.AddWithValue("?WebAccountID", entry.WebAccountID.ToString());
                    cmd.Parameters.AddWithValue("?AvatarPrincipalID", entry.AvatarPrincipalID.ToString());
                    cmd.Parameters.AddWithValue("?EventType", entry.EventType);
                    cmd.Parameters.AddWithValue("?Description", entry.Description);
                    cmd.Parameters.AddWithValue("?IPAddress", entry.IPAddress);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(entry.Created));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static WebActivityEntry ReadActivity(IDataReader reader)
        {
            return new WebActivityEntry
            {
                ID = UUID.Parse(reader.GetString(0)),
                WebAccountID = UUID.Parse(reader.GetString(1)),
                AvatarPrincipalID = UUID.Parse(reader.GetString(2)),
                EventType = reader.GetString(3),
                Description = reader.GetString(4),
                IPAddress = reader.GetString(5),
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(6)))
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // Backing store for the WebInterface's portal-account system - see
    // OpenSim.Data.IWebAccountData for the design rationale.
    public class SQLiteWebAccountData : IWebAccountData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteWebAccountData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "WebAccount");
            m.Update();
        }

        private const string AccountColumns = "ID, Email, Created, Updated";

        public WebAccount GetById(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + AccountColumns + " FROM web_accounts WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + AccountColumns + " FROM web_accounts WHERE Email = :email", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":email", email));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO web_accounts (" + AccountColumns + ") " +
                        "VALUES (:id, :email, :created, :updated)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", account.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":email", account.Email));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(account.Created)));
                    cmd.Parameters.Add(new SQLiteParameter(":updated", Utils.DateTimeToUnixTime(account.Updated)));

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

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + LinkColumns + " FROM web_account_avatars WHERE WebAccountID = :webaccountid " +
                        "ORDER BY LinkedDate ASC", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":webaccountid", webAccountId.ToString()));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + LinkColumns + " FROM web_account_avatars WHERE AvatarPrincipalID = :avatarid", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":avatarid", avatarPrincipalId.ToString()));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadLink(reader);
                    }
                }
            }

            return null;
        }

        // Deliberately a plain INSERT (not INSERT OR REPLACE) -
        // AvatarPrincipalID is the table's primary key, so a second link
        // attempt for an already-linked avatar throws a constraint
        // exception rather than silently overwriting which WebAccount owns
        // it. The WebInterface connector catches this and turns it into a
        // clean "already linked" rejection message.
        public bool LinkAvatar(WebAccountAvatarLink link)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT INTO web_account_avatars (" + LinkColumns + ") " +
                        "VALUES (:webaccountid, :avatarid, :linkeddate, :linktype, :isdefault)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":webaccountid", link.WebAccountID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":avatarid", link.AvatarPrincipalID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":linkeddate", Utils.DateTimeToUnixTime(link.LinkedDate)));
                    cmd.Parameters.Add(new SQLiteParameter(":linktype", link.LinkType));
                    cmd.Parameters.Add(new SQLiteParameter(":isdefault", link.IsDefault));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool ReassignAvatar(UUID avatarPrincipalId, UUID newWebAccountId, string linkType, bool isDefault)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "UPDATE web_account_avatars SET WebAccountID = :newid, LinkType = :linktype, IsDefault = :isdefault " +
                        "WHERE AvatarPrincipalID = :avatarid", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":newid", newWebAccountId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":linktype", linkType));
                    cmd.Parameters.Add(new SQLiteParameter(":isdefault", isDefault));
                    cmd.Parameters.Add(new SQLiteParameter(":avatarid", avatarPrincipalId.ToString()));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool ReassignActivity(UUID oldWebAccountId, UUID newWebAccountId)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "UPDATE web_activity_log SET WebAccountID = :newid WHERE WebAccountID = :oldid", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":newid", newWebAccountId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":oldid", oldWebAccountId.ToString()));

                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
        }

        public bool DeleteAccount(UUID webAccountId)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "DELETE FROM web_accounts WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", webAccountId.ToString()));

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

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + ActivityColumns + " FROM web_activity_log WHERE WebAccountID = :webaccountid " +
                        "ORDER BY Created DESC LIMIT :count", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":webaccountid", webAccountId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 10 : count));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT INTO web_activity_log (" + ActivityColumns + ") " +
                        "VALUES (:id, :webaccountid, :avatarid, :eventtype, :description, :ip, :created)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", entry.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":webaccountid", entry.WebAccountID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":avatarid", entry.AvatarPrincipalID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":eventtype", entry.EventType));
                    cmd.Parameters.Add(new SQLiteParameter(":description", entry.Description));
                    cmd.Parameters.Add(new SQLiteParameter(":ip", entry.IPAddress));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(entry.Created)));

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

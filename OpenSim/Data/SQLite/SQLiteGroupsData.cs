using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // SQLite backend for Groups, equivalent to MySQLGroupsData / PGSQLGroupsData: one table per
    // concern (groups, membership, roles, role membership, invites, notices, principals, bans),
    // each behind the generic SQLite table handler.
    public class SQLiteGroupsData : IGroupsData
    {
        private SQLiteGroupsGroupsHandler m_Groups;
        private SQLiteGroupsMembershipHandler m_Membership;
        private SQLiteGroupsRolesHandler m_Roles;
        private SQLiteGroupsRoleMembershipHandler m_RoleMembership;
        private SQLiteGroupsInvitesHandler m_Invites;
        private SQLiteGroupsNoticesHandler m_Notices;
        private SQLiteGroupsPrincipalsHandler m_Principals;
        private SQLiteGroupsBansHandler m_Bans;

        public SQLiteGroupsData(string connectionString, string realm)
        {
            // The groups handler runs the migration that creates every table, so it goes first.
            m_Groups = new SQLiteGroupsGroupsHandler(connectionString, realm + "_groups", realm + "_Store");
            m_Membership = new SQLiteGroupsMembershipHandler(connectionString, realm + "_membership");
            m_Roles = new SQLiteGroupsRolesHandler(connectionString, realm + "_roles");
            m_RoleMembership = new SQLiteGroupsRoleMembershipHandler(connectionString, realm + "_rolemembership");
            m_Invites = new SQLiteGroupsInvitesHandler(connectionString, realm + "_invites");
            m_Notices = new SQLiteGroupsNoticesHandler(connectionString, realm + "_notices");
            m_Principals = new SQLiteGroupsPrincipalsHandler(connectionString, realm + "_principals");
            m_Bans = new SQLiteGroupsBansHandler(connectionString, realm + "_bans");
        }

        #region groups table
        public bool StoreGroup(GroupData data)
        {
            return m_Groups.Store(data);
        }

        public GroupData RetrieveGroup(UUID groupID)
        {
            GroupData[] groups = m_Groups.Get("GroupID", groupID.ToString());
            if (groups.Length > 0)
                return groups[0];

            return null;
        }

        public GroupData RetrieveGroup(string name)
        {
            GroupData[] groups = m_Groups.Get("Name", name);
            if (groups.Length > 0)
                return groups[0];

            return null;
        }

        public GroupData[] RetrieveGroups(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return m_Groups.Get("ShowInList=1");

            // The search text comes from a resident, so it only ever travels as a bound parameter.
            return m_Groups.GetWhere("ShowInList=1 AND Name LIKE :pattern", new SQLiteParameter(":pattern", "%" + pattern + "%"));
        }

        // Deleting a group removes its rows from every groups table (membership, roles, role
        // membership, invites, notices) and clears it as anyone's active group, not just the
        // groups row - the same cascade as the MySQL backend.
        public bool DeleteGroup(UUID groupID)
        {
            string groupIdStr = groupID.ToString();
            bool result = m_Groups.Delete("GroupID", groupIdStr);
            m_Membership.Delete("GroupID", groupIdStr);
            m_Roles.Delete("GroupID", groupIdStr);
            m_RoleMembership.Delete("GroupID", groupIdStr);
            m_Invites.Delete("GroupID", groupIdStr);
            m_Notices.Delete("GroupID", groupIdStr);
            m_Principals.Delete("ActiveGroupID", groupIdStr);
            return result;
        }

        public int GroupsCount()
        {
            return (int)m_Groups.GetCount("Location = ''");
        }
        #endregion

        #region membership table
        public MembershipData[] RetrieveMembers(UUID groupID)
        {
            return m_Membership.Get("GroupID", groupID.ToString());
        }

        public MembershipData RetrieveMember(UUID groupID, string pricipalID)
        {
            MembershipData[] m = m_Membership.Get(new string[] { "GroupID", "PrincipalID" },
                                                  new string[] { groupID.ToString(), pricipalID });
            if (m != null && m.Length > 0)
                return m[0];

            return null;
        }

        public MembershipData[] RetrieveMemberships(string pricipalID)
        {
            return m_Membership.Get("PrincipalID", pricipalID.ToString());
        }

        public bool StoreMember(MembershipData data)
        {
            return m_Membership.Store(data);
        }

        public bool DeleteMember(UUID groupID, string pricipalID)
        {
            return m_Membership.Delete(new string[] { "GroupID", "PrincipalID" },
                                       new string[] { groupID.ToString(), pricipalID });
        }

        public int MemberCount(UUID groupID)
        {
            return (int)m_Membership.GetCount("GroupID", groupID.ToString());
        }
        #endregion

        #region roles table
        public bool StoreRole(RoleData data)
        {
            return m_Roles.Store(data);
        }

        public RoleData RetrieveRole(UUID groupID, UUID roleID)
        {
            RoleData[] data = m_Roles.Get(new string[] { "GroupID", "RoleID" },
                                          new string[] { groupID.ToString(), roleID.ToString() });

            if (data != null && data.Length > 0)
                return data[0];

            return null;
        }

        public RoleData[] RetrieveRoles(UUID groupID)
        {
            return m_Roles.Get("GroupID", groupID.ToString());
        }

        public bool DeleteRole(UUID groupID, UUID roleID)
        {
            return m_Roles.Delete(new string[] { "GroupID", "RoleID" },
                                  new string[] { groupID.ToString(), roleID.ToString() });
        }

        public int RoleCount(UUID groupID)
        {
            return (int)m_Roles.GetCount("GroupID", groupID.ToString());
        }
        #endregion

        #region rolemembership table
        public RoleMembershipData[] RetrieveRolesMembers(UUID groupID)
        {
            return m_RoleMembership.Get("GroupID", groupID.ToString());
        }

        public RoleMembershipData[] RetrieveRoleMembers(UUID groupID, UUID roleID)
        {
            return m_RoleMembership.Get(new string[] { "GroupID", "RoleID" },
                                        new string[] { groupID.ToString(), roleID.ToString() });
        }

        public RoleMembershipData[] RetrieveMemberRoles(UUID groupID, string principalID)
        {
            return m_RoleMembership.Get(new string[] { "GroupID", "PrincipalID" },
                                        new string[] { groupID.ToString(), principalID });
        }

        public RoleMembershipData RetrieveRoleMember(UUID groupID, UUID roleID, string principalID)
        {
            RoleMembershipData[] data = m_RoleMembership.Get(new string[] { "GroupID", "RoleID", "PrincipalID" },
                                                             new string[] { groupID.ToString(), roleID.ToString(), principalID.ToString() });

            if (data != null && data.Length > 0)
                return data[0];

            return null;
        }

        public int RoleMemberCount(UUID groupID, UUID roleID)
        {
            return (int)m_RoleMembership.GetCount(new string[] { "GroupID", "RoleID" },
                                                  new string[] { groupID.ToString(), roleID.ToString() });
        }

        public bool StoreRoleMember(RoleMembershipData data)
        {
            return m_RoleMembership.Store(data);
        }

        public bool DeleteRoleMember(RoleMembershipData data)
        {
            return m_RoleMembership.Delete(new string[] { "GroupID", "RoleID", "PrincipalID" },
                                           new string[] { data.GroupID.ToString(), data.RoleID.ToString(), data.PrincipalID });
        }

        public bool DeleteMemberAllRoles(UUID groupID, string principalID)
        {
            return m_RoleMembership.Delete(new string[] { "GroupID", "PrincipalID" },
                                           new string[] { groupID.ToString(), principalID });
        }
        #endregion

        #region principals table
        public bool StorePrincipal(PrincipalData data)
        {
            return m_Principals.Store(data);
        }

        public PrincipalData RetrievePrincipal(string principalID)
        {
            PrincipalData[] p = m_Principals.Get("PrincipalID", principalID);
            if (p != null && p.Length > 0)
                return p[0];

            return null;
        }

        public bool DeletePrincipal(string principalID)
        {
            return m_Principals.Delete("PrincipalID", principalID);
        }
        #endregion

        #region invites table
        public bool StoreInvitation(InvitationData data)
        {
            return m_Invites.Store(data);
        }

        public InvitationData RetrieveInvitation(UUID inviteID)
        {
            InvitationData[] invites = m_Invites.Get("InviteID", inviteID.ToString());

            if (invites != null && invites.Length > 0)
                return invites[0];

            return null;
        }

        public InvitationData RetrieveInvitation(UUID groupID, string principalID)
        {
            InvitationData[] invites = m_Invites.Get(new string[] { "GroupID", "PrincipalID" },
                                                     new string[] { groupID.ToString(), principalID });

            if (invites != null && invites.Length > 0)
                return invites[0];

            return null;
        }

        public bool DeleteInvite(UUID inviteID)
        {
            return m_Invites.Delete("InviteID", inviteID.ToString());
        }

        public void DeleteOldInvites()
        {
            m_Invites.DeleteOld();
        }
        #endregion

        #region notices table
        public bool StoreNotice(NoticeData data)
        {
            return m_Notices.Store(data);
        }

        public NoticeData RetrieveNotice(UUID noticeID)
        {
            NoticeData[] notices = m_Notices.Get("NoticeID", noticeID.ToString());

            if (notices != null && notices.Length > 0)
                return notices[0];

            return null;
        }

        public NoticeData[] RetrieveNotices(UUID groupID)
        {
            return m_Notices.Get("GroupID", groupID.ToString());
        }

        public bool DeleteNotice(UUID noticeID)
        {
            return m_Notices.Delete("NoticeID", noticeID.ToString());
        }

        public void DeleteOldNotices()
        {
            m_Notices.DeleteOld();
        }
        #endregion

        #region bans table
        public bool StoreBan(BanData data)
        {
            return m_Bans.Store(data);
        }

        public BanData RetrieveBan(UUID groupID, string bannedID)
        {
            BanData[] b = m_Bans.Get(new string[] { "GroupID", "BannedID" },
                                     new string[] { groupID.ToString(), bannedID });
            if (b != null && b.Length > 0)
                return b[0];

            return null;
        }

        public BanData[] RetrieveBans(UUID groupID)
        {
            return m_Bans.Get("GroupID", groupID.ToString());
        }

        public bool DeleteBan(UUID groupID, string bannedID)
        {
            return m_Bans.Delete(new string[] { "GroupID", "BannedID" },
                                 new string[] { groupID.ToString(), bannedID });
        }
        #endregion

        #region combinations (not implemented by any backend)
        public MembershipData RetrievePrincipalGroupMembership(string principalID, UUID groupID)
        {
            return null;
        }

        public MembershipData[] RetrievePrincipalGroupMemberships(string principalID)
        {
            return null;
        }
        #endregion
    }

    // The generic SQLite table handler has no row counts; the groups tables need them.
    public class SQLiteGroupsTableHandler<T> : SQLiteGenericTableHandler<T> where T : class, new()
    {
        private static readonly Regex s_identifier = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        protected SQLiteGroupsTableHandler(string connectionString, string realm, string store)
            : base(connectionString, realm, store)
        {
        }

        // A where clause with one bound parameter (the generic handler's Get(where) has none).
        public T[] GetWhere(string where, SQLiteParameter parameter)
        {
            using (SQLiteCommand cmd = new SQLiteCommand())
            {
                cmd.CommandText = String.Format("select * from {0} where {1}", m_Realm, where);
                cmd.Parameters.Add(parameter);
                return DoQuery(cmd);
            }
        }

        public long GetCount(string field, string key)
        {
            return GetCount(new string[] { field }, new string[] { key });
        }

        public long GetCount(string[] fields, string[] keys)
        {
            if (fields.Length == 0 || fields.Length != keys.Length)
                return 0;

            using (SQLiteCommand cmd = new SQLiteCommand())
            {
                List<string> terms = new List<string>();
                for (int i = 0; i < fields.Length; i++)
                {
                    if (!s_identifier.IsMatch(fields[i]))
                        return 0;

                    cmd.Parameters.Add(new SQLiteParameter(":p" + i, keys[i]));
                    terms.Add("`" + fields[i] + "` = :p" + i);
                }

                cmd.CommandText = String.Format("select count(*) from {0} where {1}", m_Realm, String.Join(" and ", terms.ToArray()));
                return Scalar(cmd);
            }
        }

        // The where clause is built by this assembly's own code, never from user input.
        public long GetCount(string where)
        {
            using (SQLiteCommand cmd = new SQLiteCommand())
            {
                cmd.CommandText = String.Format("select count(*) from {0} where {1}", m_Realm, where);
                return Scalar(cmd);
            }
        }

        private long Scalar(SQLiteCommand cmd)
        {
            using (IDataReader reader = ExecuteReader(cmd, m_Connection))
            {
                return reader.Read() ? Convert.ToInt64(reader[0]) : 0;
            }
        }
    }

    public class SQLiteGroupsGroupsHandler : SQLiteGroupsTableHandler<GroupData>
    {
        public SQLiteGroupsGroupsHandler(string connectionString, string realm, string store)
            : base(connectionString, realm, store)
        {
        }
    }

    public class SQLiteGroupsMembershipHandler : SQLiteGroupsTableHandler<MembershipData>
    {
        public SQLiteGroupsMembershipHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }
    }

    public class SQLiteGroupsRolesHandler : SQLiteGroupsTableHandler<RoleData>
    {
        public SQLiteGroupsRolesHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }
    }

    public class SQLiteGroupsRoleMembershipHandler : SQLiteGroupsTableHandler<RoleMembershipData>
    {
        public SQLiteGroupsRoleMembershipHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }
    }

    public class SQLiteGroupsInvitesHandler : SQLiteGroupsTableHandler<InvitationData>
    {
        public SQLiteGroupsInvitesHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }

        // Invitations older than two weeks are dropped (the timestamp column is a SQLite datetime).
        public void DeleteOld()
        {
            using (SQLiteCommand cmd = new SQLiteCommand())
            {
                cmd.CommandText = String.Format("delete from {0} where TMStamp < datetime('now', '-14 days')", m_Realm);
                ExecuteNonQuery(cmd, m_Connection);
            }
        }
    }

    public class SQLiteGroupsNoticesHandler : SQLiteGroupsTableHandler<NoticeData>
    {
        public SQLiteGroupsNoticesHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }

        // Notices carry a unix timestamp; drop those more than two weeks old.
        public void DeleteOld()
        {
            uint now = (uint)Util.UnixTimeSinceEpoch();

            using (SQLiteCommand cmd = new SQLiteCommand())
            {
                cmd.CommandText = String.Format("delete from {0} where TMStamp < :tstamp", m_Realm);
                cmd.Parameters.Add(new SQLiteParameter(":tstamp", (long)now - 14L * 24 * 60 * 60));
                ExecuteNonQuery(cmd, m_Connection);
            }
        }
    }

    public class SQLiteGroupsPrincipalsHandler : SQLiteGroupsTableHandler<PrincipalData>
    {
        public SQLiteGroupsPrincipalsHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }
    }

    public class SQLiteGroupsBansHandler : SQLiteGroupsTableHandler<BanData>
    {
        public SQLiteGroupsBansHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }
    }
}

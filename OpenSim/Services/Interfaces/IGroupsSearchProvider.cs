using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    // Minimal interface satisfied by OpenSim.Addons.Groups' concrete
    // GroupsService class, letting the Robust-side web connector query
    // group search without a compile-time reference to OpenSim.Addons.Groups
    // (which itself already references OpenSim.Server.Handlers, so a direct
    // reference the other way would be circular). Loaded purely via
    // reflection (ServerUtils.LoadPlugin), same as every other service this
    // connector uses - see WebInterfaceServiceConnector's m_GroupsService.
    //
    // GroupsService already has a plain (IConfigSource) constructor and this
    // exact FindGroups signature; the only change needed on that class is
    // declaring it implements this interface.
    public interface IGroupsSearchProvider
    {
        List<DirGroupsReplyData> FindGroups(string requestingAgentID, string search);

        // GroupsService already has this exact signature (GetAgentGroupMemberships) -
        // backs the public profile page's group-memberships list. Callers must
        // filter to GroupMembershipData.ListInProfile == true themselves, same
        // as the viewer's own profile floater - this returns every membership
        // regardless of that flag.
        List<GroupMembershipData> GetAgentGroupMemberships(string requestingAgentID, string agentID);

        // Grid-wide admin Groups overview page. GetAllGroups/DeleteGroup/
        // UpdateGroupFlags are new methods added directly to GroupsService
        // (not previously public) - see that class's comments for why
        // GetAllGroups only sees ShowInList groups (same SQL FindGroups
        // already uses) and why UpdateGroupFlags exists instead of reusing
        // the real UpdateGroup signature directly.
        List<GroupOverviewData> GetAllGroups(string requestingAgentID);
        bool DeleteGroup(string requestingAgentID, UUID groupID);
        void UpdateGroupFlags(string requestingAgentID, UUID groupID, bool showInList, bool openEnrollment, bool allowPublish, bool maturePublish);
    }
}

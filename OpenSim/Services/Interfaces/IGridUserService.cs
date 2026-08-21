/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Services.Interfaces
{
    /// <summary>
    /// Records user information specific to a grid but which is not part of a user's account.
    /// </summary>
    public class GridUserInfo
    {
        public string UserID;

        public UUID HomeRegionID;
        public Vector3 HomePosition;
        public Vector3 HomeLookAt;

        public UUID LastRegionID;
        public Vector3 LastPosition;
        public Vector3 LastLookAt;

        public bool Online;
        public DateTime Login;
        public DateTime Logout;

        public GridUserInfo() {}

        public GridUserInfo(Dictionary<string, object> kvp)
        {
            if (kvp.ContainsKey("UserID"))
                UserID = kvp["UserID"].ToString();

            if (kvp.ContainsKey("HomeRegionID"))
                UUID.TryParse(kvp["HomeRegionID"].ToString(), out HomeRegionID);
            if (kvp.ContainsKey("HomePosition"))
                Vector3.TryParse(kvp["HomePosition"].ToString(), out HomePosition);
            if (kvp.ContainsKey("HomeLookAt"))
                Vector3.TryParse(kvp["HomeLookAt"].ToString(), out HomeLookAt);

            if (kvp.ContainsKey("LastRegionID"))
                UUID.TryParse(kvp["LastRegionID"].ToString(), out LastRegionID);
            if (kvp.ContainsKey("LastPosition"))
                Vector3.TryParse(kvp["LastPosition"].ToString(), out LastPosition);
            if (kvp.ContainsKey("LastLookAt"))
                Vector3.TryParse(kvp["LastLookAt"].ToString(), out LastLookAt);

            if (kvp.ContainsKey("Login"))
                DateTime.TryParse(kvp["Login"].ToString(), out Login);
            if (kvp.ContainsKey("Logout"))
                DateTime.TryParse(kvp["Logout"].ToString(), out Logout);
            if (kvp.ContainsKey("Online"))
                Boolean.TryParse(kvp["Online"].ToString(), out Online);

        }

        public virtual Dictionary<string, object> ToKeyValuePairs()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["UserID"] = UserID;

            result["HomeRegionID"] = HomeRegionID.ToString();
            result["HomePosition"] = HomePosition.ToString();
            result["HomeLookAt"] = HomeLookAt.ToString();

            result["LastRegionID"] = LastRegionID.ToString();
            result["LastPosition"] = LastPosition.ToString();
            result["LastLookAt"] = LastLookAt.ToString();

            result["Online"] = Online.ToString();
            result["Login"] = Login.ToString();
            result["Logout"] = Logout.ToString();

            return result;
        }
    }

    public interface IGridUserService
    {
        GridUserInfo LoggedIn(string userID);

        /// <summary>
        /// Informs the grid that a user is logged out and to remove any session data for them
        /// </summary>
        /// <param name="userID">Ignore if your connector does not use userID for logouts</param>
        /// <param name="sessionID">Ignore if your connector does not use sessionID for logouts</param>
        /// <param name="regionID">RegionID where the user was last located</param>
        /// <param name="lastPosition">Last region-relative position of the user</param>
        /// <param name="lastLookAt">Last normalized look direction for the user</param>
        /// <returns>True if the logout request was successfully processed, otherwise false</returns>
        bool LoggedOut(string userID, UUID sessionID, UUID regionID, Vector3 lastPosition, Vector3 lastLookAt);

        bool SetHome(string userID, UUID homeID, Vector3 homePosition, Vector3 homeLookAt);

        /// <summary>
        /// Stores the last known user position at the grid level
        /// </summary>
        /// <param name="userID">Ignore if your connector does not use userID for position updates</param>
        /// <param name="sessionID">Ignore if your connector does not use sessionID for position updates</param>
        /// <param name="regionID">RegionID where the user is currently located</param>
        /// <param name="lastPosition">Region-relative position</param>
        /// <param name="lastLookAt">Normalized look direction</param>
        /// <returns>True if the user's last position was successfully updated, otherwise false</returns>
        bool SetLastPosition(string userID, UUID sessionID, UUID regionID, Vector3 lastPosition, Vector3 lastLookAt);

        GridUserInfo GetGridUserInfo(string userID);
        GridUserInfo[] GetGridUserInfo(string[] userID);

        // Grid-wide count of users currently marked online (Online==true and
        // logged in within the last 5 days, same accuracy caveat as the
        // "show grid users online" console command this mirrors - a region
        // that crashes without cleanly logging a user out can leave them
        // shown as online). Added for the WebInterface grid statistics
        // dashboard, which had no existing way to get this number without
        // reaching past the service interface into console-command code.
        int GetOnlineUserCount();

        // Distinct visitors (local and hypergrid) whose last login falls within
        // the given number of days, regardless of whether they're still online -
        // backs a grid-status "unique visitors" figure, e.g. the 30-day number
        // hypergrid directories look for when deciding whether to list a grid.
        int GetUniqueVisitorCount(int days);

        // Same "online now" figure as GetOnlineUserCount(), but only counting
        // users whose LastRegionID is in the given set of genuinely-alive
        // region UUIDs. GetOnlineUserCount() alone trusts the GridUser
        // "Online" flag, which - like the regions table's own online flag -
        // only ever gets cleared by a clean logout; a region process that
        // crashes or is killed leaves every avatar who was on it stuck
        // showing "online" forever, with nothing to ever correct it. Pass
        // an empty/null set to mean "no regions are alive" (returns 0).
        int GetOnlineUserCount(HashSet<string> aliveRegionIDs);
    }
}

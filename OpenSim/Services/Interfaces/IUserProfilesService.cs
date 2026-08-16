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
using OpenSim.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace OpenSim.Services.Interfaces
{
    public interface IUserProfilesService
    {
        #region Classifieds
        OSD AvatarClassifiedsRequest(UUID creatorId);
        bool ClassifiedUpdate(UserClassifiedAdd ad, ref string result);
        bool ClassifiedInfoRequest(ref UserClassifiedAdd ad, ref string result);
        bool ClassifiedDelete(UUID recordId);

        // Grid-wide (not creator-scoped like the above) - backs the
        // WebInterface splash page's "Featured Classifieds" widget.
        List<UserClassifiedAdd> GetRecentClassifieds(int count);

        // Grid-wide keyword search - backs the /web/search page.
        List<UserClassifiedAdd> SearchClassifieds(string queryText, int start, int count);
        #endregion Classifieds

        #region Picks
        OSD AvatarPicksRequest(UUID creatorId);
        bool PickInfoRequest(ref UserProfilePick pick, ref string result);
        bool PicksUpdate(ref UserProfilePick pick, ref string result);
        bool PicksDelete(UUID pickId);

        // Grid-wide keyword search, same shape as SearchClassifieds above -
        // Picks are managed entirely in-world (viewer Profile floater), so
        // this is how residents discover other people's Picks from the web
        // without a dedicated browse/management page for them.
        List<UserProfilePick> SearchPicks(string queryText, int start, int count);
        #endregion Picks

        #region Notes
        bool AvatarNotesRequest(ref UserProfileNotes note);
        bool NotesUpdate(ref UserProfileNotes note, ref string result);
        #endregion Notes

        #region Profile Properties
        bool AvatarPropertiesRequest(ref UserProfileProperties prop, ref string result);
        bool AvatarPropertiesUpdate(ref UserProfileProperties prop, ref string result);

        // PartnerId is excluded from AvatarPropertiesUpdate's underlying
        // UPDATE statement (see IProfilesData.UpdateAvatarPartner) - this is
        // the real way to change it, used by the web partner-proposal flow.
        bool UpdateAvatarPartner(UUID userId, UUID partnerId, ref string result);
        #endregion Profile Properties

        #region User Preferences
        bool UserPreferencesRequest(ref UserPreferences pref, ref string result);
        bool UserPreferencesUpdate(ref UserPreferences pref, ref string result);
        #endregion User Preferences

        #region Interests
        bool AvatarInterestsUpdate(UserProfileProperties prop, ref string result);
        #endregion Interests

        #region Utility
        OSD AvatarImageAssetsRequest(UUID avatarId);
        #endregion Utility

        #region UserData
        bool RequestUserAppData(ref UserAppData prop, ref string result);
        bool SetUserAppData(UserAppData prop, ref string result);
        #endregion UserData
    }
}


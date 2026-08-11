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
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace OpenSim.Data
{

    public interface IProfilesData
    {
        // Grid-wide, most-recent-first, excludes expired listings - unlike
        // GetClassifiedRecords (creator-scoped, id+name only, for a user's
        // own profile editor), this backs the WebInterface splash page's
        // "Featured Classifieds" widget, which needs full records across
        // every creator.
        List<UserClassifiedAdd> GetRecentClassifieds(int count);

        // Grid-wide keyword search, for the /web/search page - same
        // LIKE-on-name-or-description shape as ISearchData.SearchPlaces,
        // scoped to non-expired listings same as GetRecentClassifieds.
        List<UserClassifiedAdd> SearchClassifieds(string queryText, int start, int count);

        OSDArray GetClassifiedRecords(UUID creatorId);
        bool UpdateClassifiedRecord(UserClassifiedAdd ad, ref string result);
        bool DeleteClassifiedRecord(UUID recordId);
        OSDArray GetAvatarPicks(UUID avatarId);
        UserProfilePick GetPickInfo(UUID avatarId, UUID pickId);
        bool UpdatePicksRecord(UserProfilePick pick);
        bool DeletePicksRecord(UUID pickId);
        bool GetAvatarNotes(ref UserProfileNotes note);
        bool UpdateAvatarNotes(ref UserProfileNotes note, ref string result);
        bool GetAvatarProperties(ref UserProfileProperties props, ref string result);
        bool UpdateAvatarProperties(ref UserProfileProperties props, ref string result);
        bool UpdateAvatarInterests(UserProfileProperties up, ref string result);

        // Narrow update, same shape as UpdateAvatarInterests - PartnerId is
        // deliberately NOT part of UpdateAvatarProperties' UPDATE statement
        // (it only ever gets written once, by GetAvatarProperties' insert-if-
        // missing branch, on a brand new profile row) - this is the one real
        // way to change it after that, backing the web partner-proposal flow.
        bool UpdateAvatarPartner(UUID userId, UUID partnerId, ref string result);
        bool GetClassifiedInfo(ref UserClassifiedAdd ad, ref string result);
        bool UpdateUserPreferences(ref UserPreferences pref, ref string result);
        bool GetUserPreferences(ref UserPreferences pref, ref string result);
        bool GetUserAppData(ref UserAppData props, ref string result);
        bool SetUserAppData(UserAppData props, ref string result);
        OSDArray GetUserImageAssets(UUID avatarId);
    }
}


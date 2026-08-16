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
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    // Shared by WebInterfaceServiceConnector (web dashboard/admin login) and
    // LLLoginService (the real grid/viewer login) so a timed account ban
    // self-clears the same way regardless of which path a resident uses to
    // log in - previously only the web/admin paths cleared an expired ban,
    // so a resident who never touched the web UI stayed blocked past their
    // ban's expiry until an admin manually unbanned them.
    public static class AccountBanHelper
    {
        // Sentinel stored directly in UserAccount.UserLevel. Any negative
        // level already blocks login via the existing
        // "UserLevel < MinLoginLevel" check both login paths use - this
        // constant just gives that one negative value a clear, consistent
        // meaning wherever a ban needs to be checked or cleared.
        public const int BannedUserLevel = -1;

        // Timed-ban expiry, stored in the generic UserAppData key/value
        // store owned by IUserProfilesService, keyed by a fixed tag UUID.
        // Zero/absent means "no expiry" (permanent ban).
        public static readonly UUID BanExpiryTag = new UUID("9b1f9b1a-0000-4a00-8000-000000000003");

        public static DateTime? GetBanExpiry(IUserProfilesService profilesService, UUID userId)
        {
            if (profilesService == null)
                return null;

            UserAppData data = new UserAppData { UserId = userId.ToString(), TagId = BanExpiryTag.ToString() };
            string result = string.Empty;
            profilesService.RequestUserAppData(ref data, ref result);

            return long.TryParse(data.DataVal, out long unixSeconds) && unixSeconds > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime
                    : (DateTime?)null;
        }

        public static void SetBanExpiry(IUserProfilesService profilesService, UUID userId, DateTime? expiry)
        {
            if (profilesService == null)
                return;

            UserAppData data = new UserAppData { UserId = userId.ToString(), TagId = BanExpiryTag.ToString() };
            string result = string.Empty;
            profilesService.RequestUserAppData(ref data, ref result);
            data.DataKey = "BanExpiry";
            data.DataVal = expiry.HasValue ? new DateTimeOffset(expiry.Value, TimeSpan.Zero).ToUnixTimeSeconds().ToString() : "0";
            profilesService.SetUserAppData(data, ref result);
        }

        // What UserLevel the account had right before it was banned, so
        // unbanning (whether by the admin button or by expiry) can restore
        // it instead of dropping every unbanned account to a flat 0 - this
        // matters for anyone above the ordinary resident level (an estate
        // manager, a grid admin) who gets banned. Same storage pattern as
        // BanExpiryTag. Absent means "unknown/never recorded" - callers
        // should fall back to 0 only in that case, e.g. for accounts banned
        // before this existed.
        public static readonly UUID PreBanLevelTag = new UUID("9b1f9b1a-0000-4a00-8000-000000000004");

        public static int? GetPreBanLevel(IUserProfilesService profilesService, UUID userId)
        {
            if (profilesService == null)
                return null;

            UserAppData data = new UserAppData { UserId = userId.ToString(), TagId = PreBanLevelTag.ToString() };
            string result = string.Empty;
            profilesService.RequestUserAppData(ref data, ref result);

            return int.TryParse(data.DataVal, out int level) ? level : (int?)null;
        }

        public static void SetPreBanLevel(IUserProfilesService profilesService, UUID userId, int? level)
        {
            if (profilesService == null)
                return;

            UserAppData data = new UserAppData { UserId = userId.ToString(), TagId = PreBanLevelTag.ToString() };
            string result = string.Empty;
            profilesService.RequestUserAppData(ref data, ref result);
            data.DataKey = "PreBanLevel";
            data.DataVal = level.HasValue ? level.Value.ToString() : string.Empty;
            profilesService.SetUserAppData(data, ref result);
        }

        // Called wherever an account's UserLevel is read for a login/admin
        // decision - a temp-banned account whose timer has run out reverts
        // to whatever level it had before the ban (or 0 if that was never
        // recorded) rather than needing an admin to manually unban it.
        // Returns true if it just cleared an expired ban (callers that
        // already loaded the account's old UserLevel into a local should
        // re-check after calling this).
        public static bool ClearExpiredBan(UserAccount account, IUserAccountService accountService, IUserProfilesService profilesService)
        {
            if (account == null || account.UserLevel != BannedUserLevel || accountService == null)
                return false;

            DateTime? expiry = GetBanExpiry(profilesService, account.PrincipalID);
            if (expiry == null || expiry.Value > DateTime.UtcNow)
                return false;

            account.UserLevel = GetPreBanLevel(profilesService, account.PrincipalID) ?? 0;
            accountService.StoreUserAccount(account);
            SetBanExpiry(profilesService, account.PrincipalID, null);
            SetPreBanLevel(profilesService, account.PrincipalID, null);
            return true;
        }
    }
}

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

using System.Collections.Generic;

namespace OpenSim.Services.Interfaces
{
    // Shared naming/encoding for the account "membership type" nibble packed into
    // UserAccount.UserFlags bits 8-11 (mask 0x0f00). UserProfileModule.cs reads this
    // same nibble to fill AvatarPropertiesReply.CharterMember, the classic viewer's
    // profile badge field - Second Life's own fixed four values (Resident/Trial
    // Member/Charter Member/Linden Lab Employee) only ever meant something on
    // Linden Lab's own grid, so renamed here to labels that make sense for an
    // independent one, plus one new value SL never had: a resident who has
    // financially supported the grid.
    //
    // Only values 0-3 have a built-in badge icon in the classic viewer protocol -
    // anything above that renders nothing on its own, because the viewer shows
    // UserAccount.UserTitle text INSTEAD of the numeric badge whenever UserTitle is
    // non-empty, and shows nothing at all for an unrecognized numeric value when
    // UserTitle is empty (see UserProfileModule.cs's ProcessRequest). NeedsTitleToDisplay
    // exists so a caller setting a custom type can know it needs to also set a title.
    public static class AccountMembershipHelper
    {
        public const int Resident = 0;
        public const int TrialMember = 1;
        public const int CharterMember = 2;
        public const int GridTeam = 3;   // renamed from Second Life's "Linden Lab Employee"
        public const int Supporter = 4;  // new - a resident who has financially supported the grid

        private static readonly Dictionary<int, string> Names = new()
        {
            [Resident] = "Resident",
            [TrialMember] = "Trial Member",
            [CharterMember] = "Charter Member",
            [GridTeam] = "Grid Team",
            [Supporter] = "Supporter",
        };

        public static IReadOnlyDictionary<int, string> AllTypes => Names;

        public static string GetName(int membershipType)
        {
            return Names.TryGetValue(membershipType, out string name) ? name : "Type " + membershipType;
        }

        // Types 0-3 all have a real, built-in viewer badge icon (or, for Resident,
        // deliberately no badge). Anything past that needs UserTitle set to actually
        // show up in a resident's profile.
        public static bool NeedsTitleToDisplay(int membershipType)
        {
            return membershipType > GridTeam;
        }

        // Bits 0-7 of UserFlags are a separate set of flags (indexed/mature/
        // identified/transacted/online/age-verified - see UserProfileModule.cs)
        // that this helper must never disturb.
        public static int GetMembershipType(int userFlags)
        {
            return (userFlags >> 8) & 0x0f;
        }

        public static int SetMembershipType(int userFlags, int membershipType)
        {
            return (userFlags & 0xff) | ((membershipType & 0x0f) << 8);
        }
    }
}

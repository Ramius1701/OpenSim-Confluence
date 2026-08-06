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
 *     * Neither the name of the OpenSim Project nor the
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
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.Framework.Interfaces
{
    /// <summary>
    /// Optional team-based combat game-mode layer. Purely additive: team
    /// membership, a shared combat respawn point, teleport-block while a
    /// team member is "in combat", and a configurable health regen rate.
    /// Does not participate in damage application - avatar health is still
    /// driven entirely by the existing collision-damage code in
    /// ScenePresence and by the Combat2 llDamage/llAdjustDamage pipeline in
    /// LSL_Api; this module only decides where a team member respawns and
    /// whether they may teleport away while active in a team.
    /// </summary>
    public interface ITeamCombatModule
    {
        /// <summary>
        /// Called by CombatModule.KillAvatar just before it would otherwise
        /// teleport the dead avatar home. If the avatar is a member of a
        /// team, this places them at the configured combat respawn point
        /// (and starts the movement-lock grace window, if configured)
        /// instead, and returns true so the caller skips its own
        /// teleport-home call. Returns false for non-team-members, leaving
        /// the caller's default teleport-home behaviour untouched.
        /// </summary>
        bool TryHandleRespawn(ScenePresence deadAvatar);

        string GetTeam(UUID agentID);
        List<UUID> GetTeammates(string team);
        bool JoinTeam(UUID agentID, string team);
        bool LeaveTeam(UUID agentID);
    }
}

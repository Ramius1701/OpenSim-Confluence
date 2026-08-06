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

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Timers;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.OptionalModules.Avatar.Combat
{
    /// <summary>
    /// Team-based combat game-mode layer, ported from WhiteCore-Dev's
    /// CombatModule. Deliberately does not touch damage application -
    /// avatar health is still driven entirely by ScenePresence's own
    /// collision-damage code and by the Combat2 llDamage/llAdjustDamage
    /// pipeline in LSL_Api. This module only adds: team membership, a
    /// shared combat respawn point (instead of teleport-home) for team
    /// members, a teleport-block while a team member hasn't left combat,
    /// and a configurable health regen rate for team members. See
    /// PROJECT_LOG.md for the full rationale behind this reduced scope.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "TeamCombatModule")]
    public class TeamCombatModule : ISharedRegionModule, ITeamCombatModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly List<Scene> m_scenes = new List<Scene>();
        private bool m_enabled;

        private bool m_disallowTeleportingForCombatants = true;
        private Vector3 m_respawnPosition = new Vector3(128f, 128f, 30f);
        private int m_secondsBeforeRespawn = 5;
        private bool m_shouldRespawn = true;
        private bool m_regenHealth = true;
        private float m_regenerateHealthSpeed = 1.0f;

        private readonly object m_teamLock = new object();
        private readonly Dictionary<string, List<UUID>> m_teams = new Dictionary<string, List<UUID>>();
        private readonly Dictionary<UUID, string> m_agentTeam = new Dictionary<UUID, string>();
        private readonly Dictionary<UUID, float> m_savedHealRate = new Dictionary<UUID, float>();
        private readonly Dictionary<UUID, Timer> m_respawnTimers = new Dictionary<UUID, Timer>();
        private readonly HashSet<UUID> m_inRespawnGrace = new HashSet<UUID>();

        public string Name
        {
            get { return "TeamCombatModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["TeamCombatModule"];
            if (config == null)
                return;

            m_enabled = config.GetBoolean("Enabled", false);
            m_disallowTeleportingForCombatants = config.GetBoolean("DisallowTeleportingForCombatants", true);
            m_respawnPosition.X = config.GetFloat("RespawnPositionX", 128f);
            m_respawnPosition.Y = config.GetFloat("RespawnPositionY", 128f);
            m_respawnPosition.Z = config.GetFloat("RespawnPositionZ", 30f);
            m_secondsBeforeRespawn = config.GetInt("SecondsBeforeRespawn", 5);
            m_shouldRespawn = config.GetBoolean("ShouldRespawn", true);
            m_regenHealth = config.GetBoolean("RegenerateHealth", true);
            m_regenerateHealthSpeed = config.GetFloat("RegenerateHealthSpeed", 1.0f);
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
                m_scenes.Add(scene);

            scene.RegisterModuleInterface<ITeamCombatModule>(this);
            scene.Permissions.OnTeleport += CheckTeleport;
            scene.EventManager.OnRemovePresence += OnRemovePresence;

            MainConsole.Instance.Commands.AddCommand("Combat", false,
                "combat team join",
                "combat team join <avatar uuid> <team name>",
                "Add an avatar to a combat team", HandleTeamJoin);

            MainConsole.Instance.Commands.AddCommand("Combat", false,
                "combat team leave",
                "combat team leave <avatar uuid>",
                "Remove an avatar from its combat team", HandleTeamLeave);

            MainConsole.Instance.Commands.AddCommand("Combat", false,
                "combat team show",
                "combat team show <team name>",
                "List the members of a combat team", HandleTeamShow);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
                m_scenes.Remove(scene);

            scene.Permissions.OnTeleport -= CheckTeleport;
            scene.EventManager.OnRemovePresence -= OnRemovePresence;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        #region Console commands

        private void HandleTeamJoin(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 4 || !UUID.TryParse(cmdparams[2], out UUID agentID))
            {
                MainConsole.Instance.Output("Usage: combat team join <avatar uuid> <team name>");
                return;
            }

            string team = string.Join(" ", cmdparams, 3, cmdparams.Length - 3);
            JoinTeam(agentID, team);
            MainConsole.Instance.Output("Added {0} to combat team \"{1}\"", agentID, team);
        }

        private void HandleTeamLeave(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 3 || !UUID.TryParse(cmdparams[2], out UUID agentID))
            {
                MainConsole.Instance.Output("Usage: combat team leave <avatar uuid>");
                return;
            }

            if (LeaveTeam(agentID))
                MainConsole.Instance.Output("Removed {0} from its combat team", agentID);
            else
                MainConsole.Instance.Output("{0} is not on a combat team", agentID);
        }

        private void HandleTeamShow(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 3)
            {
                MainConsole.Instance.Output("Usage: combat team show <team name>");
                return;
            }

            string team = string.Join(" ", cmdparams, 2, cmdparams.Length - 2);
            List<UUID> members = GetTeammates(team);
            if (members.Count == 0)
            {
                MainConsole.Instance.Output("Combat team \"{0}\" has no members", team);
                return;
            }

            MainConsole.Instance.Output("Combat team \"{0}\":", team);
            foreach (UUID member in members)
                MainConsole.Instance.Output("  {0}", member);
        }

        #endregion

        #region ITeamCombatModule Members

        public string GetTeam(UUID agentID)
        {
            lock (m_teamLock)
            {
                m_agentTeam.TryGetValue(agentID, out string team);
                return team;
            }
        }

        public List<UUID> GetTeammates(string team)
        {
            lock (m_teamLock)
            {
                if (m_teams.TryGetValue(team, out List<UUID> members))
                    return new List<UUID>(members);
                return new List<UUID>();
            }
        }

        public bool JoinTeam(UUID agentID, string team)
        {
            if (string.IsNullOrEmpty(team))
                return false;

            lock (m_teamLock)
            {
                LeaveTeamLocked(agentID);

                if (!m_teams.TryGetValue(team, out List<UUID> members))
                {
                    members = new List<UUID>();
                    m_teams[team] = members;
                }
                members.Add(agentID);
                m_agentTeam[agentID] = team;
            }

            ApplyRegenRate(agentID, m_regenerateHealthSpeed);
            return true;
        }

        public bool LeaveTeam(UUID agentID)
        {
            bool wasOnTeam;
            lock (m_teamLock)
                wasOnTeam = LeaveTeamLocked(agentID);

            if (wasOnTeam)
                RestoreRegenRate(agentID);

            return wasOnTeam;
        }

        private bool LeaveTeamLocked(UUID agentID)
        {
            if (!m_agentTeam.TryGetValue(agentID, out string team))
                return false;

            m_agentTeam.Remove(agentID);
            if (m_teams.TryGetValue(team, out List<UUID> members))
                members.Remove(agentID);

            return true;
        }

        public bool TryHandleRespawn(ScenePresence deadAvatar)
        {
            if (!m_enabled || GetTeam(deadAvatar.UUID) == null)
                return false;

            if (m_shouldRespawn)
            {
                if (m_secondsBeforeRespawn > 0)
                    StartRespawnGrace(deadAvatar);

                deadAvatar.Teleport(m_respawnPosition);
                return true;
            }

            return false;
        }

        #endregion

        private void StartRespawnGrace(ScenePresence deadAvatar)
        {
            UUID agentID = deadAvatar.UUID;

            deadAvatar.AllowMovement = false;
            lock (m_teamLock)
                m_inRespawnGrace.Add(agentID);

            Timer timer = new Timer(m_secondsBeforeRespawn * 1000) { AutoReset = false };
            timer.Elapsed += (sender, e) => EndRespawnGrace(agentID);

            lock (m_teamLock)
            {
                if (m_respawnTimers.TryGetValue(agentID, out Timer existing))
                {
                    existing.Stop();
                    existing.Close();
                }
                m_respawnTimers[agentID] = timer;
            }

            timer.Start();
        }

        private void EndRespawnGrace(UUID agentID)
        {
            lock (m_teamLock)
            {
                m_inRespawnGrace.Remove(agentID);
                if (m_respawnTimers.TryGetValue(agentID, out Timer timer))
                {
                    timer.Close();
                    m_respawnTimers.Remove(agentID);
                }
            }

            foreach (Scene scene in SnapshotScenes())
            {
                ScenePresence presence = scene.GetScenePresence(agentID);
                if (presence != null && !presence.IsDeleted)
                {
                    presence.AllowMovement = true;
                    break;
                }
            }
        }

        private void ApplyRegenRate(UUID agentID, float rate)
        {
            if (!m_regenHealth)
                return;

            foreach (Scene scene in SnapshotScenes())
            {
                ScenePresence presence = scene.GetScenePresence(agentID);
                if (presence != null && !presence.IsDeleted)
                {
                    lock (m_teamLock)
                    {
                        if (!m_savedHealRate.ContainsKey(agentID))
                            m_savedHealRate[agentID] = presence.HealRate;
                    }
                    presence.HealRate = rate;
                    break;
                }
            }
        }

        private void RestoreRegenRate(UUID agentID)
        {
            float? saved = null;
            lock (m_teamLock)
            {
                if (m_savedHealRate.TryGetValue(agentID, out float rate))
                {
                    saved = rate;
                    m_savedHealRate.Remove(agentID);
                }
            }

            if (saved is null)
                return;

            foreach (Scene scene in SnapshotScenes())
            {
                ScenePresence presence = scene.GetScenePresence(agentID);
                if (presence != null && !presence.IsDeleted)
                {
                    presence.HealRate = saved.Value;
                    break;
                }
            }
        }

        private bool CheckTeleport(UUID agentID, Scene scene)
        {
            if (!m_disallowTeleportingForCombatants)
                return true;

            lock (m_teamLock)
            {
                if (m_inRespawnGrace.Contains(agentID))
                    return true;
            }

            return GetTeam(agentID) == null;
        }

        private void OnRemovePresence(UUID agentID)
        {
            LeaveTeam(agentID);

            lock (m_teamLock)
            {
                m_inRespawnGrace.Remove(agentID);
                if (m_respawnTimers.TryGetValue(agentID, out Timer timer))
                {
                    timer.Stop();
                    timer.Close();
                    m_respawnTimers.Remove(agentID);
                }
                m_savedHealRate.Remove(agentID);
            }
        }

        private List<Scene> SnapshotScenes()
        {
            lock (m_scenes)
                return new List<Scene>(m_scenes);
        }
    }
}

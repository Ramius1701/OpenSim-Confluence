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
using System.Text;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.Avatar.Concierge
{
    // Chat commands. An avatar types, say, "/4242 who" and gets the answer
    // back privately (only their own viewer receives it). The channel is
    // [Concierge] concierge_channel; the original module read that setting
    // and never used it.
    public partial class ConciergeModule
    {
        // One command every two seconds per avatar is plenty and keeps a
        // held-down key from turning into a stream of lookups.
        private const long CommandIntervalMs = 2000;
        private readonly Dictionary<UUID, long> m_lastCommandMs = new Dictionary<UUID, long>();

        private void HandleCommand(OSChatMessage c)
        {
            if (c.Channel != m_conciergeChannel || c.Scene is not Scene scene || !m_conciergedScenes.Contains(scene))
                return;

            // Viewer chat carries its sender as Sender (the client); SenderUUID
            // is only filled in for chat that comes from the world, so it is
            // empty here. Only avatars can ask, never objects.
            if (c.Sender == null)
                return;

            if (!scene.TryGetScenePresence(c.Sender.AgentId, out ScenePresence agent) || agent.IsChildAgent)
                return;

            if (m_content.Peek(scene.RegionInfo.RegionID).Enabled == false)
                return;

            long now = Environment.TickCount64;
            lock (m_lastCommandMs)
            {
                if (m_lastCommandMs.TryGetValue(agent.UUID, out long last) && now - last < CommandIntervalMs)
                    return;

                if (m_lastCommandMs.Count > 512)
                    m_lastCommandMs.Clear();

                m_lastCommandMs[agent.UUID] = now;
            }

            string verb = (c.Message ?? string.Empty).Trim().ToLowerInvariant();
            int space = verb.IndexOf(' ');
            if (space > 0)
                verb = verb.Substring(0, space);

            Task.Run(() => RunCommandAsync(scene, agent, verb));
        }

        private async Task RunCommandAsync(Scene scene, ScenePresence agent, string verb)
        {
            try
            {
                switch (verb)
                {
                    case "who":
                        Reply(agent, WhoLines(scene));
                        break;

                    case "info":
                        Reply(agent, InfoLines(scene));
                        break;

                    case "rules":
                    {
                        ConciergeContent content = await m_content.GetAsync(scene.RegionInfo.RegionID).ConfigureAwait(false);
                        Reply(agent, TextLines(scene, agent, content.Rules, "No rules have been posted for this region."));
                        break;
                    }

                    case "welcome":
                    {
                        ConciergeContent content = await m_content.GetAsync(scene.RegionInfo.RegionID).ConfigureAwait(false);
                        SendWelcome(scene, agent, Classify(scene, agent), content);
                        break;
                    }

                    case "staff":
                        Reply(agent, StaffLines(scene));
                        break;

                    default:
                        Reply(agent, HelpLines());
                        break;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[Concierge]: command \"{0}\" from {1} failed: {2}", verb, agent.Name, e.Message);
            }
        }

        private void Reply(ScenePresence agent, IEnumerable<string> lines)
        {
            foreach (string line in lines)
                AnnounceToAgent(agent, ConciergeTemplate.Limit(line));
        }

        private IEnumerable<string> HelpLines()
        {
            yield return $"I can help with: /{m_conciergeChannel} who (who is here), info (about this region), " +
                         $"rules, welcome (hear the welcome again), staff (which estate staff are online).";
        }

        // Rules text is an ordinary template: it may use {tokens} and the
        // audience sections, exactly like a welcome.
        private IEnumerable<string> TextLines(Scene scene, ScenePresence agent, string text, string whenEmpty)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield return whenEmpty;
                yield break;
            }

            Func<string, string> tokens = Tokens(scene, agent.UUID, agent.Name, agent.Firstname, false);
            foreach (string line in ConciergeTemplate.SelectLines(text, Classify(scene, agent)))
                yield return ConciergeTemplate.Expand(line, tokens);
        }

        private IEnumerable<string> WhoLines(Scene scene)
        {
            IUserManagement um = scene.RequestModuleInterface<IUserManagement>();
            var names = new List<string>();
            scene.ForEachRootScenePresence(sp =>
            {
                string name = DisplayNameOf(scene, sp.UUID, sp.Name);
                bool visitor = um != null && !um.IsLocalGridUser(sp.UUID);
                names.Add(visitor ? name + " (visitor)" : name);
            });

            names.Sort(StringComparer.OrdinalIgnoreCase);

            yield return names.Count == 1
                ? "1 person is here:"
                : $"{names.Count} people are here:";

            var line = new StringBuilder();
            foreach (string name in names)
            {
                if (line.Length > 0 && line.Length + name.Length + 2 > ConciergeTemplate.MaxLineChars)
                {
                    yield return line.ToString();
                    line.Clear();
                }

                if (line.Length > 0)
                    line.Append(", ");
                line.Append(name);
            }

            if (line.Length > 0)
                yield return line.ToString();
        }

        private static string MaturityName(byte maturity)
        {
            switch (maturity)
            {
                case 0: return "General";
                case 1: return "Moderate";
                default: return "Adult";
            }
        }

        private IEnumerable<string> InfoLines(Scene scene)
        {
            RegionInfo ri = scene.RegionInfo;
            string owner = OwnerNameOf(scene);
            string estate = ri.EstateSettings?.EstateName ?? string.Empty;

            yield return $"{ri.RegionName}: {ri.RegionSizeX}x{ri.RegionSizeY} m, {MaturityName((byte)ri.RegionSettings.Maturity)} rated, " +
                         $"{scene.GetRootAgentCount()} here now.";

            if (estate.Length > 0)
                yield return owner.Length > 0 ? $"Estate: {estate}, owned by {owner}." : $"Estate: {estate}.";

            if (m_gridName.Length > 0)
                yield return $"Grid: {m_gridName}.";
        }

        // Estate owner and managers who are online anywhere on the grid, and
        // where. Nobody is told who is offline.
        private IEnumerable<string> StaffLines(Scene scene)
        {
            var online = new List<string>();
            foreach ((UUID id, string place) in OnlineStaff(scene))
            {
                string name = DisplayNameOf(scene, id, scene.RequestModuleInterface<IUserManagement>()?.GetUserName(id) ?? id.ToString());
                online.Add(place.Length > 0 ? $"{name} (in {place})" : name);
            }

            yield return online.Count == 0
                ? "No estate staff are online right now."
                : "Estate staff online: " + string.Join(", ", online);
        }
    }
}

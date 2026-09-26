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
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using PresenceInfo = OpenSim.Services.Interfaces.PresenceInfo;

namespace OpenSim.Region.OptionalModules.Avatar.Concierge
{
    // Telling the people who run a region when somebody worth greeting
    // arrives: an instant message to each estate owner/manager who is online.
    public partial class ConciergeModule
    {
        // The same avatar arriving again within this window (a teleport out
        // and back, a flaky connection) does not notify a second time.
        private const long NotifyIntervalMs = 600_000;

        private bool m_notifyManagers = false;
        private HashSet<ConciergeAudience> m_notifyAudiences = DefaultNotifyAudiences();
        private readonly Dictionary<UUID, long> m_lastNotifiedMs = new Dictionary<UUID, long>();

        private static HashSet<ConciergeAudience> DefaultNotifyAudiences()
        {
            return new HashSet<ConciergeAudience>
            {
                ConciergeAudience.Visitor, ConciergeAudience.New, ConciergeAudience.Trial
            };
        }

        // notify_managers = true turns it on for every concierged region; the
        // web portal can override that per region. notify_audiences picks
        // who is worth an IM: any of hg, new, trial, resident, or all.
        private void ParseNotifySettings(IConfig config)
        {
            m_notifyManagers = config.GetBoolean("notify_managers", false);

            var audiences = new HashSet<ConciergeAudience>();
            string list = config.GetString("notify_audiences", "hg,new,trial");
            foreach (string raw in list.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "all":
                        audiences.Add(ConciergeAudience.Visitor);
                        audiences.Add(ConciergeAudience.New);
                        audiences.Add(ConciergeAudience.Trial);
                        audiences.Add(ConciergeAudience.Resident);
                        break;
                    case "hg":
                    case "visitor":
                        audiences.Add(ConciergeAudience.Visitor);
                        break;
                    case "new":
                        audiences.Add(ConciergeAudience.New);
                        break;
                    case "trial":
                        audiences.Add(ConciergeAudience.Trial);
                        break;
                    case "resident":
                        audiences.Add(ConciergeAudience.Resident);
                        break;
                    default:
                        m_log.WarnFormat("[Concierge]: ignoring unknown notify_audiences entry \"{0}\" (use hg, new, trial, resident or all)", raw);
                        break;
                }
            }

            m_notifyAudiences = audiences.Count > 0 ? audiences : DefaultNotifyAudiences();
        }

        private static string AudienceLabel(ConciergeAudience audience)
        {
            switch (audience)
            {
                case ConciergeAudience.Visitor: return "Hypergrid visitor";
                case ConciergeAudience.Trial: return "Trial Member";
                case ConciergeAudience.New: return "new resident";
                default: return "returning resident";
            }
        }

        // The estate owner and managers of this region's estate who are
        // logged in somewhere on the grid right now, with the region each is
        // in. Asks the grid's presence service, so someone who is offline is
        // never returned (and so never sent an offline message).
        internal List<(UUID id, string place)> OnlineStaff(Scene scene)
        {
            var result = new List<(UUID, string)>();

            EstateSettings estate = scene.RegionInfo.EstateSettings;
            if (estate == null)
                return result;

            var ids = new HashSet<UUID>(estate.EstateManagers);
            if (!estate.EstateOwner.IsZero())
                ids.Add(estate.EstateOwner);

            if (ids.Count == 0)
                return result;

            var wanted = new List<string>();
            foreach (UUID id in ids)
                wanted.Add(id.ToString());

            PresenceInfo[] sessions = scene.PresenceService?.GetAgents(wanted.ToArray());
            if (sessions == null)
                return result;

            var seen = new HashSet<UUID>();
            foreach (PresenceInfo session in sessions)
            {
                // A zero region is a login that has not landed anywhere yet.
                if (session.RegionID.IsZero())
                    continue;

                if (!UUID.TryParse(session.UserID, out UUID id) || !seen.Add(id))
                    continue;

                string place = scene.GridService?.GetRegionByUUID(scene.RegionInfo.ScopeID, session.RegionID)?.RegionName ?? string.Empty;
                result.Add((id, place));
            }

            return result;
        }

        private void NotifyManagers(Scene scene, ScenePresence agent, ConciergeAudience audience)
        {
            if (!m_notifyAudiences.Contains(audience))
                return;

            long now = Environment.TickCount64;
            lock (m_lastNotifiedMs)
            {
                if (m_lastNotifiedMs.TryGetValue(agent.UUID, out long last) && now - last < NotifyIntervalMs)
                    return;

                if (m_lastNotifiedMs.Count > 512)
                    m_lastNotifiedMs.Clear();

                m_lastNotifiedMs[agent.UUID] = now;
            }

            IMessageTransferModule transfer = scene.RequestModuleInterface<IMessageTransferModule>();
            if (transfer == null)
                return;

            List<(UUID id, string place)> staff = OnlineStaff(scene);
            if (staff.Count == 0)
                return;

            RegionInfo ri = scene.RegionInfo;
            Vector3 pos = agent.AbsolutePosition;
            string who = DisplayNameOf(scene, agent.UUID, agent.Name);
            string text = $"{who} ({AudienceLabel(audience)}) arrived in {ri.RegionName}.";
            UUID from = ri.EstateSettings?.EstateOwner ?? UUID.Zero;

            foreach ((UUID id, string _) in staff)
            {
                if (id == agent.UUID)
                    continue;

                // An object-style IM, the same shape llInstantMessage sends:
                // viewers show it as a message from a named source, and the
                // SLURL in the bucket gives the recipient a teleport link.
                GridInstantMessage msg = new()
                {
                    fromAgentID = from.Guid,
                    toAgentID = id.Guid,
                    imSessionID = ri.RegionID.Guid,
                    timestamp = (uint)Util.UnixTimeSinceEpoch(),
                    fromAgentName = $"{m_whoami} ({ri.RegionName})",
                    dialog = (byte)InstantMessageDialog.MessageFromObject,
                    fromGroup = false,
                    offline = 0,
                    ParentEstateID = ri.EstateSettings?.EstateID ?? 0,
                    Position = pos,
                    RegionID = ri.RegionID.Guid,
                    message = text,
                    binaryBucket = Util.StringToBytes256($"{ri.RegionName}/{(int)pos.X}/{(int)pos.Y}/{(int)pos.Z}")
                };

                transfer.SendInstantMessage(msg, delegate (bool success) { });
            }
        }
    }
}

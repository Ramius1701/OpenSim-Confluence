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
using System.Reflection;
using System.Timers;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.OnDemand
{
    // Ported from WhiteCore-Dev's OnDemandRegionModule (WhiteCore/Modules/World/On-Demand/) -
    // see PROJECT_LOG.md Batch 14. The original toggled a "ShouldRunHeartbeat"
    // bool plus its own WhiteCoreEventManager events; Confluence's Scene has an
    // equivalent, already-public mechanism under a different name - the
    // Active property (Active=false stops the heartbeat loop, Active=true
    // calls Start() to resume it) - so this needed no engine-level plumbing at
    // all, just the right events to hook: EventManager.OnNewPresence/
    // OnRemovePresence, both already present in vanilla-lineage OpenSim.
    //
    // Scope note: WhiteCore had three tiers (Normal/Medium/Soft, the last of
    // which skips loading prims entirely). This only ports the "Medium"
    // behavior - full normal load, but the heartbeat loop idles down to
    // nothing while the region is empty and wakes up on the first visitor -
    // since a true "Soft" tier would mean the region genuinely never fully
    // loaded until first visit, a bigger change to region startup sequencing
    // than toggling an already-existing pause/resume switch.
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "OnDemandRegionModule")]
    public class OnDemandRegionModule : INonSharedRegionModule
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private bool m_enabled;
        private int m_idleShutdownDelaySeconds;
        private Timer m_idleShutdownTimer;

        public string Name { get { return "OnDemandRegionModule"; } }

        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["OnDemand"];
            m_enabled = config != null && config.GetBoolean("Enabled", false);

            // Not in the WhiteCore original (which idled down the instant the
            // last presence left) - a short grace period avoids repeatedly
            // starting/stopping the heartbeat thread for someone teleporting
            // through several connected regions in quick succession, which
            // would cost more than it saves.
            m_idleShutdownDelaySeconds = config != null ? config.GetInt("IdleShutdownDelaySeconds", 60) : 60;
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;
            scene.EventManager.OnNewPresence += OnNewPresence;
            scene.EventManager.OnRemovePresence += OnRemovePresence;

            // Idle from the start - the normal region-load sequence still
            // calls Scene.Start() once during boot (not worth fighting that
            // timing), but immediately pausing it here means an empty region
            // isn't burning a heartbeat thread indefinitely afterward.
            scene.Active = false;
            m_log.InfoFormat("[ON DEMAND REGION]: {0} starting idle - heartbeat will resume on first visitor", scene.Name);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.EventManager.OnNewPresence -= OnNewPresence;
            scene.EventManager.OnRemovePresence -= OnRemovePresence;

            m_idleShutdownTimer?.Stop();
            m_idleShutdownTimer?.Dispose();
        }

        public void Close()
        {
        }

        private void OnNewPresence(ScenePresence presence)
        {
            // A pending idle-shutdown from someone who just left should be
            // cancelled if a new visitor arrives before the grace period ends.
            if (m_idleShutdownTimer != null)
            {
                m_idleShutdownTimer.Stop();
                m_idleShutdownTimer.Dispose();
                m_idleShutdownTimer = null;
            }

            if (m_scene.Active)
                return;

            m_log.InfoFormat("[ON DEMAND REGION]: {0} waking up - {1} connected", m_scene.Name, presence.Name);
            m_scene.Active = true;
        }

        // Matches WhiteCore's own check exactly: OnRemovePresence fires before
        // the leaving agent is actually stripped from GetScenePresences(), so
        // a count of 1 here means zero will be left once removal completes.
        private void OnRemovePresence(UUID agentId)
        {
            if (m_scene.GetScenePresences().Count > 1)
                return;

            m_idleShutdownTimer?.Stop();
            m_idleShutdownTimer?.Dispose();

            m_idleShutdownTimer = new Timer(m_idleShutdownDelaySeconds * 1000) { AutoReset = false };
            m_idleShutdownTimer.Elapsed += (_, _) =>
            {
                if (m_scene.GetScenePresences().Count == 0 && m_scene.Active)
                {
                    m_log.InfoFormat("[ON DEMAND REGION]: {0} idling down - no visitors for {1}s", m_scene.Name, m_idleShutdownDelaySeconds);
                    m_scene.Active = false;
                }
            };
            m_idleShutdownTimer.Start();
        }
    }
}

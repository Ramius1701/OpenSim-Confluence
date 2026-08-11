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
using System.Reflection;
using System.Timers;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Region
{
    // Ported from WhiteCore-Dev's SimProtection.cs (see PROJECT_LOG.md
    // Batch 14) - Confluence already had every real primitive this needs
    // (Scene.StatsReporter.LastReportedSimFPS/LastReportedSimStats[PhysicsFPS],
    // RegionSettings.DisableScripts/DisablePhysics/DisableCollisions,
    // ISceneCommandsModule.SetSceneDebugOptions - the same pieces the
    // in-viewer Estate "Debug" tab uses, see EstateManagementModule's
    // HandleEstateDebugRegionRequest), so this only had to wire them together
    // on a periodic check, the same shape as the WhiteCore original.
    //
    // Deliberate deviation from the original: WhiteCore's version calls
    // RegionSettings.Save() on every automatic toggle, persisting each
    // temporary mitigation to the database as if an admin had set it by
    // hand. This module does NOT persist - these are meant to be transient,
    // automatic responses to a performance dip, and a restart should come
    // back up with scripts/physics enabled regardless of what SimProtection
    // last did, not frozen in whatever state a performance blip left behind.
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "SimProtectionModule")]
    public class SimProtectionModule : INonSharedRegionModule
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private bool m_enabled;
        private bool m_allowDisableScripts = true;
        private bool m_allowDisablePhysics = true;
        private bool m_restartSimOnZeroFPS = true;
        private float m_baseRateFPS = 45;
        private float m_percentToBeginShutdown = 50;
        private float m_secondsBeforeReenable = 20;
        private float m_minutesBeforeZeroFPSRestart = 1;
        private float m_checkIntervalSeconds = 60;

        private DateTime m_disabledScriptsSince = DateTime.MinValue;
        private DateTime m_disabledPhysicsSince = DateTime.MinValue;
        private DateTime m_zeroFPSSince = DateTime.MinValue;
        private Timer m_checkTimer;

        public string Name { get { return "SimProtectionModule"; } }

        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["SimProtection"];
            m_enabled = config != null && config.GetBoolean("Enabled", false);
            if (!m_enabled)
                return;

            m_baseRateFPS = config.GetFloat("BaseRateFramesPerSecond", 45);
            m_percentToBeginShutdown = config.GetFloat("PercentToBeginShutDownOfServices", 50);
            m_secondsBeforeReenable = config.GetFloat("SecondsBeforeReenable", 20);
            m_allowDisableScripts = config.GetBoolean("AllowDisableScripts", true);
            m_allowDisablePhysics = config.GetBoolean("AllowDisablePhysics", true);
            m_restartSimOnZeroFPS = config.GetBoolean("RestartSimIfZeroFPS", true);
            m_minutesBeforeZeroFPSRestart = config.GetFloat("MinutesBeforeZeroFPSRestart", 1);
            m_checkIntervalSeconds = config.GetFloat("CheckIntervalSeconds", 60);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            if (m_scene.StatsReporter == null)
            {
                m_log.Warn("[SIM PROTECTION]: Cannot be used - no SimStatsReporter on this scene.");
                return;
            }

            m_checkTimer = new Timer(m_checkIntervalSeconds * 1000) { AutoReset = true };
            m_checkTimer.Elapsed += OnCheck;
            m_checkTimer.Start();
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_checkTimer?.Stop();
            m_checkTimer?.Dispose();
            m_checkTimer = null;
        }

        public void Close()
        {
        }

        private void OnCheck(object sender, ElapsedEventArgs e)
        {
            try
            {
                CheckZeroFPS();
                CheckScripts();
                CheckPhysics();
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[SIM PROTECTION]: Error during check: {0}", ex.Message);
            }
        }

        private void CheckZeroFPS()
        {
            if (!m_restartSimOnZeroFPS)
                return;

            float fps = m_scene.StatsReporter.LastReportedSimFPS;
            if (fps <= 0.5f)
            {
                if (m_zeroFPSSince == DateTime.MinValue)
                {
                    m_zeroFPSSince = DateTime.Now;
                    m_log.WarnFormat("[SIM PROTECTION]: {0} reporting ~0 FPS - will restart in {1} minute(s) if this persists",
                            m_scene.Name, m_minutesBeforeZeroFPSRestart);
                }
                else if (DateTime.Now > m_zeroFPSSince.AddMinutes(m_minutesBeforeZeroFPSRestart))
                {
                    m_log.ErrorFormat("[SIM PROTECTION]: {0} has been at ~0 FPS for over {1} minute(s) - restarting",
                            m_scene.Name, m_minutesBeforeZeroFPSRestart);
                    MainConsole.Instance.RunCommand("shutdown");
                }
            }
            else
            {
                m_zeroFPSSince = DateTime.MinValue;
            }
        }

        private void CheckScripts()
        {
            if (!m_allowDisableScripts)
                return;

            float fps = m_scene.StatsReporter.LastReportedSimFPS;
            float threshold = m_baseRateFPS * (m_percentToBeginShutdown / 100f);

            if (fps > 0 && fps < threshold && !m_scene.RegionInfo.RegionSettings.DisableScripts)
            {
                m_log.WarnFormat("[SIM PROTECTION]: {0} FPS ({1:0.0}) below threshold ({2:0.0}) - disabling scripts",
                        m_scene.Name, fps, threshold);
                SetDebug(true, m_scene.RegionInfo.RegionSettings.DisableCollisions, m_scene.RegionInfo.RegionSettings.DisablePhysics);
                m_disabledScriptsSince = DateTime.Now;
            }
            else if (m_scene.RegionInfo.RegionSettings.DisableScripts
                    && m_disabledScriptsSince != DateTime.MinValue
                    && DateTime.Now > m_disabledScriptsSince.AddSeconds(m_secondsBeforeReenable))
            {
                m_log.InfoFormat("[SIM PROTECTION]: {0} re-enabling scripts", m_scene.Name);
                SetDebug(false, m_scene.RegionInfo.RegionSettings.DisableCollisions, m_scene.RegionInfo.RegionSettings.DisablePhysics);
                m_disabledScriptsSince = DateTime.MinValue;
            }
        }

        private void CheckPhysics()
        {
            if (!m_allowDisablePhysics)
                return;

            float[] stats = m_scene.StatsReporter.LastReportedSimStats;
            if (stats == null || stats.Length <= (int)StatsIndex.PhysicsFPS)
                return;

            float physicsFps = stats[(int)StatsIndex.PhysicsFPS];
            float threshold = m_baseRateFPS * (m_percentToBeginShutdown / 100f);

            if (physicsFps > 0.5f && physicsFps < threshold && !m_scene.RegionInfo.RegionSettings.DisablePhysics)
            {
                m_log.WarnFormat("[SIM PROTECTION]: {0} physics FPS ({1:0.0}) below threshold ({2:0.0}) - disabling physics",
                        m_scene.Name, physicsFps, threshold);
                SetDebug(m_scene.RegionInfo.RegionSettings.DisableScripts, m_scene.RegionInfo.RegionSettings.DisableCollisions, true);
                m_disabledPhysicsSince = DateTime.Now;
            }
            else if (m_scene.RegionInfo.RegionSettings.DisablePhysics
                    && m_disabledPhysicsSince != DateTime.MinValue
                    && DateTime.Now > m_disabledPhysicsSince.AddSeconds(m_secondsBeforeReenable))
            {
                m_log.InfoFormat("[SIM PROTECTION]: {0} re-enabling physics", m_scene.Name);
                SetDebug(m_scene.RegionInfo.RegionSettings.DisableScripts, m_scene.RegionInfo.RegionSettings.DisableCollisions, false);
                m_disabledPhysicsSince = DateTime.MinValue;
            }
        }

        // Mirrors EstateManagementModule.HandleEstateDebugRegionRequest's two
        // real effects (in-memory RegionSettings flags + the live scene's
        // actual debug options) without its RegionSettings.Save() - see the
        // class-level comment on why persistence is deliberately skipped here.
        private void SetDebug(bool disableScripts, bool disableCollisions, bool disablePhysics)
        {
            m_scene.RegionInfo.RegionSettings.DisableScripts = disableScripts;
            m_scene.RegionInfo.RegionSettings.DisableCollisions = disableCollisions;
            m_scene.RegionInfo.RegionSettings.DisablePhysics = disablePhysics;

            ISceneCommandsModule scm = m_scene.RequestModuleInterface<ISceneCommandsModule>();
            scm?.SetSceneDebugOptions(new Dictionary<string, string>
            {
                { "scripting", (!disableScripts).ToString() },
                { "collisions", (!disableCollisions).ToString() },
                { "physics", (!disablePhysics).ToString() }
            });
        }
    }
}

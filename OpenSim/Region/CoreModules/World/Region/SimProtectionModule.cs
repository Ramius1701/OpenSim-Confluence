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
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Timer = System.Timers.Timer;

namespace OpenSim.Region.CoreModules.World.Region
{
    /// <summary>
    /// Watches region FPS and progressively disables scripts, then physics, when it drops
    /// too far below a configured baseline for too long, restoring them once FPS recovers.
    /// Optionally schedules a region restart if FPS stays near zero for an extended period.
    /// Disabled by default - opt in via the [SimProtection] config section.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "SimProtectionModule")]
    public class SimProtectionModule : INonSharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private Timer m_checkTimer;

        private bool m_enabled;
        private float m_baseFPS = 45f;
        private float m_shutdownScriptsPercent = 20f;
        private float m_shutdownPhysicsPercent = 10f;
        private float m_recoverPercent = 60f;
        private int m_checkIntervalSeconds = 15;
        private int m_killTimerSeconds = 300;
        private int m_restartWarningSeconds = 30;

        private bool m_scriptsShutdown;
        private bool m_physicsShutdown;
        private int m_secondsBelowKillThreshold;

        public string Name => "SimProtectionModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["SimProtection"];
            if (config is null)
                return;

            m_enabled = config.GetBoolean("Enabled", false);
            if (!m_enabled)
                return;

            m_baseFPS = config.GetFloat("BaseRateFramesPerSecond", m_baseFPS);
            m_shutdownScriptsPercent = config.GetFloat("ShutdownScriptsPercent", m_shutdownScriptsPercent);
            m_shutdownPhysicsPercent = config.GetFloat("ShutdownPhysicsPercent", m_shutdownPhysicsPercent);
            m_recoverPercent = config.GetFloat("RecoverPercent", m_recoverPercent);
            m_checkIntervalSeconds = config.GetInt("CheckIntervalSeconds", m_checkIntervalSeconds);
            m_killTimerSeconds = config.GetInt("KillTimerSeconds", m_killTimerSeconds);
            m_restartWarningSeconds = config.GetInt("RestartWarningSeconds", m_restartWarningSeconds);
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

            m_checkTimer = new Timer(m_checkIntervalSeconds * 1000d);
            m_checkTimer.Elapsed += CheckFPS;
            m_checkTimer.AutoReset = true;
            m_checkTimer.Start();

            m_log.InfoFormat("[SIM PROTECTION]: Enabled for region {0}, base {1} FPS, checking every {2}s",
                scene.Name, m_baseFPS, m_checkIntervalSeconds);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_checkTimer?.Stop();
            m_checkTimer?.Dispose();
            m_checkTimer = null;
        }

        public void Close() { }

        private void CheckFPS(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (m_scene is null || m_scene.StatsReporter is null)
                return;

            float fps = m_scene.StatsReporter.LastReportedSimFPS;
            float percent = m_baseFPS > 0 ? (fps / m_baseFPS) * 100f : 100f;

            if (percent <= 0.5f)
            {
                m_secondsBelowKillThreshold += m_checkIntervalSeconds;
                if (m_killTimerSeconds > 0 && m_secondsBelowKillThreshold >= m_killTimerSeconds)
                {
                    TryScheduleRestart();
                    return;
                }
            }
            else
            {
                m_secondsBelowKillThreshold = 0;
            }

            if (percent < m_shutdownPhysicsPercent)
            {
                if (!m_physicsShutdown)
                {
                    m_log.WarnFormat("[SIM PROTECTION]: {0} FPS is {1:0}% of baseline, disabling physics",
                        m_scene.Name, percent);
                    m_scene.PhysicsEnabled = false;
                    m_physicsShutdown = true;
                }
                if (!m_scriptsShutdown)
                {
                    m_log.WarnFormat("[SIM PROTECTION]: {0} FPS is {1:0}% of baseline, disabling scripts", m_scene.Name, percent);
                    m_scene.ScriptsEnabled = false;
                    m_scriptsShutdown = true;
                }
            }
            else if (percent < m_shutdownScriptsPercent)
            {
                if (!m_scriptsShutdown)
                {
                    m_log.WarnFormat("[SIM PROTECTION]: {0} FPS is {1:0}% of baseline, disabling scripts", m_scene.Name, percent);
                    m_scene.ScriptsEnabled = false;
                    m_scriptsShutdown = true;
                }
            }
            else if (percent >= m_recoverPercent)
            {
                if (m_physicsShutdown)
                {
                    m_log.InfoFormat("[SIM PROTECTION]: {0} FPS recovered to {1:0}% of baseline, re-enabling physics",
                        m_scene.Name, percent);
                    m_scene.PhysicsEnabled = true;
                    m_physicsShutdown = false;
                }
                if (m_scriptsShutdown)
                {
                    m_log.InfoFormat("[SIM PROTECTION]: {0} FPS recovered to {1:0}% of baseline, re-enabling scripts",
                        m_scene.Name, percent);
                    m_scene.ScriptsEnabled = true;
                    m_scriptsShutdown = false;
                }
            }
        }

        private void TryScheduleRestart()
        {
            m_secondsBelowKillThreshold = 0;

            IRestartModule restartModule = m_scene.RequestModuleInterface<IRestartModule>();
            if (restartModule is null)
                return;

            m_log.ErrorFormat("[SIM PROTECTION]: {0} FPS has been near zero for {1}s, scheduling a restart in {2}s",
                m_scene.Name, m_killTimerSeconds, m_restartWarningSeconds);
            restartModule.ScheduleRestart(UUID.Zero, m_restartWarningSeconds);
        }
    }
}

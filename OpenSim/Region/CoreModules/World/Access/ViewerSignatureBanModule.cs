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
using System.IO;
using System.Net.Http;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Access
{
    // Ported from WhiteCore-Dev's GridWideViewerBan.cs (see PROJECT_LOG.md
    // Batch 14) - complements Confluence's existing [LoginService]
    // AllowedClients/DeniedClients regex check (LLLoginService.cs), which
    // only looks at what the viewer SELF-REPORTS at login. This instead
    // inspects an avatar's baked appearance texture for a known "signature"
    // texture ID that certain modified/griefer viewers bake in automatically -
    // a viewer can lie about its reported version string, but can't as easily
    // fake this. The two checks are complementary, not redundant: one closes
    // the login-time gap, this one catches spoofed-identity viewers already
    // in-world.
    //
    // The viewer/texture-ID map is a third-party maintained public resource
    // (the same one WhiteCore's original used) - fetched once and cached for
    // the life of the process. Disabled by default: this is an opt-in
    // anti-griefer tool, not something every grid needs.
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ViewerSignatureBanModule")]
    public class ViewerSignatureBanModule : INonSharedRegionModule
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private bool m_enabled;
        private List<string> m_bannedViewers = new List<string>();
        private List<string> m_allowedViewers = new List<string>();
        private bool m_useAllowList;
        private string m_viewerTagURL = "http://phoenixviewer.com/app/client_list.xml";
        private OSDMap m_viewerTagMap;
        private readonly object m_fetchLock = new object();

        public string Name { get { return "ViewerSignatureBanModule"; } }

        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["GrieferProtection"];
            m_enabled = config != null && config.GetBoolean("ViewerSignatureBanEnabled", false);
            if (!m_enabled)
                return;

            m_bannedViewers = new List<string>(config.GetString("ViewersToBan", string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            m_allowedViewers = new List<string>(config.GetString("ViewersToAllow", string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            m_useAllowList = config.GetBoolean("UseAllowListInsteadOfBanList", false);
            m_viewerTagURL = config.GetString("ViewerXMLURL", m_viewerTagURL);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;
            scene.EventManager.OnAvatarAppearanceChange += OnAvatarAppearanceChange;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.EventManager.OnAvatarAppearanceChange -= OnAvatarAppearanceChange;
        }

        public void Close()
        {
        }

        private void OnAvatarAppearanceChange(ScenePresence presence)
        {
            try
            {
                OSDMap map = GetViewerTagMap();
                if (map == null)
                    return;

                Primitive.TextureEntryFace[] faces = presence.Appearance?.Texture?.FaceTextures;
                if (faces == null)
                    return;

                foreach (Primitive.TextureEntryFace face in faces)
                {
                    if (face == null)
                        continue;

                    string textureId = face.TextureID.ToString();
                    if (!map.ContainsKey(textureId))
                        continue;

                    OSDMap viewerInfo = (OSDMap)map[textureId];
                    string viewerName = viewerInfo["name"].AsString();
                    if (!IsViewerBanned(viewerName))
                        break;

                    m_log.InfoFormat("[VIEWER SIGNATURE BAN]: Kicking {0} - detected banned viewer {1} via signature texture",
                            presence.Name, viewerName);
                    presence.ControllingClient.Kick("You cannot use " + viewerName + " on this grid.");
                    Util.FireAndForget(_ => m_scene.CloseAgent(presence.UUID, false));
                    break;
                }
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[VIEWER SIGNATURE BAN]: Error checking appearance for {0}: {1}", presence.Name, e.Message);
            }
        }

        private bool IsViewerBanned(string name)
        {
            return m_useAllowList ? !m_allowedViewers.Contains(name) : m_bannedViewers.Contains(name);
        }

        // Fetched once per process (matching the WhiteCore original) - an
        // anti-griefer signature list doesn't change often enough to justify
        // refetching per-avatar or per-region.
        private OSDMap GetViewerTagMap()
        {
            if (m_viewerTagMap != null)
                return m_viewerTagMap;

            lock (m_fetchLock)
            {
                if (m_viewerTagMap != null)
                    return m_viewerTagMap;

                try
                {
                    using HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    string xml = client.GetStringAsync(m_viewerTagURL).GetAwaiter().GetResult();
                    m_viewerTagMap = OSDParser.Deserialize(xml) as OSDMap;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[VIEWER SIGNATURE BAN]: Could not fetch viewer signature list from {0}: {1}",
                            m_viewerTagURL, e.Message);
                }

                return m_viewerTagMap;
            }
        }
    }
}

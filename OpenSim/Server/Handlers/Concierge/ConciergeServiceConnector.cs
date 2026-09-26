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
using System.Net;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers.Concierge
{
    /// <summary>
    /// Where each concierge setting lives in the grid settings table. The web
    /// portal writes them and <see cref="ConciergeServiceConnector"/> reads
    /// them, so both must agree on the key names.
    ///
    /// A key is a kind plus a scope: a region's UUID for that region alone,
    /// or "default" for every region that has not set its own. Keys must fit
    /// the table's 64-character limit; the longest here is 55.
    /// </summary>
    public static class ConciergeSettingKeys
    {
        public const string Default = "default";

        public const string KindWelcome = "welcome";
        public const string KindRules = "rules";
        public const string KindEnabled = "enabled";
        public const string KindAnnounce = "announce";
        public const string KindNotify = "notify";

        public static string Key(string kind, string scope)
        {
            return "concierge." + kind + "." + scope;
        }

        public static string Key(string kind, UUID regionID)
        {
            return Key(kind, regionID.ToString());
        }

        /// <summary>
        /// The value for a region: its own setting if it has one, else the
        /// grid-wide default, else empty.
        /// </summary>
        public static string Resolve(IGridSettingsService settings, string kind, UUID regionID)
        {
            string own = settings.Get(Key(kind, regionID));
            if (!string.IsNullOrEmpty(own))
                return own;

            return settings.Get(Key(kind, Default)) ?? string.Empty;
        }

        /// <summary>The switches are stored as "true"/"false"; anything else is unset.</summary>
        public static string NormalizeFlag(string value)
        {
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                return "true";
            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                return "false";
            return string.Empty;
        }
    }

    /// <summary>
    /// Serves a region's concierge content to the region: <c>GET
    /// /concierge/&lt;region-uuid&gt;</c> returns a small JSON object with the
    /// welcome and rules text and the per-region switches an estate owner set
    /// in the web portal. Only concierge keys are ever read, so nothing else
    /// in the grid settings table is reachable through it. The content is what
    /// every visitor is shown in-world anyway, so it needs no credentials.
    /// </summary>
    public class ConciergeServiceConnector : ServiceConnector
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public ConciergeServiceConnector(IConfigSource config, IHttpServer server, string configName) :
                base(config, server, configName)
        {
            IConfig section = config.Configs["GridSettingsService"];
            string module = section?.GetString("LocalServiceModule", string.Empty) ?? string.Empty;
            if (string.IsNullOrEmpty(module))
            {
                m_log.Warn("[CONCIERGE SERVICE]: no [GridSettingsService] LocalServiceModule - region welcome content is unavailable");
                return;
            }

            IGridSettingsService settings = ServerUtils.LoadPlugin<IGridSettingsService>(module, new object[] { config });
            if (settings == null)
            {
                m_log.Error("[CONCIERGE SERVICE]: could not load the grid settings service - region welcome content is unavailable");
                return;
            }

            server.AddSimpleStreamHandler(new ConciergeSimpleHandler(settings), true);
        }
    }

    public class ConciergeSimpleHandler : SimpleStreamHandler
    {
        private readonly IGridSettingsService m_settings;

        public ConciergeSimpleHandler(IGridSettingsService settings) : base("/concierge")
        {
            m_settings = settings;
        }

        protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            if (httpRequest.HttpMethod != "GET")
            {
                httpResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            // "/concierge/<region-uuid>"
            string path = httpRequest.UriPath ?? string.Empty;
            int slash = path.LastIndexOf('/');
            if (slash < 0 || !UUID.TryParse(path.Substring(slash + 1), out UUID regionID) || regionID.IsZero())
            {
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            httpResponse.ContentType = "application/json";
            httpResponse.RawBuffer = Util.UTF8.GetBytes(OSDParser.SerializeJsonString(Build(regionID)));
        }

        public OSDMap Build(UUID regionID)
        {
            var map = new OSDMap();
            map["welcome"] = OSD.FromString(ConciergeSettingKeys.Resolve(m_settings, ConciergeSettingKeys.KindWelcome, regionID));
            map["rules"] = OSD.FromString(ConciergeSettingKeys.Resolve(m_settings, ConciergeSettingKeys.KindRules, regionID));

            // The name people know the grid by, so {grid} in a greeting needs
            // no extra setting on any region.
            map["grid"] = OSD.FromString(m_settings.Get("GridName") ?? string.Empty);

            // A switch nobody has set is left out, so the region falls back
            // to its own ini default rather than being told "false".
            foreach (string kind in new[] { ConciergeSettingKeys.KindEnabled, ConciergeSettingKeys.KindAnnounce, ConciergeSettingKeys.KindNotify })
            {
                string flag = ConciergeSettingKeys.NormalizeFlag(ConciergeSettingKeys.Resolve(m_settings, kind, regionID));
                if (flag.Length > 0)
                    map[kind] = OSD.FromString(flag);
            }

            return map;
        }
    }
}

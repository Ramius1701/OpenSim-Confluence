/*
 * Copyright (c) Legion Grid Contributors
 * AvatarRenderInfoModule.cs -- the viewer's "AvatarRenderInfo" capability,
 * used by Firestorm's LLAvatarRenderInfoAccountant (llavatarrenderinfoaccountant.cpp)
 * to exchange avatar visual-complexity ("render weight") data with a region.
 *
 * Viewers periodically POST the render weight they've locally computed for
 * every avatar they can see on this region, and periodically GET back the
 * aggregated table plus the region's configured complexity thresholds (used
 * to drive the "this region wants you to reduce complexity" notification
 * and the jellydoll-rendering decision, both entirely client-side). The
 * server is a passive aggregator here -- it doesn't compute complexity
 * itself and doesn't decide who gets jellydolled, exactly like
 * ModifyRegionModule.cs doesn't interpret the glTF override blobs it relays.
 *
 * Exact wire shape confirmed directly against Firestorm's own source
 * (llavatarrenderinfoaccountant.cpp's KEY_* constants), not the more
 * commonly-guessed underscored key names:
 *   POST {"agents": {agent_id: {"weight": int, "tooComplex": bool}}}
 *   GET  {"agents": {agent_id: {"weight": int}}, "reportinglimit": int, "overlimit": int}
 */

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using log4net;
using Mono.Addins;
using Nini.Config;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.Avatar.AvatarFactory
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "AvatarRenderInfoModule")]
    public class AvatarRenderInfoModule : INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private bool m_enabled = true;
        private int m_reportingLimit = 200000;
        private int m_overLimit = 350000;

        private struct AgentRenderInfo
        {
            public int Weight;
            public bool TooComplex;
        }

        private readonly ConcurrentDictionary<UUID, AgentRenderInfo> m_agentInfo = new();

        public string Name => "AvatarRenderInfoModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ClientStack.LindenCaps"];
            if (config is not null)
            {
                m_enabled = config.GetBoolean("enable_avatar_render_info_cap", m_enabled);
                m_reportingLimit = config.GetInt("avatar_render_reporting_limit", m_reportingLimit);
                m_overLimit = config.GetInt("avatar_render_over_limit", m_overLimit);
            }
        }

        public void Close() { }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;
            m_scene = scene;
            m_scene.EventManager.OnRegisterCaps += OnRegisterCaps;
            m_scene.EventManager.OnRemovePresence += OnRemovePresence;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;
            scene.EventManager.OnRegisterCaps -= OnRegisterCaps;
            scene.EventManager.OnRemovePresence -= OnRemovePresence;
        }

        public void RegionLoaded(Scene scene) { }

        private void OnRemovePresence(UUID agentId)
        {
            m_agentInfo.TryRemove(agentId, out _);
        }

        private void OnRegisterCaps(UUID agentID, OpenSim.Framework.Capabilities.Caps caps)
        {
            caps.RegisterSimpleHandler("AvatarRenderInfo",
                new SimpleStreamHandler("/" + UUID.Random(), HandleRequest));
        }

        private void HandleRequest(IOSHttpRequest request, IOSHttpResponse response)
        {
            switch (request.HttpMethod)
            {
                case "GET":
                    HandleGet(response);
                    break;
                case "POST":
                    HandlePost(request, response);
                    break;
                default:
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    break;
            }
        }

        private void HandleGet(IOSHttpResponse response)
        {
            OSDMap agents = new();
            foreach (var kvp in m_agentInfo)
                agents[kvp.Key.ToString()] = new OSDMap { ["weight"] = kvp.Value.Weight };

            OSDMap resp = new()
            {
                ["agents"] = agents,
                ["reportinglimit"] = m_reportingLimit,
                ["overlimit"] = m_overLimit
            };
            response.RawBuffer = Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(resp));
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        private void HandlePost(IOSHttpRequest request, IOSHttpResponse response)
        {
            OSDMap req;
            try
            {
                req = (OSDMap)OSDParser.DeserializeLLSDXml(request.InputStream);
            }
            catch
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                return;
            }

            if (req is not null && req.TryGetValue("agents", out OSD agentsOsd) && agentsOsd is OSDMap agentsMap)
            {
                foreach (var key in agentsMap.Keys)
                {
                    if (!UUID.TryParse(key, out UUID agentId) || agentsMap[key] is not OSDMap info)
                        continue;

                    m_agentInfo[agentId] = new AgentRenderInfo
                    {
                        Weight = info.TryGetValue("weight", out OSD w) ? w.AsInteger() : 0,
                        TooComplex = info.TryGetValue("tooComplex", out OSD tc) && tc.AsBoolean()
                    };
                }
            }

            response.RawBuffer = Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(new OSDMap()));
            response.StatusCode = (int)HttpStatusCode.OK;
        }
    }
}

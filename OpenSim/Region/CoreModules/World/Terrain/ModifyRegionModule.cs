/*
 * Copyright (c) Legion Grid Contributors
 * ModifyRegionModule.cs -- the viewer's "ModifyRegion" capability, used by
 * PBR-capable viewers (Firestorm's LLPBRTerrainFeatures) to query/apply the
 * per-slot glTF material *override* (tiling/scale/rotation/offset and other
 * override fields) for each of the region's 4 terrain PBR material slots.
 *
 * This capability only ever deals with the override layer. Which glTF
 * material asset occupies each of the 4 slots -- and the elevation bands
 * they cover -- is a separate, already-working mechanism inherited from
 * upstream OpenSim: RegionSettings.TerrainPBR1-4, delivered to PBR-capable
 * viewers on every region handshake (LLClientView.cs). The server never
 * interprets the override LLSD it stores here; it only persists exactly
 * what a POST body carries and relays it back verbatim on GET, matching the
 * viewer's own LLModifyRegion contract (a single virtual
 * getMaterialOverride(S32 asset) accessor -- confirmed against Firestorm's
 * llpbrterrainfeatures.cpp/llvlcomposition.h).
 */

using System;
using System.Net;
using System.Reflection;
using System.Text;
using log4net;
using Mono.Addins;
using Nini.Config;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Terrain
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ModifyRegionModule")]
    public class ModifyRegionModule : INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Matches LLTerrainMaterials::ASSET_COUNT (llvlcomposition.h) -- one override slot per
        // TerrainPBR1-4 / TerrainTexture1-4 band, the same 4-slot shape the classic terrain
        // texture system and its PBR-keyed successor already use throughout this codebase.
        public const int ASSET_COUNT = 4;

        private Scene m_scene;
        private bool m_enabled = true;

        public string Name => "ModifyRegionModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["Terrain"];
            if (config is not null)
                m_enabled = config.GetBoolean("enable_modify_region_cap", m_enabled);
        }

        public void Close() { }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;
            m_scene = scene;
            m_scene.EventManager.OnRegisterCaps += OnRegisterCaps;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;
            m_scene.EventManager.OnRegisterCaps -= OnRegisterCaps;
        }

        public void RegionLoaded(Scene scene) { }

        private void OnRegisterCaps(UUID agentID, OpenSim.Framework.Capabilities.Caps caps)
        {
            caps.RegisterSimpleHandler("ModifyRegion",
                new SimpleStreamHandler("/" + UUID.Random(),
                    (httpRequest, httpResponse) => HandleRequest(httpRequest, httpResponse, agentID)));
        }

        private void HandleRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
        {
            switch (request.HttpMethod)
            {
                case "GET":
                    HandleGet(response);
                    break;
                case "POST":
                    HandlePost(request, response, agentID);
                    break;
                default:
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    break;
            }
        }

        // Reads the stored override array, defaulting any missing/corrupt/wrong-length entry to
        // 4 empty maps (LLModifyRegion's "no override applied" value for every slot).
        private OSDArray LoadOverrides()
        {
            string stored = m_scene.RegionInfo.RegionSettings.TerrainPBROverrides;
            if (!string.IsNullOrEmpty(stored))
            {
                try
                {
                    if (OSDParser.DeserializeLLSDXml(stored) is OSDArray arr && arr.Count == ASSET_COUNT)
                        return arr;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[ModifyRegion]: failed to decode stored terrain overrides for {0}: {1}",
                        m_scene.RegionInfo.RegionName, e.Message);
                }
            }

            OSDArray defaults = new(ASSET_COUNT);
            for (int i = 0; i < ASSET_COUNT; i++)
                defaults.Add(new OSDMap());
            return defaults;
        }

        private void HandleGet(IOSHttpResponse response)
        {
            OSDMap resp = new()
            {
                ["success"] = true,
                ["overrides"] = LoadOverrides()
            };
            response.RawBuffer = Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(resp));
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        private void HandlePost(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
        {
            // Same trust level as the rest of terrain/estate editing (SetEstateTerrainTextures,
            // the classic "texturedetail" estate-message path): estate managers/owners only.
            if (!m_scene.Permissions.CanIssueEstateCommand(agentID, false))
            {
                WriteError(response, "You do not have permission to edit this region's terrain.");
                return;
            }

            OSDMap req;
            try
            {
                req = (OSDMap)OSDParser.DeserializeLLSDXml(request.InputStream);
            }
            catch
            {
                WriteError(response, "Malformed request body.");
                return;
            }

            if (req is null || !req.TryGetValue("overrides", out OSD ovOsd) ||
                ovOsd is not OSDArray ovArr || ovArr.Count != ASSET_COUNT)
            {
                WriteError(response, $"'overrides' must be a list of {ASSET_COUNT} entries.");
                return;
            }

            // Stored verbatim -- the server doesn't parse glTF override fields, only persists and
            // relays them, so a client always gets back exactly what it last POSTed.
            m_scene.RegionInfo.RegionSettings.TerrainPBROverrides = OSDParser.SerializeLLSDXmlString(ovArr);
            m_scene.RegionInfo.RegionSettings.Save();

            OSDMap resp = new() { ["success"] = true };
            response.RawBuffer = Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(resp));
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        private static void WriteError(IOSHttpResponse response, string message)
        {
            OSDMap resp = new() { ["success"] = false, ["message"] = message };
            response.RawBuffer = Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(resp));
            response.StatusCode = (int)HttpStatusCode.OK;
        }
    }
}

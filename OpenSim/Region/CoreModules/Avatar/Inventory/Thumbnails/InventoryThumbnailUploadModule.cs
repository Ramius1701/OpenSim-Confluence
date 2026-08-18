/*
 * Copyright (c) Legion Grid Contributors
 * InventoryThumbnailUploadModule.cs -- the viewer's "InventoryThumbnailUpload"
 * capability, used by Firestorm's snapshot-to-inventory floater (and any
 * other OPENSIM-build viewer following the same protocol) to set a custom
 * thumbnail image on an inventory item or folder.
 *
 * Two-phase upload, matching Firestorm's post_thumbnail_image_coro /
 * uploadImageUploadFile (llfloatersimplesnapshot.cpp):
 *   1. POST {item_id: uuid} or {category_id: uuid} to this capability.
 *      Response: {uploader: <one-time-url>}
 *   2. POST raw JPEG2000 bytes to the uploader url.
 *      Response: {state: "complete", new_asset: uuid} or {state: "failed", message: ...}
 *
 * Only agent (root) inventory is in scope -- the item/folder must be owned
 * by the requesting agent. Task (prim) inventory thumbnails and the AIS3
 * "set to existing texture"/"clear thumbnail" flows are not implemented:
 * AIS3 is off by default on OpenSim-flagged Firestorm builds, and there is
 * no legacy UDP path for either operation.
 */

using System;
using System.Net;
using System.Reflection;
using System.Text;
using System.Timers;
using log4net;
using Mono.Addins;
using Nini.Config;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.Avatar.Inventory.Thumbnails
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "InventoryThumbnailUploadModule")]
    public class InventoryThumbnailUploadModule : INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const int UPLOADER_TIMEOUT_MS = 120000;

        private Scene m_scene;
        private bool m_enabled = true;

        public string Name => "InventoryThumbnailUploadModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ClientStack.LindenCaps"];
            if (config is not null)
                m_enabled = config.GetBoolean("enable_inventory_thumbnail_cap", m_enabled);
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
            caps.RegisterSimpleHandler("InventoryThumbnailUpload",
                new SimpleStreamHandler("/" + UUID.Random(),
                    (httpRequest, httpResponse) => HandleRequest(httpRequest, httpResponse, agentID, caps)));
        }

        private void HandleRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID,
            OpenSim.Framework.Capabilities.Caps caps)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
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

            if (req is null)
            {
                WriteError(response, "Malformed request body.");
                return;
            }

            if (req.ContainsKey("task_id"))
            {
                WriteError(response, "Task inventory thumbnails are not supported.");
                return;
            }

            UUID itemId = UUID.Zero;
            UUID categoryId = UUID.Zero;

            if (req.TryGetValue("item_id", out OSD itemOsd))
            {
                itemId = itemOsd.AsUUID();
                InventoryItemBase item = m_scene.InventoryService.GetItem(agentID, itemId);
                if (item is null || item.Owner != agentID)
                {
                    WriteError(response, "No permission to set a thumbnail on this item.");
                    return;
                }
            }
            else if (req.TryGetValue("category_id", out OSD catOsd))
            {
                categoryId = catOsd.AsUUID();
                InventoryFolderBase folder = m_scene.InventoryService.GetFolder(agentID, categoryId);
                if (folder is null || folder.Owner != agentID)
                {
                    WriteError(response, "No permission to set a thumbnail on this folder.");
                    return;
                }
            }
            else
            {
                WriteError(response, "Request must contain 'item_id' or 'category_id'.");
                return;
            }

            string uploaderPath = "/" + UUID.Random();
            ThumbnailUploader uploader = new(agentID, itemId, categoryId, caps.HttpListener, uploaderPath, m_scene);

            caps.HttpListener.AddStreamHandler(
                new BinaryStreamHandler("POST", uploaderPath, uploader.HandleUpload,
                    "InventoryThumbnailUpload", agentID.ToString()));

            string protocol = caps.SSLCaps ? "https://" : "http://";
            string uploaderURL = protocol + caps.HostName + ":" + caps.Port + uploaderPath;

            OSDMap resp = new()
            {
                ["uploader"] = uploaderURL,
                ["state"] = "upload"
            };
            response.RawBuffer = Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(resp));
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        private static void WriteError(IOSHttpResponse response, string message)
        {
            OSDMap resp = new() { ["state"] = "error", ["message"] = message };
            response.RawBuffer = Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(resp));
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        // Handles the one-time second-phase POST of raw JPEG2000 bytes to the uploader url
        // minted above, mirroring BunchOfCaps.AssetUploader's single-use-handler pattern.
        private class ThumbnailUploader
        {
            private readonly UUID m_agentID;
            private readonly UUID m_itemId;
            private readonly UUID m_categoryId;
            private readonly IHttpServer m_httpListener;
            private readonly string m_uploaderPath;
            private readonly Scene m_scene;
            private readonly System.Timers.Timer m_timeoutTimer;

            public ThumbnailUploader(UUID agentID, UUID itemId, UUID categoryId, IHttpServer httpListener,
                string uploaderPath, Scene scene)
            {
                m_agentID = agentID;
                m_itemId = itemId;
                m_categoryId = categoryId;
                m_httpListener = httpListener;
                m_uploaderPath = uploaderPath;
                m_scene = scene;

                m_timeoutTimer = new System.Timers.Timer(UPLOADER_TIMEOUT_MS) { AutoReset = false };
                m_timeoutTimer.Elapsed += TimedOut;
                m_timeoutTimer.Start();
            }

            private void TimedOut(object sender, ElapsedEventArgs args)
            {
                m_httpListener.RemoveStreamHandler("POST", m_uploaderPath);
            }

            public string HandleUpload(byte[] data, string path, string param)
            {
                m_timeoutTimer.Stop();
                m_httpListener.RemoveStreamHandler("POST", m_uploaderPath);

                OSDMap resp = new();

                if (data is null || data.Length == 0)
                {
                    resp["state"] = "failed";
                    resp["message"] = "Empty upload.";
                    return OSDParser.SerializeLLSDXmlString(resp);
                }

                AssetBase asset = new(UUID.Random(), "Inventory Thumbnail", (sbyte)AssetType.Texture, m_agentID.ToString())
                {
                    Data = data
                };
                m_scene.AssetService.Store(asset);

                if (m_itemId.IsNotZero())
                {
                    InventoryItemBase item = m_scene.InventoryService.GetItem(m_agentID, m_itemId);
                    if (item is null || item.Owner != m_agentID)
                    {
                        resp["state"] = "failed";
                        resp["message"] = "Item no longer accessible.";
                        return OSDParser.SerializeLLSDXmlString(resp);
                    }
                    item.Thumbnail = asset.FullID;
                    m_scene.InventoryService.UpdateItem(item);
                }
                else
                {
                    InventoryFolderBase folder = m_scene.InventoryService.GetFolder(m_agentID, m_categoryId);
                    if (folder is null || folder.Owner != m_agentID)
                    {
                        resp["state"] = "failed";
                        resp["message"] = "Folder no longer accessible.";
                        return OSDParser.SerializeLLSDXmlString(resp);
                    }
                    folder.Thumbnail = asset.FullID;
                    m_scene.InventoryService.UpdateFolder(folder);
                }

                resp["state"] = "complete";
                resp["new_asset"] = asset.FullID;
                return OSDParser.SerializeLLSDXmlString(resp);
            }
        }
    }
}

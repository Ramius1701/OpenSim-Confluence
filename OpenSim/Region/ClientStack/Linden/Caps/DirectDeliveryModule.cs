using System;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.Framework.Marketplace;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.LindenCaps
{
    // Real SL Marketplace floater integration - llmarketplacefunctions.cpp's
    // DirectDelivery capability, traced from Firestorm/secondlife-viewer
    // source. Merchant-listing-management only: browsing, checkout, and
    // payment happen on the WebUI (/marketplace - WebInterfaceServiceConnector),
    // never through this cap, matching how real SL's viewer floater only ever
    // manages a merchant's OWN listings and leaves buying to
    // marketplace.secondlife.com. Auto-merchant for everyone (GET /merchant
    // always 200 for any authenticated agent) - no merchant-status gate.
    //
    // Registers a single "DirectDelivery" cap whose 7 sub-routes are
    // dispatched from ONE handler via the varPath mechanism
    // (RegisterSimpleHandler(..., addToListener: false) +
    // MainServer.Instance.AddSimpleStreamHandler(handler, varPath: true)) -
    // verified against Caps.cs/CapsHandlers.cs/BaseHttpServer.cs this session;
    // no other module here exercises this combination yet. For varPath's
    // prefix match (BaseHttpServer.TryGetSimpleStreamHandler: everything up
    // to the first '/' at or after index 2) to correctly key on this cap
    // instance alone, the handler's own Path must be JUST the per-agent
    // random UUID segment with a single leading slash - no "/caps/" wrapper
    // segment like most other caps here use, since that would collide every
    // agent's DirectDelivery cap onto the same "/caps" varPath key.
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "DirectDeliveryModule")]
    public class DirectDeliveryModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private bool m_Enabled = false;
        private int m_maxInventoryNodes = 5000;
        private UUID m_serviceAccountId = UUID.Zero;

        private Scene m_scene = null;
        private IMarketplaceListingsService m_listings = null;
        private IDeliveryLedger m_ledger = null;

        public string Name => "DirectDeliveryModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["MarketplaceService"];
            if (config == null)
                return;

            if (!UUID.TryParse(config.GetString("ServiceAccountUUID", string.Empty).Trim(), out m_serviceAccountId) || m_serviceAccountId == UUID.Zero)
            {
                m_log.Error("[DIRECT DELIVERY]: [MarketplaceService] ServiceAccountUUID must be a non-zero local account UUID - DirectDelivery cap disabled.");
                return;
            }

            m_maxInventoryNodes = Math.Clamp(config.GetInt("MaxInventoryNodes", 5000), 10, 100000);
            m_Enabled = true;
        }

        public void PostInitialise() { }

        public void Close() { }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_scene = scene;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_listings = scene.RequestModuleInterface<IMarketplaceListingsService>();
            m_ledger = scene.RequestModuleInterface<IDeliveryLedger>();
            if (m_listings == null || m_ledger == null)
            {
                m_log.Info("[DIRECT DELIVERY]: Disabled - IMarketplaceListingsService/IDeliveryLedger not found (is [Modules] MarketplaceService set to LocalMarketplaceListingsServiceConnector?).");
                m_Enabled = false;
                return;
            }

            scene.EventManager.OnRegisterCaps += RegisterCaps;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            scene.EventManager.OnRegisterCaps -= RegisterCaps;
        }

        private void RegisterCaps(UUID agentId, Caps caps)
        {
            string capBasePath = "/" + UUID.Random();
            SimpleStreamHandler handler = new(capBasePath, delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
            {
                HandleDirectDelivery(httpRequest, httpResponse, agentId, capBasePath);
            });

            caps.RegisterSimpleHandler("DirectDelivery", handler, addToListener: false);
            MainServer.Instance.AddSimpleStreamHandler(handler, varPath: true);
        }

        private void HandleDirectDelivery(IOSHttpRequest request, IOSHttpResponse response, UUID agentId, string capBasePath)
        {
            response.ContentType = "application/llsd+json";

            string subPath = request.Url.AbsolutePath;
            if (subPath.StartsWith(capBasePath, StringComparison.Ordinal))
                subPath = subPath.Substring(capBasePath.Length);

            try
            {
                switch (request.HttpMethod)
                {
                    case "GET" when subPath == "/merchant":
                        // Auto-merchant for everyone - presence of a 200 is the
                        // whole check the viewer makes before enabling the
                        // Marketplace Listings floater.
                        response.StatusCode = (int)HttpStatusCode.OK;
                        return;

                    case "GET" when subPath == "/listings":
                        WriteListingsResponse(response, m_listings.GetListingsBySeller(agentId));
                        return;

                    case "GET" when subPath.StartsWith("/listing/", StringComparison.Ordinal):
                        HandleGetListing(response, agentId, subPath.Substring("/listing/".Length));
                        return;

                    case "POST" when subPath == "/listings":
                        HandleCreateListing(request, response, agentId);
                        return;

                    case "PUT" when subPath.StartsWith("/listing/", StringComparison.Ordinal):
                        HandleUpdateListing(request, response, agentId, subPath.Substring("/listing/".Length));
                        return;

                    case "PUT" when subPath.StartsWith("/associate_inventory/", StringComparison.Ordinal):
                        HandleAssociateInventory(request, response, agentId, subPath.Substring("/associate_inventory/".Length));
                        return;

                    case "DELETE" when subPath.StartsWith("/listing/", StringComparison.Ordinal):
                        HandleDeleteListing(response, agentId, subPath.Substring("/listing/".Length));
                        return;

                    default:
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                }
            }
            catch (MarketplaceInventoryException ex)
            {
                WriteError(response, (int)ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[DIRECT DELIVERY]: Request failed for agent {0} ({1} {2}): {3}", agentId, request.HttpMethod, subPath, ex);
                WriteError(response, 500, "Marketplace request failed.");
            }
        }

        private void HandleGetListing(IOSHttpResponse response, UUID agentId, string idStr)
        {
            if (!int.TryParse(idStr, out int id))
            {
                WriteError(response, 400, "Invalid listing id.");
                return;
            }

            MarketplaceListing listing = m_listings.GetListing(id);
            if (listing == null || listing.SellerID != agentId)
            {
                WriteError(response, 404, "Listing not found.");
                return;
            }

            WriteListingsResponse(response, new System.Collections.Generic.List<MarketplaceListing> { listing });
        }

        private void HandleCreateListing(IOSHttpRequest request, IOSHttpResponse response, UUID agentId)
        {
            ListingRequest payload = JsonSerializer.Deserialize<ListingRequest>(request.InputStream, JsonOptions);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Title) || payload.Price < 0)
            {
                WriteError(response, 400, "title is required and price must be non-negative.");
                return;
            }

            MarketplaceListing listing = m_listings.CreateListing(
                agentId,
                payload.Title.Trim(),
                payload.Description ?? string.Empty,
                payload.Price,
                payload.CountOnHand);

            response.StatusCode = (int)HttpStatusCode.Created;
            WriteListingsResponse(response, new System.Collections.Generic.List<MarketplaceListing> { listing });
        }

        private void HandleUpdateListing(IOSHttpRequest request, IOSHttpResponse response, UUID agentId, string idStr)
        {
            if (!int.TryParse(idStr, out int id))
            {
                WriteError(response, 400, "Invalid listing id.");
                return;
            }

            MarketplaceListing listing = m_listings.GetListing(id);
            if (listing == null || listing.SellerID != agentId)
            {
                WriteError(response, 404, "Listing not found.");
                return;
            }

            ListingRequest payload = JsonSerializer.Deserialize<ListingRequest>(request.InputStream, JsonOptions);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Title) || payload.Price < 0)
            {
                WriteError(response, 400, "title is required and price must be non-negative.");
                return;
            }

            listing.Title = payload.Title.Trim();
            listing.Description = payload.Description ?? string.Empty;
            listing.Price = payload.Price;
            listing.CountOnHand = payload.CountOnHand;
            if (payload.IsListed.HasValue)
                listing.IsListed = payload.IsListed.Value;

            if (!m_listings.UpdateListing(listing))
            {
                WriteError(response, 404, "Listing not found.");
                return;
            }

            WriteListingsResponse(response, new System.Collections.Generic.List<MarketplaceListing> { listing });
        }

        private void HandleAssociateInventory(IOSHttpRequest request, IOSHttpResponse response, UUID agentId, string idStr)
        {
            if (!int.TryParse(idStr, out int id))
            {
                WriteError(response, 400, "Invalid listing id.");
                return;
            }

            MarketplaceListing listing = m_listings.GetListing(id);
            if (listing == null || listing.SellerID != agentId)
            {
                WriteError(response, 404, "Listing not found.");
                return;
            }

            AssociateInventoryRequest payload = JsonSerializer.Deserialize<AssociateInventoryRequest>(request.InputStream, JsonOptions);
            if (payload == null || !UUID.TryParse(payload.SourceFolderId, out UUID sourceFolderId) || sourceFolderId == UUID.Zero)
            {
                WriteError(response, 400, "source_folder_id must be a non-zero UUID identifying a top-level folder in the seller's Merchant Outbox.");
                return;
            }

            string versionKey = id + "|" + DateTime.UtcNow.Ticks;
            SnapshotResponse snapshot = MarketplaceInventoryOperations.Snapshot(
                m_scene,
                m_serviceAccountId,
                agentId,
                sourceFolderId,
                versionKey,
                m_maxInventoryNodes);

            if (!UUID.TryParse(snapshot.SnapshotFolderId, out UUID snapshotFolderId))
            {
                WriteError(response, 500, "Snapshot did not return a valid folder id.");
                return;
            }

            // No separate version-history concept (yet) - listing_folder_id and
            // version_folder_id both point at the same deterministic,
            // content-addressed snapshot folder MarketplaceInventoryOperations.
            // Snapshot just produced. A later re-association simply replaces
            // both with the new snapshot's folder id. Never store the
            // merchant's own mutable outbox folder (sourceFolderId) here -
            // only the immutable, custodian-owned snapshot.
            m_listings.SetInventoryAssociation(id, snapshotFolderId, snapshotFolderId, snapshotFolderId);

            listing = m_listings.GetListing(id);
            WriteListingsResponse(response, new System.Collections.Generic.List<MarketplaceListing> { listing });
        }

        private void HandleDeleteListing(IOSHttpResponse response, UUID agentId, string idStr)
        {
            if (!int.TryParse(idStr, out int id))
            {
                WriteError(response, 400, "Invalid listing id.");
                return;
            }

            MarketplaceListing listing = m_listings.GetListing(id);
            if (listing == null || listing.SellerID != agentId)
            {
                WriteError(response, 404, "Listing not found.");
                return;
            }

            // Soft-delete - unlist, retain the row. Matches this session's
            // established idempotency/audit house style for currency and
            // marketplace ledgers alike.
            m_listings.SetListed(id, false);
            response.StatusCode = (int)HttpStatusCode.NoContent;
        }

        private static void WriteListingsResponse(IOSHttpResponse response, System.Collections.Generic.List<MarketplaceListing> listings)
        {
            DirectDeliveryListingsEnvelope envelope = new();
            foreach (MarketplaceListing listing in listings)
            {
                envelope.Listings.Add(new DirectDeliveryListing
                {
                    Id = listing.ID,
                    IsListed = listing.IsListed,
                    EditUrl = "/marketplace/manage?listing=" + listing.ID,
                    InventoryInfo = new DirectDeliveryInventoryInfo
                    {
                        ListingFolderId = listing.ListingFolderID.ToString(),
                        VersionFolderId = listing.VersionFolderID.ToString(),
                        CountOnHand = listing.CountOnHand
                    }
                });
            }

            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        }

        private static void WriteError(IOSHttpResponse response, int statusCode, string message)
        {
            response.StatusCode = statusCode;
            response.RawBuffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { message }));
        }

        private sealed class ListingRequest
        {
            [JsonPropertyName("title")]
            public string Title { get; set; }

            [JsonPropertyName("description")]
            public string Description { get; set; }

            [JsonPropertyName("price")]
            public int Price { get; set; }

            [JsonPropertyName("count_on_hand")]
            public int? CountOnHand { get; set; }

            [JsonPropertyName("is_listed")]
            public bool? IsListed { get; set; }
        }

        private sealed class AssociateInventoryRequest
        {
            [JsonPropertyName("source_folder_id")]
            public string SourceFolderId { get; set; }
        }

        private sealed class DirectDeliveryListingsEnvelope
        {
            [JsonPropertyName("listings")]
            public System.Collections.Generic.List<DirectDeliveryListing> Listings { get; set; } = new();
        }

        private sealed class DirectDeliveryListing
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("is_listed")]
            public bool IsListed { get; set; }

            [JsonPropertyName("edit_url")]
            public string EditUrl { get; set; }

            [JsonPropertyName("inventory_info")]
            public DirectDeliveryInventoryInfo InventoryInfo { get; set; }
        }

        private sealed class DirectDeliveryInventoryInfo
        {
            [JsonPropertyName("listing_folder_id")]
            public string ListingFolderId { get; set; }

            [JsonPropertyName("version_folder_id")]
            public string VersionFolderId { get; set; }

            [JsonPropertyName("count_on_hand")]
            public int? CountOnHand { get; set; }
        }
    }
}

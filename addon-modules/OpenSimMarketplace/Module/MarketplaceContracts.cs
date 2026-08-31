/*
 * OpenSim Marketplace
 * Version 2.1.0
 *
 * Protected Direct Delivery HTTP request contracts. The response types
 * (ProductFolderInfo, InventoryResponse, SnapshotResponse, DeliveryReceipt,
 * DeliveryResponse) moved to
 * OpenSim.Region.CoreModules.Framework.Marketplace.MarketplaceInventoryContracts
 * so they can be shared with the native DirectDelivery cap module / WebUI
 * marketplace - see MarketplaceModule.cs's using directive.
 */
using System.Text.Json.Serialization;

namespace OpenSim.Addons.Marketplace;

internal sealed class InventoryRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "list";

    [JsonPropertyName("seller_id")]
    public string SellerId { get; set; } = string.Empty;
}

internal sealed class InspectRequest
{
    [JsonPropertyName("seller_id")]
    public string SellerId { get; set; } = string.Empty;

    [JsonPropertyName("source_folder_id")]
    public string SourceFolderId { get; set; } = string.Empty;
}

internal sealed class SnapshotRequest
{
    [JsonPropertyName("version_key")]
    public string VersionKey { get; set; } = string.Empty;

    [JsonPropertyName("seller_id")]
    public string SellerId { get; set; } = string.Empty;

    [JsonPropertyName("source_folder_id")]
    public string SourceFolderId { get; set; } = string.Empty;
}

internal sealed class DeliveryRequest
{
    [JsonPropertyName("delivery_id")]
    public string DeliveryId { get; set; } = string.Empty;

    [JsonPropertyName("seller_id")]
    public string SellerId { get; set; } = string.Empty;

    [JsonPropertyName("snapshot_folder_id")]
    public string SnapshotFolderId { get; set; } = string.Empty;

    [JsonPropertyName("recipient_id")]
    public string RecipientId { get; set; } = string.Empty;

    [JsonPropertyName("snapshot_fingerprint")]
    public string SnapshotFingerprint { get; set; } = string.Empty;
}

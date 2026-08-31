/*
 * Result types returned by MarketplaceInventoryOperations. Live in
 * OpenSim.Framework (not CoreModules) so both the data layer
 * (OpenSim.Data/OpenSim.Data.MySQL, which cannot reference CoreModules -
 * CoreModules references Data, not the other way around) and the service/
 * region layers can share one set of types, matching where CurrencyTransfer/
 * StoreCatalogItem already live for the same reason.
 */
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // One row per marketplace listing (native DirectDelivery marketplace,
    // MarketplaceListingsService/IMarketplaceListingsData) - not the old v2
    // addon's protocol, which never persisted listings of its own at all
    // (every "product" was just discovered live from Merchant Outbox folder
    // contents on each request). CountOnHand null means unlimited stock;
    // a non-null value is real, atomically-decremented finite stock (see
    // IMarketplaceListingsData.TryReserveStock).
    public class MarketplaceListing
    {
        public int ID = 0;
        public UUID SellerID = UUID.Zero;
        public string Title = string.Empty;
        public string Description = string.Empty;

        // ConfluenceCurrency units - see ICurrencyService.
        public int Price = 0;

        public int? CountOnHand = null;
        public bool IsListed = false;

        // Populated by PUT /associate_inventory/<id> (DirectDeliveryModule)
        // via MarketplaceInventoryOperations.Snapshot - UUID.Zero until then.
        public UUID SnapshotFolderID = UUID.Zero;
        public UUID ListingFolderID = UUID.Zero;
        public UUID VersionFolderID = UUID.Zero;

        public DateTime Created = DateTime.UtcNow;
        public DateTime Updated = DateTime.UtcNow;
    }

    public sealed class ProductFolderInfo
    {
        [JsonPropertyName("folder_id")]
        public string FolderId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("fingerprint")]
        public string Fingerprint { get; set; } = string.Empty;

        [JsonPropertyName("item_count")]
        public int ItemCount { get; set; }

        [JsonPropertyName("folder_count")]
        public int FolderCount { get; set; }

        [JsonPropertyName("copy")]
        public bool Copy { get; set; }

        [JsonPropertyName("transfer")]
        public bool Transfer { get; set; }

        [JsonPropertyName("modify")]
        public bool Modify { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public sealed class InventoryResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("seller_id")]
        public string SellerId { get; set; } = string.Empty;

        [JsonPropertyName("marketplace_folder_id")]
        public string MarketplaceFolderId { get; set; } = string.Empty;

        [JsonPropertyName("outbox_folder_id")]
        public string OutboxFolderId { get; set; } = string.Empty;

        [JsonPropertyName("products")]
        public List<ProductFolderInfo> Products { get; set; } = new();

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public sealed class SnapshotResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("version_key")]
        public string VersionKey { get; set; } = string.Empty;

        [JsonPropertyName("seller_id")]
        public string SellerId { get; set; } = string.Empty;

        [JsonPropertyName("source_folder_id")]
        public string SourceFolderId { get; set; } = string.Empty;

        [JsonPropertyName("snapshot_folder_id")]
        public string SnapshotFolderId { get; set; } = string.Empty;

        [JsonPropertyName("source_fingerprint")]
        public string SourceFingerprint { get; set; } = string.Empty;

        [JsonPropertyName("snapshot_fingerprint")]
        public string SnapshotFingerprint { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("item_count")]
        public int ItemCount { get; set; }

        [JsonPropertyName("folder_count")]
        public int FolderCount { get; set; }

        [JsonPropertyName("copy")]
        public bool Copy { get; set; }

        [JsonPropertyName("transfer")]
        public bool Transfer { get; set; }

        [JsonPropertyName("modify")]
        public bool Modify { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public sealed class DeliveryReceipt
    {
        [JsonPropertyName("timestamp_utc")]
        public string TimestampUtc { get; set; } = string.Empty;

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

        [JsonPropertyName("destination_folder_id")]
        public string DestinationFolderId { get; set; } = string.Empty;

        [JsonPropertyName("item_count")]
        public int ItemCount { get; set; }

        [JsonPropertyName("folder_count")]
        public int FolderCount { get; set; }

        public bool Matches(
            string sellerId,
            string snapshotFolderId,
            string recipientId,
            string snapshotFingerprint) =>
            SellerId.Equals(
                sellerId,
                System.StringComparison.OrdinalIgnoreCase)
            && SnapshotFolderId.Equals(
                snapshotFolderId,
                System.StringComparison.OrdinalIgnoreCase)
            && RecipientId.Equals(
                recipientId,
                System.StringComparison.OrdinalIgnoreCase)
            && SnapshotFingerprint.Equals(
                snapshotFingerprint,
                System.StringComparison.OrdinalIgnoreCase);
    }

    public sealed class DeliveryResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("duplicate")]
        public bool Duplicate { get; set; }

        [JsonPropertyName("retryable")]
        public bool Retryable { get; set; }

        [JsonPropertyName("delivery_id")]
        public string DeliveryId { get; set; } = string.Empty;

        [JsonPropertyName("destination_folder_id")]
        public string DestinationFolderId { get; set; } = string.Empty;

        [JsonPropertyName("item_count")]
        public int ItemCount { get; set; }

        [JsonPropertyName("folder_count")]
        public int FolderCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        public static DeliveryResponse FromReceipt(DeliveryReceipt receipt, bool duplicate, string message) => new()
        {
            Ok = true,
            Duplicate = duplicate,
            Retryable = false,
            DeliveryId = receipt.DeliveryId,
            DestinationFolderId = receipt.DestinationFolderId,
            ItemCount = receipt.ItemCount,
            FolderCount = receipt.FolderCount,
            Message = message
        };

        public static DeliveryResponse Error(string deliveryId, string message, bool retryable = false) => new()
        {
            Ok = false,
            Duplicate = false,
            Retryable = retryable,
            DeliveryId = deliveryId,
            Message = message
        };
    }
}

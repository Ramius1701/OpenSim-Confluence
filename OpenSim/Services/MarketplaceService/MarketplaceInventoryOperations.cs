#nullable enable annotations

/*
 * OpenSim Marketplace - Direct Delivery inventory operations.
 *
 * Shared by both addon-modules/OpenSimMarketplace's legacy v2 HTTP API and the
 * native DirectDelivery cap module (region-side) / WebUI marketplace checkout
 * (Robust-side). Lives here, in OpenSim.Services.MarketplaceService rather than
 * CoreModules, specifically because it has NO Scene dependency (see Deliver's
 * own comment) - that's what makes it callable directly from
 * OpenSim.Server.Handlers (WebInterfaceServiceConnector), which does not
 * reference OpenSim.Region.Framework/CoreModules at all.
 *
 * The next-owner permission transformation is intentionally modelled on the
 * current OpenSimulator Scene.GiveInventoryItem implementation. Marketplace
 * snapshots retain merchant-side permissions; delivery applies next-owner
 * permissions to the recipient copy.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Services.MarketplaceService;

public static class MarketplaceInventoryOperations
{
    // Folder-tree-based - used by the old v2 addon's own Inventory/Inspect/
    // Snapshot/Deliver below, unchanged, so that already-live addon keeps
    // working exactly as before.
    private const string MarketplaceFolderName = "OpenSim Marketplace";
    private const string MerchantOutboxFolderName = "Merchant Outbox";
    private const string ServiceInventoryFolderName = "Marketplace Inventory";
    private const string ReceivedItemsFolderName = "Received Items";
    private const uint RequiredSourcePermissions =
        (uint)(PermissionMask.Copy | PermissionMask.Transfer);

    // Item-based - used by the native marketplace path (DirectDeliveryModule /
    // the WebUI) below: ListListingItems/SnapshotListingItem/DeliverListingItem.
    // A listing is exactly one item sitting directly inside "Marketplace
    // Listings" - no per-listing subfolder, no multi-item bundling. This is a
    // deliberate simplification chosen over real SL's actual structure
    // (Marketplace Listings > <one folder per listing> > items, which can
    // bundle several items into one listing) - explicit user direction:
    // simplicity over bundle support, which nothing here currently needs.
    private const string MarketplaceListingsFolderName = "Marketplace Listings";
    private const string MarketplacePurchasesFolderName = "Marketplace Purchases";

    public static InventoryResponse Inventory(IInventoryService inventory, IUserAccountService userAccounts, UUID scopeId, UUID sellerId, int maxNodes)
    {
        if (!UserExists(userAccounts, scopeId, sellerId))
            return new InventoryResponse { Ok = false, SellerId = sellerId.ToString(), Message = "Seller account was not found." };

        try
        {
            InventoryFolderBase marketplace = EnsureMarketplaceFolder(inventory, sellerId);
            InventoryFolderBase outbox = EnsureChildFolder(
                inventory,
                sellerId,
                marketplace,
                MerchantOutboxFolderName,
                "merchant-outbox");

            InventoryCollection? content = inventory.GetFolderContent(sellerId, outbox.ID);
            List<ProductFolderInfo> products = new();

            if (content != null)
            {
                foreach (InventoryFolderBase folder in content.Folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        FolderSnapshot snapshot = CaptureFolder(inventory, sellerId, folder.ID, maxNodes);
                        products.Add(ToProductInfo(snapshot, "Ready for listing association."));
                    }
                    catch (MarketplaceInventoryException ex)
                    {
                        products.Add(new ProductFolderInfo
                        {
                            FolderId = folder.ID.ToString(),
                            Name = folder.Name,
                            Copy = false,
                            Transfer = false,
                            Modify = false,
                            Message = ex.Message
                        });
                    }
                }
            }

            return new InventoryResponse
            {
                Ok = true,
                SellerId = sellerId.ToString(),
                MarketplaceFolderId = marketplace.ID.ToString(),
                OutboxFolderId = outbox.ID.ToString(),
                Products = products,
                Message = products.Count == 0
                    ? "Merchant Outbox is ready. Add one top-level product folder per Marketplace item."
                    : "Merchant Outbox synchronized."
            };
        }
        catch (Exception ex)
        {
            return new InventoryResponse
            {
                Ok = false,
                SellerId = sellerId.ToString(),
                Message = "Marketplace inventory could not be prepared: " + ex.Message
            };
        }
    }

    public static ProductFolderInfo Inspect(IInventoryService inventory, UUID sellerId, UUID sourceFolderId, int maxNodes)
    {
        InventoryFolderBase marketplace = EnsureMarketplaceFolder(inventory, sellerId);
        InventoryFolderBase outbox = EnsureChildFolder(
            inventory,
            sellerId,
            marketplace,
            MerchantOutboxFolderName,
            "merchant-outbox");

        InventoryFolderBase? folder = inventory.GetFolder(sellerId, sourceFolderId);
        if (folder == null || folder.Owner != sellerId || folder.ParentID != outbox.ID)
            throw new MarketplaceInventoryException(HttpStatusCode.NotFound, "Product folder is not a top-level folder in the seller's Merchant Outbox.");

        return ToProductInfo(CaptureFolder(inventory, sellerId, sourceFolderId, maxNodes), "Product folder validated.");
    }

    public static SnapshotResponse Snapshot(
        IInventoryService inventory,
        IUserAccountService userAccounts,
        UUID scopeId,
        UUID serviceAccountId,
        UUID sellerId,
        UUID sourceFolderId,
        string versionKey,
        int maxNodes)
    {
        if (!UserExists(userAccounts, scopeId, sellerId))
            throw new MarketplaceInventoryException(HttpStatusCode.NotFound, "Seller account was not found.");
        if (!UserExists(userAccounts, scopeId, serviceAccountId))
            throw new MarketplaceInventoryException(HttpStatusCode.ServiceUnavailable, "Marketplace service account was not found.");

        ProductFolderInfo inspected = Inspect(inventory, sellerId, sourceFolderId, maxNodes);
        FolderSnapshot source = CaptureFolder(inventory, sellerId, sourceFolderId, maxNodes);

        InventoryFolderBase serviceRoot = EnsureRootChildFolder(
            inventory,
            serviceAccountId,
            ServiceInventoryFolderName,
            "service-inventory");

        InventoryFolderBase sellerRoot = EnsureChildFolder(
            inventory,
            serviceAccountId,
            serviceRoot,
            sellerId.ToString(),
            "service-seller|" + sellerId);

        Dictionary<UUID, UUID> folderMap = new();
        if (sellerId == serviceAccountId)
            throw new MarketplaceInventoryException(
                HttpStatusCode.Forbidden,
                "Marketplace service account cannot be used as a seller.");

        InventoryFolderBase snapshotRoot = CopySnapshotFolderRecursive(
            inventory,
            source,
            source.RootFolderId,
            sellerRoot.ID,
            serviceAccountId,
            versionKey,
            folderMap);

        FolderSnapshot storedSnapshot = CaptureFolder(
            inventory,
            serviceAccountId,
            snapshotRoot.ID,
            maxNodes);

        return new SnapshotResponse
        {
            Ok = true,
            VersionKey = versionKey,
            SellerId = sellerId.ToString(),
            SourceFolderId = sourceFolderId.ToString(),
            SnapshotFolderId = snapshotRoot.ID.ToString(),
            SourceFingerprint = source.Fingerprint,
            SnapshotFingerprint = storedSnapshot.Fingerprint,
            Name = inspected.Name,
            Description = inspected.Description,
            ItemCount = source.Items.Count,
            FolderCount = source.Folders.Count,
            Copy = source.AllCopy,
            Transfer = source.AllTransfer,
            Modify = source.AllModify,
            Message = "Marketplace listing version snapshot completed."
        };
    }

    // Deliberately Scene-free (unlike Inventory/Inspect/Snapshot, which only
    // ever run region-side backing the merchant's viewer cap): delivery is
    // the one operation the Robust-hosted WebUI checkout also needs to
    // trigger directly, for a buyer who may not be logged into any region
    // at all - IInventoryService/IUserAccountService are grid-shared
    // connectors either way, so no live Scene is actually required to reach
    // them. Region callers (the old v2 addon, and DirectDeliveryModule) pass
    // their own scene.InventoryService/UserAccountService/RegionInfo.ScopeID/
    // Permissions.PropagatePermissions(); this whole class has no Scene
    // dependency anywhere, which is what makes it callable from Robust
    // (OpenSim.Server.Handlers, which doesn't reference OpenSim.Region.Framework
    // at all) in the first place. Pass notifyRecipient only when a live Scene
    // is available and local in-viewer notification is wanted - the callback
    // receives (recipientId, destination folder, item count) and is expected
    // to do its own scene.GetScenePresence/SendBulkUpdateInventory, using
    // CollectDeliveryTree above to walk the folder; null skips it silently
    // (matching how an offline recipient was already handled either way).
    public static DeliveryResponse Deliver(
        IInventoryService inventory,
        IUserAccountService userAccounts,
        UUID scopeId,
        bool propagatePermissions,
        UUID serviceAccountId,
        UUID sellerId,
        UUID snapshotFolderId,
        UUID recipientId,
        string snapshotFingerprint,
        string deliveryId,
        int maxNodes,
        IDeliveryLedger ledger,
        ILog log,
        Action<UUID, InventoryFolderBase, int> notifyRecipient)
    {
        if (ledger.TryGet(deliveryId, out DeliveryReceipt recorded))
        {
            if (!recorded.Matches(
                    sellerId.ToString(),
                    snapshotFolderId.ToString(),
                    recipientId.ToString(),
                    snapshotFingerprint))
            {
                return DeliveryResponse.Error(
                    deliveryId,
                    "Delivery ID is already bound to different delivery data.");
            }
            return DeliveryResponse.FromReceipt(recorded, true, "Delivery already completed.");
        }

        if (!UserExists(userAccounts, scopeId, recipientId))
            return DeliveryResponse.Error(
                deliveryId,
                "Recipient must be a local grid account.");

        if (recipientId == serviceAccountId)
            return DeliveryResponse.Error(
                deliveryId,
                "Marketplace service account cannot receive customer deliveries.");

        try
        {
            FolderSnapshot snapshot = CaptureFolder(
                inventory,
                serviceAccountId,
                snapshotFolderId,
                maxNodes);

            ValidateSnapshotLocation(
                inventory,
                serviceAccountId,
                sellerId,
                snapshotFolderId);

            if (!snapshot.Fingerprint.Equals(
                    snapshotFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return DeliveryResponse.Error(
                    deliveryId,
                    "Published Marketplace snapshot fingerprint does not match the stored inventory tree.");
            }

            InventoryFolderBase marketplace = EnsureMarketplaceFolder(inventory, recipientId);
            InventoryFolderBase received = EnsureChildFolder(
                inventory,
                recipientId,
                marketplace,
                ReceivedItemsFolderName,
                "received-items");

            Dictionary<UUID, UUID> folderMap = new();
            InventoryFolderBase destination = CopyDeliveryFolderRecursive(
                propagatePermissions,
                inventory,
                snapshot,
                snapshot.RootFolderId,
                received.ID,
                recipientId,
                deliveryId,
                folderMap);

            DeliveryReceipt receipt = new()
            {
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                DeliveryId = deliveryId,
                SellerId = sellerId.ToString(),
                SnapshotFolderId = snapshotFolderId.ToString(),
                RecipientId = recipientId.ToString(),
                SnapshotFingerprint = snapshot.Fingerprint,
                DestinationFolderId = destination.ID.ToString(),
                ItemCount = snapshot.Items.Count,
                FolderCount = snapshot.Folders.Count
            };

            if (!ledger.TryRecord(receipt, out string ledgerError))
                return DeliveryResponse.Error(deliveryId, "Delivery completed but receipt ledger failed: " + ledgerError, true);

            notifyRecipient?.Invoke(recipientId, destination, snapshot.Items.Count);

            log.InfoFormat(
                "[OPENSIM MARKETPLACE]: Delivered snapshot {0} to {1}; delivery={2}, destination={3}",
                snapshotFolderId,
                recipientId,
                deliveryId,
                destination.ID);

            return DeliveryResponse.FromReceipt(receipt, false, "Direct Delivery completed.");
        }
        catch (MarketplaceInventoryException ex)
        {
            return DeliveryResponse.Error(deliveryId, ex.Message, ex.Retryable);
        }
        catch (Exception ex)
        {
            log.ErrorFormat("[OPENSIM MARKETPLACE]: Delivery {0} failed: {1}", deliveryId, ex);
            return DeliveryResponse.Error(deliveryId, "Direct Delivery failed.", true);
        }
    }

    // --- Item-based native marketplace path (see the constants' own comment) ---

    public static InventoryResponse ListListingItems(IInventoryService inventory, IUserAccountService userAccounts, UUID scopeId, UUID sellerId, int maxItems)
    {
        if (!UserExists(userAccounts, scopeId, sellerId))
            return new InventoryResponse { Ok = false, SellerId = sellerId.ToString(), Message = "Seller account was not found." };

        try
        {
            InventoryFolderBase listings = EnsureRootChildFolder(inventory, sellerId, MarketplaceListingsFolderName, "marketplace-listings-root");
            InventoryCollection? content = inventory.GetFolderContent(sellerId, listings.ID);
            List<ProductFolderInfo> products = new();

            if (content != null)
            {
                int count = 0;
                foreach (InventoryItemBase item in content.Items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (++count > maxItems)
                        break;
                    if (item.Owner != sellerId || item.AssetType == (int)AssetType.Link || item.AssetType == (int)AssetType.LinkFolder)
                        continue;

                    bool copy = (item.CurrentPermissions & (uint)PermissionMask.Copy) != 0;
                    bool transfer = (item.CurrentPermissions & (uint)PermissionMask.Transfer) != 0;
                    bool modify = (item.CurrentPermissions & (uint)PermissionMask.Modify) != 0;

                    products.Add(new ProductFolderInfo
                    {
                        FolderId = item.ID.ToString(),
                        Name = item.Name,
                        Description = item.Description,
                        Copy = copy,
                        Transfer = transfer,
                        Modify = modify,
                        Message = copy && transfer
                            ? "Ready for listing association."
                            : "Not sellable - needs Copy and Transfer permissions."
                    });
                }
            }

            return new InventoryResponse
            {
                Ok = true,
                SellerId = sellerId.ToString(),
                MarketplaceFolderId = listings.ID.ToString(),
                Products = products,
                Message = products.Count == 0
                    ? "Marketplace Listings is ready. Drag an item into it to list it."
                    : "Marketplace Listings synchronized."
            };
        }
        catch (Exception ex)
        {
            return new InventoryResponse
            {
                Ok = false,
                SellerId = sellerId.ToString(),
                Message = "Marketplace inventory could not be prepared: " + ex.Message
            };
        }
    }

    public static SnapshotResponse SnapshotListingItem(
        IInventoryService inventory,
        IUserAccountService userAccounts,
        UUID scopeId,
        UUID serviceAccountId,
        UUID sellerId,
        UUID sourceItemId,
        string versionKey)
    {
        if (!UserExists(userAccounts, scopeId, sellerId))
            throw new MarketplaceInventoryException(HttpStatusCode.NotFound, "Seller account was not found.");
        if (!UserExists(userAccounts, scopeId, serviceAccountId))
            throw new MarketplaceInventoryException(HttpStatusCode.ServiceUnavailable, "Marketplace service account was not found.");
        if (sellerId == serviceAccountId)
            throw new MarketplaceInventoryException(HttpStatusCode.Forbidden, "Marketplace service account cannot be used as a seller.");

        InventoryFolderBase listings = EnsureRootChildFolder(inventory, sellerId, MarketplaceListingsFolderName, "marketplace-listings-root");
        InventoryItemBase? sourceItem = inventory.GetItem(sellerId, sourceItemId);
        if (sourceItem == null || sourceItem.Owner != sellerId || sourceItem.Folder != listings.ID)
            throw new MarketplaceInventoryException(HttpStatusCode.NotFound, "Item is not directly inside the seller's Marketplace Listings folder.");
        if (sourceItem.AssetType == (int)AssetType.Link || sourceItem.AssetType == (int)AssetType.LinkFolder)
            throw new MarketplaceInventoryException(HttpStatusCode.UnprocessableEntity, "Inventory links are not supported as Marketplace listings.");

        bool copy = (sourceItem.CurrentPermissions & (uint)PermissionMask.Copy) != 0;
        bool transfer = (sourceItem.CurrentPermissions & (uint)PermissionMask.Transfer) != 0;
        bool modify = (sourceItem.CurrentPermissions & (uint)PermissionMask.Modify) != 0;
        if (!copy || !transfer)
            throw new MarketplaceInventoryException(
                HttpStatusCode.UnprocessableEntity,
                $"Marketplace source item '{sourceItem.Name}' must be Copy and Transfer for reliable Direct Delivery. Next-owner permissions may still remove Copy or Transfer for the buyer.");

        ItemNode sourceNode = new(sourceItem);
        string sourceFingerprint = ComputeItemFingerprint(sourceNode);

        InventoryFolderBase serviceRoot = EnsureRootChildFolder(inventory, serviceAccountId, ServiceInventoryFolderName, "service-inventory");
        InventoryFolderBase sellerRoot = EnsureChildFolder(inventory, serviceAccountId, serviceRoot, sellerId.ToString(), "service-seller|" + sellerId);

        UUID snapshotItemId = CreateDeterministicUuid("listing-snapshot-item", versionKey + "|" + sourceItem.ID);
        InventoryItemBase snapshotItem = sourceNode.CreateSnapshotItem(snapshotItemId, serviceAccountId, sellerRoot.ID);
        AddOrVerifyItem(inventory, snapshotItem);

        ItemNode storedNode = new(snapshotItem);
        string storedFingerprint = ComputeItemFingerprint(storedNode);

        return new SnapshotResponse
        {
            Ok = true,
            VersionKey = versionKey,
            SellerId = sellerId.ToString(),
            SourceFolderId = sourceItemId.ToString(),
            SnapshotFolderId = snapshotItemId.ToString(),
            SourceFingerprint = sourceFingerprint,
            SnapshotFingerprint = storedFingerprint,
            Name = sourceNode.Name,
            Description = sourceNode.Description,
            ItemCount = 1,
            FolderCount = 0,
            Copy = copy,
            Transfer = transfer,
            Modify = modify,
            Message = "Marketplace listing snapshot completed."
        };
    }

    // Item-based counterpart to Deliver above - same Scene-free posture (see
    // Deliver's own comment) and the same idempotency-ledger contract, just
    // delivering a single item straight into the recipient's "Marketplace
    // Purchases" folder instead of copying a folder tree. notifyRecipient
    // receives the delivered item directly - no CollectDeliveryTree walk
    // needed for a single item.
    public static DeliveryResponse DeliverListingItem(
        IInventoryService inventory,
        IUserAccountService userAccounts,
        UUID scopeId,
        bool propagatePermissions,
        UUID serviceAccountId,
        UUID sellerId,
        UUID snapshotItemId,
        UUID recipientId,
        string snapshotFingerprint,
        string deliveryId,
        IDeliveryLedger ledger,
        ILog log,
        Action<UUID, InventoryItemBase> notifyRecipient)
    {
        if (ledger.TryGet(deliveryId, out DeliveryReceipt recorded))
        {
            if (!recorded.Matches(
                    sellerId.ToString(),
                    snapshotItemId.ToString(),
                    recipientId.ToString(),
                    snapshotFingerprint))
            {
                return DeliveryResponse.Error(
                    deliveryId,
                    "Delivery ID is already bound to different delivery data.");
            }
            return DeliveryResponse.FromReceipt(recorded, true, "Delivery already completed.");
        }

        if (!UserExists(userAccounts, scopeId, recipientId))
            return DeliveryResponse.Error(
                deliveryId,
                "Recipient must be a local grid account.");

        if (recipientId == serviceAccountId)
            return DeliveryResponse.Error(
                deliveryId,
                "Marketplace service account cannot receive customer deliveries.");

        try
        {
            InventoryItemBase? snapshotItem = inventory.GetItem(serviceAccountId, snapshotItemId);
            if (snapshotItem == null || snapshotItem.Owner != serviceAccountId)
                throw new MarketplaceInventoryException(HttpStatusCode.NotFound, "Snapshot item was not found.");

            InventoryFolderBase serviceRoot = EnsureRootChildFolder(inventory, serviceAccountId, ServiceInventoryFolderName, "service-inventory");
            InventoryFolderBase sellerRoot = EnsureChildFolder(inventory, serviceAccountId, serviceRoot, sellerId.ToString(), "service-seller|" + sellerId);
            if (snapshotItem.Folder != sellerRoot.ID)
                throw new MarketplaceInventoryException(HttpStatusCode.Forbidden, "Snapshot item is not owned by the Marketplace service account for this seller.");

            ItemNode snapshotNode = new(snapshotItem);
            string actualFingerprint = ComputeItemFingerprint(snapshotNode);
            if (!actualFingerprint.Equals(snapshotFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return DeliveryResponse.Error(
                    deliveryId,
                    "Published Marketplace snapshot fingerprint does not match the stored item.");
            }

            InventoryFolderBase purchases = EnsureRootChildFolder(inventory, recipientId, MarketplacePurchasesFolderName, "marketplace-purchases-root");

            UUID deliveredItemId = CreateDeterministicUuid("listing-delivery-item", deliveryId + "|" + snapshotItem.ID);
            InventoryItemBase deliveredItem = snapshotNode.CreateDeliveryItem(deliveredItemId, recipientId, purchases.ID, propagatePermissions);
            AddOrVerifyItem(inventory, deliveredItem);

            DeliveryReceipt receipt = new()
            {
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                DeliveryId = deliveryId,
                SellerId = sellerId.ToString(),
                SnapshotFolderId = snapshotItemId.ToString(),
                RecipientId = recipientId.ToString(),
                SnapshotFingerprint = actualFingerprint,
                DestinationFolderId = deliveredItem.ID.ToString(),
                ItemCount = 1,
                FolderCount = 0
            };

            if (!ledger.TryRecord(receipt, out string ledgerError))
                return DeliveryResponse.Error(deliveryId, "Delivery completed but receipt ledger failed: " + ledgerError, true);

            notifyRecipient?.Invoke(recipientId, deliveredItem);

            log.InfoFormat(
                "[MARKETPLACE]: Delivered item {0} to {1}; delivery={2}, destination item={3}",
                snapshotItemId,
                recipientId,
                deliveryId,
                deliveredItem.ID);

            return DeliveryResponse.FromReceipt(receipt, false, "Direct Delivery completed.");
        }
        catch (MarketplaceInventoryException ex)
        {
            return DeliveryResponse.Error(deliveryId, ex.Message, ex.Retryable);
        }
        catch (Exception ex)
        {
            log.ErrorFormat("[MARKETPLACE]: Delivery {0} failed: {1}", deliveryId, ex);
            return DeliveryResponse.Error(deliveryId, "Direct Delivery failed.", true);
        }
    }

    private static string ComputeItemFingerprint(ItemNode item)
    {
        string data = $"I|{item.Id}|{item.FolderId}|{item.AssetId}|{item.Name}|{item.Description}|{item.AssetType}|{item.InvType}|{item.BasePermissions}|{item.CurrentPermissions}|{item.NextPermissions}|{item.EveryOnePermissions}|{item.GroupPermissions}|{item.Flags}\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    // --- Shared helpers (used by both the folder-tree and item-based paths above) ---

    private static void ValidateSnapshotLocation(
        IInventoryService inventory,
        UUID serviceAccountId,
        UUID sellerId,
        UUID snapshotFolderId)
    {
        InventoryFolderBase serviceRoot = EnsureRootChildFolder(
            inventory,
            serviceAccountId,
            ServiceInventoryFolderName,
            "service-inventory");
        InventoryFolderBase sellerRoot = EnsureChildFolder(
            inventory,
            serviceAccountId,
            serviceRoot,
            sellerId.ToString(),
            "service-seller|" + sellerId);
        InventoryFolderBase? snapshot = inventory.GetFolder(serviceAccountId, snapshotFolderId);

        if (snapshot == null || snapshot.Owner != serviceAccountId || snapshot.ParentID != sellerRoot.ID)
            throw new MarketplaceInventoryException(HttpStatusCode.Forbidden, "Snapshot folder is not owned by the Marketplace service account for this seller.");
    }

    private static FolderSnapshot CaptureFolder(
        IInventoryService inventory,
        UUID ownerId,
        UUID rootFolderId,
        int maxNodes)
    {
        InventoryFolderBase? root = inventory.GetFolder(ownerId, rootFolderId);
        if (root == null || root.Owner != ownerId)
            throw new MarketplaceInventoryException(HttpStatusCode.NotFound, "Inventory folder was not found.");

        FolderSnapshot snapshot = new(root.ID, root.Name, string.Empty);
        CaptureFolderRecursive(inventory, ownerId, root, snapshot, maxNodes, 0);
        snapshot.FinalizeFingerprint();
        return snapshot;
    }

    private static void CaptureFolderRecursive(
        IInventoryService inventory,
        UUID ownerId,
        InventoryFolderBase folder,
        FolderSnapshot snapshot,
        int maxNodes,
        int depth)
    {
        if (depth > 64)
            throw new MarketplaceInventoryException(HttpStatusCode.UnprocessableEntity, "Product folder depth exceeds 64 levels.");
        if (snapshot.NodeCount >= maxNodes)
            throw new MarketplaceInventoryException(HttpStatusCode.UnprocessableEntity, "Product folder exceeds the configured Marketplace node limit.");

        snapshot.Folders.Add(new FolderNode(folder));
        InventoryCollection? content = inventory.GetFolderContent(ownerId, folder.ID);
        if (content == null)
            throw new MarketplaceInventoryException(HttpStatusCode.ServiceUnavailable, "Inventory service could not read a product folder.", true);

        foreach (InventoryItemBase item in content.Items.OrderBy(i => i.ID))
        {
            if (snapshot.NodeCount >= maxNodes)
                throw new MarketplaceInventoryException(HttpStatusCode.UnprocessableEntity, "Product folder exceeds the configured Marketplace node limit.");
            if (item.Owner != ownerId)
                throw new MarketplaceInventoryException(HttpStatusCode.Forbidden, "A product inventory item is not owned by the expected account.");
            if (item.AssetType == (int)AssetType.Link || item.AssetType == (int)AssetType.LinkFolder)
                throw new MarketplaceInventoryException(HttpStatusCode.UnprocessableEntity, "Inventory links are not supported in Marketplace products.");

            bool copy = (item.CurrentPermissions & (uint)PermissionMask.Copy) != 0;
            bool transfer = (item.CurrentPermissions & (uint)PermissionMask.Transfer) != 0;
            bool modify = (item.CurrentPermissions & (uint)PermissionMask.Modify) != 0;

            if (!copy || !transfer)
                throw new MarketplaceInventoryException(
                    HttpStatusCode.UnprocessableEntity,
                    $"Marketplace source item '{item.Name}' must be Copy and Transfer for reliable Direct Delivery. Next-owner permissions may still remove Copy or Transfer for the buyer.");

            snapshot.AllCopy &= copy;
            snapshot.AllTransfer &= transfer;
            snapshot.AllModify &= modify;
            snapshot.Items.Add(new ItemNode(item));
        }

        foreach (InventoryFolderBase child in content.Folders.OrderBy(f => f.ID))
            CaptureFolderRecursive(inventory, ownerId, child, snapshot, maxNodes, depth + 1);
    }

    private static InventoryFolderBase CopySnapshotFolderRecursive(
        IInventoryService inventory,
        FolderSnapshot snapshot,
        UUID sourceFolderId,
        UUID destinationParentId,
        UUID serviceAccountId,
        string versionKey,
        Dictionary<UUID, UUID> folderMap)
    {
        FolderNode sourceFolder = snapshot.Folders.First(f => f.Id == sourceFolderId);
        UUID destinationId = CreateDeterministicUuid("snapshot-folder", versionKey + "|" + sourceFolder.Id);
        InventoryFolderBase destination = AddOrVerifyFolder(
            inventory,
            destinationId,
            sourceFolder.Name,
            serviceAccountId,
            sourceFolder.Type,
            destinationParentId,
            sourceFolder.Version);
        folderMap[sourceFolder.Id] = destination.ID;

        foreach (ItemNode sourceItem in snapshot.Items.Where(i => i.FolderId == sourceFolderId))
        {
            UUID itemId = CreateDeterministicUuid("snapshot-item", versionKey + "|" + sourceItem.Id);
            InventoryItemBase item = sourceItem.CreateSnapshotItem(itemId, serviceAccountId, destination.ID);
            AddOrVerifyItem(inventory, item);
        }

        foreach (FolderNode child in snapshot.Folders.Where(f => f.ParentId == sourceFolderId).OrderBy(f => f.Id))
        {
            CopySnapshotFolderRecursive(
                inventory,
                snapshot,
                child.Id,
                destination.ID,
                serviceAccountId,
                versionKey,
                folderMap);
        }

        return destination;
    }

    private static InventoryFolderBase CopyDeliveryFolderRecursive(
        bool propagatePermissions,
        IInventoryService inventory,
        FolderSnapshot snapshot,
        UUID sourceFolderId,
        UUID destinationParentId,
        UUID recipientId,
        string deliveryId,
        Dictionary<UUID, UUID> folderMap)
    {
        FolderNode sourceFolder = snapshot.Folders.First(f => f.Id == sourceFolderId);
        UUID destinationId = CreateDeterministicUuid("delivery-folder", deliveryId + "|" + sourceFolder.Id);
        InventoryFolderBase destination = AddOrVerifyFolder(
            inventory,
            destinationId,
            sourceFolder.Name,
            recipientId,
            sourceFolder.Type,
            destinationParentId,
            sourceFolder.Version);
        folderMap[sourceFolder.Id] = destination.ID;

        foreach (ItemNode sourceItem in snapshot.Items.Where(i => i.FolderId == sourceFolderId))
        {
            UUID itemId = CreateDeterministicUuid("delivery-item", deliveryId + "|" + sourceItem.Id);
            InventoryItemBase item = sourceItem.CreateDeliveryItem(itemId, recipientId, destination.ID, propagatePermissions);
            AddOrVerifyItem(inventory, item);
        }

        foreach (FolderNode child in snapshot.Folders.Where(f => f.ParentId == sourceFolderId).OrderBy(f => f.Id))
        {
            CopyDeliveryFolderRecursive(
                propagatePermissions,
                inventory,
                snapshot,
                child.Id,
                destination.ID,
                recipientId,
                deliveryId,
                folderMap);
        }

        return destination;
    }

    private static InventoryFolderBase AddOrVerifyFolder(
        IInventoryService inventory,
        UUID id,
        string name,
        UUID owner,
        short type,
        UUID parentId,
        ushort version)
    {
        InventoryFolderBase? existing = inventory.GetFolder(owner, id);
        if (existing != null)
        {
            if (existing.Owner != owner || existing.ParentID != parentId)
                throw new MarketplaceInventoryException(HttpStatusCode.Conflict, "Deterministic Marketplace folder ID collides with another inventory folder.");
            return existing;
        }

        InventoryFolderBase folder = new(id, Normalize(name, "Marketplace Item", 64), owner, type, parentId, version);
        if (!inventory.AddFolder(folder))
            throw new MarketplaceInventoryException(HttpStatusCode.ServiceUnavailable, "Inventory service could not create a Marketplace folder.", true);
        return folder;
    }

    private static void AddOrVerifyItem(IInventoryService inventory, InventoryItemBase item)
    {
        InventoryItemBase? existing = inventory.GetItem(item.Owner, item.ID);
        if (existing != null)
        {
            if (existing.Owner != item.Owner || existing.Folder != item.Folder || existing.AssetID != item.AssetID)
                throw new MarketplaceInventoryException(HttpStatusCode.Conflict, "Deterministic Marketplace item ID collides with another inventory item.");
            return;
        }

        if (!inventory.AddItem(item))
            throw new MarketplaceInventoryException(HttpStatusCode.ServiceUnavailable, "Inventory service could not add a Marketplace inventory item.", true);
    }

    private static InventoryFolderBase EnsureMarketplaceFolder(IInventoryService inventory, UUID ownerId) =>
        EnsureRootChildFolder(inventory, ownerId, MarketplaceFolderName, "marketplace-root");

    private static InventoryFolderBase EnsureRootChildFolder(
        IInventoryService inventory,
        UUID ownerId,
        string name,
        string purpose)
    {
        InventoryFolderBase? root = inventory.GetRootFolder(ownerId);
        if (root == null)
            throw new MarketplaceInventoryException(HttpStatusCode.ServiceUnavailable, "Inventory root folder was not found.", true);
        return EnsureChildFolder(inventory, ownerId, root, name, purpose);
    }

    private static InventoryFolderBase EnsureChildFolder(
        IInventoryService inventory,
        UUID ownerId,
        InventoryFolderBase parent,
        string name,
        string purpose)
    {
        InventoryCollection? content = inventory.GetFolderContent(ownerId, parent.ID);
        if (content != null)
        {
            InventoryFolderBase? named = content.Folders.FirstOrDefault(
                f => f.Owner == ownerId && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (named != null)
                return named;
        }

        UUID id = CreateDeterministicUuid(purpose, ownerId + "|" + parent.ID + "|" + name);
        return AddOrVerifyFolder(inventory, id, name, ownerId, -1, parent.ID, 1);
    }

    private static ProductFolderInfo ToProductInfo(FolderSnapshot snapshot, string message) => new()
    {
        FolderId = snapshot.RootFolderId.ToString(),
        Name = snapshot.RootName,
        Description = snapshot.RootDescription,
        Fingerprint = snapshot.Fingerprint,
        ItemCount = snapshot.Items.Count,
        FolderCount = snapshot.Folders.Count,
        Copy = snapshot.AllCopy,
        Transfer = snapshot.AllTransfer,
        Modify = snapshot.AllModify,
        Message = message
    };

    private static bool UserExists(IUserAccountService userAccounts, UUID scopeId, UUID userId)
    {
        if (userAccounts == null)
            throw new MarketplaceInventoryException(HttpStatusCode.ServiceUnavailable, "User account service is unavailable.", true);
        return userAccounts.GetUserAccount(scopeId, userId) != null;
    }

    // Walks a delivered folder's full tree for a caller's own in-viewer
    // "you got mail" push - kept here as a public helper (rather than private
    // NotifyRecipient logic, as this class had before) specifically so it has
    // no Scene/ScenePresence dependency of its own: the region-side callers
    // that actually HAVE a Scene (DirectDeliveryModule, the old v2 addon) can
    // call this then do their own scene.GetScenePresence/SendBulkUpdateInventory,
    // while Deliver's own notifyRecipient callback param stays a plain
    // delegate - this keeps the whole class callable from Robust (the WebUI
    // checkout), which has no Scene at all.
    public static void CollectDeliveryTree(
        IInventoryService inventory,
        UUID ownerId,
        InventoryFolderBase folder,
        List<InventoryFolderBase> folders,
        List<InventoryItemBase> items)
    {
        CollectTree(inventory, ownerId, folder, folders, items);
    }

    private static void CollectTree(
        IInventoryService inventory,
        UUID ownerId,
        InventoryFolderBase folder,
        List<InventoryFolderBase> folders,
        List<InventoryItemBase> items)
    {
        folders.Add(folder);
        InventoryCollection? content = inventory.GetFolderContent(ownerId, folder.ID);
        if (content == null)
            return;
        items.AddRange(content.Items);
        foreach (InventoryFolderBase child in content.Folders)
            CollectTree(inventory, ownerId, child, folders, items);
    }

    private static UUID CreateDeterministicUuid(string purpose, string value)
    {
        // Retained as the v2 compatibility namespace so existing inventory folder UUIDs remain stable.
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes("Casperia.Marketplace.v2|" + purpose + "|" + value));
        byte[] uuid = new byte[16];
        Array.Copy(bytes, uuid, 16);
        uuid[6] = (byte)((uuid[6] & 0x0f) | 0x50);
        uuid[8] = (byte)((uuid[8] & 0x3f) | 0x80);
        return new UUID(new Guid(uuid));
    }

    private static string Normalize(string? value, string fallback, int max)
    {
        string text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return text.Length <= max ? text : text[..max];
    }

    private sealed class FolderSnapshot
    {
        public FolderSnapshot(UUID rootFolderId, string rootName, string rootDescription)
        {
            RootFolderId = rootFolderId;
            RootName = Normalize(rootName, "Marketplace Item", 64);
            RootDescription = Normalize(rootDescription, string.Empty, 1024);
        }

        public UUID RootFolderId { get; }
        public string RootName { get; }
        public string RootDescription { get; }
        public List<FolderNode> Folders { get; } = new();
        public List<ItemNode> Items { get; } = new();
        public bool AllCopy { get; set; } = true;
        public bool AllTransfer { get; set; } = true;
        public bool AllModify { get; set; } = true;
        public string Fingerprint { get; private set; } = string.Empty;
        public int NodeCount => Folders.Count + Items.Count;

        public void FinalizeFingerprint()
        {
            StringBuilder data = new();
            foreach (FolderNode folder in Folders.OrderBy(f => f.Id))
                data.Append("F|").Append(folder.Id).Append('|').Append(folder.ParentId).Append('|').Append(folder.Name).Append('|').Append(folder.Type).Append('|').Append(folder.Version).Append('\n');
            foreach (ItemNode item in Items.OrderBy(i => i.Id))
                data.Append("I|").Append(item.Id).Append('|').Append(item.FolderId).Append('|').Append(item.AssetId).Append('|').Append(item.Name).Append('|').Append(item.Description).Append('|').Append(item.AssetType).Append('|').Append(item.InvType).Append('|').Append(item.BasePermissions).Append('|').Append(item.CurrentPermissions).Append('|').Append(item.NextPermissions).Append('|').Append(item.EveryOnePermissions).Append('|').Append(item.GroupPermissions).Append('|').Append(item.Flags).Append('\n');
            Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToString()))).ToLowerInvariant();
        }
    }

    private sealed class FolderNode
    {
        public FolderNode(InventoryFolderBase folder)
        {
            Id = folder.ID;
            ParentId = folder.ParentID;
            Name = Normalize(folder.Name, "Marketplace Folder", 64);
            Type = folder.Type;
            Version = folder.Version;
        }
        public UUID Id { get; }
        public UUID ParentId { get; }
        public string Name { get; }
        public short Type { get; }
        public ushort Version { get; }
    }

    private sealed class ItemNode
    {
        private readonly InventoryItemBase m_item;

        public ItemNode(InventoryItemBase item)
        {
            m_item = (InventoryItemBase)item.Clone();
            Id = item.ID;
            FolderId = item.Folder;
            AssetId = item.AssetID;
            Name = Normalize(item.Name, "Marketplace Item", 64);
            Description = Normalize(item.Description, string.Empty, 1024);
            AssetType = item.AssetType;
            InvType = item.InvType;
            BasePermissions = item.BasePermissions;
            CurrentPermissions = item.CurrentPermissions;
            NextPermissions = item.NextPermissions;
            EveryOnePermissions = item.EveryOnePermissions;
            GroupPermissions = item.GroupPermissions;
            Flags = item.Flags;
        }

        public UUID Id { get; }
        public UUID FolderId { get; }
        public UUID AssetId { get; }
        public string Name { get; }
        public string Description { get; }
        public int AssetType { get; }
        public int InvType { get; }
        public uint BasePermissions { get; }
        public uint CurrentPermissions { get; }
        public uint NextPermissions { get; }
        public uint EveryOnePermissions { get; }
        public uint GroupPermissions { get; }
        public uint Flags { get; }

        public InventoryItemBase CreateSnapshotItem(UUID id, UUID serviceAccountId, UUID folderId)
        {
            InventoryItemBase copy = (InventoryItemBase)m_item.Clone();
            copy.ID = id;
            copy.Owner = serviceAccountId;
            copy.Folder = folderId;
            copy.GroupID = UUID.Zero;
            copy.GroupOwned = false;
            copy.GroupPermissions = 0;
            copy.SalePrice = 0;
            copy.SaleType = 0;
            return copy;
        }

        public InventoryItemBase CreateDeliveryItem(UUID id, UUID recipientId, UUID folderId, bool propagatePermissions)
        {
            InventoryItemBase copy = new(id, recipientId)
            {
                CreatorId = m_item.CreatorId,
                CreatorData = m_item.CreatorData,
                AssetID = m_item.AssetID,
                Description = m_item.Description,
                Name = m_item.Name,
                AssetType = m_item.AssetType,
                InvType = m_item.InvType,
                Folder = folderId,
                Flags = m_item.Flags,
                GroupID = UUID.Zero,
                GroupOwned = false,
                SalePrice = 0,
                SaleType = 0,
                CreationDate = m_item.CreationDate
            };

            if (propagatePermissions)
                ApplyNextOwnerPermissions(m_item, copy);
            else
            {
                copy.BasePermissions = m_item.BasePermissions;
                copy.CurrentPermissions = m_item.CurrentPermissions;
                copy.NextPermissions = m_item.NextPermissions;
                copy.EveryOnePermissions = m_item.EveryOnePermissions & m_item.NextPermissions;
                copy.GroupPermissions = m_item.GroupPermissions & m_item.NextPermissions;
            }

            return copy;
        }

        private static void ApplyNextOwnerPermissions(InventoryItemBase source, InventoryItemBase target)
        {
            uint permsMask = ~((uint)PermissionMask.Copy | (uint)PermissionMask.Transfer | (uint)PermissionMask.Modify | (uint)PermissionMask.Export);
            uint nextPerms = permsMask | (source.NextPermissions & ((uint)PermissionMask.Copy | (uint)PermissionMask.Transfer | (uint)PermissionMask.Modify));
            if (nextPerms == permsMask)
                nextPerms |= (uint)PermissionMask.Transfer;

            uint basePerms = source.BasePermissions | (uint)PermissionMask.Move;
            uint ownerPerms = source.CurrentPermissions;
            uint foldedPerms = (source.CurrentPermissions & (uint)PermissionMask.FoldedMask) << (int)PermissionMask.FoldingShift;

            if (foldedPerms != 0 && source.InvType == (int)InventoryType.Object)
            {
                foldedPerms |= permsMask;
                bool rootModify = (source.CurrentPermissions & (uint)PermissionMask.Modify) != 0;
                ownerPerms &= foldedPerms;
                basePerms &= foldedPerms;
                if (rootModify)
                {
                    ownerPerms |= (uint)PermissionMask.Modify;
                    basePerms |= (uint)PermissionMask.Modify;
                }
            }

            ownerPerms &= nextPerms;
            basePerms &= nextPerms;
            basePerms &= ~(uint)PermissionMask.FoldedMask;
            basePerms |= ((basePerms >> 13) & 7)
                | (((basePerms & (uint)PermissionMask.Export) != 0) ? (uint)PermissionMask.FoldedExport : 0);

            target.BasePermissions = basePerms;
            target.CurrentPermissions = ownerPerms;
            target.Flags |= (uint)InventoryItemFlags.ObjectSlamPerm;
            target.Flags &= ~(uint)(InventoryItemFlags.ObjectOverwriteBase
                | InventoryItemFlags.ObjectOverwriteOwner
                | InventoryItemFlags.ObjectOverwriteGroup
                | InventoryItemFlags.ObjectOverwriteEveryone
                | InventoryItemFlags.ObjectOverwriteNextOwner);
            target.NextPermissions = source.NextPermissions;
            target.EveryOnePermissions = source.EveryOnePermissions & nextPerms;
            target.GroupPermissions = 0;
        }
    }
}

public sealed class MarketplaceInventoryException : Exception
{
    public MarketplaceInventoryException(HttpStatusCode statusCode, string message, bool retryable = false)
        : base(message)
    {
        StatusCode = statusCode;
        Retryable = retryable;
    }

    public HttpStatusCode StatusCode { get; }
    public bool Retryable { get; }
}

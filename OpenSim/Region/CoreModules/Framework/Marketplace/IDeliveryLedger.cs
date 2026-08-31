/*
 * Delivery idempotency ledger contract used by MarketplaceInventoryOperations.Deliver.
 * A delivery_id maps to exactly one completed delivery; a retried request with the
 * same delivery_id and matching parameters returns the original receipt instead of
 * delivering a second copy.
 */

namespace OpenSim.Region.CoreModules.Framework.Marketplace
{
    public interface IDeliveryLedger
    {
        bool TryGet(string deliveryId, out DeliveryReceipt receipt);

        bool TryRecord(DeliveryReceipt receipt, out string error);
    }
}

/*
 * Delivery idempotency ledger contract used by MarketplaceInventoryOperations.Deliver
 * (OpenSim.Region.CoreModules.Framework.Marketplace). A delivery_id maps to exactly one
 * completed delivery; a retried request with the same delivery_id and matching
 * parameters returns the original receipt instead of delivering a second copy.
 *
 * Lives in OpenSim.Services.Interfaces (not CoreModules, where
 * MarketplaceInventoryOperations itself lives) so the Robust-hosted
 * MarketplaceListingsService can implement it directly - a Robust service must not
 * reference OpenSim.Region.CoreModules (Scene/LindenUDP/PhysicsModules weight that
 * has no business in Robust.exe); every layer already references Services.Interfaces.
 * The old v2 addon's own file-backed DeliveryLedger implements this same contract
 * independently, with no DB dependency at all.
 */
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    public interface IDeliveryLedger
    {
        bool TryGet(string deliveryId, out DeliveryReceipt receipt);

        bool TryRecord(DeliveryReceipt receipt, out string error);
    }
}

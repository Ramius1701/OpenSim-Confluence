using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    public interface IStoreService
    {
        // Catalog - admin-managed, add/edit/deactivate, not user-generated.
        StoreCatalogItem GetCatalogItem(UUID id);

        // Resident-facing catalog list - IsActive items only.
        List<StoreCatalogItem> GetActiveCatalogItems();

        // Admin catalog list - every item, active or not.
        List<StoreCatalogItem> GetAllCatalogItems();
        bool StoreCatalogItem(StoreCatalogItem item);

        // Orders
        StoreOrder GetOrder(UUID id);

        // A resident's own order history - My Purchases.
        List<StoreOrder> GetOrdersByResident(UUID avatarId);

        // Admin fulfillment/renewal queue - every order.
        List<StoreOrder> GetAllOrders();
        bool StoreOrder(StoreOrder order);

        // Gloebit auth - one row per avatar's OAuth2 authorization.
        StoreGloebitAuth GetGloebitAuth(UUID avatarId);
        bool StoreGloebitAuth(StoreGloebitAuth auth);

        // Gloebit transactions - one row per transaction hold.
        StoreGloebitTransaction GetGloebitTransaction(UUID id);
        bool StoreGloebitTransaction(StoreGloebitTransaction txn);
    }
}

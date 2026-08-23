using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for the WebInterface Store (prim-capacity packs +
    // self-service region orders) - see OpenSim.Framework.StoreCatalogItem/
    // StoreOrder/StoreGloebitAuth/StoreGloebitTransaction for the design
    // rationale.
    public interface IStoreData
    {
        StoreCatalogItem GetCatalogItem(UUID id);
        List<StoreCatalogItem> GetActiveCatalogItems();
        List<StoreCatalogItem> GetAllCatalogItems();
        bool StoreCatalogItem(StoreCatalogItem item);

        StoreOrder GetOrder(UUID id);
        List<StoreOrder> GetOrdersByResident(UUID avatarId);
        List<StoreOrder> GetAllOrders();
        bool StoreOrder(StoreOrder order);

        StoreGloebitAuth GetGloebitAuth(UUID avatarId);
        bool StoreGloebitAuth(StoreGloebitAuth auth);

        StoreGloebitTransaction GetGloebitTransaction(UUID id);
        bool StoreGloebitTransaction(StoreGloebitTransaction txn);
    }
}

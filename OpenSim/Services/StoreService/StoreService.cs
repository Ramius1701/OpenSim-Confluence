using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.StoreService
{
    // Backing service for the WebInterface Store (prim-capacity packs +
    // self-service region orders) - see IStoreService/IStoreData for the
    // design rationale.
    public class StoreService : StoreServiceBase, IStoreService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public StoreService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[STORE SERVICE]: Starting store service");
        }

        public StoreCatalogItem GetCatalogItem(UUID id)
        {
            return m_Database.GetCatalogItem(id);
        }

        public List<StoreCatalogItem> GetActiveCatalogItems()
        {
            return m_Database.GetActiveCatalogItems();
        }

        public List<StoreCatalogItem> GetAllCatalogItems()
        {
            return m_Database.GetAllCatalogItems();
        }

        public bool StoreCatalogItem(StoreCatalogItem item)
        {
            return m_Database.StoreCatalogItem(item);
        }

        public StoreOrder GetOrder(UUID id)
        {
            return m_Database.GetOrder(id);
        }

        public List<StoreOrder> GetOrdersByResident(UUID avatarId)
        {
            return m_Database.GetOrdersByResident(avatarId);
        }

        public List<StoreOrder> GetAllOrders()
        {
            return m_Database.GetAllOrders();
        }

        public bool StoreOrder(StoreOrder order)
        {
            return m_Database.StoreOrder(order);
        }

        public StoreGloebitAuth GetGloebitAuth(UUID avatarId)
        {
            return m_Database.GetGloebitAuth(avatarId);
        }

        public bool StoreGloebitAuth(StoreGloebitAuth auth)
        {
            return m_Database.StoreGloebitAuth(auth);
        }

        public StoreGloebitTransaction GetGloebitTransaction(UUID id)
        {
            return m_Database.GetGloebitTransaction(id);
        }

        public bool StoreGloebitTransaction(StoreGloebitTransaction txn)
        {
            return m_Database.StoreGloebitTransaction(txn);
        }
    }
}

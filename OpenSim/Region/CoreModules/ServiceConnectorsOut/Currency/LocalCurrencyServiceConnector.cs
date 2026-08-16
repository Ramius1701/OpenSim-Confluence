using log4net;
using Mono.Addins;
using Nini.Config;
using System;
using System.Collections.Generic;
using System.Reflection;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenMetaverse;
using OpenSim.Data;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Currency
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LocalCurrencyServiceConnector")]
    public class LocalCurrencyServiceConnector : ISharedRegionModule, ICurrencyService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private List<Scene> m_Scenes = new List<Scene>();
        protected ICurrencyService m_service = null;

        private bool m_Enabled = false;

        #region ISharedRegionModule

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public string Name
        {
            get { return "LocalCurrencyServiceConnector"; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];

            if (moduleConfig == null)
                return;

            string name = moduleConfig.GetString("CurrencyService", "");
            if (name != Name)
                return;

            IConfig userConfig = source.Configs["CurrencyService"];
            if (userConfig == null)
            {
                m_log.Error("[CURRENCY LOCALCONNECTOR]: CurrencyService missing from configuration");
                return;
            }

            string serviceDll = userConfig.GetString("LocalServiceModule", String.Empty);

            if (serviceDll == String.Empty)
            {
                m_log.Error("[CURRENCY LOCALCONNECTOR]: No LocalServiceModule named in section CurrencyService");
                return;
            }

            Object[] args = new Object[] { source };
            try
            {
                m_service = ServerUtils.LoadPlugin<ICurrencyService>(serviceDll, args);
            }
            catch
            {
                m_log.Error("[CURRENCY LOCALCONNECTOR]: Failed to load currency service");
                return;
            }

            if (m_service == null)
            {
                m_log.Error("[CURRENCY LOCALCONNECTOR]: Can't load currency service");
                return;
            }

            m_Enabled = true;
            m_log.Info("[CURRENCY LOCALCONNECTOR]: Enabled!");
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            lock (m_Scenes)
            {
                m_Scenes.Add(scene);
                scene.RegisterModuleInterface<ICurrencyService>(this);
            }
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void PostInitialise()
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            lock (m_Scenes)
            {
                if (m_Scenes.Contains(scene))
                {
                    m_Scenes.Remove(scene);
                    scene.UnregisterModuleInterface<ICurrencyService>(this);
                }
            }
        }

        #endregion ISharedRegionModule

        #region ICurrencyService

        public int GetBalance(UUID agentID)
        {
            return m_service.GetBalance(agentID);
        }

        public int SetBalance(UUID agentID, int amount, string description)
        {
            return m_service.SetBalance(agentID, amount, description);
        }

        public bool Transfer(UUID toID, UUID fromID, int amount, string description, int transactionType, UUID transactionID)
        {
            return m_service.Transfer(toID, fromID, amount, description, transactionType, transactionID);
        }

        public uint NumberOfTransactions(UUID toAgentID, UUID fromAgentID)
        {
            return m_service.NumberOfTransactions(toAgentID, fromAgentID);
        }

        public List<CurrencyTransfer> GetTransactionHistory(UUID toAgentID, UUID fromAgentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            return m_service.GetTransactionHistory(toAgentID, fromAgentID, dateStart, dateEnd, start, count);
        }

        public uint NumberOfPurchases(UUID agentID)
        {
            return m_service.NumberOfPurchases(agentID);
        }

        public List<CurrencyPurchase> GetPurchaseHistory(UUID agentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            return m_service.GetPurchaseHistory(agentID, dateStart, dateEnd, start, count);
        }

        public bool RecordPurchase(UUID agentID, int amount, int realAmountHundredths, string ip)
        {
            return m_service.RecordPurchase(agentID, amount, realAmountHundredths, ip);
        }

        public int GetGroupBalance(UUID groupID)
        {
            return m_service.GetGroupBalance(groupID);
        }

        public bool GroupCurrencyTransfer(UUID groupID, UUID agentID, int amount, string description,
                int transactionType, UUID transactionID, bool payingIntoGroup)
        {
            return m_service.GroupCurrencyTransfer(groupID, agentID, amount, description, transactionType, transactionID, payingIntoGroup);
        }

        public List<CurrencyTransfer> GetGroupTransactionHistory(UUID groupID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            return m_service.GetGroupTransactionHistory(groupID, dateStart, dateEnd, start, count);
        }

        public int PayGroupDividend(UUID groupID, List<UUID> memberIDs, string description)
        {
            return m_service.PayGroupDividend(groupID, memberIDs, description);
        }

        public int GetTotalCirculation()
        {
            return m_service.GetTotalCirculation();
        }

        public int CountAccountsWithBalance()
        {
            return m_service.CountAccountsWithBalance();
        }

        public List<CurrencyBalanceEntry> GetTopBalances(int count)
        {
            return m_service.GetTopBalances(count);
        }

        #endregion ICurrencyService
    }
}

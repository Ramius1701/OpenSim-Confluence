using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // Plain DTOs shared between the ICurrencyData storage layer and the
    // ICurrencyService API layer - lives here (not in OpenSim.Data or
    // OpenSim.Services.Interfaces) for the same reason ExperienceInfoData does:
    // OpenSim.Data can't reference OpenSim.Services.Interfaces without a circular
    // project dependency, and OpenSim.Framework sits below both.
    public class CurrencyTransfer
    {
        public UUID ID = UUID.Zero;
        public UUID ToAgent = UUID.Zero;
        public UUID FromAgent = UUID.Zero;
        public string ToAgentName = string.Empty;
        public string FromAgentName = string.Empty;
        public int Amount = 0;
        public int TransferType = 0;
        public string Description = string.Empty;
        public DateTime TransferDate = DateTime.UtcNow;
        public int ToBalance = 0;
        public int FromBalance = 0;
    }

    public class CurrencyPurchase
    {
        public UUID ID = UUID.Zero;
        public UUID AgentID = UUID.Zero;
        public string IP = string.Empty;
        public int Amount = 0;
        public int RealAmount = 0;
        public DateTime PurchaseDate = DateTime.UtcNow;
    }
}

/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Timers;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.World.Land
{
    // Web-bidding backing (2026-08-12) - the real viewer has no in-world
    // bidding UI at all (confirmed against Firestorm's own
    // llfloaterauction.h/.cpp: that floater is seller/admin tooling for
    // STARTING an auction, never for bidding - real SL auctions were
    // always bid on through the website). DB-backed via IAuctionService
    // (same dual-load pattern as ICurrencyService/IEventsService) so
    // WebInterfaceServiceConnector on Robust can list auctions and accept
    // bids without needing live RPC into this region process - the old
    // in-memory-only m_auctions dictionary is gone; the database is now
    // the single source of truth for auction/bid state, read fresh on
    // every call rather than cached.
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "AuctionModule")]
    public class AuctionModule : INonSharedRegionModule, IAuctionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private bool m_enabled = true;
        private IAuctionService m_auctionService;

        // Default auction length when started from the console with no
        // explicit end time - matches the stock AuctionInfo.AuctionLength
        // default (7 days) this module already declared before the DB
        // backing existed.
        private const int DefaultAuctionDays = 7;

        // Sweeps for auctions whose EndsAt has passed but are still
        // Status=Active and closes them automatically - without this, an
        // auction only ever ends if an admin remembers to run
        // "land auction end", which defeats the point of letting residents
        // bid unattended over several days.
        private Timer m_expirySweepTimer;

        public string Name
        {
            get { return "AuctionModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig auctionConfig = source.Configs["AuctionModule"];
            if (auctionConfig != null)
                m_enabled = auctionConfig.GetBoolean("Enabled", true);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;
            m_scene.RegisterModuleInterface<IAuctionModule>(this);

            MainConsole.Instance.Commands.AddCommand("Land", false,
                "land auction start",
                "land auction start <local id> [min bid] [days]",
                "Start a web-biddable parcel auction for the given parcel local id. " +
                "Residents bid from the WebInterface, not the viewer (see AuctionModule's " +
                "class comment for why) - min bid defaults to 0, length defaults to 7 days.",
                HandleAuctionStart);

            MainConsole.Instance.Commands.AddCommand("Land", false,
                "land auction bid",
                "land auction bid <local id> <bidder uuid> <amount>",
                "Record a bid on a running parcel auction from the console - same code path " +
                "a resident's web bid uses, for testing/admin use.", HandleAuctionBid);

            MainConsole.Instance.Commands.AddCommand("Land", false,
                "land auction end",
                "land auction end <local id>",
                "End a parcel auction immediately, selling to the highest bidder", HandleAuctionEnd);

            MainConsole.Instance.Commands.AddCommand("Land", false,
                "land auction show",
                "land auction show <local id>",
                "Show the current status of a parcel auction", HandleAuctionShow);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_expirySweepTimer?.Stop();
            m_expirySweepTimer?.Dispose();
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            m_auctionService = scene.RequestModuleInterface<IAuctionService>();
            if (m_auctionService == null)
            {
                m_log.Error("[AUCTION MODULE]: No IAuctionService available - is [Modules] AuctionService "
                        + "and [AuctionService] LocalServiceModule configured? Web-bidding cannot function "
                        + "without it; console commands will also fail.");
                m_enabled = false;
                return;
            }

            // Every 2 minutes - frequent enough that a resident isn't left
            // wondering why their winning auction hasn't closed for hours,
            // infrequent enough not to hammer the DB on a busy grid running
            // several regions.
            m_expirySweepTimer = new Timer(120000);
            m_expirySweepTimer.Elapsed += (sender, e) => SweepExpiredAuctions();
            m_expirySweepTimer.AutoReset = true;
            m_expirySweepTimer.Start();
        }

        public void Close()
        {
        }

        private void SweepExpiredAuctions()
        {
            try
            {
                List<LandAuction> expired = m_auctionService.GetExpiredActive(DateTime.UtcNow);
                foreach (LandAuction auction in expired)
                {
                    // Only close auctions for parcels this specific region
                    // process actually hosts - GetExpiredActive is grid-wide
                    // (the DB has no concept of "which region process is
                    // asking"), and only the owning region has the live
                    // Scene/ILandObject needed to actually transfer the land.
                    if (auction.RegionID != m_scene.RegionInfo.RegionID)
                        continue;

                    CloseAuction(auction.LocalID);
                }
            }
            catch (Exception e)
            {
                m_log.Error("[AUCTION MODULE]: Error sweeping expired auctions", e);
            }
        }

        #region Console commands

        private bool TryGetConsoleScene(out Scene scene)
        {
            scene = null;
            if (MainConsole.Instance.ConsoleScene is not null && MainConsole.Instance.ConsoleScene != m_scene)
                return false;
            scene = m_scene;
            return true;
        }

        private void HandleAuctionStart(string module, string[] cmdparams)
        {
            if (!TryGetConsoleScene(out Scene scene))
                return;

            if (cmdparams.Length < 4 || !int.TryParse(cmdparams[3], out int localID))
            {
                MainConsole.Instance.Output("Usage: land auction start <local id> [min bid] [days]");
                return;
            }

            int minBid = 0;
            if (cmdparams.Length >= 5 && !int.TryParse(cmdparams[4], out minBid))
            {
                MainConsole.Instance.Output("Min bid must be a whole number");
                return;
            }

            int days = DefaultAuctionDays;
            if (cmdparams.Length >= 6 && !int.TryParse(cmdparams[5], out days))
            {
                MainConsole.Instance.Output("Days must be a whole number");
                return;
            }

            ILandObject land = scene.LandChannel.GetLandObject(localID);
            if (land is null)
            {
                MainConsole.Instance.Output("No parcel found with local id {0}", localID);
                return;
            }

            if (m_auctionService.GetActiveForParcel(scene.RegionInfo.RegionID, localID) != null)
            {
                MainConsole.Instance.Output("Parcel \"{0}\" already has an active auction", land.LandData.Name);
                return;
            }

            StartAuctionInternal(land, minBid, days);
            MainConsole.Instance.Output("Started {0}-day web-biddable auction for parcel \"{1}\" (local id {2}), min bid {3}",
                    days, land.LandData.Name, localID, minBid);
        }

        private void HandleAuctionBid(string module, string[] cmdparams)
        {
            if (!TryGetConsoleScene(out Scene scene))
                return;

            if (cmdparams.Length < 6 || !int.TryParse(cmdparams[3], out int localID) ||
                !UUID.TryParse(cmdparams[4], out UUID bidderID) || !int.TryParse(cmdparams[5], out int amount))
            {
                MainConsole.Instance.Output("Usage: land auction bid <local id> <bidder uuid> <amount>");
                return;
            }

            LandAuction auction = m_auctionService.GetActiveForParcel(scene.RegionInfo.RegionID, localID);
            if (auction is null)
            {
                MainConsole.Instance.Output("No auction is running for local id {0}", localID);
                return;
            }

            if (m_auctionService.PlaceBid(auction.ID, bidderID, amount))
                MainConsole.Instance.Output("Recorded bid of {0} from {1} on local id {2}", amount, bidderID, localID);
            else
                MainConsole.Instance.Output("Bid rejected - must be higher than the current bid ({0}) and at least the minimum ({1})",
                        auction.HighestBid, auction.MinBid);
        }

        private void HandleAuctionEnd(string module, string[] cmdparams)
        {
            if (!TryGetConsoleScene(out Scene scene))
                return;

            if (cmdparams.Length < 4 || !int.TryParse(cmdparams[3], out int localID))
            {
                MainConsole.Instance.Output("Usage: land auction end <local id>");
                return;
            }

            if (m_auctionService.GetActiveForParcel(scene.RegionInfo.RegionID, localID) is null)
            {
                MainConsole.Instance.Output("No auction is running for local id {0}", localID);
                return;
            }

            CloseAuction(localID);
            MainConsole.Instance.Output("Ended auction for local id {0}", localID);
        }

        private void HandleAuctionShow(string module, string[] cmdparams)
        {
            if (!TryGetConsoleScene(out Scene scene))
                return;

            if (cmdparams.Length < 4 || !int.TryParse(cmdparams[3], out int localID))
            {
                MainConsole.Instance.Output("Usage: land auction show <local id>");
                return;
            }

            LandAuction auction = m_auctionService.GetActiveForParcel(scene.RegionInfo.RegionID, localID);
            if (auction is null)
            {
                MainConsole.Instance.Output("No auction is running for local id {0}", localID);
                return;
            }

            List<LandAuctionBid> bids = m_auctionService.GetBidHistory(auction.ID, 100);
            MainConsole.Instance.Output("Auction for local id {0} started {1}, ends {2}, min bid {3}, {4} bid(s)",
                    localID, auction.StartedAt, auction.EndsAt, auction.MinBid, bids.Count);
            foreach (LandAuctionBid bid in bids)
                MainConsole.Instance.Output("  {0} bid {1} at {2}", bid.BidderID, bid.Amount, bid.BidTime);
        }

        #endregion

        #region IAuctionModule Members

        // Stock IAuctionModule entry point - starts a web-biddable auction
        // with default min bid (0, any bid accepted) and default length
        // (7 days). Use "land auction start <local id> [min bid] [days]"
        // from the console for real control over either.
        public void StartAuction(int localID, UUID snapshotID)
        {
            ILandObject land = m_scene.LandChannel.GetLandObject(localID);
            if (land is null)
                return;

            land.LandData.SnapshotID = snapshotID;
            StartAuctionInternal(land, 0, DefaultAuctionDays);
        }

        private void StartAuctionInternal(ILandObject land, int minBid, int days)
        {
            land.LandData.AuctionID = (uint)Util.RandomClass.Next(0, int.MaxValue);
            land.LandData.Status = ParcelStatus.Leased;

            LandAuction auction = new LandAuction
            {
                ID = UUID.Random(),
                RegionID = m_scene.RegionInfo.RegionID,
                RegionName = m_scene.RegionInfo.RegionName,
                LocalID = land.LandData.LocalID,
                ParcelName = land.LandData.Name,
                SnapshotID = land.LandData.SnapshotID,
                OwnerID = land.LandData.OwnerID,
                MinBid = minBid < 0 ? 0 : minBid,
                StartedAt = DateTime.UtcNow,
                EndsAt = DateTime.UtcNow.AddDays(days <= 0 ? DefaultAuctionDays : days),
                Status = LandAuctionStatus.Active
            };
            m_auctionService.Store(auction);

            land.SendLandUpdateToAvatarsOverMe();
        }

        // Stock IAuctionModule entry point - see HandleAuctionBid for the
        // console path, which reports success/failure directly rather than
        // swallowing it the way this void-returning interface method must.
        public void AddAuctionBid(int localID, UUID bidderID, int bid)
        {
            LandAuction auction = m_auctionService.GetActiveForParcel(m_scene.RegionInfo.RegionID, localID);
            if (auction is null)
                return;

            m_auctionService.PlaceBid(auction.ID, bidderID, bid);
        }

        // Stock IAuctionModule shape (AuctionInfo/AuctionBid) reconstructed
        // on demand from the DB-backed LandAuction/LandAuctionBid records -
        // there's no separate in-memory copy to keep in sync anymore.
        public AuctionInfo GetAuctionInfo(int localID)
        {
            LandAuction auction = m_auctionService.GetActiveForParcel(m_scene.RegionInfo.RegionID, localID);
            if (auction is null)
                return null;

            AuctionInfo info = new AuctionInfo
            {
                AuctionStart = auction.StartedAt
            };

            foreach (LandAuctionBid bid in m_auctionService.GetBidHistory(auction.ID, 1000))
            {
                info.AuctionBids.Add(new AuctionBid
                {
                    Amount = bid.Amount,
                    AuctionBidder = bid.BidderID,
                    TimeBid = bid.BidTime
                });
            }

            return info;
        }

        // Stock IAuctionModule entry point - ends the named parcel's
        // auction immediately regardless of EndsAt, same as before this
        // was DB-backed. The automatic expiry sweep (SweepExpiredAuctions)
        // calls the same CloseAuction helper this delegates to.
        public void AuctionEnd(int localID)
        {
            CloseAuction(localID);
        }

        // Shared close logic - real money changes hands here, so this is
        // the one place both the manual "land auction end" console command
        // and the automatic expiry sweep both go through, rather than two
        // slightly-diverging copies of the same charge-and-transfer logic.
        private void CloseAuction(int localID)
        {
            ILandObject land = m_scene.LandChannel.GetLandObject(localID);
            if (land is null)
                return;

            LandAuction auction = m_auctionService.GetActiveForParcel(m_scene.RegionInfo.RegionID, localID);
            if (auction is null)
                return;

            List<LandAuctionBid> bids = m_auctionService.GetBidHistory(auction.ID, 1000);

            LandAuctionBid highestBid = null;
            foreach (LandAuctionBid bid in bids)
            {
                if (highestBid is null || bid.Amount > highestBid.Amount)
                    highestBid = bid;
            }

            if (highestBid is null)
            {
                m_log.InfoFormat("[AUCTION MODULE]: Auction for parcel \"{0}\" ended with no bids", land.LandData.Name);
                m_auctionService.EndAuction(auction.ID, LandAuctionStatus.Ended, UUID.Zero, 0);
                return;
            }

            // Bug fix (pre-dates web-bidding): this used to transfer
            // ownership straight to the highest bidder without ever
            // charging them - land.UpdateLandSold() only moves ownership,
            // it has no idea about currency at all. The normal
            // client-driven land-buy path (LandManagementModule's
            // EventManagerOnLandBuy) only fires after a currency-module
            // listener sets LandBuyArgs.economyValidated = true, which is
            // where the actual charge happens; an auction ending has no
            // client-sent LandBuyArgs to hook, so the charge has to happen
            // directly here, through the generic IMoneyModule interface
            // (works with whatever economy module is actually configured -
            // ConfluenceCurrencyModule, Gloebit, DTLNSLMoneyModule, etc. -
            // rather than assuming one specific implementation).
            IMoneyModule moneyModule = m_scene.RequestModuleInterface<IMoneyModule>();
            if (moneyModule is not null && highestBid.Amount > 0)
            {
                if (!moneyModule.AmountCovered(highestBid.BidderID, highestBid.Amount))
                {
                    m_log.WarnFormat(
                        "[AUCTION MODULE]: Highest bidder for parcel \"{0}\" no longer has sufficient funds ({1}) - auction cancelled, no ownership change made",
                        land.LandData.Name, highestBid.Amount);
                    m_auctionService.EndAuction(auction.ID, LandAuctionStatus.Cancelled, UUID.Zero, 0);
                    return;
                }

                string payDescription = "Land auction for parcel \"" + land.LandData.Name + "\"";
                if (!moneyModule.MoveMoney(highestBid.BidderID, land.LandData.OwnerID, highestBid.Amount,
                        MoneyTransactionType.LandAuction, payDescription))
                {
                    m_log.WarnFormat(
                        "[AUCTION MODULE]: Payment failed for parcel \"{0}\" auction - auction cancelled, no ownership change made",
                        land.LandData.Name);
                    m_auctionService.EndAuction(auction.ID, LandAuctionStatus.Cancelled, UUID.Zero, 0);
                    return;
                }
            }

            m_auctionService.EndAuction(auction.ID, LandAuctionStatus.Ended, highestBid.BidderID, highestBid.Amount);

            IMessageTransferModule messageTransfer = m_scene.RequestModuleInterface<IMessageTransferModule>();
            if (messageTransfer is not null)
            {
                string message = "You won the auction for the parcel " + land.LandData.Name +
                    ", paying " + highestBid.Amount + " for it";

                messageTransfer.SendInstantMessage(new GridInstantMessage(
                        m_scene, UUID.Zero, "System", highestBid.BidderID,
                        (byte)InstantMessageDialog.MessageBox, false, message,
                        UUID.Random(), true, Vector3.Zero, Array.Empty<byte>(), true),
                        delegate (bool success) { });
            }

            land.UpdateLandSold(highestBid.BidderID, UUID.Zero, false, land.LandData.AuctionID,
                highestBid.Amount, land.LandData.Area);
        }

        #endregion
    }
}

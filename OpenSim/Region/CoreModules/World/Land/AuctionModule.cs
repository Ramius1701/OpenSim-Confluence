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
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Land
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "AuctionModule")]
    public class AuctionModule : INonSharedRegionModule, IAuctionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private bool m_enabled = true;
        private readonly Dictionary<int, AuctionInfo> m_auctions = new Dictionary<int, AuctionInfo>();

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
                "land auction start <local id>",
                "Start a parcel auction for the given parcel local id", HandleAuctionStart);

            MainConsole.Instance.Commands.AddCommand("Land", false,
                "land auction bid",
                "land auction bid <local id> <bidder uuid> <amount>",
                "Record a bid on a running parcel auction", HandleAuctionBid);

            MainConsole.Instance.Commands.AddCommand("Land", false,
                "land auction end",
                "land auction end <local id>",
                "End a parcel auction, selling to the highest bidder", HandleAuctionEnd);

            MainConsole.Instance.Commands.AddCommand("Land", false,
                "land auction show",
                "land auction show <local id>",
                "Show the current status of a parcel auction", HandleAuctionShow);
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void Close()
        {
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
                MainConsole.Instance.Output("Usage: land auction start <local id>");
                return;
            }

            ILandObject land = scene.LandChannel.GetLandObject(localID);
            if (land is null)
            {
                MainConsole.Instance.Output("No parcel found with local id {0}", localID);
                return;
            }

            StartAuction(localID, land.LandData.SnapshotID);
            MainConsole.Instance.Output("Started auction for parcel \"{0}\" (local id {1})", land.LandData.Name, localID);
        }

        private void HandleAuctionBid(string module, string[] cmdparams)
        {
            if (!TryGetConsoleScene(out _))
                return;

            if (cmdparams.Length < 6 || !int.TryParse(cmdparams[3], out int localID) ||
                !UUID.TryParse(cmdparams[4], out UUID bidderID) || !int.TryParse(cmdparams[5], out int amount))
            {
                MainConsole.Instance.Output("Usage: land auction bid <local id> <bidder uuid> <amount>");
                return;
            }

            if (GetAuctionInfo(localID) is null)
            {
                MainConsole.Instance.Output("No auction is running for local id {0}", localID);
                return;
            }

            AddAuctionBid(localID, bidderID, amount);
            MainConsole.Instance.Output("Recorded bid of {0} from {1} on local id {2}", amount, bidderID, localID);
        }

        private void HandleAuctionEnd(string module, string[] cmdparams)
        {
            if (!TryGetConsoleScene(out _))
                return;

            if (cmdparams.Length < 4 || !int.TryParse(cmdparams[3], out int localID))
            {
                MainConsole.Instance.Output("Usage: land auction end <local id>");
                return;
            }

            if (GetAuctionInfo(localID) is null)
            {
                MainConsole.Instance.Output("No auction is running for local id {0}", localID);
                return;
            }

            AuctionEnd(localID);
            MainConsole.Instance.Output("Ended auction for local id {0}", localID);
        }

        private void HandleAuctionShow(string module, string[] cmdparams)
        {
            if (!TryGetConsoleScene(out _))
                return;

            if (cmdparams.Length < 4 || !int.TryParse(cmdparams[3], out int localID))
            {
                MainConsole.Instance.Output("Usage: land auction show <local id>");
                return;
            }

            AuctionInfo info = GetAuctionInfo(localID);
            if (info is null)
            {
                MainConsole.Instance.Output("No auction is running for local id {0}", localID);
                return;
            }

            MainConsole.Instance.Output("Auction for local id {0} started {1}, {2} bid(s)",
                localID, info.AuctionStart, info.AuctionBids.Count);
            foreach (AuctionBid bid in info.AuctionBids)
                MainConsole.Instance.Output("  {0} bid {1} at {2}", bid.AuctionBidder, bid.Amount, bid.TimeBid);
        }

        #endregion

        #region IAuctionModule Members

        public void StartAuction(int localID, UUID snapshotID)
        {
            ILandObject land = m_scene.LandChannel.GetLandObject(localID);
            if (land is null)
                return;

            land.LandData.SnapshotID = snapshotID;
            land.LandData.AuctionID = (uint)Util.RandomClass.Next(0, int.MaxValue);
            land.LandData.Status = ParcelStatus.Leased;

            lock (m_auctions)
                m_auctions[localID] = new AuctionInfo();

            land.SendLandUpdateToAvatarsOverMe();
        }

        public void AddAuctionBid(int localID, UUID bidderID, int bid)
        {
            AuctionInfo info = GetAuctionInfo(localID);
            if (info is null)
                return;

            lock (m_auctions)
            {
                info.AuctionBids.Add(new AuctionBid
                {
                    Amount = bid,
                    AuctionBidder = bidderID,
                    TimeBid = DateTime.UtcNow
                });
            }
        }

        public AuctionInfo GetAuctionInfo(int localID)
        {
            lock (m_auctions)
            {
                m_auctions.TryGetValue(localID, out AuctionInfo info);
                return info;
            }
        }

        public void AuctionEnd(int localID)
        {
            ILandObject land = m_scene.LandChannel.GetLandObject(localID);
            if (land is null)
                return;

            AuctionInfo info = GetAuctionInfo(localID);
            if (info is null)
                return;

            AuctionBid highestBid = null;
            foreach (AuctionBid bid in info.AuctionBids)
            {
                if (highestBid is null || bid.Amount > highestBid.Amount)
                    highestBid = bid;
            }

            lock (m_auctions)
                m_auctions.Remove(localID);

            if (highestBid is null)
            {
                m_log.InfoFormat("[AUCTION MODULE]: Auction for parcel \"{0}\" ended with no bids", land.LandData.Name);
                return;
            }

            IMessageTransferModule messageTransfer = m_scene.RequestModuleInterface<IMessageTransferModule>();
            if (messageTransfer is not null)
            {
                string message = "You won the auction for the parcel " + land.LandData.Name +
                    ", paying " + highestBid.Amount + " for it";

                messageTransfer.SendInstantMessage(new GridInstantMessage(
                        m_scene, UUID.Zero, "System", highestBid.AuctionBidder,
                        (byte)InstantMessageDialog.MessageBox, false, message,
                        UUID.Random(), true, Vector3.Zero, Array.Empty<byte>(), true),
                        delegate (bool success) { });
            }

            land.UpdateLandSold(highestBid.AuctionBidder, UUID.Zero, false, land.LandData.AuctionID,
                highestBid.Amount, land.LandData.Area);
        }

        #endregion
    }
}

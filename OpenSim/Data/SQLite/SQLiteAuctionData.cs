using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // Backing store for web-bidding on land auctions - see
    // OpenSim.Data.IAuctionData for the design rationale.
    public class SQLiteAuctionData : IAuctionData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        private const string SelectColumns =
                "ID, RegionID, RegionName, LocalID, ParcelName, SnapshotID, OwnerID, MinBid, " +
                "StartedAt, EndsAt, Status, HighestBid, HighestBidderID, WinnerID, WinningAmount";

        public SQLiteAuctionData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "Auctions");
            m.Update();
        }

        public LandAuction Get(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + SelectColumns + " FROM land_auctions WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadItem(reader);
                    }
                }
            }

            return null;
        }

        public LandAuction GetActiveForParcel(UUID regionId, int localId)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + SelectColumns + " FROM land_auctions " +
                        "WHERE RegionID = :regionId AND LocalID = :localId AND Status = :active LIMIT 1", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":regionId", regionId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":localId", localId));
                    cmd.Parameters.Add(new SQLiteParameter(":active", (int)LandAuctionStatus.Active));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadItem(reader);
                    }
                }
            }

            return null;
        }

        public List<LandAuction> GetActive()
        {
            List<LandAuction> results = new List<LandAuction>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + SelectColumns + " FROM land_auctions WHERE Status = :active ORDER BY EndsAt ASC", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":active", (int)LandAuctionStatus.Active));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadItem(reader));
                    }
                }
            }

            return results;
        }

        public List<LandAuction> GetExpiredActive(DateTime now)
        {
            List<LandAuction> results = new List<LandAuction>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + SelectColumns + " FROM land_auctions WHERE Status = :active AND EndsAt <= :now", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":active", (int)LandAuctionStatus.Active));
                    cmd.Parameters.Add(new SQLiteParameter(":now", (int)Utils.DateTimeToUnixTime(now)));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadItem(reader));
                    }
                }
            }

            return results;
        }

        public bool Store(LandAuction auction)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO land_auctions (" + SelectColumns + ") VALUES (" +
                        ":id, :regionId, :regionName, :localId, :parcelName, :snapshotId, :ownerId, :minBid, " +
                        ":startedAt, :endsAt, :status, :highestBid, :highestBidderId, :winnerId, :winningAmount)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", auction.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":regionId", auction.RegionID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":regionName", auction.RegionName));
                    cmd.Parameters.Add(new SQLiteParameter(":localId", auction.LocalID));
                    cmd.Parameters.Add(new SQLiteParameter(":parcelName", auction.ParcelName));
                    cmd.Parameters.Add(new SQLiteParameter(":snapshotId", auction.SnapshotID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":ownerId", auction.OwnerID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":minBid", auction.MinBid));
                    cmd.Parameters.Add(new SQLiteParameter(":startedAt", (int)Utils.DateTimeToUnixTime(auction.StartedAt)));
                    cmd.Parameters.Add(new SQLiteParameter(":endsAt", (int)Utils.DateTimeToUnixTime(auction.EndsAt)));
                    cmd.Parameters.Add(new SQLiteParameter(":status", (int)auction.Status));
                    cmd.Parameters.Add(new SQLiteParameter(":highestBid", auction.HighestBid));
                    cmd.Parameters.Add(new SQLiteParameter(":highestBidderId", auction.HighestBidderID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":winnerId", auction.WinnerID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":winningAmount", auction.WinningAmount));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        // Atomic highest-bid check - see MySqlAuctionData.PlaceBid for the
        // full rationale. SQLite's writer lock (single-writer, this
        // process's own `lock (this)` on top of that) makes this safe the
        // same way the MySQL row lock does.
        public bool PlaceBid(UUID auctionId, UUID bidderId, int amount)
        {
            lock (this)
            {
                int updated;
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "UPDATE land_auctions SET HighestBid = :amount, HighestBidderID = :bidderId " +
                        "WHERE ID = :id AND Status = :active AND HighestBid < :amount AND MinBid <= :amount", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":amount", amount));
                    cmd.Parameters.Add(new SQLiteParameter(":bidderId", bidderId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":id", auctionId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":active", (int)LandAuctionStatus.Active));

                    updated = cmd.ExecuteNonQuery();
                }

                if (updated == 0)
                    return false;

                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT INTO land_auction_bids (ID, AuctionID, BidderID, Amount, BidTime) " +
                        "VALUES (:id, :auctionId, :bidderId, :amount, :bidTime)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", UUID.Random().ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":auctionId", auctionId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":bidderId", bidderId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":amount", amount));
                    cmd.Parameters.Add(new SQLiteParameter(":bidTime", (int)Util.UnixTimeSinceEpoch()));

                    cmd.ExecuteNonQuery();
                }

                return true;
            }
        }

        public List<LandAuctionBid> GetBidHistory(UUID auctionId, int count)
        {
            List<LandAuctionBid> results = new List<LandAuctionBid>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT ID, AuctionID, BidderID, Amount, BidTime FROM land_auction_bids " +
                        "WHERE AuctionID = :auctionId ORDER BY BidTime DESC LIMIT :count", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":auctionId", auctionId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 20 : count));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(new LandAuctionBid
                            {
                                ID = UUID.Parse(reader.GetString(0)),
                                AuctionID = UUID.Parse(reader.GetString(1)),
                                BidderID = UUID.Parse(reader.GetString(2)),
                                Amount = Convert.ToInt32(reader.GetValue(3)),
                                BidTime = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(4)))
                            });
                        }
                    }
                }
            }

            return results;
        }

        public bool EndAuction(UUID auctionId, LandAuctionStatus finalStatus, UUID winnerId, int winningAmount)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "UPDATE land_auctions SET Status = :status, WinnerID = :winnerId, WinningAmount = :winningAmount " +
                        "WHERE ID = :id AND Status = :active", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":status", (int)finalStatus));
                    cmd.Parameters.Add(new SQLiteParameter(":winnerId", winnerId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":winningAmount", winningAmount));
                    cmd.Parameters.Add(new SQLiteParameter(":id", auctionId.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":active", (int)LandAuctionStatus.Active));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static LandAuction ReadItem(IDataReader reader)
        {
            return new LandAuction
            {
                ID = UUID.Parse(reader.GetString(0)),
                RegionID = UUID.Parse(reader.GetString(1)),
                RegionName = reader.GetString(2),
                LocalID = Convert.ToInt32(reader.GetValue(3)),
                ParcelName = reader.GetString(4),
                SnapshotID = UUID.Parse(reader.GetString(5)),
                OwnerID = UUID.Parse(reader.GetString(6)),
                MinBid = Convert.ToInt32(reader.GetValue(7)),
                StartedAt = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(8))),
                EndsAt = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(9))),
                Status = (LandAuctionStatus)Convert.ToInt32(reader.GetValue(10)),
                HighestBid = Convert.ToInt32(reader.GetValue(11)),
                HighestBidderID = UUID.Parse(reader.GetString(12)),
                WinnerID = UUID.Parse(reader.GetString(13)),
                WinningAmount = Convert.ToInt32(reader.GetValue(14))
            };
        }
    }
}

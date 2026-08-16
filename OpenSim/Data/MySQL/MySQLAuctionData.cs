using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    // Backing store for web-bidding on land auctions - see
    // OpenSim.Data.IAuctionData for the design rationale.
    public class MySqlAuctionData : MySqlFramework, IAuctionData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        private const string SelectColumns =
                "ID, RegionID, RegionName, LocalID, ParcelName, SnapshotID, OwnerID, MinBid, " +
                "StartedAt, EndsAt, Status, HighestBid, HighestBidderID, WinnerID, WinningAmount";

        public MySqlAuctionData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "Auctions");
                m.Update();
                dbcon.Close();
            }
        }

        public LandAuction Get(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + SelectColumns + " FROM land_auctions WHERE ID = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());

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
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + SelectColumns + " FROM land_auctions " +
                        "WHERE RegionID = ?RegionID AND LocalID = ?LocalID AND Status = ?Active LIMIT 1", dbcon))
                {
                    cmd.Parameters.AddWithValue("?RegionID", regionId.ToString());
                    cmd.Parameters.AddWithValue("?LocalID", localId);
                    cmd.Parameters.AddWithValue("?Active", (int)LandAuctionStatus.Active);

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

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + SelectColumns + " FROM land_auctions WHERE Status = ?Active ORDER BY EndsAt ASC", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Active", (int)LandAuctionStatus.Active);

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

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + SelectColumns + " FROM land_auctions " +
                        "WHERE Status = ?Active AND EndsAt <= ?Now", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Active", (int)LandAuctionStatus.Active);
                    cmd.Parameters.AddWithValue("?Now", Utils.DateTimeToUnixTime(now));

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
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO land_auctions (" + SelectColumns + ") VALUES (" +
                        "?ID, ?RegionID, ?RegionName, ?LocalID, ?ParcelName, ?SnapshotID, ?OwnerID, ?MinBid, " +
                        "?StartedAt, ?EndsAt, ?Status, ?HighestBid, ?HighestBidderID, ?WinnerID, ?WinningAmount)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", auction.ID.ToString());
                    cmd.Parameters.AddWithValue("?RegionID", auction.RegionID.ToString());
                    cmd.Parameters.AddWithValue("?RegionName", auction.RegionName);
                    cmd.Parameters.AddWithValue("?LocalID", auction.LocalID);
                    cmd.Parameters.AddWithValue("?ParcelName", auction.ParcelName);
                    cmd.Parameters.AddWithValue("?SnapshotID", auction.SnapshotID.ToString());
                    cmd.Parameters.AddWithValue("?OwnerID", auction.OwnerID.ToString());
                    cmd.Parameters.AddWithValue("?MinBid", auction.MinBid);
                    cmd.Parameters.AddWithValue("?StartedAt", Utils.DateTimeToUnixTime(auction.StartedAt));
                    cmd.Parameters.AddWithValue("?EndsAt", Utils.DateTimeToUnixTime(auction.EndsAt));
                    cmd.Parameters.AddWithValue("?Status", (int)auction.Status);
                    cmd.Parameters.AddWithValue("?HighestBid", auction.HighestBid);
                    cmd.Parameters.AddWithValue("?HighestBidderID", auction.HighestBidderID.ToString());
                    cmd.Parameters.AddWithValue("?WinnerID", auction.WinnerID.ToString());
                    cmd.Parameters.AddWithValue("?WinningAmount", auction.WinningAmount);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        // Atomic highest-bid check: the UPDATE's WHERE clause only matches
        // (and only then commits) if the auction is still Active and this
        // bid actually beats the current highest - MySQL's row lock on the
        // UPDATE is what makes two near-simultaneous bids resolve safely
        // rather than a read-then-write race.
        public bool PlaceBid(UUID auctionId, UUID bidderId, int amount)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                int updated;
                using (MySqlCommand cmd = new MySqlCommand(
                        "UPDATE land_auctions SET HighestBid = ?Amount, HighestBidderID = ?BidderID " +
                        "WHERE ID = ?ID AND Status = ?Active AND HighestBid < ?Amount AND MinBid <= ?Amount", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Amount", amount);
                    cmd.Parameters.AddWithValue("?BidderID", bidderId.ToString());
                    cmd.Parameters.AddWithValue("?ID", auctionId.ToString());
                    cmd.Parameters.AddWithValue("?Active", (int)LandAuctionStatus.Active);

                    updated = cmd.ExecuteNonQuery();
                }

                if (updated == 0)
                    return false;

                using (MySqlCommand cmd = new MySqlCommand(
                        "INSERT INTO land_auction_bids (ID, AuctionID, BidderID, Amount, BidTime) " +
                        "VALUES (?ID, ?AuctionID, ?BidderID, ?Amount, ?BidTime)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", UUID.Random().ToString());
                    cmd.Parameters.AddWithValue("?AuctionID", auctionId.ToString());
                    cmd.Parameters.AddWithValue("?BidderID", bidderId.ToString());
                    cmd.Parameters.AddWithValue("?Amount", amount);
                    cmd.Parameters.AddWithValue("?BidTime", Util.UnixTimeSinceEpoch());

                    cmd.ExecuteNonQuery();
                }

                return true;
            }
        }

        public List<LandAuctionBid> GetBidHistory(UUID auctionId, int count)
        {
            List<LandAuctionBid> results = new List<LandAuctionBid>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT ID, AuctionID, BidderID, Amount, BidTime FROM land_auction_bids " +
                        "WHERE AuctionID = ?AuctionID ORDER BY BidTime DESC LIMIT ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?AuctionID", auctionId.ToString());
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 20 : count);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(new LandAuctionBid
                            {
                                ID = UUID.Parse(reader["ID"].ToString()),
                                AuctionID = UUID.Parse(reader["AuctionID"].ToString()),
                                BidderID = UUID.Parse(reader["BidderID"].ToString()),
                                Amount = Convert.ToInt32(reader["Amount"]),
                                BidTime = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader["BidTime"]))
                            });
                        }
                    }
                }
            }

            return results;
        }

        public bool EndAuction(UUID auctionId, LandAuctionStatus finalStatus, UUID winnerId, int winningAmount)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "UPDATE land_auctions SET Status = ?Status, WinnerID = ?WinnerID, WinningAmount = ?WinningAmount " +
                        "WHERE ID = ?ID AND Status = ?Active", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Status", (int)finalStatus);
                    cmd.Parameters.AddWithValue("?WinnerID", winnerId.ToString());
                    cmd.Parameters.AddWithValue("?WinningAmount", winningAmount);
                    cmd.Parameters.AddWithValue("?ID", auctionId.ToString());
                    cmd.Parameters.AddWithValue("?Active", (int)LandAuctionStatus.Active);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static LandAuction ReadItem(IDataReader reader)
        {
            return new LandAuction
            {
                ID = UUID.Parse(reader["ID"].ToString()),
                RegionID = UUID.Parse(reader["RegionID"].ToString()),
                RegionName = reader["RegionName"].ToString(),
                LocalID = Convert.ToInt32(reader["LocalID"]),
                ParcelName = reader["ParcelName"].ToString(),
                SnapshotID = UUID.Parse(reader["SnapshotID"].ToString()),
                OwnerID = UUID.Parse(reader["OwnerID"].ToString()),
                MinBid = Convert.ToInt32(reader["MinBid"]),
                StartedAt = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader["StartedAt"])),
                EndsAt = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader["EndsAt"])),
                Status = (LandAuctionStatus)Convert.ToInt32(reader["Status"]),
                HighestBid = Convert.ToInt32(reader["HighestBid"]),
                HighestBidderID = UUID.Parse(reader["HighestBidderID"].ToString()),
                WinnerID = UUID.Parse(reader["WinnerID"].ToString()),
                WinningAmount = Convert.ToInt32(reader["WinningAmount"])
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    // Backing store for web-bidding on land auctions - see
    // OpenSim.Data.IAuctionData for the design rationale.
    public class PGSQLAuctionData : IAuctionData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        private const string SelectColumns =
                "\"ID\", \"RegionID\", \"RegionName\", \"LocalID\", \"ParcelName\", \"SnapshotID\", \"OwnerID\", \"MinBid\", " +
                "\"StartedAt\", \"EndsAt\", \"Status\", \"HighestBid\", \"HighestBidderID\", \"WinnerID\", \"WinningAmount\"";

        public PGSQLAuctionData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "Auctions");
                m.Update();
            }
        }

        public LandAuction Get(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + SelectColumns + " FROM land_auctions WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return ReadItem(reader);
                }
            }

            return null;
        }

        public LandAuction GetActiveForParcel(UUID regionId, int localId)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + SelectColumns + " FROM land_auctions " +
                    "WHERE \"RegionID\" = :regionId AND \"LocalID\" = :localId AND \"Status\" = :active LIMIT 1", conn))
            {
                cmd.Parameters.AddWithValue(":regionId", regionId.ToString());
                cmd.Parameters.AddWithValue(":localId", localId);
                cmd.Parameters.AddWithValue(":active", (int)LandAuctionStatus.Active);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return ReadItem(reader);
                }
            }

            return null;
        }

        public List<LandAuction> GetActive()
        {
            List<LandAuction> results = new List<LandAuction>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + SelectColumns + " FROM land_auctions WHERE \"Status\" = :active ORDER BY \"EndsAt\" ASC", conn))
            {
                cmd.Parameters.AddWithValue(":active", (int)LandAuctionStatus.Active);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadItem(reader));
                }
            }

            return results;
        }

        public List<LandAuction> GetExpiredActive(DateTime now)
        {
            List<LandAuction> results = new List<LandAuction>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + SelectColumns + " FROM land_auctions WHERE \"Status\" = :active AND \"EndsAt\" <= :now", conn))
            {
                cmd.Parameters.AddWithValue(":active", (int)LandAuctionStatus.Active);
                cmd.Parameters.AddWithValue(":now", (int)Utils.DateTimeToUnixTime(now));
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadItem(reader));
                }
            }

            return results;
        }

        public bool Store(LandAuction auction)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO land_auctions (" + SelectColumns + ") VALUES (" +
                    ":id, :regionId, :regionName, :localId, :parcelName, :snapshotId, :ownerId, :minBid, " +
                    ":startedAt, :endsAt, :status, :highestBid, :highestBidderId, :winnerId, :winningAmount) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET \"RegionID\" = :regionId, \"RegionName\" = :regionName, " +
                    "\"LocalID\" = :localId, \"ParcelName\" = :parcelName, \"SnapshotID\" = :snapshotId, " +
                    "\"OwnerID\" = :ownerId, \"MinBid\" = :minBid, \"StartedAt\" = :startedAt, \"EndsAt\" = :endsAt, " +
                    "\"Status\" = :status, \"HighestBid\" = :highestBid, \"HighestBidderID\" = :highestBidderId, " +
                    "\"WinnerID\" = :winnerId, \"WinningAmount\" = :winningAmount", conn))
            {
                cmd.Parameters.AddWithValue(":id", auction.ID.ToString());
                cmd.Parameters.AddWithValue(":regionId", auction.RegionID.ToString());
                cmd.Parameters.AddWithValue(":regionName", auction.RegionName);
                cmd.Parameters.AddWithValue(":localId", auction.LocalID);
                cmd.Parameters.AddWithValue(":parcelName", auction.ParcelName);
                cmd.Parameters.AddWithValue(":snapshotId", auction.SnapshotID.ToString());
                cmd.Parameters.AddWithValue(":ownerId", auction.OwnerID.ToString());
                cmd.Parameters.AddWithValue(":minBid", auction.MinBid);
                cmd.Parameters.AddWithValue(":startedAt", (int)Utils.DateTimeToUnixTime(auction.StartedAt));
                cmd.Parameters.AddWithValue(":endsAt", (int)Utils.DateTimeToUnixTime(auction.EndsAt));
                cmd.Parameters.AddWithValue(":status", (int)auction.Status);
                cmd.Parameters.AddWithValue(":highestBid", auction.HighestBid);
                cmd.Parameters.AddWithValue(":highestBidderId", auction.HighestBidderID.ToString());
                cmd.Parameters.AddWithValue(":winnerId", auction.WinnerID.ToString());
                cmd.Parameters.AddWithValue(":winningAmount", auction.WinningAmount);
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        // Atomic highest-bid check - see MySqlAuctionData.PlaceBid for the
        // full rationale (same WHERE-clause gating, just Npgsql syntax).
        public bool PlaceBid(UUID auctionId, UUID bidderId, int amount)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();

                int updated;
                using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "UPDATE land_auctions SET \"HighestBid\" = :amount, \"HighestBidderID\" = :bidderId " +
                        "WHERE \"ID\" = :id AND \"Status\" = :active AND \"HighestBid\" < :amount AND \"MinBid\" <= :amount", conn))
                {
                    cmd.Parameters.AddWithValue(":amount", amount);
                    cmd.Parameters.AddWithValue(":bidderId", bidderId.ToString());
                    cmd.Parameters.AddWithValue(":id", auctionId.ToString());
                    cmd.Parameters.AddWithValue(":active", (int)LandAuctionStatus.Active);

                    updated = cmd.ExecuteNonQuery();
                }

                if (updated == 0)
                    return false;

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "INSERT INTO land_auction_bids (\"ID\", \"AuctionID\", \"BidderID\", \"Amount\", \"BidTime\") " +
                        "VALUES (:id, :auctionId, :bidderId, :amount, :bidTime)", conn))
                {
                    cmd.Parameters.AddWithValue(":id", UUID.Random().ToString());
                    cmd.Parameters.AddWithValue(":auctionId", auctionId.ToString());
                    cmd.Parameters.AddWithValue(":bidderId", bidderId.ToString());
                    cmd.Parameters.AddWithValue(":amount", amount);
                    cmd.Parameters.AddWithValue(":bidTime", (int)Util.UnixTimeSinceEpoch());

                    cmd.ExecuteNonQuery();
                }

                return true;
            }
        }

        public List<LandAuctionBid> GetBidHistory(UUID auctionId, int count)
        {
            List<LandAuctionBid> results = new List<LandAuctionBid>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"ID\", \"AuctionID\", \"BidderID\", \"Amount\", \"BidTime\" FROM land_auction_bids " +
                    "WHERE \"AuctionID\" = :auctionId ORDER BY \"BidTime\" DESC LIMIT :count", conn))
            {
                cmd.Parameters.AddWithValue(":auctionId", auctionId.ToString());
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 20 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        results.Add(new LandAuctionBid
                        {
                            ID = UUID.Parse(reader.GetString(0)),
                            AuctionID = UUID.Parse(reader.GetString(1)),
                            BidderID = UUID.Parse(reader.GetString(2)),
                            Amount = reader.GetInt32(3),
                            BidTime = Utils.UnixTimeToDateTime((uint)reader.GetInt32(4))
                        });
                    }
                }
            }

            return results;
        }

        public bool EndAuction(UUID auctionId, LandAuctionStatus finalStatus, UUID winnerId, int winningAmount)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "UPDATE land_auctions SET \"Status\" = :status, \"WinnerID\" = :winnerId, \"WinningAmount\" = :winningAmount " +
                    "WHERE \"ID\" = :id AND \"Status\" = :active", conn))
            {
                cmd.Parameters.AddWithValue(":status", (int)finalStatus);
                cmd.Parameters.AddWithValue(":winnerId", winnerId.ToString());
                cmd.Parameters.AddWithValue(":winningAmount", winningAmount);
                cmd.Parameters.AddWithValue(":id", auctionId.ToString());
                cmd.Parameters.AddWithValue(":active", (int)LandAuctionStatus.Active);
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static LandAuction ReadItem(NpgsqlDataReader reader)
        {
            return new LandAuction
            {
                ID = UUID.Parse(reader.GetString(0)),
                RegionID = UUID.Parse(reader.GetString(1)),
                RegionName = reader.GetString(2),
                LocalID = reader.GetInt32(3),
                ParcelName = reader.GetString(4),
                SnapshotID = UUID.Parse(reader.GetString(5)),
                OwnerID = UUID.Parse(reader.GetString(6)),
                MinBid = reader.GetInt32(7),
                StartedAt = Utils.UnixTimeToDateTime((uint)reader.GetInt32(8)),
                EndsAt = Utils.UnixTimeToDateTime((uint)reader.GetInt32(9)),
                Status = (LandAuctionStatus)reader.GetInt32(10),
                HighestBid = reader.GetInt32(11),
                HighestBidderID = UUID.Parse(reader.GetString(12)),
                WinnerID = UUID.Parse(reader.GetString(13)),
                WinningAmount = reader.GetInt32(14)
            };
        }
    }
}

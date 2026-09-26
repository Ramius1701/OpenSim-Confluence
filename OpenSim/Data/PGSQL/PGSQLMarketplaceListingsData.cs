using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using Npgsql;
using NpgsqlTypes;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    // PostgreSQL backend for the native Marketplace, equivalent to MySqlMarketplaceListingsData.
    public class PGSQLMarketplaceListingsData : IMarketplaceListingsData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLMarketplaceListingsData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "Marketplace");
                m.Update();
            }
        }

        private const string ListingColumns =
            "\"ID\", \"SellerID\", \"Title\", \"Description\", \"Price\", \"CountOnHand\", \"IsListed\", "
            + "\"SnapshotFolderID\", \"ListingFolderID\", \"VersionFolderID\", \"SnapshotFingerprint\", \"Created\", \"Updated\"";

        private static int Now()
        {
            return (int)Utils.DateTimeToUnixTime(DateTime.UtcNow);
        }

        public MarketplaceListing GetListing(int id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "SELECT " + ListingColumns + " FROM marketplace_listings WHERE \"ID\" = :ID", conn))
            {
                cmd.Parameters.AddWithValue(":ID", id);
                conn.Open();

                using (NpgsqlDataReader result = cmd.ExecuteReader())
                {
                    if (result.Read())
                        return ReadListing(result);
                }
            }

            return null;
        }

        public List<MarketplaceListing> GetListingsBySeller(UUID sellerId)
        {
            List<MarketplaceListing> listings = new List<MarketplaceListing>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "SELECT " + ListingColumns + " FROM marketplace_listings WHERE \"SellerID\" = :SellerID ORDER BY \"Created\" DESC", conn))
            {
                cmd.Parameters.AddWithValue(":SellerID", sellerId.ToString());
                conn.Open();

                using (NpgsqlDataReader result = cmd.ExecuteReader())
                {
                    while (result.Read())
                        listings.Add(ReadListing(result));
                }
            }

            return listings;
        }

        public List<MarketplaceListing> GetListedListings(int start, int count)
        {
            List<MarketplaceListing> listings = new List<MarketplaceListing>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "SELECT " + ListingColumns + " FROM marketplace_listings WHERE \"IsListed\" = true "
                + "ORDER BY \"Created\" DESC LIMIT :Count OFFSET :Start", conn))
            {
                cmd.Parameters.AddWithValue(":Start", Math.Max(0, start));
                cmd.Parameters.AddWithValue(":Count", Math.Max(0, count));
                conn.Open();

                using (NpgsqlDataReader result = cmd.ExecuteReader())
                {
                    while (result.Read())
                        listings.Add(ReadListing(result));
                }
            }

            return listings;
        }

        public int InsertListing(MarketplaceListing listing)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "INSERT INTO marketplace_listings "
                + "(\"SellerID\", \"Title\", \"Description\", \"Price\", \"CountOnHand\", \"IsListed\", "
                + "\"SnapshotFolderID\", \"ListingFolderID\", \"VersionFolderID\", \"SnapshotFingerprint\", \"Created\", \"Updated\") VALUES "
                + "(:SellerID, :Title, :Description, :Price, :CountOnHand, :IsListed, "
                + ":SnapshotFolderID, :ListingFolderID, :VersionFolderID, :SnapshotFingerprint, :Created, :Updated) "
                + "RETURNING \"ID\"", conn))
            {
                AddListingParameters(cmd, listing, true);
                conn.Open();
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public bool UpdateListing(MarketplaceListing listing)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "UPDATE marketplace_listings SET \"SellerID\" = :SellerID, \"Title\" = :Title, "
                + "\"Description\" = :Description, \"Price\" = :Price, \"CountOnHand\" = :CountOnHand, "
                + "\"IsListed\" = :IsListed, \"SnapshotFolderID\" = :SnapshotFolderID, "
                + "\"ListingFolderID\" = :ListingFolderID, \"VersionFolderID\" = :VersionFolderID, "
                + "\"SnapshotFingerprint\" = :SnapshotFingerprint, "
                + "\"Updated\" = :Updated WHERE \"ID\" = :ID", conn))
            {
                AddListingParameters(cmd, listing, false);
                cmd.Parameters.AddWithValue(":ID", listing.ID);
                conn.Open();
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool SetListedState(int id, bool isListed)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "UPDATE marketplace_listings SET \"IsListed\" = :IsListed, \"Updated\" = :Updated WHERE \"ID\" = :ID", conn))
            {
                cmd.Parameters.AddWithValue(":IsListed", isListed);
                cmd.Parameters.AddWithValue(":Updated", Now());
                cmd.Parameters.AddWithValue(":ID", id);
                conn.Open();
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public bool TryReserveStock(int id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();

                // Row-locked conditional decrement: only succeeds if CountOnHand is still > 0 at
                // the moment this UPDATE executes, so two buyers racing for the last unit cannot
                // both win.
                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "UPDATE marketplace_listings SET \"CountOnHand\" = \"CountOnHand\" - 1, \"Updated\" = :Updated "
                    + "WHERE \"ID\" = :ID AND \"IsListed\" = true AND \"CountOnHand\" > 0", conn))
                {
                    cmd.Parameters.AddWithValue(":Updated", Now());
                    cmd.Parameters.AddWithValue(":ID", id);
                    if (cmd.ExecuteNonQuery() > 0)
                        return true;
                }

                // Not decremented: out of stock, unlisted, missing, or unlimited (CountOnHand is
                // NULL, which the WHERE clause above can never match). Tell them apart.
                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"IsListed\", \"CountOnHand\" FROM marketplace_listings WHERE \"ID\" = :ID", conn))
                {
                    cmd.Parameters.AddWithValue(":ID", id);

                    using (NpgsqlDataReader result = cmd.ExecuteReader())
                    {
                        if (!result.Read())
                            return false;
                        bool isListed = Convert.ToBoolean(result["IsListed"]);
                        bool unlimited = result["CountOnHand"] is DBNull;
                        return isListed && unlimited;
                    }
                }
            }
        }

        public void ReleaseStock(int id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "UPDATE marketplace_listings SET \"CountOnHand\" = \"CountOnHand\" + 1, \"Updated\" = :Updated "
                + "WHERE \"ID\" = :ID AND \"CountOnHand\" IS NOT NULL", conn))
            {
                cmd.Parameters.AddWithValue(":Updated", Now());
                cmd.Parameters.AddWithValue(":ID", id);
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        public bool TryGetDelivery(string deliveryId, out DeliveryReceipt receipt)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "SELECT \"DeliveryID\", \"SellerID\", \"SnapshotFolderID\", \"RecipientID\", \"SnapshotFingerprint\", "
                + "\"DestinationFolderID\", \"ItemCount\", \"FolderCount\", \"Created\" "
                + "FROM marketplace_deliveries WHERE \"DeliveryID\" = :DeliveryID", conn))
            {
                cmd.Parameters.AddWithValue(":DeliveryID", deliveryId);
                conn.Open();

                using (NpgsqlDataReader result = cmd.ExecuteReader())
                {
                    if (result.Read())
                    {
                        receipt = new DeliveryReceipt
                        {
                            DeliveryId = result["DeliveryID"].ToString(),
                            SellerId = result["SellerID"].ToString(),
                            SnapshotFolderId = result["SnapshotFolderID"].ToString(),
                            RecipientId = result["RecipientID"].ToString(),
                            SnapshotFingerprint = result["SnapshotFingerprint"].ToString(),
                            DestinationFolderId = result["DestinationFolderID"].ToString(),
                            ItemCount = Convert.ToInt32(result["ItemCount"]),
                            FolderCount = Convert.ToInt32(result["FolderCount"]),
                            TimestampUtc = Utils.UnixTimeToDateTime(Convert.ToUInt32(result["Created"])).ToString("O")
                        };
                        return true;
                    }
                }
            }

            receipt = null;
            return false;
        }

        public bool TryInsertDelivery(DeliveryReceipt receipt)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "INSERT INTO marketplace_deliveries "
                + "(\"DeliveryID\", \"SellerID\", \"SnapshotFolderID\", \"RecipientID\", \"SnapshotFingerprint\", "
                + "\"DestinationFolderID\", \"ItemCount\", \"FolderCount\", \"Created\") VALUES "
                + "(:DeliveryID, :SellerID, :SnapshotFolderID, :RecipientID, :SnapshotFingerprint, "
                + ":DestinationFolderID, :ItemCount, :FolderCount, :Created)", conn))
            {
                cmd.Parameters.AddWithValue(":DeliveryID", receipt.DeliveryId);
                cmd.Parameters.AddWithValue(":SellerID", receipt.SellerId);
                cmd.Parameters.AddWithValue(":SnapshotFolderID", receipt.SnapshotFolderId);
                cmd.Parameters.AddWithValue(":RecipientID", receipt.RecipientId);
                cmd.Parameters.AddWithValue(":SnapshotFingerprint", receipt.SnapshotFingerprint);
                cmd.Parameters.AddWithValue(":DestinationFolderID", receipt.DestinationFolderId);
                cmd.Parameters.AddWithValue(":ItemCount", receipt.ItemCount);
                cmd.Parameters.AddWithValue(":FolderCount", receipt.FolderCount);
                cmd.Parameters.AddWithValue(":Created", Now());
                conn.Open();

                try
                {
                    cmd.ExecuteNonQuery();
                    return true;
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    // duplicate DeliveryID primary key
                    return false;
                }
            }
        }

        private static void AddListingParameters(NpgsqlCommand cmd, MarketplaceListing listing, bool forInsert)
        {
            cmd.Parameters.AddWithValue(":SellerID", listing.SellerID.ToString());
            cmd.Parameters.AddWithValue(":Title", listing.Title ?? string.Empty);
            cmd.Parameters.AddWithValue(":Description", listing.Description ?? string.Empty);
            cmd.Parameters.AddWithValue(":Price", listing.Price);

            // NULL needs an explicit type, or Npgsql cannot infer one.
            NpgsqlParameter count = new NpgsqlParameter(":CountOnHand", NpgsqlDbType.Integer);
            count.Value = listing.CountOnHand.HasValue ? (object)listing.CountOnHand.Value : DBNull.Value;
            cmd.Parameters.Add(count);

            cmd.Parameters.AddWithValue(":IsListed", listing.IsListed);
            cmd.Parameters.AddWithValue(":SnapshotFolderID", listing.SnapshotFolderID.ToString());
            cmd.Parameters.AddWithValue(":ListingFolderID", listing.ListingFolderID.ToString());
            cmd.Parameters.AddWithValue(":VersionFolderID", listing.VersionFolderID.ToString());
            cmd.Parameters.AddWithValue(":SnapshotFingerprint", listing.SnapshotFingerprint ?? string.Empty);
            if (forInsert)
                cmd.Parameters.AddWithValue(":Created", (int)Utils.DateTimeToUnixTime(listing.Created));
            cmd.Parameters.AddWithValue(":Updated", (int)Utils.DateTimeToUnixTime(listing.Updated));
        }

        private static MarketplaceListing ReadListing(IDataReader result)
        {
            MarketplaceListing listing = new MarketplaceListing
            {
                ID = Convert.ToInt32(result["ID"]),
                Title = result["Title"].ToString(),
                Description = result["Description"].ToString(),
                Price = Convert.ToInt32(result["Price"]),
                CountOnHand = result["CountOnHand"] is DBNull ? (int?)null : Convert.ToInt32(result["CountOnHand"]),
                IsListed = Convert.ToBoolean(result["IsListed"]),
                SnapshotFingerprint = result["SnapshotFingerprint"].ToString(),
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(result["Created"])),
                Updated = Utils.UnixTimeToDateTime(Convert.ToUInt32(result["Updated"]))
            };
            UUID.TryParse(result["SellerID"].ToString(), out listing.SellerID);
            UUID.TryParse(result["SnapshotFolderID"].ToString(), out listing.SnapshotFolderID);
            UUID.TryParse(result["ListingFolderID"].ToString(), out listing.ListingFolderID);
            UUID.TryParse(result["VersionFolderID"].ToString(), out listing.VersionFolderID);
            return listing;
        }
    }
}

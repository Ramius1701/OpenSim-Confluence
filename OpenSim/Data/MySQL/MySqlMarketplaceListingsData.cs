using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    public class MySqlMarketplaceListingsData : MySqlFramework, IMarketplaceListingsData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlMarketplaceListingsData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "Marketplace");
                m.Update();
                dbcon.Close();
            }
        }

        private const string ListingColumns =
            "`ID`, `SellerID`, `Title`, `Description`, `Price`, `CountOnHand`, `IsListed`, "
            + "`SnapshotFolderID`, `ListingFolderID`, `VersionFolderID`, `SnapshotFingerprint`, `Created`, `Updated`";

        public MarketplaceListing GetListing(int id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "select " + ListingColumns + " from `marketplace_listings` where `ID` = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id);

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        if (result.Read())
                            return ReadListing(result);
                    }
                }
            }

            return null;
        }

        public List<MarketplaceListing> GetListingsBySeller(UUID sellerId)
        {
            List<MarketplaceListing> listings = new List<MarketplaceListing>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "select " + ListingColumns + " from `marketplace_listings` where `SellerID` = ?SellerID order by `Created` desc", dbcon))
                {
                    cmd.Parameters.AddWithValue("?SellerID", sellerId.ToString());

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                            listings.Add(ReadListing(result));
                    }
                }
            }

            return listings;
        }

        public List<MarketplaceListing> GetListedListings(int start, int count)
        {
            List<MarketplaceListing> listings = new List<MarketplaceListing>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "select " + ListingColumns + " from `marketplace_listings` where `IsListed` = 1 "
                    + "order by `Created` desc limit ?Start, ?Count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Start", Math.Max(0, start));
                    cmd.Parameters.AddWithValue("?Count", Math.Max(0, count));

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                            listings.Add(ReadListing(result));
                    }
                }
            }

            return listings;
        }

        public int InsertListing(MarketplaceListing listing)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "insert into `marketplace_listings` "
                    + "(`SellerID`, `Title`, `Description`, `Price`, `CountOnHand`, `IsListed`, "
                    + "`SnapshotFolderID`, `ListingFolderID`, `VersionFolderID`, `SnapshotFingerprint`, `Created`, `Updated`) values "
                    + "(?SellerID, ?Title, ?Description, ?Price, ?CountOnHand, ?IsListed, "
                    + "?SnapshotFolderID, ?ListingFolderID, ?VersionFolderID, ?SnapshotFingerprint, ?Created, ?Updated)", dbcon))
                {
                    AddListingParameters(cmd, listing);
                    cmd.ExecuteNonQuery();
                }

                using (MySqlCommand cmd2 = new MySqlCommand("select LAST_INSERT_ID()", dbcon))
                {
                    return Convert.ToInt32(cmd2.ExecuteScalar());
                }
            }
        }

        public bool UpdateListing(MarketplaceListing listing)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "update `marketplace_listings` set `SellerID` = ?SellerID, `Title` = ?Title, "
                    + "`Description` = ?Description, `Price` = ?Price, `CountOnHand` = ?CountOnHand, "
                    + "`IsListed` = ?IsListed, `SnapshotFolderID` = ?SnapshotFolderID, "
                    + "`ListingFolderID` = ?ListingFolderID, `VersionFolderID` = ?VersionFolderID, "
                    + "`SnapshotFingerprint` = ?SnapshotFingerprint, "
                    + "`Updated` = ?Updated where `ID` = ?ID", dbcon))
                {
                    AddListingParameters(cmd, listing);
                    cmd.Parameters.AddWithValue("?ID", listing.ID);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool SetListedState(int id, bool isListed)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "update `marketplace_listings` set `IsListed` = ?IsListed, `Updated` = ?Updated where `ID` = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?IsListed", isListed);
                    cmd.Parameters.AddWithValue("?Updated", Utils.DateTimeToUnixTime(DateTime.UtcNow));
                    cmd.Parameters.AddWithValue("?ID", id);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool TryReserveStock(int id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                // Row-locked conditional decrement: only succeeds if CountOnHand
                // is still > 0 at the moment this UPDATE actually executes, so
                // two concurrent buyers racing for the last unit can't both win.
                using (MySqlCommand cmd = new MySqlCommand(
                    "update `marketplace_listings` set `CountOnHand` = `CountOnHand` - 1, `Updated` = ?Updated "
                    + "where `ID` = ?ID and `IsListed` = 1 and `CountOnHand` > 0", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Updated", Utils.DateTimeToUnixTime(DateTime.UtcNow));
                    cmd.Parameters.AddWithValue("?ID", id);
                    if (cmd.ExecuteNonQuery() > 0)
                        return true;
                }

                // Not decremented - either out of stock, unlisted, missing, or
                // genuinely unlimited (CountOnHand is null, so the WHERE clause
                // above could never match it). Distinguish those cases.
                using (MySqlCommand cmd = new MySqlCommand(
                    "select `IsListed`, `CountOnHand` from `marketplace_listings` where `ID` = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id);

                    using (IDataReader result = cmd.ExecuteReader())
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
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "update `marketplace_listings` set `CountOnHand` = `CountOnHand` + 1, `Updated` = ?Updated "
                    + "where `ID` = ?ID and `CountOnHand` is not null", dbcon))
                {
                    cmd.Parameters.AddWithValue("?Updated", Utils.DateTimeToUnixTime(DateTime.UtcNow));
                    cmd.Parameters.AddWithValue("?ID", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public bool TryGetDelivery(string deliveryId, out DeliveryReceipt receipt)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "select `DeliveryID`, `SellerID`, `SnapshotFolderID`, `RecipientID`, `SnapshotFingerprint`, "
                    + "`DestinationFolderID`, `ItemCount`, `FolderCount`, `Created` "
                    + "from `marketplace_deliveries` where `DeliveryID` = ?DeliveryID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?DeliveryID", deliveryId);

                    using (IDataReader result = cmd.ExecuteReader())
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
            }

            receipt = null;
            return false;
        }

        public bool TryInsertDelivery(DeliveryReceipt receipt)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "insert into `marketplace_deliveries` "
                    + "(`DeliveryID`, `SellerID`, `SnapshotFolderID`, `RecipientID`, `SnapshotFingerprint`, "
                    + "`DestinationFolderID`, `ItemCount`, `FolderCount`, `Created`) values "
                    + "(?DeliveryID, ?SellerID, ?SnapshotFolderID, ?RecipientID, ?SnapshotFingerprint, "
                    + "?DestinationFolderID, ?ItemCount, ?FolderCount, ?Created)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?DeliveryID", receipt.DeliveryId);
                    cmd.Parameters.AddWithValue("?SellerID", receipt.SellerId);
                    cmd.Parameters.AddWithValue("?SnapshotFolderID", receipt.SnapshotFolderId);
                    cmd.Parameters.AddWithValue("?RecipientID", receipt.RecipientId);
                    cmd.Parameters.AddWithValue("?SnapshotFingerprint", receipt.SnapshotFingerprint);
                    cmd.Parameters.AddWithValue("?DestinationFolderID", receipt.DestinationFolderId);
                    cmd.Parameters.AddWithValue("?ItemCount", receipt.ItemCount);
                    cmd.Parameters.AddWithValue("?FolderCount", receipt.FolderCount);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(DateTime.UtcNow));

                    try
                    {
                        cmd.ExecuteNonQuery();
                        return true;
                    }
                    catch (MySqlException ex) when (ex.Number == 1062) // duplicate key
                    {
                        return false;
                    }
                }
            }
        }

        private static void AddListingParameters(MySqlCommand cmd, MarketplaceListing listing)
        {
            cmd.Parameters.AddWithValue("?SellerID", listing.SellerID.ToString());
            cmd.Parameters.AddWithValue("?Title", listing.Title ?? string.Empty);
            cmd.Parameters.AddWithValue("?Description", listing.Description ?? string.Empty);
            cmd.Parameters.AddWithValue("?Price", listing.Price);
            cmd.Parameters.AddWithValue("?CountOnHand", listing.CountOnHand.HasValue ? (object)listing.CountOnHand.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("?IsListed", listing.IsListed);
            cmd.Parameters.AddWithValue("?SnapshotFolderID", listing.SnapshotFolderID.ToString());
            cmd.Parameters.AddWithValue("?ListingFolderID", listing.ListingFolderID.ToString());
            cmd.Parameters.AddWithValue("?VersionFolderID", listing.VersionFolderID.ToString());
            cmd.Parameters.AddWithValue("?SnapshotFingerprint", listing.SnapshotFingerprint ?? string.Empty);
            cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(listing.Created));
            cmd.Parameters.AddWithValue("?Updated", Utils.DateTimeToUnixTime(listing.Updated));
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

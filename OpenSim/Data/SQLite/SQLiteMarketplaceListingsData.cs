using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // SQLite backend for the native Marketplace, equivalent to MySqlMarketplaceListingsData.
    // One connection, every call serialized under a lock (SQLite is single-writer anyway,
    // which also makes TryReserveStock's conditional UPDATE atomic).
    public class SQLiteMarketplaceListingsData : IMarketplaceListingsData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteMarketplaceListingsData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "Marketplace");
            m.Update();
        }

        private const string ListingColumns =
            "ID, SellerID, Title, Description, Price, CountOnHand, IsListed, "
            + "SnapshotFolderID, ListingFolderID, VersionFolderID, SnapshotFingerprint, Created, Updated";

        public MarketplaceListing GetListing(int id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT " + ListingColumns + " FROM marketplace_listings WHERE ID = :ID", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":ID", id));

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

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT " + ListingColumns + " FROM marketplace_listings WHERE SellerID = :SellerID ORDER BY Created DESC", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":SellerID", sellerId.ToString()));

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

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT " + ListingColumns + " FROM marketplace_listings WHERE IsListed = 1 "
                    + "ORDER BY Created DESC LIMIT :Count OFFSET :Start", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":Start", Math.Max(0, start)));
                    cmd.Parameters.Add(new SQLiteParameter(":Count", Math.Max(0, count)));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "INSERT INTO marketplace_listings "
                    + "(SellerID, Title, Description, Price, CountOnHand, IsListed, "
                    + "SnapshotFolderID, ListingFolderID, VersionFolderID, SnapshotFingerprint, Created, Updated) VALUES "
                    + "(:SellerID, :Title, :Description, :Price, :CountOnHand, :IsListed, "
                    + ":SnapshotFolderID, :ListingFolderID, :VersionFolderID, :SnapshotFingerprint, :Created, :Updated)", m_conn))
                {
                    AddListingParameters(cmd, listing);
                    cmd.ExecuteNonQuery();
                }

                using (SQLiteCommand cmd2 = new SQLiteCommand("SELECT last_insert_rowid()", m_conn))
                {
                    return Convert.ToInt32(cmd2.ExecuteScalar());
                }
            }
        }

        public bool UpdateListing(MarketplaceListing listing)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "UPDATE marketplace_listings SET SellerID = :SellerID, Title = :Title, "
                    + "Description = :Description, Price = :Price, CountOnHand = :CountOnHand, "
                    + "IsListed = :IsListed, SnapshotFolderID = :SnapshotFolderID, "
                    + "ListingFolderID = :ListingFolderID, VersionFolderID = :VersionFolderID, "
                    + "SnapshotFingerprint = :SnapshotFingerprint, "
                    + "Updated = :Updated WHERE ID = :ID", m_conn))
                {
                    AddListingParameters(cmd, listing);
                    cmd.Parameters.Add(new SQLiteParameter(":ID", listing.ID));
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool SetListedState(int id, bool isListed)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "UPDATE marketplace_listings SET IsListed = :IsListed, Updated = :Updated WHERE ID = :ID", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":IsListed", isListed ? 1 : 0));
                    cmd.Parameters.Add(new SQLiteParameter(":Updated", (int)Utils.DateTimeToUnixTime(DateTime.UtcNow)));
                    cmd.Parameters.Add(new SQLiteParameter(":ID", id));
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool TryReserveStock(int id)
        {
            lock (this)
            {
                // Conditional decrement: only succeeds if CountOnHand is still > 0 when this
                // UPDATE runs. SQLite allows one writer at a time, so two buyers racing for
                // the last unit cannot both win.
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "UPDATE marketplace_listings SET CountOnHand = CountOnHand - 1, Updated = :Updated "
                    + "WHERE ID = :ID AND IsListed = 1 AND CountOnHand > 0", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":Updated", (int)Utils.DateTimeToUnixTime(DateTime.UtcNow)));
                    cmd.Parameters.Add(new SQLiteParameter(":ID", id));
                    if (cmd.ExecuteNonQuery() > 0)
                        return true;
                }

                // Not decremented: out of stock, unlisted, missing, or unlimited (CountOnHand
                // is NULL, which the WHERE clause above can never match). Tell them apart.
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT IsListed, CountOnHand FROM marketplace_listings WHERE ID = :ID", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":ID", id));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "UPDATE marketplace_listings SET CountOnHand = CountOnHand + 1, Updated = :Updated "
                    + "WHERE ID = :ID AND CountOnHand IS NOT NULL", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":Updated", (int)Utils.DateTimeToUnixTime(DateTime.UtcNow)));
                    cmd.Parameters.Add(new SQLiteParameter(":ID", id));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public bool TryGetDelivery(string deliveryId, out DeliveryReceipt receipt)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT DeliveryID, SellerID, SnapshotFolderID, RecipientID, SnapshotFingerprint, "
                    + "DestinationFolderID, ItemCount, FolderCount, Created "
                    + "FROM marketplace_deliveries WHERE DeliveryID = :DeliveryID", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":DeliveryID", deliveryId));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "INSERT INTO marketplace_deliveries "
                    + "(DeliveryID, SellerID, SnapshotFolderID, RecipientID, SnapshotFingerprint, "
                    + "DestinationFolderID, ItemCount, FolderCount, Created) VALUES "
                    + "(:DeliveryID, :SellerID, :SnapshotFolderID, :RecipientID, :SnapshotFingerprint, "
                    + ":DestinationFolderID, :ItemCount, :FolderCount, :Created)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":DeliveryID", receipt.DeliveryId));
                    cmd.Parameters.Add(new SQLiteParameter(":SellerID", receipt.SellerId));
                    cmd.Parameters.Add(new SQLiteParameter(":SnapshotFolderID", receipt.SnapshotFolderId));
                    cmd.Parameters.Add(new SQLiteParameter(":RecipientID", receipt.RecipientId));
                    cmd.Parameters.Add(new SQLiteParameter(":SnapshotFingerprint", receipt.SnapshotFingerprint));
                    cmd.Parameters.Add(new SQLiteParameter(":DestinationFolderID", receipt.DestinationFolderId));
                    cmd.Parameters.Add(new SQLiteParameter(":ItemCount", receipt.ItemCount));
                    cmd.Parameters.Add(new SQLiteParameter(":FolderCount", receipt.FolderCount));
                    cmd.Parameters.Add(new SQLiteParameter(":Created", (int)Utils.DateTimeToUnixTime(DateTime.UtcNow)));

                    try
                    {
                        cmd.ExecuteNonQuery();
                        return true;
                    }
                    catch (SQLiteException ex) when (ex.ResultCode == SQLiteErrorCode.Constraint)
                    {
                        // duplicate DeliveryID primary key
                        return false;
                    }
                }
            }
        }

        private static void AddListingParameters(SQLiteCommand cmd, MarketplaceListing listing)
        {
            cmd.Parameters.Add(new SQLiteParameter(":SellerID", listing.SellerID.ToString()));
            cmd.Parameters.Add(new SQLiteParameter(":Title", listing.Title ?? string.Empty));
            cmd.Parameters.Add(new SQLiteParameter(":Description", listing.Description ?? string.Empty));
            cmd.Parameters.Add(new SQLiteParameter(":Price", listing.Price));
            cmd.Parameters.Add(new SQLiteParameter(":CountOnHand", listing.CountOnHand.HasValue ? (object)listing.CountOnHand.Value : DBNull.Value));
            cmd.Parameters.Add(new SQLiteParameter(":IsListed", listing.IsListed ? 1 : 0));
            cmd.Parameters.Add(new SQLiteParameter(":SnapshotFolderID", listing.SnapshotFolderID.ToString()));
            cmd.Parameters.Add(new SQLiteParameter(":ListingFolderID", listing.ListingFolderID.ToString()));
            cmd.Parameters.Add(new SQLiteParameter(":VersionFolderID", listing.VersionFolderID.ToString()));
            cmd.Parameters.Add(new SQLiteParameter(":SnapshotFingerprint", listing.SnapshotFingerprint ?? string.Empty));
            cmd.Parameters.Add(new SQLiteParameter(":Created", (int)Utils.DateTimeToUnixTime(listing.Created)));
            cmd.Parameters.Add(new SQLiteParameter(":Updated", (int)Utils.DateTimeToUnixTime(listing.Updated)));
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

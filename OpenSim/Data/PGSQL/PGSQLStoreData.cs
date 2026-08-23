using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    // Backing store for the WebInterface Store - see OpenSim.Data.IStoreData
    // for the design rationale.
    public class PGSQLStoreData : IStoreData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLStoreData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "Store");
                m.Update();
            }
        }

        private const string CatalogColumns =
                "\"ID\", \"ItemType\", \"Name\", \"Description\", \"PrimAmount\", \"RegionSizeX\", \"RegionSizeY\", " +
                "\"PriceConfluence\", \"PriceGloebits\", \"DurationDays\", \"IsActive\", \"SortOrder\", \"Created\", \"Updated\"";

        public StoreCatalogItem GetCatalogItem(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + CatalogColumns + " FROM store_catalog_items WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return ReadCatalogItem(reader);
                }
            }

            return null;
        }

        public List<StoreCatalogItem> GetActiveCatalogItems()
        {
            List<StoreCatalogItem> results = new List<StoreCatalogItem>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + CatalogColumns + " FROM store_catalog_items WHERE \"IsActive\" = true " +
                    "ORDER BY \"SortOrder\" ASC", conn))
            {
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadCatalogItem(reader));
                }
            }

            return results;
        }

        public List<StoreCatalogItem> GetAllCatalogItems()
        {
            List<StoreCatalogItem> results = new List<StoreCatalogItem>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + CatalogColumns + " FROM store_catalog_items ORDER BY \"SortOrder\" ASC", conn))
            {
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadCatalogItem(reader));
                }
            }

            return results;
        }

        public bool StoreCatalogItem(StoreCatalogItem item)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO store_catalog_items (" + CatalogColumns + ") " +
                    "VALUES (:id, :itemtype, :name, :description, :primamount, :regionsizex, :regionsizey, " +
                    ":priceconfluence, :pricegloebits, :durationdays, :isactive, :sortorder, :created, :updated) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET " +
                    "\"ItemType\" = :itemtype, \"Name\" = :name, \"Description\" = :description, " +
                    "\"PrimAmount\" = :primamount, \"RegionSizeX\" = :regionsizex, \"RegionSizeY\" = :regionsizey, " +
                    "\"PriceConfluence\" = :priceconfluence, \"PriceGloebits\" = :pricegloebits, " +
                    "\"DurationDays\" = :durationdays, \"IsActive\" = :isactive, \"SortOrder\" = :sortorder, " +
                    "\"Updated\" = :updated", conn))
            {
                cmd.Parameters.AddWithValue(":id", item.ID.ToString());
                cmd.Parameters.AddWithValue(":itemtype", item.ItemType);
                cmd.Parameters.AddWithValue(":name", item.Name);
                cmd.Parameters.AddWithValue(":description", item.Description);
                cmd.Parameters.AddWithValue(":primamount", item.PrimAmount);
                cmd.Parameters.AddWithValue(":regionsizex", item.RegionSizeX);
                cmd.Parameters.AddWithValue(":regionsizey", item.RegionSizeY);
                cmd.Parameters.AddWithValue(":priceconfluence", item.PriceConfluence);
                cmd.Parameters.AddWithValue(":pricegloebits", item.PriceGloebits);
                cmd.Parameters.AddWithValue(":durationdays", item.DurationDays);
                cmd.Parameters.AddWithValue(":isactive", item.IsActive);
                cmd.Parameters.AddWithValue(":sortorder", item.SortOrder);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(item.Created));
                cmd.Parameters.AddWithValue(":updated", (int)Utils.DateTimeToUnixTime(item.Updated));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static StoreCatalogItem ReadCatalogItem(NpgsqlDataReader reader)
        {
            return new StoreCatalogItem
            {
                ID = UUID.Parse(reader.GetString(0)),
                ItemType = reader.GetString(1),
                Name = reader.GetString(2),
                Description = reader.GetString(3),
                PrimAmount = reader.GetInt32(4),
                RegionSizeX = reader.GetInt32(5),
                RegionSizeY = reader.GetInt32(6),
                PriceConfluence = reader.GetInt32(7),
                PriceGloebits = reader.GetInt32(8),
                DurationDays = reader.GetInt32(9),
                IsActive = reader.GetBoolean(10),
                SortOrder = reader.GetInt32(11),
                Created = Utils.UnixTimeToDateTime((uint)reader.GetInt32(12)),
                Updated = Utils.UnixTimeToDateTime((uint)reader.GetInt32(13))
            };
        }

        private const string OrderColumns =
                "\"ID\", \"CatalogItemID\", \"OrderType\", \"ResidentAvatarID\", \"ResidentName\", \"CurrencyUsed\", " +
                "\"AmountCharged\", \"PaymentTransactionID\", \"Status\", \"TargetRegionID\", \"RequestedRegionName\", " +
                "\"AllocatedLocationX\", \"AllocatedLocationY\", \"AllocatedPort\", \"SimulatorFolderName\", " +
                "\"StartedAt\", \"ExpiresAt\", \"Notes\", \"Created\", \"Updated\"";

        public StoreOrder GetOrder(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + OrderColumns + " FROM store_orders WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return ReadOrder(reader);
                }
            }

            return null;
        }

        public List<StoreOrder> GetOrdersByResident(UUID avatarId)
        {
            List<StoreOrder> results = new List<StoreOrder>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + OrderColumns + " FROM store_orders WHERE \"ResidentAvatarID\" = :avatarid " +
                    "ORDER BY \"Created\" DESC", conn))
            {
                cmd.Parameters.AddWithValue(":avatarid", avatarId.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadOrder(reader));
                }
            }

            return results;
        }

        public List<StoreOrder> GetAllOrders()
        {
            List<StoreOrder> results = new List<StoreOrder>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + OrderColumns + " FROM store_orders ORDER BY \"Created\" DESC", conn))
            {
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadOrder(reader));
                }
            }

            return results;
        }

        public bool StoreOrder(StoreOrder order)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO store_orders (" + OrderColumns + ") " +
                    "VALUES (:id, :catalogitemid, :ordertype, :residentavatarid, :residentname, :currencyused, " +
                    ":amountcharged, :paymenttransactionid, :status, :targetregionid, :requestedregionname, " +
                    ":allocatedlocationx, :allocatedlocationy, :allocatedport, :simulatorfoldername, :startedat, " +
                    ":expiresat, :notes, :created, :updated) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET " +
                    "\"CatalogItemID\" = :catalogitemid, \"OrderType\" = :ordertype, " +
                    "\"ResidentAvatarID\" = :residentavatarid, \"ResidentName\" = :residentname, " +
                    "\"CurrencyUsed\" = :currencyused, \"AmountCharged\" = :amountcharged, " +
                    "\"PaymentTransactionID\" = :paymenttransactionid, \"Status\" = :status, " +
                    "\"TargetRegionID\" = :targetregionid, \"RequestedRegionName\" = :requestedregionname, " +
                    "\"AllocatedLocationX\" = :allocatedlocationx, \"AllocatedLocationY\" = :allocatedlocationy, " +
                    "\"AllocatedPort\" = :allocatedport, \"SimulatorFolderName\" = :simulatorfoldername, " +
                    "\"StartedAt\" = :startedat, \"ExpiresAt\" = :expiresat, \"Notes\" = :notes, " +
                    "\"Updated\" = :updated", conn))
            {
                cmd.Parameters.AddWithValue(":id", order.ID.ToString());
                cmd.Parameters.AddWithValue(":catalogitemid", order.CatalogItemID.ToString());
                cmd.Parameters.AddWithValue(":ordertype", order.OrderType);
                cmd.Parameters.AddWithValue(":residentavatarid", order.ResidentAvatarID.ToString());
                cmd.Parameters.AddWithValue(":residentname", order.ResidentName);
                cmd.Parameters.AddWithValue(":currencyused", order.CurrencyUsed);
                cmd.Parameters.AddWithValue(":amountcharged", order.AmountCharged);
                cmd.Parameters.AddWithValue(":paymenttransactionid", order.PaymentTransactionID);
                cmd.Parameters.AddWithValue(":status", order.Status);
                cmd.Parameters.AddWithValue(":targetregionid",
                        order.TargetRegionID.HasValue ? (object)order.TargetRegionID.Value.ToString() : DBNull.Value);
                cmd.Parameters.AddWithValue(":requestedregionname", (object)order.RequestedRegionName ?? DBNull.Value);
                cmd.Parameters.AddWithValue(":allocatedlocationx",
                        order.AllocatedLocationX.HasValue ? (object)order.AllocatedLocationX.Value : DBNull.Value);
                cmd.Parameters.AddWithValue(":allocatedlocationy",
                        order.AllocatedLocationY.HasValue ? (object)order.AllocatedLocationY.Value : DBNull.Value);
                cmd.Parameters.AddWithValue(":allocatedport",
                        order.AllocatedPort.HasValue ? (object)order.AllocatedPort.Value : DBNull.Value);
                cmd.Parameters.AddWithValue(":simulatorfoldername", (object)order.SimulatorFolderName ?? DBNull.Value);
                cmd.Parameters.AddWithValue(":startedat",
                        order.StartedAt.HasValue ? (object)(int)Utils.DateTimeToUnixTime(order.StartedAt.Value) : DBNull.Value);
                cmd.Parameters.AddWithValue(":expiresat",
                        order.ExpiresAt.HasValue ? (object)(int)Utils.DateTimeToUnixTime(order.ExpiresAt.Value) : DBNull.Value);
                cmd.Parameters.AddWithValue(":notes", (object)order.Notes ?? DBNull.Value);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(order.Created));
                cmd.Parameters.AddWithValue(":updated", (int)Utils.DateTimeToUnixTime(order.Updated));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static StoreOrder ReadOrder(NpgsqlDataReader reader)
        {
            return new StoreOrder
            {
                ID = UUID.Parse(reader.GetString(0)),
                CatalogItemID = UUID.Parse(reader.GetString(1)),
                OrderType = reader.GetString(2),
                ResidentAvatarID = UUID.Parse(reader.GetString(3)),
                ResidentName = reader.GetString(4),
                CurrencyUsed = reader.GetString(5),
                AmountCharged = reader.GetInt32(6),
                PaymentTransactionID = reader.GetString(7),
                Status = reader.GetString(8),
                TargetRegionID = reader.IsDBNull(9) ? (UUID?)null : UUID.Parse(reader.GetString(9)),
                RequestedRegionName = reader.IsDBNull(10) ? null : reader.GetString(10),
                AllocatedLocationX = reader.IsDBNull(11) ? (int?)null : reader.GetInt32(11),
                AllocatedLocationY = reader.IsDBNull(12) ? (int?)null : reader.GetInt32(12),
                AllocatedPort = reader.IsDBNull(13) ? (int?)null : reader.GetInt32(13),
                SimulatorFolderName = reader.IsDBNull(14) ? null : reader.GetString(14),
                StartedAt = reader.IsDBNull(15) ? (DateTime?)null : Utils.UnixTimeToDateTime((uint)reader.GetInt32(15)),
                ExpiresAt = reader.IsDBNull(16) ? (DateTime?)null : Utils.UnixTimeToDateTime((uint)reader.GetInt32(16)),
                Notes = reader.IsDBNull(17) ? null : reader.GetString(17),
                Created = Utils.UnixTimeToDateTime((uint)reader.GetInt32(18)),
                Updated = Utils.UnixTimeToDateTime((uint)reader.GetInt32(19))
            };
        }

        private const string GloebitAuthColumns =
                "\"AvatarPrincipalID\", \"GloebitID\", \"AccessToken\", \"Authorized\", \"Created\", \"Updated\"";

        public StoreGloebitAuth GetGloebitAuth(UUID avatarId)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + GloebitAuthColumns + " FROM store_gloebit_auth WHERE \"AvatarPrincipalID\" = :avatarid", conn))
            {
                cmd.Parameters.AddWithValue(":avatarid", avatarId.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return ReadGloebitAuth(reader);
                }
            }

            return null;
        }

        public bool StoreGloebitAuth(StoreGloebitAuth auth)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO store_gloebit_auth (" + GloebitAuthColumns + ") " +
                    "VALUES (:avatarid, :gloebitid, :accesstoken, :authorized, :created, :updated) " +
                    "ON CONFLICT (\"AvatarPrincipalID\") DO UPDATE SET " +
                    "\"GloebitID\" = :gloebitid, \"AccessToken\" = :accesstoken, \"Authorized\" = :authorized, " +
                    "\"Updated\" = :updated", conn))
            {
                cmd.Parameters.AddWithValue(":avatarid", auth.AvatarPrincipalID.ToString());
                cmd.Parameters.AddWithValue(":gloebitid", (object)auth.GloebitID ?? DBNull.Value);
                cmd.Parameters.AddWithValue(":accesstoken", (object)auth.AccessToken ?? DBNull.Value);
                cmd.Parameters.AddWithValue(":authorized", auth.Authorized);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(auth.Created));
                cmd.Parameters.AddWithValue(":updated", (int)Utils.DateTimeToUnixTime(auth.Updated));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static StoreGloebitAuth ReadGloebitAuth(NpgsqlDataReader reader)
        {
            return new StoreGloebitAuth
            {
                AvatarPrincipalID = UUID.Parse(reader.GetString(0)),
                GloebitID = reader.IsDBNull(1) ? null : reader.GetString(1),
                AccessToken = reader.IsDBNull(2) ? null : reader.GetString(2),
                Authorized = reader.GetBoolean(3),
                Created = Utils.UnixTimeToDateTime((uint)reader.GetInt32(4)),
                Updated = Utils.UnixTimeToDateTime((uint)reader.GetInt32(5))
            };
        }

        private const string GloebitTxnColumns =
                "\"ID\", \"StoreOrderID\", \"AvatarPrincipalID\", \"Amount\", \"Stage\", \"Enacted\", \"Consumed\", " +
                "\"Cancelled\", \"ResponseReason\", \"Created\", \"Updated\"";

        public StoreGloebitTransaction GetGloebitTransaction(UUID id)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT " + GloebitTxnColumns + " FROM store_gloebit_transactions WHERE \"ID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", id.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return ReadGloebitTransaction(reader);
                }
            }

            return null;
        }

        public bool StoreGloebitTransaction(StoreGloebitTransaction txn)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO store_gloebit_transactions (" + GloebitTxnColumns + ") " +
                    "VALUES (:id, :storeorderid, :avatarid, :amount, :stage, :enacted, :consumed, :cancelled, " +
                    ":responsereason, :created, :updated) " +
                    "ON CONFLICT (\"ID\") DO UPDATE SET " +
                    "\"Stage\" = :stage, \"Enacted\" = :enacted, \"Consumed\" = :consumed, \"Cancelled\" = :cancelled, " +
                    "\"ResponseReason\" = :responsereason, \"Updated\" = :updated", conn))
            {
                cmd.Parameters.AddWithValue(":id", txn.ID.ToString());
                cmd.Parameters.AddWithValue(":storeorderid", txn.StoreOrderID.ToString());
                cmd.Parameters.AddWithValue(":avatarid", txn.AvatarPrincipalID.ToString());
                cmd.Parameters.AddWithValue(":amount", txn.Amount);
                cmd.Parameters.AddWithValue(":stage", txn.Stage);
                cmd.Parameters.AddWithValue(":enacted", txn.Enacted);
                cmd.Parameters.AddWithValue(":consumed", txn.Consumed);
                cmd.Parameters.AddWithValue(":cancelled", txn.Cancelled);
                cmd.Parameters.AddWithValue(":responsereason", (object)txn.ResponseReason ?? DBNull.Value);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(txn.Created));
                cmd.Parameters.AddWithValue(":updated", (int)Utils.DateTimeToUnixTime(txn.Updated));
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static StoreGloebitTransaction ReadGloebitTransaction(NpgsqlDataReader reader)
        {
            return new StoreGloebitTransaction
            {
                ID = UUID.Parse(reader.GetString(0)),
                StoreOrderID = UUID.Parse(reader.GetString(1)),
                AvatarPrincipalID = UUID.Parse(reader.GetString(2)),
                Amount = reader.GetInt32(3),
                Stage = reader.GetString(4),
                Enacted = reader.GetBoolean(5),
                Consumed = reader.GetBoolean(6),
                Cancelled = reader.GetBoolean(7),
                ResponseReason = reader.IsDBNull(8) ? null : reader.GetString(8),
                Created = Utils.UnixTimeToDateTime((uint)reader.GetInt32(9)),
                Updated = Utils.UnixTimeToDateTime((uint)reader.GetInt32(10))
            };
        }
    }
}

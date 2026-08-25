using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    // Backing store for the WebInterface Store - see OpenSim.Data.IStoreData
    // for the design rationale.
    public class MySqlStoreData : MySqlFramework, IStoreData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlStoreData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "Store");
                m.Update();
                dbcon.Close();
            }
        }

        private const string CatalogColumns =
                "ID, ItemType, Name, Description, PrimAmount, RegionSizeX, RegionSizeY, " +
                "PriceConfluence, PriceGloebits, DurationDays, IsActive, SortOrder, Created, Updated";

        public StoreCatalogItem GetCatalogItem(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + CatalogColumns + " FROM store_catalog_items WHERE ID = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadCatalogItem(reader);
                    }
                }
            }

            return null;
        }

        public List<StoreCatalogItem> GetActiveCatalogItems()
        {
            List<StoreCatalogItem> results = new List<StoreCatalogItem>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + CatalogColumns + " FROM store_catalog_items WHERE IsActive = 1 " +
                        "ORDER BY SortOrder ASC", dbcon))
                {
                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadCatalogItem(reader));
                    }
                }
            }

            return results;
        }

        public List<StoreCatalogItem> GetAllCatalogItems()
        {
            List<StoreCatalogItem> results = new List<StoreCatalogItem>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + CatalogColumns + " FROM store_catalog_items " +
                        "ORDER BY SortOrder ASC", dbcon))
                {
                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadCatalogItem(reader));
                    }
                }
            }

            return results;
        }

        public bool StoreCatalogItem(StoreCatalogItem item)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO store_catalog_items (" + CatalogColumns + ") " +
                        "VALUES (?ID, ?ItemType, ?Name, ?Description, ?PrimAmount, ?RegionSizeX, ?RegionSizeY, " +
                        "?PriceConfluence, ?PriceGloebits, ?DurationDays, ?IsActive, ?SortOrder, ?Created, ?Updated)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", item.ID.ToString());
                    cmd.Parameters.AddWithValue("?ItemType", item.ItemType);
                    cmd.Parameters.AddWithValue("?Name", item.Name);
                    cmd.Parameters.AddWithValue("?Description", item.Description);
                    cmd.Parameters.AddWithValue("?PrimAmount", item.PrimAmount);
                    cmd.Parameters.AddWithValue("?RegionSizeX", item.RegionSizeX);
                    cmd.Parameters.AddWithValue("?RegionSizeY", item.RegionSizeY);
                    cmd.Parameters.AddWithValue("?PriceConfluence", item.PriceConfluence);
                    cmd.Parameters.AddWithValue("?PriceGloebits", item.PriceGloebits);
                    cmd.Parameters.AddWithValue("?DurationDays", item.DurationDays);
                    cmd.Parameters.AddWithValue("?IsActive", item.IsActive);
                    cmd.Parameters.AddWithValue("?SortOrder", item.SortOrder);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(item.Created));
                    cmd.Parameters.AddWithValue("?Updated", Utils.DateTimeToUnixTime(item.Updated));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static StoreCatalogItem ReadCatalogItem(IDataReader reader)
        {
            return new StoreCatalogItem
            {
                ID = UUID.Parse(reader.GetString(0)),
                ItemType = reader.GetString(1),
                Name = reader.GetString(2),
                Description = reader.GetString(3),
                PrimAmount = Convert.ToInt32(reader.GetValue(4)),
                RegionSizeX = Convert.ToInt32(reader.GetValue(5)),
                RegionSizeY = Convert.ToInt32(reader.GetValue(6)),
                PriceConfluence = Convert.ToInt32(reader.GetValue(7)),
                PriceGloebits = Convert.ToInt32(reader.GetValue(8)),
                DurationDays = Convert.ToInt32(reader.GetValue(9)),
                IsActive = Convert.ToBoolean(reader.GetValue(10)),
                SortOrder = Convert.ToInt32(reader.GetValue(11)),
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(12))),
                Updated = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(13)))
            };
        }

        private const string OrderColumns =
                "ID, CatalogItemID, OrderType, ResidentAvatarID, ResidentName, CurrencyUsed, AmountCharged, " +
                "PaymentTransactionID, Status, TargetRegionID, RequestedRegionName, AllocatedLocationX, " +
                "AllocatedLocationY, AllocatedPort, SimulatorFolderName, RequestedEstateID, RequestedEstateName, " +
                "RequestedLocationX, RequestedLocationY, StartedAt, ExpiresAt, Notes, Created, Updated";

        public StoreOrder GetOrder(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + OrderColumns + " FROM store_orders WHERE ID = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadOrder(reader);
                    }
                }
            }

            return null;
        }

        public List<StoreOrder> GetOrdersByResident(UUID avatarId)
        {
            List<StoreOrder> results = new List<StoreOrder>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + OrderColumns + " FROM store_orders WHERE ResidentAvatarID = ?ResidentAvatarID " +
                        "ORDER BY Created DESC", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ResidentAvatarID", avatarId.ToString());

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadOrder(reader));
                    }
                }
            }

            return results;
        }

        public List<StoreOrder> GetAllOrders()
        {
            List<StoreOrder> results = new List<StoreOrder>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + OrderColumns + " FROM store_orders ORDER BY Created DESC", dbcon))
                {
                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadOrder(reader));
                    }
                }
            }

            return results;
        }

        public bool StoreOrder(StoreOrder order)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO store_orders (" + OrderColumns + ") " +
                        "VALUES (?ID, ?CatalogItemID, ?OrderType, ?ResidentAvatarID, ?ResidentName, ?CurrencyUsed, " +
                        "?AmountCharged, ?PaymentTransactionID, ?Status, ?TargetRegionID, ?RequestedRegionName, " +
                        "?AllocatedLocationX, ?AllocatedLocationY, ?AllocatedPort, ?SimulatorFolderName, " +
                        "?RequestedEstateID, ?RequestedEstateName, ?RequestedLocationX, ?RequestedLocationY, ?StartedAt, " +
                        "?ExpiresAt, ?Notes, ?Created, ?Updated)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", order.ID.ToString());
                    cmd.Parameters.AddWithValue("?CatalogItemID", order.CatalogItemID.ToString());
                    cmd.Parameters.AddWithValue("?OrderType", order.OrderType);
                    cmd.Parameters.AddWithValue("?ResidentAvatarID", order.ResidentAvatarID.ToString());
                    cmd.Parameters.AddWithValue("?ResidentName", order.ResidentName);
                    cmd.Parameters.AddWithValue("?CurrencyUsed", order.CurrencyUsed);
                    cmd.Parameters.AddWithValue("?AmountCharged", order.AmountCharged);
                    cmd.Parameters.AddWithValue("?PaymentTransactionID", order.PaymentTransactionID);
                    cmd.Parameters.AddWithValue("?Status", order.Status);
                    cmd.Parameters.AddWithValue("?TargetRegionID",
                            order.TargetRegionID.HasValue ? (object)order.TargetRegionID.Value.ToString() : DBNull.Value);
                    cmd.Parameters.AddWithValue("?RequestedRegionName", (object)order.RequestedRegionName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("?AllocatedLocationX",
                            order.AllocatedLocationX.HasValue ? (object)order.AllocatedLocationX.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("?AllocatedLocationY",
                            order.AllocatedLocationY.HasValue ? (object)order.AllocatedLocationY.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("?AllocatedPort",
                            order.AllocatedPort.HasValue ? (object)order.AllocatedPort.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("?SimulatorFolderName", (object)order.SimulatorFolderName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("?RequestedEstateID",
                            order.RequestedEstateID.HasValue ? (object)order.RequestedEstateID.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("?RequestedEstateName", (object)order.RequestedEstateName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("?RequestedLocationX",
                            order.RequestedLocationX.HasValue ? (object)order.RequestedLocationX.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("?RequestedLocationY",
                            order.RequestedLocationY.HasValue ? (object)order.RequestedLocationY.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("?StartedAt",
                            order.StartedAt.HasValue ? (object)Utils.DateTimeToUnixTime(order.StartedAt.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("?ExpiresAt",
                            order.ExpiresAt.HasValue ? (object)Utils.DateTimeToUnixTime(order.ExpiresAt.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("?Notes", (object)order.Notes ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(order.Created));
                    cmd.Parameters.AddWithValue("?Updated", Utils.DateTimeToUnixTime(order.Updated));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static StoreOrder ReadOrder(IDataReader reader)
        {
            return new StoreOrder
            {
                ID = UUID.Parse(reader.GetString(0)),
                CatalogItemID = UUID.Parse(reader.GetString(1)),
                OrderType = reader.GetString(2),
                ResidentAvatarID = UUID.Parse(reader.GetString(3)),
                ResidentName = reader.GetString(4),
                CurrencyUsed = reader.GetString(5),
                AmountCharged = Convert.ToInt32(reader.GetValue(6)),
                PaymentTransactionID = reader.GetString(7),
                Status = reader.GetString(8),
                TargetRegionID = reader.IsDBNull(9) ? (UUID?)null : UUID.Parse(reader.GetString(9)),
                RequestedRegionName = reader.IsDBNull(10) ? null : reader.GetString(10),
                AllocatedLocationX = reader.IsDBNull(11) ? (int?)null : Convert.ToInt32(reader.GetValue(11)),
                AllocatedLocationY = reader.IsDBNull(12) ? (int?)null : Convert.ToInt32(reader.GetValue(12)),
                AllocatedPort = reader.IsDBNull(13) ? (int?)null : Convert.ToInt32(reader.GetValue(13)),
                SimulatorFolderName = reader.IsDBNull(14) ? null : reader.GetString(14),
                RequestedEstateID = reader.IsDBNull(15) ? (int?)null : Convert.ToInt32(reader.GetValue(15)),
                RequestedEstateName = reader.IsDBNull(16) ? null : reader.GetString(16),
                RequestedLocationX = reader.IsDBNull(17) ? (int?)null : Convert.ToInt32(reader.GetValue(17)),
                RequestedLocationY = reader.IsDBNull(18) ? (int?)null : Convert.ToInt32(reader.GetValue(18)),
                StartedAt = reader.IsDBNull(19) ? (DateTime?)null : Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(19))),
                ExpiresAt = reader.IsDBNull(20) ? (DateTime?)null : Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(20))),
                Notes = reader.IsDBNull(21) ? null : reader.GetString(21),
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(22))),
                Updated = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(23)))
            };
        }

        private const string GloebitAuthColumns =
                "AvatarPrincipalID, GloebitID, AccessToken, Authorized, Created, Updated";

        public StoreGloebitAuth GetGloebitAuth(UUID avatarId)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + GloebitAuthColumns + " FROM store_gloebit_auth WHERE AvatarPrincipalID = ?AvatarPrincipalID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?AvatarPrincipalID", avatarId.ToString());

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadGloebitAuth(reader);
                    }
                }
            }

            return null;
        }

        public bool StoreGloebitAuth(StoreGloebitAuth auth)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO store_gloebit_auth (" + GloebitAuthColumns + ") " +
                        "VALUES (?AvatarPrincipalID, ?GloebitID, ?AccessToken, ?Authorized, ?Created, ?Updated)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?AvatarPrincipalID", auth.AvatarPrincipalID.ToString());
                    cmd.Parameters.AddWithValue("?GloebitID", (object)auth.GloebitID ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("?AccessToken", (object)auth.AccessToken ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("?Authorized", auth.Authorized);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(auth.Created));
                    cmd.Parameters.AddWithValue("?Updated", Utils.DateTimeToUnixTime(auth.Updated));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static StoreGloebitAuth ReadGloebitAuth(IDataReader reader)
        {
            return new StoreGloebitAuth
            {
                AvatarPrincipalID = UUID.Parse(reader.GetString(0)),
                GloebitID = reader.IsDBNull(1) ? null : reader.GetString(1),
                AccessToken = reader.IsDBNull(2) ? null : reader.GetString(2),
                Authorized = Convert.ToBoolean(reader.GetValue(3)),
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(4))),
                Updated = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(5)))
            };
        }

        private const string GloebitTxnColumns =
                "ID, StoreOrderID, AvatarPrincipalID, Amount, Stage, Enacted, Consumed, Cancelled, " +
                "ResponseReason, Created, Updated";

        public StoreGloebitTransaction GetGloebitTransaction(UUID id)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT " + GloebitTxnColumns + " FROM store_gloebit_transactions WHERE ID = ?ID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", id.ToString());

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadGloebitTransaction(reader);
                    }
                }
            }

            return null;
        }

        public bool StoreGloebitTransaction(StoreGloebitTransaction txn)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO store_gloebit_transactions (" + GloebitTxnColumns + ") " +
                        "VALUES (?ID, ?StoreOrderID, ?AvatarPrincipalID, ?Amount, ?Stage, ?Enacted, ?Consumed, " +
                        "?Cancelled, ?ResponseReason, ?Created, ?Updated)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ID", txn.ID.ToString());
                    cmd.Parameters.AddWithValue("?StoreOrderID", txn.StoreOrderID.ToString());
                    cmd.Parameters.AddWithValue("?AvatarPrincipalID", txn.AvatarPrincipalID.ToString());
                    cmd.Parameters.AddWithValue("?Amount", txn.Amount);
                    cmd.Parameters.AddWithValue("?Stage", txn.Stage);
                    cmd.Parameters.AddWithValue("?Enacted", txn.Enacted);
                    cmd.Parameters.AddWithValue("?Consumed", txn.Consumed);
                    cmd.Parameters.AddWithValue("?Cancelled", txn.Cancelled);
                    cmd.Parameters.AddWithValue("?ResponseReason", (object)txn.ResponseReason ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(txn.Created));
                    cmd.Parameters.AddWithValue("?Updated", Utils.DateTimeToUnixTime(txn.Updated));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static StoreGloebitTransaction ReadGloebitTransaction(IDataReader reader)
        {
            return new StoreGloebitTransaction
            {
                ID = UUID.Parse(reader.GetString(0)),
                StoreOrderID = UUID.Parse(reader.GetString(1)),
                AvatarPrincipalID = UUID.Parse(reader.GetString(2)),
                Amount = Convert.ToInt32(reader.GetValue(3)),
                Stage = reader.GetString(4),
                Enacted = Convert.ToBoolean(reader.GetValue(5)),
                Consumed = Convert.ToBoolean(reader.GetValue(6)),
                Cancelled = Convert.ToBoolean(reader.GetValue(7)),
                ResponseReason = reader.IsDBNull(8) ? null : reader.GetString(8),
                Created = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(9))),
                Updated = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(10)))
            };
        }
    }
}

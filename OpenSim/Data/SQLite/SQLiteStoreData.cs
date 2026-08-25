using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // Backing store for the WebInterface Store - see OpenSim.Data.IStoreData
    // for the design rationale.
    public class SQLiteStoreData : IStoreData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteStoreData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "Store");
            m.Update();
        }

        private const string CatalogColumns =
                "ID, ItemType, Name, Description, PrimAmount, RegionSizeX, RegionSizeY, " +
                "PriceConfluence, PriceGloebits, DurationDays, IsActive, SortOrder, Created, Updated";

        public StoreCatalogItem GetCatalogItem(UUID id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + CatalogColumns + " FROM store_catalog_items WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));

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

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + CatalogColumns + " FROM store_catalog_items WHERE IsActive = 1 " +
                        "ORDER BY SortOrder ASC", m_conn))
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

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + CatalogColumns + " FROM store_catalog_items ORDER BY SortOrder ASC", m_conn))
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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO store_catalog_items (" + CatalogColumns + ") " +
                        "VALUES (:id, :itemtype, :name, :description, :primamount, :regionsizex, :regionsizey, " +
                        ":priceconfluence, :pricegloebits, :durationdays, :isactive, :sortorder, :created, :updated)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", item.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":itemtype", item.ItemType));
                    cmd.Parameters.Add(new SQLiteParameter(":name", item.Name));
                    cmd.Parameters.Add(new SQLiteParameter(":description", item.Description));
                    cmd.Parameters.Add(new SQLiteParameter(":primamount", item.PrimAmount));
                    cmd.Parameters.Add(new SQLiteParameter(":regionsizex", item.RegionSizeX));
                    cmd.Parameters.Add(new SQLiteParameter(":regionsizey", item.RegionSizeY));
                    cmd.Parameters.Add(new SQLiteParameter(":priceconfluence", item.PriceConfluence));
                    cmd.Parameters.Add(new SQLiteParameter(":pricegloebits", item.PriceGloebits));
                    cmd.Parameters.Add(new SQLiteParameter(":durationdays", item.DurationDays));
                    cmd.Parameters.Add(new SQLiteParameter(":isactive", item.IsActive));
                    cmd.Parameters.Add(new SQLiteParameter(":sortorder", item.SortOrder));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(item.Created)));
                    cmd.Parameters.Add(new SQLiteParameter(":updated", Utils.DateTimeToUnixTime(item.Updated)));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + OrderColumns + " FROM store_orders WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));

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

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + OrderColumns + " FROM store_orders WHERE ResidentAvatarID = :avatarid " +
                        "ORDER BY Created DESC", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":avatarid", avatarId.ToString()));

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

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + OrderColumns + " FROM store_orders ORDER BY Created DESC", m_conn))
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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO store_orders (" + OrderColumns + ") " +
                        "VALUES (:id, :catalogitemid, :ordertype, :residentavatarid, :residentname, :currencyused, " +
                        ":amountcharged, :paymenttransactionid, :status, :targetregionid, :requestedregionname, " +
                        ":allocatedlocationx, :allocatedlocationy, :allocatedport, :simulatorfoldername, " +
                        ":requestedestateid, :requestedestatename, :requestedlocationx, :requestedlocationy, :startedat, " +
                        ":expiresat, :notes, :created, :updated)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", order.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":catalogitemid", order.CatalogItemID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":ordertype", order.OrderType));
                    cmd.Parameters.Add(new SQLiteParameter(":residentavatarid", order.ResidentAvatarID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":residentname", order.ResidentName));
                    cmd.Parameters.Add(new SQLiteParameter(":currencyused", order.CurrencyUsed));
                    cmd.Parameters.Add(new SQLiteParameter(":amountcharged", order.AmountCharged));
                    cmd.Parameters.Add(new SQLiteParameter(":paymenttransactionid", order.PaymentTransactionID));
                    cmd.Parameters.Add(new SQLiteParameter(":status", order.Status));
                    cmd.Parameters.Add(new SQLiteParameter(":targetregionid",
                            order.TargetRegionID.HasValue ? (object)order.TargetRegionID.Value.ToString() : DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":requestedregionname", (object)order.RequestedRegionName ?? DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":allocatedlocationx",
                            order.AllocatedLocationX.HasValue ? (object)order.AllocatedLocationX.Value : DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":allocatedlocationy",
                            order.AllocatedLocationY.HasValue ? (object)order.AllocatedLocationY.Value : DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":allocatedport",
                            order.AllocatedPort.HasValue ? (object)order.AllocatedPort.Value : DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":simulatorfoldername", (object)order.SimulatorFolderName ?? DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":requestedestateid",
                            order.RequestedEstateID.HasValue ? (object)order.RequestedEstateID.Value : DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":requestedestatename", (object)order.RequestedEstateName ?? DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":requestedlocationx",
                            order.RequestedLocationX.HasValue ? (object)order.RequestedLocationX.Value : DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":requestedlocationy",
                            order.RequestedLocationY.HasValue ? (object)order.RequestedLocationY.Value : DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":startedat",
                            order.StartedAt.HasValue ? (object)Utils.DateTimeToUnixTime(order.StartedAt.Value) : DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":expiresat",
                            order.ExpiresAt.HasValue ? (object)Utils.DateTimeToUnixTime(order.ExpiresAt.Value) : DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":notes", (object)order.Notes ?? DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(order.Created)));
                    cmd.Parameters.Add(new SQLiteParameter(":updated", Utils.DateTimeToUnixTime(order.Updated)));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + GloebitAuthColumns + " FROM store_gloebit_auth WHERE AvatarPrincipalID = :avatarid", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":avatarid", avatarId.ToString()));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO store_gloebit_auth (" + GloebitAuthColumns + ") " +
                        "VALUES (:avatarid, :gloebitid, :accesstoken, :authorized, :created, :updated)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":avatarid", auth.AvatarPrincipalID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":gloebitid", (object)auth.GloebitID ?? DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":accesstoken", (object)auth.AccessToken ?? DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":authorized", auth.Authorized));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(auth.Created)));
                    cmd.Parameters.Add(new SQLiteParameter(":updated", Utils.DateTimeToUnixTime(auth.Updated)));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT " + GloebitTxnColumns + " FROM store_gloebit_transactions WHERE ID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id.ToString()));

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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO store_gloebit_transactions (" + GloebitTxnColumns + ") " +
                        "VALUES (:id, :storeorderid, :avatarid, :amount, :stage, :enacted, :consumed, :cancelled, " +
                        ":responsereason, :created, :updated)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", txn.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":storeorderid", txn.StoreOrderID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":avatarid", txn.AvatarPrincipalID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":amount", txn.Amount));
                    cmd.Parameters.Add(new SQLiteParameter(":stage", txn.Stage));
                    cmd.Parameters.Add(new SQLiteParameter(":enacted", txn.Enacted));
                    cmd.Parameters.Add(new SQLiteParameter(":consumed", txn.Consumed));
                    cmd.Parameters.Add(new SQLiteParameter(":cancelled", txn.Cancelled));
                    cmd.Parameters.Add(new SQLiteParameter(":responsereason", (object)txn.ResponseReason ?? DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(txn.Created)));
                    cmd.Parameters.Add(new SQLiteParameter(":updated", Utils.DateTimeToUnixTime(txn.Updated)));

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

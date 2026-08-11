using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    public class SQLiteSearchData : ISearchData
    {
        private readonly SQLiteConnection m_conn;

        private const uint ForSaleFlag = 0x1000;
        private const uint ShowDirectoryFlag = 0x100000;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteSearchData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "SearchStore");
            m.Update();
        }

        public List<LandSearchRecord> SearchPlaces(string queryText, int start, int count, int maxAccess)
        {
            List<LandSearchRecord> results = new List<LandSearchRecord>();

            // Real per-region maturity filter (see MySqlSearchData for the
            // full rationale) - LEFT JOIN + COALESCE so an unmatched region
            // defaults to Adult/unrestricted rather than hiding the parcel.
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT land.UUID, land.Name, land.LandFlags, land.SalePrice, land.AuctionID, land.Area, land.Dwell FROM land " +
                        "LEFT JOIN regions ON land.RegionUUID = regions.uuid " +
                        "WHERE (land.LandFlags & :showdir) <> 0 AND (land.Name LIKE :query OR land.Desc LIKE :query) " +
                        "AND COALESCE(regions.access, 42) <= :maxaccess " +
                        "ORDER BY land.Dwell DESC LIMIT :count OFFSET :start", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":showdir", ShowDirectoryFlag));
                    cmd.Parameters.Add(new SQLiteParameter(":query", "%" + (queryText ?? string.Empty) + "%"));
                    cmd.Parameters.Add(new SQLiteParameter(":maxaccess", maxAccess));
                    cmd.Parameters.Add(new SQLiteParameter(":start", start < 0 ? 0 : start));
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 100 : count));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadRecord(reader));
                    }
                }
            }

            return results;
        }

        public List<LandSearchRecord> SearchLandForSale(int minPrice, int minArea, int start, int count)
        {
            List<LandSearchRecord> results = new List<LandSearchRecord>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT UUID, Name, LandFlags, SalePrice, AuctionID, Area, Dwell FROM land " +
                        "WHERE (LandFlags & :forsale) <> 0 AND (LandFlags & :showdir) <> 0 " +
                        "AND SalePrice >= :minprice AND Area >= :minarea " +
                        "ORDER BY SalePrice ASC LIMIT :count OFFSET :start", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":forsale", ForSaleFlag));
                    cmd.Parameters.Add(new SQLiteParameter(":showdir", ShowDirectoryFlag));
                    cmd.Parameters.Add(new SQLiteParameter(":minprice", minPrice < 0 ? 0 : minPrice));
                    cmd.Parameters.Add(new SQLiteParameter(":minarea", minArea < 0 ? 0 : minArea));
                    cmd.Parameters.Add(new SQLiteParameter(":start", start < 0 ? 0 : start));
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 100 : count));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadRecord(reader));
                    }
                }
            }

            return results;
        }

        public void LogSearch(string query, string category, int resultCount)
        {
            if (string.IsNullOrWhiteSpace(query) || resultCount <= 0)
                return;

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT INTO search_log (Query, Category, ResultCount, Created) VALUES (:query, :category, :count, :now)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":query", query.Trim()));
                    cmd.Parameters.Add(new SQLiteParameter(":category", category ?? "all"));
                    cmd.Parameters.Add(new SQLiteParameter(":count", resultCount));
                    cmd.Parameters.Add(new SQLiteParameter(":now", Util.UnixTimeSinceEpoch()));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public List<string> GetTrendingQueries(int count)
        {
            List<string> results = new List<string>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT Query FROM search_log WHERE Created >= :since " +
                        "GROUP BY Query ORDER BY COUNT(*) DESC, MAX(Created) DESC LIMIT :count", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":since", Util.UnixTimeSinceEpoch() - (30 * 24 * 60 * 60)));
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 8 : count));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(reader.GetString(0));
                    }
                }
            }

            return results;
        }

        public List<string> GetSuggestions(string prefix, int count)
        {
            List<string> results = new List<string>();

            if (string.IsNullOrWhiteSpace(prefix))
                return results;

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT Query FROM search_log WHERE Query LIKE :prefix " +
                        "GROUP BY Query ORDER BY COUNT(*) DESC LIMIT :count", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":prefix", prefix.Trim() + "%"));
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 8 : count));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(reader.GetString(0));
                    }
                }
            }

            return results;
        }

        private static LandSearchRecord ReadRecord(IDataReader reader)
        {
            uint flags = reader.IsDBNull(2) ? 0 : (uint)System.Convert.ToInt64(reader.GetValue(2));
            long auctionId = reader.IsDBNull(4) ? 0 : System.Convert.ToInt64(reader.GetValue(4));

            return new LandSearchRecord
            {
                ParcelID = UUID.Parse(reader.GetString(0)),
                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                ForSale = (flags & ForSaleFlag) != 0,
                SalePrice = reader.IsDBNull(3) ? 0 : System.Convert.ToInt32(reader.GetValue(3)),
                Auction = auctionId != 0,
                Area = reader.IsDBNull(5) ? 0 : System.Convert.ToInt32(reader.GetValue(5)),
                Dwell = reader.IsDBNull(6) ? 0f : System.Convert.ToInt32(reader.GetValue(6))
            };
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLSearchData : ISearchData
    {
        private readonly string m_connectionString;

        private const uint ForSaleFlag = 0x1000;
        private const uint ShowDirectoryFlag = 0x100000;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLSearchData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "SearchStore");
                m.Update();
            }
        }

        public List<LandSearchRecord> SearchPlaces(string queryText, int start, int count, int maxAccess)
        {
            List<LandSearchRecord> results = new List<LandSearchRecord>();

            // Real per-region maturity filter (see MySqlSearchData for the
            // full rationale) - LEFT JOIN + COALESCE so an unmatched region
            // defaults to Adult/unrestricted rather than hiding the parcel.
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT land.\"UUID\", land.\"Name\", land.\"LandFlags\", land.\"SalePrice\", land.\"AuctionID\", land.\"Area\", land.\"Dwell\" FROM land " +
                    "LEFT JOIN regions ON land.\"RegionUUID\" = regions.uuid " +
                    "WHERE (land.\"LandFlags\" & :showdir) <> 0 AND (land.\"Name\" ILIKE :query OR land.\"Description\" ILIKE :query) " +
                    "AND COALESCE(regions.access, 42) <= :maxaccess " +
                    "ORDER BY land.\"Dwell\" DESC LIMIT :count OFFSET :start", conn))
            {
                cmd.Parameters.AddWithValue(":showdir", (int)ShowDirectoryFlag);
                cmd.Parameters.AddWithValue(":query", "%" + (queryText ?? string.Empty) + "%");
                cmd.Parameters.AddWithValue(":maxaccess", maxAccess);
                cmd.Parameters.AddWithValue(":start", start < 0 ? 0 : start);
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 100 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadRecord(reader));
                }
            }

            return results;
        }

        public List<LandSearchRecord> SearchLandForSale(int minPrice, int minArea, int start, int count)
        {
            List<LandSearchRecord> results = new List<LandSearchRecord>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"UUID\", \"Name\", \"LandFlags\", \"SalePrice\", \"AuctionID\", \"Area\", \"Dwell\" FROM land " +
                    "WHERE (\"LandFlags\" & :forsale) <> 0 AND (\"LandFlags\" & :showdir) <> 0 " +
                    "AND \"SalePrice\" >= :minprice AND \"Area\" >= :minarea " +
                    "ORDER BY \"SalePrice\" ASC LIMIT :count OFFSET :start", conn))
            {
                cmd.Parameters.AddWithValue(":forsale", (int)ForSaleFlag);
                cmd.Parameters.AddWithValue(":showdir", (int)ShowDirectoryFlag);
                cmd.Parameters.AddWithValue(":minprice", minPrice < 0 ? 0 : minPrice);
                cmd.Parameters.AddWithValue(":minarea", minArea < 0 ? 0 : minArea);
                cmd.Parameters.AddWithValue(":start", start < 0 ? 0 : start);
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 100 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadRecord(reader));
                }
            }

            return results;
        }

        public void LogSearch(string query, string category, int resultCount)
        {
            if (string.IsNullOrWhiteSpace(query) || resultCount <= 0)
                return;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO search_log (\"Query\", \"Category\", \"ResultCount\", \"Created\") VALUES (:query, :category, :count, :now)", conn))
            {
                cmd.Parameters.AddWithValue(":query", query.Trim());
                cmd.Parameters.AddWithValue(":category", category ?? "all");
                cmd.Parameters.AddWithValue(":count", resultCount);
                cmd.Parameters.AddWithValue(":now", (int)Util.UnixTimeSinceEpoch());
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        public List<string> GetTrendingQueries(int count)
        {
            List<string> results = new List<string>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"Query\" FROM search_log WHERE \"Created\" >= :since " +
                    "GROUP BY \"Query\" ORDER BY COUNT(*) DESC, MAX(\"Created\") DESC LIMIT :count", conn))
            {
                cmd.Parameters.AddWithValue(":since", (int)Util.UnixTimeSinceEpoch() - (30 * 24 * 60 * 60));
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 8 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(reader.GetString(0));
                }
            }

            return results;
        }

        public List<string> GetSuggestions(string prefix, int count)
        {
            List<string> results = new List<string>();

            if (string.IsNullOrWhiteSpace(prefix))
                return results;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"Query\" FROM search_log WHERE \"Query\" ILIKE :prefix " +
                    "GROUP BY \"Query\" ORDER BY COUNT(*) DESC LIMIT :count", conn))
            {
                cmd.Parameters.AddWithValue(":prefix", prefix.Trim() + "%");
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 8 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(reader.GetString(0));
                }
            }

            return results;
        }

        private static LandSearchRecord ReadRecord(NpgsqlDataReader reader)
        {
            uint flags = reader.IsDBNull(2) ? 0 : (uint)reader.GetInt64(2);
            long auctionId = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);

            return new LandSearchRecord
            {
                ParcelID = UUID.Parse(reader.GetString(0)),
                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                ForSale = (flags & ForSaleFlag) != 0,
                SalePrice = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                Auction = auctionId != 0,
                Area = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Dwell = reader.IsDBNull(6) ? 0f : reader.GetInt32(6)
            };
        }
    }
}

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

        // ParcelFlags.ForSale = 0x4, ParcelFlags.ShowDirectory = 0x1000
        // (confirmed by enumerating the real, compiled OpenMetaverse.ParcelFlags
        // enum directly against OpenMetaverseTypes.dll/OpenMetaverse.dll - the
        // previous 0x1000/0x100000 values here were wrong).
        private const uint ForSaleFlag = 0x4;
        private const uint ShowDirectoryFlag = 0x1000;

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
                    "SELECT land.\"UUID\", land.\"Name\", land.\"LandFlags\", land.\"SalePrice\", land.\"AuctionID\", land.\"Area\", land.\"Dwell\", " +
                    "land.\"RegionUUID\", regions.\"regionName\", land.\"Description\", land.\"Category\", " +
                    "land.\"UserLocationX\", land.\"UserLocationY\", land.\"UserLocationZ\" FROM land " +
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
                        results.Add(ReadEnrichedRecord(reader));
                }
            }

            return results;
        }

        // Destination Guide "Featured" tab - see ISearchData for the
        // rationale. Same enriched column set as SearchPlaces.
        public List<LandSearchRecord> GetFeaturedPlaces(int count, int maxAccess)
        {
            List<LandSearchRecord> results = new List<LandSearchRecord>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT land.\"UUID\", land.\"Name\", land.\"LandFlags\", land.\"SalePrice\", land.\"AuctionID\", land.\"Area\", land.\"Dwell\", " +
                    "land.\"RegionUUID\", regions.\"regionName\", land.\"Description\", land.\"Category\", " +
                    "land.\"UserLocationX\", land.\"UserLocationY\", land.\"UserLocationZ\" FROM land " +
                    "LEFT JOIN regions ON land.\"RegionUUID\" = regions.uuid " +
                    "WHERE (land.\"LandFlags\" & :showdir) <> 0 AND land.\"Category\" > 0 " +
                    "AND COALESCE(regions.access, 42) <= :maxaccess " +
                    "ORDER BY RANDOM() LIMIT :count", conn))
            {
                cmd.Parameters.AddWithValue(":showdir", (int)ShowDirectoryFlag);
                cmd.Parameters.AddWithValue(":maxaccess", maxAccess);
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 30 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadEnrichedRecord(reader));
                }
            }

            return results;
        }

        // maxPrice/minArea semantics match OpenSim-Grid-Interface's real
        // helper/query.php (dir_land_query) - see MySqlSearchData's copy of
        // this method for the full rationale. Enriched (RegionName/Landing
        // position) same as SearchPlaces now - ConfluenceSearchModule.
        // DirLandQuery needs those to build the viewer's "fake parcel ID"
        // for each result (see MySqlSearchData's fuller comment on this).
        public List<LandSearchRecord> SearchLandForSale(int maxPrice, int minArea, int start, int count)
        {
            List<LandSearchRecord> results = new List<LandSearchRecord>();

            string sql = "SELECT land.\"UUID\", land.\"Name\", land.\"LandFlags\", land.\"SalePrice\", land.\"AuctionID\", land.\"Area\", land.\"Dwell\", " +
                    "land.\"RegionUUID\", regions.\"regionName\", land.\"Description\", land.\"Category\", " +
                    "land.\"UserLocationX\", land.\"UserLocationY\", land.\"UserLocationZ\" FROM land " +
                    "LEFT JOIN regions ON land.\"RegionUUID\" = regions.uuid " +
                    "WHERE (land.\"LandFlags\" & :forsale) <> 0 AND (land.\"LandFlags\" & :showdir) <> 0 ";
            if (maxPrice > 0)
                sql += "AND land.\"SalePrice\" <= :maxprice ";
            if (minArea > 0)
                sql += "AND land.\"Area\" >= :minarea ";
            sql += "ORDER BY land.\"SalePrice\" ASC LIMIT :count OFFSET :start";

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue(":forsale", (int)ForSaleFlag);
                cmd.Parameters.AddWithValue(":showdir", (int)ShowDirectoryFlag);
                if (maxPrice > 0)
                    cmd.Parameters.AddWithValue(":maxprice", maxPrice);
                if (minArea > 0)
                    cmd.Parameters.AddWithValue(":minarea", minArea);
                cmd.Parameters.AddWithValue(":start", start < 0 ? 0 : start);
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 100 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadEnrichedRecord(reader));
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

        // /myland self-service source - see MySqlSearchData's copy of this
        // method for the full rationale.
        public List<LandSearchRecord> GetParcelsByOwner(UUID ownerID)
        {
            List<LandSearchRecord> results = new List<LandSearchRecord>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT land.\"UUID\", land.\"Name\", land.\"LandFlags\", land.\"SalePrice\", land.\"AuctionID\", land.\"Area\", land.\"Dwell\", " +
                    "land.\"RegionUUID\", regions.\"regionName\", land.\"Description\", land.\"Category\", " +
                    "land.\"UserLocationX\", land.\"UserLocationY\", land.\"UserLocationZ\" FROM land " +
                    "LEFT JOIN regions ON land.\"RegionUUID\" = regions.uuid " +
                    "WHERE land.\"OwnerUUID\" = :ownerid ORDER BY regions.\"regionName\", land.\"Name\"", conn))
            {
                cmd.Parameters.AddWithValue(":ownerid", ownerID.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadEnrichedRecord(reader));
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
                ShowInSearch = (flags & ShowDirectoryFlag) != 0,
                SalePrice = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                Auction = auctionId != 0,
                Area = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Dwell = reader.IsDBNull(6) ? 0f : reader.GetInt32(6)
            };
        }

        // See MySqlSearchData's ReadEnrichedRecord for the rationale -
        // same base columns (0-6) plus RegionUUID and the Destination-
        // Guide-only columns at 7-13.
        private static LandSearchRecord ReadEnrichedRecord(NpgsqlDataReader reader)
        {
            LandSearchRecord record = ReadRecord(reader);
            record.RegionID = reader.IsDBNull(7) ? UUID.Zero : UUID.Parse(reader.GetString(7));
            record.RegionName = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
            record.Description = reader.IsDBNull(9) ? string.Empty : reader.GetString(9);
            record.Category = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
            record.LandingX = reader.IsDBNull(11) ? 0f : (float)reader.GetDouble(11);
            record.LandingY = reader.IsDBNull(12) ? 0f : (float)reader.GetDouble(12);
            record.LandingZ = reader.IsDBNull(13) ? 0f : (float)reader.GetDouble(13);
            return record;
        }
    }
}

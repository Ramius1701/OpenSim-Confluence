using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    // Places/land search is read-only against the existing `land` table -
    // no schema of its own. Search logging (added for real Trending/
    // autocomplete on /web/search, rather than fabricating either) does own
    // one small table, `search_log`, hence the migration below - see
    // ISearchData for the full design rationale.
    public class MySqlSearchData : ISearchData
    {
        private readonly string m_connectionString;

        // ParcelFlags.ForSale = 0x1000, ParcelFlags.ShowDirectory = 0x100000
        // (OpenMetaverse.ParcelFlags, same values LandManagementModule.cs
        // already uses elsewhere in this codebase).
        private const uint ForSaleFlag = 0x1000;
        private const uint ShowDirectoryFlag = 0x100000;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlSearchData(string connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "SearchStore");
                m.Update();
            }
        }

        public List<LandSearchRecord> SearchPlaces(string queryText, int start, int count, int maxAccess)
        {
            List<LandSearchRecord> results = new List<LandSearchRecord>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                // Real per-region maturity filter, not decorative - joins to
                // the `regions` table's own persisted `access` byte (13/21/42
                // = PG/Mature/Adult, same convention Util.
                // ConvertMaturityToAccessLevel already uses elsewhere).
                // Parcels don't carry their own maturity in this schema, only
                // the region does. LEFT JOIN + COALESCE(...,42) so a parcel
                // whose region row is missing/unmatched still shows up
                // (Adult/unrestricted) rather than silently vanishing from
                // results due to a join miss.
                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT land.UUID, land.Name, land.LandFlags, land.SalePrice, land.AuctionID, land.Area, land.Dwell FROM land " +
                        "LEFT JOIN regions ON land.RegionUUID = regions.uuid " +
                        "WHERE (land.LandFlags & ?ShowDirectory) <> 0 AND (land.Name LIKE ?query OR land.Description LIKE ?query) " +
                        "AND COALESCE(regions.access, 42) <= ?maxAccess " +
                        "ORDER BY land.Dwell DESC LIMIT ?start, ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ShowDirectory", ShowDirectoryFlag);
                    cmd.Parameters.AddWithValue("?query", "%" + (queryText ?? string.Empty) + "%");
                    cmd.Parameters.AddWithValue("?maxAccess", maxAccess);
                    cmd.Parameters.AddWithValue("?start", start < 0 ? 0 : start);
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 100 : count);

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

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT UUID, Name, LandFlags, SalePrice, AuctionID, Area, Dwell FROM land " +
                        "WHERE (LandFlags & ?ForSale) <> 0 AND (LandFlags & ?ShowDirectory) <> 0 " +
                        "AND SalePrice >= ?minPrice AND Area >= ?minArea " +
                        "ORDER BY SalePrice ASC LIMIT ?start, ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ForSale", ForSaleFlag);
                    cmd.Parameters.AddWithValue("?ShowDirectory", ShowDirectoryFlag);
                    cmd.Parameters.AddWithValue("?minPrice", minPrice < 0 ? 0 : minPrice);
                    cmd.Parameters.AddWithValue("?minArea", minArea < 0 ? 0 : minArea);
                    cmd.Parameters.AddWithValue("?start", start < 0 ? 0 : start);
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 100 : count);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadRecord(reader));
                    }
                }
            }

            return results;
        }

        // Only successful (resultCount>0) searches get logged, by design -
        // mirrors "suggestions from past searches that returned results",
        // so Trending/autocomplete never surface dead-end queries.
        public void LogSearch(string query, string category, int resultCount)
        {
            if (string.IsNullOrWhiteSpace(query) || resultCount <= 0)
                return;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "INSERT INTO search_log (Query, Category, ResultCount, Created) VALUES (?query, ?category, ?count, ?now)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?query", query.Trim());
                    cmd.Parameters.AddWithValue("?category", category ?? "all");
                    cmd.Parameters.AddWithValue("?count", resultCount);
                    cmd.Parameters.AddWithValue("?now", Util.UnixTimeSinceEpoch());
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // Most frequent successful search terms in the last 30 days -
        // real usage data, not a hand-picked or fabricated list.
        public List<string> GetTrendingQueries(int count)
        {
            List<string> results = new List<string>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT Query FROM search_log WHERE Created >= ?since " +
                        "GROUP BY Query ORDER BY COUNT(*) DESC, MAX(Created) DESC LIMIT ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?since", Util.UnixTimeSinceEpoch() - (30 * 24 * 60 * 60));
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 8 : count);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(reader.GetString(0));
                    }
                }
            }

            return results;
        }

        // Prefix match against past successful queries, most-used first -
        // backs the search box's autocomplete dropdown.
        public List<string> GetSuggestions(string prefix, int count)
        {
            List<string> results = new List<string>();

            if (string.IsNullOrWhiteSpace(prefix))
                return results;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT Query FROM search_log WHERE Query LIKE ?prefix " +
                        "GROUP BY Query ORDER BY COUNT(*) DESC LIMIT ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?prefix", prefix.Trim() + "%");
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 8 : count);

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

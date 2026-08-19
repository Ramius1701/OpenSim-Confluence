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
                        "SELECT land.UUID, land.Name, land.LandFlags, land.SalePrice, land.AuctionID, land.Area, land.Dwell, " +
                        "land.RegionUUID, regions.regionName, land.Description, land.Category, " +
                        "land.UserLocationX, land.UserLocationY, land.UserLocationZ FROM land " +
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
                            results.Add(ReadEnrichedRecord(reader));
                    }
                }
            }

            return results;
        }

        // Destination Guide "Featured" tab - see ISearchData for the
        // rationale. Same enriched column set as SearchPlaces (needed for
        // the same teleport-link/region/description use), but filtered by
        // a real category instead of free text, and randomly ordered.
        public List<LandSearchRecord> GetFeaturedPlaces(int count, int maxAccess)
        {
            List<LandSearchRecord> results = new List<LandSearchRecord>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT land.UUID, land.Name, land.LandFlags, land.SalePrice, land.AuctionID, land.Area, land.Dwell, " +
                        "land.RegionUUID, regions.regionName, land.Description, land.Category, " +
                        "land.UserLocationX, land.UserLocationY, land.UserLocationZ FROM land " +
                        "LEFT JOIN regions ON land.RegionUUID = regions.uuid " +
                        "WHERE (land.LandFlags & ?ShowDirectory) <> 0 AND land.Category > 0 " +
                        "AND COALESCE(regions.access, 42) <= ?maxAccess " +
                        "ORDER BY RAND() LIMIT ?count", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ShowDirectory", ShowDirectoryFlag);
                    cmd.Parameters.AddWithValue("?maxAccess", maxAccess);
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 30 : count);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadEnrichedRecord(reader));
                    }
                }
            }

            return results;
        }

        // maxPrice/minArea semantics match OpenSim-Grid-Interface's real
        // helper/query.php (dir_land_query) exactly - that PHP script was
        // the actual, proven backend behind the viewer's Land Sales tab
        // before this native module existed, so its filter semantics are
        // the reference, not a guess: price is a CEILING ("no more than"),
        // area is a FLOOR ("at least"), and either filter only applies when
        // the caller actually wants it (0/negative = "don't filter on
        // this") - matching query.php's own LimitByPrice/LimitByArea flag
        // gating, and also matching how /web/landsearch already calls this
        // method with (0, 0, ...) expecting "show everything".
        // Enriched (RegionName/LandingX-Y-Z) same as SearchPlaces now, not
        // just the base 7 columns - ConfluenceSearchModule.DirLandQuery
        // needs RegionName+Landing position to build the viewer's "fake
        // parcel ID" (region handle + local x/y baked into a UUID, see
        // Util.BuildFakeParcelID) for each result. Without a real fake ID,
        // clicking a Land Sales result sends a ParcelInfoRequest the stock
        // LandManagementModule.ClientOnParcelInfoRequest can never resolve
        // (Util.ParseFakeParcelID fails on a plain database UUID and it
        // silently drops the request) - the viewer's detail pane and
        // Teleport/Map buttons then hang on "Loading..." forever.
        public List<LandSearchRecord> SearchLandForSale(int maxPrice, int minArea, int start, int count)
        {
            List<LandSearchRecord> results = new List<LandSearchRecord>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                string sql = "SELECT land.UUID, land.Name, land.LandFlags, land.SalePrice, land.AuctionID, land.Area, land.Dwell, " +
                        "land.RegionUUID, regions.regionName, land.Description, land.Category, " +
                        "land.UserLocationX, land.UserLocationY, land.UserLocationZ FROM land " +
                        "LEFT JOIN regions ON land.RegionUUID = regions.uuid " +
                        "WHERE (land.LandFlags & ?ForSale) <> 0 AND (land.LandFlags & ?ShowDirectory) <> 0 ";
                if (maxPrice > 0)
                    sql += "AND land.SalePrice <= ?maxPrice ";
                if (minArea > 0)
                    sql += "AND land.Area >= ?minArea ";
                sql += "ORDER BY land.SalePrice ASC LIMIT ?start, ?count";

                using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
                {
                    cmd.Parameters.AddWithValue("?ForSale", ForSaleFlag);
                    cmd.Parameters.AddWithValue("?ShowDirectory", ShowDirectoryFlag);
                    if (maxPrice > 0)
                        cmd.Parameters.AddWithValue("?maxPrice", maxPrice);
                    if (minArea > 0)
                        cmd.Parameters.AddWithValue("?minArea", minArea);
                    cmd.Parameters.AddWithValue("?start", start < 0 ? 0 : start);
                    cmd.Parameters.AddWithValue("?count", count <= 0 ? 100 : count);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadEnrichedRecord(reader));
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

        // /myland self-service source - every parcel this owner has,
        // anywhere on the grid, no ShowDirectory/maxAccess gate (the owner
        // managing their own land, not browsing a search result).
        public List<LandSearchRecord> GetParcelsByOwner(UUID ownerID)
        {
            List<LandSearchRecord> results = new List<LandSearchRecord>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT land.UUID, land.Name, land.LandFlags, land.SalePrice, land.AuctionID, land.Area, land.Dwell, " +
                        "land.RegionUUID, regions.regionName, land.Description, land.Category, " +
                        "land.UserLocationX, land.UserLocationY, land.UserLocationZ FROM land " +
                        "LEFT JOIN regions ON land.RegionUUID = regions.uuid " +
                        "WHERE land.OwnerUUID = ?ownerID ORDER BY regions.regionName, land.Name", dbcon))
                {
                    cmd.Parameters.AddWithValue("?ownerID", ownerID.ToString());

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadEnrichedRecord(reader));
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
                ShowInSearch = (flags & ShowDirectoryFlag) != 0,
                SalePrice = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                Auction = auctionId != 0,
                Area = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Dwell = reader.IsDBNull(6) ? 0f : reader.GetInt32(6)
            };
        }

        // Same base columns as ReadRecord (ordinals 0-6) plus RegionUUID/
        // RegionName/Description/Category/Landing position at ordinals
        // 7-13 - shared by SearchPlaces, GetFeaturedPlaces,
        // SearchLandForSale and GetParcelsByOwner, all of which now need
        // RegionName+Landing to build a real viewer-facing parcel ID (see
        // SearchLandForSale's own comment), and GetParcelsByOwner
        // specifically also needs RegionID to route a remote console
        // command to the right region (see LandManagementModule's "land
        // search enable/disable").
        private static LandSearchRecord ReadEnrichedRecord(IDataReader reader)
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

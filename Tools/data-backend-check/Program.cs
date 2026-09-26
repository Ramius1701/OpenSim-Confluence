using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;

static class Program
{
    static int checks = 0, failures = 0;

    static void Eq(string name, object expected, object actual)
    {
        checks++;
        if (!Equals(expected, actual))
        {
            failures++;
            Console.WriteLine("FAIL " + name + "\n   expected: " + expected + "\n   actual:   " + actual);
        }
    }

    static MarketplaceListing Make(UUID seller, string title, int? stock, bool listed, int price)
    {
        return new MarketplaceListing
        {
            SellerID = seller,
            Title = title,
            Description = "Description of " + title + " with unicode é中 and 'quotes'",
            Price = price,
            CountOnHand = stock,
            IsListed = listed,
            SnapshotFolderID = UUID.Random(),
            ListingFolderID = UUID.Random(),
            VersionFolderID = UUID.Random(),
            SnapshotFingerprint = "fp-" + title,
            Created = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            Updated = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc)
        };
    }

    static void Market(string label, IMarketplaceListingsData d)
    {
        UUID seller = UUID.Random(), other = UUID.Random();

        Eq(label + " get missing listing is null", null, d.GetListing(999999));

        MarketplaceListing a = Make(seller, "A finite", 2, true, 50);
        int idA = d.InsertListing(a);
        Eq(label + " insert returns positive id", true, idA > 0);
        MarketplaceListing got = d.GetListing(idA);
        Eq(label + " round trip title", a.Title, got.Title);
        Eq(label + " round trip description (unicode, quotes)", a.Description, got.Description);
        Eq(label + " round trip price", 50, got.Price);
        Eq(label + " round trip stock", (int?)2, got.CountOnHand);
        Eq(label + " round trip listed", true, got.IsListed);
        Eq(label + " round trip seller", seller, got.SellerID);
        Eq(label + " round trip folders", a.SnapshotFolderID, got.SnapshotFolderID);
        Eq(label + " round trip fingerprint", "fp-A finite", got.SnapshotFingerprint);
        Eq(label + " round trip created", a.Created, got.Created);
        Eq(label + " round trip updated", a.Updated, got.Updated);

        MarketplaceListing u = Make(seller, "B unlimited", null, false, 10);
        int idU = d.InsertListing(u);
        Eq(label + " unlimited stock stored as null", (int?)null, d.GetListing(idU).CountOnHand);
        Eq(label + " unlisted stored as false", false, d.GetListing(idU).IsListed);

        int idO = d.InsertListing(Make(other, "C other seller", 5, true, 1));
        Eq(label + " by-seller returns only that seller's", 2, d.GetListingsBySeller(seller).Count);
        Eq(label + " by-seller other", 1, d.GetListingsBySeller(other).Count);

        List<MarketplaceListing> listed = d.GetListedListings(0, 10);
        Eq(label + " listed excludes unlisted", 2, listed.Count);
        Eq(label + " listed paging count", 1, d.GetListedListings(0, 1).Count);
        Eq(label + " listed paging start", 1, d.GetListedListings(1, 10).Count);

        // update
        got.Title = "A renamed";
        got.Price = 75;
        got.CountOnHand = 3;
        got.SnapshotFingerprint = "fp-new";
        got.Updated = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        Eq(label + " update reports success", true, d.UpdateListing(got));
        MarketplaceListing again = d.GetListing(idA);
        Eq(label + " update title", "A renamed", again.Title);
        Eq(label + " update price", 75, again.Price);
        Eq(label + " update stock", (int?)3, again.CountOnHand);
        Eq(label + " update keeps Created", a.Created, again.Created);
        got.ID = 424242;
        Eq(label + " update missing reports false", false, d.UpdateListing(got));

        // listed state
        Eq(label + " set listed false", true, d.SetListedState(idA, false));
        Eq(label + " set listed false stored", false, d.GetListing(idA).IsListed);
        Eq(label + " set listed missing", false, d.SetListedState(777777, true));
        Eq(label + " set listed true", true, d.SetListedState(idA, true));

        // stock: finite (3 units)
        Eq(label + " reserve 1", true, d.TryReserveStock(idA));
        Eq(label + " reserve 2", true, d.TryReserveStock(idA));
        Eq(label + " reserve 3", true, d.TryReserveStock(idA));
        Eq(label + " reserve at zero refused", false, d.TryReserveStock(idA));
        Eq(label + " stock is zero", (int?)0, d.GetListing(idA).CountOnHand);
        d.ReleaseStock(idA);
        Eq(label + " release gives one back", (int?)1, d.GetListing(idA).CountOnHand);
        Eq(label + " reserve after release", true, d.TryReserveStock(idA));
        Eq(label + " stock back to zero", (int?)0, d.GetListing(idA).CountOnHand);

        // stock: unlimited but unlisted, then listed
        Eq(label + " unlimited unlisted refused", false, d.TryReserveStock(idU));
        d.SetListedState(idU, true);
        Eq(label + " unlimited listed always ok", true, d.TryReserveStock(idU));
        Eq(label + " unlimited listed always ok again", true, d.TryReserveStock(idU));
        Eq(label + " unlimited stays null", (int?)null, d.GetListing(idU).CountOnHand);
        d.ReleaseStock(idU);
        Eq(label + " release on unlimited is a no-op", (int?)null, d.GetListing(idU).CountOnHand);
        Eq(label + " reserve missing refused", false, d.TryReserveStock(31337));

        // unlisted finite refused
        d.SetListedState(idO, false);
        Eq(label + " unlisted finite refused", false, d.TryReserveStock(idO));
        Eq(label + " unlisted finite unchanged", (int?)5, d.GetListing(idO).CountOnHand);

        // deliveries
        DeliveryReceipt r;
        Eq(label + " get missing delivery", false, d.TryGetDelivery("nope", out r));
        DeliveryReceipt rec = new DeliveryReceipt
        {
            DeliveryId = "delivery-" + Guid.NewGuid().ToString("N"),
            SellerId = seller.ToString(),
            SnapshotFolderId = UUID.Random().ToString(),
            RecipientId = other.ToString(),
            SnapshotFingerprint = "fingerprint",
            DestinationFolderId = UUID.Random().ToString(),
            ItemCount = 7,
            FolderCount = 2
        };
        Eq(label + " insert delivery", true, d.TryInsertDelivery(rec));
        Eq(label + " duplicate delivery id refused", false, d.TryInsertDelivery(rec));
        Eq(label + " get delivery", true, d.TryGetDelivery(rec.DeliveryId, out r));
        Eq(label + " delivery seller", rec.SellerId, r.SellerId);
        Eq(label + " delivery recipient", rec.RecipientId, r.RecipientId);
        Eq(label + " delivery fingerprint", "fingerprint", r.SnapshotFingerprint);
        Eq(label + " delivery destination", rec.DestinationFolderId, r.DestinationFolderId);
        Eq(label + " delivery item count", 7, r.ItemCount);
        Eq(label + " delivery folder count", 2, r.FolderCount);
        Eq(label + " delivery timestamp parses", true, DateTime.TryParse(r.TimestampUtc, out _));

        // a second instance over the same database finds the tables already migrated
    }

    // Many buyers racing for a few units: exactly that many must win, never more (no overselling).
    static void Race(string label, IMarketplaceListingsData d)
    {
        int id = d.InsertListing(Make(UUID.Random(), "Race", 5, true, 1));
        int wins = 0;
        System.Threading.Tasks.Parallel.For(0, 60, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            if (d.TryReserveStock(id))
                System.Threading.Interlocked.Increment(ref wins);
        });
        Eq(label + " race: exactly 5 of 60 buyers win", 5, wins);
        Eq(label + " race: stock ends at zero, never negative", (int?)0, d.GetListing(id).CountOnHand);
    }

    static OfflineIMData Im(UUID to, UUID from, string text)
    {
        OfflineIMData m = new OfflineIMData();
        m.PrincipalID = to;
        m.FromID = from;
        m.Data = new Dictionary<string, string>();
        m.Data["Message"] = text;
        return m;
    }

    // ageSql runs a raw statement to make one message look 20 days old (per database).
    static void OfflineIM(string label, IOfflineIMData d, Action<string> ageMessage)
    {
        UUID me = UUID.Random(), other = UUID.Random(), from1 = UUID.Random(), from2 = UUID.Random();
        string text = "<GridInstantMessage><message>Hello é中 'quoted' &amp; \"double\" \U0001F600</message></GridInstantMessage>";

        Eq(label + " im: unknown principal has none", 0, d.Get("PrincipalID", me.ToString()).Length);
        Eq(label + " im: unknown principal count", 0L, d.GetCount("PrincipalID", me.ToString()));

        Eq(label + " im: store 1", true, d.Store(Im(me, from1, text)));
        Eq(label + " im: store 2", true, d.Store(Im(me, from2, "second")));
        Eq(label + " im: store 3", true, d.Store(Im(me, from1, "third")));
        Eq(label + " im: store other principal", true, d.Store(Im(other, from1, "for someone else")));

        OfflineIMData[] mine = d.Get("PrincipalID", me.ToString());
        Eq(label + " im: get returns own 3", 3, mine.Length);
        Eq(label + " im: count own", 3L, d.GetCount("PrincipalID", me.ToString()));
        Eq(label + " im: count other", 1L, d.GetCount("PrincipalID", other.ToString()));
        Eq(label + " im: count by sender", 2L, d.GetCount("FromID", from1.ToString()) - 1L); // 3 stored from1 in total: 2 to me + 1 to other
        long bad;
        try { bad = d.GetCount("PrincipalID; drop table im_offline", "x"); } catch (Exception) { bad = 0; }
        Eq(label + " im: bad column name is refused safely (none, or an error)", 0L, bad);
        Eq(label + " im: table still there after that", 3L, d.GetCount("PrincipalID", me.ToString()));

        bool sawText = false, allHaveId = true, principalOk = true;
        foreach (OfflineIMData m in mine)
        {
            if (m.Data.ContainsKey("Message") && m.Data["Message"] == text)
                sawText = true;
            if (!m.Data.ContainsKey("ID") || string.IsNullOrEmpty(m.Data["ID"]))
                allHaveId = false;
            if (m.PrincipalID != me)
                principalOk = false;
        }
        Eq(label + " im: unicode/quotes/emoji message round trips", true, sawText);
        Eq(label + " im: every message has its own ID", true, allHaveId);
        Eq(label + " im: principal id read back", true, principalOk);

        // delete exactly one message by ID, scoped to the principal
        string id0 = mine[0].Data["ID"];
        Eq(label + " im: delete with someone else's principal removes nothing", false,
           d.Delete(new string[] { "ID", "PrincipalID" }, new string[] { id0, other.ToString() }));
        Eq(label + " im: still 3 after wrong-principal delete", 3, d.Get("PrincipalID", me.ToString()).Length);
        Eq(label + " im: delete one by id+principal", true,
           d.Delete(new string[] { "ID", "PrincipalID" }, new string[] { id0, me.ToString() }));
        Eq(label + " im: 2 left after deleting one", 2, d.Get("PrincipalID", me.ToString()).Length);
        Eq(label + " im: other principal untouched", 1, d.Get("PrincipalID", other.ToString()).Length);

        // DeleteOld drops messages older than two weeks, keeps recent ones
        string oldId = d.Get("PrincipalID", me.ToString())[0].Data["ID"];
        ageMessage(oldId);
        d.DeleteOld();
        OfflineIMData[] afterOld = d.Get("PrincipalID", me.ToString());
        Eq(label + " im: DeleteOld removed the 20-day-old message", 1, afterOld.Length);
        Eq(label + " im: DeleteOld kept the recent one", true, afterOld[0].Data["ID"] != oldId);
        Eq(label + " im: DeleteOld left the other principal's recent message", 1, d.Get("PrincipalID", other.ToString()).Length);

        // delete all for a principal
        Eq(label + " im: delete all for principal", true, d.Delete("PrincipalID", me.ToString()));
        Eq(label + " im: none left", 0, d.Get("PrincipalID", me.ToString()).Length);
        Eq(label + " im: delete when none left reports false", false, d.Delete("PrincipalID", me.ToString()));
    }

    static void RegionHG(string label, IRegionHGData d)
    {
        UUID a = UUID.Random(), b = UUID.Random();
        Eq(label + " unknown region -> null", null, d.GetIsOpen(a));
        d.SetIsOpen(a, false);
        Eq(label + " set closed", (bool?)false, d.GetIsOpen(a));
        d.SetIsOpen(a, true);
        Eq(label + " upsert to open", (bool?)true, d.GetIsOpen(a));
        d.SetIsOpen(a, false);
        Eq(label + " upsert to closed again", (bool?)false, d.GetIsOpen(a));
        d.SetIsOpen(b, true);
        Eq(label + " other region independent", (bool?)true, d.GetIsOpen(b));
        Eq(label + " first region unchanged", (bool?)false, d.GetIsOpen(a));
    }

    static int Main(string[] args)
    {
        string sqliteFile = args.Length > 0 ? args[0] : "datatest.db";
        string pg = args.Length > 1 ? args[1] : null;

        string sq = "URI=file:" + sqliteFile + ",version=3";
        Market("sqlite", new OpenSim.Data.SQLite.SQLiteMarketplaceListingsData(sq));
        Race("sqlite", new OpenSim.Data.SQLite.SQLiteMarketplaceListingsData(sq));
        RegionHG("sqlite", new OpenSim.Data.SQLite.SQLiteRegionHGData(sq));
        OfflineIM("sqlite", new OpenSim.Data.SQLite.SQLiteOfflineIMData(sq, "im_offline"), id =>
        {
            using (var c = new System.Data.SQLite.SQLiteConnection(sq))
            {
                c.Open();
                using (var cmd = new System.Data.SQLite.SQLiteCommand("update im_offline set TMStamp = datetime('now','-20 days') where ID = " + long.Parse(id), c))
                    cmd.ExecuteNonQuery();
            }
        });
        // constructing again over the same file must not fail (migration is idempotent)
        Eq("sqlite reopen ok", true, new OpenSim.Data.SQLite.SQLiteMarketplaceListingsData(sq) != null);

        if (pg != null)
        {
            Market("pgsql", new OpenSim.Data.PGSQL.PGSQLMarketplaceListingsData(pg));
            Race("pgsql", new OpenSim.Data.PGSQL.PGSQLMarketplaceListingsData(pg));
            RegionHG("pgsql", new OpenSim.Data.PGSQL.PGSQLRegionHGData(pg));
            OfflineIM("pgsql", new OpenSim.Data.PGSQL.PGSQLOfflineIMData(pg, "im_offline"), id =>
            {
                using (var c = new Npgsql.NpgsqlConnection(pg))
                {
                    c.Open();
                    using (var cmd = new Npgsql.NpgsqlCommand("update im_offline set \"TMStamp\" = now() - interval '20 days' where \"ID\" = " + long.Parse(id), c))
                        cmd.ExecuteNonQuery();
                }
            });
            Eq("pgsql reopen ok", true, new OpenSim.Data.PGSQL.PGSQLMarketplaceListingsData(pg) != null);
        }

        Console.WriteLine(checks + " checks, " + failures + " failures");
        return failures == 0 ? 0 : 1;
    }
}

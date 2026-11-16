using System;
using System.Collections.Generic;
using System.Linq;

namespace EvoHelper;

// Snapshots of game data, built when the pick screen is filled in.
// The game's UpgradeInfo assets are kept for their icons; everything else is plain C#.

internal enum EvoStatus { HaveAll = 0, HaveSome = 1, HaveNone = 2 }

internal sealed class Item
{
    public UpgradeInfo Info;
    public string Name;
    public int Lvl;            // displayed level (1-based); 0 = not owned
    public bool Discovered;    // in the player's encyclopedia
    public bool IsBall;        // a ball (HeroInfo), not a passive
    public bool Owned => Lvl > 0;
}

internal sealed class EvoLine
{
    public Item Result;
    public List<Item> Partners = new();
    public EvoStatus Status;
}

internal sealed class Offer
{
    public string Kind;        // ball / passive / upgrade
    public Item Self;
    public bool IsNew;
    public List<EvoLine> Evos = new();
}

internal sealed class OwnedPair
{
    public Item Result;
    public List<Item> Parts = new();   // every ingredient, all owned
}

internal static class Evo
{
    public const int EvoLevel = 3;        // every ingredient must reach this level

    public static readonly Offer[] Offers = new Offer[8];
    public static readonly List<OwnedPair> Pairs = new();
    public static int Hovered = -1;       // offer index under the cursor / selected
    public static int Version;            // bumped whenever the data changes

    static readonly Dictionary<string, int> Owned = new();            // slug -> displayed level
    static readonly Dictionary<string, UpgradeInfo> OwnedInfos = new();
    static readonly Dictionary<string, bool> DiscoveredCache = new();
    static bool _discoveryLogged;

    public static bool HasOffers => Offers.Any(o => o != null);

    public static Offer Current =>
        Hovered >= 0 && Hovered < Offers.Length && Offers[Hovered] != null
            ? Offers[Hovered]
            : Offers.FirstOrDefault(o => o != null);

    public static void Clear()
    {
        for (int i = 0; i < Offers.Length; i++) Offers[i] = null;
        Pairs.Clear();
        Hovered = -1;
        Version++;
    }

    // "In the encyclopedia" = obtained at least once, per the save's meta stats.
    public static bool IsDiscovered(UpgradeInfo inf)
    {
        if (inf == null) return true;
        if (DiscoveredCache.TryGetValue(inf.Slug, out var d)) return d;
        bool result = true;
        try
        {
            var meta = SaveMgr.I?.MetaSave;
            if (meta != null)
            {
                int idx = inf.GetIdx();
                var hero = inf.TryCast<HeroInfo>();
                if (hero != null && meta.HeroStats != null && idx >= 0 && idx < meta.HeroStats.Length)
                    result = meta.HeroStats[idx] != null && meta.HeroStats[idx].NumObtained > 0;
                var passive = inf.TryCast<PassiveInfo>();
                if (passive != null && meta.PassiveStats != null && idx >= 0 && idx < meta.PassiveStats.Length)
                    result = meta.PassiveStats[idx] != null && meta.PassiveStats[idx].NumObtained > 0;
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning("Encyclopedia check failed: " + e.Message); }
        DiscoveredCache[inf.Slug] = result;
        return result;
    }

    public static Item MakeItem(UpgradeInfo inf) => new Item
    {
        Info = inf,
        Name = inf.Name,
        Lvl = Owned.TryGetValue(inf.Slug, out var l) ? l : 0,
        Discovered = IsDiscovered(inf),
        IsBall = inf.TryCast<HeroInfo>() != null,
    };

    public static void RefreshOwned()
    {
        Owned.Clear();
        OwnedInfos.Clear();
        DiscoveredCache.Clear();
        var mgr = UpgradeMgr.I;
        var db = InfoDB.I;
        if (mgr == null || db == null) return;

        foreach (var h in db.Heroes)
        {
            var inst = mgr.GetHero(h.Type);
            if (inst == null) continue;
            Owned[h.Slug] = inst.Lvl + 1;
            OwnedInfos[h.Slug] = h;
        }
        foreach (var p in db.Passives)
        {
            var inst = mgr.GetPassive(p.Type);
            if (inst == null) continue;
            Owned[p.Slug] = inst.Lvl + 1;
            OwnedInfos[p.Slug] = p;
        }
        RebuildPairs();
        LogDiscoveryOnce(db);
    }

    // One-time log of what the mod thinks is missing from the encyclopedia, to check against the game.
    static void LogDiscoveryOnce(InfoDB db)
    {
        if (_discoveryLogged) return;
        _discoveryLogged = true;
        var missingBalls = new List<string>();
        foreach (var h in db.Heroes) if (h.IsInGame && !IsDiscovered(h)) missingBalls.Add(h.Name);
        var missingPassives = new List<string>();
        foreach (var p in db.Passives) if (p.IsInGame && !IsDiscovered(p)) missingPassives.Add(p.Name);
        Plugin.Log.LogInfo($"Not in encyclopedia: {missingBalls.Count} balls: {string.Join(", ", missingBalls)}");
        Plugin.Log.LogInfo($"Not in encyclopedia: {missingPassives.Count} passives: {string.Join(", ", missingPassives)}");
    }

    // All evolutions of `inf`, one line per recipe that uses it, with the other ingredients.
    public static List<EvoLine> EvosFor(UpgradeInfo inf)
    {
        var lines = new List<EvoLine>();
        if (inf?.Evolutions == null) return lines;

        foreach (var e in inf.Evolutions)
        {
            var sets = e.MergeComponents;
            if (sets == null) continue;
            foreach (var set in sets)
            {
                if (set == null) continue;
                bool usesThis = false;
                var partners = new List<Item>();
                foreach (var c in set)
                {
                    if (c == null) continue;
                    if (c.Slug == inf.Slug) { usesThis = true; continue; }
                    partners.Add(MakeItem(c));
                }
                if (!usesThis || partners.Count == 0) continue;

                int have = partners.Count(p => p.Owned);
                lines.Add(new EvoLine
                {
                    Result = MakeItem(e),
                    Partners = partners,
                    Status = have == partners.Count ? EvoStatus.HaveAll
                           : have > 0 ? EvoStatus.HaveSome : EvoStatus.HaveNone,
                });
            }
        }
        return lines.OrderBy(l => (int)l.Status).ToList();
    }

    public static void SetOffer(int idx, string kind, LevelUpBtn btn)
    {
        if (idx < 0 || idx >= Offers.Length) return;
        if (idx == 0) { Clear(); RefreshOwned(); }   // first slot = new batch (open or re-roll)

        var inf = btn.TgtInfo;
        Offers[idx] = inf == null ? null : new Offer
        {
            Kind = kind,
            Self = MakeItem(inf),
            IsNew = btn.IsNew,
            Evos = EvosFor(inf),
        };
        Version++;
    }

    public static void SetHovered(int idx)
    {
        if (Hovered == idx) return;
        Hovered = idx;
        Version++;
    }

    // Evolutions you can already make from what you own (all ingredients owned).
    static void RebuildPairs()
    {
        Pairs.Clear();
        var seen = new HashSet<string>();
        foreach (var inf in OwnedInfos.Values)
        {
            if (inf.Evolutions == null) continue;
            foreach (var e in inf.Evolutions)
            {
                if (e.MergeComponents == null) continue;
                foreach (var set in e.MergeComponents)
                {
                    if (set == null) continue;
                    var parts = new List<Item>();
                    bool all = true, usesThis = false;
                    foreach (var c in set)
                    {
                        if (c == null) continue;
                        if (c.Slug == inf.Slug) usesThis = true;
                        if (!Owned.ContainsKey(c.Slug)) { all = false; break; }
                        parts.Add(MakeItem(c));
                    }
                    if (!all || !usesThis) continue;
                    var key = e.Name + "|" + string.Join("+", parts.Select(p => p.Name).OrderBy(n => n));
                    if (seen.Add(key)) Pairs.Add(new OwnedPair { Result = MakeItem(e), Parts = parts });
                }
            }
        }
    }
}

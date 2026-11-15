using System.Collections.Generic;
using System.Linq;

namespace EvoHelper;

// Plain C# snapshots of game data, built when the pick screen is filled in,
// so the overlay never touches game objects while drawing.

internal enum EvoStatus { HaveAll = 0, HaveSome = 1, HaveNone = 2 }

internal sealed class Partner
{
    public string Name;
    public int Lvl;          // displayed level (1-based); 0 = not owned
    public bool Owned => Lvl > 0;
}

internal sealed class EvoLine
{
    public string Result;
    public List<Partner> Partners = new();
    public EvoStatus Status;
}

internal sealed class Offer
{
    public string Kind;      // ball / passive / upgrade
    public string Name;
    public bool IsNew;
    public int OwnedLvl;     // displayed level if already owned, else 0
    public List<EvoLine> Evos = new();
}

internal sealed class OwnedPair
{
    public string Result;
    public List<Partner> Parts = new();   // every ingredient, all owned
}

internal static class Evo
{
    public const int EvoLevel = 3;        // every ingredient must reach this level

    public static readonly Offer[] Offers = new Offer[8];
    public static readonly List<OwnedPair> Pairs = new();

    // slug -> displayed level, for owned balls and passives
    static readonly Dictionary<string, int> Owned = new();
    static readonly Dictionary<string, UpgradeInfo> OwnedInfos = new();

    public static bool HasOffers => Offers.Any(o => o != null);

    public static void Clear()
    {
        for (int i = 0; i < Offers.Length; i++) Offers[i] = null;
        Pairs.Clear();
    }

    public static void RefreshOwned()
    {
        Owned.Clear();
        OwnedInfos.Clear();
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
    }

    static int LvlOf(UpgradeInfo inf) => Owned.TryGetValue(inf.Slug, out var l) ? l : 0;

    // All evolutions of `inf`, one line per recipe that uses it, with the other ingredients.
    static List<EvoLine> EvosFor(UpgradeInfo inf)
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
                var partners = new List<Partner>();
                foreach (var c in set)
                {
                    if (c == null) continue;
                    if (c.Slug == inf.Slug) { usesThis = true; continue; }
                    partners.Add(new Partner { Name = c.Name, Lvl = LvlOf(c) });
                }
                if (!usesThis || partners.Count == 0) continue;

                int have = partners.Count(p => p.Owned);
                lines.Add(new EvoLine
                {
                    Result = e.Name,
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
        if (inf == null) { Offers[idx] = null; return; }
        Offers[idx] = new Offer
        {
            Kind = kind,
            Name = inf.Name,
            IsNew = btn.IsNew,
            OwnedLvl = LvlOf(inf),
            Evos = EvosFor(inf),
        };
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
                    var parts = new List<Partner>();
                    bool all = true, usesThis = false;
                    foreach (var c in set)
                    {
                        if (c == null) continue;
                        if (c.Slug == inf.Slug) usesThis = true;
                        int l = LvlOf(c);
                        if (l == 0) { all = false; break; }
                        parts.Add(new Partner { Name = c.Name, Lvl = l });
                    }
                    if (!all || !usesThis) continue;
                    var key = e.Name + "|" + string.Join("+", parts.Select(p => p.Name).OrderBy(n => n));
                    if (seen.Add(key)) Pairs.Add(new OwnedPair { Result = e.Name, Parts = parts });
                }
            }
        }
    }
}

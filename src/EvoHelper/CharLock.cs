using System;
using System.Collections.Generic;
using System.Linq;

namespace EvoHelper;

// Character-specific passives: which characters can roll them, per the game's own
// PassiveInfo.CanBeUsed(CharType), and whether one of them is in the current run.
internal static class CharLock
{
    const int NumChars = 31;          // CharType.kNum; the game's IsCharSpecific loops the same range
    public const int MaxShown = 2;    // more allowed characters than this: treat as not character-specific

    static readonly Dictionary<string, List<CharInfo>> Cache = new();
    static bool _logged;

    // The characters this item is limited to, or null if anyone can get it.
    public static List<CharInfo> For(UpgradeInfo inf)
    {
        if (inf == null) return null;
        if (Cache.TryGetValue(inf.Slug, out var hit)) return hit;
        List<CharInfo> result = null;
        try
        {
            var p = inf.TryCast<PassiveInfo>();
            if (p != null && p.IsCharSpecific())
            {
                var allowed = new List<CharType>();
                for (int c = 0; c < NumChars; c++)
                    if (p.CanBeUsed((CharType)c)) allowed.Add((CharType)c);
                if (allowed.Count > 0 && allowed.Count <= MaxShown)
                    result = allowed.Select(Info).Where(ci => ci != null).ToList();
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning("Character check failed for " + inf.Name + ": " + e.Message); }
        Cache[inf.Slug] = result;
        return result;
    }

    // True if one of these characters is playing this run (Matchmaker pairs included).
    public static bool InRun(List<CharInfo> chars)
    {
        try
        {
            var run = CharBattleInst.I;
            if (run == null) return false;
            foreach (var c in chars) if (run.HasType(c.Type)) return true;
        }
        catch { }
        return false;
    }

    static CharInfo Info(CharType t)
    {
        var db = InfoDB.I;
        if (db?.Chars == null) return null;
        foreach (var ci in db.Chars) if (ci != null && ci.Type == t) return ci;
        return null;
    }

    // One-off log of every character-specific passive, to check the data.
    public static void LogAll()
    {
        if (_logged) return;
        var db = InfoDB.I;
        if (db?.Passives == null || db.Chars == null) return;
        _logged = true;
        try
        {
            int n = 0;
            foreach (var p in db.Passives)
            {
                if (p == null || !p.IsCharSpecific()) continue;
                var allowed = new List<string>();
                for (int c = 0; c < NumChars; c++)
                    if (p.CanBeUsed((CharType)c)) allowed.Add(Info((CharType)c)?.Name ?? ((CharType)c).ToString());
                Plugin.Log.LogInfo($"Character-specific: {p.Name} -> {(allowed.Count <= 6 ? string.Join(", ", allowed) : allowed.Count + " characters")}");
                n++;
            }
            Plugin.Log.LogInfo($"{n} character-specific passives.");
        }
        catch (Exception e) { Plugin.Log.LogWarning("Character list failed: " + e.Message); }
    }
}

using System;
using System.Collections.Generic;
using I2.Loc;
using UnityEngine;

namespace EvoHelper;

// What a ball/passive can evolve into next, e.g. Wraith -> Banshee with Phantom.
internal static class Further
{
    static readonly Dictionary<string, List<(UpgradeInfo result, List<UpgradeInfo> partners)>> Cache = new();

    public static List<(UpgradeInfo result, List<UpgradeInfo> partners)> For(UpgradeInfo inf)
    {
        if (inf == null) return new();
        if (Cache.TryGetValue(inf.Slug, out var hit)) return hit;

        var list = new List<(UpgradeInfo result, List<UpgradeInfo> partners)>();
        if (inf.Evolutions != null)
        {
            foreach (var e in inf.Evolutions)
            {
                if (e?.MergeComponents == null) continue;
                foreach (var set in e.MergeComponents)
                {
                    if (set == null) continue;
                    bool usesThis = false;
                    var others = new List<UpgradeInfo>();
                    foreach (var c in set)
                    {
                        if (c == null) continue;
                        if (c.Slug == inf.Slug) usesThis = true; else others.Add(c);
                    }
                    if (usesThis && others.Count > 0) list.Add((e, others));
                }
            }
        }
        Cache[inf.Slug] = list;
        return list;
    }

    static readonly Dictionary<string, List<List<UpgradeInfo>>> MadeCache = new();

    // The ingredient sets that make `inf` (empty for a base item).
    public static List<List<UpgradeInfo>> MadeFrom(UpgradeInfo inf)
    {
        if (inf == null) return new();
        if (MadeCache.TryGetValue(inf.Slug, out var hit)) return hit;
        var list = new List<List<UpgradeInfo>>();
        if (inf.MergeComponents != null)
            foreach (var set in inf.MergeComponents)
            {
                if (set == null) continue;
                var parts = new List<UpgradeInfo>();
                foreach (var c in set) if (c != null) parts.Add(c);
                if (parts.Count > 0) list.Add(parts);
            }
        MadeCache[inf.Slug] = list;
        return list;
    }
}

// The game's own description text for an upgrade, with its numbers filled in the same
// way the game's tooltip does it (ApplyUpgradeParams into a LocalizationParamsManager).
internal static class Describe
{
    static readonly Dictionary<string, string> Cache = new();
    static GameObject _paramsGo;
    static LocalizationParamsManager _params;
    static bool _warned;

    public static string Text(UpgradeInfo inf, int lvlIdx)
    {
        if (inf == null) return "";
        string key = inf.Slug + "|" + lvlIdx;
        if (Cache.TryGetValue(key, out var hit)) return hit;

        string result = "";
        try
        {
            if (_paramsGo == null)
            {
                _paramsGo = new GameObject("EvoHelperParams");
                UnityEngine.Object.DontDestroyOnLoad(_paramsGo);
                _paramsGo.hideFlags = HideFlags.HideAndDontSave;
                _params = _paramsGo.AddComponent<LocalizationParamsManager>();
            }
            inf.ApplyUpgradeParams(lvlIdx, _params, false);
            result = LocalizationManager.GetTranslation(inf.GetDescSlug(), true, 0, true, true, _paramsGo, null, true);
        }
        catch (Exception e)
        {
            if (!_warned) { _warned = true; Plugin.Log.LogWarning("Description lookup failed, using raw text: " + e.Message); }
        }
        if (string.IsNullOrEmpty(result)) result = inf.Desc ?? "";
        Cache[key] = result;
        return result;
    }
}

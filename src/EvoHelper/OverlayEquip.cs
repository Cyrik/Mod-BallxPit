using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EvoHelper;

// Adds the evolutions list to the game's equipment popup: the "Details" view on the
// level-up screen and hovering your balls/passives on the in-run HUD. Display only;
// the popup closes when the mouse leaves the item, so its icons are not hoverable.
public partial class Overlay
{
    // Filled by the EquipmentInfoPanel patches: which panel shows which item(s).
    internal sealed class EquipTarget
    {
        public IntPtr Key;
        public EquipmentInfoPanel Panel;
        public List<UpgradeInfo> Infos = new();
        public int Version;
        public RectTransform Block;
        public LayoutElement Layout;
        public bool InLayout;
        public bool SkipLogged;
        public int BuiltVersion = -1;
        public float BuiltWidth = -1;
    }
    internal static readonly Dictionary<IntPtr, EquipTarget> EquipTargets = new();
    static bool _equipLayoutLogged;

    internal static void SetEquip(EquipmentInfoPanel panel, IEnumerable<UpgradeInfo> infos)
    {
        if (panel == null) return;
        if (!EquipTargets.TryGetValue(panel.Pointer, out var t))
            EquipTargets[panel.Pointer] = t = new EquipTarget { Key = panel.Pointer, Panel = panel };
        t.Infos = infos.Where(i => i != null).GroupBy(i => i.Slug).Select(g => g.First()).ToList();
        t.Version++;
    }

    void UpdateEquipBlocks()
    {
        if (_hidden) { foreach (var t in EquipTargets.Values) if (t.Block != null) t.Block.gameObject.SetActive(false); return; }
        foreach (var t in EquipTargets.Values.ToList())
        {
            if (t.Panel == null) { EquipTargets.Remove(t.Key); continue; }
            if (!t.Panel.gameObject.activeInHierarchy) continue;
            try { UpdateEquip(t); }
            catch (Exception e)
            {
                if (!_equipErrorLogged) { _equipErrorLogged = true; Plugin.Log.LogError("Equipment popup block failed: " + e); }
            }
        }
    }
    bool _equipErrorLogged;

    [HideFromIl2Cpp]
    void UpdateEquip(EquipTarget t)
    {
        var wrapper = t.Panel.Wrapper != null ? t.Panel.Wrapper : t.Panel.transform.Cast<RectTransform>();
        // Only popups that stack their contents (Details view, HUD hover). The fusion /
        // evolution result screen also uses this panel, inside a fixed-size scroll view with
        // no layout group; the block would spill over the screen there, so skip it.
        if (wrapper.GetComponent<VerticalLayoutGroup>() == null)
        {
            if (t.Block != null) t.Block.gameObject.SetActive(false);
            if (!t.SkipLogged) { t.SkipLogged = true; Plugin.Log.LogInfo($"Equipment popup '{wrapper.name}' has no layout group; no evolutions block there."); }
            return;
        }
        if (t.Block == null)
        {
            if (!_equipLayoutLogged) { _equipLayoutLogged = true; LayoutLog.Dump(t.Panel.transform); }
            EnsureFonts(t.Panel.transform);
            t.Block = NewRect("EvoHelperEquipBlock", wrapper);
            t.InLayout = true;
            t.Layout = t.Block.gameObject.AddComponent<LayoutElement>();
            t.Layout.flexibleWidth = 1f;
            t.BuiltVersion = -1;
            Plugin.Log.LogInfo($"Equipment popup block attached to '{wrapper.name}'.");
        }
        if (!t.Block.gameObject.activeSelf) t.Block.gameObject.SetActive(true);
        if (t.InLayout && t.Block.GetSiblingIndex() != wrapper.childCount - 1) t.Block.SetAsLastSibling();

        float width = t.Block.rect.width;
        if (width < 10f) width = wrapper.rect.width * 0.88f;
        if (width < 10f) return;
        if (t.BuiltVersion == t.Version && Mathf.Abs(width - t.BuiltWidth) <= 1f) return;
        t.BuiltVersion = t.Version;
        t.BuiltWidth = width;
        BuildEquip(t, width);
        LayoutRebuilder.ForceRebuildLayoutImmediate(wrapper);
    }

    [HideFromIl2Cpp]
    void BuildEquip(EquipTarget t, float width)
    {
        var root = t.Block;
        for (int i = root.childCount - 1; i >= 0; i--) Destroy(root.GetChild(i).gameObject);

        Evo.RefreshOwned();   // levels can change mid-run
        var sink = new List<(RectTransform rt, Item item)>();
        bool anyUndiscovered = false;
        float y = Gap;
        bool many = t.Infos.Count > 1;

        foreach (var inf in t.Infos)
        {
            var lines = Evo.EvosFor(inf);
            string title = many ? $"{inf.Name}: evolutions" : "Evolutions";
            Text(root, title, 0, y, width, _hdrSize, _hdrColor, TextAlignmentOptions.Center, _hdrFont, _hdrMat);
            y += _hdrSize * 1.8f;
            if (lines.Count == 0)
            {
                Text(root, "None", 0, y, width, _hdrSize, Grey, TextAlignmentOptions.Center, _hdrFont, _hdrMat);
                y += _hdrSize * 1.6f;
                continue;
            }
            var chips = lines.Select(e => (parts: e.Partners, result: e.Result,
                bg: e.Status == EvoStatus.HaveAll ? ChipBgReady : ChipBg, showLvlOf3: false)).ToList();
            y = FlowChips(root, sink, chips, 0, y, width, ref anyUndiscovered);
        }

        if (anyUndiscovered)
        {
            y += Gap * 0.5f;
            var sw = NewRect("LegendSwatch", root);
            Place(sw, 0, y + 1, _hdrSize, _hdrSize);
            Frame(sw, Gold);
            Text(root, "not in your encyclopedia yet", _hdrSize * 1.6f, y, width, _hdrSize, Grey, font: _hdrFont, mat: _hdrMat);
            y += _hdrSize * 1.6f;
        }
        y += Gap;
        if (t.InLayout) { t.Layout.preferredHeight = y; t.Layout.minHeight = y; }
        root.sizeDelta = new Vector2(root.sizeDelta.x, y);
    }

    // Fonts for when the equipment popup shows before the level-up screen has ever opened.
    void EnsureFonts(Transform searchRoot)
    {
        if (_font != null) return;
        var src = searchRoot.GetComponentsInChildren<TextMeshProUGUI>(true)
            .OrderBy(x => Mathf.Abs(x.fontSize - 16f)).FirstOrDefault();
        if (src == null) return;
        _font = src.font; _fontMat = src.fontSharedMaterial;
        _fs = Mathf.Max(8f, src.fontSize); _textColor = src.color;
        _hdrFont = _font; _hdrMat = _fontMat; _hdrSize = Mathf.Round(_fs * 0.666f * 100f) / 100f;
        _glyphFont = _font; _glyphMat = _fontMat;
    }
}

internal static class EquipPatches
{
    static IEnumerable<UpgradeInfo> HeroInfos(HeroInst h)
    {
        var db = InfoDB.I;
        if (db == null || h == null) yield break;
        var types = new List<HeroType> { h.Type };
        if (h.CombinedHeroes != null)
            for (int i = 0; i < h.CombinedHeroes.Count; i++) types.Add(h.CombinedHeroes[i]);
        foreach (var ty in types)
            foreach (var inf in db.Heroes)
                if (inf.Type == ty) { yield return inf; break; }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EquipmentInfoPanel), nameof(EquipmentInfoPanel.SetHero))]
    static void OnSetHero(EquipmentInfoPanel __instance, HeroInst h)
    {
        try { Overlay.SetEquip(__instance, HeroInfos(h).ToList()); }
        catch (Exception e) { Plugin.Log.LogError(e); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EquipmentInfoPanel), nameof(EquipmentInfoPanel.SetPassive))]
    static void OnSetPassive(EquipmentInfoPanel __instance, PassiveInst p)
    {
        try
        {
            var db = InfoDB.I;
            if (db == null || p == null) return;
            foreach (var inf in db.Passives)
                if (inf.Type == p.Type) { Overlay.SetEquip(__instance, new[] { (UpgradeInfo)inf }); return; }
        }
        catch (Exception e) { Plugin.Log.LogError(e); }
    }
}

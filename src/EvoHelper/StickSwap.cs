using System;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EvoHelper;

// Optional "Swap sticks" settings for controllers, one for the village, one for fights.
//
// The game reads every stick through InputMgr.GetAxis: the village reads the raw sticks
// (right stick = camera, left stick = cursor), runs and harvesting read the Move / Aim
// actions. While one of those gameplay updates runs, this swaps left and right. Menus
// (pause, encyclopedia, level-up scrolling) are left alone.
//
// The toggles are extra rows at the bottom of Options > General, saved in the mod's own
// config file (BepInEx\config\schuah.ballxpit.evohelper.cfg), never in the game's settings.
internal static class StickSwap
{
    internal enum Where { Village = 0, Fights = 1 }
    static readonly string[] Labels = { "Village: camera on left stick, cursor on right", "Fights: aim with left stick, move with right" };
    static readonly ConfigEntry<bool>[] _swap = new ConfigEntry<bool>[2];

    public static bool On(Where w) => _swap[(int)w] != null && _swap[(int)w].Value;

    public static void Init(ConfigFile cfg)
    {
        _swap[0] = cfg.Bind("Controller", "SwapSticksVillage", false,
            "Village: left stick moves the camera, right stick the cursor (also in the game's Options > General).");
        _swap[1] = cfg.Bind("Controller", "SwapSticksFights", false,
            "Fights: left stick aims, right stick moves (also in the game's Options > General).");
    }

    public static void Toggle(Where w)
    {
        var e = _swap[(int)w];
        if (e == null) return;
        e.Value = !e.Value;
        Plugin.Log.LogInfo($"{Labels[(int)w]}: {(e.Value ? "on" : "off")}");
    }

    // ---------- input ----------

    internal static readonly int[] Scope = new int[2];   // > 0 while a village / fight update that reads the sticks runs
    static bool _inSwap;

    static bool Active
    {
        get
        {
            if (_inSwap) return false;
            bool any = (Scope[0] > 0 && On(Where.Village)) || (Scope[1] > 0 && On(Where.Fights));
            if (!any) return false;
            var im = InputMgr.I;
            return im != null && im.LastControlType == InputType.kController;
        }
    }

    internal static void SwapBtn(InputMgr im, ControllerBtn t, ref float result)
    {
        if (!Active) return;
        ControllerBtn other;
        switch (t)
        {
            case ControllerBtn.kLeftStickHorizontal: other = ControllerBtn.kRightStickHorizontal; break;
            case ControllerBtn.kLeftStickVertical: other = ControllerBtn.kRightStickVertical; break;
            case ControllerBtn.kRightStickHorizontal: other = ControllerBtn.kLeftStickHorizontal; break;
            case ControllerBtn.kRightStickVertical: other = ControllerBtn.kLeftStickVertical; break;
            default: return;
        }
        _inSwap = true;
        try { result = im.GetAxis(other); }
        finally { _inSwap = false; }
    }

    internal static void SwapAction(InputMgr im, GameActionType t, ref float result)
    {
        if (!Active) return;
        GameActionType other;
        switch (t)
        {
            case GameActionType.kMoveHorizontal: other = GameActionType.kAimHorizontal; break;
            case GameActionType.kMoveVertical: other = GameActionType.kAimVertical; break;
            case GameActionType.kAimHorizontal: other = GameActionType.kMoveHorizontal; break;
            case GameActionType.kAimVertical: other = GameActionType.kMoveVertical; break;
            default: return;
        }
        _inSwap = true;
        try { result = im.GetAxis(other); }
        finally { _inSwap = false; }
    }

    // ---------- Options > General row ----------

    const string RowName = "EvoHelperSwapSticks";
    static readonly GameObject[] _rows = new GameObject[2];
    static bool _logged;

    internal static bool IsOurs(SettingsItem item) =>
        item != null && item.gameObject != null && item.gameObject.name.StartsWith(RowName);

    internal static Where WhereOf(SettingsItem item) =>
        item.gameObject.name.EndsWith(nameof(Where.Fights)) ? Where.Fights : Where.Village;

    internal static void OnFilterSet(SettingsScreen scr, SettingsFilter filt)
    {
        for (int i = 0; i < _rows.Length; i++)
            if (_rows[i] != null) { UnityEngine.Object.Destroy(_rows[i]); _rows[i] = null; }
        if (filt != SettingsFilter.kGeneral || scr == null) return;

        try
        {
            // Copy one of the game's own toggle rows on this page.
            SettingsItem src = null, prev = null;
            var items = scr.GetComponentsInChildren<SettingsItem>(false);
            foreach (var it in items)
            {
                if (IsOurs(it)) continue;
                if (src == null && it.DisplayMode == OptionDisplayMode.kToggle) src = it;
            }
            if (src == null) { Plugin.Log.LogInfo("Swap sticks row: no toggle row on the General page to copy."); return; }

            var parent = src.transform.parent;
            SettingsItem last = null;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (!c.gameObject.activeSelf) continue;
                var si = c.GetComponent<SettingsItem>();
                if (si == null || IsOurs(si)) continue;
                prev = last;
                last = si;
            }

            bool layout = parent.GetComponent<LayoutGroup>() != null;
            var step = last != null && prev != null ? last.transform.localPosition - prev.transform.localPosition : new Vector3(0, -60, 0);
            for (int i = 0; i < _rows.Length; i++)
            {
                var row = UnityEngine.Object.Instantiate(src.gameObject, parent, false);
                row.name = RowName + ((Where)i).ToString();
                _rows[i] = row;
                var item = row.GetComponent<SettingsItem>();
                if (item.LocLabel != null) item.LocLabel.enabled = false;
                row.transform.SetAsLastSibling();

                // Without a layout group the game places rows by hand: put ours below the last row.
                if (!layout && last != null)
                {
                    row.transform.localPosition = last.transform.localPosition + step * (i + 1);
                    GrowContent(parent as RectTransform, Mathf.Abs(step.y));
                }
                Refresh(item);
            }
            if (!_logged) { _logged = true; Plugin.Log.LogInfo($"Swap sticks rows added under '{parent.name}' (layout group: {layout}), copied from {src.TgtOption}."); }
        }
        catch (Exception e) { Plugin.Log.LogWarning("Swap sticks row failed: " + e.Message); }
    }

    static void GrowContent(RectTransform content, float by)
    {
        if (content == null || by <= 0) return;
        var sr = content.GetComponentInParent<ScrollRect>();
        if (sr != null && sr.content == content)
            content.sizeDelta = new Vector2(content.sizeDelta.x, content.sizeDelta.y + by);
    }

    internal static void Refresh(SettingsItem item)
    {
        if (item.WrapperToggle != null) item.WrapperToggle.SetActive(true);
        if (item.WrapperList != null) item.WrapperList.SetActive(false);
        if (item.WrapperSlider != null) item.WrapperSlider.SetActive(false);
        var w = WhereOf(item);
        if (item.ImgToggleOn != null) item.ImgToggleOn.gameObject.SetActive(On(w));
        if (item.TxtLabel != null) item.TxtLabel.text = Labels[(int)w];
    }
}

// Scopes: the gameplay updates that read the sticks.
[HarmonyPatch]
internal static class StickSwapPatches
{
    static void V() => StickSwap.Scope[0]++;
    static void VEnd() { if (StickSwap.Scope[0] > 0) StickSwap.Scope[0]--; }
    static void F() => StickSwap.Scope[1]++;
    static void FEnd() { if (StickSwap.Scope[1] > 0) StickSwap.Scope[1]--; }

    // Village: camera, cursor and the village character.
    [HarmonyPrefix, HarmonyPatch(typeof(BaseGridMgr), "UpdateControllerControls")] static void A1() => V();
    [HarmonyPostfix, HarmonyPatch(typeof(BaseGridMgr), "UpdateControllerControls")] static void A2() => VEnd();
    [HarmonyPrefix, HarmonyPatch(typeof(BaseGridMgr), "MyUpdate")] static void B1() => V();
    [HarmonyPostfix, HarmonyPatch(typeof(BaseGridMgr), "MyUpdate")] static void B2() => VEnd();
    [HarmonyPrefix, HarmonyPatch(typeof(ControllerCursorUI), "MyUpdate")] static void C1() => V();
    [HarmonyPostfix, HarmonyPatch(typeof(ControllerCursorUI), "MyUpdate")] static void C2() => VEnd();
    [HarmonyPrefix, HarmonyPatch(typeof(BasePlayer), "MyUpdate")] static void D1() => V();
    [HarmonyPostfix, HarmonyPatch(typeof(BasePlayer), "MyUpdate")] static void D2() => VEnd();

    // Fights: moving and aiming in a run.
    [HarmonyPrefix, HarmonyPatch(typeof(Player), "MyUpdate")] static void E1() => F();
    [HarmonyPostfix, HarmonyPatch(typeof(Player), "MyUpdate")] static void E2() => FEnd();
    [HarmonyPrefix, HarmonyPatch(typeof(Player), "MyUpdateMoonComplete")] static void F1() => F();
    [HarmonyPostfix, HarmonyPatch(typeof(Player), "MyUpdateMoonComplete")] static void F2() => FEnd();
    [HarmonyPrefix, HarmonyPatch(typeof(Player), "UpdateAim")] static void G1() => F();
    [HarmonyPostfix, HarmonyPatch(typeof(Player), "UpdateAim")] static void G2() => FEnd();

    [HarmonyPostfix, HarmonyPatch(typeof(InputMgr), nameof(InputMgr.GetAxis), new[] { typeof(ControllerBtn) })]
    static void AxisBtn(InputMgr __instance, ControllerBtn t, ref float __result) => StickSwap.SwapBtn(__instance, t, ref __result);

    [HarmonyPostfix, HarmonyPatch(typeof(InputMgr), nameof(InputMgr.GetAxis), new[] { typeof(GameActionType) })]
    static void AxisAction(InputMgr __instance, GameActionType t, ref float __result) => StickSwap.SwapAction(__instance, t, ref __result);
}

// The extra row in Options > General.
[HarmonyPatch]
internal static class StickSwapMenuPatches
{
    [HarmonyPostfix, HarmonyPatch(typeof(SettingsScreen), nameof(SettingsScreen.SetFilter))]
    static void SetFilter(SettingsScreen __instance, SettingsFilter filt) => StickSwap.OnFilterSet(__instance, filt);

    static bool Click(SettingsItem item)
    {
        if (!StickSwap.IsOurs(item)) return true;
        StickSwap.Toggle(StickSwap.WhereOf(item));
        StickSwap.Refresh(item);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SettingsItem), "OnClicked")]
    static bool OnClicked(SettingsItem __instance) => Click(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(SettingsItem), nameof(SettingsItem.OnLeftClicked))]
    static bool OnLeft(SettingsItem __instance) => Click(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(SettingsItem), nameof(SettingsItem.OnRightClicked))]
    static bool OnRight(SettingsItem __instance) => Click(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(SettingsItem), nameof(SettingsItem.RefreshValues))]
    static bool RefreshValues(SettingsItem __instance)
    {
        if (!StickSwap.IsOurs(__instance)) return true;
        StickSwap.Refresh(__instance);
        return false;
    }

    // The game's hover text would describe the row we copied; show none for ours.
    [HarmonyPrefix, HarmonyPatch(typeof(SettingsItem), "OnHover")]
    static bool OnHover(SettingsItem __instance) => !StickSwap.IsOurs(__instance);
}

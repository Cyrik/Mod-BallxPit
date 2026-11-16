using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.EventSystems;

namespace EvoHelper;

// Controller support for the evolutions block: "inspect mode".
//
//  R3            enter / leave inspect mode (a frame appears on the first icon)
//  D-pad/L-stick move between icons; the selected icon's tooltip opens at once
//  A             go into that tooltip's icons
//  B             back out one level (leaves inspect mode at the top)
//  LT / RT held  show descriptions (same as Shift)
//  R-stick       scroll the panel (always, on a controller)
//
// While inspecting, the game's own UI navigation is paused and InputMgr reports the pad's
// buttons as not pressed, so the pick screen does not move or pick underneath.
public partial class Overlay
{
    internal static bool Inspecting;      // read by the InputMgr patches
    internal static bool PadReading;      // our own InputMgr queries bypass the block

    bool _padDesc, _kbShift, _builtPad, _builtInspect, _prevNavEvents;
    int _selLevel = -1, _selIdx;           // -1 = the block's icons, n = icons inside tooltip n
    RectTransform _cursor, _cursorOn;
    Vector2Int _heldDir;
    float _nextRepeat;
    const float RepeatDelay = 0.35f, RepeatRate = 0.12f;

    static bool PadActive
    {
        get
        {
            var im = InputMgr.I;
            return im != null && im.LastControlType == InputType.kController;
        }
    }

    // ---------- input ----------

    void UpdatePad(LevelUpUI ui)
    {
        var im = InputMgr.I;
        if (im == null || !PadActive) { if (Inspecting) ExitInspect(); _padDesc = false; return; }

        PadReading = true;
        try
        {
            _padDesc = Inspecting && (im.IsBtnHeld(ControllerBtn.kLeftTrigger) || im.IsBtnHeld(ControllerBtn.kRightTrigger));

            float ry = im.GetAxis(ControllerBtn.kRightStickVertical);
            if (Mathf.Abs(ry) > 0.2f) ScrollByRows(ry * Time.unscaledDeltaTime * 8f);

            if (im.IsBtnDown(ControllerBtn.kR3))
            {
                if (Inspecting) ExitInspect(); else EnterInspect();
                return;
            }
            if (!Inspecting) return;

            if (im.IsBtnDown(ControllerBtn.kB)) { Back(); return; }
            if (im.IsBtnDown(ControllerBtn.kA)) { Into(); return; }

            var dir = HeldDir(im);
            float now = Time.unscaledTime;
            if (dir != _heldDir)
            {
                _heldDir = dir;
                if (dir != Vector2Int.zero) { Move(dir); _nextRepeat = now + RepeatDelay; }
            }
            else if (dir != Vector2Int.zero && now >= _nextRepeat)
            {
                Move(dir);
                _nextRepeat = now + RepeatRate;
            }
        }
        finally { PadReading = false; }
    }

    static Vector2Int HeldDir(InputMgr im)
    {
        bool H(ControllerBtn a, ControllerBtn b) => im.IsBtnHeld(a) || im.IsBtnHeld(b);
        int x = (H(ControllerBtn.kNavRight, ControllerBtn.kDPadRight) ? 1 : 0) - (H(ControllerBtn.kNavLeft, ControllerBtn.kDPadLeft) ? 1 : 0);
        int y = (H(ControllerBtn.kNavUp, ControllerBtn.kDPadUp) ? 1 : 0) - (H(ControllerBtn.kNavDown, ControllerBtn.kDPadDown) ? 1 : 0);
        if (x != 0 && y != 0) y = 0;   // one axis at a time
        return new Vector2Int(x, y);
    }

    void EnterInspect()
    {
        if (_icons.Count == 0) return;
        Inspecting = true;
        _selLevel = -1;
        _selIdx = 0;
        _heldDir = Vector2Int.zero;
        ClosePopupsFrom(0);
        var es = EventSystem.current;
        if (es != null) { _prevNavEvents = es.sendNavigationEvents; es.sendNavigationEvents = false; }
        Plugin.Log.LogInfo("Controller inspect mode on.");
    }

    void ExitInspect()
    {
        Inspecting = false;
        _padDesc = false;
        ClosePopupsFrom(0);
        if (_cursor != null) { Destroy(_cursor.gameObject); _cursor = null; _cursorOn = null; }
        var es = EventSystem.current;
        if (es != null && _prevNavEvents) es.sendNavigationEvents = true;
        _prevNavEvents = false;
    }

    [HideFromIl2Cpp]
    List<(RectTransform rt, Item item)> LevelIcons(int level) =>
        level < 0 ? _icons : level < _popups.Count ? _popups[level].Icons : null;

    void Into()
    {
        int next = _selLevel + 1;
        var list = LevelIcons(next);
        if (list == null || list.Count == 0) return;
        _selLevel = next;
        _selIdx = 0;
    }

    void Back()
    {
        if (_selLevel < 0) { ExitInspect(); return; }
        // The tooltip we are in was opened by the selected icon one level down.
        var anchor = _popups[_selLevel].Anchor;
        ClosePopupsFrom(_selLevel + 1);
        _selLevel--;
        var list = LevelIcons(_selLevel);
        _selIdx = Math.Max(0, list?.FindIndex(e => e.rt == anchor) ?? 0);
    }

    // Spatial move: the nearest icon in that direction, preferring ones in line with this one.
    void Move(Vector2Int dir)
    {
        var list = LevelIcons(_selLevel);
        if (list == null || list.Count < 2 || _selIdx >= list.Count) return;
        Vector2 from = Centre(list[_selIdx].rt);
        Vector2 d = new(dir.x, dir.y);
        int best = -1;
        float bestScore = float.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            if (i == _selIdx || list[i].rt == null) continue;
            Vector2 v = Centre(list[i].rt) - from;
            float along = Vector2.Dot(v, d);
            if (along <= 1f) continue;
            float across = Mathf.Abs(v.x * d.y - v.y * d.x);
            float score = along + across * 2.5f;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        if (best >= 0) _selIdx = best;
    }

    Vector2 Centre(RectTransform rt)
    {
        var root = _root.GetComponentInParent<Canvas>()?.rootCanvas?.transform;
        var w = rt.TransformPoint(rt.rect.center);
        return root != null ? (Vector2)root.InverseTransformPoint(w) : (Vector2)w;
    }

    // ---------- tooltips driven by the selection ----------

    void UpdatePopupsPad()
    {
        var canvas = _root.GetComponentInParent<Canvas>();
        if (canvas == null) return;
        float now = Time.unscaledTime;

        for (int i = 0; i < _popups.Count; i++)
            if (_popups[i].Rt == null || _popups[i].Anchor == null) { ClosePopupsFrom(i); break; }
        if (_selLevel >= _popups.Count) _selLevel = _popups.Count - 1;
        foreach (var p in _popups) p.Touched = now;

        var list = LevelIcons(_selLevel);
        if (list == null || list.Count == 0) { if (_selLevel < 0) ExitInspect(); else Back(); return; }
        _selIdx = Mathf.Clamp(_selIdx, 0, list.Count - 1);
        var (selRt, selItem) = list[_selIdx];
        if (selRt == null) return;

        int child = _selLevel + 1;
        if (_popups.Count > child && _popups[child].Anchor == selRt)
        {
            if (_popups[child].Shift != _shift) RebuildPopup(child, canvas.rootCanvas.transform);
        }
        else
        {
            ClosePopupsFrom(child);
            OpenPopup(selRt, selItem, canvas.rootCanvas.transform, now);
        }

        PlaceCursor(selRt);
        if (_selLevel < 0) KeepVisible(selRt);
    }

    void PlaceCursor(RectTransform target)
    {
        if (_cursor == null)
        {
            _cursor = NewRect("EvoHelperPadCursor", target);
            Frame(_cursor, Color.white);
            _cursorOn = null;
        }
        if (_cursorOn != target)
        {
            _cursor.SetParent(target, false);
            _cursor.anchorMin = Vector2.zero; _cursor.anchorMax = Vector2.one;
            float o = Mathf.Max(2f, Mathf.Round(_fs * 0.2f));
            _cursor.offsetMin = new Vector2(-o, -o); _cursor.offsetMax = new Vector2(o, o);
            _cursor.SetAsLastSibling();
            _cursorOn = target;
        }
    }

    // ---------- scrolling ----------

    void ScrollByRows(float rows)
    {
        var scrl = LevelUpUI.I?.ScrlDetails;
        if (scrl == null || scrl.content == null || scrl.viewport == null) return;
        float overflow = scrl.content.rect.height - scrl.viewport.rect.height;
        if (overflow <= 1f) return;
        scrl.verticalNormalizedPosition = Mathf.Clamp01(scrl.verticalNormalizedPosition + rows * (Icon + Gap * 2) / overflow);
    }

    // Scrolls the panel so the selected block icon is inside the viewport.
    void KeepVisible(RectTransform rt)
    {
        var scrl = LevelUpUI.I?.ScrlDetails;
        if (scrl == null || scrl.content == null || scrl.viewport == null) return;
        float overflow = scrl.content.rect.height - scrl.viewport.rect.height;
        if (overflow <= 1f) return;
        var vp = scrl.viewport;
        var r = rt.rect;
        float top = vp.InverseTransformPoint(rt.TransformPoint(new Vector2(r.center.x, r.yMax))).y;
        float bottom = vp.InverseTransformPoint(rt.TransformPoint(new Vector2(r.center.x, r.yMin))).y;
        float margin = Gap * 2;
        float pos = scrl.verticalNormalizedPosition;
        if (top + margin > vp.rect.yMax) pos += (top + margin - vp.rect.yMax) / overflow;
        else if (bottom - margin < vp.rect.yMin) pos -= (vp.rect.yMin - (bottom - margin)) / overflow;
        scrl.verticalNormalizedPosition = Mathf.Clamp01(pos);
    }

    // ---------- hint line ----------

    static string PadHint(bool inspecting)
    {
        bool ps = InputMgr.I != null && (InputMgr.I.LastJoypadType == JoypadType.kPS4 || InputMgr.I.LastJoypadType == JoypadType.kPS5);
        bool sw = InputMgr.I != null && InputMgr.I.LastJoypadType == JoypadType.kSwitch;
        string a = ps ? "Cross" : sw ? "B" : "A";
        string b = ps ? "Circle" : sw ? "A" : "B";
        string trig = ps ? "L2/R2" : sw ? "ZL/ZR" : "LT/RT";
        string r3 = ps || sw ? "R3" : "RS";
        return inspecting
            ? $"{a} open   {b} back   hold {trig} description"
            : $"press {r3} to inspect icons";
    }
}

// While inspecting, the game sees the controller's buttons and sticks as idle.
internal static class PadPatches
{
    static bool Block => Overlay.Inspecting && !Overlay.PadReading;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InputMgr), nameof(InputMgr.IsBtnDown), new[] { typeof(ControllerBtn) })]
    static void BtnDown(ref bool __result) { if (Block) __result = false; }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InputMgr), nameof(InputMgr.IsBtnHeld), new[] { typeof(ControllerBtn) })]
    static void BtnHeld(ref bool __result) { if (Block) __result = false; }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InputMgr), nameof(InputMgr.IsBtnUp), new[] { typeof(ControllerBtn), typeof(bool) })]
    static void BtnUp(ref bool __result) { if (Block) __result = false; }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InputMgr), nameof(InputMgr.IsCancelBtnPressed))]
    static void Cancel(ref bool __result) { if (Block) __result = false; }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InputMgr), nameof(InputMgr.IsFaceBtnDown))]
    static void Face(ref bool __result) { if (Block) __result = false; }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InputMgr), nameof(InputMgr.GetAxis), new[] { typeof(ControllerBtn) })]
    static void Axis(ref float __result) { if (Block) __result = 0f; }
}

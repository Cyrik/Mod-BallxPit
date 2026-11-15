using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EvoHelper;

// In-run HUD additions, read-only:
//  - DPS readout at the top middle of the screen (damage your balls dealt over the
//    last few seconds of game time, from the game's own per-ball damage stats).
//  - "current / max" numbers on the health orb.
//  - "current / needed" numbers on the XP bar.
// F9 toggles all three.
public class Hud : MonoBehaviour
{
    public Hud(IntPtr ptr) : base(ptr) { }

    const float Window = 2f;          // seconds of game time the DPS is averaged over
    const float Refresh = 0.5f;       // real seconds between DPS text updates, so it stays readable

    static bool _hidden;
    bool _errorLogged;

    TextMeshProUGUI _dps, _dpsShadow, _xp, _xpShadow, _hp, _hpShadow;
    Canvas _root;
    int _xpLogs, _lastLoggedXp = -1;
    InGameUI _builtFor;

    readonly Queue<(float t, long dmg)> _samples = new();
    float _lastTime = -1f;
    float _nextDpsText;
    long _lastDmg = -1;

    void OnGUI()
    {
        var ev = Event.current;
        if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.F9)
        {
            _hidden = !_hidden;
            ev.Use();
        }
    }

    void LateUpdate()
    {
        try { Tick(); }
        catch (Exception e)
        {
            if (!_errorLogged) { _errorLogged = true; Plugin.Log.LogError("HUD: " + e); }
        }
    }

    void Tick()
    {
        var ui = InGameUI.I;
        var battle = BattleSaveData.I;
        if (ui == null || battle == null) { SetVisible(false); return; }

        if (_builtFor == null || _builtFor.Pointer != ui.Pointer || _dps == null) Build(ui);

        UpdateDps(battle);
        UpdateBars(ui, battle);

        bool show = !_hidden && ui.gameObject.activeInHierarchy;
        SetVisible(show);
    }

    // ---------- DPS ----------

    static long Sum(HeroStatData s) =>
        s == null ? 0 : s.BounceDmgDealt + s.StatusEffectDmgDealt + s.AltDmgDealt + s.AOEDmgDealt;

    static long TotalDamage(BattleSaveData b)
    {
        long total = 0;
        var heroes = b.Heroes;
        if (heroes == null) return 0;
        for (int i = 0; i < heroes.Count; i++)
        {
            var stats = heroes[i]?.StatData;
            if (stats == null) continue;
            for (int j = 0; j < stats.Count; j++) total += Sum(stats[j]);
        }
        return total;
    }

    void UpdateDps(BattleSaveData b)
    {
        float now = b.ElapsedTime;
        long dmg = TotalDamage(b);

        // New run, or a ball evolved and its stats moved: restart from here.
        if (now < _lastTime || dmg < _lastDmg) _samples.Clear();
        _lastTime = now;
        _lastDmg = dmg;

        if (_samples.Count == 0 || now > LastT() + 0.1f)
            _samples.Enqueue((now, dmg));
        while (_samples.Count > 1 && now - _samples.Peek().t > Window) _samples.Dequeue();

        long dps = 0;
        if (_samples.Count > 1)
        {
            var first = _samples.Peek();
            float dt = now - first.t;
            if (dt > 0.5f) dps = (long)((dmg - first.dmg) / dt);
        }
        if (Time.unscaledTime < _nextDpsText) return;
        _nextDpsText = Time.unscaledTime + Refresh;
        SetText(_dps, _dpsShadow, $"DPS <color=#F2C14E>{Fmt(dps)}</color>");
    }

    float LastT()
    {
        float t = -1f;
        foreach (var s in _samples) t = s.t;
        return t;
    }

    // ---------- health / XP numbers ----------

    void UpdateBars(InGameUI ui, BattleSaveData b)
    {
        var mgr = UpgradeMgr.I;
        if (mgr == null) return;

        if (_hp != null)
        {
            // Prefer the game's own (hidden) value text; fall back to the run data.
            string cur = ui.TxtCurHealth != null ? ui.TxtCurHealth.text : null;
            string max = ui.TxtTotalHealth != null ? ui.TxtTotalHealth.text : null;
            string text = !string.IsNullOrWhiteSpace(cur) && !string.IsNullOrWhiteSpace(max)
                ? $"{cur.Trim().TrimStart('/')} / {max.Trim().TrimStart('/').Trim()}"
                : $"{Fmt(Mathf.CeilToInt(b.CurHealth))} / {Fmt(mgr.MaxHealth)}";
            SetText(_hp, _hpShadow, text);
            Follow(ui.HealthFill != null ? ui.HealthFill.rectTransform : null, _hp, 0f);
        }

        if (_xp != null)
        {
            int need = mgr.TgtXP;
            int curXp = Mathf.FloorToInt(b.CurXP);
            SetText(_xp, _xpShadow, need > 0 ? $"{Fmt(curXp)} / {Fmt(need)}" : Fmt(curXp));
            Follow(ui.XPFill != null ? ui.XPFill.rectTransform : null, _xp, 0f);

            if (_xpLogs < 6 && curXp != _lastLoggedXp)
            {
                _xpLogs++;
                _lastLoggedXp = curXp;
                Plugin.Log.LogInfo($"HUD check: XP {b.CurXP}/{need} (bar fill {ui.XPFill?.fillAmount}), HP {b.CurHealth}/{mgr.MaxHealth}, game HP text '{ui.TxtCurHealth?.text}' '{ui.TxtTotalHealth?.text}'");
            }
        }
    }

    // Keeps our label centred on a game HUD element, whatever canvas or mask it sits in.
    void Follow(RectTransform target, TextMeshProUGUI label, float yOffset)
    {
        var rt = label.transform.parent.Cast<RectTransform>();
        if (target == null || !target.gameObject.activeInHierarchy) { rt.gameObject.SetActive(false); return; }
        var tCanvas = target.GetComponentInParent<Canvas>();
        Camera tCam = tCanvas == null || tCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : tCanvas.worldCamera;
        var rootRt = _root.transform.Cast<RectTransform>();
        Camera rCam = _root.renderMode == RenderMode.ScreenSpaceOverlay ? null : _root.worldCamera;

        var world = target.TransformPoint(target.rect.center);
        var screen = RectTransformUtility.WorldToScreenPoint(tCam, world);
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rootRt, screen, rCam, out var local))
            rt.anchoredPosition = local + new Vector2(0f, yOffset);
        if (!_hidden && !rt.gameObject.activeSelf) rt.gameObject.SetActive(true);
    }

    static string Fmt(long v) =>
        v >= 10_000_000 ? (v / 1_000_000f).ToString("0.#") + "M"
        : v >= 100_000 ? (v / 1000f).ToString("0.#") + "k"
        : v.ToString("N0");

    // ---------- building ----------

    void Build(InGameUI ui)
    {
        if (_dps != null) Destroy(_dps.transform.parent.gameObject);
        if (_xp != null) Destroy(_xp.transform.parent.gameObject);
        if (_hp != null) Destroy(_hp.transform.parent.gameObject);
        _dps = _xp = _hp = null;
        _builtFor = ui;
        _samples.Clear();

        var src = ui.TxtCurHealth;
        if (src == null) throw new Exception("InGameUI.TxtCurHealth missing");
        var canvas = src.canvas != null ? src.canvas.rootCanvas : null;
        if (canvas == null) throw new Exception("HUD canvas missing");

        _root = canvas;

        // DPS: top middle of the screen.
        var dpsRt = NewRect("EvoHelperDPS", canvas.transform);
        dpsRt.anchorMin = dpsRt.anchorMax = dpsRt.pivot = new Vector2(0.5f, 1f);
        dpsRt.anchoredPosition = new Vector2(0f, -10f);
        dpsRt.sizeDelta = new Vector2(300f, src.fontSize * 1.2f);
        OnTop(dpsRt);
        (_dps, _dpsShadow) = ShadowText(dpsRt, src, src.fontSize * 0.75f);

        // Health and XP numbers: on the root canvas, above everything, moved onto the bars each frame.
        (_hp, _hpShadow) = Label("EvoHelperHP", canvas, src, src.fontSize * 0.7f);
        (_xp, _xpShadow) = Label("EvoHelperXP", canvas, src, src.fontSize * 0.6f);
        Plugin.Log.LogInfo($"HUD built on canvas '{canvas.name}' (XP bar {(ui.XPFill != null ? ui.XPFill.name : "missing")}).");
    }

    static (TextMeshProUGUI, TextMeshProUGUI) Label(string name, Canvas canvas, TextMeshProUGUI src, float size)
    {
        var rt = NewRect(name, canvas.transform);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(200f, size * 1.5f);
        OnTop(rt);
        return ShadowText(rt, src, size);
    }

    // Own sorting layer so nested HUD canvases (the health orb) can't draw over it.
    static void OnTop(RectTransform rt)
    {
        var c = rt.gameObject.AddComponent<Canvas>();
        c.overrideSorting = true;
        c.sortingOrder = 1000;
    }

    static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    // Text plus a dark copy offset by 2px underneath (outlines crash on new TMP text here).
    static (TextMeshProUGUI, TextMeshProUGUI) ShadowText(RectTransform parent, TextMeshProUGUI src, float size)
    {
        TextMeshProUGUI Make(string name, Vector2 offset, Color color)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = offset; rt.offsetMax = offset;
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.font = src.font;
            t.fontSharedMaterial = src.fontSharedMaterial;
            t.fontSize = size;
            t.color = color;
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
            t.richText = true;
            return t;
        }
        var shadow = Make("Shadow", new Vector2(2f, -2f), new Color(0f, 0f, 0f, 0.85f));
        var main = Make("Text", Vector2.zero, Color.white);
        return (main, shadow);
    }

    static void SetText(TextMeshProUGUI main, TextMeshProUGUI shadow, string text)
    {
        if (main == null || main.text == text) return;
        main.text = text;
        // The shadow ignores colour tags so it stays a plain dark copy.
        shadow.text = System.Text.RegularExpressions.Regex.Replace(text, "</?color[^>]*>", "");
    }

    void SetVisible(bool on)
    {
        if (_dps != null && _dps.transform.parent.gameObject.activeSelf != on) _dps.transform.parent.gameObject.SetActive(on);
        // HP / XP labels: Follow() shows them when their bar is visible; hide them when the HUD is off.
        if (!on)
        {
            if (_xp != null) _xp.transform.parent.gameObject.SetActive(false);
            if (_hp != null) _hp.transform.parent.gameObject.SetActive(false);
        }
    }
}

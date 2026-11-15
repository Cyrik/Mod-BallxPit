using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EvoHelper;

// In-run HUD additions, read-only:
//  - DPS readout at the bottom middle of the screen (damage your balls dealt over the
//    last few seconds of game time, from the game's own per-ball damage stats).
//  - The health bar's own numbers kept visible (the game shows them only on hover).
//  - "current / needed" numbers on the XP bar.
// F9 toggles all three.
public class Hud : MonoBehaviour
{
    public Hud(IntPtr ptr) : base(ptr) { }

    const float Window = 5f;          // seconds of game time the DPS is averaged over
    const float Refresh = 0.5f;       // real seconds between DPS text updates, so it stays readable

    static bool _hidden;
    bool _errorLogged, _valuesLogged;

    TextMeshProUGUI _dps, _dpsShadow, _xp, _xpShadow;
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

        // Health numbers: keep the game's own value text showing.
        if (!_hidden && ui.GrpHealthValues != null) ui.GrpHealthValues.alpha = 1f;

        UpdateDps(battle);
        UpdateXp(ui, battle);

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

    // ---------- XP ----------

    void UpdateXp(InGameUI ui, BattleSaveData b)
    {
        var mgr = UpgradeMgr.I;
        if (mgr == null || _xp == null) return;
        int need = mgr.TgtXP;
        int cur = Mathf.FloorToInt(b.CurXP);
        SetText(_xp, _xpShadow, need > 0 ? $"{Fmt(cur)} / {Fmt(need)}" : Fmt(cur));

        if (!_valuesLogged && need > 0)
        {
            _valuesLogged = true;
            Plugin.Log.LogInfo($"HUD check: XP {b.CurXP}/{need} (bar fill {ui.XPFill?.fillAmount}), HP {b.CurHealth}/{mgr.MaxHealth}");
        }
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
        _dps = _xp = null;
        _builtFor = ui;
        _samples.Clear();

        var src = ui.TxtCurHealth;
        if (src == null) throw new Exception("InGameUI.TxtCurHealth missing");
        var canvas = src.canvas != null ? src.canvas.rootCanvas : null;
        if (canvas == null) throw new Exception("HUD canvas missing");

        // DPS: bottom middle of the screen.
        var dpsRt = NewRect("EvoHelperDPS", canvas.transform);
        dpsRt.anchorMin = dpsRt.anchorMax = dpsRt.pivot = new Vector2(0.5f, 0f);
        dpsRt.anchoredPosition = new Vector2(0f, 12f);
        dpsRt.sizeDelta = new Vector2(400f, src.fontSize * 1.6f);
        (_dps, _dpsShadow) = ShadowText(dpsRt, src, src.fontSize * 1.1f);

        // XP numbers: centred on the XP bar.
        if (ui.XPFill != null)
        {
            var bar = ui.XPFill.rectTransform;
            var xpRt = NewRect("EvoHelperXP", bar.parent);
            xpRt.anchorMin = bar.anchorMin; xpRt.anchorMax = bar.anchorMax; xpRt.pivot = bar.pivot;
            xpRt.anchoredPosition = bar.anchoredPosition; xpRt.sizeDelta = bar.sizeDelta;
            xpRt.localScale = Vector3.one;
            xpRt.SetAsLastSibling();
            (_xp, _xpShadow) = ShadowText(xpRt, src, src.fontSize * 0.8f);
        }
        Plugin.Log.LogInfo($"HUD built on canvas '{canvas.name}' (XP bar {(ui.XPFill != null ? ui.XPFill.name : "missing")}).");
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
        if (_xp != null && _xp.transform.parent.gameObject.activeSelf != on) _xp.transform.parent.gameObject.SetActive(on);
    }
}

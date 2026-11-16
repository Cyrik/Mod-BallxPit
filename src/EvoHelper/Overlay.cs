using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.Attributes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EvoHelper;

// Adds an "Evolutions" block of icons to the game's own description panel on the
// "Select an upgrade" page, using the game's font. Hovering an icon shows its name.
// If building the block fails, it falls back to the plain IMGUI panel from v0.3.
public partial class Overlay : MonoBehaviour
{
    public Overlay(IntPtr ptr) : base(ptr) { }

    static readonly Color Gold = new(0.95f, 0.76f, 0.31f, 1f);
    static readonly Color Green = new(0.56f, 0.89f, 0.53f, 1f);
    static readonly Color Grey = new(0.60f, 0.56f, 0.60f, 1f);
    static readonly Color ChipBg = new(0f, 0f, 0f, 0.22f);
    static readonly Color ChipBgReady = new(0.30f, 0.62f, 0.28f, 0.28f);
    static readonly Color Dim = new(0.42f, 0.42f, 0.42f, 0.75f);

    static bool _hidden;
    bool _uguiFailed, _layoutLogged, _errorLogged, _shift;
    Vector2 _mouse;

    // Game-font settings copied from the tooltip: body text (cloak 16), the
    // "Synergies with Equipment" heading, and the pixel font of its "x" / "=" signs.
    TMP_FontAsset _font, _hdrFont, _glyphFont;
    Material _fontMat, _hdrMat, _glyphMat;
    float _fs = 16f, _hdrSize = 10.65f;
    Color _textColor = Color.white, _hdrColor = Gold;

    RectTransform _root;
    LayoutElement _rootLayout;
    bool _inLayoutGroup;
    int _builtVersion = -1;
    float _builtWidth = -1;
    readonly List<(RectTransform rt, Item item)> _icons = new();

    // Stack of hover tooltips: [0] opened from the block's icons, [n+1] from an icon inside [n].
    sealed class Popup
    {
        public RectTransform Rt, Anchor;
        public Item Item;
        public bool Shift;
        public float Touched;
        public readonly List<(RectTransform rt, Item item)> Icons = new();
    }
    readonly List<Popup> _popups = new();
    const float PopupGrace = 0.3f;   // seconds a tooltip survives after the mouse leaves it
    const float OpenDelay = 0.35f;   // seconds the mouse must rest on an icon before its tooltip opens
    const int TooltipRows = 3;       // rows of recipes shown in a tooltip without Shift
    float TooltipIcon => Mathf.Round(Icon * 0.6f);
    RectTransform _pendingRt;
    float _pendingSince;
    bool _layoutDirty;

    static bool ShouldShow()
    {
        var ui = LevelUpUI.I;
        if (ui == null || !ui.gameObject.activeInHierarchy) return false;
        if (ui.CurPage != LevelUpPage.kPickUpgrade) return false;
        return Evo.HasOffers;
    }

    // ---------- frame loop ----------

    void LateUpdate()
    {
        CharLock.LogAll();
        UpdateEquipBlocks();
        bool show = !_hidden && ShouldShow();
        try
        {
            if (!show || _uguiFailed)
            {
                if (Inspecting) ExitInspect();
                if (_root != null) _root.gameObject.SetActive(false);
                RestoreGameSynergies();
                ClosePopupsFrom(0);
                return;
            }

            var ui = LevelUpUI.I;
            if (_root == null && !CreateRoot(ui)) { _uguiFailed = true; return; }
            if (!_root.gameObject.activeSelf) _root.gameObject.SetActive(true);
            if (_inLayoutGroup && _root.GetSiblingIndex() != _root.parent.childCount - 1) _root.SetAsLastSibling();
            HideGameSynergies();

            float width = _root.rect.width;
            if (width < 10f) return;   // wait for the first layout pass
            if (_builtVersion != Evo.Version || Mathf.Abs(width - _builtWidth) > 1f
                || _builtPad != PadActive || _builtInspect != Inspecting)
            {
                Rebuild(width);
                _builtVersion = Evo.Version;
                _builtWidth = width;
                _builtPad = PadActive;
                _builtInspect = Inspecting;
            }
            EnsureScrollable(ui);
            UpdatePad(ui);
            _shift = _kbShift || _padDesc;
            if (Inspecting) UpdatePopupsPad(); else UpdatePopups();
        }
        catch (Exception e)
        {
            _uguiFailed = true;
            if (Inspecting) ExitInspect();
            Plugin.Log.LogError("In-tooltip panel failed, using the fallback panel: " + e);
            if (_root != null) Destroy(_root.gameObject);
            ClosePopupsFrom(0);
        }
    }

    // The game's own "Synergies with Equipment" heading and rows, in the same list as our
    // block. Hidden while our block shows (it lists the same recipes), restored when F8 hides ours.
    readonly List<GameObject> _hiddenGame = new();

    void HideGameSynergies()
    {
        if (!_inLayoutGroup || _root == null) return;
        var list = _root.parent;
        for (int i = 0; i < list.childCount; i++)
        {
            var go = list.GetChild(i).gameObject;
            if (!go.activeSelf || go == _root.gameObject) continue;
            if (go.name == "TxtEvos" || go.GetComponent<EquipmentEvoPanel>() != null)
            {
                go.SetActive(false);
                if (!_hiddenGame.Contains(go)) _hiddenGame.Add(go);
            }
        }
    }

    void RestoreGameSynergies()
    {
        if (_hiddenGame.Count == 0) return;
        foreach (var go in _hiddenGame) if (go != null) go.SetActive(true);
        _hiddenGame.Clear();
    }

    void OnGUI()
    {
        var ev = Event.current;
        if (ev == null) return;
        // IMGUI y runs top-down; UI screen points run bottom-up.
        _mouse = new Vector2(ev.mousePosition.x, Screen.height - ev.mousePosition.y);

        _kbShift = ev.shift;

        if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.F8)
        {
            _hidden = !_hidden;
            ev.Use();
        }
        if (ev.type == EventType.ScrollWheel && _root != null && _root.gameObject.activeInHierarchy)
            ScrollDetails(ev.delta.y);
        if (_uguiFailed && !_hidden && ShouldShow())
        {
            try { FallbackPanel.Draw(); }
            catch (Exception e)
            {
                if (!_errorLogged) { _errorLogged = true; Plugin.Log.LogError("Fallback panel failed: " + e); }
            }
        }
    }

    // ---------- setup ----------

    bool CreateRoot(LevelUpUI ui)
    {
        var details = ui.PanelSelectionDetails;
        if (details == null) { Plugin.Log.LogWarning("No PanelSelectionDetails; using fallback panel."); return false; }

        if (!_layoutLogged) { _layoutLogged = true; LayoutLog.Dump(details.transform); }

        // Font: the description text of the game's tooltip.
        TextMeshProUGUI src = null;
        if (ui.LocDetailsDesc != null) src = ui.LocDetailsDesc.GetComponent<TextMeshProUGUI>();
        if (src == null) src = details.GetComponentInChildren<TextMeshProUGUI>(true);
        if (src == null) { Plugin.Log.LogWarning("No tooltip text found; using fallback panel."); return false; }
        _font = src.font;
        _fontMat = src.fontSharedMaterial;
        _fs = Mathf.Max(8f, src.fontSize);
        _textColor = src.color;

        _hdrFont = _font; _hdrMat = _fontMat; _hdrSize = Mathf.Round(_fs * 0.666f * 100f) / 100f;
        var hdr = FindDeep(details.transform, "TxtEvos")?.GetComponent<TextMeshProUGUI>();
        if (hdr != null) { _hdrFont = hdr.font; _hdrMat = hdr.fontSharedMaterial; _hdrSize = hdr.fontSize; _hdrColor = hdr.color; }

        _glyphFont = _font; _glyphMat = _fontMat;
        var glyph = FindDeep(details.transform, "TxtEquals")?.GetComponent<TextMeshProUGUI>();
        if (glyph != null) { _glyphFont = glyph.font; _glyphMat = glyph.fontSharedMaterial; }

        // Prefer flowing inside the scrollable details content, after the game's own text.
        Transform container = ui.ScrlDetails != null && ui.ScrlDetails.content != null
            ? ui.ScrlDetails.content
            : details.transform;
        _inLayoutGroup = container.GetComponent<VerticalLayoutGroup>() != null;

        var go = new GameObject("EvoHelperBlock");
        _root = go.AddComponent<RectTransform>();
        if (_inLayoutGroup)
        {
            _root.SetParent(container, false);
            _rootLayout = go.AddComponent<LayoutElement>();
            _rootLayout.flexibleWidth = 1f;
        }
        else
        {
            // No layout group: pin to the bottom of the details panel, inside its frame.
            _root.SetParent(details.transform, false);
            _root.anchorMin = new Vector2(0.06f, 0f);
            _root.anchorMax = new Vector2(0.94f, 0f);
            _root.pivot = new Vector2(0.5f, 0f);
            _root.anchoredPosition = new Vector2(0f, _fs * 2.2f);
        }
        Plugin.Log.LogInfo($"Tooltip block attached to '{_root.parent.name}' (layout group: {_inLayoutGroup}), font '{_font?.name}' size {_fs}.");
        return true;
    }

    // ---------- building ----------

    float Icon => Mathf.Round(_fs * 1.9f);          // 30 at the tooltip's size 16
    float Gap => Mathf.Max(3f, Mathf.Round(_fs * 0.3f));

    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            var hit = FindDeep(t.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }

    void Rebuild(float width)
    {
        for (int i = _root.childCount - 1; i >= 0; i--) Destroy(_root.GetChild(i).gameObject);
        _icons.Clear();

        float y = Gap;
        bool anyUndiscovered = false;
        var offer = Evo.Current;


        if (offer != null)
        {
            y = Header("Evolutions", y, width);
            if (offer.Evos.Count == 0)
            {
                Text(_root, "None", 0, y, width, _hdrSize, Grey, font: _hdrFont, mat: _hdrMat);
                y += _hdrSize * 1.6f;
            }
            else
            {
                var chips = offer.Evos.Select(e => (parts: e.Partners, result: e.Result,
                    bg: e.Status == EvoStatus.HaveAll ? ChipBgReady : ChipBg, showLvlOf3: false)).ToList();
                y = FlowChips(_root, _icons, chips, 0, y, width, ref anyUndiscovered);
            }
        }

        // Only the kind being looked at: ball pairs for a ball, passive pairs for a passive.
        bool wantBalls = offer == null || offer.Self.IsBall;
        var pairs = Evo.Pairs.Where(p => p.Result.IsBall == wantBalls).ToList();
        if (pairs.Count > 0)
        {
            y += Gap;
            y = Header($"Ready to evolve (each Lv{Evo.EvoLevel})", y, width);
            var chips = pairs.Select(p => (parts: p.Parts, result: p.Result, bg: ChipBgReady, showLvlOf3: true)).ToList();
            y = FlowChips(_root, _icons, chips, 0, y, width, ref anyUndiscovered);
        }

        // Controller: how to reach the icons, with the game's own button icons.
        if (PadActive && _icons.Count > 0)
        {
            y += Gap * 0.5f;
            y = PadHintRow(_root, y, width);
        }

        if (anyUndiscovered)
        {
            y += Gap * 0.5f;
            var sw = NewRect("LegendSwatch", _root);
            Place(sw, 0, y + 1, _hdrSize, _hdrSize);
            Frame(sw, Gold);
            Text(_root, "not in your encyclopedia yet", _hdrSize * 1.6f, y, width, _hdrSize, Grey, font: _hdrFont, mat: _hdrMat);
            y += _hdrSize * 1.6f;
        }

        y += Gap;
        // The game's list does not let its layout group size children, so it reads the block's
        // own rect height: set that too, not only the preferred height.
        if (_inLayoutGroup) { _rootLayout.preferredHeight = y; _rootLayout.minHeight = y; }
        _root.sizeDelta = new Vector2(_root.sizeDelta.x, y);

        _layoutDirty = true;
    }

    // The game decides whether its tooltip scrolls from its own text only. After adding the
    // block, re-measure the list and switch scrolling (and its scrollbar) on if it now overflows.
    void EnsureScrollable(LevelUpUI ui)
    {
        var scrl = ui.ScrlDetails;
        if (scrl == null || scrl.content == null || scrl.viewport == null) return;
        if (_layoutDirty)
        {
            _layoutDirty = false;
            LayoutRebuilder.ForceRebuildLayoutImmediate(scrl.content);
        }
        if (scrl.content.rect.height <= scrl.viewport.rect.height + 1f) return;
        if (!scrl.vertical) scrl.vertical = true;
        var bar = scrl.verticalScrollbar;
        if (bar != null && !bar.gameObject.activeSelf) bar.gameObject.SetActive(true);
    }

    // Mouse-wheel scrolling of the tooltip while the cursor is over it.
    void ScrollDetails(float delta)
    {
        var scrl = LevelUpUI.I?.ScrlDetails;
        if (scrl == null || scrl.content == null || scrl.viewport == null) return;

        var canvas = _root.GetComponentInParent<Canvas>();
        var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        if (!RectTransformUtility.RectangleContainsScreenPoint(scrl.viewport, _mouse, cam)) return;

        float overflow = scrl.content.rect.height - scrl.viewport.rect.height;
        if (overflow <= 1f) return;
        // One wheel notch = about one row of icons.
        float step = (Icon + Gap * 2) / overflow;
        scrl.verticalNormalizedPosition = Mathf.Clamp01(scrl.verticalNormalizedPosition - Mathf.Sign(delta) * step);
    }

    // Styled like the tooltip's own "Synergies with Equipment" heading.
    float Header(string text, float y, float width)
    {
        Text(_root, text, 0, y, width, _hdrSize, _hdrColor, TextAlignmentOptions.Center, _hdrFont, _hdrMat);
        return y + _hdrSize * 1.8f;
    }

    // Lays out recipe chips left to right, wrapping to new rows.
    [HideFromIl2Cpp]
    float FlowChips(RectTransform parent, List<(RectTransform rt, Item item)> sink,
        List<(List<Item> parts, Item result, Color bg, bool showLvlOf3)> chips, float x0, float y, float width, ref bool anyUndiscovered,
        float iconSize = 0f, int maxRows = 0, List<int> hiddenOut = null)
    {
        float icon = iconSize > 0 ? iconSize : Icon;
        float scale = icon / Icon;
        float gap = Mathf.Max(2f, Mathf.Round(Gap * scale)), pad = Mathf.Max(1f, Mathf.Round(Gap * 0.6f * scale));
        float arrowW = Mathf.Round(_fs * 0.9f * scale);
        float glyph = scale < 0.9f ? _hdrSize : _fs;
        float rowH = icon + pad * 2;
        float x = 0;
        int row = 0, shown = 0;

        foreach (var c in chips)
        {
            int n = c.parts.Count;
            float chipW = pad * 2 + n * icon + (n - 1) * pad + arrowW + icon;
            if (x > 0 && x + chipW > width)
            {
                x = 0; row++;
                if (maxRows > 0 && row >= maxRows) break;
                y += rowH + gap;
            }
            shown++;

            var chip = NewRect("Chip", parent);
            Place(chip, x0 + x, y, chipW, rowH);
            var bg = chip.gameObject.AddComponent<Image>();
            bg.color = c.bg;
            bg.raycastTarget = false;

            float ix = pad;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) ix += pad;
                IconAt(chip, sink, c.parts[i], ix, pad, icon, dimIfMissing: true, lvlOf3: c.showLvlOf3, ref anyUndiscovered);
                ix += icon;
            }
            // Same "=" glyph and pixel font as the game's own evolution rows.
            Text(chip, "=", ix, pad + icon * 0.5f - glyph * 0.7f, arrowW, glyph, _textColor, TextAlignmentOptions.Center, _glyphFont, _glyphMat);
            ix += arrowW;
            IconAt(chip, sink, c.result, ix, pad, icon, dimIfMissing: false, lvlOf3: false, ref anyUndiscovered);

            // "+" badge: this result can itself evolve further (hover it for details).
            if (Further.For(c.result.Info).Count > 0)
            {
                float bw = Mathf.Round(glyph * 0.66f), by = pad - Mathf.Round(glyph * 0.15f);
                Text(chip, "+", ix + icon - bw + 1, by + 1, bw, glyph, new Color(0, 0, 0, 0.9f), TextAlignmentOptions.Center, _glyphFont, _glyphMat);
                Text(chip, "+", ix + icon - bw, by, bw, glyph, Gold, TextAlignmentOptions.Center, _glyphFont, _glyphMat);
            }

            x += chipW + gap;
        }
        hiddenOut?.Add(chips.Count - shown);
        return y + rowH + gap;
    }

    [HideFromIl2Cpp]
    void IconAt(RectTransform parent, List<(RectTransform rt, Item item)> sink, Item item, float x, float y, float size, bool dimIfMissing, bool lvlOf3, ref bool anyUndiscovered)
    {
        var holder = NewRect(item.Name, parent);
        Place(holder, x, y, size, size);

        if (!item.Discovered)
        {
            anyUndiscovered = true;
            Frame(holder, Gold);
        }

        var iconRt = NewRect("Icon", holder);
        Place(iconRt, 1, 1, size - 2, size - 2);
        var img = iconRt.gameObject.AddComponent<Image>();
        img.raycastTarget = false;
        img.preserveAspect = true;
        item.Info.ApplyIcon(img);
        if (dimIfMissing && !item.Owned) img.color = img.color * Dim;

        if (item.Owned && dimIfMissing && size >= 24f)   // no level badge on small tooltip icons
        {
            // Level number in the corner, with a 1-unit drop shadow (TMP outlines crash on new text here).
            string lvl = lvlOf3 ? $"{item.Lvl}/{Evo.EvoLevel}" : item.Lvl.ToString();
            float ty = size - _hdrSize * 1.25f;
            Text(holder, lvl, 1, ty + 1, size, _hdrSize, new Color(0, 0, 0, 0.9f), TextAlignmentOptions.Right, _hdrFont, _hdrMat);
            Text(holder, lvl, 0, ty, size, _hdrSize, item.Lvl >= Evo.EvoLevel ? Green : _textColor, TextAlignmentOptions.Right, _hdrFont, _hdrMat);
        }
        else if (size >= 20f)
        {
            // Character-specific passive: that character's portrait in the corner,
            // lit if they are in this run (so it can roll), greyed if not.
            var chars = CharLock.For(item.Info);
            if (chars != null && chars.Count > 0)
            {
                bool live = CharLock.InRun(chars);
                float b = Mathf.Round(size * 0.42f);
                for (int i = 0; i < chars.Count; i++)
                {
                    var sprite = chars[i].IconSmall != null ? chars[i].IconSmall : chars[i].Icon;
                    if (sprite == null) continue;
                    var badge = NewRect("CharBadge", holder);
                    Place(badge, size - b * (i + 1) + 2, size - b + 2, b, b);
                    var bi = badge.gameObject.AddComponent<Image>();
                    bi.raycastTarget = false;
                    bi.preserveAspect = true;
                    bi.sprite = sprite;
                    bi.color = live ? Color.white : new Color(0.32f, 0.32f, 0.32f, 0.85f);
                }
            }
        }
        sink.Add((holder, item));
    }

    // A 2-unit border drawn as four thin images.
    void Frame(RectTransform target, Color color)
    {
        float t = Mathf.Max(2f, Mathf.Round(_fs * 0.08f));
        void Edge(string name, Vector2 aMin, Vector2 aMax, Vector2 size)
        {
            var e = NewRect(name, target);
            e.anchorMin = aMin; e.anchorMax = aMax; e.pivot = new Vector2(0.5f, 0.5f);
            e.sizeDelta = size; e.anchoredPosition = Vector2.zero;
            var img = e.gameObject.AddComponent<Image>();
            img.color = color; img.raycastTarget = false;
        }
        Edge("FrameT", new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, t));
        Edge("FrameB", new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, t));
        Edge("FrameL", new Vector2(0, 0), new Vector2(0, 1), new Vector2(t, 0));
        Edge("FrameR", new Vector2(1, 0), new Vector2(1, 1), new Vector2(t, 0));
    }

    // ---------- hover tooltips (nested) ----------
    //
    // Hovering an icon opens a tooltip above it. The tooltip stays open while the mouse is
    // on it or on the icon that opened it (with a short grace period for crossing the gap),
    // and its own icons open further tooltips the same way.

    Camera UiCam(out Canvas canvas)
    {
        canvas = _root.GetComponentInParent<Canvas>();
        return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
    }

    bool Inside(RectTransform rt, Camera cam) =>
        rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, _mouse, cam);

    void UpdatePopups()
    {
        var cam = UiCam(out var canvas);
        if (canvas == null) return;
        float now = Time.unscaledTime;

        // Drop tooltips whose opening icon has gone (block rebuilt, parent tooltip rebuilt).
        for (int i = 0; i < _popups.Count; i++)
            if (_popups[i].Rt == null || _popups[i].Anchor == null) { ClosePopupsFrom(i); break; }

        // 1. Keep alive every tooltip the mouse is on (or on its icon), plus all its parents.
        for (int i = _popups.Count - 1; i >= 0; i--)
        {
            var p = _popups[i];
            if (!Inside(p.Rt, cam) && !Inside(p.Anchor, cam)) continue;
            for (int j = 0; j <= i; j++) _popups[j].Touched = now;
            break;
        }

        // 2. Find the icon under the mouse, in the topmost tooltip it is over (or the block).
        int level = -2;
        RectTransform hitRt = null;
        Item hitItem = null;
        for (int i = _popups.Count - 1; i >= 0 && level == -2; i--)
        {
            if (!Inside(_popups[i].Rt, cam)) continue;
            level = i;
            foreach (var (rt, item) in _popups[i].Icons)
                if (Inside(rt, cam)) { hitRt = rt; hitItem = item; break; }
        }
        if (level == -2)
        {
            foreach (var (rt, item) in _icons)
                if (Inside(rt, cam)) { hitRt = rt; hitItem = item; level = -1; break; }
        }

        if (hitRt != null)
        {
            int child = level + 1;
            if (_popups.Count > child && _popups[child].Anchor == hitRt)
            {
                _popups[child].Touched = now;
                _pendingRt = null;
                if (_popups[child].Shift != _shift) RebuildPopup(child, canvas.rootCanvas.transform);
            }
            else if (_pendingRt != hitRt)
            {
                // Start the hover delay; passing over icons does not open anything.
                _pendingRt = hitRt;
                _pendingSince = now;
            }
            else if (now - _pendingSince >= OpenDelay)
            {
                _pendingRt = null;
                ClosePopupsFrom(child);
                OpenPopup(hitRt, hitItem, canvas.rootCanvas.transform, now);
            }
        }
        else _pendingRt = null;

        // 3. Close tooltips (from the top) the mouse has left for longer than the grace period.
        while (_popups.Count > 0 && now - _popups[_popups.Count - 1].Touched > PopupGrace)
            ClosePopupsFrom(_popups.Count - 1);
    }

    [HideFromIl2Cpp]
    void OpenPopup(RectTransform anchor, Item item, Transform canvasRoot, float now)
    {
        var p = new Popup { Anchor = anchor, Item = item, Touched = now };
        _popups.Add(p);
        BuildPopup(p, canvasRoot);
    }

    void RebuildPopup(int index, Transform canvasRoot)
    {
        ClosePopupsFrom(index + 1);
        var p = _popups[index];
        if (p.Rt != null) Destroy(p.Rt.gameObject);
        p.Icons.Clear();
        BuildPopup(p, canvasRoot);
    }

    void ClosePopupsFrom(int index)
    {
        for (int i = _popups.Count - 1; i >= index && i >= 0; i--)
        {
            if (_popups[i].Rt != null) Destroy(_popups[i].Rt.gameObject);
            _popups.RemoveAt(i);
        }
    }

    [HideFromIl2Cpp]
    void BuildPopup(Popup p, Transform canvasRoot)
    {
        var item = p.Item;
        p.Shift = _shift;
        float pad = Mathf.Round(_fs * 0.45f);
        float maxW = Mathf.Round(_fs * 12f);

        var rt = NewRect("EvoHelperTooltip", canvasRoot);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        p.Rt = rt;
        var bg = rt.gameObject.AddComponent<Image>();
        bg.color = new Color(0.08f, 0.05f, 0.07f, 0.96f);
        bg.raycastTarget = true;   // the game's cards underneath should not react to the mouse
        Frame(rt, new Color(0.55f, 0.40f, 0.28f, 1f));

        // Text part.
        var sb = new System.Text.StringBuilder();
        sb.Append(item.Name);
        if (item.Owned) sb.Append($"  <color=#8FE388>Lv{item.Lvl}</color>");
        if (!item.Discovered) sb.Append($"\n<color=#F2C14E>Not in encyclopedia</color>");
        if (_shift)
        {
            var desc = Describe.Text(item.Info, Math.Max(0, item.Lvl - 1));
            if (!string.IsNullOrEmpty(desc)) sb.Append($"\n<size={_hdrSize}>{desc}</size>");
        }
        else sb.Append($"\n<size={_hdrSize}><color=#9A8F99>hold {DescKey()} for description</color></size>");
        string text = sb.ToString();

        var recipes = Further.For(item.Info);
        var madeFrom = Further.MadeFrom(item.Info);
        var t = Text(rt, text, pad, pad, maxW, _fs, _textColor, TextAlignmentOptions.Top);
        t.enableWordWrapping = true;
        float textW = recipes.Count > 0 || madeFrom.Count > 0 ? maxW : Mathf.Min(maxW, t.GetPreferredValues(text).x + 2f);
        float textH = t.GetPreferredValues(text, textW, 0f).y;
        Place(t.rectTransform, pad, pad, textW, textH);
        float y = pad + textH;
        float w = textW;

        // "Made from": the recipe(s) that create this item, ingredients hoverable too.
        if (madeFrom.Count > 0)
        {
            y += Gap;
            Text(rt, "Made from", pad, y, w, _hdrSize, _hdrColor, TextAlignmentOptions.Center, _hdrFont, _hdrMat);
            y += _hdrSize * 1.8f;
            bool anyUndiscoveredM = false;
            var self = Evo.MakeItem(item.Info);
            var chipsM = madeFrom.Select(set =>
            {
                var parts = set.Select(Evo.MakeItem).ToList();
                return (parts: parts, result: self, bg: parts.All(x => x.Owned) ? ChipBgReady : ChipBg, showLvlOf3: false);
            }).ToList();
            y = FlowChips(rt, p.Icons, chipsM, pad, y, w, ref anyUndiscoveredM, iconSize: TooltipIcon) - Gap;
        }

        // "Evolves further" as icon chips, hoverable for the next tooltip.
        if (recipes.Count > 0)
        {
            y += Gap;
            Text(rt, "Evolves further", pad, y, w, _hdrSize, _hdrColor, TextAlignmentOptions.Center, _hdrFont, _hdrMat);
            y += _hdrSize * 1.8f;
            bool anyUndiscovered = false;
            var chips = recipes.Select(r =>
            {
                var partners = r.partners.Select(Evo.MakeItem).ToList();
                bool all = partners.All(x => x.Owned);
                return (parts: partners, result: Evo.MakeItem(r.result), bg: all ? ChipBgReady : ChipBg, showLvlOf3: false);
            }).OrderBy(c => c.bg == ChipBgReady ? 0 : 1).ToList();

            // Small icons; at most a few rows unless Shift is held.
            var hidden = new List<int>();
            y = FlowChips(rt, p.Icons, chips, pad, y, w, ref anyUndiscovered,
                iconSize: TooltipIcon, maxRows: _shift ? 0 : TooltipRows, hiddenOut: hidden) - Gap;
            if (hidden.Count > 0 && hidden[0] > 0)
            {
                y += Gap;
                Text(rt, $"+{hidden[0]} more (hold {DescKey()})", pad, y, w, _hdrSize, Grey, TextAlignmentOptions.Center, _hdrFont, _hdrMat);
                y += _hdrSize * 1.5f;
            }
        }
        y += pad;
        rt.sizeDelta = new Vector2(Mathf.Round(w + pad * 2), Mathf.Round(y));

        PlacePopup(rt, p.Anchor, canvasRoot);
        rt.SetAsLastSibling();
    }

    // Icon in the bottom half of the screen: tooltip goes above it; top half: below it.
    // Then nudged so it stays fully on screen.
    void PlacePopup(RectTransform rt, RectTransform anchor, Transform canvasRoot)
    {
        var canvasRt = canvasRoot.TryCast<RectTransform>();
        var anchorCenter = anchor.TransformPoint(anchor.rect.center);
        bool iconInBottomHalf = canvasRt == null || canvasRt.InverseTransformPoint(anchorCenter).y < canvasRt.rect.center.y;

        if (iconInBottomHalf)
        {
            rt.pivot = new Vector2(0.5f, 0f);
            rt.position = anchor.TransformPoint(new Vector3(anchor.rect.center.x, anchor.rect.yMax + Gap, 0f));
        }
        else
        {
            rt.pivot = new Vector2(0.5f, 1f);
            rt.position = anchor.TransformPoint(new Vector3(anchor.rect.center.x, anchor.rect.yMin - Gap, 0f));
        }
        if (canvasRt == null) return;

        // Keep inside the screen (canvas-local units; the tooltip's size is in the same units).
        var local = canvasRt.InverseTransformPoint(rt.position);
        var b = canvasRt.rect;
        float w = rt.sizeDelta.x, h = rt.sizeDelta.y;
        float x = Mathf.Clamp(local.x, b.xMin + w * 0.5f, Mathf.Max(b.xMin + w * 0.5f, b.xMax - w * 0.5f));
        float bottom = iconInBottomHalf ? local.y : local.y - h;
        float clampedBottom = Mathf.Clamp(bottom, b.yMin, Mathf.Max(b.yMin, b.yMax - h));
        float y = local.y + (clampedBottom - bottom);
        if (!Mathf.Approximately(x, local.x) || !Mathf.Approximately(y, local.y))
            rt.position = canvasRt.TransformPoint(new Vector3(x, y, local.z));
    }

    // ---------- small helpers ----------

    static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        return rt;
    }

    static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchoredPosition = new Vector2(Mathf.Round(x), -Mathf.Round(y));
        rt.sizeDelta = new Vector2(Mathf.Round(w), Mathf.Round(h));
    }

    TextMeshProUGUI Text(RectTransform parent, string text, float x, float y, float w, float size, Color color,
        TextAlignmentOptions align = TextAlignmentOptions.TopLeft, TMP_FontAsset font = null, Material mat = null)
    {
        var rt = NewRect("Text", parent);
        Place(rt, x, y, w, size * 1.4f);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.font = font ?? _font;
        t.fontSharedMaterial = mat ?? _fontMat;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.enableWordWrapping = false;
        t.overflowMode = TextOverflowModes.Overflow;
        t.raycastTarget = false;
        t.text = text;
        return t;
    }
}

// Logs the description panel's hierarchy once, so layout problems can be fixed from the log.
internal static class LayoutLog
{
    public static void Dump(Transform root)
    {
        Plugin.Log.LogInfo("Description panel layout:");
        Walk(root, 0);
    }

    static void Walk(Transform t, int depth)
    {
        if (depth > 5) return;
        var comps = new List<string>();
        foreach (var c in t.GetComponents<Component>())
            if (c != null) comps.Add(c.GetIl2CppType().Name);
        var rt = t.TryCast<RectTransform>();
        string size = rt != null ? $" {rt.rect.width:0}x{rt.rect.height:0}" : "";
        var tmp = t.GetComponent<TextMeshProUGUI>();
        string txt = tmp != null ? $" text=\"{Short(tmp.text)}\" font={tmp.font?.name} size={tmp.fontSize}" : "";
        Plugin.Log.LogInfo($"{new string(' ', depth * 2)}- {t.name} [{(t.gameObject.activeSelf ? "on" : "off")}]{size} ({string.Join(", ", comps)}){txt}");
        for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
    }

    static string Short(string s) => s == null ? "" : (s.Length > 30 ? s.Substring(0, 30) + "..." : s).Replace("\n", " ");
}

// The v0.3 plain panel, used only if the in-tooltip block cannot be built.
internal static class FallbackPanel
{
    static GUIStyle _style;
    static Texture2D _bg;

    public static void Draw()
    {
        float sw = Screen.width, sh = Screen.height;
        int fontSize = Mathf.Max(12, Mathf.RoundToInt(sh / 1080f * 19f));
        if (_bg == null)
        {
            _bg = new Texture2D(1, 1);
            _bg.SetPixel(0, 0, new Color(0.09f, 0.06f, 0.08f, 0.92f));
            _bg.Apply();
            _bg.hideFlags = HideFlags.HideAndDontSave;
        }
        if (_style == null || _style.fontSize != fontSize)
            _style = new GUIStyle(GUI.skin.label) { fontSize = fontSize, richText = true, wordWrap = false };

        var lines = new List<string> { "<color=#F2C14E><b>Evolution helper</b></color>  <color=#9A8F99>F8 to hide</color>" };
        foreach (var o in Evo.Offers)
        {
            if (o == null) continue;
            lines.Add($"<b>{o.Self.Name}</b>");
            foreach (var e in o.Evos.Where(e => e.Status != EvoStatus.HaveNone))
                lines.Add($"   <color=#8FE388>{e.Result.Name}</color> with {string.Join(" + ", e.Partners.Select(p => p.Name))}");
        }

        float lineH = fontSize * 1.35f;
        var box = new Rect(sw * 0.515f, sh * 0.47f, sw * 0.445f, Mathf.Min(sh * 0.47f, (lines.Count + 1) * lineH));
        GUI.DrawTexture(box, _bg);
        float y = box.y + lineH * 0.5f;
        foreach (var l in lines)
        {
            if (y + lineH > box.yMax) break;
            GUI.Label(new Rect(box.x + fontSize, y, box.width - fontSize * 2, lineH), l, _style);
            y += lineH;
        }
    }
}

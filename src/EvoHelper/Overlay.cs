using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EvoHelper;

// Draws the helper panel with Unity's immediate-mode GUI while the
// "Select an upgrade" page is open. F8 hides/shows it.
public class Overlay : MonoBehaviour
{
    public Overlay(IntPtr ptr) : base(ptr) { }

    const string Green = "#8FE388";
    const string Gold = "#F2C14E";
    const string Grey = "#9A8F99";
    const string White = "#F3ECE4";

    static bool _hidden;
    static bool _errorLogged;

    GUIStyle _style;
    Texture2D _bg;
    int _styleFontSize;

    static bool ShouldShow()
    {
        var ui = LevelUpUI.I;
        if (ui == null || !ui.gameObject.activeInHierarchy) return false;
        if (ui.CurPage != LevelUpPage.kPickUpgrade) return false;
        return Evo.HasOffers;
    }

    void EnsureStyle(int fontSize)
    {
        if (_bg == null)
        {
            _bg = new Texture2D(1, 1);
            _bg.SetPixel(0, 0, new Color(0.09f, 0.06f, 0.08f, 0.92f));
            _bg.Apply();
            _bg.hideFlags = HideFlags.HideAndDontSave;
        }
        if (_style == null || _styleFontSize != fontSize)
        {
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                richText = true,
                wordWrap = false,
                alignment = TextAnchor.UpperLeft,
            };
            _style.normal.textColor = Color.white;
            _styleFontSize = fontSize;
        }
    }

    void OnGUI()
    {
        try
        {
            var ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.F8)
            {
                _hidden = !_hidden;
                ev.Use();
            }
            if (_hidden || !ShouldShow()) return;
            Draw();
        }
        catch (Exception e)
        {
            if (!_errorLogged) { _errorLogged = true; Plugin.Log.LogError("Overlay draw failed: " + e); }
        }
    }

    void Draw()
    {
        float sw = Screen.width, sh = Screen.height;
        int fontSize = Mathf.Max(12, Mathf.RoundToInt(sh / 1080f * 19f));
        EnsureStyle(fontSize);
        float lineH = fontSize * 1.35f;

        // Lower part of the right-hand description panel, which is otherwise empty.
        var area = new Rect(sw * 0.515f, sh * 0.47f, sw * 0.445f, sh * 0.47f);
        int maxChars = Mathf.Max(30, Mathf.FloorToInt(area.width / (fontSize * 0.52f)));

        var lines = BuildLines(maxChars);
        float height = Mathf.Min(area.height, lines.Count * lineH + lineH);
        var box = new Rect(area.x, area.y, area.width, height);

        GUI.DrawTexture(box, _bg);
        float y = box.y + lineH * 0.5f;
        foreach (var line in lines)
        {
            if (y + lineH > box.yMax) break;
            GUI.Label(new Rect(box.x + fontSize, y, box.width - fontSize * 2, lineH), line, _style);
            y += lineH;
        }
    }

    static string C(string color, string text) => $"<color={color}>{text}</color>";
    static string B(string text) => $"<b>{text}</b>";

    static string PartnerText(Partner p) =>
        p.Owned ? C(Green, $"{p.Name} Lv{p.Lvl}") : C(Grey, p.Name);

    static List<string> BuildLines(int maxChars)
    {
        var lines = new List<string>();
        lines.Add(C(Gold, B("Evolution helper")) + C(Grey, "   F8 to hide"));

        foreach (var o in Evo.Offers)
        {
            if (o == null) continue;
            string tag = o.IsNew ? "new " + o.Kind : $"{o.Kind}, you have Lv{o.OwnedLvl}";
            lines.Add(C(White, B(o.Name)) + C(Grey, $"  ({tag})"));

            var useful = o.Evos.Where(e => e.Status != EvoStatus.HaveNone).ToList();
            var other = o.Evos.Where(e => e.Status == EvoStatus.HaveNone).ToList();

            if (o.Evos.Count == 0)
                lines.Add("   " + C(Grey, "no evolutions"));

            foreach (var e in useful)
            {
                string mark = e.Status == EvoStatus.HaveAll ? C(Green, "★ ") : C(Gold, "◐ ");
                string partners = string.Join(" + ", e.Partners.Select(PartnerText));
                lines.Add("   " + mark + C(e.Status == EvoStatus.HaveAll ? Green : Gold, B(e.Result)) + C(Grey, " with ") + partners);
            }

            if (other.Count > 0)
            {
                var parts = other.Select(e => $"{e.Result} ({string.Join("+", e.Partners.Select(p => p.Name))})");
                foreach (var w in Wrap((useful.Count > 0 ? "also: " : "needs: ") + string.Join(", ", parts), maxChars - 3))
                    lines.Add("   " + C(Grey, w));
            }
        }

        if (Evo.Pairs.Count > 0)
        {
            lines.Add("");
            lines.Add(C(Gold, B("You can already make")) + C(Grey, $"  (all ingredients Lv{Evo.EvoLevel}, then a Fusion Reactor)"));
            foreach (var p in Evo.Pairs)
            {
                string parts = string.Join(" + ", p.Parts.Select(x =>
                    C(x.Lvl >= Evo.EvoLevel ? Green : White, $"{x.Name} Lv{x.Lvl}/{Evo.EvoLevel}")));
                lines.Add("   " + C(Green, B(p.Result)) + C(Grey, ": ") + parts);
            }
        }
        return lines;
    }

    static IEnumerable<string> Wrap(string text, int width)
    {
        var line = "";
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line;
                line = word;
            }
            else line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) yield return line;
    }
}

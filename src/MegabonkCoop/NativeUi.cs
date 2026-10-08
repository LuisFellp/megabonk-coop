using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MegabonkCoop
{
    // Native widgets (uGUI Canvas + TextMeshPro with the GAME'S OWN font) built at runtime, replacing IMGUI.
    // Everything is positioned in a 1920x1080 reference space, top-left origin (the CanvasScaler does the rest).
    // Clicks are handled here (mouse position vs rect) so we do not depend on the game's EventSystem; our Image
    // panels still have raycastTarget so the game's own buttons behind a window do not receive the click.
    public static class NUi
    {
        public const float W = 1920f, H = 1080f;
        public static bool Ready => root != null && font != null;
        static GameObject root; static Canvas canvas; static TMP_FontAsset font;
        static float nextTry; static bool failLogged;
        static readonly List<El> buttons = new List<El>();

        public class El
        {
            public GameObject go; public RectTransform rt; public Image img; public RawImage raw; public TextMeshProUGUI tmp;
            public float ax, ay, w, h;                 // absolute position (reference space) and size
            public Action onClick; public Color normal, hover; public bool isButton;
            public bool Visible { get => go != null && go.activeSelf; set { if (go != null && go.activeSelf != value) go.SetActive(value); } }
            public void SetText(string s) { if (tmp == null) return; s = Clean(s); if (tmp.text != s) tmp.text = s; }
            public void SetColor(Color c) { if (tmp != null) tmp.color = c; }
            public void SetBg(Color c) { if (img != null) img.color = c; if (raw != null) raw.color = c; normal = c; }
            public void SetPos(float x, float y) { rt.anchoredPosition = new Vector2(x, -y); ax = x; ay = y; }
            public void SetSize(float w2, float h2) { rt.sizeDelta = new Vector2(w2, h2); w = w2; h = h2; }
            public void SetRot(float degClockwise) { rt.localRotation = Quaternion.Euler(0f, 0f, -degClockwise); }
            public void SetPivotCenter() { rt.pivot = new Vector2(0.5f, 0.5f); }
            public void Destroy() { if (go != null) UnityEngine.Object.Destroy(go); buttons.Remove(this); }
        }

        // The game's pixel font lacks many characters (arrows, emoji, symbols that appear in Steam names / chat). TMP then logs a warning EVERY time
        // the text is rebuilt (2870 in one session) and draws a square. Replace what the font cannot draw with '?'.
        static readonly Dictionary<string, string> cleanCache = new Dictionary<string, string>();
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (cleanCache.TryGetValue(s, out var c)) return c;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
            {
                bool ok = ch >= ' ' && ch <= '~';
                if (!ok && ch > '~')
                {
                    try { ok = font != null && font.HasCharacter(ch); } catch { ok = ch <= (char)0x24F; }       // fallback: Latin letters with accents
                }
                if (ch == '\n') ok = true;
                sb.Append(ok ? ch : '?');
            }
            c = sb.ToString();
            if (cleanCache.Count > 600) cleanCache.Clear();
            cleanCache[s] = c;
            return c;
        }

        // Called every frame by the plugin. Returns true when the native UI is usable.
        public static bool Ensure()
        {
            if (Ready) return true;
            float now = Time.realtimeSinceStartup;
            if (now < nextTry) return false;
            nextTry = now + 1.5f;
            try
            {
                if (font == null)
                {
                    try { font = TMP_Settings.defaultFontAsset; } catch { }
                    if (font == null) { var any = UnityEngine.Object.FindObjectOfType<TextMeshProUGUI>(); if (any != null) font = any.font; }
                    if (font == null) { if (!failLogged) { failLogged = true; Plugin.Info("[nui] no TMP font found yet (will retry)"); } return false; }
                }
                if (root == null)
                {
                    root = new GameObject("CoopNativeUI");
                    UnityEngine.Object.DontDestroyOnLoad(root);
                    canvas = root.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 30000;
                    var cs = root.AddComponent<CanvasScaler>();
                    cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    cs.referenceResolution = new Vector2(W, H);
                    cs.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                    cs.matchWidthOrHeight = 0.5f;
                    root.AddComponent<GraphicRaycaster>();
                    Plugin.Info($"[nui] canvas created, font='{font.name}'");
                }
                return true;
            }
            catch (Exception e) { if (!failLogged) { failLogged = true; Plugin.Info("[nui] init failed: " + e.Message); } return false; }
        }

        public static El Group(El parent, float x, float y, float w, float h)
        {
            var e = New("g", parent, x, y, w, h);
            return e;
        }

        static El New(string name, El parent, float x, float y, float w, float h)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            go.transform.SetParent(parent != null ? parent.go.transform : root.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return new El { go = go, rt = rt, ax = (parent != null ? parent.ax : 0f) + x, ay = (parent != null ? parent.ay : 0f) + y, w = w, h = h };
        }

        public static El Box(El parent, float x, float y, float w, float h, Color c, bool block = true)
        {
            var e = New("box", parent, x, y, w, h);
            e.img = e.go.AddComponent<Image>();
            e.img.color = c; e.img.raycastTarget = block; e.normal = c;
            return e;
        }

        static Texture2D tri;
        // a white triangle pointing UP, drawn procedurally (no asset needed; Sprite.Create is stripped in this game's IL2CPP build)
        public static Texture2D Triangle()
        {
            if (tri != null) return tri;
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float half = (n - 1 - y) / (float)(n - 1) * (n / 2f);
                    t.SetPixel(x, y, Mathf.Abs(x - n / 2f + 0.5f) <= half ? Color.white : new Color(1f, 1f, 1f, 0f));
                }
            t.Apply(); t.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(t);
            tri = t;
            return tri;
        }

        // Arrow icon (centered on its position, rotatable)
        public static El Arrow(El parent, float size, Color c)
        {
            var e = New("arrow", parent, 0, 0, size, size);
            e.rt.pivot = new Vector2(0.5f, 0.5f);
            e.raw = e.go.AddComponent<RawImage>();
            e.raw.texture = Triangle(); e.raw.color = c; e.raw.raycastTarget = false; e.normal = c;
            return e;
        }

        public static Vector2 ScreenToRef(Vector2 sp)
        {
            float sf = canvas != null && canvas.scaleFactor > 0.01f ? canvas.scaleFactor : 1f;
            return new Vector2(sp.x / sf, (Screen.height - sp.y) / sf);
        }
        public static float RefW => Screen.width / (canvas != null && canvas.scaleFactor > 0.01f ? canvas.scaleFactor : 1f);
        public static float RefH => Screen.height / (canvas != null && canvas.scaleFactor > 0.01f ? canvas.scaleFactor : 1f);

        public static El Text(El parent, float x, float y, float w, float h, string s, float size, Color c, TextAlignmentOptions al = TextAlignmentOptions.Left)
        {
            var e = New("txt", parent, x, y, w, h);
            e.tmp = e.go.AddComponent<TextMeshProUGUI>();
            e.tmp.font = font;
            e.tmp.fontSize = size;
            e.tmp.color = c;
            e.tmp.alignment = al;
            e.tmp.enableWordWrapping = false;
            e.tmp.overflowMode = TextOverflowModes.Ellipsis;
            e.tmp.raycastTarget = false;
            e.tmp.text = Clean(s);
            return e;
        }

        public static El Button(El parent, float x, float y, float w, float h, string s, Action onClick, Color bg, float size = 30f)
        {
            var e = Box(parent, x, y, w, h, bg);
            e.isButton = true; e.onClick = onClick; e.normal = bg;
            e.hover = Color.Lerp(bg, Color.white, 0.22f);
            var t = Text(e, 0, 0, w, h, s, size, Color.white, TextAlignmentOptions.Center);
            e.tmp = t.tmp;                      // so SetText changes the label
            buttons.Add(e);
            return e;
        }

        // Mouse position in reference space (top-left origin).
        public static Vector2 Mouse()
        {
            float sf = canvas != null && canvas.scaleFactor > 0.01f ? canvas.scaleFactor : 1f;
            Vector3 m = Input.mousePosition;
            return new Vector2(m.x / sf, (Screen.height - m.y) / sf);
        }

        public static bool Over(El e) { var m = Mouse(); return e.go != null && e.go.activeInHierarchy && m.x >= e.ax && m.x <= e.ax + e.w && m.y >= e.ay && m.y <= e.ay + e.h; }

        // hover colours + clicks (only for buttons whose whole parent chain is active)
        public static void Tick(bool allowClicks)
        {
            if (!Ready) return;
            bool down = allowClicks && Input.GetMouseButtonDown(0);
            El clicked = null;
            for (int i = buttons.Count - 1; i >= 0; i--)
            {
                var b = buttons[i];
                if (b.go == null) { buttons.RemoveAt(i); continue; }
                if (!b.go.activeInHierarchy) continue;
                bool over = Over(b);
                if (b.img != null) b.img.color = over ? b.hover : b.normal;
                if (over && down && clicked == null) clicked = b;
            }
            if (clicked != null && clicked.onClick != null)
            {
                try { clicked.onClick(); } catch (Exception e) { Plugin.Info("[nui] click handler failed: " + e.Message); }
            }
        }

        public static void Shutdown() { if (root != null) UnityEngine.Object.Destroy(root); root = null; buttons.Clear(); }
    }
}

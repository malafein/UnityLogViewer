using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UnityLogViewer
{
    public class LogViewerUI : MonoBehaviour
    {
        private bool isVisible = Plugin.ShowWindow.Value;
        private bool isPinned;
        private Rect windowRect;
        private Vector2 scrollPosition;
        private string filterText = "";
        private bool filterDirty = true;

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> filteredLines = new List<string>();
        private bool autoScroll = true;
        private bool pendingScrollToBottom;

        // Pre-built rich text lines for virtual scroll
        private readonly List<string> renderedLines = new List<string>();
        private bool renderDirty = true;

        // Compiled highlight rules (rebuilt when config changes)
        private readonly List<HighlightRule> highlightRules = new List<HighlightRule>();
        private bool highlightsDirty = true;

        private int MaxLines => Plugin.BufferSize.Value;
        private const int WindowID = 99701;

        // Approximate header heights (title bar + filter row + font row + info row; pinned unpin row)
        // Used to compute the scroll view height so GUILayout.Window doesn't expand to content.
        private const float WindowHeaderHeight = 98f;
        private const float PinnedHeaderHeight = 26f;

        // Line height is measured from the style in InitStyles and used for virtual scroll.
        private float lineHeight = 20f;

        // Visible range computed during EventType.Layout and reused during EventType.Repaint
        // to guarantee identical GUILayout entry counts across both passes.
        private int scrollFirstVisible;
        private int scrollLastVisible;

        // Resize state
        private bool isResizing;
        private const float ResizeHandleSize = 20f;
        private const float MinWidth = 400f;
        private const float MinHeight = 300f;

        // Background texture
        private Texture2D bgTexture;
        private int lastOpacity = -1;

        // Styles
        private GUIStyle logStyle;
        private GUIStyle closeButtonStyle;
        private GUIStyle pinButtonStyle;
        private GUIStyle pinnedWindowStyle;
        private bool stylesInitialized;

        private struct HighlightRule
        {
            public Regex Pattern;
            public string Color;
        }

        // Tracks last-saved rect to avoid writing config every frame
        private Rect lastSavedRect;

        // Font selector — built in InitStyles() using CalcSize probes in OnGUI context.
        // validFontNames[0] is always "" (the default GUI skin font).
        private readonly List<string> validFontNames = new List<string>();
        private int fontSelectorIndex;

        // Font dropdown popup state
        private bool fontDropdownOpen;
        private Rect fontDropdownRect;
        private Vector2 fontDropdownScrollPos;
        private const int FontDropdownWindowID = 99702;

        private void Awake()
        {
            windowRect = new Rect(
                Plugin.WindowX.Value,
                Plugin.WindowY.Value,
                Plugin.WindowWidth.Value,
                Plugin.WindowHeight.Value);
            lastSavedRect = windowRect;

            filterText = Plugin.Filter.Value ?? "";
            isPinned = Plugin.Pinned.Value;

            Plugin.Filter.SettingChanged += (_, __) =>
            {
                filterText = Plugin.Filter.Value ?? "";
                filterDirty = true;
            };

            Plugin.ShowWindow.SettingChanged += (_, __) =>
            {
                isVisible = Plugin.ShowWindow.Value;
            };

            Plugin.Pinned.SettingChanged += (_, __) =>
            {
                isPinned = Plugin.Pinned.Value;
            };

            Plugin.WindowX.SettingChanged += (_, __) => { windowRect.x = Plugin.WindowX.Value; };
            Plugin.WindowY.SettingChanged += (_, __) => { windowRect.y = Plugin.WindowY.Value; };
            Plugin.WindowWidth.SettingChanged += (_, __) => { windowRect.width = Plugin.WindowWidth.Value; };
            Plugin.WindowHeight.SettingChanged += (_, __) => { windowRect.height = Plugin.WindowHeight.Value; };

            for (int i = 0; i < Plugin.HighlightSlotCount; i++)
            {
                Plugin.HighlightPattern[i].SettingChanged += (_, __) => { highlightsDirty = true; renderDirty = true; };
                Plugin.HighlightColor[i].SettingChanged += (_, __) => { highlightsDirty = true; renderDirty = true; };
            }

            // FontName changes from an external config editor: sync the selector index.
            // If styles aren't initialized yet (viewer hasn't been opened), the index will
            // be resolved when InitStyles runs and builds the validated font list.
            Plugin.FontName.SettingChanged += (_, __) =>
            {
                int idx = validFontNames.IndexOf(Plugin.FontName.Value);
                if (idx >= 0) fontSelectorIndex = idx;
            };

            Application.logMessageReceived += OnLogMessageReceived;
        }

        // Probes all OS fonts using CalcSize (requires OnGUI context) to determine which
        // ones Unity can actually load and render. Fonts that fail generate warnings, which
        // we capture via a temporary lambda. Only passing fonts are added to validFontNames.
        // Called once from InitStyles(); results are used by the in-window font selector.
        private void BuildValidFontList()
        {
            validFontNames.Clear();
            validFontNames.Add("");  // index 0 = default GUI skin font

            string[] all = Font.GetOSInstalledFontNames();
            Array.Sort(all, StringComparer.OrdinalIgnoreCase);

            // OnLogMessageReceived must not be subscribed during probing or any warning
            // from a failing font would be captured into our log buffer and trigger a rebuild.
            Application.logMessageReceived -= OnLogMessageReceived;

            foreach (string name in all)
            {
                bool warned = false;
                Application.LogCallback probe = (msg, _, type) =>
                {
                    if (type == LogType.Warning || type == LogType.Error) warned = true;
                };

                Application.logMessageReceived += probe;
                Font font = Font.CreateDynamicFontFromOSFont(name, 13);
                // CalcSize forces Unity to compute text metrics, which triggers the font
                // face load in the same code path as actual rendering — catching failures
                // that only surface lazily at render time rather than at creation time.
                new GUIStyle(GUI.skin.label) { font = font, fontSize = 13 }
                    .CalcSize(new GUIContent("Ag"));
                Application.logMessageReceived -= probe;

                if (!warned)
                    validFontNames.Add(name);
            }

            Application.logMessageReceived += OnLogMessageReceived;

            // Set selector to match the persisted FontName, falling back to default.
            fontSelectorIndex = Math.Max(0, validFontNames.IndexOf(Plugin.FontName.Value));

            var sb = new System.Text.StringBuilder();
            for (int i = 1; i < validFontNames.Count; i++)
            {
                if (i > 1) sb.Append(", ");
                sb.Append(validFontNames[i]);
            }
            Plugin.Log.LogInfo(validFontNames.Count > 1
                ? $"[UnityLogViewer] {validFontNames.Count - 1} usable font(s): {sb}"
                : "[UnityLogViewer] No usable OS fonts found; using default UI font.");
        }

        private void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            string prefix;
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                    prefix = "[ERROR] ";
                    break;
                case LogType.Warning:
                    prefix = "[WARNING] ";
                    break;
                default:
                    prefix = "";
                    break;
            }

            logLines.Add(prefix + message);

            if (logLines.Count > MaxLines)
            {
                logLines.RemoveRange(0, logLines.Count - MaxLines);
            }

            filterDirty = true;

            if (autoScroll)
                pendingScrollToBottom = true;
        }

        private void Update()
        {
            if (Plugin.ToggleShortcut.Value.IsDown())
                Plugin.ShowWindow.Value = !Plugin.ShowWindow.Value;

            // Rebuild filtered/rendered lines here, not inside OnGUI callbacks.
            // OnLogMessageReceived can fire synchronously during OnGUI (Unity logs the
            // GUILayout exception, which triggers our callback, which sets filterDirty).
            // If we rebuild inside DrawVirtualScrollContent, the scroll view's entry count
            // changes between the Layout and Repaint events → GUILayout mismatch →
            // exception → more log messages → feedback loop of spam and misrendering.
            // Rebuilding in Update() ensures renderedLines is frozen for the entire frame.
            if (filterDirty) RebuildFilteredLines();
            if (renderDirty) RebuildRenderedLines();

            if (pendingScrollToBottom)
            {
                scrollPosition.y = 1e9f;
                pendingScrollToBottom = false;
            }
        }

        private void RebuildHighlightRules()
        {
            highlightRules.Clear();
            for (int i = 0; i < Plugin.HighlightSlotCount; i++)
            {
                string pattern = Plugin.HighlightPattern[i].Value;
                string color = Plugin.HighlightColor[i].Value;
                if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(color)) continue;

                try
                {
                    highlightRules.Add(new HighlightRule
                    {
                        Pattern = new Regex(pattern, RegexOptions.IgnoreCase),
                        Color = color
                    });
                }
                catch (ArgumentException)
                {
                    // Skip invalid regex
                }
            }
            highlightsDirty = false;
        }

        private void RebuildFilteredLines()
        {
            filteredLines.Clear();
            if (string.IsNullOrEmpty(filterText))
            {
                filteredLines.AddRange(logLines);
            }
            else
            {
                try
                {
                    var regex = new Regex(filterText, RegexOptions.IgnoreCase);
                    for (int i = 0; i < logLines.Count; i++)
                    {
                        if (regex.IsMatch(logLines[i]))
                            filteredLines.Add(logLines[i]);
                    }
                }
                catch (ArgumentException)
                {
                    for (int i = 0; i < logLines.Count; i++)
                    {
                        if (logLines[i].IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0)
                            filteredLines.Add(logLines[i]);
                    }
                }
            }
            filterDirty = false;
            renderDirty = true;
        }

        private static string EscapeRichText(string text)
        {
            return text.Replace("<", "\u200B<");
        }

        private string GetColorForLine(string line)
        {
            for (int i = 0; i < highlightRules.Count; i++)
            {
                if (highlightRules[i].Pattern.IsMatch(line))
                    return highlightRules[i].Color;
            }
            return null;
        }

        private void RebuildRenderedLines()
        {
            if (highlightsDirty) RebuildHighlightRules();

            renderedLines.Clear();
            for (int i = 0; i < filteredLines.Count; i++)
            {
                string escaped = EscapeRichText(filteredLines[i]);
                string color = GetColorForLine(filteredLines[i]);
                renderedLines.Add(color != null
                    ? "<color=" + color + ">" + escaped + "</color>"
                    : escaped);
            }
            renderDirty = false;
        }

        private Texture2D MakeBgTexture(int opacity)
        {
            byte alpha = (byte)(opacity * 255 / 100);
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, new Color32(0, 0, 0, alpha));
            tex.Apply();
            return tex;
        }

        private void InitStyles()
        {
            // Must run before logStyle is created so CalcSize probes use the correct skin.
            BuildValidFontList();

            string selectedFont = fontSelectorIndex > 0 && fontSelectorIndex < validFontNames.Count
                ? validFontNames[fontSelectorIndex] : null;

            logStyle = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                wordWrap = false,
                fontSize = 13,
                normal = { textColor = Color.white },
                padding = new RectOffset(4, 4, 2, 2),
                margin = new RectOffset(0, 0, 0, 0),
                font = string.IsNullOrEmpty(selectedFont)
                    ? null
                    : Font.CreateDynamicFontFromOSFont(selectedFont, 13)
            };

            lineHeight = logStyle.CalcSize(new GUIContent("Ag")).y;

            closeButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

            pinButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter
            };

            pinnedWindowStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(4, 4, 4, 4)
            };

            stylesInitialized = true;
        }

        private void UpdateBgTexture()
        {
            int opacity = Plugin.BackgroundOpacity.Value;
            if (opacity != lastOpacity)
            {
                if (bgTexture != null) Destroy(bgTexture);
                bgTexture = MakeBgTexture(opacity);
                lastOpacity = opacity;

                if (stylesInitialized)
                    pinnedWindowStyle.normal.background = bgTexture;
            }
        }

        private void OnGUI()
        {
            if (!isVisible)
            {
                fontDropdownOpen = false;
                return;
            }
            if (!stylesInitialized) InitStyles();

            UpdateBgTexture();

            if (isPinned)
            {
                DrawPinnedWindow();
            }
            else
            {
                Color prevBg = GUI.backgroundColor;
                float alpha = Plugin.BackgroundOpacity.Value / 100f;
                GUI.backgroundColor = new Color(prevBg.r, prevBg.g, prevBg.b, alpha);
                GUI.Window(WindowID, windowRect, DrawWindow, "Log Viewer");
                GUI.backgroundColor = prevBg;

                HandleResize();
            }

            SaveWindowRectIfChanged();

            // Draw dropdown on top of everything else (after main window).
            if (fontDropdownOpen)
                DrawFontDropdown();
        }

        private void SaveWindowRectIfChanged()
        {
            if (windowRect.x != lastSavedRect.x || windowRect.y != lastSavedRect.y ||
                windowRect.width != lastSavedRect.width || windowRect.height != lastSavedRect.height)
            {
                Plugin.WindowX.Value = (int)windowRect.x;
                Plugin.WindowY.Value = (int)windowRect.y;
                Plugin.WindowWidth.Value = (int)windowRect.width;
                Plugin.WindowHeight.Value = (int)windowRect.height;
                lastSavedRect = windowRect;
            }
        }

        // Virtual scroll: renders only the lines visible in the viewport.
        // viewHeight is passed explicitly so the scroll view is constrained to the remaining
        // window space rather than expanding to fit all content.
        // GUILayout.Space fills the virtual height above and below the visible lines so the
        // scrollbar reflects the true buffer size.
        // scrollPosition.y is clamped before use — passing float.MaxValue to GUILayout
        // corrupts internal layout state and causes spurious log messages.
        private void DrawVirtualScrollContent(float viewHeight)
        {
            float lh = lineHeight;
            int totalLines = renderedLines.Count;
            float totalContentHeight = totalLines * lh;

            // Clamp before GUILayout sees it.
            scrollPosition.y = Mathf.Min(scrollPosition.y, Mathf.Max(0f, totalContentHeight - viewHeight));

            // Compute the visible range ONLY during Layout and cache it.
            // GUILayout.BeginScrollView can modify scrollPosition.y during Layout (internal
            // clamping), so recalculating during Repaint can yield a different firstVisible
            // index — changing the number of Label/Space calls by 1 and triggering the
            // "Getting control N's position in a group with only N controls" mismatch error.
            // Reusing the Layout-pass values in Repaint guarantees identical entry counts.
            if (Event.current.type == EventType.Layout)
            {
                scrollFirstVisible = Mathf.Max(0, (int)(scrollPosition.y / lh) - 1);
                scrollLastVisible = Mathf.Min(totalLines - 1, scrollFirstVisible + (int)(viewHeight / lh) + 3);
            }

            scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.Height(viewHeight));

            if (scrollFirstVisible > 0)
                GUILayout.Space(scrollFirstVisible * lh);

            for (int i = scrollFirstVisible; i <= scrollLastVisible; i++)
                GUILayout.Label(renderedLines[i], logStyle);

            int linesAfter = totalLines - scrollLastVisible - 1;
            if (linesAfter > 0)
                GUILayout.Space(linesAfter * lh);

            GUILayout.EndScrollView();
        }

        private void DrawPinnedWindow()
        {
            pinnedWindowStyle.normal.background = bgTexture;

            GUILayout.BeginArea(windowRect, pinnedWindowStyle);

            // Unpin button — pure GUILayout to avoid mixing GUI/GUILayout inside BeginArea.
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("\u25a1", pinButtonStyle, GUILayout.Width(26), GUILayout.Height(18)))
            {
                isPinned = false;
                Plugin.Pinned.Value = false;
            }
            GUILayout.EndHorizontal();

            DrawVirtualScrollContent(Mathf.Max(50f, windowRect.height - PinnedHeaderHeight));

            GUILayout.EndArea();
        }

        private void ApplyFont(int index)
        {
            fontSelectorIndex = index;
            string name = index > 0 && index < validFontNames.Count ? validFontNames[index] : null;
            Plugin.FontName.Value = name ?? "";
            logStyle.font = string.IsNullOrEmpty(name) ? null : Font.CreateDynamicFontFromOSFont(name, 13);
            lineHeight = logStyle.CalcSize(new GUIContent("Ag")).y;
            renderDirty = true;
        }

        private void DrawWindow(int windowID)
        {
            // Close button
            if (GUI.Button(new Rect(windowRect.width - 25, 2, 22, 18), "X", closeButtonStyle))
            {
                Plugin.ShowWindow.Value = false;
                return;
            }

            // Pin button (next to close)
            if (GUI.Button(new Rect(windowRect.width - 53, 2, 26, 18), "\u25a0", pinButtonStyle))
            {
                isPinned = true;
                Plugin.Pinned.Value = true;
                return;
            }

            // Font selector row
            if (validFontNames.Count > 1)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Font:", GUILayout.Width(40));
                string displayName = fontSelectorIndex > 0 ? validFontNames[fontSelectorIndex] : "(default)";
                if (GUILayout.Button(displayName + " \u25bc", GUILayout.ExpandWidth(true)))
                {
                    fontDropdownOpen = !fontDropdownOpen;
                    if (fontDropdownOpen)
                    {
                        // btnRect is window-content-local; convert to screen space by adding
                        // the window's origin and the title bar height (window.border.top).
                        Rect btnRect = GUILayoutUtility.GetLastRect();
                        float titleH = GUI.skin.window.border.top;
                        float dropH = Mathf.Min(validFontNames.Count * 22f + 6f, 200f);
                        fontDropdownRect = new Rect(
                            windowRect.x + btnRect.x,
                            windowRect.y + titleH + btnRect.yMax,
                            btnRect.width,
                            dropH);
                    }
                }
                GUILayout.EndHorizontal();
            }

            // Filter row
            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter:", GUILayout.Width(40));
            string newFilter = GUILayout.TextField(filterText);
            if (newFilter != filterText)
            {
                filterText = newFilter;
                filterDirty = true;
            }
            // ⌫ clears the filter text
            if (GUILayout.Button("\u232b", GUILayout.Width(28)))
            {
                filterText = "";
                filterDirty = true;
            }
            // Clear clears the log buffer
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                logLines.Clear();
                filteredLines.Clear();
                renderedLines.Clear();
                scrollPosition = Vector2.zero;
            }
            GUILayout.EndHorizontal();

            // Info row
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{filteredLines.Count} / {logLines.Count} lines", GUILayout.Width(150));
            GUILayout.FlexibleSpace();
            bool newAutoScroll = GUILayout.Toggle(autoScroll, "Auto-scroll");
            if (newAutoScroll != autoScroll)
            {
                autoScroll = newAutoScroll;
                if (autoScroll) pendingScrollToBottom = true;
            }
            GUILayout.EndHorizontal();

            float headerH = validFontNames.Count > 1 ? WindowHeaderHeight : WindowHeaderHeight - 26f;
            DrawVirtualScrollContent(Mathf.Max(50f, windowRect.height - headerH));

            // Resize handle
            GUI.Label(new Rect(
                windowRect.width - ResizeHandleSize,
                windowRect.height - ResizeHandleSize,
                ResizeHandleSize,
                ResizeHandleSize), "\u25e2");

            // Dragging (header area, excluding buttons)
            GUI.DragWindow(new Rect(0, 0, windowRect.width - 58, 25));
        }

        private void DrawFontDropdown()
        {
            // Close when clicking outside the dropdown window.
            if (Event.current.type == EventType.MouseDown &&
                !fontDropdownRect.Contains(Event.current.mousePosition))
            {
                fontDropdownOpen = false;
                return;
            }

            fontDropdownRect = GUI.Window(FontDropdownWindowID, fontDropdownRect, id =>
            {
                fontDropdownScrollPos = GUILayout.BeginScrollView(fontDropdownScrollPos);
                for (int i = 0; i < validFontNames.Count; i++)
                {
                    string label = i == 0 ? "(default)" : validFontNames[i];
                    if (GUILayout.Button(label, GUILayout.ExpandWidth(true)))
                    {
                        ApplyFont(i);
                        fontDropdownOpen = false;
                    }
                }
                GUILayout.EndScrollView();
            }, "");
        }

        private void HandleResize()
        {
            Event e = Event.current;
            Rect screenResizeRect = new Rect(
                windowRect.x + windowRect.width - ResizeHandleSize,
                windowRect.y + windowRect.height - ResizeHandleSize,
                ResizeHandleSize,
                ResizeHandleSize);

            if (e.type == EventType.MouseDown && screenResizeRect.Contains(e.mousePosition))
            {
                isResizing = true;
                e.Use();
            }

            if (isResizing)
            {
                if (e.type == EventType.MouseDrag || e.type == EventType.MouseUp)
                {
                    windowRect.width = Mathf.Max(MinWidth, e.mousePosition.x - windowRect.x);
                    windowRect.height = Mathf.Max(MinHeight, e.mousePosition.y - windowRect.y);

                    if (e.type == EventType.MouseUp)
                        isResizing = false;

                    e.Use();
                }
            }
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLogMessageReceived;
            if (bgTexture != null) Destroy(bgTexture);
        }
    }
}

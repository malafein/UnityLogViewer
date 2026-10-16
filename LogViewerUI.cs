using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace malafein.UnityLogViewer
{
    public class LogViewerUI : MonoBehaviour, ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;
        private readonly object logLock = new object();

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

        // Invalid highlight colours are reported once the value has stopped changing:
        // Configuration Manager sets the entry on every keystroke. Checked at startup too.
        private const float ColorCheckDelay = 1f;
        private float colorCheckAt;

        // Set by the Clear button, applied in Update(): clearing inside OnGUI would change
        // renderedLines between the Layout and Repaint passes of the same frame.
        private bool clearRequested;

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
        // The probed Font objects, parallel to validFontNames ([0] = null, the skin font).
        private readonly List<Font> validFonts = new List<Font>();
        // Font list rows: the name in the skin font (symbol fonts can't spell their own name)
        // and a sample in the font itself, on solid textures: the skin's button image is
        // see-through in some games (PEAK), and BackgroundOpacity tints it further.
        private const string FontSample = "[Info] AaBbCc 0123 {}[]";
        private readonly List<GUIStyle> fontSampleStyles = new List<GUIStyle>();
        private GUIStyle fontRowStyle;
        private GUIStyle fontNameStyle;
        private Texture2D fontRowTexture;
        private Texture2D fontRowHoverTexture;
        private int fontSelectorIndex;

        // Font list, drawn inside the main window in place of the log while open. A separate
        // popup window never showed in PEAK (IMGUI window stacking). Opening, closing and
        // applying a font are requested from OnGUI and done in Update(), so the layout doesn't
        // change between the Layout and Repaint passes of a frame.
        private bool fontListOpen;
        private bool fontListToggleRequested;
        private int pendingFontIndex = -1;
        private Vector2 fontListScrollPos;

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
                Plugin.HighlightColor[i].SettingChanged += (_, __) =>
                {
                    highlightsDirty = true;
                    renderDirty = true;
                    colorCheckAt = Time.unscaledTime + ColorCheckDelay;
                };
            }

            // FontName changes from an external config editor: sync the selector index.
            // If styles aren't initialized yet (viewer hasn't been opened), the index will
            // be resolved when InitStyles runs and builds the validated font list.
            Plugin.FontName.SettingChanged += (_, __) =>
            {
                int idx = validFontNames.IndexOf(Plugin.FontName.Value);
                if (idx >= 0) fontSelectorIndex = idx;
            };

            BepInEx.Logging.Logger.Listeners.Add(this);
        }

        // Probes all OS fonts using CalcSize (requires OnGUI context) to determine which
        // ones Unity can actually load and render. Fonts that fail generate warnings, which
        // we capture via a temporary lambda. Only passing fonts are added to validFontNames.
        // Called once from InitStyles(); results are used by the in-window font selector.
        private void BuildValidFontList()
        {
            validFontNames.Clear();
            validFontNames.Add("");  // index 0 = default GUI skin font
            validFonts.Clear();
            validFonts.Add(null);

            string[] all = Font.GetOSInstalledFontNames();
            Array.Sort(all, StringComparer.OrdinalIgnoreCase);

            // BepInEx log listener must be removed during probing or any warning
            // from a failing font would be captured into our log buffer and trigger a rebuild.
            BepInEx.Logging.Logger.Listeners.Remove(this);

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
                {
                    validFontNames.Add(name);
                    validFonts.Add(font);
                }
            }

            BepInEx.Logging.Logger.Listeners.Add(this);

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

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            string prefix = $"[{eventArgs.Level}] [{eventArgs.Source.SourceName}] ";

            lock (logLock)
            {
                logLines.Add(prefix + eventArgs.Data);

                if (logLines.Count > MaxLines)
                {
                    logLines.RemoveRange(0, logLines.Count - MaxLines);
                }

                filterDirty = true;

                if (autoScroll)
                    pendingScrollToBottom = true;
            }
        }

        public void Dispose()
        {
        }

        private void Update()
        {
            if (Plugin.ToggleShortcut.Value.IsDown())
                Plugin.ShowWindow.Value = !Plugin.ShowWindow.Value;

            if (colorCheckAt >= 0f && Time.unscaledTime >= colorCheckAt)
            {
                colorCheckAt = -1f;
                WarnInvalidHighlightColors();
            }

            // Nothing below is visible while hidden; the dirty flags stay set, so the
            // rebuild happens on the first frame after the window is shown again.
            if (!isVisible)
            {
                fontListOpen = false;
                fontListToggleRequested = false;
                return;
            }

            if (fontListToggleRequested)
            {
                fontListOpen = !fontListOpen;
                fontListToggleRequested = false;
            }

            if (pendingFontIndex >= 0)
            {
                ApplyFont(pendingFontIndex);
                pendingFontIndex = -1;
                fontListOpen = false;
            }

            if (clearRequested)
            {
                lock (logLock)
                {
                    logLines.Clear();
                }
                filteredLines.Clear();
                renderedLines.Clear();
                scrollPosition = Vector2.zero;
                clearRequested = false;
            }

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
                string color = Plugin.HighlightColor[i].Value?.Trim();
                if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(color)) continue;

                // An unknown colour would show the raw <color=...> tag in every matching line.
                if (!IsValidColor(color)) continue;

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

        private static bool IsValidColor(string color)
        {
            return ColorUtility.TryParseHtmlString(color, out _);
        }

        private void WarnInvalidHighlightColors()
        {
            for (int i = 0; i < Plugin.HighlightSlotCount; i++)
            {
                string color = Plugin.HighlightColor[i].Value?.Trim();
                if (string.IsNullOrEmpty(color) || string.IsNullOrEmpty(Plugin.HighlightPattern[i].Value)) continue;
                if (!IsValidColor(color))
                    Plugin.Log.LogWarning($"Highlight{i + 1}Color \"{color}\" isn't a known colour name or #RRGGBB; rule skipped.");
            }
        }

        private void RebuildFilteredLines()
        {
            filteredLines.Clear();

            lock (logLock)
            {
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
            }

            renderDirty = true;
        }

        private static string EscapeRichText(string text)
        {
            // The zero-width space must sit between '<' and the tag name; before the '<'
            // the tag still parses.
            return text.Replace("<", "<\u200B");
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

                // Split by newline so virtual scroll logic counts it accurately.
                string[] lines = escaped.Split('\n');
                for (int j = 0; j < lines.Length; j++)
                {
                    string line = lines[j].TrimEnd('\r');
                    renderedLines.Add(color != null
                        ? "<color=" + color + ">" + line + "</color>"
                        : line);
                }
            }
            renderDirty = false;
        }

        private static Texture2D MakeSolidTexture(Color32 color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
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

            logStyle = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                wordWrap = false,
                fontSize = 13,
                normal = { textColor = Color.white },
                padding = new RectOffset(4, 4, 2, 2),
                margin = new RectOffset(0, 0, 0, 0),
                font = validFonts[fontSelectorIndex]
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

            fontRowTexture = MakeSolidTexture(new Color32(38, 38, 38, 255));
            fontRowHoverTexture = MakeSolidTexture(new Color32(70, 70, 70, 255));
            fontRowStyle = new GUIStyle
            {
                normal = { background = fontRowTexture },
                hover = { background = fontRowHoverTexture },
                active = { background = fontRowHoverTexture },
                margin = new RectOffset(0, 0, 1, 1)
            };
            fontNameStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white },
                padding = new RectOffset(8, 4, 0, 0)
            };
            fontSampleStyles.Clear();
            for (int i = 0; i < validFonts.Count; i++)
            {
                fontSampleStyles.Add(new GUIStyle(fontNameStyle) { font = validFonts[i] });
            }

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
                return;
            }
            if (!stylesInitialized) InitStyles();

            UpdateBgTexture();
            ClampToScreen();

            if (isPinned)
            {
                DrawPinnedWindow();
            }
            else
            {
                Color prevBg = GUI.backgroundColor;
                float alpha = Plugin.BackgroundOpacity.Value / 100f;
                GUI.backgroundColor = new Color(prevBg.r, prevBg.g, prevBg.b, alpha);
                // GUI.DragWindow moves the window by returning the new rect from GUI.Window.
                windowRect = GUI.Window(WindowID, windowRect, DrawWindow, "Log Viewer");
                GUI.backgroundColor = prevBg;

                HandleResize();
            }

            SaveWindowRectIfChanged();
        }

        // Keeps the whole window on screen, e.g. after a resolution change or with a saved
        // layout from a larger monitor. Shrinks only when the window is larger than the screen.
        private void ClampToScreen()
        {
            windowRect.width = Mathf.Min(windowRect.width, Screen.width);
            windowRect.height = Mathf.Min(windowRect.height, Screen.height);
            windowRect.x = Mathf.Clamp(windowRect.x, 0f, Screen.width - windowRect.width);
            windowRect.y = Mathf.Clamp(windowRect.y, 0f, Screen.height - windowRect.height);
        }

        // Saves once a drag or resize ends rather than every frame of it: each config set
        // writes the .cfg to disk and fires SettingChanged.
        private void SaveWindowRectIfChanged()
        {
            if (isResizing || GUIUtility.hotControl != 0) return;

            if (windowRect.x != lastSavedRect.x || windowRect.y != lastSavedRect.y ||
                windowRect.width != lastSavedRect.width || windowRect.height != lastSavedRect.height)
            {
                ConfigFile config = Plugin.WindowX.ConfigFile;
                bool saveOnSet = config.SaveOnConfigSet;
                config.SaveOnConfigSet = false;
                Plugin.WindowX.Value = (int)windowRect.x;
                Plugin.WindowY.Value = (int)windowRect.y;
                Plugin.WindowWidth.Value = (int)windowRect.width;
                Plugin.WindowHeight.Value = (int)windowRect.height;
                config.SaveOnConfigSet = saveOnSet;
                config.Save();
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

            // Clamp before GUILayout sees it, adding a buffer to allow room for the
            // horizontal scrollbar and minor view padding. Unity's internal scroll
            // view will perform the final pixel-perfect clamp.
            float clampMax = Mathf.Max(0f, totalContentHeight - viewHeight + 30f);
            scrollPosition.y = Mathf.Min(scrollPosition.y, clampMax);

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
            Plugin.FontName.Value = validFontNames[index];
            logStyle.font = validFonts[index];
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
                string arrow = fontListOpen ? " \u25b2" : " \u25bc";
                if (GUILayout.Button(displayName + arrow, GUILayout.ExpandWidth(true)))
                    fontListToggleRequested = true;
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
            // × clears the filter text (Latin-1, so every UI font has it; ⌫ didn't render in PEAK)
            if (GUILayout.Button("\u00d7", GUILayout.Width(28)))
            {
                filterText = "";
                filterDirty = true;
            }
            // Clear clears the log buffer
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
                clearRequested = true;
            GUILayout.EndHorizontal();

            // Info row
            GUILayout.BeginHorizontal();
            int totalLinesCount;
            lock (logLock)
            {
                totalLinesCount = logLines.Count;
            }
            GUILayout.Label($"{filteredLines.Count} / {totalLinesCount} lines", GUILayout.Width(150));
            GUILayout.FlexibleSpace();
            bool newAutoScroll = GUILayout.Toggle(autoScroll, "Auto-scroll");
            if (newAutoScroll != autoScroll)
            {
                autoScroll = newAutoScroll;
                if (autoScroll) pendingScrollToBottom = true;
            }
            GUILayout.EndHorizontal();

            float headerH = validFontNames.Count > 1 ? WindowHeaderHeight : WindowHeaderHeight - 26f;
            float viewHeight = Mathf.Max(50f, windowRect.height - headerH);
            if (fontListOpen)
                DrawFontList(viewHeight);
            else
                DrawVirtualScrollContent(viewHeight);

            // Resize handle
            GUI.Label(new Rect(
                windowRect.width - ResizeHandleSize,
                windowRect.height - ResizeHandleSize,
                ResizeHandleSize,
                ResizeHandleSize), "\u25e2");

            // Dragging (header area, excluding buttons)
            GUI.DragWindow(new Rect(0, 0, windowRect.width - 58, 25));
        }

        private void DrawFontList(float viewHeight)
        {
            // BackgroundOpacity tints every control; the rows stay opaque.
            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = Color.white;

            // Whole pixels: a fractional height left the 1px gaps between rows on every other row.
            float rowHeight = Mathf.Round(Mathf.Max(22f, lineHeight + 4f));
            fontListScrollPos = GUILayout.BeginScrollView(fontListScrollPos, GUILayout.Height(viewHeight));
            for (int i = 0; i < validFontNames.Count; i++)
            {
                Rect row = GUILayoutUtility.GetRect(0f, rowHeight, fontRowStyle, GUILayout.ExpandWidth(true));
                if (GUI.Button(row, GUIContent.none, fontRowStyle))
                    pendingFontIndex = i;

                Rect nameRect = new Rect(row.x, row.y, Mathf.Min(220f, row.width * 0.4f), row.height);
                Rect sampleRect = new Rect(nameRect.xMax, row.y, row.width - nameRect.width, row.height);
                GUI.Label(nameRect, i == 0 ? "(default)" : validFontNames[i], fontNameStyle);
                GUI.Label(sampleRect, FontSample, fontSampleStyles[i]);
            }
            GUILayout.EndScrollView();

            GUI.backgroundColor = prevBg;
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
            BepInEx.Logging.Logger.Listeners.Remove(this);
            if (bgTexture != null) Destroy(bgTexture);
            if (fontRowTexture != null) Destroy(fontRowTexture);
            if (fontRowHoverTexture != null) Destroy(fontRowHoverTexture);
        }
    }
}

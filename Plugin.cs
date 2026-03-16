using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityLogViewer
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGUID = "com.malafein.unitylogviewer";
        public const string ModName = "UnityLogViewer";
        public const string ModVersion = "0.0.2";

        public static ConfigEntry<KeyboardShortcut> ToggleShortcut;
        public static ConfigEntry<bool> ShowWindow;
        public static ConfigEntry<string> Filter;
        public static ConfigEntry<int> BackgroundOpacity;
        public static ConfigEntry<bool> Pinned;
        public static ConfigEntry<int> BufferSize;
        public static ConfigEntry<string> FontName;
        public static ConfigEntry<int> WindowX;
        public static ConfigEntry<int> WindowY;
        public static ConfigEntry<int> WindowWidth;
        public static ConfigEntry<int> WindowHeight;

        public const int HighlightSlotCount = 8;
        public static ConfigEntry<string>[] HighlightPattern = new ConfigEntry<string>[HighlightSlotCount];
        public static ConfigEntry<string>[] HighlightColor = new ConfigEntry<string>[HighlightSlotCount];

        // Exposed so LogViewerUI can log without a direct reference to the BepInEx Logger field.
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Logger.LogInfo($"{ModName} {ModVersion} is loading...");

            // General settings (highest order = appears first)
            ShowWindow = Config.Bind(
                "General",
                "ShowWindow",
                false,
                new ConfigDescription("Toggle the log viewer window on/off. Use this if the hotkey doesn't work with your game's input system.",
                    null, new ConfigurationManagerAttributes { Order = 65 }));

            ToggleShortcut = Config.Bind(
                "General",
                "ToggleShortcut",
                new KeyboardShortcut(KeyCode.F7),
                new ConfigDescription("Keyboard shortcut to toggle log view.",
                    null, new ConfigurationManagerAttributes { Order = 60 }));

            Filter = Config.Bind(
                "General",
                "Filter",
                "",
                new ConfigDescription("Optional filter, supports regular expressions.",
                    null, new ConfigurationManagerAttributes { Order = 50 }));

            FontName = Config.Bind(
                "General",
                "FontName",
                "",
                new ConfigDescription("Font to use for log text. Leave empty for the default UI font. " +
                    "Check the BepInEx log at startup for a list of fonts available on this system.",
                    null, new ConfigurationManagerAttributes { Order = 45 }));

            Pinned = Config.Bind(
                "General",
                "Pinned",
                false,
                new ConfigDescription("Pin the log view in place. Hides window decorations and disables interaction.",
                    null, new ConfigurationManagerAttributes { Order = 40 }));

            BufferSize = Config.Bind(
                "General",
                "BufferSize",
                500,
                new ConfigDescription("Maximum number of log lines to keep in the buffer.",
                    new AcceptableValueRange<int>(100, 5000),
                    new ConfigurationManagerAttributes { Order = 20 }));

            BackgroundOpacity = Config.Bind(
                "General",
                "BackgroundOpacity",
                80,
                new ConfigDescription("Background opacity of the log view window (0-100).",
                    new AcceptableValueRange<int>(0, 100),
                    new ConfigurationManagerAttributes { Order = 10 }));

            // Window position/size
            WindowX = Config.Bind("Window", "WindowX", 833,
                new ConfigDescription("Horizontal position of the log viewer window.",
                    null, new ConfigurationManagerAttributes { Order = 40 }));
            WindowY = Config.Bind("Window", "WindowY", 1019,
                new ConfigDescription("Vertical position of the log viewer window.",
                    null, new ConfigurationManagerAttributes { Order = 30 }));
            WindowWidth = Config.Bind("Window", "WindowWidth", 1507,
                new ConfigDescription("Width of the log viewer window.",
                    null, new ConfigurationManagerAttributes { Order = 20 }));
            WindowHeight = Config.Bind("Window", "WindowHeight", 300,
                new ConfigDescription("Height of the log viewer window.",
                    null, new ConfigurationManagerAttributes { Order = 10 }));

            // Highlight rules (pattern before color for each slot)
            string[] defaultPatterns = { @"\[ERROR\]", @"\[WARNING\]", @"\[DEBUG\]|\[DBG\]", @"\[INFO\]", "", "", "", "" };
            string[] defaultColors = { "red", "yellow", "cyan", "white", "", "", "", "" };

            for (int i = 0; i < HighlightSlotCount; i++)
            {
                int slot = i + 1;
                int order = (HighlightSlotCount - i) * 2;

                HighlightPattern[i] = Config.Bind(
                    "Highlighting",
                    $"Highlight{slot}Pattern",
                    defaultPatterns[i],
                    new ConfigDescription($"Regex pattern for highlight rule {slot}. Leave empty to disable.",
                        null, new ConfigurationManagerAttributes { Order = order }));

                HighlightColor[i] = Config.Bind(
                    "Highlighting",
                    $"Highlight{slot}Color",
                    defaultColors[i],
                    new ConfigDescription($"Color for highlight rule {slot} (e.g. red, yellow, cyan, green, blue, magenta, orange, white, or #RRGGBB hex).",
                        null, new ConfigurationManagerAttributes { Order = order - 1 }));
            }

            if (!Application.isBatchMode)
            {
                gameObject.AddComponent<LogViewerUI>();
            }

            Logger.LogInfo($"{ModName} loaded!");
        }

        internal class ConfigurationManagerAttributes
        {
            public int? Order;
        }
    }
}

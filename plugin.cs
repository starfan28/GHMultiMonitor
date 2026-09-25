extern alias GH;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[BepInPlugin(MultiMonitorPlugin.Guid, MultiMonitorPlugin.PluginName, MultiMonitorPlugin.Version)]
[BepInProcess("Grey Hack.exe")]
public class MultiMonitorPlugin : BaseUnityPlugin
{
    public const string Guid = "com.bobby.greyhack.multimonitor";
    public const string PluginName = "GHMultiMonitor";
    public const string Version = "1.0.1";

    static readonly string[] SupportedGameVersions = { "0.9.6", "0.9.7" }; // game versions starting with any of these are allowed
    static readonly string[] BarNames = { "TaskBar", "BarraSupDesktop" };   // desktop bars windows must not cover
    const string DefaultDesktopPath = "ComputerCanvas/Desktop";
    const string IconsName = "Icons"; // the desktop icon layer inside the desktop
    const float SameAppOverlapLimit = 0.5f; // if a reopened window would cover this much of another copy, use the default spot instead
    const float KeepVisibleMargin = 60f;  // how much of a window must stay on screen so it can be grabbed again
    const float WindowSettleTime = 0.5f; // seconds to keep a reopened window in its remembered spot while the game finishes setting it up

    // Settings
    ConfigEntry<string> enabledDisplaysCfg;
    ConfigEntry<string> windowContainerPath;
    ConfigEntry<bool> keepFullscreen;
    ConfigEntry<string> backgroundSources;
    ConfigEntry<string> backgroundPath;
    ConfigEntry<string> excludedScreens;
    ConfigEntry<bool> snapEnabled;
    ConfigEntry<float> snapDelay;
    ConfigEntry<float> snapEdgeSize;
    ConfigEntry<float> snapCornerSize;
    ConfigEntry<KeyboardShortcut> menuKey;
    ConfigEntry<KeyboardShortcut> debugKey;
    int markerCount;

    // Unsupported game version
    bool unsupported;
    string noticeText;
    float noticeStart = -1f;
    float nextNoticeCheck;

    // Desktop state
    Canvas mainCanvas;
    RectTransform mainContainer;
    RectTransform mainIcons;
    readonly Dictionary<int, RectTransform> iconContainers = new Dictionary<int, RectTransform>(); // display -> desktop icon holder
    readonly Dictionary<int, RectTransform> popupLayers = new Dictionary<int, RectTransform>(); // display -> layer above the windows, for menus
    float nextCanvasSync;
    readonly Dictionary<int, RectTransform> containers = new Dictionary<int, RectTransform>(); // display -> window holder
    readonly Dictionary<int, GameObject> secondaryCanvases = new Dictionary<int, GameObject>();
    readonly Dictionary<int, Camera> clearCameras = new Dictionary<int, Camera>();
    readonly HashSet<int> activatedDisplays = new HashSet<int>(); // Unity can't turn these off again
    readonly List<int> wantedDisplays = new List<int>();  // monitors in use this session
    readonly List<int> savedDisplays = new List<int>();   // monitors saved for next launch
    readonly Queue<int> pendingActivations = new Queue<int>();
    float nextSetupAttempt;
    float fullscreenCheckAt = -1f;

    // Background state
    readonly Dictionary<int, RawImage> secondaryBackgrounds = new Dictionary<int, RawImage>();
    readonly Dictionary<string, System.Type> typeCache = new Dictionary<string, System.Type>();
    readonly HashSet<string> loggedMessages = new HashSet<string>();
    string lastBackgroundDescription;
    float nextBackgroundSync;
    bool loggedNoBackground;

    // Remembered window positions
    class Placement
    {
        public int Display;
        public Rect Norm; // position and size as fractions of the monitor
    }
    readonly Dictionary<string, Placement> placements = new Dictionary<string, Placement>();
    readonly Dictionary<Transform, bool> windowActive = new Dictionary<Transform, bool>(); // last seen open/closed state
    readonly Dictionary<Transform, float> openedWindows = new Dictionary<Transform, float>(); // window -> time it opened
        class Snapshot
    {
        public string Name;
        public int Display;
        public Rect Norm;
    }
    readonly Dictionary<Transform, Snapshot> openSnapshots = new Dictionary<Transform, Snapshot>(); // last known spot of each open window
    bool placementsDirty;
    float nextPlacementRefresh;
    string PlacementsFile => Path.Combine(Paths.ConfigPath, Guid + ".windows.txt");

    // Error handling
    readonly HashSet<string> loggedErrors = new HashSet<string>();

    // Menu state
    bool showMenu;
    bool[] menuSelection;
    bool menuSnap;
    float menuDelay, menuEdge, menuCorner;
    Rect menuRect;
    int menuAction; // 0 = nothing, 1 = save, 2 = cancel, 3 = bring windows back, 4 = forget positions
    GameObject clickBlocker;
    Font labelFont;

    // Drag state
    Transform pressedWindow;
    Vector3 pressedWindowStartPos;
    Vector2 grabOffset;
    bool isWindowDrag;
    bool ownsDrag;
    bool pressedIsIcon; // dragging a desktop icon rather than a window
    int lastClickDisplay; // the monitor you last clicked on
    // Lets the game's dialog code find what it expects when an app is on another monitor
    static MultiMonitorPlugin instance;
    class TempMove
    {
        public Transform Window;
        public Transform Parent;
        public Vector3 LocalPos;
        public int Display;
    }
    class PendingDialog
    {
        public int Display;
        public Transform Anchor; // the app window that asked for the dialog
        public float Until;
    }
    PendingDialog pendingDialog;

    // Right-click menus opened on other monitors
    class MenuHome
    {
        public Transform Parent;
        public int Sibling;
    }
    readonly Dictionary<Transform, MenuHome> menuHomes = new Dictionary<Transform, MenuHome>(); // where each menu normally lives
    Transform menuOnOtherMonitor;
    GH::ContextualMenu mainMenu;
    static AccessTools.FieldRef<GH::InteractableContextual, GH::ContextualMenu> contextualMenuRef;

    Transform pressedIcon; // the real desktop icon being dragged (the game drags a copy of it around)
    Transform dragCopy;    // the game's copy of the icon that follows the cursor
    static readonly string[] TransientNames = { "CopyIcon(Clone)", "Tooltip(Clone)", "ContextualMenu" }; // game objects that aren't windows
    static readonly string[] NoMemoryPrefixes = { "Properties - ", "ErrorWindow" }; // windows the game places itself (centered on the monitor you're using)

    // Snap state
    enum SnapZone { None, Left, Right, Top, TopLeft, TopRight, BottomLeft, BottomRight }
    SnapZone pendingZone = SnapZone.None; // zone under the cursor
    SnapZone shownZone = SnapZone.None;   // zone currently being previewed
    int zoneDisplay = -1;
    float zoneEnteredAt;
    readonly Dictionary<Transform, Vector2> restoreSizes = new Dictionary<Transform, Vector2>(); // size before snapping

    class SnapPreview
    {
        public GameObject Root;
        public RectTransform Box;
        public Image Fill;
        public Image[] Edges;
        public Rect Current;
    }
    readonly Dictionary<int, SnapPreview> previews = new Dictionary<int, SnapPreview>();

    void Awake()
    {
        // Only run on the game versions this mod was made for
        string gameVersion = Application.version;
        if (!SupportedGameVersions.Any(v => gameVersion.StartsWith(v, System.StringComparison.Ordinal)))
        {
            unsupported = true;
            noticeText = $"{PluginName} is off: Grey Hack {gameVersion} isn't supported yet. Check for an updated version of the mod.";
            Logger.LogWarning($"Grey Hack version {gameVersion} isn't supported by {PluginName} {Version} " +
                              $"(made for {string.Join(", ", SupportedGameVersions)}). The mod has turned itself off. Check for an updated version of the mod.");
            return;
        }
        Logger.LogInfo($"Grey Hack version {gameVersion} detected (supported).");
        instance = this;
        PatchGame();

        // General
        enabledDisplaysCfg = Config.Bind("General", "EnabledDisplays", "",
            "Extra monitors to use, e.g. 1,2. Leave empty to be asked on next launch. Set from the in-game menu.");
        keepFullscreen = Config.Bind("General", "KeepMainFullscreen", true,
            "Keeps the main game window in borderless fullscreen. Turning on extra monitors can knock it into windowed mode.");
        windowContainerPath = Config.Bind("General", "WindowContainerPath", DefaultDesktopPath,
            "Hierarchy path of the object that holds the in-game windows. Only change this if a game update moves it.");

        // Background
        backgroundSources = Config.Bind("Background", "BackgroundSources", "DesktopFinder.desktopBackground,Apariencia.desktopImage",
            "Game variables (ClassName.variableName) to read the desktop background from, tried in order.");
        backgroundPath = Config.Bind("Background", "BackgroundPath", "",
            "Hierarchy path of the desktop background to copy onto extra monitors. Overrides BackgroundSources when set.");
        excludedScreens = Config.Bind("Background", "ExcludedScreens", "BootUp,InstallationOS",
            "Objects directly under the main canvas that are never copied to extra monitors. While any of them is showing, extra monitors stay black.");

        // Snapping
        snapEnabled = Config.Bind("Snapping", "EnableSnapping", true,
            "Snap windows to halves, quarters or full screen by dragging them to screen edges and corners. Set from the in-game menu.");
        snapDelay = Config.Bind("Snapping", "Delay", 0.1f, new ConfigDescription(
            "Seconds the cursor rests at an edge before the snap preview appears. Set from the in-game menu.",
            new AcceptableValueRange<float>(0f, 1f)));
        snapEdgeSize = Config.Bind("Snapping", "EdgeSize", 24f, new ConfigDescription(
            "Pixels from a screen edge that count as being at the edge. Set from the in-game menu.",
            new AcceptableValueRange<float>(2f, 100f)));
        snapCornerSize = Config.Bind("Snapping", "CornerSize", 0.2f, new ConfigDescription(
            "Portion of each screen edge that counts as a corner. Set from the in-game menu.",
            new AcceptableValueRange<float>(0.05f, 0.45f)));

        // Keys
        menuKey = Config.Bind("Keys", "OpenMenu", new KeyboardShortcut(KeyCode.F9),
            "Opens the multi-monitor settings menu.");
        debugKey = Config.Bind("Keys", "LogDebug", new KeyboardShortcut(KeyCode.F8),
            "Writes a numbered marker line to the log, handy for noting when something happened.");

        LoadPlacements();

        Logger.LogInfo($"Displays detected: {Display.displays.Length}");
        for (int i = 0; i < Display.displays.Length; i++)
            Logger.LogInfo($"  [{i}] {Display.displays[i].systemWidth}x{Display.displays[i].systemHeight}");

        if (Display.displays.Length < 2)
            Logger.LogInfo("Only one monitor found; window snapping is still available.");

        menuSelection = new bool[Display.displays.Length];

        string saved = enabledDisplaysCfg.Value.Trim();
        if (saved == "")
        {
            OpenMenu(); // first launch: ask
        }
        else
        {
            savedDisplays.AddRange(ParseDisplays(saved));
            wantedDisplays.AddRange(savedDisplays);
            Logger.LogInfo($"Using saved monitors: {(wantedDisplays.Count == 0 ? "none" : string.Join(",", wantedDisplays))}");
        }
    }

    void Start()
    {
        if (unsupported) return;
        foreach (int i in wantedDisplays) pendingActivations.Enqueue(i);
    }

    void OnApplicationQuit()
    {
        if (!unsupported && placementsDirty) SavePlacements();
    }

    // ---------------- Error safety net ----------------
    // If something breaks (most likely after a game update), log each distinct error once,
    // cancel any drag in progress, and keep the game running instead of flooding the log.

    void HandleError(string where, System.Exception e)
    {
        string key = $"{where}: {e.GetType().Name}: {e.Message}";
        if (loggedErrors.Add(key))
            Logger.LogError($"{key}\n{e.StackTrace}\n(Further identical errors won't be logged.)");
        try { EndPress(); } catch { }
    }

    void Update()
    {
        try
        {
            if (unsupported) UpdateUnsupportedNotice();
            else UpdateInner();
        }
        catch (System.Exception e) { HandleError("Update", e); }
    }

    void LateUpdate()
    {
        if (unsupported) return;
        try
        {
            if (!showMenu && mainContainer != null) TrackWindows(); // runs after the game's own code each frame
            if (!showMenu && mainContainer != null) UpdateIconDrag();
            LateUpdateInner();
        }
        catch (System.Exception e) { HandleError("LateUpdate", e); }
    }

    void OnGUI()
    {
        try
        {
            if (unsupported) DrawUnsupportedNotice();
            else OnGUIInner();
        }
        catch (ExitGUIException) { throw; } // normal Unity control flow, not an error
        catch (System.Exception e) { HandleError("Menu", e); }
    }

    void UpdateInner()
    {
        // Menu buttons are acted on here, outside of menu drawing, which is safer.
        // The action is cleared first so an error can't make it repeat every frame.
        int action = menuAction;
        menuAction = 0;
        if (action == 1) ApplyMenuSelection();
        if (action == 3) BringAllWindowsBack();
        if (action == 4) ForgetPlacements();
        if (action == 1 || action == 2) CloseMenu();

        if (menuKey.Value.IsDown())
        {
            if (showMenu) CloseMenu(); else OpenMenu();
        }

        UpdateScreenOverlays();

        // Turn on at most one monitor per frame
        if (pendingActivations.Count > 0) ActivateDisplay(pendingActivations.Dequeue());

        if (fullscreenCheckAt > 0f && Time.unscaledTime >= fullscreenCheckAt)
        {
            fullscreenCheckAt = -1f;
            EnsureMainFullscreen();
        }

        if (showMenu) return;

        if (debugKey.Value.IsDown()) LogDebug();

        if (mainContainer == null && !TrySetup()) return;

        if (Time.unscaledTime >= nextBackgroundSync)
        {
            nextBackgroundSync = Time.unscaledTime + 1f;
            SyncBackgrounds();
        }

        if (Time.unscaledTime >= nextCanvasSync)
        {
            nextCanvasSync = Time.unscaledTime + 0.25f;
            SyncNestedCanvases();
        }

        UpdateWebCaster();

        if (Input.GetMouseButtonDown(0)) CloseStrayMenu();
        if (Input.GetMouseButtonDown(0)) BeginPress();
        // Mouse release is handled in LateUpdate, after the game has finished its own drag handling
    }

    List<int> ParseDisplays(string text)
    {
        var result = new List<int>();
        foreach (var part in text.Split(','))
            if (int.TryParse(part.Trim(), out int i) && i >= 1 && i < Display.displays.Length && !result.Contains(i))
                result.Add(i);
        return result;
    }

    static List<string> ParseList(string text)
    {
        return text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    // ---------------- Unsupported version notice ----------------

    // Waits until the in-game desktop has loaded (or a minute has passed) so players actually see the notice
    void UpdateUnsupportedNotice()
    {
        if (noticeStart >= 0f || Time.unscaledTime < nextNoticeCheck) return;
        nextNoticeCheck = Time.unscaledTime + 1f;
        if (GameObject.Find(DefaultDesktopPath) != null || Time.unscaledTime > 60f)
            noticeStart = Time.unscaledTime;
    }

    void DrawUnsupportedNotice()
    {
        if (noticeStart < 0f) return;
        const float duration = 12f;
        float elapsed = Time.unscaledTime - noticeStart;
        if (elapsed > duration) return;

        float scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

        const float w = 460f, h = 64f;
        var box = new Rect(Screen.width / scale - w - 20f, Screen.height / scale - h - 60f, w, h);
        var oldColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01((duration - elapsed) / 1.5f)); // fade out at the end
        GUI.Box(box, GUIContent.none);
        GUI.Box(box, GUIContent.none); // drawn twice for a darker background
        var style = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 14 };
        GUI.Label(new Rect(box.x + 12f, box.y + 8f, w - 24f, h - 16f), noticeText, style);
        GUI.color = oldColor;
    }

    // ---------------- Menu ----------------

    void OpenMenu()
    {
        EndPress();
        for (int i = 0; i < menuSelection.Length; i++) menuSelection[i] = savedDisplays.Contains(i);
        menuSnap = snapEnabled.Value;
        menuDelay = snapDelay.Value;
        menuEdge = snapEdgeSize.Value;
        menuCorner = snapCornerSize.Value;
        menuRect = new Rect(0, 0, 0, 0); // re-center next time it's drawn
        showMenu = true;
        SetClickBlocker(true);
    }

    void CloseMenu()
    {
        showMenu = false;
        SetClickBlocker(false);
    }

    // A dimmed layer over the main screen that soaks up clicks while the menu is open.
    // The game's own input is never switched off.
    void SetClickBlocker(bool on)
    {
        if (clickBlocker == null)
        {
            if (!on) return;

            clickBlocker = new GameObject("MM_MenuClickBlocker");
            DontDestroyOnLoad(clickBlocker);

            var canvas = clickBlocker.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = 0;
            canvas.sortingOrder = 32700; // above the game and snap preview, below the screen overlays
            clickBlocker.AddComponent<GraphicRaycaster>();

            var shade = new GameObject("Shade", typeof(RectTransform));
            var rt = (RectTransform)shade.transform;
            rt.SetParent(clickBlocker.transform, false);
            Stretch(rt);

            var img = shade.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.4f);
            img.raycastTarget = true;
        }
        clickBlocker.SetActive(on);
    }

    void OnGUIInner()
    {
        if (!showMenu) return;

        float scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

        if (menuRect.width == 0)
            menuRect = new Rect((Screen.width / scale - 440f) / 2f, (Screen.height / scale - 300f) / 2f, 440f, 0f);

        menuRect = GUILayout.Window(0x4D4D, menuRect, DrawMenu, $"Grey Hack Multi-Monitor {Version}");
    }

    void DrawMenu(int id)
    {
        bool hasExtraMonitors = menuSelection.Length > 1;

        if (hasExtraMonitors)
        {
            GUILayout.Label("Pick which extra monitors in-game windows can be dragged onto:");
            GUILayout.Space(6);

            for (int i = 1; i < menuSelection.Length; i++)
            {
                var d = Display.displays[i];
                string status = "";
                if (wantedDisplays.Contains(i) && !menuSelection[i]) status = "   (turns off after restart)";
                else if (activatedDisplays.Contains(i)) status = "   (in use)";
                menuSelection[i] = GUILayout.Toggle(menuSelection[i], $"  Monitor {i}    {d.systemWidth} x {d.systemHeight}{status}");
            }
            GUILayout.Label("Monitors in use show their number while this menu is open. To identify an unticked monitor, tick it and click Save.");
        }
        else
        {
            GUILayout.Label("No extra monitors detected. Window snapping still works on your main screen.");
        }

        GUILayout.Space(8);
        menuSnap = GUILayout.Toggle(menuSnap, "  Snap windows to screen edges and corners");

        if (menuSnap)
        {
            menuEdge = Slider($"Edge size: {menuEdge:0} px", menuEdge, 2f, 100f, 1f);
            menuCorner = Slider($"Corner size: {menuCorner * 100f:0}% of each edge", menuCorner, 0.05f, 0.45f, 0.01f);
            menuDelay = Slider($"Delay before preview: {menuDelay:0.00} s", menuDelay, 0f, 1f, 0.05f);
            if (GUILayout.Button("Reset snap settings to defaults"))
            {
                menuEdge = (float)snapEdgeSize.DefaultValue;
                menuCorner = (float)snapCornerSize.DefaultValue;
                menuDelay = (float)snapDelay.DefaultValue;
            }
            GUILayout.Label("The highlighted strips on your screens show where snapping triggers (blue: halves and maximize, orange: corners).");
        }

        GUILayout.Space(8);
        GUILayout.Label($"Windows reopen where you last put them ({placements.Count} remembered).");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Bring all windows to main screen")) menuAction = 3;
        if (GUILayout.Button("Forget saved window positions")) menuAction = 4;
        GUILayout.EndHorizontal();

        GUILayout.Space(8);
        if (hasExtraMonitors)
            GUILayout.Label("Turning a monitor on works right away. Turning one off takes effect the next time you start the game.");
        GUILayout.Label($"Reopen this menu anytime with {menuKey.Value}.");
        GUILayout.Space(6);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save", GUILayout.Height(28))) menuAction = 1;
        if (GUILayout.Button("Cancel", GUILayout.Height(28))) menuAction = 2;
        GUILayout.EndHorizontal();

        GUI.DragWindow();
    }

    static float Slider(string label, float value, float min, float max, float step)
    {
        GUILayout.Label(label);
        float v = GUILayout.HorizontalSlider(value, min, max);
        return Mathf.Round(v / step) * step;
    }

    void ApplyMenuSelection()
    {
        var chosen = new List<int>();
        for (int i = 1; i < menuSelection.Length; i++)
            if (menuSelection[i]) chosen.Add(i);

        enabledDisplaysCfg.Value = chosen.Count == 0 ? "none" : string.Join(",", chosen); // saved automatically
        savedDisplays.Clear();
        savedDisplays.AddRange(chosen);
        snapEnabled.Value = menuSnap;
        snapDelay.Value = menuDelay;
        snapEdgeSize.Value = menuEdge;
        snapCornerSize.Value = menuCorner;

        // Newly ticked monitors are queued to start (one per frame)
        foreach (int i in chosen)
        {
            if (!wantedDisplays.Contains(i)) wantedDisplays.Add(i);
            if (!activatedDisplays.Contains(i) && !pendingActivations.Contains(i)) pendingActivations.Enqueue(i);
        }

        // Unticked monitors keep working until the game restarts
        var pendingOff = wantedDisplays.Where(i => !chosen.Contains(i)).ToList();
        if (pendingOff.Count > 0)
            Logger.LogInfo($"Monitor(s) {string.Join(",", pendingOff)} will stop being used after the game restarts.");

        Logger.LogInfo($"Saved monitors: {enabledDisplaysCfg.Value}, snapping {(snapEnabled.Value ? "on" : "off")}");
    }

    // ---------------- Screen overlays (shown while the F9 menu is open) ----------------
    // Each monitor in use shows its number (like Windows' "Identify") and, with snapping on,
    // the strips where snapping triggers.

    class ScreenOverlay
    {
        public GameObject Root;
        public GameObject Zones;
        public RectTransform[] Pieces;
        public RectTransform LabelBox;
        public Text Label;
    }
    readonly Dictionary<int, ScreenOverlay> screenOverlays = new Dictionary<int, ScreenOverlay>();

    Font LabelFont()
    {
        if (labelFont != null) return labelFont;

        // Prefer the game's own font so the numbers match its style
        if (mainCanvas != null)
        {
            var t = mainCanvas.GetComponentInChildren<Text>(true);
            if (t != null && t.font != null) return labelFont = t.font;
        }
        try { labelFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (labelFont == null)
            try { labelFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
        return labelFont;
    }

    ScreenOverlay GetScreenOverlay(int display)
    {
        if (screenOverlays.TryGetValue(display, out var o) && o.Root != null) return o;

        o = new ScreenOverlay { Root = new GameObject($"MM_ScreenOverlay_{display}"), Pieces = new RectTransform[11] };
        DontDestroyOnLoad(o.Root);

        var canvas = o.Root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.targetDisplay = display;
        canvas.sortingOrder = 32750; // above the F9 menu shade

        // Snap zone strips
        o.Zones = new GameObject("Zones", typeof(RectTransform));
        var zonesRT = (RectTransform)o.Zones.transform;
        zonesRT.SetParent(o.Root.transform, false);
        Stretch(zonesRT);

        var half = new Color(0.35f, 0.65f, 1f, 0.35f);
        var corner = new Color(1f, 0.75f, 0.2f, 0.45f);
        for (int i = 0; i < o.Pieces.Length; i++)
        {
            var go = new GameObject("Zone", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(zonesRT, false);
            PinBottomLeft(rt);
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            img.color = i < 3 ? half : corner; // first three pieces are halves/maximize, the rest are corners
            o.Pieces[i] = rt;
        }

        // Monitor number
        var boxGO = new GameObject("NumberBox", typeof(RectTransform));
        o.LabelBox = (RectTransform)boxGO.transform;
        o.LabelBox.SetParent(o.Root.transform, false);
        PinBottomLeft(o.LabelBox);
        var boxImg = boxGO.AddComponent<Image>();
        boxImg.color = new Color(0f, 0f, 0f, 0.6f);
        boxImg.raycastTarget = false;

        var textGO = new GameObject("Number", typeof(RectTransform));
        var textRT = (RectTransform)textGO.transform;
        textRT.SetParent(o.LabelBox, false);
        Stretch(textRT);
        o.Label = textGO.AddComponent<Text>();
        o.Label.font = LabelFont();
        o.Label.alignment = TextAnchor.MiddleCenter;
        o.Label.horizontalOverflow = HorizontalWrapMode.Overflow;
        o.Label.verticalOverflow = VerticalWrapMode.Overflow;
        o.Label.color = Color.white;
        o.Label.raycastTarget = false;

        o.Root.SetActive(false);
        screenOverlays[display] = o;
        return o;
    }

    // Positions are measured from the bottom-left of each monitor
    void UpdateScreenOverlays()
    {
        var displays = new List<int> { 0 };
        displays.AddRange(activatedDisplays);

        foreach (var kv in screenOverlays)
            if (kv.Value.Root != null && kv.Value.Root.activeSelf && (!showMenu || !displays.Contains(kv.Key)))
                kv.Value.Root.SetActive(false);
        if (!showMenu) return;

        foreach (int d in displays)
        {
            var o = GetScreenOverlay(d);
            Vector2 size = DisplaySize(d);
            float w = size.x, h = size.y;
            float e = menuEdge, cw = w * menuCorner, ch = h * menuCorner;

            o.Zones.SetActive(menuSnap);
            if (menuSnap)
            {
                SetPiece(o.Pieces[0], cw, h - e, w - 2f * cw, e);    // top (maximize)
                SetPiece(o.Pieces[1], 0f, ch, e, h - 2f * ch);       // left half
                SetPiece(o.Pieces[2], w - e, ch, e, h - 2f * ch);    // right half
                SetPiece(o.Pieces[3], 0f, h - e, cw, e);             // corners along the top edge
                SetPiece(o.Pieces[4], w - cw, h - e, cw, e);
                SetPiece(o.Pieces[5], 0f, 0f, cw, e);                // corners along the bottom edge
                SetPiece(o.Pieces[6], w - cw, 0f, cw, e);
                SetPiece(o.Pieces[7], 0f, h - ch, e, ch - e);        // corners along the left and right edges
                SetPiece(o.Pieces[8], w - e, h - ch, e, ch - e);
                SetPiece(o.Pieces[9], 0f, e, e, ch - e);
                SetPiece(o.Pieces[10], w - e, e, e, ch - e);
            }

            // Monitor number in the top-left corner, clear of the snap strips
            float boxH = h * 0.18f;
            float boxW = d == 0 ? boxH * 2f : boxH;
            float inset = e + h * 0.04f;
            SetPiece(o.LabelBox, inset, h - inset - boxH, boxW, boxH);
            o.Label.fontSize = Mathf.RoundToInt(boxH * (d == 0 ? 0.45f : 0.65f));
            o.Label.text = d == 0 ? "Main" : d.ToString();
            if (o.Label.font == null) o.Label.font = LabelFont();

            if (!o.Root.activeSelf) o.Root.SetActive(true);
        }
    }

    static void PinBottomLeft(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
    }

    static void SetPiece(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(Mathf.Max(0f, w), Mathf.Max(0f, h));
    }

    // ---------------- Displays & canvases ----------------

    void ActivateDisplay(int i)
    {
        if (activatedDisplays.Add(i))
        {
            Display.displays[i].Activate();
            Logger.LogInfo($"Activated display {i}.");
            fullscreenCheckAt = Time.unscaledTime + 1f; // check the main window shortly after
        }
        if (mainContainer != null && !containers.ContainsKey(i)) BuildSecondaryDesktop(i);
    }

    void EnsureMainFullscreen()
    {
        if (!keepFullscreen.Value || activatedDisplays.Count == 0) return;
        if (Screen.fullScreenMode == FullScreenMode.FullScreenWindow) return;

        int w = Display.main.systemWidth;
        int h = Display.main.systemHeight;
        Logger.LogInfo($"Main window was {Screen.fullScreenMode}; switching to borderless fullscreen {w}x{h}.");
        Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
    }

    bool TrySetup()
    {
        if (Time.unscaledTime < nextSetupAttempt) return false;
        nextSetupAttempt = Time.unscaledTime + 1f;

        if (string.IsNullOrEmpty(windowContainerPath.Value)) return false;
        var go = GameObject.Find(windowContainerPath.Value);
        if (go == null)
        {
            LogOnce($"Waiting for '{windowContainerPath.Value}' (this is normal until the in-game desktop loads).");
            return false;
        }

        // Bring any menu back to the desktop before the extra monitors' surfaces are rebuilt
        foreach (var m in menuHomes.Keys.ToList()) if (m != null) MenuComeHome(m);
        menuOnOtherMonitor = null;

        // Clear anything left over from a previous desktop (e.g. after logging out)
        foreach (var c in secondaryCanvases.Values) if (c != null) Destroy(c);
        secondaryCanvases.Clear();
        secondaryBackgrounds.Clear();
        containers.Clear();
        iconContainers.Clear();
        popupLayers.Clear();
        restoreSizes.Clear();
        windowActive.Clear();
        openedWindows.Clear();
        openSnapshots.Clear();
        lastBackgroundDescription = null;
        loggedNoBackground = false;

        mainContainer = (RectTransform)go.transform;
        mainCanvas = go.GetComponentInParent<Canvas>().rootCanvas;
        mainMenu = mainCanvas.GetComponentInChildren<GH::ContextualMenu>();
        containers[0] = mainContainer;
        mainIcons = mainContainer.Find(IconsName) as RectTransform;
        if (mainIcons != null) iconContainers[0] = mainIcons;
        else LogOnce($"No '{IconsName}' found on the desktop; desktop icons will stay on the main screen.");

        // Windows already on the desktop are left where they are
        foreach (Transform child in mainContainer)
            if (!IsBar(child)) windowActive[child] = child.gameObject.activeSelf;

        foreach (int i in wantedDisplays)
            if (activatedDisplays.Contains(i)) BuildSecondaryDesktop(i);

        Logger.LogInfo("Desktop found; extra monitors ready.");
        return true;
    }

    void BuildSecondaryDesktop(int idx)
    {
        if (!clearCameras.TryGetValue(idx, out var cam) || cam == null)
        {
            cam = new GameObject($"MM_ClearCamera_{idx}").AddComponent<Camera>();
            cam.targetDisplay = idx;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 0;
            cam.depth = -100;
            clearCameras[idx] = cam;
        }

        var canvasGO = new GameObject($"MM_Canvas_{idx}");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.targetDisplay = idx;
        canvas.sortingOrder = mainCanvas.sortingOrder;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        var mainScaler = mainCanvas.GetComponent<CanvasScaler>();
        if (mainScaler != null)
        {
            scaler.uiScaleMode = mainScaler.uiScaleMode;
            scaler.referenceResolution = mainScaler.referenceResolution;
            scaler.screenMatchMode = mainScaler.screenMatchMode;
            scaler.matchWidthOrHeight = mainScaler.matchWidthOrHeight;
            scaler.scaleFactor = mainScaler.scaleFactor;
            scaler.referencePixelsPerUnit = mainScaler.referencePixelsPerUnit;
        }
        canvasGO.AddComponent<GraphicRaycaster>();

        // Background goes in first so it's drawn behind the windows
        var bgGO = new GameObject("MM_Background", typeof(RectTransform));
        var bgRT = (RectTransform)bgGO.transform;
        bgRT.SetParent(canvasGO.transform, false);
        Stretch(bgRT);
        var bg = bgGO.AddComponent<RawImage>();
        bg.raycastTarget = false;
        bg.enabled = false; // shown once a background is found
        secondaryBackgrounds[idx] = bg;

        // Desktop icons go between the wallpaper and the windows, like on the main screen
        var iconsGO = new GameObject("MM_Icons", typeof(RectTransform));
        var iconsRT = (RectTransform)iconsGO.transform;
        iconsRT.SetParent(canvasGO.transform, false);
        Stretch(iconsRT);
        iconContainers[idx] = iconsRT;

        var container = new GameObject("MM_Windows", typeof(RectTransform));
        var rt = (RectTransform)container.transform;
        rt.SetParent(canvasGO.transform, false);
        Stretch(rt);

        // Menus go in a layer above all the windows, so a window can never cover one
        var popupsGO = new GameObject("MM_Popups", typeof(RectTransform));
        var popupsRT = (RectTransform)popupsGO.transform;
        popupsRT.SetParent(canvasGO.transform, false);
        Stretch(popupsRT);
        popupLayers[idx] = popupsRT;

        secondaryCanvases[idx] = canvasGO;
        containers[idx] = rt;
        nextBackgroundSync = 0f; // fill in the background right away
    }

    int DisplayOf(Transform container)
    {
        foreach (var kv in containers)
            if (kv.Value == container) return kv.Key;
        return -1;
    }

    // ---------------- Desktop bars ----------------

    // The game's bars (taskbar, and the top bar with the clock/settings/user buttons) live on
    // the main monitor, inside the same object as the windows
    List<RectTransform> Bars(RectTransform container)
    {
        var bars = new List<RectTransform>();
        if (container == null || container != mainContainer) return bars;
        foreach (var name in BarNames)
        {
            var t = container.Find(name) as RectTransform;
            if (t != null && t.gameObject.activeInHierarchy) bars.Add(t);
        }
        return bars;
    }

    static bool IsBar(Transform t)
    {
        return BarNames.Contains(t.name);
    }

    // Brings a window to the front, but keeps it underneath any bars drawn on top
    void PlaceOnTop(Transform win, RectTransform container)
    {
        win.SetAsLastSibling();
        int idx = win.GetSiblingIndex();
        while (idx > 0 && IsBar(container.GetChild(idx - 1))) idx--;
        win.SetSiblingIndex(idx);
    }

    // The part of a monitor windows can snap into (the whole screen, minus the bars)
    Rect WorkArea(RectTransform container)
    {
        Rect area = container.rect;
        foreach (var bar in Bars(container))
        {
            Rect b = LocalRectOf(bar);
            if (b.width >= area.width * 0.5f) // horizontal bar
            {
                if (b.center.y < area.center.y) area.yMin = Mathf.Max(area.yMin, b.yMax);
                else area.yMax = Mathf.Min(area.yMax, b.yMin);
            }
            else if (b.height >= area.height * 0.5f) // vertical bar
            {
                if (b.center.x < area.center.x) area.xMin = Mathf.Max(area.xMin, b.xMax);
                else area.xMax = Mathf.Min(area.xMax, b.xMin);
            }
        }
        return area;
    }

    // Nudges a window so its top edge (where the title bar is) sits below the top bar and
    // enough of it stays on screen to grab. Windows that are already reachable don't move.
    void KeepGrabbable(RectTransform win, RectTransform container)
    {
        if (win == null || container == null || !win.gameObject.activeInHierarchy) return;

        Rect r = LocalRectOf(win);
        if (r.height >= container.rect.height * 0.99f) return; // a full-screen layer, not a window

        Rect area = WorkArea(container);
        float mx = Mathf.Min(KeepVisibleMargin, r.width * 0.5f);
        float dx = 0f, dy = 0f;

        if (r.xMax < area.xMin + mx) dx = area.xMin + mx - r.xMax;           // too far left
        else if (r.xMin > area.xMax - mx) dx = area.xMax - mx - r.xMin;      // too far right

        if (r.yMax > area.yMax) dy = area.yMax - r.yMax;                     // title bar under the top bar or off the top
        else if (r.yMax < area.yMin + KeepVisibleMargin) dy = area.yMin + KeepVisibleMargin - r.yMax; // dropped below the bottom

        if (dx != 0f || dy != 0f) win.localPosition += new Vector3(dx, dy, 0f);
    }

    // Moves every window onto the main screen, and makes sure each one can be grabbed
    void BringAllWindowsBack()
    {
        if (mainContainer == null)
        {
            Logger.LogInfo("The in-game desktop hasn't loaded yet, so there are no windows to bring back.");
            return;
        }

        var windows = new List<(RectTransform win, RectTransform from)>();
        foreach (var c in containers.Values)
        {
            if (c == null) continue;
            foreach (Transform child in c)
                if (!IsBar(child) && !IsTransient(child) && child is RectTransform rt) windows.Add((rt, c)); // never the menu
        }

        Rect area = WorkArea(mainContainer);
        int moved = 0;
        foreach (var (win, from) in windows)
        {
            if (from != mainContainer)
            {
                win.SetParent(mainContainer, false);
                PlaceOnTop(win, mainContainer);

                // Shrink it if it's bigger than the main screen, then cascade it near the center
                Rect r = LocalRectOf(win);
                float w = Mathf.Min(r.width, area.width), h = Mathf.Min(r.height, area.height);
                Vector2 offset = new Vector2(30f, -30f) * (moved % 8);
                SetLocalRect(win, new Rect(area.center.x - w / 2f + offset.x, area.center.y - h / 2f + offset.y, w, h));
                moved++;
            }
            KeepGrabbable(win, mainContainer);
        }
        Logger.LogInfo($"Brought {moved} window(s) back to the main screen.");
    }

    // ---------------- Remembered window positions ----------------

    // Watches for windows opening (new ones, or hidden ones shown again) and puts them back where
    // they were last time. Also keeps saved positions up to date while remembered windows are open.
       // Watches for windows opening (new ones, or hidden ones shown again) and puts them back where
    // they were last time. Also keeps saved positions up to date while remembered windows are open.
    void TrackWindows()
    {
        float now = Time.unscaledTime;
        UpdateSnapshots();
        List<Transform> justOpened = null;

        foreach (var c in containers.Values)
        {
            if (c == null) continue;
            for (int i = 0; i < c.childCount; i++)
            {
                var t = c.GetChild(i);
                if (IsBar(t) || IsTransient(t)) continue;
                bool active = t.gameObject.activeSelf;
                bool opened = windowActive.TryGetValue(t, out bool wasActive)
                    ? active && !wasActive  // hidden window shown again
                    : active;               // brand new window
                windowActive[t] = active;
                if (opened) (justOpened ?? (justOpened = new List<Transform>())).Add(t);
            }
        }

        // Move newly opened windows right away, before they're drawn in the game's default spot
        if (justOpened != null)
            foreach (var t in justOpened)
            {
                if (PlaceDialog(t)) continue; // dialogs follow the app that opened them
                if (CenterOnClickedMonitor(t)) continue; // windows like Properties open centered on the monitor you're using
                if (RestorePlacement(t, true)) openedWindows[t] = now;
            }

        // For a short moment, keep them there in case the game repositions them a frame later
        if (openedWindows.Count > 0)
        {
            foreach (var kv in openedWindows.ToList())
            {
                if (kv.Key == null || now - kv.Value > WindowSettleTime) { openedWindows.Remove(kv.Key); continue; }
                if (kv.Key != pressedWindow) RestorePlacement(kv.Key, false);
            }
        }

        if (now >= nextPlacementRefresh)
        {
            nextPlacementRefresh = now + 2f;

            // Forget windows that have been destroyed
            foreach (var k in windowActive.Keys.Where(k => k == null).ToList()) windowActive.Remove(k);

            // Keep remembered windows' saved positions current (e.g. after resizing them)
            foreach (var c in containers.Values)
            {
                if (c == null) continue;
                foreach (Transform child in c)
                {
                    if (IsBar(child) || !child.gameObject.activeSelf || child == pressedWindow) continue;
                    if (openedWindows.ContainsKey(child) || !placements.ContainsKey(child.name)) continue;
                    if (CountOpen(child.name) == 1) RememberPlacement(child);
                }
            }

            if (placementsDirty) SavePlacements();
        }
    }

    // Puts a window back in its remembered spot. "first" is true the moment it opens,
    // and false while it's being held in place afterwards.
    bool RestorePlacement(Transform t, bool first)
    {
        var win = t as RectTransform;
        if (win == null || !win.gameObject.activeInHierarchy) return false;
        if (IsNoMemory(win.name)) return false; // the game decides where these open
        if (!placements.TryGetValue(win.name, out var p)) return false;
        if (!containers.TryGetValue(p.Display, out var target) || target == null) return false; // that monitor isn't on
        if (first && OverlapsSameApp(win, target, p.Norm))
        {
            Logger.LogInfo($"'{win.name}' would mostly cover another open copy; leaving it in the game's default spot.");
            return false;
        }

        if (win.parent != target) win.SetParent(target, false);
        FromNorm(win, target, p.Norm);
        KeepGrabbable(win, target);

        if (first)
        {
            PlaceOnTop(win, target);
            Logger.LogInfo($"Opened '{win.name}' where it was last (display {p.Display}).");
        }
        return true;
    }

    // Keeps a note of where each open window of a remembered app is. When one closes (or is
    // minimized), its last spot becomes the saved position, so the last window closed wins.
    void UpdateSnapshots()
    {
        // Save the spot of any window that has just closed
        if (openSnapshots.Count > 0)
        {
            foreach (var kv in openSnapshots.ToList())
            {
                var t = kv.Key;
                if (t != null && t.gameObject.activeSelf) continue; // still open
                openSnapshots.Remove(t);

                var s = kv.Value;
                if (!placements.TryGetValue(s.Name, out var p) || p.Display != s.Display || !Same(p.Norm, s.Norm))
                {
                    placements[s.Name] = new Placement { Display = s.Display, Norm = s.Norm };
                    placementsDirty = true;
                }
            }
        }

        // Note the current spot of every open window of a remembered app
        foreach (var kv in containers)
        {
            var c = kv.Value;
            if (c == null) continue;
            foreach (Transform child in c)
            {
                if (IsBar(child) || !child.gameObject.activeSelf) continue;
                if (!placements.ContainsKey(child.name) || openedWindows.ContainsKey(child)) continue;
                var win = child as RectTransform;
                if (win == null) continue;

                if (!openSnapshots.TryGetValue(child, out var s))
                    openSnapshots[child] = s = new Snapshot { Name = child.name };
                s.Display = kv.Key;
                s.Norm = ToNorm(win, c);
            }
        }
    }

    
    int CountOpen(string name)
    {
        int count = 0;
        foreach (var c in containers.Values)
        {
            if (c == null) continue;
            foreach (Transform child in c)
                if (child.name == name && child.gameObject.activeSelf) count++;
        }
        return count;
    }

    void RememberPlacement(Transform t)
    {
        var win = t as RectTransform;
        var container = t.parent as RectTransform;
        int display = DisplayOf(container);
        if (win == null || display < 0 || IsBar(t) || IsNoMemory(t.name)) return;

        Rect norm = ToNorm(win, container);
        if (placements.TryGetValue(win.name, out var p) && p.Display == display && Same(p.Norm, norm)) return;

        placements[win.name] = new Placement { Display = display, Norm = norm };
        placementsDirty = true;
    }

    // Would this window, placed at its saved spot, cover a large part of another open copy of the same app?
    bool OverlapsSameApp(RectTransform win, RectTransform container, Rect n)
    {
        // Where the window would end up (the same math FromNorm uses)
        Rect c = container.rect;
        float w = n.width * c.width, h = n.height * c.height;
        float px = c.xMin + n.x * c.width, py = c.yMin + n.y * c.height;
        var target = new Rect(px - win.pivot.x * w, py - win.pivot.y * h, w, h);

        foreach (Transform other in container)
        {
            if (other == win || other.name != win.name || !other.gameObject.activeSelf) continue;
            var o = LocalRectOf((RectTransform)other);

            float overlapW = Mathf.Min(target.xMax, o.xMax) - Mathf.Max(target.xMin, o.xMin);
            float overlapH = Mathf.Min(target.yMax, o.yMax) - Mathf.Max(target.yMin, o.yMin);
            if (overlapW <= 0f || overlapH <= 0f) continue;

            float smaller = Mathf.Min(target.width * target.height, o.width * o.height);
            if (smaller > 0f && overlapW * overlapH / smaller >= SameAppOverlapLimit) return true;
        }
        return false;
    }

    // Stores a window's position (its pivot point) and size as fractions of its monitor, so they
    // still make sense if the resolution changes
    static Rect ToNorm(RectTransform win, RectTransform container)
    {
        Rect c = container.rect;
        Vector2 size = win.rect.size;
        Vector3 p = win.localPosition;
        return new Rect((p.x - c.xMin) / c.width, (p.y - c.yMin) / c.height, size.x / c.width, size.y / c.height);
    }

    static void FromNorm(RectTransform win, RectTransform container, Rect n)
    {
        Rect c = container.rect;
        win.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, n.width * c.width);
        win.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, n.height * c.height);
        win.localPosition = new Vector3(c.xMin + n.x * c.width, c.yMin + n.y * c.height, win.localPosition.z);
    }

    static bool Same(Rect a, Rect b)
    {
        const float eps = 0.001f;
        return Mathf.Abs(a.x - b.x) < eps && Mathf.Abs(a.y - b.y) < eps
            && Mathf.Abs(a.width - b.width) < eps && Mathf.Abs(a.height - b.height) < eps;
    }

    void LoadPlacements()
    {
        try
        {
            if (!File.Exists(PlacementsFile)) return;
            foreach (var line in File.ReadAllLines(PlacementsFile))
            {
                var parts = line.Split('\t');
                if (parts.Length != 6) continue;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int display)) continue;

                var f = new float[4];
                bool ok = true;
                for (int i = 0; i < 4; i++)
                    ok &= float.TryParse(parts[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i]);
                if (ok) placements[parts[0]] = new Placement { Display = display, Norm = new Rect(f[0], f[1], f[2], f[3]) };
            }
            // Clear any positions saved for windows the game now places itself
            var stale = placements.Keys.Where(IsNoMemory).ToList();
            foreach (var k in stale) placements.Remove(k);
            if (stale.Count > 0) placementsDirty = true;
            Logger.LogInfo($"Loaded {placements.Count} remembered window position(s).");
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"Couldn't load remembered window positions: {e.Message}");
        }
    }

    void SavePlacements()
    {
        placementsDirty = false;
        try
        {
            var lines = placements.Select(kv => string.Join("\t",
                kv.Key,
                kv.Value.Display.ToString(CultureInfo.InvariantCulture),
                F(kv.Value.Norm.x), F(kv.Value.Norm.y), F(kv.Value.Norm.width), F(kv.Value.Norm.height)));
            File.WriteAllLines(PlacementsFile, lines.ToArray());
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"Couldn't save remembered window positions: {e.Message}");
        }
    }

    static string F(float v)
    {
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    void ForgetPlacements()
    {
        placements.Clear();
        placementsDirty = false;
        try { if (File.Exists(PlacementsFile)) File.Delete(PlacementsFile); } catch { }
        Logger.LogInfo("Forgot all remembered window positions.");
    }

    // ---------------- Background ----------------

    class Picture
    {
        public Texture Texture;
        public Rect Uv = new Rect(0f, 0f, 1f, 1f);
        public Color Color = Color.white;
        public string Description;
    }

    void SyncBackgrounds()
    {
        if (secondaryBackgrounds.Count == 0) return;

        // Keep extra monitors black while the boot or OS install screens are up
        if (ExcludedScreenShowing())
        {
            foreach (var b in secondaryBackgrounds.Values) if (b != null) b.enabled = false;
            return;
        }

        var pic = FindBackgroundPicture();
        string desc = pic == null ? "none found" : pic.Description;
        if (desc != lastBackgroundDescription)
        {
            Logger.LogInfo($"Desktop background: {desc}");
            lastBackgroundDescription = desc;
        }

        foreach (var kv in secondaryBackgrounds)
        {
            var bg = kv.Value;
            if (bg == null) continue;
            bg.enabled = pic != null;
            if (pic == null) continue;
            bg.texture = pic.Texture;
            bg.uvRect = FillUv(pic, DisplaySize(kv.Key));
            bg.color = pic.Color;
        }
    }

    // Crops the wallpaper to fill the monitor without stretching it (like Windows' "Fill")
    static Rect FillUv(Picture pic, Vector2 screen)
    {
        Rect uv = pic.Uv;
        if (pic.Texture == null || screen.x <= 0f || screen.y <= 0f) return uv;

        float imgW = uv.width * pic.Texture.width;
        float imgH = uv.height * pic.Texture.height;
        if (imgW <= 0f || imgH <= 0f) return uv;

        float imgAspect = imgW / imgH;
        float screenAspect = screen.x / screen.y;
        if (imgAspect > screenAspect)
        {
            float newWidth = uv.width * screenAspect / imgAspect; // image is wider: trim the sides
            uv.x += (uv.width - newWidth) / 2f;
            uv.width = newWidth;
        }
        else if (imgAspect < screenAspect)
        {
            float newHeight = uv.height * imgAspect / screenAspect; // image is taller: trim top and bottom
            uv.y += (uv.height - newHeight) / 2f;
            uv.height = newHeight;
        }
        return uv;
    }

    Picture FindBackgroundPicture()
    {
        // 1. Manual override from the config file
        if (!string.IsNullOrEmpty(backgroundPath.Value))
        {
            var go = GameObject.Find(backgroundPath.Value);
            if (go == null)
            {
                LogOnce($"BackgroundPath '{backgroundPath.Value}' wasn't found.");
                return null;
            }
            return ToPicture(go, $"BackgroundPath '{backgroundPath.Value}'");
        }

        // 2. The game's own variables
        foreach (var source in ParseList(backgroundSources.Value))
        {
            var pic = ToPicture(ReadGameVariable(source), source);
            if (pic != null) return pic;
        }

        // 3. Fall back to scanning the screen
        var g = AutoDetectBackground();
        return g == null ? null : ToPicture(g, "auto-detect");
    }

    // Reads ClassName.variableName from the game, whether it's shared (static) or on an object in the scene
    object ReadGameVariable(string source)
    {
        int dot = source.LastIndexOf('.');
        if (dot <= 0)
        {
            LogOnce($"Background source '{source}' should look like ClassName.variableName.");
            return null;
        }
        string typeName = source.Substring(0, dot);
        string memberName = source.Substring(dot + 1);

        if (!typeCache.TryGetValue(typeName, out var type))
        {
            type = AccessTools.TypeByName(typeName);
            typeCache[typeName] = type;
        }
        if (type == null)
        {
            LogOnce($"Background source: class '{typeName}' wasn't found in the game.");
            return null;
        }

        var field = AccessTools.Field(type, memberName);
        var prop = field == null ? AccessTools.Property(type, memberName) : null;
        if (field == null && prop == null)
        {
            LogOnce($"Background source: '{memberName}' wasn't found in {typeName}.");
            return null;
        }

        bool isStatic = field != null ? field.IsStatic : prop.GetGetMethod(true).IsStatic;
        object instance = null;
        if (!isStatic)
        {
            if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                LogOnce($"Background source: {typeName} isn't a Unity object, so the mod can't find it in the scene.");
                return null;
            }
            instance = FindObjectOfType(type);
            if (instance == null) return null; // not loaded yet
        }

        try
        {
            return field != null ? field.GetValue(instance) : prop.GetValue(instance, null);
        }
        catch (System.Exception e)
        {
            LogOnce($"Background source: couldn't read {source}: {e.Message}");
            return null;
        }
    }

    // Turns whatever the game gave us into something the extra monitors can show
    Picture ToPicture(object value, string source)
    {
        if (value == null) return null;
        if (value is UnityEngine.Object uo && uo == null) return null; // destroyed

        if (value is GameObject go) value = go.GetComponent<Graphic>();
        else if (value is Component comp && !(value is Graphic)) value = comp.GetComponent<Graphic>();
        if (value == null || (value is UnityEngine.Object uo2 && uo2 == null))
        {
            LogOnce($"Background source {source}: no image found on it.");
            return null;
        }

        var pic = new Picture();
        switch (value)
        {
            case RawImage raw:
                if (raw.texture == null) return null;
                pic.Texture = raw.texture;
                pic.Uv = raw.uvRect;
                pic.Color = raw.color;
                pic.Description = $"{source} -> RawImage on '{GetPath(raw.transform)}', texture '{raw.texture.name}' {raw.texture.width}x{raw.texture.height}, color {raw.color}";
                break;

            case Image img:
                if (img.sprite == null) return null;
                SetFromSprite(pic, img.sprite);
                pic.Color = img.color;
                pic.Description = $"{source} -> Image on '{GetPath(img.transform)}', sprite '{img.sprite.name}' {img.sprite.texture.width}x{img.sprite.texture.height}, color {img.color}";
                break;

            case Sprite sprite:
                SetFromSprite(pic, sprite);
                pic.Description = $"{source} -> sprite '{sprite.name}' {sprite.texture.width}x{sprite.texture.height}";
                break;

            case Texture tex:
                pic.Texture = tex;
                pic.Description = $"{source} -> texture '{tex.name}' {tex.width}x{tex.height}";
                break;

            default:
                LogOnce($"Background source {source} holds a {value.GetType().Name}, which the mod doesn't know how to display.");
                return null;
        }
        return pic;
    }

    static void SetFromSprite(Picture pic, Sprite s)
    {
        pic.Texture = s.texture;
        var r = s.textureRect;
        pic.Uv = new Rect(r.x / s.texture.width, r.y / s.texture.height, r.width / s.texture.width, r.height / s.texture.height);
    }

    Graphic AutoDetectBackground()
    {
        // First visible, (nearly) full-screen image directly under the main canvas
        var root = (RectTransform)mainCanvas.transform;
        foreach (Transform child in root)
        {
            if (!child.gameObject.activeInHierarchy) continue;
            if (IsExcluded(child)) continue;
            var rt = child as RectTransform;
            var g = child.GetComponent<Graphic>();
            if (rt == null || g == null || !g.enabled || g.color.a <= 0.01f) continue;

            bool hasPicture = (g is RawImage raw && raw.texture != null) || (g is Image img && img.sprite != null);
            if (!hasPicture) continue;

            if (rt.rect.width < root.rect.width * 0.9f || rt.rect.height < root.rect.height * 0.9f) continue;
            return g;
        }

        if (!loggedNoBackground)
        {
            loggedNoBackground = true;
            Logger.LogInfo("Auto-detect found no background image. Objects directly under the canvas:");
            foreach (Transform child in root)
            {
                var g = child.GetComponent<Graphic>();
                var rt = child as RectTransform;
                Logger.LogInfo($"  {child.name}  active={child.gameObject.activeInHierarchy}  " +
                               $"image={(g == null ? "none" : g.GetType().Name)}  size={(rt == null ? "?" : rt.rect.size.ToString())}");
            }
        }
        return null;
    }

    void LogOnce(string message)
    {
        if (loggedMessages.Add(message)) Logger.LogInfo(message);
    }

    bool IsExcluded(Transform child)
    {
        return ParseList(excludedScreens.Value).Contains(child.name);
    }

    bool ExcludedScreenShowing()
    {
        var root = mainCanvas.transform;
        foreach (var name in ParseList(excludedScreens.Value))
        {
            var t = root.Find(name);
            if (t == null || !t.gameObject.activeInHierarchy) continue;

            // Some games hide screens by fading them out instead of switching them off
            var fade = t.GetComponent<CanvasGroup>();
            if (fade != null && fade.alpha <= 0.01f) continue;

            return true;
        }
        return false;
    }

    // ---------------- Dragging ----------------

    void LateUpdateInner()
    {
        if (showMenu || pressedWindow == null)
        {
            // Clean up if the window was closed mid-drag or the menu opened
            if (!ReferenceEquals(pressedWindow, null) || shownZone != SnapZone.None) EndPress();
            return;
        }

        bool released = Input.GetMouseButtonUp(0);
        if (!released && !Input.GetMouseButton(0)) { EndPress(); return; }

        // Only treat it as a drag once the game actually starts moving the window or icon
        if (!isWindowDrag)
        {
            if ((pressedWindow.localPosition - pressedWindowStartPos).sqrMagnitude < 0.01f)
            {
                if (released) EndPress();
                return;
            }
            isWindowDrag = true;
            if (!pressedIsIcon) RestoreIfSnapped();
        }

        // Windows move between the window layers, icons between the icon layers
        var targets = pressedIsIcon ? iconContainers : containers;

        GetMouse(out int mouseDisplay, out Vector2 mousePos);
        if (targets.TryGetValue(mouseDisplay, out var mouseContainer) && mouseContainer != null)
        {
            var currentContainer = (RectTransform)pressedWindow.parent;
            if (mouseContainer != currentContainer)
            {
                pressedWindow.SetParent(mouseContainer, false);
                SyncNestedCanvases(pressedWindow, mouseDisplay);
                if (!pressedIsIcon) PlaceOnTop(pressedWindow, mouseContainer);
                ownsDrag = true;
                Logger.LogInfo($"Dragged {(pressedIsIcon ? "icon" : "window")} '{pressedWindow.name}' onto display {mouseDisplay}.");
            }

            // After crossing monitors (or restoring from a snap), the mod positions it itself
            if (ownsDrag && ScreenToLocal(mouseContainer, mousePos, out Vector2 local))
            {
                Vector3 p = pressedWindow.localPosition;
                pressedWindow.localPosition = new Vector3(local.x + grabOffset.x, local.y + grabOffset.y, p.z);
            }

            UpdateSnapZone(pressedIsIcon ? -1 : mouseDisplay, mousePos); // icons don't snap
        }
        else
        {
            UpdateSnapZone(-1, Vector2.zero);
        }

        if (released)
        {
            if (pressedIsIcon) { EndPress(); return; } // icons: the game's Align to Grid takes care of the rest

            var win = (RectTransform)pressedWindow;
            if (shownZone != SnapZone.None
                && containers.TryGetValue(zoneDisplay, out var snapContainer)
                && snapContainer != null && win.parent == snapContainer)
            {
                ApplySnap(win, snapContainer, zoneDisplay, shownZone);
            }
            else
            {
                KeepGrabbable(win, win.parent as RectTransform);
            }
            RememberPlacement(win);
            EndPress();
        }
    }

    void BeginPress()
    {
        EndPress();

        GetMouse(out lastClickDisplay, out _); // remember which monitor you're working on

        // Forget windows that have been closed
        foreach (var k in restoreSizes.Keys.Where(k => k == null).ToList()) restoreSizes.Remove(k);

        var win = DraggableUnderCursor(out bool isIcon);
        if (win == null) return;
        if (isIcon)
        {
            return; // icon dragging is fucked
        }
        GetMouse(out _, out Vector2 mousePos);
        if (!ScreenToLocal((RectTransform)win.parent, mousePos, out Vector2 local)) return;

        pressedWindow = win;
        pressedWindowStartPos = win.localPosition;
        grabOffset = new Vector2(win.localPosition.x - local.x, win.localPosition.y - local.y);
    }

    void EndPress()
    {
        pressedWindow = null;
        isWindowDrag = false;
        ownsDrag = false;
        pressedIsIcon = false;
        pendingZone = SnapZone.None;
        shownZone = SnapZone.None;
        zoneDisplay = -1;
        HidePreview();
    }

    // ---------------- Snapping ----------------

    SnapZone ZoneAt(int display, Vector2 pos)
    {
        Vector2 size = DisplaySize(display);
        float w = size.x, h = size.y;
        float edge = snapEdgeSize.Value;
        float cw = w * snapCornerSize.Value, ch = h * snapCornerSize.Value;

        bool left = pos.x <= edge;
        bool right = pos.x >= w - 1 - edge;
        bool top = pos.y >= h - 1 - edge;
        bool bottom = pos.y <= edge;

        if (top)
        {
            if (pos.x <= cw) return SnapZone.TopLeft;
            if (pos.x >= w - cw) return SnapZone.TopRight;
            return SnapZone.Top;
        }
        if (left)
        {
            if (pos.y >= h - ch) return SnapZone.TopLeft;
            if (pos.y <= ch) return SnapZone.BottomLeft;
            return SnapZone.Left;
        }
        if (right)
        {
            if (pos.y >= h - ch) return SnapZone.TopRight;
            if (pos.y <= ch) return SnapZone.BottomRight;
            return SnapZone.Right;
        }
        if (bottom)
        {
            if (pos.x <= cw) return SnapZone.BottomLeft;
            if (pos.x >= w - cw) return SnapZone.BottomRight;
        }
        return SnapZone.None;
    }

    void UpdateSnapZone(int display, Vector2 pos)
    {
        var zone = (snapEnabled.Value && display >= 0) ? ZoneAt(display, pos) : SnapZone.None;

        if (zone == SnapZone.None)
        {
            pendingZone = SnapZone.None;
            if (shownZone != SnapZone.None)
            {
                shownZone = SnapZone.None;
                HidePreview();
            }
            return;
        }

        if (zone != pendingZone || display != zoneDisplay)
        {
            // Once a preview is showing, moving between zones on the same monitor switches instantly
            bool alreadyShowingHere = shownZone != SnapZone.None && display == zoneDisplay;
            pendingZone = zone;
            zoneDisplay = display;
            zoneEnteredAt = Time.unscaledTime;

            if (alreadyShowingHere) shownZone = zone;
            else if (shownZone != SnapZone.None)
            {
                shownZone = SnapZone.None;
                HidePreview();
            }
        }

        // First time at an edge: wait a moment, so passing through to another monitor doesn't trigger it
        if (shownZone == SnapZone.None && Time.unscaledTime - zoneEnteredAt >= snapDelay.Value)
            shownZone = pendingZone;

        if (shownZone != SnapZone.None) UpdatePreview(zoneDisplay, shownZone);
    }

    Rect SnapRect(RectTransform container, SnapZone zone)
    {
        Rect a = WorkArea(container);
        float hw = a.width / 2f, hh = a.height / 2f;

        switch (zone)
        {
            case SnapZone.Left:        return new Rect(a.xMin, a.yMin, hw, a.height);
            case SnapZone.Right:       return new Rect(a.xMin + hw, a.yMin, hw, a.height);
            case SnapZone.TopLeft:     return new Rect(a.xMin, a.yMin + hh, hw, hh);
            case SnapZone.TopRight:    return new Rect(a.xMin + hw, a.yMin + hh, hw, hh);
            case SnapZone.BottomLeft:  return new Rect(a.xMin, a.yMin, hw, hh);
            case SnapZone.BottomRight: return new Rect(a.xMin + hw, a.yMin, hw, hh);
            default:                   return a; // Top = maximize
        }
    }

    void ApplySnap(RectTransform win, RectTransform container, int display, SnapZone zone)
    {
        if (!restoreSizes.ContainsKey(win)) restoreSizes[win] = win.rect.size;
        SetLocalRect(win, SnapRect(container, zone));
        Logger.LogInfo($"Snapped '{win.name}' to {zone} on display {display}.");
    }

    // When a snapped window is dragged, give it back its original size, keeping the cursor
    // at the same spot along the title bar (like Windows does)
    void RestoreIfSnapped()
    {
        if (!restoreSizes.TryGetValue(pressedWindow, out var size)) return;
        restoreSizes.Remove(pressedWindow);

        var rt = (RectTransform)pressedWindow;
        var container = (RectTransform)pressedWindow.parent;
        GetMouse(out _, out Vector2 mousePos);
        if (!ScreenToLocal(container, mousePos, out Vector2 cursor)) return;

        Rect old = LocalRectOf(rt);
        float fx = old.width > 0f ? (cursor.x - old.xMin) / old.width : 0.5f;
        var s = rt.localScale;
        float newW = size.x * s.x;
        float newH = size.y * s.y;
        float fromTop = Mathf.Min(old.yMax - cursor.y, newH);

        float left = cursor.x - fx * newW;
        float top = cursor.y + fromTop;
        SetLocalRect(rt, new Rect(left, top - newH, newW, newH));

        grabOffset = new Vector2(rt.localPosition.x - cursor.x, rt.localPosition.y - cursor.y);
        ownsDrag = true;
    }

    // ---------------- Snap preview ----------------

    SnapPreview GetPreview(int display)
    {
        if (previews.TryGetValue(display, out var p) && p.Root != null) return p;

        p = new SnapPreview();
        p.Root = new GameObject($"MM_SnapPreview_{display}");
        DontDestroyOnLoad(p.Root);

        var canvas = p.Root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.targetDisplay = display;
        canvas.sortingOrder = 32000; // above windows, below the F9 menu shade

        var box = new GameObject("Box", typeof(RectTransform));
        p.Box = (RectTransform)box.transform;
        p.Box.SetParent(p.Root.transform, false);
        PinBottomLeft(p.Box);
        p.Fill = box.AddComponent<Image>();
        p.Fill.raycastTarget = false;

        // Four thin edges make the border
        p.Edges = new Image[4];
        const float thickness = 3f;
        for (int i = 0; i < 4; i++)
        {
            var edge = new GameObject("Edge", typeof(RectTransform));
            var ert = (RectTransform)edge.transform;
            ert.SetParent(p.Box, false);
            switch (i)
            {
                case 0: // top
                    ert.anchorMin = new Vector2(0f, 1f); ert.anchorMax = Vector2.one;
                    ert.pivot = new Vector2(0.5f, 1f); ert.sizeDelta = new Vector2(0f, thickness);
                    break;
                case 1: // bottom
                    ert.anchorMin = Vector2.zero; ert.anchorMax = new Vector2(1f, 0f);
                    ert.pivot = new Vector2(0.5f, 0f); ert.sizeDelta = new Vector2(0f, thickness);
                    break;
                case 2: // left
                    ert.anchorMin = Vector2.zero; ert.anchorMax = new Vector2(0f, 1f);
                    ert.pivot = new Vector2(0f, 0.5f); ert.sizeDelta = new Vector2(thickness, 0f);
                    break;
                default: // right
                    ert.anchorMin = new Vector2(1f, 0f); ert.anchorMax = Vector2.one;
                    ert.pivot = new Vector2(1f, 0.5f); ert.sizeDelta = new Vector2(thickness, 0f);
                    break;
            }
            ert.anchoredPosition = Vector2.zero;
            var img = edge.AddComponent<Image>();
            img.raycastTarget = false;
            p.Edges[i] = img;
        }

        p.Root.SetActive(false);
        previews[display] = p;
        return p;
    }

    void UpdatePreview(int display, SnapZone zone)
    {
        if (!containers.TryGetValue(display, out var container) || container == null) return;

        var p = GetPreview(display);
        Rect target = LocalToScreen(container, SnapRect(container, zone));

        if (!p.Root.activeSelf)
        {
            HidePreview();
            // Start from the window's current size and grow into place
            p.Current = LocalToScreen(container, LocalRectOf((RectTransform)pressedWindow));
            ApplyPreviewColors(p);
            p.Root.SetActive(true);
        }

        float t = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
        p.Current = new Rect(Vector2.Lerp(p.Current.position, target.position, t),
                             Vector2.Lerp(p.Current.size, target.size, t));
        p.Box.anchoredPosition = p.Current.position;
        p.Box.sizeDelta = p.Current.size;
    }

    // Matches the preview to the window's own outline color when it has one
    void ApplyPreviewColors(SnapPreview p)
    {
        Color accent = new Color(0.35f, 0.65f, 1f);
        var outline = pressedWindow != null ? pressedWindow.Find("Dialog/Outline") : null;
        var g = outline != null ? outline.GetComponent<Graphic>() : null;
        if (g != null) accent = new Color(g.color.r, g.color.g, g.color.b);

        p.Fill.color = new Color(accent.r, accent.g, accent.b, 0.18f);
        foreach (var e in p.Edges) e.color = new Color(accent.r, accent.g, accent.b, 0.85f);
    }

    void HidePreview()
    {
        foreach (var p in previews.Values)
            if (p.Root != null && p.Root.activeSelf) p.Root.SetActive(false);
    }

    // ---------------- Helpers ----------------

    void LogDebug()
    {
        markerCount++;
        Logger.LogInfo($"===== Marker {markerCount} =====");
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static string GetPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }

    static Vector2 DisplaySize(int display)
    {
        if (display == 0) return new Vector2(Screen.width, Screen.height);
        var d = Display.displays[display];
        return new Vector2(d.renderingWidth, d.renderingHeight);
    }

    // A child's rectangle in its parent's coordinates
    static Rect LocalRectOf(RectTransform rt)
    {
        var r = rt.rect;
        var s = rt.localScale;
        var p = rt.localPosition;
        return new Rect(p.x + r.xMin * s.x, p.y + r.yMin * s.y, r.width * s.x, r.height * s.y);
    }

    // Resizes and moves a window so it exactly fills a rectangle in its parent's coordinates
    static void SetLocalRect(RectTransform rt, Rect target)
    {
        var s = rt.localScale;
        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, target.width / s.x);
        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, target.height / s.y);
        var r = rt.rect;
        rt.localPosition = new Vector3(target.xMin - r.xMin * s.x, target.yMin - r.yMin * s.y, rt.localPosition.z);
    }

    // Converts a rectangle in a container's coordinates to screen pixels on that container's monitor
    static Rect LocalToScreen(RectTransform container, Rect r)
    {
        var canvas = container.GetComponentInParent<Canvas>().rootCanvas;
        Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, container.TransformPoint(r.min));
        Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, container.TransformPoint(r.max));
        return Rect.MinMaxRect(a.x, a.y, b.x, b.y);
    }

       // Finds the window or desktop icon under the cursor that can be dragged between monitors
    Transform DraggableUnderCursor(out bool isIcon)
    {
        isIcon = false;
        var es = EventSystem.current;
        if (es == null) return null;

        var ped = new PointerEventData(es) { position = Input.mousePosition };
        var hits = new List<RaycastResult>();
        es.RaycastAll(ped, hits);

        foreach (var hit in hits)
        {
            var item = FindDraggable(hit.gameObject.transform, out isIcon);
            if (item != null) return item;
        }
        isIcon = false;
        return null;
    }

    Transform FindDraggable(Transform t, out bool isIcon)
    {
        isIcon = false;
        while (t != null)
        {
            var parent = t.parent;
            if (parent != null)
            {
                if (IconDisplayOf(parent) >= 0) { isIcon = true; return t; } // a desktop icon
                if (DisplayOf(parent) >= 0)
                    return (IsBar(t) || t == mainIcons || IsTransient(t)) ? null : t; // a window (never the bars, the icon layer, or temporary objects)
            }
            t = parent;
        }
        return null;
    }

    int IconDisplayOf(Transform container)
    {
        foreach (var kv in iconContainers)
            if (kv.Value == container) return kv.Key;
        return -1;
    }


    static bool IsTransient(Transform t)
    {
        return TransientNames.Contains(t.name) || t.GetComponent<GH::ContextualMenu>() != null; // includes every kind of right-click menu
    }

    static bool IsNoMemory(string name)
    {
        return NoMemoryPrefixes.Any(p => name.StartsWith(p, System.StringComparison.Ordinal));
    }

    // Desktop icons: the game drags a copy of the icon around and only moves the real icon when you let go.
    // This shows the copy on whichever monitor the cursor is on, and puts the real icon on that monitor when dropped.
    void UpdateIconDrag()
    {
        if (pressedIcon == null) return;

        bool released = Input.GetMouseButtonUp(0);
        if (!released && !Input.GetMouseButton(0)) { pressedIcon = null; dragCopy = null; return; }

        GetMouse(out int display, out Vector2 mousePos);

        // Keep the dragged copy under the cursor, on the right monitor
        if (dragCopy == null) dragCopy = FindDragCopy();
        if (dragCopy != null)
        {
            if (display != 0 && containers.TryGetValue(display, out var layer) && layer != null)
            {
                if (dragCopy.parent != layer) dragCopy.SetParent(layer, false);
                dragCopy.SetAsLastSibling();
                CenterOnCursor((RectTransform)dragCopy, layer, mousePos);
            }
            else if (display == 0 && dragCopy.parent != mainContainer)
            {
                dragCopy.SetParent(mainContainer, false); // back on the main screen, where the game positions it itself
            }
        }

        if (!released) return;

        var icon = pressedIcon as RectTransform;
        pressedIcon = null;
        dragCopy = null;
        if (icon == null) return; // the game removed it (for example, the file was moved into a folder)

        // Dropped onto a window (like a folder or a program): let the game handle it
        var under = DraggableUnderCursor(out bool underIsIcon);
        if (under != null && !underIsIcon) return;

        if (display != 0)
        {
            if (!iconContainers.TryGetValue(display, out var target) || target == null) return;
            if (icon.parent != target) icon.SetParent(target, false);
            CenterOnCursor(icon, target, mousePos);
            Logger.LogInfo($"Moved icon '{icon.name}' to display {display}.");
        }
        else if (mainIcons != null && icon.parent != mainIcons)
        {
            icon.SetParent(mainIcons, false);
            CenterOnCursor(icon, mainIcons, mousePos);
            Logger.LogInfo($"Moved icon '{icon.name}' back to the main screen.");
        }
    }

    Transform FindDragCopy()
    {
        foreach (var c in containers.Values)
        {
            if (c == null) continue;
            var t = c.Find("CopyIcon(Clone)");
            if (t != null && t.gameObject.activeInHierarchy) return t;
        }
        return null;
    }

    // Places something so its center sits under the cursor
    void CenterOnCursor(RectTransform rt, RectTransform layer, Vector2 mousePos)
    {
        if (!ScreenToLocal(layer, mousePos, out Vector2 local)) return;
        Vector2 center = Vector2.Scale(rt.rect.center, rt.localScale);
        rt.localPosition = new Vector3(local.x - center.x, local.y - center.y, rt.localPosition.z);
    }


    // ---------------- Game patches ----------------

    // The game's dialog code (for example, the Save Program window) looks for the desktop by working up from
    // the app's window, and crashes if that window is on another monitor. These patches briefly put the app's
    // window back inside the desktop while that code runs, then return it and move the dialog next to it.
        void PatchGame()
    {
        var harmony = new Harmony(Guid);

        // Dialogs (like Save Program) opened by apps on other monitors
        PatchMethod(harmony, AccessTools.Method("Ventana:CreateDialogFinder"), "Ventana.CreateDialogFinder",
            prefix: nameof(DialogPrefix), finalizer: nameof(DialogFinalizer));

        // Updates the game sends to a window (like a folder's contents) also reach windows on other monitors
        var clientMethods = AccessTools.TypeByName("PlayerClientMethods");
        var getVentana = clientMethods == null ? null : AccessTools.Method(clientMethods, "GetVentana", new[] { typeof(int), typeof(bool) });
        PatchMethod(harmony, getVentana, "PlayerClientMethods.GetVentana", postfix: nameof(GetVentanaPostfix));

        // Right-click menus for things on other monitors
        try { contextualMenuRef = AccessTools.FieldRefAccess<GH::InteractableContextual, GH::ContextualMenu>("contextualMenu"); }
        catch (System.Exception e) { Logger.LogWarning($"Couldn't access InteractableContextual.contextualMenu: {e.Message}"); }
        PatchMethod(harmony, AccessTools.Method(typeof(GH::InteractableContextual), "Start"), "InteractableContextual.Start",
            postfix: nameof(ContextStartPostfix));
        // Every kind of right-click menu: files and folders, text (Notepad, web pages), and the Terminal
        foreach (var menuType in new[] { typeof(GH::ContextualMenu), typeof(GH::ContextualMenuClipboard), typeof(GH::ContextualMenuTerminal) })
            foreach (var m in AccessTools.GetDeclaredMethods(menuType).Where(m => m.Name == "OpenMenu"))
                PatchMethod(harmony, m, $"{menuType.Name}.OpenMenu", prefix: nameof(MenuOpenPrefix), postfix: nameof(MenuOpenPostfix));

        // Notepad's right-click looks for its menu from the window, so let it run as if the window were on the main desktop
        PatchMethod(harmony, AccessTools.Method(typeof(GH::ClipboardNotepad), "OnPointerClick"), "ClipboardNotepad.OnPointerClick",
            prefix: nameof(DialogPrefix), finalizer: nameof(InDesktopFinalizer));

        // Text fields (like the Browser's address bar) have their own right-click handler that also looks for its menu from the window
        PatchMethod(harmony, AccessTools.DeclaredMethod(typeof(GH::ClipboardInteractableContextual), "OnPointerClick"),
            "ClipboardInteractableContextual.OnPointerClick", prefix: nameof(DialogPrefix), finalizer: nameof(InDesktopFinalizer));

        // Menus go back to the desktop as soon as they close, so the game always finds them where it expects
        foreach (var menuType in new[] { typeof(GH::ContextualMenu), typeof(GH::ContextualMenuClipboard), typeof(GH::ContextualMenuTerminal) })
            foreach (var m in AccessTools.GetDeclaredMethods(menuType).Where(m => m.Name == "ClearOptions"))
                PatchMethod(harmony, m, $"{menuType.Name}.ClearOptions", postfix: nameof(MenuClosedPostfix));


        // Game code that works out exactly what's under the mouse (text selection, web pages) uses the raw mouse
        // position, which on another monitor is measured from the main screen. Convert it for things on other monitors.
        var publicStatic = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
        foreach (var m in typeof(RectTransformUtility).GetMethods(publicStatic).Where(m =>
                 {
                     var p = m.GetParameters();
                     return p.Length >= 2 && p[0].ParameterType == typeof(RectTransform) && p[1].ParameterType == typeof(Vector2);
                 }))
            PatchMethod(harmony, m, $"RectTransformUtility.{m.Name}", prefix: nameof(RectPointPrefix));

        var tmpUtils = AccessTools.TypeByName("TMPro.TMP_TextUtilities");
        if (tmpUtils == null) Logger.LogWarning("Couldn't find TMP_TextUtilities; text selection on other monitors may not work.");
        else
            foreach (var m in tmpUtils.GetMethods(publicStatic).Where(m =>
                     {
                         var p = m.GetParameters();
                         return p.Length >= 2 && typeof(Component).IsAssignableFrom(p[0].ParameterType) && p[1].ParameterType == typeof(Vector3);
                     }))
                PatchMethod(harmony, m, $"TMP_TextUtilities.{m.Name}", prefix: nameof(TmpPointPrefix));
    }

    void PatchMethod(Harmony harmony, System.Reflection.MethodInfo target, string label,
                     string prefix = null, string postfix = null, string finalizer = null)
    {
        if (target == null)
        {
            Logger.LogWarning($"Couldn't find {label}; some things may not work for windows on other monitors.");
            return;
        }
        try
        {
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(MultiMonitorPlugin), prefix)),
                postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(MultiMonitorPlugin), postfix)),
                finalizer: finalizer == null ? null : new HarmonyMethod(AccessTools.Method(typeof(MultiMonitorPlugin), finalizer)));
            Logger.LogInfo($"Patched {label}.");
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"Couldn't patch {label}: {e.Message}");
        }
    }


    // When the game can't find a window by its ID (because it's on another monitor), find it there instead
    static void GetVentanaPostfix(int __0, ref GH::Ventana __result)
    {
        if (__result != null || instance == null || __0 == -1) return; // -1 means "no window" to the game
        try { __result = instance.FindWindowByPID(__0); }
        catch { }
    }

    GH::Ventana FindWindowByPID(int pid)
    {
        foreach (var kv in containers)
        {
            if (kv.Key == 0 || kv.Value == null) continue; // the game already searched the main screen
            foreach (var window in kv.Value.GetComponentsInChildren<GH::Ventana>()) // visible windows only, like the game's own search
                if (window.GetPID() == pid) return window;
        }
        return null;
    }


    // Things created on another monitor can't find the game's right-click menu, so give them the real one
    static void ContextStartPostfix(GH::InteractableContextual __instance)
    {
        try
        {
            if (instance == null || contextualMenuRef == null || instance.mainMenu == null) return;
            if (contextualMenuRef(__instance) == null) contextualMenuRef(__instance) = instance.mainMenu;
        }
        catch { }
    }

    static void MenuOpenPrefix(GH::ContextualMenu __instance)
    {
        try { if (instance != null) instance.MenuComeHome(__instance.transform); }
        catch { }
    }

    static void MenuOpenPostfix(GH::ContextualMenu __instance)
    {
        try { if (instance != null) instance.MenuFollowCursor(__instance.transform); }
        catch { }
    }

    static void MenuClosedPostfix(GH::ContextualMenu __instance)
    {
        try { if (instance != null) instance.MenuComeHome(__instance.transform); }
        catch { }
    }

    // Puts a menu back where it normally lives in the desktop
    void MenuComeHome(Transform t)
    {
        if (t == null || !menuHomes.TryGetValue(t, out var home) || home.Parent == null) return;
        if (t.parent != home.Parent)
        {
            t.SetParent(home.Parent, false);
            t.SetSiblingIndex(Mathf.Min(home.Sibling, home.Parent.childCount - 1));
        }
        if (menuOnOtherMonitor == t) menuOnOtherMonitor = null;
    }

    // If the menu was opened on another monitor, move it there with its top-left corner at the cursor
    void MenuFollowCursor(Transform t)
    {
        if (t == null) return;
        GetMouse(out int display, out Vector2 mousePos);
        if (display == 0 || !popupLayers.TryGetValue(display, out var target) || target == null) return; // main screen: the game handles it
        if (!menuHomes.ContainsKey(t)) menuHomes[t] = new MenuHome { Parent = t.parent, Sibling = t.GetSiblingIndex() };
        t.SetParent(target, false);
        SyncNestedCanvases(t, display);

        if (t is RectTransform rt) LayoutRebuilder.ForceRebuildLayoutImmediate(rt); // make sure its options are laid out first
        if (!ScreenToLocal(target, mousePos, out Vector2 cursor)) return;

        Bounds b = RectTransformUtility.CalculateRelativeRectTransformBounds(target, t);
        Vector2 shift = new Vector2(cursor.x - b.min.x, cursor.y - b.max.y);
        Rect area = target.rect;
        if (b.max.x + shift.x > area.xMax) shift.x -= b.max.x + shift.x - area.xMax; // too far right: open to the left
        if (b.min.y + shift.y < area.yMin) shift.y += area.yMin - (b.min.y + shift.y); // too low: open upwards
        t.localPosition += new Vector3(shift.x, shift.y, 0f);

        menuOnOtherMonitor = t;
    }

    // The game only closes menus when you click on the main screen, so close one on another monitor
    // when you click anywhere there except on the menu itself
    void CloseStrayMenu()
    {
        if (menuOnOtherMonitor == null) return;
        GetMouse(out int display, out _);
        if (display == 0) return;                      // clicks on the main screen are handled by the game
        if (IsPointerOver(menuOnOtherMonitor)) return; // clicking one of the menu's options
        if (GH::ControlContextualMenus.Singleton != null) GH::ControlContextualMenus.Singleton.ClearMenus();
        menuOnOtherMonitor = null;
    }

    bool IsPointerOver(Transform t)
    {
        var es = EventSystem.current;
        if (es == null) return false;
        var hits = new List<RaycastResult>();
        es.RaycastAll(new PointerEventData(es) { position = Input.mousePosition }, hits);
        foreach (var hit in hits)
            if (hit.gameObject.transform.IsChildOf(t)) return true;
        return false;
    }

    // Some parts of windows (like web pages) have their own canvas inside the window. Unity only sends them clicks
    // from the monitor they're marked as being on, so keep that matched to the monitor they're actually shown on.
    void SyncNestedCanvases()
    {
        foreach (var kv in containers) SyncNestedCanvases(kv.Value, kv.Key);
        foreach (var kv in popupLayers) SyncNestedCanvases(kv.Value, kv.Key);
    }

    static void SyncNestedCanvases(Transform root, int display)
    {
        if (root == null) return;
        foreach (var c in root.GetComponentsInChildren<Canvas>(true))
            if (!c.isRootCanvas && c.targetDisplay != display) c.targetDisplay = display;
    }


    // The Browser's web pages (PowerUI) find what's under the mouse using a single caster, fixed to whichever canvas
    // the first web page started on. Keep it pointed at the monitor the cursor is on, so pages work on every monitor.
    void UpdateWebCaster()
    {
        if (mainCanvas == null) return;
        GetMouse(out int display, out _);

        GraphicRaycaster caster = null;
        if (display != 0 && secondaryCanvases.TryGetValue(display, out var canvasGO) && canvasGO != null)
            caster = canvasGO.GetComponent<GraphicRaycaster>();
        if (caster == null)
            caster = mainCanvas.GetComponent<GraphicRaycaster>();

        if (caster != null && GH::PowerUI.Input.UnityUICaster != caster)
            GH::PowerUI.Input.UnityUICaster = caster;
    }

    // Temporary: logs what's under the cursor on a right-click, and which game scripts are on it and above it
    void LogRightClickTarget()
    {
        GetMouse(out int display, out _);
        var es = EventSystem.current;
        if (es == null) return;

        var hits = new List<RaycastResult>();
        es.RaycastAll(new PointerEventData(es) { position = Input.mousePosition }, hits);
        if (hits.Count == 0)
        {
            Logger.LogInfo($"Right-click on display {display}: nothing under the cursor.");
            return;
        }

        for (int i = 0; i < Mathf.Min(3, hits.Count); i++)
        {
            var t = hits[i].gameObject.transform;
            var scripts = string.Join(", ", t.GetComponentsInParent<MonoBehaviour>(true)
                .Select(c => c.GetType().Name).Distinct().Take(20));
            Logger.LogInfo($"Right-click on display {display}, hit {i + 1}: '{GetPath(t)}' (scripts: {scripts})");
        }
    }

    static void DialogPrefix(object __instance, out TempMove __state)
    {
        __state = null;
        try { if (instance != null) __state = instance.MoveToMainForGame(__instance as Component); }
        catch { }
    }

    // Runs after the game's code, even if it hit an error
    static System.Exception DialogFinalizer(TempMove __state, System.Exception __exception)
    {
        try { if (instance != null && __state != null) instance.MoveBackAfterGame(__state); }
        catch { }
        return __exception;
    }

    TempMove MoveToMainForGame(Component caller)
    {
        if (caller == null || mainContainer == null) return null;
        var win = FindDraggable(caller.transform, out bool isIcon);
        if (win == null || isIcon) return null;

        int display = DisplayOf(win.parent);
        if (display < 0) return null;

        var state = new TempMove { Window = win, Parent = win.parent, LocalPos = win.localPosition, Display = display };
        if (display > 0) win.SetParent(mainContainer, false); // only for this moment; it goes straight back afterwards
        return state;
    }

    void MoveBackAfterGame(TempMove state)
    {
        MoveBackOnly(state);
        if (state.Window != null)
            pendingDialog = new PendingDialog { Display = state.Display, Anchor = state.Window, Until = Time.unscaledTime + 0.5f };
    }

    // Puts a window back on its own monitor after the game's code has run
    void MoveBackOnly(TempMove state)
    {
        if (state.Window == null || state.Display <= 0 || state.Parent == null) return;
        state.Window.SetParent(state.Parent, false);
        state.Window.localPosition = state.LocalPos;
    }

    // Like DialogFinalizer, but for game code that doesn't open a dialog (for example, a right-click)
    static System.Exception InDesktopFinalizer(TempMove __state, System.Exception __exception)
    {
        try { if (instance != null && __state != null) instance.MoveBackOnly(__state); }
        catch { }
        return __exception;
    }


    // Unity's "is this point in this box" helpers, used by the game with the raw mouse position
    static void RectPointPrefix(RectTransform __0, ref Vector2 __1)
    {
        try { FixScreenPoint(__0, ref __1); }
        catch { }
    }

    // TextMeshPro's "which character/word/link is at this point" helpers
    static void TmpPointPrefix(object __0, ref Vector3 __1)
    {
        try
        {
            var point = new Vector2(__1.x, __1.y);
            if (FixScreenPoint(__0 as Component, ref point)) __1 = new Vector3(point.x, point.y, __1.z);
        }
        catch { }
    }

    // If a raw mouse position is on another monitor, and the thing being checked is on that same monitor,
    // convert the position to that monitor's own coordinates. Anything else is left exactly as it is.
    static bool FixScreenPoint(Component target, ref Vector2 point)
    {
        if (target == null || instance == null || instance.activatedDisplays.Count == 0) return false;

        // Positions on the main screen (almost every call, including all of Unity's own) need nothing
        if (point.x >= 0f && point.y >= 0f && point.x < Screen.width && point.y < Screen.height) return false;

        Vector3 rel = Display.RelativeMouseAt(new Vector3(point.x, point.y, 0f));
        int display = (int)rel.z;
        if (rel == Vector3.zero || display <= 0) return false;

        var canvas = target.GetComponentInParent<Canvas>();
        if (canvas == null || canvas.rootCanvas.targetDisplay != display) return false; // not on that monitor: leave it alone

        point = new Vector2(rel.x, rel.y);
        return true;
    }

    // Puts a newly opened dialog on the same monitor as the app that asked for it, centered over that app
    bool PlaceDialog(Transform t)
    {
        if (pendingDialog == null) return false;
        if (Time.unscaledTime > pendingDialog.Until) { pendingDialog = null; return false; }
        if (t == pendingDialog.Anchor) return false;

        var p = pendingDialog;
        pendingDialog = null;

        var win = t as RectTransform;
        if (win == null) return true;
        if (p.Display == 0 || !containers.TryGetValue(p.Display, out var target) || target == null)
            return true; // opened from the main screen: leave it where the game put it

        Rect r = LocalRectOf(win);
        var anchor = p.Anchor as RectTransform;
        Vector2 center = (anchor != null && anchor.parent == target) ? LocalRectOf(anchor).center : WorkArea(target).center;

        win.SetParent(target, false);
        SetLocalRect(win, new Rect(center.x - r.width / 2f, center.y - r.height / 2f, r.width, r.height));
        PlaceOnTop(win, target);
        KeepGrabbable(win, target);
        Logger.LogInfo($"Opened '{win.name}' on display {p.Display}, next to the app that opened it.");
        return true;
    }


    // Windows the game places itself (like Properties) open in the center of the monitor you last clicked on
    bool CenterOnClickedMonitor(Transform t)
    {
        if (!IsNoMemory(t.name)) return false;

        var win = t as RectTransform;
        if (win == null || lastClickDisplay == 0) return true; // main screen: the game centers it itself
        if (!containers.TryGetValue(lastClickDisplay, out var target) || target == null) return true;

        Rect r = LocalRectOf(win);
        Vector2 center = WorkArea(target).center;
        win.SetParent(target, false);
        SetLocalRect(win, new Rect(center.x - r.width / 2f, center.y - r.height / 2f, r.width, r.height));
        PlaceOnTop(win, target);
        Logger.LogInfo($"Opened '{win.name}' in the center of display {lastClickDisplay}.");
        return true;
    }

    void GetMouse(out int display, out Vector2 pos)
    {
        Vector3 rel = Display.RelativeMouseAt(Input.mousePosition);
        if (rel == Vector3.zero) { display = 0; pos = Input.mousePosition; } // fallback: assume primary
        else { display = (int)rel.z; pos = rel; }
    }

    bool ScreenToLocal(RectTransform container, Vector2 screenPos, out Vector2 local)
    {
        var canvas = container.GetComponentInParent<Canvas>().rootCanvas;
        Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(container, screenPos, cam, out local);
    }
}

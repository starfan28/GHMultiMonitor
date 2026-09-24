using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[BepInPlugin("com.yourname.greyhack.multimonitor", "GH MultiMonitor", "0.1.0")]
public class MultiMonitorPlugin : BaseUnityPlugin
{
    ConfigEntry<int> targetDisplayIndex;
    ConfigEntry<string> windowContainerPath;
    ConfigEntry<KeyboardShortcut> moveKey;
    ConfigEntry<KeyboardShortcut> debugKey;

    Canvas mainCanvas;
    Transform mainContainer;
    Canvas secondaryCanvas;
    RectTransform secondaryContainer;
    float nextSetupAttempt;

    void Awake()
    {
        targetDisplayIndex = Config.Bind("General", "TargetDisplay", 1,
            "Monitor index to extend onto (0 = primary).");
        windowContainerPath = Config.Bind("General", "WindowContainerPath", "",
            "Hierarchy path of the transform that holds the in-game windows (find it with UnityExplorer).");
        moveKey = Config.Bind("Keys", "MoveWindow",
            new KeyboardShortcut(KeyCode.M, KeyCode.LeftControl, KeyCode.LeftAlt),
            "Moves the window under the cursor to the other monitor.");
        debugKey = Config.Bind("Keys", "LogMouse", new KeyboardShortcut(KeyCode.F8),
            "Logs raw and per-display mouse coordinates.");

        Logger.LogInfo($"Displays detected: {Display.displays.Length}");
        for (int i = 0; i < Display.displays.Length; i++)
            Logger.LogInfo($"  [{i}] {Display.displays[i].systemWidth}x{Display.displays[i].systemHeight}");
    }

    void Start()
    {
        int idx = targetDisplayIndex.Value;
        if (idx <= 0 || idx >= Display.displays.Length)
        {
            Logger.LogWarning("Target display not available; plugin idle.");
            enabled = false;
            return;
        }
        Display.displays[idx].Activate(); // cannot be undone without restarting the game
    }

    void Update()
    {
        if (debugKey.Value.IsDown())
        {
            Vector3 rel = Display.RelativeMouseAt(Input.mousePosition);
            Logger.LogInfo($"Input.mousePosition={Input.mousePosition}  RelativeMouseAt={rel} (z = display)");
        }

        if (secondaryCanvas == null && !TrySetup()) return;

        if (moveKey.Value.IsDown()) MoveWindowUnderCursor();
    }

    bool TrySetup()
    {
        if (Time.unscaledTime < nextSetupAttempt) return false;
        nextSetupAttempt = Time.unscaledTime + 1f;

        if (string.IsNullOrEmpty(windowContainerPath.Value)) return false;
        var go = GameObject.Find(windowContainerPath.Value);
        if (go == null) return false;

        mainContainer = go.transform;
        mainCanvas = go.GetComponentInParent<Canvas>().rootCanvas;
        BuildSecondaryDesktop();
        Logger.LogInfo("Secondary desktop ready.");
        return true;
    }

    void BuildSecondaryDesktop()
    {
        int idx = targetDisplayIndex.Value;
        var root = new GameObject("MM_SecondaryDesktop");

        // Camera only exists to clear display 2 each frame; otherwise it smears.
        var cam = new GameObject("MM_ClearCamera").AddComponent<Camera>();
        cam.transform.SetParent(root.transform, false);
        cam.targetDisplay = idx;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.cullingMask = 0;
        cam.depth = -100;

        var canvasGO = new GameObject("MM_Canvas");
        canvasGO.transform.SetParent(root.transform, false);
        secondaryCanvas = canvasGO.AddComponent<Canvas>();
        secondaryCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        secondaryCanvas.targetDisplay = idx;
        secondaryCanvas.sortingOrder = mainCanvas.sortingOrder;

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

        var container = new GameObject("MM_Windows", typeof(RectTransform));
        secondaryContainer = (RectTransform)container.transform;
        secondaryContainer.SetParent(canvasGO.transform, false);
        secondaryContainer.anchorMin = Vector2.zero;
        secondaryContainer.anchorMax = Vector2.one;
        secondaryContainer.offsetMin = Vector2.zero;
        secondaryContainer.offsetMax = Vector2.zero;
    }

    void MoveWindowUnderCursor()
    {
        var es = EventSystem.current;
        if (es == null) return;

        var ped = new PointerEventData(es) { position = Input.mousePosition };
        var hits = new List<RaycastResult>();
        es.RaycastAll(ped, hits);

        foreach (var hit in hits)
        {
            var win = FindWindowRoot(hit.gameObject.transform);
            if (win != null) { ToggleDisplay(win); return; }
        }
        Logger.LogInfo($"No window under cursor ({hits.Count} UI hits).");
    }

    Transform FindWindowRoot(Transform t)
    {
        while (t != null)
        {
            if (t.parent == mainContainer || t.parent == secondaryContainer) return t;
            t = t.parent;
        }
        return null;
    }

    void ToggleDisplay(Transform win)
    {
        var target = (RectTransform)(win.parent == mainContainer ? (Transform)secondaryContainer : mainContainer);
        win.SetParent(target, false);
        win.localPosition = target.rect.center; // drop it in the middle of the destination
        win.SetAsLastSibling();
        Logger.LogInfo($"Moved '{win.name}' to {(target == secondaryContainer ? "secondary" : "primary")} display.");
    }
}

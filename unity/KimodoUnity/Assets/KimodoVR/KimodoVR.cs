// KimodoVR — VR/desktop client for demonstration-driven motion generation.
// You demonstrate WHERE (touch points, walk dots, poses); a text prompt says
// WHAT; the WSL server (generation/server.py) turns that into Kimodo
// constraints and returns a full-body clip, played on a skinned character.
//
// Setup: empty GameObject -> Add Component -> KimodoVR; assign Character
// Prefab (Humanoid rig), Scene Surfaces / Obstacles; press Play. Desktop works
// with mouse+keyboard; with an OpenXR headset the same scene becomes VR.
//
// VR (the right hand carries the laser)
//   R-trigger tap    laser-place a point: surface = touch, floor = walk dot
//   R-trigger hold   trace along a surface with the hand
//   L-trigger hold   record a walking path (headset floor position)
//   X tap / hold     snap a pose / open-close the panel   (L-grip = pose alt)
//   A                generate  (selected person: queue a follow-up action)
//   B tap / hold     next prompt or select the pointed person / start typing
//   sticks           selected person: move (L), rotate + cut up/down (R);
//                    panel: move (L), scroll prompts (R l/r), distance (R f/b)
//   R-grip           clear everything
// Desktop
//   L-click surface  touch point      R-click floor  walk dot (first = spawn)
//   X pose · H sim rig (WASD/QE, RMB look, Ctrl/Shift hands) · G generate
//   J save config + generate · P/T cycle/type prompt · C clear · Tab panels
//   click a person: 1-9/T behavior · N follow-up · [ ] F cut · arrows , . R
//   move/rotate/reset · Del remove · Esc close
//   F1 tutorial · F2 input readout · F3 spectator camera (recording)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Management;
// both namespaces define these names — we mean the XR ones
using InputDevice = UnityEngine.XR.InputDevice;
using CommonUsages = UnityEngine.XR.CommonUsages;

public class KimodoVR : MonoBehaviour
{
    [Tooltip("Kimodo generation server (the WSL one, port 8123)")]
    public string serverUrl = "http://127.0.0.1:8123";
    public float duration = 6f;
    public int steps = 40;
    [Tooltip("Optional: a Humanoid character (e.g. your Mixamo import). Drag the model asset here — performers then use it instead of capsules.")]
    public GameObject characterPrefab;

    [Tooltip("Build your own scene and drag its interactive surfaces here (table tops, counters, shelves — anything with a collider you want to trace on). When non-empty, the default table/shelf/robot room is NOT created.")]
    public List<GameObject> sceneSurfaces = new List<GameObject>();
    [Tooltip("Extra furniture performers should walk around but not interact with.")]
    public List<GameObject> sceneObstacles = new List<GameObject>();
    [Tooltip("Optional: where auto-planned walks begin (e.g. the room door). If empty they start at the camera. Manual blue dots always override.")]
    public Transform entranceAnchor;
    [Tooltip("Optional: a collider enclosing the room floor (BoxCollider on an empty works). Performers are hard-clamped inside it. If empty, the room is estimated from surfaces+obstacles plus 1.2 m.")]
    public Collider roomBounds;

    string prompt = "a person stands at a table and slides their right hand along its surface";
    string[] cachedPrompts = new string[0];
    int promptIndex = -1;

    readonly List<Vector3> handPoints = new List<Vector3>();
    readonly List<float> handTimes = new List<float>();
    // Contact schedule as sent (clip times) — replayed by the character's contact IK.
    readonly List<Vector3> sentHandTargets = new List<Vector3>();
    readonly List<float> sentHandTimes = new List<float>();
    bool sentAnchorWrist;
    readonly List<Vector3> bodyPoints = new List<Vector3>();
    readonly List<Vector3> walkPoints = new List<Vector3>();
    readonly List<float> walkTimes = new List<float>();
    bool capturedInVR;

    // Snapped VR poses on the shared capture clock; sent as pose_keys.
    readonly List<float> poseTimes = new List<float>();
    readonly List<Vector3> poseL = new List<Vector3>();
    readonly List<Vector3> poseR = new List<Vector3>();
    readonly List<Vector3> poseBody = new List<Vector3>();
    readonly List<float> poseHeadY = new List<float>();   // crouches survive
    readonly List<float> poseHeading = new List<float>();

    // --- desktop rig simulator (H): camera = head, mouse-placed hand spheres
    bool simMode;
    Transform simL, simR;
    bool simLPlaced, simRPlaced;   // a grabbed hand stays where you left it
    float simReach = 0.55f;
    bool lookDragged;

    Rect roomRect;      // performers are hard-clamped inside this floor rect
    bool roomKnown;

    float lastSnapReal = -99f;   // real-time debounce for pose snapping
    float captureStart = -1f, lastSample, lTrigHeldSince = -1f;
    bool generating;
    TextMesh boardMenu, boardTut;   // VR HUD texts: prompt list + guide columns
    TextMesh vrGuideKeys;
    Transform vrHud;                // in-view panel (child of the HMD camera)
    bool vrHintShown;
    Text promptMenu;   // screen-space HUD panel (uGUI), draggable
    Image controlsPanel;                 // collapsible controls cheat-sheet
    Text controlsHeader, controlsBody, controlsKeys;
    Image mapPanel;                      // collapsible controller-map section
    Text mapHeader;
    RawImage controlsDiag;
    float diagH;
    bool controlsOpen, mapOpen;
    bool xWas, xLongFired;               // X tap-vs-hold state (VR)
    float xDownAt;
    bool bWas, bLongFired;               // B tap-vs-hold state (VR)
    float bDownAt;

    Transform ctrlL, ctrlR;              // in-world controller proxies + laser
    Transform laserBeam, laserDot;       // live-length beam + hit reticle
    Renderer laserDotRend;
    Transform vrKb;                      // virtual keyboard for VR typing
    TextMesh vrKbPreview;                // typed-text preview on the keyboard
    readonly List<TextMesh> vrKbKeys = new List<TextMesh>();
    readonly List<Renderer> vrKbCaps = new List<Renderer>();
    readonly List<string> vrKbVals = new List<string>();
    readonly List<float> vrKbW = new List<float>();
    int vrKbHover = -1;
    bool vrKbTrigWas;
    static readonly Color CapIdle = new Color(0.16f, 0.2f, 0.3f);
    static readonly Color CapHover = new Color(0.33f, 0.45f, 0.68f);

    readonly List<int> vrMenuRows = new List<int>();   // laser prompt-picking
    int vrMenuHover = -1;
    bool vrPointTrigWas;
    readonly List<int> popupRows = new List<int>();    // laser popup-picking
    int popupHover = -1;
    bool popupTrigWas;
    float rTrigDownAt = -1f;             // trigger tap (laser place) vs hold (trace)
    TextMesh promptHud;                  // always-visible current prompt (VR)
    Image typingPanel;                   // on-screen input box for T
    Text typingText;
    Text tutStrip;                       // guided tutorial: screen strip…
    TextMesh tutVr;                      // …and a head-locked strip for VR
    TextMesh vrDebug;                    // live controller-input readout (F2)
    bool vrDebugOn;                      // hidden unless summoned with F2
    int tutStep = -1;                    // tutorial OFF by default; F1 starts it
    Text statusLog;                      // status window: last command + events
    readonly List<GameObject> leftHudPanels = new List<GameObject>();
    bool leftHudOn;                      // Tab shows/hides the top-left stack
    readonly List<string> statusHist = new List<string>();
    Transform markers;
    Camera cam;
    // A spawned person: their player (mesh or capsules) plus the request that
    // made them, so a "Sims popup" can regenerate their behavior in place.
    class ChainClip
    {
        public FlatMotion m;
        public string json;
        public float cutIn;
        public float cutOut = -1f;      // -1 = full clip
        public Vector3[] cpts = new Vector3[0];
        public float[] ctimes = new float[0];
        public bool wrist;
    }

    class Performer
    {
        public KimodoCharacterPlayer cp;
        public KimodoSkeletonPlayer sp;
        public string requestJson;
        public Vector3[] contactPts;
        public float[] contactTimes;
        public bool wrist;
        public FlatMotion motion;       // playing clip (chaining anchor)
        // The performer's action SEQUENCE: entry 0 is the original, appended
        // follow-ups come after, and playback cycles through kept ranges.
        public readonly List<ChainClip> chain = new List<ChainClip>();
        public int chainIdx;
        public Vector3 Pos => cp != null ? cp.CurrentPos() : sp != null ? sp.CurrentPos() : Vector3.zero;
        public IKimodoDirectable Dir => cp != null ? (IKimodoDirectable)cp : sp;
        public void Kill()
        {
            if (cp != null) cp.Stop();
            if (sp != null) { sp.Stop(); UnityEngine.Object.Destroy(sp.gameObject); }
            cp = null; sp = null;
        }
    }
    readonly List<Performer> people = new List<Performer>();
    string sentRequestJson = "";

    // typed-command + behavior-popup state (desktop mode)
    bool typing, typedDirty;
    string typedText = "";
    Performer popupTarget;
    TextMesh popup;
    string popupBase;    // popup text without the live timeline line
    Vector3 popupAnchorPos;   // fixed while the popup is open
    Transform popupPanel;   // dark backing panel sized to the text
    bool queueMode;      // popup/typing pick a FOLLOW-UP instead of replacing
    static readonly Color[] palette = {
        new Color(0.98f, 0.55f, 0.25f),  // orange
        new Color(0.25f, 0.78f, 0.75f),  // teal
        new Color(0.72f, 0.55f, 0.98f),  // violet
        new Color(0.55f, 0.85f, 0.35f),  // green
    };
    GameObject tableTop, shelfTop;
    readonly List<GameObject> surfaces = new List<GameObject>();
    Vector3 camHomePos;
    Quaternion camHomeRot;

    // ------------------------------------------------------------- lifecycle
    void Start()
    {
        BuildEnvironment();
        markers = new GameObject("CaptureMarkers").transform;
        StartCoroutine(FetchPrompts());
        StartCoroutine(EnsureFloorOrigin());
        SetStatus("Ready. Trace: right trigger / click table | A or G: generate\n" +
                  "T: type a command | click a person: change their behavior");
    }

    // Request floor-level tracking origin so y=0 is the real floor.
    IEnumerator EnsureFloorOrigin()
    {
        var subs = new List<XRInputSubsystem>();
        for (int i = 0; i < 50; i++)
        {
            SubsystemManager.GetSubsystems(subs);
            foreach (var s in subs)
                if (s.running && s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor))
                    yield break;
            yield return new WaitForSeconds(0.2f);
        }
    }

    void Update()
    {
        if (typing)   // all other bindings suspended; VR keyboard stays live
        {
            DriveCameraFromHmd(InputDevices.GetDeviceAtXRNode(XRNode.Head));
            if (ctrlR != null)   // hands + laser stay visible while typing
            {
                UpdateControllerProxy(ctrlR, InputDevices.GetDeviceAtXRNode(XRNode.RightHand));
                UpdateControllerProxy(ctrlL, InputDevices.GetDeviceAtXRNode(XRNode.LeftHand));
            }
            if (vrKb != null && vrKb.gameObject.activeSelf) VrKeyboardInput();
            TypingInput();
            return;
        }

        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        // Either controller keeps VR input alive (controllers sleep when idle).
        bool xr = right.isValid || InputDevices.GetDeviceAtXRNode(XRNode.LeftHand).isValid;

        if (!xr && ctrlR != null)   // proxies exist only while VR is live
        {
            ctrlR.gameObject.SetActive(false);
            ctrlL.gameObject.SetActive(false);
            if (laserDot != null) laserDot.gameObject.SetActive(false);
        }
        if (xr && !vrHintShown)   // one-time discoverability hint, no auto-open
        {
            vrHintShown = true;
            SetStatus("VR ready — HOLD X (or M key) opens the panel with prompts + guide.");
            stripLive = false;    // the idle hint must not summon the strip
        }
        TutorialUpdate(xr);
        if (tutVr != null)   // strip: tutorial or live status — hidden while
        {                    // typing (the keyboard carries its own preview)
            bool showStrip = xr && !typing && (tutStep >= 0 || stripLive);
            if (tutVr.gameObject.activeSelf != showStrip) tutVr.gameObject.SetActive(showStrip);
        }
        if (promptHud != null && promptHud.gameObject.activeSelf != xr)
        {
            promptHud.gameObject.SetActive(xr);
            if (xr) RefreshPromptMenu();   // fill it on first show
        }
        if (spectatorCam != null && spectatorMode == 2)   // stabilized follow
        {
            spectatorCam.transform.position = Vector3.Lerp(
                spectatorCam.transform.position, cam.transform.position, 4f * Time.deltaTime);
            spectatorCam.transform.rotation = Quaternion.Slerp(
                spectatorCam.transform.rotation, cam.transform.rotation, 4f * Time.deltaTime);
        }

        DriveCameraFromHmd(head);

        if (popup != null && popupTarget != null)   // popup stays where opened
        {
            popup.transform.position = popupAnchorPos;
            Vector3 pl = popup.transform.position - cam.transform.position;
            pl.y = 0;                               // readable from anywhere
            if (pl.sqrMagnitude > 0.01f) popup.transform.rotation = Quaternion.LookRotation(pl);
            if (popupTarget.Dir != null)            // live playhead + cut range
                popup.text = popupBase + TimelineBar(popupTarget.Dir);
            FitPopupPanel();
        }

        // Action sequences: when a clip's kept range ends, advance to the next
        // chain entry (cyclically) — A → B → A → B…, cuts and placement kept.
        foreach (var p in people)
            if (p.chain.Count > 1 && p.Dir != null && p.Dir.ClipTime >= p.Dir.CutOut - 0.034f)
            {
                var d = p.Dir;
                var cur = p.chain[p.chainIdx];
                cur.cutIn = d.CutIn;              // remember edits made live
                cur.cutOut = d.CutOut;
                Vector3 uo = d.GetAdjustOffset();
                float uy = d.GetAdjustYaw();

                p.chainIdx = (p.chainIdx + 1) % p.chain.Count;
                var nx = p.chain[p.chainIdx];
                p.requestJson = nx.json;
                p.contactPts = nx.cpts;
                p.contactTimes = nx.ctimes;
                p.wrist = nx.wrist;
                if (popupTarget == p) ClosePopup();
                p.Kill();
                BuildPerformer(p, nx.m);
                if (p.Dir != null)
                {
                    p.Dir.SetAdjust(uo, uy);
                    p.Dir.SetCutIn(nx.cutIn);
                    if (nx.cutOut > 0) p.Dir.SetCutOut(nx.cutOut);
                }
            }

        var kbAlways = Keyboard.current;
        if (kbAlways != null && kbAlways.vKey.wasPressedThisFrame) StartCoroutine(ToggleVR());
        if (kbAlways != null && kbAlways.f1Key.wasPressedThisFrame)
        {
            tutStep = tutStep < 0 ? 0 : -1;   // F1 starts / hides the tutorial
            tutEntered = -1;
            if (tutStep < 0)
            {
                SetTut("");
                if (statusHist.Count > 0) PushStatus(statusHist[0]);   // strip → status
            }
        }
        if (kbAlways != null && kbAlways.f2Key.wasPressedThisFrame)
            vrDebugOn = !vrDebugOn;           // F2: live VR input readout
        if (kbAlways != null && kbAlways.f3Key.wasPressedThisFrame)
            ToggleSpectator();                // F3: stable desktop view for recording
        if (kbAlways != null && kbAlways.tabKey.wasPressedThisFrame)
        {
            leftHudOn = !leftHudOn;           // Tab: prompts/controls stack
            foreach (var g in leftHudPanels) g.SetActive(leftHudOn);
            if (leftHudOn) LayoutHudStack();
        }

        // The physical keyboard stays usable INSIDE VR — a reliable fallback
        // when a controller profile doesn't deliver the face buttons.
        if (xr && kbAlways != null)
        {
            if (kbAlways.tKey.wasPressedThisFrame) { StartTyping(null); return; }
            if (kbAlways.gKey.wasPressedThisFrame && !generating) StartCoroutine(Generate());
            if (kbAlways.pKey.wasPressedThisFrame) CyclePrompt();
            if (kbAlways.cKey.wasPressedThisFrame) ClearCaptures();
            if (kbAlways.mKey.wasPressedThisFrame && vrHud != null)
                vrHud.gameObject.SetActive(!vrHud.gameObject.activeSelf);
        }

        if (xr) XrInput(right, head);
        else DesktopInput();
    }

    // ------------------------------------------------------------------ input
    void XrInput(InputDevice right, InputDevice head)
    {
        var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        if (ctrlR == null) { ctrlR = BuildControllerProxy(false); ctrlL = BuildControllerProxy(true); }
        UpdateControllerProxy(ctrlR, right);
        UpdateControllerProxy(ctrlL, left);

        // Laser: beam ends at the hit; reticle colored by target (surface / floor).
        if (laserBeam != null && ctrlR.gameObject.activeSelf &&
            right.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 lbp) &&
            right.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion lbr))
        {
            var lray2 = new Ray(lbp, lbr * Vector3.forward);
            float ldist = 3f;
            bool showDot = false;
            Vector3 dotPos = Vector3.zero;
            Color dotCol = Color.white;
            if (TrySurfaceHit(lray2, out RaycastHit lsh))
            {
                ldist = lsh.distance;
                dotPos = lsh.point;
                dotCol = new Color(1f, 0.55f, 0.26f);
                showDot = true;
            }
            else if (TryFloorPointRay(lray2, out Vector3 lfp))
            {
                ldist = Vector3.Distance(lbp, lfp);
                dotPos = lfp + Vector3.up * 0.01f;
                dotCol = new Color(0.29f, 0.56f, 1f);
                showDot = true;
            }
            laserBeam.localScale = new Vector3(0.003f, 0.003f, ldist);
            laserBeam.localPosition = new Vector3(0, 0, ldist * 0.5f);
            if (laserDot != null)
            {
                if (laserDot.gameObject.activeSelf != showDot) laserDot.gameObject.SetActive(showDot);
                if (showDot)
                {
                    laserDot.position = dotPos;
                    laserDot.localScale = Vector3.one * (0.02f + ldist * 0.012f);
                    laserDotRend.material.color = dotCol;
                }
            }
        }

        right.TryGetFeatureValue(CommonUsages.triggerButton, out bool rTrig);
        left.TryGetFeatureValue(CommonUsages.triggerButton, out bool lTrig);
        right.TryGetFeatureValue(CommonUsages.primaryButton, out bool aBtn);
        right.TryGetFeatureValue(CommonUsages.secondaryButton, out bool bBtn);
        right.TryGetFeatureValue(CommonUsages.gripButton, out bool grip);
        left.TryGetFeatureValue(CommonUsages.gripButton, out bool lGrip);
        // Accept several "generate" bindings — controller profiles differ.
        right.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool rStick);
        left.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool lStick);
        right.TryGetFeatureValue(CommonUsages.menuButton, out bool menuR);
        left.TryGetFeatureValue(CommonUsages.menuButton, out bool menuL);
        left.TryGetFeatureValue(CommonUsages.primaryButton, out bool xBtn);
        left.TryGetFeatureValue(CommonUsages.secondaryButton, out bool yBtn);
        right.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 rAxis);
        left.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 lAxis);
        aBtn |= rStick;   // LEFT-stick click opens the menu instead
        bBtn |= yBtn;

        if (vrDebug != null)   // which inputs actually arrive from the runtime
        {
            if (vrDebug.gameObject.activeSelf != vrDebugOn)
                vrDebug.gameObject.SetActive(vrDebugOn);
            if (vrDebugOn)
            {
                right.TryGetFeatureValue(CommonUsages.triggerButton, out bool dRT);
                left.TryGetFeatureValue(CommonUsages.triggerButton, out bool dLT);
                right.TryGetFeatureValue(CommonUsages.secondaryButton, out bool dB);
                left.TryGetFeatureValue(CommonUsages.secondaryButton, out bool dY);
                right.TryGetFeatureValue(CommonUsages.primaryButton, out bool dA);
                vrDebug.text =
                    $"A:{(dA ? 1 : 0)} B:{(dB ? 1 : 0)} X:{(xBtn ? 1 : 0)} Y:{(dY ? 1 : 0)} " +
                    $"MENU:{(menuR | menuL ? 1 : 0)}  RT:{(dRT ? 1 : 0)} LT:{(dLT ? 1 : 0)} " +
                    $"RG:{(grip ? 1 : 0)} LG:{(lGrip ? 1 : 0)}\n" +
                    $"RS:{rAxis.x:0.0},{rAxis.y:0.0}  LS:{lAxis.x:0.0},{lAxis.y:0.0}  " +
                    $"RSC:{(rStick ? 1 : 0)} LSC:{(lStick ? 1 : 0)}   (F2 hides)";
            }
        }

        // X (or L-grip): tap = pose / delete-selected; hold = panel.
        bool xNow = xBtn | lGrip;
        if (xNow && !xWas) { xDownAt = Time.time; xLongFired = false; }
        bool xTap = xWas && !xNow && Time.time - xDownAt < 0.9f;
        if (xNow && !xLongFired && Time.time - xDownAt >= 0.9f)
        {
            xLongFired = true;
            if (vrHud != null)
            {
                bool on = !vrHud.gameObject.activeSelf;
                vrHud.gameObject.SetActive(on);
                SetStatus(on ? "Panel opened (X held). TAP X shorter to snap a pose."
                             : "Panel closed.");
            }
        }
        xWas = xNow;

        // B: tap = prompt/select; hold = start typing.
        bool bNow = bBtn;
        if (bNow && !bWas) { bDownAt = Time.time; bLongFired = false; }
        bool bTap = bWas && !bNow && Time.time - bDownAt < 0.9f;
        if (bNow && !bLongFired && Time.time - bDownAt >= 0.9f)
        {
            bLongFired = true;
            bWas = false;
            StartTyping(popupTarget);
            return;
        }
        bWas = bNow;

        // MENU button still toggles too, where the runtime delivers it.
        if (Pressed("menu", menuR | menuL) && vrHud != null)
            vrHud.gameObject.SetActive(!vrHud.gameObject.activeSelf);
        if (vrHud != null && vrHud.gameObject.activeSelf && popupTarget == null)
        {
            // right stick left/right scrolls the prompt list
            if (Pressed("pnext", rAxis.x > 0.7f)) CyclePrompt(1);
            if (Pressed("pprev", rAxis.x < -0.7f)) CyclePrompt(-1);
            Vector3 hp2 = vrHud.localPosition;
            if (lAxis.sqrMagnitude > 0.04f)
                hp2 += new Vector3(lAxis.x, lAxis.y, 0) * (0.5f * Time.deltaTime);
            if (Mathf.Abs(rAxis.y) > 0.3f)
                hp2.z += rAxis.y * 0.5f * Time.deltaTime;
            hp2.x = Mathf.Clamp(hp2.x, -0.7f, 0.7f);
            hp2.y = Mathf.Clamp(hp2.y, -0.5f, 0.5f);
            hp2.z = Mathf.Clamp(hp2.z, 0.55f, 1.8f);
            vrHud.localPosition = hp2;
        }

        // While a person is selected (point + B), the sticks move/rotate them
        // and X deletes them; otherwise X snaps a pose as usual.
        if (popupTarget != null && popupTarget.Dir != null)
        {
            var pd = popupTarget.Dir;
            if (lAxis.sqrMagnitude > 0.04f)
                pd.Nudge(HeadYaw(head) * new Vector3(lAxis.x, 0, lAxis.y)
                         * (0.9f * Time.deltaTime));
            if (Mathf.Abs(rAxis.x) > 0.25f)
                pd.Rotate(rAxis.x * 70f * Time.deltaTime);
            // right stick up/down = mark cut start/end at the playhead
            if (Pressed("cutin", rAxis.y > 0.7f)) pd.SetCutIn(pd.ClipTime);
            if (Pressed("cutout", rAxis.y < -0.7f)) pd.SetCutOut(pd.ClipTime);
            // A on a selected person queues the current prompt as a follow-up.
            if (Pressed("a", aBtn))
            {
                var tgt = popupTarget;
                SetStatus($"A → queuing follow-up for SELECTED person {people.IndexOf(tgt) + 1} " +
                          "(B releases the selection first if you wanted a NEW generation).");
                ClosePopup();
                if (!generating) StartCoroutine(Append(tgt, prompt));
                return;
            }
            if (xTap) { DeletePerson(popupTarget); return; }
        }
        else if (xTap) SnapPose(left, right, head);

        bool menuPointing = VrMenuPointing(right, rTrig);
        bool popupPointing = popupTarget != null && VrPopupPointing(right, rTrig);

        if (rTrig && !menuPointing && !popupPointing &&
            right.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 handRaw))
        {
            if (rTrigDownAt < 0) rTrigDownAt = Time.time;
            if (Time.time - rTrigDownAt > 0.25f)   // HOLD = trace on the surface
            {
                capturedInVR = true;
                Vector3 hand = SnapToSurface(handRaw);
                Sample(0.15f, () =>
                {
                    handPoints.Add(hand);
                    handTimes.Add(Time.time - captureStart);
                    if (head.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 hp))
                        bodyPoints.Add(new Vector3(hp.x, 0, hp.z));
                    Mark(hand, new Color(1f, 0.55f, 0.26f), 0.02f);
                });
            }
        }
        else if (lTrig && head.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 hmd))
        {
            // debounce: a momentary squeeze while handling the controller must
            // not record a stray waypoint
            if (lTrigHeldSince < 0) lTrigHeldSince = Time.time;
            if (Time.time - lTrigHeldSince > 0.35f)
            {
                capturedInVR = true;
                Sample(0.3f, () =>
                {
                    walkPoints.Add(new Vector3(hmd.x, 0, hmd.z));
                    walkTimes.Add(Time.time - captureStart);
                    Mark(new Vector3(hmd.x, 0.03f, hmd.z), new Color(0.29f, 0.56f, 1f), 0.035f);
                });
            }
        }
        else
        {
            lTrigHeldSince = -1f;    // capture clock persists across strokes
            // short trigger tap = laser-place a point, like a desktop click
            if (!rTrig && rTrigDownAt >= 0)
            {
                if (Time.time - rTrigDownAt <= 0.25f && !menuPointing && !popupPointing &&
                    right.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 lrp) &&
                    right.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion lrr))
                {
                    var lray = new Ray(lrp, lrr * Vector3.forward);
                    if (TrySurfaceHit(lray, out RaycastHit lh))
                    {
                        capturedInVR = false;   // point-style touch => fingertip
                        PlaceTouchAt(lh);
                    }
                    else if (TryFloorPointRay(lray, out Vector3 fp))
                        PlaceWalkDot(fp);       // floor tap = blue waypoint
                }
                rTrigDownAt = -1f;
            }
        }

        if (Pressed("a", aBtn))
        {
            if (generating)
                SetStatus("Still generating — wait for it to finish.");
            else if (handPoints.Count == 0 && walkPoints.Count == 0)
                SetStatus("A pressed, nothing captured — trace, tap a point, or snap a pose first.\nPrompt: " + Trunc(prompt, 60));
            else
                StartCoroutine(Generate());
        }
        if (bTap)
        {
            // Pointing at a person selects them (deliberate 0.32 m aim, loud
            // feedback); otherwise B cycles the prompt — panel open or not.
            if (popupTarget != null)
            {
                ClosePopup();
                SetStatus("Released. A generates · B cycles the prompt.");
            }
            else if (right.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rpos) &&
                     right.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rrot) &&
                     PersonAlongRay(new Ray(rpos, rrot * Vector3.forward)) is Performer selP)
            {
                OpenPopup(selP);
                SetStatus($"SELECTED person {people.IndexOf(selP) + 1} — sticks move/rotate · " +
                          "R-stick up/down cut · A follow-up · X delete · B release");
            }
            else CyclePrompt();
        }
        if (Pressed("grip", grip)) ClearCaptures();
    }

    // X: freeze this instant as a pose keyframe (wrists, stance, facing).
    void SnapPose(InputDevice left, InputDevice right, InputDevice head)
    {
        if (!right.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rp) ||
            !left.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 lp) ||
            !head.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 hp))
        {
            SetStatus("Pose snap needs both controllers + headset tracked.");
            return;
        }
        head.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion hr);
        capturedInVR = true;
        AddPose(lp, rp, hp, hr);
    }

    // Shared pose-keyframe core; enforces executability limits.
    void AddPose(Vector3 lp, Vector3 rp, Vector3 hp, Quaternion hr)
    {
        // Debounce button spam.
        if (Time.time - lastSnapReal < 0.7f) return;
        // identical pose right after the previous = spam; later = a deliberate hold
        if (poseTimes.Count > 0 && Time.time - lastSnapReal < 2f &&
            Vector3.Distance(poseBody[poseBody.Count - 1], new Vector3(hp.x, 0, hp.z)) < 0.12f &&
            Vector3.Distance(poseL[poseL.Count - 1], lp) < 0.08f &&
            Vector3.Distance(poseR[poseR.Count - 1], rp) < 0.08f)
        {
            SetStatus("Same pose again — wait a moment or strike a different pose.");
            return;
        }

        if (captureStart < 0) captureStart = Time.time;
        float t = Time.time - captureStart;

        // travel-time feasibility: no teleporting between consecutive poses
        bool delayed = false;
        if (poseTimes.Count > 0)
        {
            Vector3 prev = poseBody[poseBody.Count - 1];
            float need = Vector3.Distance(prev, new Vector3(hp.x, 0, hp.z)) / 1.3f + 1.0f;
            float minT = poseTimes[poseTimes.Count - 1] + need;
            if (t < minT) { t = minT; delayed = true; }
        }
        if (t > 27f)   // server hard-rejects clips over 30 s
        {
            SetStatus("Demonstration too long for one clip (30 s budget).\nGenerate now (G/A) or clear (C).");
            return;
        }

        // Body height from the hands only (headset heights are unreliable):
        // low hands lower the hips proportionally.
        float loHand = Mathf.Min(lp.y, rp.y);
        float rootH = loHand >= 0.65f ? 0.97f
                    : Mathf.Max(0.45f, 0.97f - (0.65f - loHand) * 1.1f);

        // hands within arm's reach of the chest, and above the floor
        Vector3 chest = new Vector3(hp.x, rootH + 0.25f, hp.z);
        bool clamped = false;
        lp = ClampReach(lp, chest, 0.85f, ref clamped);
        rp = ClampReach(rp, chest, 0.85f, ref clamped);

        if (popupTarget != null) ClosePopup();   // authoring => release selection
        lastSnapReal = Time.time;
        poseTimes.Add(t);
        poseL.Add(lp);
        poseR.Add(rp);
        poseBody.Add(new Vector3(hp.x, 0, hp.z));
        poseHeadY.Add(rootH + 0.75f);   // implied head height — carries the crouch
        Vector3 fwd = hr * Vector3.forward;
        poseHeading.Add(Mathf.Atan2(-fwd.x, fwd.z));   // Kimodo yaw, 0 = +Z

        // the stance joins the walk path so the root trajectory stays coherent
        walkPoints.Add(new Vector3(hp.x, 0, hp.z));
        walkTimes.Add(t);

        var purple = new Color(0.75f, 0.4f, 1f);       // pose markers ≠ trace/dots
        Mark(lp, purple, 0.03f);
        Mark(rp, purple, 0.03f);
        Mark(new Vector3(hp.x, 0.03f, hp.z), new Color(0.29f, 0.56f, 1f), 0.035f);
        BuildPoseGhost(new Vector3(hp.x, 0, hp.z), rootH, rootH + 0.65f,
                       Quaternion.Euler(0, Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg, 0), lp, rp);
        string note = (clamped ? " (hand pulled to reach)" : "") +
                      (delayed ? " (time pushed for travel)" : "");
        SetStatus($"Pose {poseTimes.Count} snapped at t={t:0.0}s{note}.\nX for more poses, A/G to generate.");
    }

    // Pull a hand target onto the reachable sphere around the chest.
    static Vector3 ClampReach(Vector3 hand, Vector3 chest, float reach, ref bool clamped)
    {
        if (hand.y < 0.03f) { hand.y = 0.03f; clamped = true; }
        Vector3 d = hand - chest;
        float m = d.magnitude;
        if (m <= reach) return hand;
        clamped = true;
        return chest + d * (reach / m);
    }

    // Ghost stick figure for the snapped pose (under `markers`, swept with them).
    void BuildPoseGhost(Vector3 stance, float rootH, float headY, Quaternion yawQ,
                        Vector3 lw, Vector3 rw)
    {
        var g = new GameObject("PoseGhost").transform;
        g.SetParent(markers);
        var c = new Color(0.78f, 0.55f, 1f);

        Vector3 hips = stance + Vector3.up * rootH;
        Vector3 neck = stance + Vector3.up * Mathf.Max(rootH + 0.3f, headY - 0.28f);
        Vector3 headC = neck + Vector3.up * 0.18f;
        Vector3 shL = neck + yawQ * new Vector3(-0.19f, -0.02f, 0);
        Vector3 shR = neck + yawQ * new Vector3(0.19f, -0.02f, 0);

        Stick(g, hips, neck, c);
        Stick(g, shL, shR, c);
        Stick(g, shL, ElbowPoint(shL, lw), c);
        Stick(g, ElbowPoint(shL, lw), lw, c);
        Stick(g, shR, ElbowPoint(shR, rw), c);
        Stick(g, ElbowPoint(shR, rw), rw, c);
        Stick(g, hips + yawQ * new Vector3(-0.09f, 0, 0), stance + yawQ * new Vector3(-0.12f, 0.02f, 0), c);
        Stick(g, hips + yawQ * new Vector3(0.09f, 0, 0), stance + yawQ * new Vector3(0.12f, 0.02f, 0), c);

        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(head.GetComponent<Collider>());
        head.transform.SetParent(g);
        head.transform.position = headC;
        head.transform.localScale = Vector3.one * 0.19f;
        Tint(head, c);
    }

    void Stick(Transform parent, Vector3 a, Vector3 b, Color c)
    {
        var cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(cyl.GetComponent<Collider>());
        cyl.transform.SetParent(parent);
        cyl.transform.position = (a + b) * 0.5f;
        Vector3 d = b - a;
        if (d.sqrMagnitude > 1e-6f) cyl.transform.up = d.normalized;
        cyl.transform.localScale = new Vector3(0.035f, d.magnitude * 0.5f, 0.035f);
        Tint(cyl, c);
    }

    // Two-bone elbow estimate (law of cosines, pole down) for the ghost's arms.
    static Vector3 ElbowPoint(Vector3 s, Vector3 w)
    {
        const float l1 = 0.28f, l2 = 0.26f;
        Vector3 to = w - s;
        float d = Mathf.Clamp(to.magnitude, 0.05f, l1 + l2 - 0.01f);
        Vector3 axis = to.normalized;
        Vector3 pole = Vector3.down - Vector3.Dot(Vector3.down, axis) * axis;
        if (pole.sqrMagnitude < 1e-4f) pole = Vector3.forward;
        pole.Normalize();
        float cosA = Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1f, 1f);
        return s + axis * (l1 * cosA) + pole * (l1 * Mathf.Sqrt(1 - cosA * cosA));
    }

    // A blue walk waypoint (mouse right-click or VR laser tap on the floor).
    void PlaceWalkDot(Vector3 wp)
    {
        if (popupTarget != null) ClosePopup();   // authoring => release selection
        if (roomKnown)   // waypoints can't be placed outside the room
        {
            wp.x = Mathf.Clamp(wp.x, roomRect.xMin + 0.2f, roomRect.xMax - 0.2f);
            wp.z = Mathf.Clamp(wp.z, roomRect.yMin + 0.2f, roomRect.yMax - 0.2f);
        }
        float t = walkPoints.Count > 0
            ? walkTimes[walkTimes.Count - 1]
              + Vector3.Distance(walkPoints[walkPoints.Count - 1], wp) / 1.3f
            : 0f;
        walkPoints.Add(wp);
        walkTimes.Add(t);
        Mark(new Vector3(wp.x, 0.03f, wp.z), new Color(0.29f, 0.56f, 1f), 0.035f);
        SetStatus($"Waypoint {walkPoints.Count} down (first = spawn).");
    }

    // Click / laser-tap on a surface = touch target + standing spot.
    void PlaceTouchAt(RaycastHit hit)
    {
        if (popupTarget != null) ClosePopup();   // authoring => release selection
        handPoints.Add(hit.point);
        handTimes.Add(handPoints.Count * 0.5f);
        // stand 0.42 m from the point, on the viewer's side
        Vector3 center = hit.collider.bounds.center;
        Vector3 away = new Vector3(cam.transform.position.x - center.x, 0,
                                   cam.transform.position.z - center.z).normalized;
        if (away.sqrMagnitude < 0.1f) away = Vector3.back;
        Vector3 body = new Vector3(hit.point.x, 0, hit.point.z) + away * 0.42f;
        // never stand INSIDE the furniture being touched
        var sroot = SurfaceRootOf(hit.collider.gameObject);
        if (sroot != null && TryBounds(sroot, out var fb))
            body = PushOutXZ(body, fb, away, 0.35f);
        bodyPoints.Add(body);
        Mark(new Vector3(body.x, 0.02f, body.z), new Color(1f, 0.85f, 0.3f), 0.028f);
        Mark(hit.point, new Color(1f, 0.55f, 0.26f), 0.02f);
        SetStatus($"Touch point {handPoints.Count} placed.");
    }

    void DesktopInput()
    {
        var mouse = Mouse.current;
        var kb = Keyboard.current;

        if (simMode && kb != null && mouse != null) SimRigUpdate(kb, mouse);

        // clicks on the HUD panel must not fall through into the scene
        bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        if (mouse != null && !overUI && mouse.leftButton.wasPressedThisFrame)
        {
            // clicking a person opens their behavior popup (Sims-style)
            var sel = PersonUnderMouse(mouse.position.ReadValue());
            if (sel != null) { OpenPopup(sel); return; }
            if (popup != null) { ClosePopup(); return; }

            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            if (TrySurfaceHit(ray, out RaycastHit hit))
            {
                capturedInVR = false;
                PlaceTouchAt(hit);
            }
        }

        // Right-click = blue walk dot (first = spawn); in sim mode only a click without drag.
        bool dotClick = false;
        if (mouse != null && !overUI)
        {
            if (!simMode) dotClick = mouse.rightButton.wasPressedThisFrame;
            else
            {
                if (mouse.rightButton.wasPressedThisFrame) lookDragged = false;
                if (mouse.rightButton.wasReleasedThisFrame && !lookDragged) dotClick = true;
            }
        }
        if (dotClick && TryFloorPoint(mouse.position.ReadValue(), out Vector3 wp))
            PlaceWalkDot(wp);
        if (kb == null) return;
        if (popup != null) { PopupKeys(kb); return; }   // popup owns the keyboard
        if (kb.tKey.wasPressedThisFrame) { StartTyping(null); return; }
        if (kb.gKey.wasPressedThisFrame && !generating) StartCoroutine(Generate());
        if (kb.jKey.wasPressedThisFrame && !generating) StartCoroutine(SaveAndGenerate());
        if (kb.hKey.wasPressedThisFrame) ToggleSim();
        if (kb.xKey.wasPressedThisFrame)
        {
            // desktop stand-in for the VR pose snap: the camera is the
            // headset; hands are the sim spheres (H mode) or chest offsets
            Vector3 hp = cam.transform.position;
            Quaternion yaw = CamYaw();
            if (simMode)
                AddPose(simL.position, simR.position, hp, yaw);
            else
                AddPose(hp + yaw * new Vector3(-0.22f, -0.45f, 0.40f),
                        hp + yaw * new Vector3(0.22f, -0.45f, 0.40f), hp, yaw);
        }
        if (kb.pKey.wasPressedThisFrame) CyclePrompt();
        if (kb.cKey.wasPressedThisFrame) ClearCaptures();
    }

    // ------------------------------------------------- desktop rig simulator
    Quaternion CamYaw() => Quaternion.Euler(0, cam.transform.eulerAngles.y, 0);

    void ToggleSim()
    {
        simMode = !simMode;
        if (simMode)
        {
            simL = SimHand("SimLeftHand", new Color(0.25f, 0.8f, 0.9f));
            simR = SimHand("SimRightHand", new Color(1f, 0.6f, 0.2f));
            simLPlaced = simRPlaced = false;
            SetStatus("SIM RIG ON — you are the headset.\n" +
                      "WASD move · Q/E height (crouch!) · hold RIGHT MOUSE to look\n" +
                      "hold CTRL = left hand, SHIFT = right hand (mouse aims, scroll = reach)\n" +
                      "X snap pose · right-CLICK dot · G generate · H exit");
        }
        else
        {
            if (simL != null) Destroy(simL.gameObject);
            if (simR != null) Destroy(simR.gameObject);
            simL = simR = null;
            SetStatus("Sim rig off.");
        }
    }

    Transform SimHand(string name, Color c)
    {
        var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        s.name = name;
        Destroy(s.GetComponent<Collider>());   // must not swallow clicks/raycasts
        s.transform.localScale = Vector3.one * 0.07f;
        Tint(s, c);
        return s.transform;
    }

    void SimRigUpdate(Keyboard kb, Mouse mouse)
    {
        // head: WASD planar, Q/E height (headset height feeds crouch poses)
        Vector3 fwd = cam.transform.forward; fwd.y = 0; fwd.Normalize();
        Vector3 rgt = cam.transform.right; rgt.y = 0; rgt.Normalize();
        Vector3 mv = Vector3.zero;
        if (kb.wKey.isPressed) mv += fwd;
        if (kb.sKey.isPressed) mv -= fwd;
        if (kb.dKey.isPressed) mv += rgt;
        if (kb.aKey.isPressed) mv -= rgt;
        if (kb.eKey.isPressed) mv += Vector3.up;
        if (kb.qKey.isPressed) mv -= Vector3.up;
        cam.transform.position += mv * (2.0f * Time.deltaTime);

        if (mouse.rightButton.isPressed)   // hold right mouse to look around
        {
            Vector2 d = mouse.delta.ReadValue();
            if (d.sqrMagnitude > 2f) lookDragged = true;
            var e = cam.transform.eulerAngles;
            float pitch = e.x > 180 ? e.x - 360 : e.x;
            pitch = Mathf.Clamp(pitch - d.y * 0.15f, -80, 80);
            cam.transform.rotation = Quaternion.Euler(pitch, e.y + d.x * 0.15f, 0);
        }

        // hands: held modifier aims the hand along the mouse ray at simReach
        float scroll = mouse.scroll.ReadValue().y;
        if (scroll != 0) simReach = Mathf.Clamp(simReach + scroll * 0.0008f, 0.25f, 1.3f);
        bool grabL = kb.leftCtrlKey.isPressed, grabR = kb.leftShiftKey.isPressed;
        if (grabL || grabR)
        {
            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            Vector3 p = ray.origin + ray.direction * simReach;
            if (grabL) { simL.position = p; simLPlaced = true; }
            if (grabR) { simR.position = p; simRPlaced = true; }
        }
        // un-grabbed hands ride along at chest offsets, like holstered controllers
        if (!simLPlaced) simL.position = cam.transform.position + CamYaw() * new Vector3(-0.22f, -0.45f, 0.40f);
        if (!simRPlaced) simR.position = cam.transform.position + CamYaw() * new Vector3(0.22f, -0.45f, 0.40f);
    }

    // Screen point -> a spot on the floor (y=0): whatever the ray hits,
    // projected down; open void falls back to the mathematical ground plane.
    bool TryFloorPoint(Vector2 screen, out Vector3 p) =>
        TryFloorPointRay(cam.ScreenPointToRay(screen), out p);

    bool TryFloorPointRay(Ray ray, out Vector3 p)
    {
        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            p = new Vector3(hit.point.x, 0, hit.point.z);
            return true;
        }
        if (new Plane(Vector3.up, 0f).Raycast(ray, out float enter))
        {
            p = ray.GetPoint(enter);
            p.y = 0;
            return true;
        }
        p = default;
        return false;
    }

    // ------------------------------------------- behavior popup + typed input
    Performer PersonUnderMouse(Vector2 screen) => PersonAlongRay(cam.ScreenPointToRay(screen));

    Performer PersonAlongRay(Ray ray)
    {
        Performer best = null;
        float bestD = 0.32f;   // metres from the ray to the torso — deliberate aim
        foreach (var p in people)
        {
            Vector3 c = p.Pos + Vector3.up * 0.2f;
            Vector3 v = c - ray.origin;
            if (Vector3.Dot(v, ray.direction) < 0) continue;
            float d = Vector3.Cross(ray.direction, v).magnitude;
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    void OpenPopup(Performer p)
    {
        // The popup ANCHORS where it opened (eye level, pulled toward the
        // viewer) — a menu that follows a moving performer is unaimable.
        if (popupTarget != p)
        {
            Vector3 pp = p.Pos;
            float py = Mathf.Clamp(cam.transform.position.y + 0.05f, 1.0f, pp.y + 1.15f);
            Vector3 toCam = cam.transform.position - pp;
            toCam.y = 0;
            Vector3 off = toCam.sqrMagnitude > 0.04f ? toCam.normalized * 0.35f : Vector3.zero;
            popupAnchorPos = new Vector3(pp.x, py, pp.z) + off;
        }
        popupTarget = p;
        if (popup == null)
        {
            var go = new GameObject("BehaviorPopup");
            popup = go.AddComponent<TextMesh>();
            popup.characterSize = 0.015f;
            popup.fontSize = 48;
            popup.anchor = TextAnchor.LowerCenter;
            popup.alignment = TextAlignment.Left;
            popup.color = new Color(1f, 0.95f, 0.75f);
        }
        if (popupPanel == null)
        {
            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "PopupPanel";
            Destroy(panel.GetComponent<Collider>());
            panel.transform.SetParent(popup.transform);
            Tint(panel, new Color(0.07f, 0.08f, 0.11f));
            popupPanel = panel.transform;
        }

        // Rows are tracked so the VR laser can pick them: >=0 prompt index,
        // -10 type, -11 queue, -12 delete, -13 close/back, -1 inert.
        popupRows.Clear();
        var sb = new StringBuilder();
        int line = 0;
        void Row(int code)
        {
            popupRows.Add(code);
            if (line == popupHover) sb.Append("<color=#a8ffc0>» ");
        }
        void End(bool last = false)
        {
            if (line == popupHover) sb.Append("</color>");
            if (!last) sb.Append("\n");
            line++;
        }

        Row(-1);
        sb.Append(queueMode ? "<color=#ffd479><b>QUEUE NEXT ACTION</b></color> — person "
                            : "<color=#ffd479><b>PERSON</b></color> ")
          .Append(people.IndexOf(p) + 1);
        End();
        int nOpts = Mathf.Min(9, cachedPrompts.Length);
        for (int i = 0; i < nOpts; i++)
        {
            Row(i);
            sb.Append(" <color=#8fd0ff>").Append(i + 1).Append("</color>  ")
              .Append(Trunc(cachedPrompts[i], 34));
            End();
        }
        Row(-10); sb.Append(" <color=#8fd0ff>T</color>  type a command"); End();
        if (p.Dir != null && !queueMode)
        {
            Row(-11); sb.Append(" <color=#8fd0ff>N</color>  queue a follow-up action"); End();
            Row(-14); sb.Append(" <color=#8fd0ff>R-stick ↑↓</color>  cut   <color=#8fd0ff>F / click here</color>  full clip"); End();
            Row(-1); sb.Append(" <color=#8fd0ff>arrows</color>  move   <color=#8fd0ff>, .</color>  rotate   <color=#8fd0ff>R</color>  reset"); End();
        }
        if (!queueMode) { Row(-12); sb.Append(" <color=#ff8f8f>Del</color>  remove person"); End(); }
        Row(-13); sb.Append(" <color=#8fd0ff>Esc</color>  ").Append(queueMode ? "back" : "close"); End(true);
        popupBase = sb.ToString();
        popup.text = popupBase;
        popup.transform.position = p.Pos + new Vector3(0, 1.15f, 0);
    }

    // Guided walkthrough: each step waits for the user to perform it. F1 toggles.
    int tutEntered = -1;
    string tutPrompt0;
    int tutPeople0;

    void TutorialUpdate(bool xr)
    {
        if (tutStep < 0 || typing) return;   // typing owns the strip meanwhile
        if (tutEntered != tutStep)           // entering a new step: baseline it
        {
            tutEntered = tutStep;
            tutPrompt0 = prompt;
            tutPeople0 = people.Count;
        }
        switch (tutStep)
        {
            case 0:
                SetTut(xr ? "<b>1/5 · SHOW IT</b> — tap X to snap your pose, or hold RIGHT trigger and trace along a table"
                          : "<b>1/5 · SHOW IT</b> — LEFT-click a table (touch point) or press X for a pose (H = sim rig)");
                if (handPoints.Count > 0 || poseTimes.Count > 0) tutStep = 1;
                break;
            case 1:
                SetTut(xr ? "<b>2/5 · PICK A PROMPT</b> — press B until a prompt you like is highlighted (hold X shows the list)"
                          : "<b>2/5 · PICK A PROMPT</b> — press P until a prompt you like is highlighted (or T to type one)");
                if (prompt != tutPrompt0) tutStep = 2;
                break;
            case 2:
                SetTut(generating
                    ? "<b>3/5 · GENERATING…</b> — wait, the performer appears at the route start"
                    : (xr ? "<b>3/5 · MAKE IT</b> — press A to generate"
                          : "<b>3/5 · MAKE IT</b> — press G to generate"));
                if (people.Count > tutPeople0) tutStep = 3;
                break;
            case 3:
                SetTut(xr ? "<b>4/5 · SELECT</b> — point the RIGHT controller at the person and tap B"
                          : "<b>4/5 · SELECT</b> — click the person to open their menu");
                if (popupTarget != null) tutStep = 4;
                break;
            case 4:
                SetTut(xr ? "<b>5/5 · DIRECT</b> — L-stick moves · R-stick rotates · up/down cuts · A queues a follow-up · X deletes · B releases (ends the tutorial)"
                          : "<b>5/5 · DIRECT</b> — [ ] cut · N follow-up · arrows move · Del removes · Esc closes (ends the tutorial)");
                if (popupTarget == null) { tutStep = -1; SetTut(""); SetStatus("Tutorial done — F1 replays it."); }
                break;
        }
    }

    void SetTut(string s)
    {
        if (tutStrip != null)
        {
            tutStrip.transform.parent.gameObject.SetActive(!string.IsNullOrEmpty(s));
            tutStrip.text = s;
        }
        if (tutVr != null) tutVr.text = Wrap(s ?? "", 46);   // TextMesh can't wrap
    }

    static string Wrap(string s, int width)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder();
        int len = 0;
        foreach (var w in s.Split(' '))
        {
            if (len > 0 && len + w.Length + 1 > width) { sb.Append('\n'); len = 0; }
            else if (len > 0) { sb.Append(' '); len++; }
            sb.Append(w);
            len += w.Length;
        }
        return sb.ToString();
    }

    Camera spectatorCam;
    int spectatorMode;   // 0 off · 1 fixed framing · 2 smooth-follow your view

    // F3 cycles the desktop view: mirror -> fixed framing -> smooth follow (recording).
    void ToggleSpectator()
    {
        spectatorMode = (spectatorMode + 1) % 3;
        if (spectatorMode == 0)
        {
            if (spectatorCam != null) { Destroy(spectatorCam.gameObject); spectatorCam = null; }
            SetStatus("Spectator OFF — desktop shows the headset mirror. (F3 cycles)");
            return;
        }
        if (spectatorCam == null)
        {
            var go = new GameObject("SpectatorCam");
            spectatorCam = go.AddComponent<Camera>();
            spectatorCam.clearFlags = cam.clearFlags;
            spectatorCam.backgroundColor = cam.backgroundColor;
            spectatorCam.stereoTargetEye = StereoTargetEyeMask.None;   // desktop only
            spectatorCam.depth = cam.depth + 1;
        }
        if (spectatorMode == 1)
        {
            spectatorCam.fieldOfView = 60f;
            spectatorCam.transform.SetPositionAndRotation(camHomePos, camHomeRot);
            SetStatus("Spectator: FIXED scene framing. F3 again = smooth follow of your view.");
        }
        else
        {
            spectatorCam.fieldOfView = 78f;   // wide — the whole scene you see
            spectatorCam.transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
            SetStatus("Spectator: SMOOTH FOLLOW — wide stabilized copy of your VR view. F3 = off.");
        }
    }

    void DeletePerson(Performer p)
    {
        if (p == null) return;
        ClosePopup();
        p.Kill();
        people.Remove(p);
        SetStatus("Person removed.");
    }

    static Quaternion HeadYaw(InputDevice head) =>
        head.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion q)
            ? Quaternion.Euler(0, q.eulerAngles.y, 0)
            : Quaternion.identity;

    void ToggleControls()
    {
        controlsOpen = !controlsOpen;
        controlsBody.gameObject.SetActive(controlsOpen);
        controlsKeys.gameObject.SetActive(controlsOpen);
        controlsHeader.text = controlsOpen ? "▾ CONTROLS — click to close"
                                           : "▸ CONTROLS — click to open";
        LayoutHudStack();
    }

    void ToggleMap()
    {
        mapOpen = !mapOpen && controlsDiag != null;
        if (controlsDiag != null) controlsDiag.gameObject.SetActive(mapOpen);
        mapHeader.text = mapOpen ? "▾ CONTROLLER MAP — click to close"
                                 : "▸ CONTROLLER MAP — click";
        LayoutHudStack();
    }

    // Keep the stacked HUD sections (controls, then map) flowing tidily.
    void LayoutHudStack()
    {
        float ch = controlsOpen ? 330 : 30;
        controlsPanel.rectTransform.sizeDelta = new Vector2(460, ch);
        if (mapPanel != null)
        {
            mapPanel.rectTransform.anchoredPosition = new Vector2(12, -(232 + ch + 8));
            mapPanel.rectTransform.sizeDelta = new Vector2(460, mapOpen ? diagH + 38 : 26);
        }
    }

    // Backing panel hugs the text each frame (the timeline line changes size).
    void FitPopupPanel()
    {
        if (popup == null || popupPanel == null) return;
        var r = popup.GetComponent<Renderer>();
        popupPanel.rotation = popup.transform.rotation;
        popupPanel.position = r.bounds.center + popup.transform.forward * 0.015f;
        popupPanel.localScale = Vector3.one;   // reset before world-size math
        Vector3 s = r.bounds.size;
        popupPanel.localScale = new Vector3(s.x + 0.12f, s.y + 0.07f, 0.004f);
    }

    // Live timeline under the popup: playhead |, kept range #, cut range -.
    string TimelineBar(IKimodoDirectable d)
    {
        float dur = d.Duration;
        if (dur <= 0.01f) return "";
        const int N = 30;
        int ph = Mathf.Clamp(Mathf.RoundToInt(d.ClipTime / dur * (N - 1)), 0, N - 1);
        int a = Mathf.Clamp(Mathf.RoundToInt(d.CutIn / dur * (N - 1)), 0, N - 1);
        int b = Mathf.Clamp(Mathf.RoundToInt(d.CutOut / dur * (N - 1)), 0, N - 1);
        var sb = new StringBuilder("\n ");
        for (int i = 0; i < N; i++)
            sb.Append(i == ph ? '|' : (i >= a && i <= b ? '#' : '-'));
        sb.Append("\n ").Append(d.ClipTime.ToString("0.0")).Append("s / ")
          .Append(dur.ToString("0.0")).Append("s   keep ")
          .Append(d.CutIn.ToString("0.0")).Append("–")
          .Append(d.CutOut.ToString("0.0")).Append("s");
        return sb.ToString();
    }

    void ClosePopup()
    {
        if (popup != null) Destroy(popup.gameObject);   // panel is its child
        popup = null;
        popupPanel = null;
        popupTarget = null;
        popupHover = -1;
        queueMode = false;
    }

    void PopupKeys(Keyboard kb)
    {
        if (kb.escapeKey.wasPressedThisFrame)
        {
            if (queueMode) { queueMode = false; OpenPopup(popupTarget); }   // back
            else ClosePopup();
            return;
        }
        if (kb.deleteKey.wasPressedThisFrame) { DeletePerson(popupTarget); return; }
        if (popupTarget != null && popupTarget.Dir != null && !queueMode)
        {
            var d = popupTarget.Dir;
            if (kb[Key.LeftBracket].wasPressedThisFrame) { d.SetCutIn(d.ClipTime); return; }
            if (kb[Key.RightBracket].wasPressedThisFrame) { d.SetCutOut(d.ClipTime); return; }
            if (kb.fKey.wasPressedThisFrame) { d.ResetCut(); return; }
            if (kb.nKey.wasPressedThisFrame) { queueMode = true; OpenPopup(popupTarget); return; }

            // manual placement: arrows nudge (camera-relative), , . rotate, R resets
            Vector3 nd = Vector3.zero;
            if (kb.upArrowKey.isPressed) nd += Vector3.forward;
            if (kb.downArrowKey.isPressed) nd += Vector3.back;
            if (kb.rightArrowKey.isPressed) nd += Vector3.right;
            if (kb.leftArrowKey.isPressed) nd += Vector3.left;
            if (nd != Vector3.zero) d.Nudge(CamYaw() * nd * (0.8f * Time.deltaTime));
            if (kb.commaKey.isPressed) d.Rotate(-70f * Time.deltaTime);
            if (kb.periodKey.isPressed) d.Rotate(70f * Time.deltaTime);
            if (kb.rKey.wasPressedThisFrame) d.ResetAdjust();
        }
        if (kb.tKey.wasPressedThisFrame)
        {
            var t = popupTarget;
            bool q = queueMode;
            ClosePopup();
            queueMode = q;          // ClosePopup resets it; typing still queues
            StartTyping(t);
            return;
        }
        int nOpts = Mathf.Min(9, cachedPrompts.Length);
        for (int i = 0; i < nOpts; i++)
        {
            if (!kb[Key.Digit1 + i].wasPressedThisFrame) continue;
            if (generating) { SetStatus("Busy generating — try again in a moment."); return; }
            var target = popupTarget;
            string np = cachedPrompts[i];
            bool q = queueMode;
            ClosePopup();
            StartCoroutine(q ? Append(target, np) : Redirect(target, np));
            return;
        }
    }

    void StartTyping(Performer target)
    {
        typing = true;
        typedText = "";
        typedDirty = false;
        popupTarget = target;
        if (Keyboard.current != null) Keyboard.current.onTextInput += OnTextChar;
        if (typingPanel != null) typingPanel.gameObject.SetActive(true);
        // VR: typing opens the laser keyboard and hides the panel behind it
        if (vrKb != null && (InputDevices.GetDeviceAtXRNode(XRNode.RightHand).isValid ||
                             InputDevices.GetDeviceAtXRNode(XRNode.LeftHand).isValid))
        {
            vrKb.gameObject.SetActive(true);
            if (vrHud != null) vrHud.gameObject.SetActive(false);
            if (promptHud != null) promptHud.gameObject.SetActive(false);
        }
        RefreshTypingStatus();
    }

    void OnTextChar(char c)
    {
        if (c >= ' ') { typedText += c; typedDirty = true; }
    }

    void StopTyping()
    {
        typing = false;
        if (Keyboard.current != null) Keyboard.current.onTextInput -= OnTextChar;
        if (typingPanel != null) typingPanel.gameObject.SetActive(false);
        if (vrKb != null) vrKb.gameObject.SetActive(false);
        popupTarget = null;
    }

    void TypingInput()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.backspaceKey.wasPressedThisFrame && typedText.Length > 0)
        {
            typedText = typedText.Substring(0, typedText.Length - 1);
            typedDirty = true;
        }
        if (kb.escapeKey.wasPressedThisFrame)
        {
            queueMode = false;
            StopTyping();
            SetStatus("Typing cancelled.");
            return;
        }
        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
        {
            SubmitTyped();
            return;
        }
        if (typedDirty) { typedDirty = false; RefreshTypingStatus(); }
    }

    // Simple visible controller models at the tracked hand poses — with a
    // pointing laser on the RIGHT one (selection + virtual keyboard aiming).
    Transform BuildControllerProxy(bool leftHand)
    {
        var root = new GameObject(leftHand ? "CtrlProxyL" : "CtrlProxyR").transform;
        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(head.GetComponent<Collider>());
        head.transform.SetParent(root, false);
        head.transform.localScale = Vector3.one * 0.045f;
        Tint(head, new Color(0.25f, 0.27f, 0.32f));
        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(ring.GetComponent<Collider>());
        ring.transform.SetParent(root, false);
        ring.transform.localPosition = new Vector3(0, 0.015f, 0.03f);
        ring.transform.localRotation = Quaternion.Euler(70, 0, 0);
        ring.transform.localScale = new Vector3(0.085f, 0.004f, 0.085f);
        Tint(ring, new Color(0.45f, 0.5f, 0.58f));
        var grip = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        Destroy(grip.GetComponent<Collider>());
        grip.transform.SetParent(root, false);
        grip.transform.localPosition = new Vector3(0, -0.045f, -0.02f);
        grip.transform.localRotation = Quaternion.Euler(25, 0, 0);
        grip.transform.localScale = new Vector3(0.028f, 0.05f, 0.028f);
        Tint(grip, new Color(0.25f, 0.27f, 0.32f));
        if (!leftHand)   // pointing laser + hit reticle
        {
            var laser = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(laser.GetComponent<Collider>());
            laser.transform.SetParent(root, false);
            laser.transform.localPosition = new Vector3(0, 0, 1.0f);
            laser.transform.localScale = new Vector3(0.003f, 0.003f, 2.0f);
            Tint(laser, new Color(0.5f, 0.85f, 1f));
            laserBeam = laser.transform;

            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "LaserDot";
            Destroy(dot.GetComponent<Collider>());
            Tint(dot, new Color(1f, 0.55f, 0.26f));
            laserDot = dot.transform;
            laserDotRend = dot.GetComponent<Renderer>();
            dot.SetActive(false);
        }
        return root;
    }

    void UpdateControllerProxy(Transform proxy, InputDevice dev)
    {
        bool ok = dev.isValid &&
                  dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 p) &&
                  dev.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion q);
        if (proxy.gameObject.activeSelf != ok) proxy.gameObject.SetActive(ok);
        if (ok)
        {
            dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pp);
            dev.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion qq);
            proxy.SetPositionAndRotation(pp, qq);
        }
    }

    void VrKey(string label, float x, float y, float w = 0.06f, float cs = 0.011f)
    {
        var cap = GameObject.CreatePrimitive(PrimitiveType.Cube);   // key cap
        Destroy(cap.GetComponent<Collider>());
        cap.transform.SetParent(vrKb, false);
        cap.transform.localPosition = new Vector3(x, y, 0.007f);
        cap.transform.localScale = new Vector3(w - 0.007f, 0.056f, 0.002f);
        Tint(cap, CapIdle);
        vrKbCaps.Add(cap.GetComponent<Renderer>());

        var go = new GameObject("Key_" + label);
        var tm = go.AddComponent<TextMesh>();
        go.transform.SetParent(vrKb, false);
        go.transform.localPosition = new Vector3(x, y, 0);
        tm.text = label;
        tm.characterSize = cs;
        tm.fontSize = 48;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = new Color(0.85f, 0.92f, 1f);
        vrKbKeys.Add(tm);
        vrKbVals.Add(label);
        vrKbW.Add(w);
    }

    // Point the right controller at the keyboard plane; trigger presses the
    // hovered key. Runs only while typing with a live VR rig.
    void VrKeyboardInput()
    {
        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (!right.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rp) ||
            !right.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rr)) return;
        right.TryGetFeatureValue(CommonUsages.triggerButton, out bool trig);

        Vector3 o = vrKb.InverseTransformPoint(rp);
        Vector3 dir = vrKb.InverseTransformDirection(rr * Vector3.forward);
        int hov = -1;
        if (Mathf.Abs(dir.z) > 1e-4f)
        {
            float t = -o.z / dir.z;
            if (t > 0)
            {
                Vector3 p = o + dir * t;
                for (int i = 0; i < vrKbKeys.Count; i++)
                {
                    Vector3 k = vrKbKeys[i].transform.localPosition;
                    if (Mathf.Abs(p.x - k.x) < vrKbW[i] * 0.5f &&
                        Mathf.Abs(p.y - k.y) < 0.034f) { hov = i; break; }
                }
            }
        }
        if (hov != vrKbHover)
        {
            if (vrKbHover >= 0)
            {
                vrKbKeys[vrKbHover].color = new Color(0.85f, 0.92f, 1f);
                vrKbCaps[vrKbHover].material.color = CapIdle;
            }
            if (hov >= 0)
            {
                vrKbKeys[hov].color = new Color(1f, 0.95f, 0.6f);
                vrKbCaps[hov].material.color = CapHover;
            }
            vrKbHover = hov;
        }
        if (trig && !vrKbTrigWas && hov >= 0)
        {
            string v = vrKbVals[hov];
            if (v == "SPACE") { typedText += " "; typedDirty = true; }
            else if (v == "DEL")
            {
                if (typedText.Length > 0) typedText = typedText.Substring(0, typedText.Length - 1);
                typedDirty = true;
            }
            else if (v == "ENTER") { SubmitTyped(); vrKbTrigWas = trig; return; }
            else if (v == "ESC")
            {
                queueMode = false;
                StopTyping();
                SetStatus("Typing cancelled.");
            }
            else { typedText += v.ToLower(); typedDirty = true; }
        }
        vrKbTrigWas = trig;
        if (typedDirty) { typedDirty = false; RefreshTypingStatus(); }
    }

    // Laser-pick rows on the person popup: hover highlights, trigger executes
    // (prompts, type, queue, delete, close). Returns true while pointing.
    bool VrPopupPointing(InputDevice right, bool trig)
    {
        int hov = -1;
        if (popup != null && popupTarget != null &&
            right.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rp) &&
            right.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rr))
        {
            var tr = popup.transform;
            Vector3 o = tr.InverseTransformPoint(rp);
            Vector3 dir = tr.InverseTransformDirection(rr * Vector3.forward);
            if (Mathf.Abs(dir.z) > 1e-4f)
            {
                float t = -o.z / dir.z;
                if (t > 0)
                {
                    Vector3 p = o + dir * t;
                    const float lineH = 0.072f;   // popup characterSize 0.015
                    int total = popupRows.Count + (popupTarget.Dir != null ? 3 : 0);
                    float top = total * lineH;    // anchor is LowerCenter
                    int row = Mathf.FloorToInt((top - p.y) / lineH);
                    if (Mathf.Abs(p.x) < 0.9f && p.y > -0.02f &&
                        row >= 0 && row < popupRows.Count)
                        hov = row;
                }
            }
        }
        if (hov != popupHover)
        {
            popupHover = hov;
            if (popupTarget != null) OpenPopup(popupTarget);   // redraw highlight
        }
        if (hov >= 0 && trig && !popupTrigWas)
        {
            int code = popupRows[hov];
            var target = popupTarget;
            if (code >= 0 && code < cachedPrompts.Length)
            {
                if (!generating)
                {
                    string np = cachedPrompts[code];
                    bool q = queueMode;
                    ClosePopup();
                    StartCoroutine(q ? Append(target, np) : Redirect(target, np));
                }
                else SetStatus("Busy generating — try again in a moment.");
            }
            else if (code == -10)
            {
                bool q = queueMode;
                ClosePopup();
                queueMode = q;
                StartTyping(target);
            }
            else if (code == -11) { queueMode = true; OpenPopup(target); }
            else if (code == -14 && target != null && target.Dir != null)
            {
                target.Dir.ResetCut();
                SetStatus("Full clip restored.");
            }
            else if (code == -12) DeletePerson(target);
            else if (code == -13)
            {
                if (queueMode) { queueMode = false; OpenPopup(target); }
                else ClosePopup();
            }
        }
        popupTrigWas = trig;
        return hov >= 0;
    }

    // Laser-pick prompts on the panel (▲/▼ rows scroll). True while pointing.
    bool VrMenuPointing(InputDevice right, bool trig)
    {
        int hov = -1;
        if (vrHud != null && vrHud.gameObject.activeSelf &&
            right.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rp) &&
            right.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rr))
        {
            Vector3 o = vrHud.InverseTransformPoint(rp);
            Vector3 dir = vrHud.InverseTransformDirection(rr * Vector3.forward);
            if (Mathf.Abs(dir.z) > 1e-4f)
            {
                float t = -o.z / dir.z;
                if (t > 0)
                {
                    Vector3 p = o + dir * t;
                    const float lineH = 0.0456f;   // boardMenu line height
                    const float yTop = 0.25f, xMin = -0.68f, xMax = -0.21f;
                    int row = Mathf.FloorToInt((yTop - p.y) / lineH);
                    if (p.x > xMin && p.x < xMax && row >= 0 && row < vrMenuRows.Count)
                        hov = row;
                }
            }
        }
        if (hov != vrMenuHover)
        {
            vrMenuHover = hov;
            RefreshPromptMenu();   // redraw with the pointing highlight
        }
        if (hov >= 0 && trig && !vrPointTrigWas)
        {
            int v = vrMenuRows[hov];
            if (v >= 0 && v < cachedPrompts.Length)
            {
                promptIndex = v;
                prompt = cachedPrompts[v];
                RefreshPromptMenu();
                SetStatus("Prompt: " + Trunc(prompt, 70));
            }
            else if (v == -2) CyclePrompt(-1);
            else if (v == -3) CyclePrompt(1);
        }
        vrPointTrigWas = trig;
        return hov >= 0;
    }

    void SubmitTyped()
    {
        string text = typedText.Trim();
        var target = popupTarget;
        StopTyping();
        if (text.Length < 3) { SetStatus("Too short — not sent."); return; }
        if (target != null)
        {
            if (generating) { SetStatus("Busy generating — try again in a moment."); return; }
            bool q = queueMode;
            queueMode = false;
            StartCoroutine(q ? Append(target, text) : Redirect(target, text));
        }
        else
        {
            // Typing only SETS the prompt — placement continues (dots,
            // touches, poses) and G/A generates when everything is staged.
            prompt = text;
            RefreshPromptMenu();
            SetStatus("Prompt set: " + Trunc(text, 44) + "\nPlace your markers, then G/A generates.");
        }
    }

    void RefreshTypingStatus()
    {
        string tgt = popupTarget != null
            ? (queueMode ? "follow-up for person " : "new behavior for person ")
              + (people.IndexOf(popupTarget) + 1)
            : "new prompt";
        string box = "<color=#ffd479><b>TYPE</b> — " + tgt + "</color>\n" +
                     typedText + "<color=#8fd0ff>▌</color>\n" +
                     "<color=#9aa4b5>[Enter] send   [Esc] cancel</color>";
        if (typingText != null)   // on-screen input box (no per-keystroke logs)
            typingText.text = box;
        if (vrKbPreview != null)  // preview ON the VR keyboard itself
            vrKbPreview.text = Wrap("TYPE — " + tgt + ":  " + typedText + "▌", 42);
    }

    // Follow-up clip anchored where the sequence ends; swapped in at the cut boundary.
    IEnumerator Append(Performer target, string newPrompt)
    {
        if (generating || target == null || target.Dir == null || target.motion == null)
        {
            SetStatus(generating ? "Busy generating — try again in a moment."
                                 : "Nothing to append to.");
            yield break;
        }
        generating = true;
        // anchor at the end of the LAST clip in the sequence — that is where
        // the new follow-up will start playing
        var lastClip = target.chain.Count > 0 ? target.chain[target.chain.Count - 1] : null;
        var m0 = lastClip != null ? lastClip.m : target.motion;
        float endT = target.chain.Count - 1 == target.chainIdx && target.Dir != null
            ? target.Dir.CutOut
            : (lastClip != null && lastClip.cutOut > 0
                ? lastClip.cutOut
                : m0.frame_count / Mathf.Max(1f, m0.fps));
        // -1 frame: match the LAST DISPLAYED frame before the swap fires
        int f = Mathf.Clamp(Mathf.RoundToInt(endT * m0.fps) - 1, 0, m0.frame_count - 1);
        float ax = -m0.root_flat[f * 3];          // Kimodo -> Unity x flip
        float az = m0.root_flat[f * 3 + 2];
        var ci = CultureInfo.InvariantCulture;
        // anchor at t=0 and hold the spot each second so the follow-up stays put
        float durA = Mathf.Max(4f, duration);
        var wsb = new StringBuilder();
        for (float tw = 0; tw < durA - 0.05f; tw += 1f)
        {
            if (wsb.Length > 0) wsb.Append(",");
            wsb.Append("{\"x\":").Append((-ax).ToString("0.000", ci))
               .Append(",\"z\":").Append(az.ToString("0.000", ci))
               .Append(",\"t\":").Append(tw.ToString("0.00", ci)).Append("}");
        }
        string body = "{\"prompt\":\"" + Escape(newPrompt) + "\"" +
            ",\"duration\":" + durA.ToString("0.0", ci) +
            ",\"steps\":" + steps + ",\"flat\":true,\"hand_anchor\":\"fingertip\"" +
            ",\"waypoints\":[" + wsb + "]" +
            ",\"hand_keys\":[],\"body_keys\":[],\"pose_keys\":[]}";

        SetStatus($"Queuing for person {people.IndexOf(target) + 1}: \"{Trunc(newPrompt, 40)}\"…");
        var progress = StartCoroutine(PollProgress());
        var req = new UnityWebRequest(serverUrl + "/api/generate", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 600;
        yield return req.SendWebRequest();
        StopCoroutine(progress);
        generating = false;

        if (req.result != UnityWebRequest.Result.Success) { SetStatus("Error: " + req.error); yield break; }
        var m = JsonUtility.FromJson<FlatMotion>(req.downloadHandler.text);
        if (m == null || m.frames_flat == null || m.frames_flat.Length == 0)
        {
            SetStatus("Error: unexpected response");
            yield break;
        }
        target.chain.Add(new ChainClip { m = m, json = body });
        SetStatus($"Follow-up queued — sequence of {target.chain.Count} actions now loops in order.");
    }

    IEnumerator Redirect(Performer target, string newPrompt)
    {
        if (generating || target == null || string.IsNullOrEmpty(target.requestJson)) yield break;
        generating = true;
        string body = ReplacePrompt(target.requestJson, newPrompt);
        SetStatus($"Person {people.IndexOf(target) + 1} → \"{Trunc(newPrompt, 44)}\"…");
        var progress = StartCoroutine(PollProgress());

        var req = new UnityWebRequest(serverUrl + "/api/generate", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 600;
        yield return req.SendWebRequest();
        StopCoroutine(progress);
        generating = false;

        if (req.result != UnityWebRequest.Result.Success)
        {
            SetStatus("Error: " + req.error);
            yield break;
        }
        var m = JsonUtility.FromJson<FlatMotion>(req.downloadHandler.text);
        if (m == null || m.frames_flat == null || m.frames_flat.Length == 0)
        {
            SetStatus("Error: unexpected response");
            yield break;
        }
        target.requestJson = body;
        target.chain.Clear();   // a redirect REPLACES the whole sequence
        target.chainIdx = 0;
        target.chain.Add(new ChainClip
        {
            m = m, json = body,
            cpts = target.contactPts, ctimes = target.contactTimes, wrist = target.wrist,
        });
        target.Kill();
        BuildPerformer(target, m);
        SetStatus($"Person {people.IndexOf(target) + 1} now: {Trunc(newPrompt, 52)}");
        StartCoroutine(FetchPrompts());
    }

    // ------------------------------------------- obstacle-aware approach path
    // Unity plans around inflated furniture footprints; Kimodo performs.
    void PlanApproach()
    {
        Vector3 start = entranceAnchor != null
            ? new Vector3(entranceAnchor.position.x, 0, entranceAnchor.position.z)
            : new Vector3(cam.transform.position.x, 0, cam.transform.position.z);
        Vector3 goal = new Vector3(bodyPoints[0].x, 0, bodyPoints[0].z);
        if (Vector3.Distance(start, goal) < 0.3f) return;   // already there

        var route = new List<Vector3> { start };
        route.AddRange(Detour(start, goal));
        route.Add(goal);

        float t = 0f;
        const float pace = 1.3f;   // m/s, comfortable walk
        for (int i = 0; i < route.Count; i++)
        {
            if (i > 0) t += Vector3.Distance(route[i - 1], route[i]) / pace;
            walkPoints.Add(route[i]);
            walkTimes.Add(t);
            Mark(new Vector3(route[i].x, 0.03f, route[i].z), new Color(0.29f, 0.56f, 1f), 0.03f);
        }
    }

    // Up to two corner detours keeping every segment clear; else the direct line.
    IEnumerable<Vector3> Detour(Vector3 a, Vector3 b)
    {
        if (SegClear(a, b)) return new Vector3[0];
        var corners = new List<Vector3>();
        foreach (var r in ObstacleRects())
        {
            corners.Add(new Vector3(r.xMin, 0, r.yMin));
            corners.Add(new Vector3(r.xMin, 0, r.yMax));
            corners.Add(new Vector3(r.xMax, 0, r.yMin));
            corners.Add(new Vector3(r.xMax, 0, r.yMax));
        }
        Vector3[] best = null;
        float bestLen = float.MaxValue;
        foreach (var c in corners)
        {
            if (!SegClear(a, c) || !SegClear(c, b)) continue;
            float len = Vector3.Distance(a, c) + Vector3.Distance(c, b);
            if (len < bestLen) { bestLen = len; best = new[] { c }; }
        }
        if (best == null)
            foreach (var c1 in corners)
                foreach (var c2 in corners)
                {
                    if (c1 == c2 || !SegClear(a, c1) || !SegClear(c1, c2) || !SegClear(c2, b)) continue;
                    float len = Vector3.Distance(a, c1) + Vector3.Distance(c1, c2) + Vector3.Distance(c2, b);
                    if (len < bestLen) { bestLen = len; best = new[] { c1, c2 }; }
                }
        return best ?? new Vector3[0];
    }

    const float CLEARANCE = 0.36f;   // body radius + comfort margin

    IEnumerable<Bounds> ObstacleBounds()
    {
        foreach (var s in surfaces)
            if (s != null && TryBounds(s, out var b)) yield return b;
        foreach (var s in sceneObstacles)
            if (s != null && TryBounds(s, out var b)) yield return b;
    }

    IEnumerable<Rect> ObstacleRects()
    {
        foreach (var b in ObstacleBounds()) yield return Inflate(b, CLEARANCE);
    }

    static Rect Inflate(Bounds b, float m) =>
        Rect.MinMaxRect(b.min.x - m, b.min.z - m, b.max.x + m, b.max.z + m);

    bool SegClear(Vector3 a, Vector3 b)
    {
        foreach (var ob in ObstacleBounds())
        {
            Rect big = Inflate(ob, CLEARANCE);
            // The standing spot sits INSIDE the comfort buffer by design —
            // segments that start or end there forbid only the hard footprint.
            Rect used = big.Contains(new Vector2(a.x, a.z)) || big.Contains(new Vector2(b.x, b.z))
                ? Inflate(ob, 0.05f) : big;
            if (SegmentHitsRect(a, b, used)) return false;
        }
        return true;
    }

    // 2D (xz) segment vs rectangle, Liang–Barsky clip.
    static bool SegmentHitsRect(Vector3 a, Vector3 b, Rect r)
    {
        float t0 = 0f, t1 = 1f, dx = b.x - a.x, dz = b.z - a.z;
        float[] p = { -dx, dx, -dz, dz };
        float[] q = { a.x - r.xMin, r.xMax - a.x, a.z - r.yMin, r.yMax - a.z };
        for (int i = 0; i < 4; i++)
        {
            if (Mathf.Abs(p[i]) < 1e-6f) { if (q[i] < 0) return false; continue; }
            float t = q[i] / p[i];
            if (p[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
        }
        return true;
    }

    static string ReplacePrompt(string json, string newPrompt)
    {
        int start = json.IndexOf("\"prompt\":\"", StringComparison.Ordinal);
        if (start < 0) return json;
        start += 10;
        int i = start;
        while (i < json.Length && !(json[i] == '"' && json[i - 1] != '\\')) i++;
        return json.Substring(0, start) + Escape(newPrompt) + json.Substring(i);
    }

    // With "Initialize XR on Startup" unticked, Play starts flat on the desktop;
    // V enters VR when the headset stream is ready, V again returns to desktop.
    IEnumerator ToggleVR()
    {
        var mgr = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        if (mgr == null) { SetStatus("XR Management unavailable."); yield break; }
        if (mgr.activeLoader == null)
        {
            SetStatus("Starting VR…");
            yield return mgr.InitializeLoader();
            if (mgr.activeLoader == null)
            {
                SetStatus("VR failed to start — is the headset streaming (SteamVR running)?");
                yield break;
            }
            mgr.StartSubsystems();
            StartCoroutine(EnsureFloorOrigin());
            SetStatus("VR on. (V in desktop window to leave)");
        }
        else
        {
            mgr.StopSubsystems();
            mgr.DeinitializeLoader();
            cam.transform.position = camHomePos;
            cam.transform.rotation = camHomeRot;
            SetStatus("Desktop mode.");
        }
    }

    readonly Dictionary<string, bool> was = new Dictionary<string, bool>();
    bool Pressed(string key, bool now)
    {
        was.TryGetValue(key, out bool before);
        was[key] = now;
        return now && !before;
    }

    void Sample(float interval, Action take)
    {
        if (captureStart < 0) { captureStart = Time.time; lastSample = -999; }
        if (Time.time - lastSample >= interval) { lastSample = Time.time; take(); }
    }

    // ------------------------------------------------------------ generation
    // J: save the whole configuration to JSON and generate from the file.
    IEnumerator SaveAndGenerate()
    {
        string body, path;
        try
        {
            body = BuildRequestJson();
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "KimodoConfigs"));
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "config_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            File.WriteAllText(path, body);
            File.WriteAllText(Path.Combine(dir, "latest.json"), body);
            body = File.ReadAllText(path);   // round-trip: run from the file
        }
        catch (Exception e)
        {
            SetStatus("Config save failed: " + e.Message);
            yield break;
        }
        SetStatus("Config saved: " + Path.GetFileName(path) + "\nGenerating from file…");
        Debug.Log("[KimodoVR] Config saved to " + path);
        yield return Generate(body);
    }

    IEnumerator Generate(string presetBody = null)
    {
        generating = true;
        string body = presetBody;
        if (body == null)
        {
            try { body = BuildRequestJson(); }
            catch (Exception e)
            {
                // an exception here would otherwise leave `generating` stuck true
                generating = false;
                SetStatus("Request build failed: " + e.Message);
                yield break;
            }
        }
        sentRequestJson = body;
        SetStatus("Generating…");
        var progress = StartCoroutine(PollProgress());

        var req = new UnityWebRequest(serverUrl + "/api/generate", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 600;
        yield return req.SendWebRequest();
        StopCoroutine(progress);

        if (req.result != UnityWebRequest.Result.Success)
        {
            SetStatus("Error: " + req.error);
        }
        else
        {
            var m = JsonUtility.FromJson<FlatMotion>(req.downloadHandler.text);
            if (m != null && m.frames_flat != null && m.frames_flat.Length > 0)
            {
                // captures are consumed — the next trace defines the next person
                handPoints.Clear(); handTimes.Clear(); bodyPoints.Clear();
                walkPoints.Clear(); walkTimes.Clear();
                poseTimes.Clear(); poseL.Clear(); poseR.Clear(); poseBody.Clear();
                poseHeadY.Clear(); poseHeading.Clear();
                captureStart = -1f;
                foreach (Transform c in markers) Destroy(c.gameObject);
                StartCoroutine(SpawnPerformer(m));
            }
            else SetStatus("Error: unexpected response " + req.downloadHandler.text.Substring(0, Mathf.Min(120, req.downloadHandler.text.Length)));
        }
        StartCoroutine(FetchPrompts());
        generating = false;
    }

    IEnumerator SpawnPerformer(FlatMotion m)
    {
        // Snapshot before the countdown — captures may change meanwhile.
        var rec = new Performer
        {
            requestJson = sentRequestJson,
            contactPts = sentHandTargets.ToArray(),
            contactTimes = sentHandTimes.ToArray(),
            wrist = sentAnchorWrist,
        };
        // Countdown so the user can step aside.
        for (int s = 3; s > 0; s--)
        {
            SetStatus($"Step aside — performer starts where you stood… {s}");
            yield return new WaitForSeconds(1f);
        }
        people.Add(rec);
        rec.chain.Add(new ChainClip
        {
            m = m, json = rec.requestJson,
            cpts = rec.contactPts, ctimes = rec.contactTimes, wrist = rec.wrist,
        });
        BuildPerformer(rec, m);
        SetStatus($"Person {people.Count} playing ({m.gen_seconds:0.0}s). " +
                  "Click a person to change their behavior. T types a command.");
    }

    void BuildPerformer(Performer rec, FlatMotion m)
    {
        rec.motion = m;
        int idx = Mathf.Max(0, people.IndexOf(rec));
        if (characterPrefab == null)
            Debug.LogWarning("[KimodoVR] Character Prefab slot is EMPTY on the KimodoVR " +
                             "component — using capsule mannequin. Drag your Humanoid " +
                             "character from Assets onto the slot in the Inspector.");
        else
            rec.cp = KimodoCharacterPlayer.Create(
                characterPrefab, m.joint_names, m.parents, m.bone_offsets_flat,
                m.quats_flat, m.root_flat, m.frame_count, m.joint_count,
                m.root_index, m.fps);
        if (rec.cp != null)
        {
            rec.cp.SetContact(rec.contactPts, rec.contactTimes, rec.wrist);
            if (roomKnown) rec.cp.SetRoom(roomRect);
            var ko = new List<Rect>();
            foreach (var b in ObstacleBounds())
                ko.Add(Rect.MinMaxRect(b.min.x, b.min.z, b.max.x, b.max.z));
            rec.cp.SetKeepOut(ko.ToArray());
            Debug.Log("[KimodoVR] Playing on skinned character mesh.");
        }
        else
        {
            // capsule fallback — convert Kimodo x back to Unity x
            for (int i = 0; i < m.frames_flat.Length; i += 3) m.frames_flat[i] = -m.frames_flat[i];
            var go = new GameObject("Performer" + (idx + 1));
            rec.sp = go.AddComponent<KimodoSkeletonPlayer>();
            rec.sp.accent = palette[idx % palette.Length];
            rec.sp.Play(m.frames_flat, m.frame_count, m.joint_count, m.parents, m.joint_names, m.fps);
        }
    }

    string BuildRequestJson()
    {
        var ci = CultureInfo.InvariantCulture;
        // Normalize the capture clock so the demonstration starts at t=0.
        float tFirst = float.MaxValue;
        if (walkTimes.Count > 0) tFirst = Mathf.Min(tFirst, walkTimes[0]);
        if (poseTimes.Count > 0) tFirst = Mathf.Min(tFirst, poseTimes[0]);
        if (tFirst != float.MaxValue && tFirst > 0.05f)
        {
            for (int i = 0; i < walkTimes.Count; i++) walkTimes[i] -= tFirst;
            for (int i = 0; i < poseTimes.Count; i++) poseTimes[i] -= tFirst;
            for (int i = 0; i < handTimes.Count; i++) handTimes[i] -= tFirst;
        }

        // Compress idle gaps to natural walking pace.
        if (walkTimes.Count > 1)
        {
            var oldT = new List<float>(walkTimes);
            float shift = 0f;
            for (int i = 1; i < walkTimes.Count; i++)
            {
                float gap = oldT[i] - oldT[i - 1];
                float natural = Vector3.Distance(walkPoints[i], walkPoints[i - 1]) / 1.3f + 1.2f;
                if (gap > natural) shift += gap - natural;
                walkTimes[i] = oldT[i] - shift;
            }
            for (int k = 0; k < poseTimes.Count; k++)
            {
                int best = 0;   // poses share timestamps with their walk entry
                for (int i = 1; i < oldT.Count; i++)
                    if (Mathf.Abs(oldT[i] - poseTimes[k]) < Mathf.Abs(oldT[best] - poseTimes[k]))
                        best = i;
                poseTimes[k] = walkTimes[best];
            }
        }
        // Kimodo has no scene awareness: plan the approach into the waypoints.
        if (walkPoints.Count == 0 && bodyPoints.Count > 0) PlanApproach();
        else if (walkPoints.Count > 0 && bodyPoints.Count > 0)
        {
            // Hand-placed route: make sure it actually ends at the standing
            // spot in front of the clicked surface, so the touch is reachable.
            Vector3 goal = new Vector3(bodyPoints[0].x, 0, bodyPoints[0].z);
            Vector3 last = walkPoints[walkPoints.Count - 1];
            if (Vector3.Distance(last, goal) > 0.35f)
            {
                walkTimes.Add(walkTimes[walkTimes.Count - 1] + Vector3.Distance(last, goal) / 1.3f);
                walkPoints.Add(goal);
                Mark(new Vector3(goal.x, 0.03f, goal.z), new Color(0.29f, 0.56f, 1f), 0.03f);
            }
        }
        // Timeline: walk first, then the hand trace after a lead-in;
        // short traces are stretched to a deliberate tempo.
        float rawLen = handTimes.Count > 1 ? handTimes[handTimes.Count - 1] - handTimes[0] : 0f;
        float tScale = rawLen > 0.01f && rawLen < 2.5f ? 2.5f / rawLen : 1f;

        float walkLen = walkTimes.Count > 0 ? walkTimes[walkTimes.Count - 1] : 0f;
        float lead = walkLen > 0 ? walkLen + 0.8f : 2.5f;
        float traceLen = Mathf.Max(rawLen * tScale, 1.5f);
        // Clip ends shortly after the demonstration (prompt-only keeps `duration`).
        bool hasEvents = walkPoints.Count > 0 || handPoints.Count > 0 || poseTimes.Count > 0;
        float dur;
        if (hasEvents)
        {
            dur = Mathf.Max(3f, handPoints.Count > 0 ? lead + traceLen + 0.7f : 0f);
            dur = Mathf.Max(dur, walkLen + 1.8f);
            if (poseTimes.Count > 0)
                dur = Mathf.Max(dur, poseTimes[poseTimes.Count - 1] + 1.8f);
        }
        else dur = duration;

        var sb = new StringBuilder(4096);
        sb.Append("{\"prompt\":\"").Append(Escape(prompt)).Append("\"");
        sb.Append(",\"duration\":").Append(dur.ToString("0.0", ci));
        sb.Append(",\"steps\":").Append(steps);
        sb.Append(",\"flat\":true");
        sb.Append(",\"hand_anchor\":\"").Append(capturedInVR ? "wrist" : "fingertip").Append("\"");

        // NB: Unity is left-handed, Kimodo right-handed — negate X at this
        // boundary (and back on receipt) so the character's left/right is true.
        sb.Append(",\"waypoints\":[");
        for (int i = 0; i < walkPoints.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"x\":").Append((-walkPoints[i].x).ToString("0.000", ci))
              .Append(",\"z\":").Append(walkPoints[i].z.ToString("0.000", ci))
              .Append(",\"t\":").Append(walkTimes[i].ToString("0.00", ci)).Append("}");
        }
        // Pin the root at the final dot until the clip ends (no overshoot).
        if (walkPoints.Count > 0)
        {
            Vector3 lastW = walkPoints[walkPoints.Count - 1];
            for (float th = walkTimes[walkTimes.Count - 1] + 1.2f; th < dur - 0.05f; th += 1.5f)
                sb.Append(",{\"x\":").Append((-lastW.x).ToString("0.000", ci))
                  .Append(",\"z\":").Append(lastW.z.ToString("0.000", ci))
                  .Append(",\"t\":").Append(th.ToString("0.00", ci)).Append("}");
        }
        sb.Append("]");

        sb.Append(",\"hand_keys\":[");
        sentHandTargets.Clear();
        sentHandTimes.Clear();
        sentAnchorWrist = capturedInVR;
        for (int i = 0; i < handPoints.Count; i++)
        {
            if (i > 0) sb.Append(",");
            float t = lead + (handTimes.Count > i ? (handTimes[i] - handTimes[0]) * tScale : i * 0.5f);
            sentHandTargets.Add(handPoints[i]);   // Unity space, as captured
            sentHandTimes.Add(t);
            sb.Append("{\"x\":").Append((-handPoints[i].x).ToString("0.000", ci))
              .Append(",\"y\":").Append(handPoints[i].y.ToString("0.000", ci))
              .Append(",\"z\":").Append(handPoints[i].z.ToString("0.000", ci))
              .Append(",\"t\":").Append(t.ToString("0.00", ci)).Append("}");
        }
        sb.Append("]");

        sb.Append(",\"body_keys\":[");
        for (int i = 0; i < bodyPoints.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"x\":").Append((-bodyPoints[i].x).ToString("0.000", ci))
              .Append(",\"z\":").Append(bodyPoints[i].z.ToString("0.000", ci)).Append("}");
        }
        sb.Append("]");

        sb.Append(",\"pose_keys\":[");   // snapped VR poses (x negated for Kimodo)
        for (int i = 0; i < poseTimes.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"t\":").Append(poseTimes[i].ToString("0.00", ci))
              .Append(",\"bx\":").Append((-poseBody[i].x).ToString("0.000", ci))
              .Append(",\"bz\":").Append(poseBody[i].z.ToString("0.000", ci))
              .Append(",\"by\":").Append(poseHeadY[i].ToString("0.000", ci))
              .Append(",\"h\":").Append(poseHeading[i].ToString("0.000", ci))
              .Append(",\"lx\":").Append((-poseL[i].x).ToString("0.000", ci))
              .Append(",\"ly\":").Append(poseL[i].y.ToString("0.000", ci))
              .Append(",\"lz\":").Append(poseL[i].z.ToString("0.000", ci))
              .Append(",\"rx\":").Append((-poseR[i].x).ToString("0.000", ci))
              .Append(",\"ry\":").Append(poseR[i].y.ToString("0.000", ci))
              .Append(",\"rz\":").Append(poseR[i].z.ToString("0.000", ci)).Append("}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    IEnumerator PollProgress()
    {
        while (true)
        {
            var req = UnityWebRequest.Get(serverUrl + "/api/progress");
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
            {
                Progress p = null;
                try { p = JsonUtility.FromJson<Progress>(req.downloadHandler.text); }
                catch { }
                if (p != null && p.phase != "idle" && p.phase != "done")
                    SetStatus(p.phase + (p.pct > 0 ? $" {p.pct}%" : "") +
                              (string.IsNullOrEmpty(p.detail) ? "" : "\n" + Trunc(p.detail, 60)));
            }
            yield return new WaitForSeconds(0.5f);
        }
    }

    IEnumerator FetchPrompts()
    {
        var req = UnityWebRequest.Get(serverUrl + "/api/prompts");
        yield return req.SendWebRequest();
        if (req.result == UnityWebRequest.Result.Success)
        {
            var list = JsonUtility.FromJson<PromptList>(req.downloadHandler.text);
            if (list != null && list.prompts != null) cachedPrompts = list.prompts;
            RefreshPromptMenu();
        }
    }

    void CyclePrompt(int dir = 1)
    {
        if (cachedPrompts.Length == 0) { SetStatus("No cached prompts yet."); return; }
        promptIndex = (promptIndex + dir + cachedPrompts.Length) % cachedPrompts.Length;
        prompt = cachedPrompts[promptIndex];
        RefreshPromptMenu();
        SetStatus("Prompt: " + Trunc(prompt, 70));
    }

    void ClearCaptures()
    {
        handPoints.Clear(); handTimes.Clear(); bodyPoints.Clear();
        walkPoints.Clear(); walkTimes.Clear();
        poseTimes.Clear(); poseL.Clear(); poseR.Clear(); poseBody.Clear();
        poseHeadY.Clear(); poseHeading.Clear();
        captureStart = -1f;
        foreach (Transform c in markers) Destroy(c.gameObject);
        foreach (var p in people) p.Kill();
        people.Clear();
        ClosePopup();
        SetStatus("Cleared.");
    }

    // ---------------------------------------------------------- environment
    void BuildEnvironment()
    {
        // A user-authored scene: surfaces dragged into the Inspector replace
        // the default room entirely — their lights, floor, and camera stay.
        bool custom = sceneSurfaces != null && sceneSurfaces.Count > 0;

        cam = Camera.main;
        bool camWasInScene = cam != null;
        if (cam == null)
        {
            cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
        }
        if (!custom || !camWasInScene)
        {
            cam.transform.position = new Vector3(1.8f, 1.7f, -0.8f);
            cam.transform.LookAt(new Vector3(0, 0.9f, 1.2f));
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
        }
        camHomePos = cam.transform.position;
        camHomeRot = cam.transform.rotation;

        surfaces.Clear();
        if (custom)
        {
            foreach (var s in sceneSurfaces) if (s != null)
            {
                EnsureColliders(s);
                surfaces.Add(s);
                // The generated human is ALWAYS 1.75 m — scene furniture must
                // be real-world scale, so flag implausible surface heights.
                string kind = s.GetComponentInChildren<Renderer>() == null ? "Area" : "Surface";
                if (TryBounds(s, out var b))
                    Debug.Log($"[KimodoVR] {kind} '{s.name}' top at {b.max.y:F2} m" +
                              (b.max.y > 1.1f ? "  <-- too high for a 1.75 m human (real tables ~0.75 m)" :
                               b.max.y < 0.3f ? "  <-- very low (seat ~0.45, table ~0.75)" : "  (ok)"));
                else
                    Debug.LogWarning($"[KimodoVR] {kind} '{s.name}' has NO collider — " +
                                     "add a BoxCollider so it can be clicked/traced.");
            }
            foreach (var o in sceneObstacles) if (o != null) EnsureColliders(o);
            roomRect = ComputeRoomRect();
            roomKnown = true;
            Debug.Log($"[KimodoVR] Room bounds: x {roomRect.xMin:F1}..{roomRect.xMax:F1}, " +
                      $"z {roomRect.yMin:F1}..{roomRect.yMax:F1} — performers stay inside.");
            DrawSurfaceFrames();
            BuildUI();
            return;
        }

        var lightGo = new GameObject("Sun");
        var sun = lightGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.3f;
        lightGo.transform.rotation = Quaternion.Euler(55, -35, 0);

        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.localScale = new Vector3(2, 1, 2);
        Tint(floor, new Color(0.16f, 0.18f, 0.21f));

        tableTop = GameObject.CreatePrimitive(PrimitiveType.Cube);
        tableTop.name = "TableTop";
        tableTop.transform.position = new Vector3(0, 0.73f, 1.4f);
        tableTop.transform.localScale = new Vector3(1.2f, 0.04f, 0.7f);
        Tint(tableTop, new Color(0.36f, 0.29f, 0.21f));
        foreach (var (x, z) in new[] { (-0.55f, 1.1f), (0.55f, 1.1f), (-0.55f, 1.7f), (0.55f, 1.7f) })
        {
            var leg = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            leg.transform.position = new Vector3(x, 0.355f, z);
            leg.transform.localScale = new Vector3(0.05f, 0.355f, 0.05f);
            leg.transform.SetParent(tableTop.transform.parent);
            Tint(leg, new Color(0.27f, 0.22f, 0.16f));
        }

        // A higher surface next to the table — pick-and-place target.
        shelfTop = GameObject.CreatePrimitive(PrimitiveType.Cube);
        shelfTop.name = "Shelf";
        shelfTop.transform.position = new Vector3(1.05f, 0.575f, 1.3f);
        shelfTop.transform.localScale = new Vector3(0.4f, 1.15f, 0.4f);
        Tint(shelfTop, new Color(0.3f, 0.33f, 0.4f));

        // A robot prop on the table (static scene dressing for now).
        var robotBase = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        robotBase.transform.position = new Vector3(-0.42f, 0.80f, 1.55f);
        robotBase.transform.localScale = new Vector3(0.12f, 0.05f, 0.12f);
        Tint(robotBase, new Color(0.75f, 0.77f, 0.8f));
        var link1 = GameObject.CreatePrimitive(PrimitiveType.Cube);
        link1.transform.position = new Vector3(-0.42f, 0.95f, 1.55f);
        link1.transform.localScale = new Vector3(0.05f, 0.22f, 0.05f);
        Tint(link1, new Color(0.9f, 0.55f, 0.15f));
        var link2 = GameObject.CreatePrimitive(PrimitiveType.Cube);
        link2.transform.position = new Vector3(-0.42f, 1.06f, 1.44f);
        link2.transform.rotation = Quaternion.Euler(55, 0, 0);
        link2.transform.localScale = new Vector3(0.045f, 0.24f, 0.045f);
        Tint(link2, new Color(0.9f, 0.55f, 0.15f));
        var gripper = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        gripper.transform.position = new Vector3(-0.42f, 1.12f, 1.32f);
        gripper.transform.localScale = Vector3.one * 0.07f;
        Tint(gripper, new Color(0.3f, 0.3f, 0.32f));

        surfaces.Add(tableTop);
        surfaces.Add(shelfTop);
        roomRect = Rect.MinMaxRect(-3.9f, -3.9f, 3.9f, 3.9f);   // default room walls
        roomKnown = true;
        DrawSurfaceFrames();
        BuildUI();
    }

    void BuildUI()
    {

        // VR HUD: head-locked panel; hold X toggles it, sticks move it.
        vrHud = new GameObject("VrHud").transform;
        vrHud.SetParent(cam.transform, false);
        vrHud.localPosition = new Vector3(0f, -0.02f, 1.05f);
        vrHud.localScale = Vector3.one * 0.86f;
        var hudBack = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hudBack.name = "VrHudBack";
        Destroy(hudBack.GetComponent<Collider>());
        hudBack.transform.SetParent(vrHud, false);
        hudBack.transform.localPosition = new Vector3(0, 0, 0.03f);
        hudBack.transform.localScale = new Vector3(1.36f, 0.6f, 0.004f);
        Tint(hudBack, new Color(0.07f, 0.08f, 0.11f));

        var vmGo = new GameObject("VrPrompts");
        boardMenu = vmGo.AddComponent<TextMesh>();
        vmGo.transform.SetParent(vrHud, false);
        vmGo.transform.localPosition = new Vector3(-0.64f, 0.25f, 0);
        boardMenu.characterSize = 0.0095f;
        boardMenu.fontSize = 48;
        boardMenu.anchor = TextAnchor.UpperLeft;
        boardMenu.color = new Color(0.75f, 0.85f, 1f);

        // thin divider between the prompt list and the controls columns
        var div = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(div.GetComponent<Collider>());
        div.transform.SetParent(vrHud, false);
        div.transform.localPosition = new Vector3(-0.2f, 0, 0.01f);
        div.transform.localScale = new Vector3(0.003f, 0.5f, 0.002f);
        Tint(div, new Color(0.3f, 0.35f, 0.45f));

        // controls as ALIGNED columns: keys left (cyan), actions right —
        // the two texts must keep identical line counts
        var gkGo = new GameObject("VrGuideKeys");
        vrGuideKeys = gkGo.AddComponent<TextMesh>();
        gkGo.transform.SetParent(vrHud, false);
        gkGo.transform.localPosition = new Vector3(-0.16f, 0.25f, 0);
        vrGuideKeys.characterSize = 0.0085f;
        vrGuideKeys.fontSize = 48;
        vrGuideKeys.anchor = TextAnchor.UpperLeft;
        vrGuideKeys.color = new Color(0.56f, 0.82f, 1f);
        vrGuideKeys.text =
            "<color=#ffd479><b>CONTROLS</b></color>\n" +
            "R-trig tap\nR-trig hold\nL-trig hold\nX tap\n" +
            "A · B tap\nB hold\npoint + B\nsticks\nX hold";

        var tutGo = new GameObject("VrTutorial");
        boardTut = tutGo.AddComponent<TextMesh>();
        tutGo.transform.SetParent(vrHud, false);
        tutGo.transform.localPosition = new Vector3(0.1f, 0.25f, 0);
        boardTut.characterSize = 0.0085f;
        boardTut.fontSize = 48;
        boardTut.anchor = TextAnchor.UpperLeft;
        boardTut.color = new Color(0.88f, 0.92f, 1f);
        boardTut.text =
            "\n" +
            "place point (surf / floor)\n" +
            "trace on the surface\n" +
            "walk a path\n" +
            "snap a pose\n" +
            "generate · next prompt\n" +
            "type (laser + trigger)\n" +
            "select person\n" +
            "move · rotate · cut ↑/↓\n" +
            "this panel";
        vrHud.gameObject.SetActive(false);

        // Prompt list: a REAL screen-space panel (uGUI) — always visible
        // regardless of camera FOV/aspect, movable by the bar at its bottom.
        var canvasGo = new GameObject("KimodoHUD");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();
        if (EventSystem.current == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<InputSystemUIInputModule>();
        }

        Font uiFont = null;
        try { uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (uiFont == null)
            try { uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }

        var panel = new GameObject("PromptPanel").AddComponent<Image>();
        panel.transform.SetParent(canvasGo.transform, false);
        leftHudPanels.Add(panel.gameObject);
        panel.color = new Color(0.06f, 0.07f, 0.1f, 0.82f);
        var prt = panel.rectTransform;
        prt.anchorMin = prt.anchorMax = new Vector2(0, 1);
        prt.pivot = new Vector2(0, 1);
        prt.anchoredPosition = new Vector2(12, -12);
        prt.sizeDelta = new Vector2(460, 210);

        var textGo = new GameObject("PromptText");
        promptMenu = textGo.AddComponent<Text>();
        textGo.transform.SetParent(panel.transform, false);
        promptMenu.font = uiFont;
        promptMenu.fontSize = 20;
        promptMenu.supportRichText = true;
        promptMenu.color = new Color(0.78f, 0.87f, 1f);
        var trt = promptMenu.rectTransform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(10, 22);   // room for the drag bar below
        trt.offsetMax = new Vector2(-10, -8);

        var bar = new GameObject("DragBar").AddComponent<Image>();
        bar.transform.SetParent(panel.transform, false);
        bar.color = new Color(0.3f, 0.4f, 0.55f, 0.9f);
        var brt = bar.rectTransform;
        brt.anchorMin = new Vector2(0, 0);
        brt.anchorMax = new Vector2(1, 0);
        brt.pivot = new Vector2(0.5f, 0);
        brt.anchoredPosition = Vector2.zero;
        brt.sizeDelta = new Vector2(0, 16);
        bar.gameObject.AddComponent<UiDragHandle>().target = prt;
        var barTxt = new GameObject("lbl").AddComponent<Text>();
        barTxt.transform.SetParent(bar.transform, false);
        barTxt.font = uiFont;
        barTxt.fontSize = 11;
        barTxt.text = "· · ·  drag  · · ·";
        barTxt.alignment = TextAnchor.MiddleCenter;
        barTxt.color = new Color(0.85f, 0.9f, 1f, 0.75f);
        var btr = barTxt.rectTransform;
        btr.anchorMin = Vector2.zero;
        btr.anchorMax = Vector2.one;
        btr.offsetMin = btr.offsetMax = Vector2.zero;

        // Collapsible controls cheat-sheet, right under the prompt panel.
        controlsPanel = new GameObject("ControlsPanel").AddComponent<Image>();
        controlsPanel.transform.SetParent(canvasGo.transform, false);
        leftHudPanels.Add(controlsPanel.gameObject);
        controlsPanel.color = new Color(0.06f, 0.07f, 0.1f, 0.82f);
        var crt = controlsPanel.rectTransform;
        crt.anchorMin = crt.anchorMax = new Vector2(0, 1);
        crt.pivot = new Vector2(0, 1);
        crt.anchoredPosition = new Vector2(12, -232);
        crt.sizeDelta = new Vector2(460, 30);

        var headGo = new GameObject("Header");
        controlsHeader = headGo.AddComponent<Text>();
        headGo.transform.SetParent(controlsPanel.transform, false);
        controlsHeader.font = uiFont;
        controlsHeader.fontSize = 18;
        controlsHeader.color = new Color(0.95f, 0.85f, 0.55f);
        controlsHeader.text = "▸ CONTROLS — click to open";
        controlsHeader.alignment = TextAnchor.MiddleLeft;
        var hrt = controlsHeader.rectTransform;
        hrt.anchorMin = new Vector2(0, 1);
        hrt.anchorMax = new Vector2(1, 1);
        hrt.pivot = new Vector2(0.5f, 1);
        hrt.anchoredPosition = new Vector2(0, 0);
        hrt.sizeDelta = new Vector2(-24, 30);
        headGo.AddComponent<Button>().onClick.AddListener(ToggleControls);

        // Two aligned columns: keys on the left, actions on the right — the
        // line counts of the two texts must stay identical.
        var keysGo = new GameObject("Keys");
        controlsKeys = keysGo.AddComponent<Text>();
        keysGo.transform.SetParent(controlsPanel.transform, false);
        controlsKeys.font = uiFont;
        controlsKeys.fontSize = 15;
        controlsKeys.horizontalOverflow = HorizontalWrapMode.Overflow;
        controlsKeys.color = new Color(0.56f, 0.82f, 1f);
        var krt = controlsKeys.rectTransform;
        krt.anchorMin = new Vector2(0, 0);
        krt.anchorMax = new Vector2(0, 1);
        krt.offsetMin = new Vector2(14, 8);
        krt.offsetMax = new Vector2(120, -34);
        controlsKeys.text =
            "\nL-click\nR-click\nX\nH\n" +
            "\nG / J\nP / T\nC\n" +
            "\n1-9 / T\nN\n[ ] F\narrows , .\nDel";

        var cbodyGo = new GameObject("Body");
        controlsBody = cbodyGo.AddComponent<Text>();
        cbodyGo.transform.SetParent(controlsPanel.transform, false);
        controlsBody.font = uiFont;
        controlsBody.fontSize = 15;
        controlsBody.supportRichText = true;
        controlsBody.horizontalOverflow = HorizontalWrapMode.Overflow;
        controlsBody.color = new Color(0.82f, 0.87f, 0.95f);
        var cbrt = controlsBody.rectTransform;
        cbrt.anchorMin = Vector2.zero;
        cbrt.anchorMax = Vector2.one;
        cbrt.offsetMin = new Vector2(126, 8);
        cbrt.offsetMax = new Vector2(-10, -34);
        controlsBody.text =
            "<color=#ffd479><b>CAPTURE</b></color>\n" +
            "touch point + stand spot\n" +
            "walk dot (first = spawn)\n" +
            "snap current pose\n" +
            "sim rig: WASD/QE · RMB look · Ctrl/Shift hands\n" +
            "<color=#ffd479><b>GENERATE</b></color>\n" +
            "generate / save config + run\n" +
            "cycle / type prompt\n" +
            "clear everything\n" +
            "<color=#ffd479><b>PERSON — click one</b></color>\n" +
            "new behavior (pick / type)\n" +
            "queue follow-up action\n" +
            "cut start/end · full clip\n" +
            "move · rotate  (R resets)\n" +
            "remove person";
        keysGo.SetActive(false);
        cbodyGo.SetActive(false);

        // VR controller map: its own collapsible section below CONTROLS.
        mapPanel = new GameObject("MapPanel").AddComponent<Image>();
        mapPanel.transform.SetParent(canvasGo.transform, false);
        leftHudPanels.Add(mapPanel.gameObject);
        mapPanel.color = new Color(0.06f, 0.07f, 0.1f, 0.82f);
        var mprt = mapPanel.rectTransform;
        mprt.anchorMin = mprt.anchorMax = new Vector2(0, 1);
        mprt.pivot = new Vector2(0, 1);
        mprt.sizeDelta = new Vector2(460, 26);
        var mhGo = new GameObject("MapHeader");
        mapHeader = mhGo.AddComponent<Text>();
        mhGo.transform.SetParent(mapPanel.transform, false);
        mapHeader.font = uiFont;
        mapHeader.fontSize = 16;
        mapHeader.color = new Color(0.95f, 0.85f, 0.55f);
        mapHeader.text = "▸ CONTROLLER MAP — click";
        mapHeader.alignment = TextAnchor.MiddleLeft;
        var mhrt = mapHeader.rectTransform;
        mhrt.anchorMin = Vector2.zero;
        mhrt.anchorMax = Vector2.one;
        mhrt.offsetMin = new Vector2(12, 0);
        mhrt.offsetMax = new Vector2(-6, 0);
        mhGo.AddComponent<Button>().onClick.AddListener(ToggleMap);
        var dTex = Resources.Load<Texture2D>("controller_guide");
        if (dTex != null)
        {
            controlsDiag = new GameObject("Diagram").AddComponent<RawImage>();
            controlsDiag.transform.SetParent(mapPanel.transform, false);
            controlsDiag.texture = dTex;
            var drt = controlsDiag.rectTransform;
            drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 0);
            drt.pivot = new Vector2(0.5f, 0);
            drt.anchoredPosition = new Vector2(0, 6);
            diagH = 444f * dTex.height / dTex.width;
            drt.sizeDelta = new Vector2(444, diagH);
            controlsDiag.gameObject.SetActive(false);
        }
        LayoutHudStack();
        foreach (var g in leftHudPanels) g.SetActive(false);   // Tab summons them

        // Typing box (T): hidden until typing starts, centered near the top.
        typingPanel = new GameObject("TypingPanel").AddComponent<Image>();
        typingPanel.transform.SetParent(canvasGo.transform, false);
        typingPanel.color = new Color(0.05f, 0.06f, 0.09f, 0.92f);
        var tprt = typingPanel.rectTransform;
        tprt.anchorMin = tprt.anchorMax = new Vector2(0.5f, 1);
        tprt.pivot = new Vector2(0.5f, 1);
        tprt.anchoredPosition = new Vector2(0, -16);
        tprt.sizeDelta = new Vector2(720, 96);
        var ttGo = new GameObject("TypingText");
        typingText = ttGo.AddComponent<Text>();
        ttGo.transform.SetParent(typingPanel.transform, false);
        typingText.font = uiFont;
        typingText.fontSize = 20;
        typingText.supportRichText = true;
        typingText.color = new Color(0.95f, 0.95f, 1f);
        var ttrt = typingText.rectTransform;
        ttrt.anchorMin = Vector2.zero;
        ttrt.anchorMax = Vector2.one;
        ttrt.offsetMin = new Vector2(14, 8);
        ttrt.offsetMax = new Vector2(-14, -8);
        typingPanel.gameObject.SetActive(false);

        // Virtual keyboard for VR typing: point with the RIGHT controller,
        // pull the trigger to press the highlighted key.
        vrKb = new GameObject("VrKeyboard").transform;
        vrKb.SetParent(cam.transform, false);
        vrKb.localPosition = new Vector3(0, -0.17f, 0.8f);
        var kbBack = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(kbBack.GetComponent<Collider>());
        kbBack.transform.SetParent(vrKb, false);
        kbBack.transform.localPosition = new Vector3(0, 0.025f, 0.012f);
        kbBack.transform.localScale = new Vector3(0.8f, 0.46f, 0.003f);
        Tint(kbBack, new Color(0.07f, 0.08f, 0.11f));

        // the typed text lives ON the keyboard — nothing else shows behind it
        var kpGo = new GameObject("KbPreview");
        vrKbPreview = kpGo.AddComponent<TextMesh>();
        kpGo.transform.SetParent(vrKb, false);
        kpGo.transform.localPosition = new Vector3(0, 0.20f, 0);
        vrKbPreview.characterSize = 0.0095f;
        vrKbPreview.fontSize = 48;
        vrKbPreview.anchor = TextAnchor.MiddleCenter;
        vrKbPreview.alignment = TextAlignment.Center;
        vrKbPreview.color = new Color(0.95f, 0.95f, 1f);
        string[] kbRows = { "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
        for (int r = 0; r < kbRows.Length; r++)
            for (int i = 0; i < kbRows[r].Length; i++)
                VrKey(kbRows[r][i].ToString(),
                      -0.335f + 0.07f * i + 0.025f * r, 0.11f - 0.075f * r);
        VrKey("SPACE", -0.17f, -0.125f, 0.24f, 0.008f);
        VrKey("DEL", 0.02f, -0.125f, 0.10f, 0.008f);
        VrKey("ENTER", 0.16f, -0.125f, 0.14f, 0.008f);
        VrKey("ESC", 0.31f, -0.125f, 0.10f, 0.008f);
        vrKb.gameObject.SetActive(false);

        // Guided tutorial: a strip at the bottom of the screen + a mirrored
        // line under the world board (readable in VR). F1 hides/replays it.
        var tutPanel = new GameObject("TutorialStrip").AddComponent<Image>();
        tutPanel.transform.SetParent(canvasGo.transform, false);
        tutPanel.color = new Color(0.05f, 0.1f, 0.06f, 0.88f);
        var tsrt = tutPanel.rectTransform;
        tsrt.anchorMin = tsrt.anchorMax = new Vector2(0.5f, 0);
        tsrt.pivot = new Vector2(0.5f, 0);
        tsrt.anchoredPosition = new Vector2(0, 138);   // above the status window
        tsrt.sizeDelta = new Vector2(1000, 44);
        var tstGo = new GameObject("TutText");
        tutStrip = tstGo.AddComponent<Text>();
        tstGo.transform.SetParent(tutPanel.transform, false);
        tutStrip.font = uiFont;
        tutStrip.fontSize = 19;
        tutStrip.alignment = TextAnchor.MiddleCenter;
        tutStrip.supportRichText = true;
        tutStrip.color = new Color(0.75f, 1f, 0.8f);
        var tstrt = tutStrip.rectTransform;
        tstrt.anchorMin = Vector2.zero;
        tstrt.anchorMax = Vector2.one;
        tstrt.offsetMin = tstrt.offsetMax = Vector2.zero;

        // STATUS window: the last command + what is happening now, as a small
        // log (newest bright, older dimmed; progress updates replace in place).
        var slPanel = new GameObject("StatusWindow").AddComponent<Image>();
        slPanel.transform.SetParent(canvasGo.transform, false);
        slPanel.color = new Color(0.06f, 0.07f, 0.1f, 0.85f);
        var slrt = slPanel.rectTransform;
        slrt.anchorMin = slrt.anchorMax = new Vector2(0.5f, 0);
        slrt.pivot = new Vector2(0.5f, 0);
        slrt.anchoredPosition = new Vector2(0, 12);
        slrt.sizeDelta = new Vector2(720, 118);
        var slGo = new GameObject("StatusText");
        statusLog = slGo.AddComponent<Text>();
        slGo.transform.SetParent(slPanel.transform, false);
        statusLog.font = uiFont;
        statusLog.fontSize = 17;
        statusLog.supportRichText = true;
        statusLog.color = Color.white;
        var slt = statusLog.rectTransform;
        slt.anchorMin = Vector2.zero;
        slt.anchorMax = Vector2.one;
        slt.offsetMin = new Vector2(14, 8);
        slt.offsetMax = new Vector2(-14, -6);
        PushStatus("Ready.");

        // Head-locked tutorial strip for VR — bottom of the view, wrapped.
        var tvGo = new GameObject("TutVr");
        tutVr = tvGo.AddComponent<TextMesh>();
        tvGo.transform.SetParent(cam.transform, false);
        tvGo.transform.localPosition = new Vector3(0, -0.34f, 1.1f);
        tutVr.characterSize = 0.0105f;
        tutVr.fontSize = 48;
        tutVr.anchor = TextAnchor.UpperCenter;
        tutVr.alignment = TextAlignment.Center;
        tutVr.color = new Color(0.7f, 1f, 0.75f);
        tvGo.SetActive(false);

        // Always-visible current prompt (VR only) — a small line at the top
        // of the view, so you always know what A will generate.
        var phGo = new GameObject("PromptHud");
        promptHud = phGo.AddComponent<TextMesh>();
        phGo.transform.SetParent(cam.transform, false);
        phGo.transform.localPosition = new Vector3(0, 0.30f, 1.15f);
        promptHud.characterSize = 0.009f;
        promptHud.fontSize = 48;
        promptHud.anchor = TextAnchor.UpperCenter;
        promptHud.alignment = TextAlignment.Center;
        promptHud.color = new Color(1f, 0.85f, 0.47f);
        phGo.SetActive(false);

        // Live input readout — shows which controller buttons actually arrive
        // (diagnoses missing OpenXR interaction profiles). F2 hides it.
        var vdGo = new GameObject("VrDebug");
        vrDebug = vdGo.AddComponent<TextMesh>();
        vdGo.transform.SetParent(cam.transform, false);
        vdGo.transform.localPosition = new Vector3(-0.4f, 0.34f, 1.1f);
        vrDebug.characterSize = 0.008f;
        vrDebug.fontSize = 48;
        vrDebug.anchor = TextAnchor.UpperLeft;
        vrDebug.color = new Color(1f, 0.7f, 0.7f);
        vdGo.SetActive(false);

        RefreshPromptMenu();
    }

    // The rectangle performers may never leave: the explicit roomBounds
    // collider if given, else the extent of everything registered + margin.
    Rect ComputeRoomRect()
    {
        if (roomBounds != null)
        {
            var rb = roomBounds.bounds;
            return Rect.MinMaxRect(rb.min.x, rb.min.z, rb.max.x, rb.max.z);
        }
        bool any = false;
        Bounds u = default;
        foreach (var b in ObstacleBounds())
        {
            if (!any) { u = b; any = true; }
            else u.Encapsulate(b);
        }
        if (!any) return Rect.MinMaxRect(-6, -6, 6, 6);
        return Rect.MinMaxRect(u.min.x - 1.2f, u.min.z - 1.2f,
                               u.max.x + 1.2f, u.max.z + 1.2f);
    }

    // Green outline on every registered surface top.
    void DrawSurfaceFrames()
    {
        var root = new GameObject("SurfaceFrames").transform;
        var c = new Color(0.35f, 0.9f, 0.45f);
        foreach (var s in surfaces)
        {
            if (s == null || !TryBounds(s, out var b)) continue;
            float y = b.max.y + 0.004f;
            Vector3 p1 = new Vector3(b.min.x, y, b.min.z), p2 = new Vector3(b.max.x, y, b.min.z);
            Vector3 p3 = new Vector3(b.max.x, y, b.max.z), p4 = new Vector3(b.min.x, y, b.max.z);
            Edge(root, p1, p2, c); Edge(root, p2, p3, c);
            Edge(root, p3, p4, c); Edge(root, p4, p1, c);
        }
    }

    void Edge(Transform parent, Vector3 a, Vector3 b, Color c)
    {
        var e = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(e.GetComponent<Collider>());
        e.transform.SetParent(parent);
        e.transform.position = (a + b) * 0.5f;
        Vector3 d = b - a;
        if (d.sqrMagnitude > 1e-6f) e.transform.rotation = Quaternion.LookRotation(d.normalized);
        e.transform.localScale = new Vector3(0.015f, 0.006f, d.magnitude + 0.015f);
        Tint(e, c);
    }

    // Give collider-less furniture MeshColliders at startup.
    static void EnsureColliders(GameObject go)
    {
        if (go.GetComponentsInChildren<Collider>(true).Length > 0) return;
        int added = 0;
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null) { mf.gameObject.AddComponent<MeshCollider>(); added++; }
        if (added > 0)
            Debug.Log($"[KimodoVR] '{go.name}' had no colliders — added {added} MeshColliders.");
    }

    // Combined collider bounds of an object (colliders may sit on children).
    static bool TryBounds(GameObject go, out Bounds b)
    {
        var cols = go.GetComponentsInChildren<Collider>();
        b = default;
        if (cols.Length == 0) return false;
        b = cols[0].bounds;
        foreach (var c in cols) b.Encapsulate(c.bounds);
        return true;
    }

    // Nearest REGISTERED surface along the ray — other colliders (cabinet
    // walls, doors) don't block, so areas inside furniture stay clickable.
    bool TrySurfaceHit(Ray ray, out RaycastHit best)
    {
        best = default;
        float bd = float.MaxValue;
        foreach (var h in Physics.RaycastAll(ray, 100f))
            if (h.distance < bd && IsSurface(h.collider.gameObject)) { best = h; bd = h.distance; }
        return bd < float.MaxValue;
    }

    bool IsSurface(GameObject go) => SurfaceRootOf(go) != null;

    // The registered surface entry this collider belongs to (or null).
    GameObject SurfaceRootOf(GameObject go)
    {
        foreach (var s in surfaces)
            if (s != null && (go == s || go.transform.IsChildOf(s.transform))) return s;
        return null;
    }

    // Walk a point along `dir` (XZ) until it leaves the bounds' footprint
    // plus margin — used to keep standing spots out of furniture.
    static Vector3 PushOutXZ(Vector3 p, Bounds b, Vector3 dir, float margin)
    {
        bool Inside(Vector3 q) =>
            q.x > b.min.x - margin && q.x < b.max.x + margin &&
            q.z > b.min.z - margin && q.z < b.max.z + margin;
        if (!Inside(p)) return p;
        Vector3 d = new Vector3(dir.x, 0, dir.z);
        d = d.sqrMagnitude > 0.01f ? d.normalized : Vector3.back;
        for (int i = 0; i < 80 && Inside(p); i++) p += d * 0.05f;
        return p;
    }

    // Sliding window: only a few entries visible at a time, centered on the
    // selection; ▲/▼ counters show how many are hidden either side.
    void RefreshPromptMenu()
    {
        if (promptMenu != null) promptMenu.text = PromptListText(42, null, -1);
        if (boardMenu != null) boardMenu.text = PromptListText(22, vrMenuRows, vrMenuHover);
        if (promptHud != null) promptHud.text = "▶ " + Trunc(prompt, 54);
    }

    // rowMap records what each visible line is: >=0 prompt index, -1 inert,
    // -2 scroll up, -3 scroll down. hoverRow gets a green pointing highlight.
    string PromptListText(int w, List<int> rowMap, int hoverRow)
    {
        const int WIN = 3;
        rowMap?.Clear();
        var sb = new StringBuilder();
        int line = 0;
        void Open(int idx) { rowMap?.Add(idx); if (line == hoverRow) sb.Append("<color=#a8ffc0>» "); }
        void Close() { if (line == hoverRow) sb.Append("</color>"); sb.Append("\n"); line++; }

        Open(-1); sb.Append("<b>PROMPTS</b>"); Close();
        int n = cachedPrompts.Length;
        if (n == 0) { Open(-1); sb.Append("  (loading…)"); Close(); }
        else
        {
            int cur = Mathf.Max(0, System.Array.IndexOf(cachedPrompts, prompt));
            int first = Mathf.Clamp(cur - 1, 0, Mathf.Max(0, n - WIN));
            if (first > 0) { Open(-2); sb.Append("   ▲ ").Append(first).Append(" more"); Close(); }
            for (int i = first; i < Mathf.Min(n, first + WIN); i++)
            {
                bool c = cachedPrompts[i] == prompt;
                Open(i);
                sb.Append(c ? "<color=#ffd479>> " : "   ")
                  .Append(Trunc(cachedPrompts[i], w)).Append(c ? "</color>" : "");
                Close();
            }
            int rest = n - Mathf.Min(n, first + WIN);
            if (rest > 0) { Open(-3); sb.Append("   ▼ ").Append(rest).Append(" more"); Close(); }
        }
        if (System.Array.IndexOf(cachedPrompts, prompt) < 0)
        {
            Open(-1);
            sb.Append("<color=#ffd479>> ").Append(Trunc(prompt, w)).Append("  (custom)</color>");
            Close();
        }
        return sb.ToString();
    }

    void DriveCameraFromHmd(InputDevice head)
    {
        if (!head.isValid) return;
        if (head.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 p))
            cam.transform.position = p;
        if (head.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion q))
            cam.transform.rotation = q;
    }

    // If the controller hovers near the top of the table or shelf, pull the
    // sample down onto that surface — pinning exactly by hand is hard in VR.
    Vector3 SnapToSurface(Vector3 p)
    {
        foreach (var s in surfaces)
        {
            if (s == null || !TryBounds(s, out var b)) continue;
            if (p.x > b.min.x - 0.08f && p.x < b.max.x + 0.08f &&
                p.z > b.min.z - 0.08f && p.z < b.max.z + 0.08f &&
                p.y > b.max.y - 0.06f && p.y < b.max.y + 0.18f)
                return new Vector3(p.x, b.max.y + 0.01f, p.z);
        }
        return p;
    }

    void Mark(Vector3 at, Color c, float r)
    {
        var m = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(m.GetComponent<Collider>());
        m.transform.SetParent(markers);
        m.transform.position = at;
        m.transform.localScale = Vector3.one * r * 2;
        Tint(m, c);
    }

    static void Tint(GameObject go, Color c)
    {
        var r = go.GetComponent<Renderer>();
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        r.material = new Material(shader) { color = c };
    }

    bool stripLive;   // the VR strip stays hidden until something HAPPENS

    void SetStatus(string s)
    {
        stripLive = true;
        PushStatus(s);
        Debug.Log("[KimodoVR] " + s.Replace("\n", " | "));
    }

    // Digits stripped: progress-style messages update in place.
    static string Digitless(string x)
    {
        var sb = new StringBuilder(x.Length);
        foreach (char c in x) if (!char.IsDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    void PushStatus(string s)
    {
        s = s.Replace("\n", "  ");
        if (statusHist.Count > 0 && Digitless(statusHist[0]) == Digitless(s))
            statusHist[0] = s;
        else
            statusHist.Insert(0, s);
        while (statusHist.Count > 4) statusHist.RemoveAt(4);

        if (statusLog != null)
        {
            var sb = new StringBuilder("<color=#ffd479><b>STATUS</b></color>\n");
            for (int i = 0; i < statusHist.Count; i++)
                sb.Append(i == 0 ? "<color=#ffffff>› " : "<color=#788296>· ")
                  .Append(Trunc(statusHist[i], 72)).Append("</color>\n");
            statusLog.text = sb.ToString();
        }
        // VR strip mirrors the latest events (typing/tutorial take priority).
        if (tutVr != null && !typing && tutStep < 0 && stripLive)
            tutVr.text = Wrap("› " + statusHist[0], 52) +
                         (statusHist.Count > 1 ? "\n" + Wrap("· " + statusHist[1], 52) : "");
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ------------------------------------------------------------- DTO types
    [Serializable] class FlatMotion
    {
        public float fps;
        public int frame_count, joint_count, root_index;
        public float[] frames_flat;
        public float[] quats_flat;        // T*J*4 wxyz local rotations (Kimodo coords)
        public float[] root_flat;         // T*3 root positions (Kimodo coords)
        public float[] bone_offsets_flat; // J*3 rest offsets from parent
        public int[] parents;
        public string[] joint_names;
        public float gen_seconds;
    }
    [Serializable] class Progress { public string phase; public float pct; public string detail; }
    [Serializable] class PromptList { public string[] prompts; }
}

// Drag bar for the runtime-built HUD panel: dragging it moves the panel.
class UiDragHandle : MonoBehaviour, IDragHandler
{
    public RectTransform target;

    public void OnDrag(PointerEventData e)
    {
        if (target == null) return;
        float s = target.lossyScale.x;   // canvas scale factor
        target.anchoredPosition += e.delta / (s > 0.001f ? s : 1f);
    }
}

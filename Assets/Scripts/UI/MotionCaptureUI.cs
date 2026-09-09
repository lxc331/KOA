using System;
using UnityEngine;

/// <summary>
/// 动捕控制与 06-09 遥测界面。白色低透明度面板 + 绿色强调色。
/// </summary>
[RequireComponent(typeof(MotionCaptureController))]
public class MotionCaptureUI : MonoBehaviour
{
    public event Action<string, int> OnConnectRequested;
    public event Action OnDisconnectRequested;
    public event Action OnRefreshPortsRequested;
    public event Action OnBeginDrivingRequested;
    public event Action OnResetRequested;
    public event Action<bool> OnLimitsToggled;
    public event Action<bool> OnTwistSwingToggled;
    public event Action<bool, float> OnSmoothingChanged;
    public event Action<bool> OnRequireAllDevicesChanged;
    public event Action<int> OnMinStableDevicesChanged;
    public event Action<int> OnPortSelected;
    public event Action<string> OnPortManualInput;

    [SerializeField] private MotionCaptureController controller;

    private const int CtrlWindowId = 0xC0DE120;
    private const int TeleWindowId = 0xC0DE123;
    private const int StartButtonSize = 160;
    private const float PortItemHeight = 24f;

    private readonly Color darkGreen = new Color(0.08f, 0.20f, 0.16f, 1f);
    private readonly Color green = new Color(0.12f, 0.48f, 0.31f, 1f);
    private readonly Color paleGreen = new Color(0.90f, 0.96f, 0.93f, 0.98f);
    private readonly Color panelWhite = new Color(0.99f, 1f, 0.995f, 0.95f);

    private Texture2D panelTexture, greenTexture, paleTexture, whiteTexture;
    private Texture2D waitingCircle, readyCircle;
    private GUIStyle windowStyle, titleStyle, labelStyle, smallStyle;
    private GUIStyle buttonStyle, tableHeaderStyle, tableCellStyle, valueStyle;

    private string connectLabel = "连接";
    private string baudText = "115200";
    private bool hasStarted;
    private bool isCalibratingUI;
    private float calibratedTimestamp = -1f;
    private bool limitsEnabledUI;
    private bool smoothingEnabledUI = true;
    private bool twistSwingEnabledUI;
    private bool requireAllDevicesUI;
    private int minStableDevicesUI = 1;
    private bool formalTrainingMode;
    private bool formalConnectionPanelVisible = true;

    private bool portDropdownOpen;
    private Vector2 portDropdownScroll;
    private Rect controlWindowRect = new Rect(20f, 20f, 320f, 450f);
    private Rect telemetryWindowRect;

    /// <summary>
    /// 正式训练模式只保留必要的串口连接窗口，四元数遥测表不进入患者 UI。
    /// </summary>
    public void SetFormalTrainingMode(bool enabled)
    {
        formalTrainingMode = enabled;
        if (enabled && controller != null && controller.State != null)
            formalConnectionPanelVisible = !controller.State.IsDriving;
    }

    public void SetFormalConnectionPanelVisible(bool visible)
    {
        formalConnectionPanelVisible = visible;
    }

    public bool IsFormalConnectionPanelVisible => formalConnectionPanelVisible;

    public bool CanBeginDrivingFromFormalUI
    {
        get
        {
            MotionCaptureState state = controller != null ? controller.State : null;
            return state != null && state.IsConnected && state.HasAnyData &&
                   state.IsCalibrated && state.IsStable && !state.IsDriving;
        }
    }

    public bool TryBeginDrivingFromFormalUI()
    {
        if (controller != null && controller.State != null &&
            controller.State.IsDriving)
            return true;
        if (!CanBeginDrivingFromFormalUI) return false;
        hasStarted = true;
        isCalibratingUI = false;
        OnBeginDrivingRequested?.Invoke();
        return controller != null && controller.State != null &&
               controller.State.IsDriving;
    }

    private void Awake()
    {
        if (controller == null) controller = GetComponent<MotionCaptureController>();

        panelTexture = Solid(panelWhite);
        greenTexture = Solid(green);
        paleTexture = Solid(paleGreen);
        whiteTexture = Solid(new Color(1f, 1f, 1f, 0.99f));
        waitingCircle = MakeCircleTexture(StartButtonSize,
            new Color(0.55f, 0.68f, 0.60f, 0.82f),
            new Color(0.82f, 0.91f, 0.86f, 1f));
        readyCircle = MakeCircleTexture(StartButtonSize,
            new Color(0.12f, 0.48f, 0.31f, 0.92f),
            new Color(0.55f, 0.80f, 0.66f, 1f));
    }

    private void Start()
    {
        if (controller != null && controller.State != null)
            controller.State.OnChanged += OnStateChanged;

        float w = 620f;
        telemetryWindowRect = new Rect(
            Mathf.Max(20f, Screen.width - w - 20f), 20f, w, 210f);
    }

    private void OnDestroy()
    {
        if (controller != null && controller.State != null)
            controller.State.OnChanged -= OnStateChanged;
        DestroyTexture(panelTexture);
        DestroyTexture(greenTexture);
        DestroyTexture(paleTexture);
        DestroyTexture(whiteTexture);
        DestroyTexture(waitingCircle);
        DestroyTexture(readyCircle);
    }

    private void OnStateChanged(MotionCaptureState state)
    {
        connectLabel = state.IsConnected ? "断开" : "连接";
        isCalibratingUI = state.IsConnected && !state.IsCalibrated;
        if (state.IsConnected && state.IsCalibrated && calibratedTimestamp < 0f)
            calibratedTimestamp = Time.time;
        if (!state.IsConnected) calibratedTimestamp = -1f;
    }

    private void OnGUI()
    {
        if (controller == null || controller.State == null || controller.Serial == null)
            return;
        EnsureStyles();

        if (formalTrainingMode)
        {
            if (formalConnectionPanelVisible)
            {
                controlWindowRect = GUI.Window(
                    CtrlWindowId, controlWindowRect, DrawControlWindow, "", windowStyle);
                DrawCenterStartButton();
            }
            return;
        }

        controlWindowRect = GUI.Window(CtrlWindowId, controlWindowRect, DrawControlWindow, "", windowStyle);
        telemetryWindowRect = GUI.Window(TeleWindowId, telemetryWindowRect, DrawTelemetryWindow, "", windowStyle);
        DrawCenterStartButton();
    }

    private void DrawControlWindow(int id)
    {
        float w = controlWindowRect.width;
        var serial = controller.Serial;
        var state = controller.State;
        DrawHeader(w, "动捕控制");

        GUI.Label(new Rect(18, 42, 54, 24), "串口", labelStyle);
        string[] ports = serial.AvailablePorts;
        int selected = serial.SelectedPortIndex;
        string portLabel = ports != null && ports.Length > 0 && selected >= 0 && selected < ports.Length
            ? ports[selected] : "选择端口";
        Rect portRect = new Rect(76, 42, w - 94f, 26f);
        if (GUI.Button(portRect, portLabel, buttonStyle)) portDropdownOpen = !portDropdownOpen;
        DrawPortDropdown(portRect, ports);

        if (ports == null || ports.Length == 0)
        {
            string manual = GUI.TextField(new Rect(76, 72, w - 94f, 24f), serial.CurrentPort ?? "");
            if (manual != serial.CurrentPort) OnPortManualInput?.Invoke(manual);
        }

        GUI.enabled = !isCalibratingUI;
        if (GUI.Button(new Rect(18, 105, 92, 34), connectLabel, buttonStyle))
        {
            if (connectLabel == "连接")
            {
                OnConnectRequested?.Invoke(serial.CurrentPort, ParseBaud(baudText, 115200));
                isCalibratingUI = true;
                hasStarted = false;
            }
            else
            {
                OnDisconnectRequested?.Invoke();
                hasStarted = false;
                isCalibratingUI = false;
            }
        }
        GUI.enabled = true;

        if (GUI.Button(new Rect(120, 105, 92, 34), "刷新端口", buttonStyle))
            OnRefreshPortsRequested?.Invoke();
        if (GUI.Button(new Rect(222, 105, w - 240f, 34), "重置", buttonStyle))
        {
            hasStarted = false;
            calibratedTimestamp = -1f;
            OnResetRequested?.Invoke();
        }

        GUI.Label(new Rect(18, 150, 56, 24), "波特率", labelStyle);
        baudText = GUI.TextField(new Rect(82, 150, 120, 24), baudText, 10);
        GUI.Label(new Rect(18, 180, w - 36f, 24), $"当前端口：{serial.CurrentPort}", smallStyle);

        bool limits = DrawToggle(new Rect(18, 215, w - 36f, 32f), limitsEnabledUI, "角度限制");
        if (limits != limitsEnabledUI)
        {
            limitsEnabledUI = limits;
            OnLimitsToggled?.Invoke(limitsEnabledUI);
        }

        GUI.enabled = limitsEnabledUI;
        string mode = twistSwingEnabledUI ? "限幅方式：Twist + Swing" : "限幅方式：Euler";
        if (GUI.Button(new Rect(18, 253, w - 36f, 30f), mode, buttonStyle))
        {
            twistSwingEnabledUI = !twistSwingEnabledUI;
            OnTwistSwingToggled?.Invoke(twistSwingEnabledUI);
        }
        GUI.enabled = true;

        bool smooth = DrawToggle(new Rect(18, 291, w - 36f, 32f), smoothingEnabledUI, "动作平滑 Slerp");
        if (smooth != smoothingEnabledUI)
        {
            smoothingEnabledUI = smooth;
            OnSmoothingChanged?.Invoke(smoothingEnabledUI, controller.Config.smoothSpeed);
        }

        bool requireAll = DrawToggle(new Rect(18, 329, w - 36f, 32f), requireAllDevicesUI, "要求全部设备稳定");
        if (requireAll != requireAllDevicesUI)
        {
            requireAllDevicesUI = requireAll;
            OnRequireAllDevicesChanged?.Invoke(requireAllDevicesUI);
        }

        GUI.Label(new Rect(18, 370, 150, 24), $"最少稳定设备：{minStableDevicesUI}", labelStyle);
        string minText = GUI.TextField(new Rect(w - 72f, 370, 54f, 24), minStableDevicesUI.ToString(), 2);
        if (int.TryParse(minText, out int parsed) && parsed != minStableDevicesUI)
        {
            minStableDevicesUI = Mathf.Clamp(parsed, 1, controller.Config.deviceCount);
            OnMinStableDevicesChanged?.Invoke(minStableDevicesUI);
        }

        GUI.Label(new Rect(18, 405, w - 36f, 26),
            state.IsStable ? "状态：已稳定" : "状态：等待稳定", valueStyle);
        GUI.DragWindow(new Rect(0, 0, w, 34f));
    }

    private void DrawTelemetryWindow(int id)
    {
        float w = telemetryWindowRect.width;
        DrawHeader(w, "下肢传感器 06–09");

        string[] headers = { "设备", "q0", "q1", "q2", "q3", "Yaw", "Pitch", "Roll" };
        float first = 72f;
        float other = Mathf.Max(56f, (w - first - 20f) / 7f);
        float x = 10f;
        for (int c = 0; c < headers.Length; c++)
        {
            float cw = c == 0 ? first : other;
            GUI.Label(new Rect(x, 40f, cw, 24f), headers[c], tableHeaderStyle);
            x += cw;
        }

        Quaternion[] q = controller.TransformedQuaternions;
        for (int row = 0; row < 4; row++)
        {
            int i = row + 5;
            float y = 68f + row * 31f;
            GUI.DrawTexture(new Rect(8f, y, w - 16f, 28f), row % 2 == 0 ? paleTexture : whiteTexture);
            Quaternion v = q != null && i < q.Length ? q[i] : Quaternion.identity;
            Vector3 e = v.eulerAngles;
            string[] values =
            {
                $"{i + 1:00}", v.x.ToString("F3"), v.y.ToString("F3"), v.z.ToString("F3"), v.w.ToString("F3"),
                e.z.ToString("F1"), e.y.ToString("F1"), e.x.ToString("F1")
            };
            x = 10f;
            for (int c = 0; c < values.Length; c++)
            {
                float cw = c == 0 ? first : other;
                GUI.Label(new Rect(x, y + 2f, cw, 24f), values[c], tableCellStyle);
                x += cw;
            }
        }
        GUI.DragWindow(new Rect(0, 0, w, 34f));
    }

    private void DrawCenterStartButton()
    {
        var state = controller.State;
        if (hasStarted || state.IsDriving) return;

        string label = !state.IsConnected ? "未连接" :
            !state.HasAnyData ? "等待数据" :
            isCalibratingUI && !state.IsCalibrated ? "校准中" :
            state.IsStable ? "点击开始" : "等待稳定";

        bool canStart = state.IsConnected && state.HasAnyData &&
                        !isCalibratingUI && state.IsCalibrated && state.IsStable;
        Rect rect = new Rect((Screen.width - StartButtonSize) / 2f,
            (Screen.height - StartButtonSize) / 2f, StartButtonSize, StartButtonSize);
        GUI.DrawTexture(rect, canStart ? readyCircle : waitingCircle, ScaleMode.StretchToFill, true);

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 28,
            fontStyle = FontStyle.Bold
        };
        style.normal.textColor = Color.white;
        GUI.Label(rect, label, style);

        if (canStart && GUI.Button(rect, GUIContent.none, GUIStyle.none))
        {
            hasStarted = true;
            isCalibratingUI = false;
            OnBeginDrivingRequested?.Invoke();
        }

        if (state.IsConnected && state.HasAnyData && state.IsCalibrated &&
            !hasStarted && !state.IsDriving && calibratedTimestamp > 0f &&
            Time.time - calibratedTimestamp >= 2f)
        {
            hasStarted = true;
            isCalibratingUI = false;
            OnBeginDrivingRequested?.Invoke();
        }
    }

    private bool DrawToggle(Rect rect, bool value, string label)
    {
        GUI.DrawTexture(rect, value ? paleTexture : whiteTexture);
        Rect mark = new Rect(rect.x + 8f, rect.y + 7f, 18f, 18f);
        GUI.DrawTexture(mark, value ? greenTexture : paleTexture);
        GUI.Label(new Rect(rect.x + 34f, rect.y + 4f, rect.width - 40f, 24f), label, labelStyle);
        if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) value = !value;
        return value;
    }

    private void DrawPortDropdown(Rect buttonRect, string[] ports)
    {
        if (!portDropdownOpen) return;
        int count = ports != null ? ports.Length : 0;
        float visible = Mathf.Min(6, Mathf.Max(1, count));
        Rect drop = new Rect(buttonRect.x, buttonRect.y + buttonRect.height + 2f,
            buttonRect.width, visible * PortItemHeight + 6f);
        GUI.DrawTexture(drop, panelTexture);
        Rect view = new Rect(0, 0, drop.width - 18f, Mathf.Max(1, count) * PortItemHeight);
        portDropdownScroll = GUI.BeginScrollView(drop, portDropdownScroll, view);
        for (int i = 0; i < count; i++)
        {
            if (GUI.Button(new Rect(2f, i * PortItemHeight, drop.width - 22f, PortItemHeight), ports[i], buttonStyle))
            {
                OnPortSelected?.Invoke(i);
                portDropdownOpen = false;
            }
        }
        GUI.EndScrollView();
    }

    private void DrawHeader(float width, string title)
    {
        GUI.DrawTexture(new Rect(0f, 0f, width, 34f), greenTexture);
        GUI.Label(new Rect(14f, 2f, width - 28f, 30f), title, titleStyle);
    }

    private void EnsureStyles()
    {
        if (windowStyle != null) return;
        windowStyle = new GUIStyle(GUI.skin.window);
        windowStyle.normal.background = panelTexture;
        windowStyle.border = new RectOffset(8, 8, 8, 8);

        titleStyle = Label(18, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
        labelStyle = Label(14, FontStyle.Normal, darkGreen, TextAnchor.MiddleLeft);
        smallStyle = Label(13, FontStyle.Normal, darkGreen, TextAnchor.MiddleLeft);
        tableHeaderStyle = Label(14, FontStyle.Bold, darkGreen, TextAnchor.MiddleCenter);
        tableCellStyle = Label(12, FontStyle.Normal, darkGreen, TextAnchor.MiddleCenter);
        valueStyle = Label(15, FontStyle.Bold, green, TextAnchor.MiddleLeft);

        buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        buttonStyle.normal.background = paleTexture;
        buttonStyle.normal.textColor = darkGreen;
        buttonStyle.hover.background = whiteTexture;
        buttonStyle.hover.textColor = green;
        buttonStyle.active.background = greenTexture;
        buttonStyle.active.textColor = Color.white;
    }

    private static GUIStyle Label(int size, FontStyle weight, Color color, TextAnchor align)
    {
        var s = new GUIStyle(GUI.skin.label)
        {
            fontSize = size,
            fontStyle = weight,
            alignment = align
        };
        s.normal.textColor = color;
        return s;
    }

    private static int ParseBaud(string text, int fallback) =>
        int.TryParse(text, out int b) && b > 0 ? b : fallback;

    private static Texture2D Solid(Color color)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        t.SetPixel(0, 0, color);
        t.Apply();
        return t;
    }

    private static Texture2D MakeCircleTexture(int size, Color fill, Color rim)
    {
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float r = size * 0.5f - 1f;
        float cx = r + 1f, cy = r + 1f;
        float rimW = Mathf.Max(2f, size * 0.06f);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - cx, dy = y - cy;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            if (d <= r)
            {
                Color c = d > r - rimW
                    ? Color.Lerp(rim, fill, Mathf.InverseLerp(r, r - rimW, d))
                    : fill;
                t.SetPixel(x, y, c);
            }
            else t.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
        }
        t.Apply(false, true);
        return t;
    }

    private static void DestroyTexture(Texture2D t)
    {
        if (t != null) Destroy(t);
    }
}

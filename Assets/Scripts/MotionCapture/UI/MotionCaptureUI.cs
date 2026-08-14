
using System;
using System.IO;
using UnityEngine;

/// <summary>
/// IMGUI 界面层。
/// 
/// 纯粹负责绘制 UI 和采集用户输入，不直接修改任何业务状态。
/// 所有用户操作通过事件（Action）向外广播，由 MotionCaptureController 接收并处理。
/// 
/// 界面组成：
///   1. Control Interface 窗口 — 端口选择、连接/断开、参数调整、导出设置
///   2. Sensor Telemetry 窗口 — 9 个传感器的实时数据及在线/稳定/标定状态
///   3. 中心状态按钮 — 自动标定倒计时与采样状态；按钮仅作为手动重试入口
/// 
/// 挂载方式：
///   通过 [RequireComponent] 自动与 MotionCaptureController 共存于同一 GameObject。
/// </summary>
[RequireComponent(typeof(MotionCaptureController))]
public class MotionCaptureUI : MonoBehaviour
{
    // ═══════════════════════════════════════════════════════════════
    //  UI → 业务事件
    //  UI 层不直接修改业务状态，而是通过这些事件通知 Controller。
    //  Controller 在 BindUIEvents() 中订阅它们。
    // ═══════════════════════════════════════════════════════════════

    /// <summary>用户点击"连接"按钮，参数：(端口名, 波特率)</summary>
    public event Action<string, int> OnConnectRequested;

    /// <summary>用户点击"断开"按钮</summary>
    public event Action OnDisconnectRequested;

    /// <summary>用户点击"刷新端口"按钮</summary>
    public event Action OnRefreshPortsRequested;

    /// <summary>用户点击中心"开始"按钮或自动倒计时触发</summary>
    public event Action OnBeginDrivingRequested;

    /// <summary>用户点击"重置"按钮</summary>
    public event Action OnResetRequested;

    /// <summary>用户切换"角度限制"开关，参数：是否启用</summary>
    public event Action<bool> OnLimitsToggled;

    /// <summary>用户切换限幅模式（Euler ↔ Twist+Swing）</summary>
    public event Action<bool> OnTwistSwingToggled;

    /// <summary>用户切换"平滑(Slerp)"开关，参数：(是否启用, 速度)</summary>
    public event Action<bool, float> OnSmoothingChanged;

    /// <summary>用户切换"要求所有设备稳定"开关</summary>
    public event Action<bool> OnRequireAllDevicesChanged;

    /// <summary>用户修改"最少稳定设备数"</summary>
    public event Action<int> OnMinStableDevicesChanged;

    /// <summary>用户切换"保存到文件"开关</summary>
    public event Action<bool> OnSaveEnabledChanged;

    /// <summary>用户点击“开始记录”，数据先缓存到内存。</summary>
    public event Action OnStartRecordingRequested;

    /// <summary>用户点击“停止记录”，一次性生成 9-Sheet Excel。</summary>
    public event Action OnStopRecordingRequested;

    /// <summary>用户在问题发生时手动写入带九路快照的诊断标记。</summary>
    public event Action OnDiagnosticMarkerRequested;

    /// <summary>用户在端口下拉列表中选中某一项，参数：索引</summary>
    public event Action<int> OnPortSelected;

    /// <summary>用户手动输入端口名（当无可用端口时）</summary>
    public event Action<string> OnPortManualInput;

    // ═══════════════════════════════════════════════════════════════
    //  Inspector 引用
    // ═══════════════════════════════════════════════════════════════

    [Header("外部引用")]
    [Tooltip("自动获取同 GameObject 上的 MotionCaptureController")]
    [SerializeField] private MotionCaptureController controller;

    // ═══════════════════════════════════════════════════════════════
    //  内部 UI 状态
    //  这些变量仅控制 UI 外观和交互，不影响业务逻辑。
    // ═══════════════════════════════════════════════════════════════

    // 中心开始按钮的直径（像素）
    private const int START_BUTTON_SIZE = 160;

    // 两种按钮纹理：等待中（灰色）和就绪（绿色）
    private Texture2D btnCircleWaiting, btnCircleReady;

    // 连接按钮的文本标签（"turn on" / "turn off"）
    private string connectLabel = "turn on";

    // 是否正在倒计时/稳定采样中。业务状态以 Controller 为准。
    private bool isCalibratingUI;

    // 波特率输入框的文本内容
    private string baudText = "115200";

    // 各个开关的 UI 状态（UI 层的本地副本，变化时通过事件通知业务层）
    private bool limitsEnabledUI;           // 角度限制开关
    private bool smoothingEnabledUI = true; // 平滑开关（默认开启）
    private bool twistSwingEnabledUI;       // Twist+Swing 模式开关
    private bool requireAllDevicesUI;       // 要求所有设备稳定
    private int minStableDevicesUI = 1;     // 最少稳定设备数
    private bool saveEnabledUI = true;      // 保存到文件开关
    // ── 端口下拉选择 ──
    private bool portDropdownOpen;          // 下拉菜单是否展开
    private Vector2 portDropdownScroll;     // 下拉列表的滚动位置
    private const float PORT_ITEM_HEIGHT = 20f;  // 每个端口项的高度

    // ── 窗口布局 ──
    private Rect controlWindowRect = new Rect(20f, 20f, 320f, 660f);   // 控制面板位置
    private Rect telemetryWindowRect;        // 遥测窗口位置（在 Start 中初始化）
    private Rect kneeWindowRect = new Rect(0f, 0f, 500f, 430f);
    private bool telemetryRectInitialized;   // 遥测窗口是否已初始化过位置

    [Header("Sensor Telemetry 固定位置")]
    [Tooltip("开启后，Sensor Telemetry 会固定在 Game 视图左下角，不能拖动/缩放。")]
    [SerializeField] private bool lockTelemetryToBottomLeft = true;

    // 窗口 ID（IMGUI 要求每个窗口有唯一 ID）
    private const int CTRL_WINDOW_ID = 0xC0DE120;
    private const int TELE_WINDOW_ID = 0xC0DE123;
    private const int KNEE_WINDOW_ID = 0xC0DE124;

    // 窗口最小尺寸限制
    private const float MIN_CTRL_W = 320f, MIN_CTRL_H = 660f;
    private const float MIN_TELE_W = 1120f, MIN_TELE_H = 340f;
    private const float TELE_DEFAULT_W = 1120f, TELE_DEFAULT_H = 340f;
    private const float TELE_MARGIN = 20f;     // 遥测窗口距屏幕边缘的边距
    private const float KNEE_WINDOW_W = 500f;
    private const float KNEE_WINDOW_H = 430f;
    private const float KNEE_MARGIN = 24f;
    private const float RESIZE_HANDLE = 14f;   // 缩放手柄大小（像素）

    // 窗口缩放状态
    private bool isResizingControl, isResizingTelemetry;
    private Vector2 resizeStartMouse;       // 缩放开始时的鼠标位置
    private Rect resizeStartRect;           // 缩放开始时的窗口矩形

    // 遥测表格的样式（延迟初始化，因为 GUIStyle 需要在 OnGUI 中创建）
    private GUIStyle tableHeaderStyle, tableCellStyle;
    private GUIStyle statusOfflineStyle, statusWaitingStyle, statusReadyStyle;
    private GUIStyle statusSuccessStyle, statusLockedStyle, statusFailedStyle;
    private Texture2D tableHeaderBackground, tableCellBackground;

    private static readonly Color UiTextPrimary = new Color(0.96f, 0.98f, 1f, 1f);
    private static readonly Color UiTextSecondary = new Color(0.68f, 0.90f, 1f, 1f);
    private static readonly Color UiAccent = new Color(1f, 0.86f, 0.25f, 1f);
    private static readonly Color UiSuccess = new Color(0.35f, 1f, 0.48f, 1f);
    private static readonly Color UiDanger = new Color(1f, 0.38f, 0.34f, 1f);
    private static readonly Color UiMuted = new Color(0.70f, 0.74f, 0.80f, 1f);

    // ═══════════════════════════════════════════════════════════════
    //  Unity 生命周期
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Awake — 获取 Controller 引用，生成按钮纹理。
    /// </summary>
    private void Awake()
    {
        // 自动获取同 GameObject 上的 Controller
        if (controller == null)
            controller = GetComponent<MotionCaptureController>();

        // 程序化生成两种圆形按钮纹理（不依赖外部图片资源）
        btnCircleWaiting = MakeCircleTexture(START_BUTTON_SIZE,
            new Color(0.25f, 0.25f, 0.25f, 0.75f),    // 灰色填充
            new Color(0.55f, 0.55f, 0.55f, 1f));       // 浅灰色边缘
        btnCircleReady = MakeCircleTexture(START_BUTTON_SIZE,
            new Color(0.12f, 0.55f, 0.12f, 0.85f),    // 绿色填充
            new Color(0.25f, 0.85f, 0.25f, 1f));       // 亮绿色边缘

        // V5继续保留用户选定的V1深色高对比遥测表。
        tableHeaderBackground = MakeSolidTexture(new Color(0.04f, 0.08f, 0.13f, 0.96f));
        tableCellBackground = MakeSolidTexture(new Color(0.07f, 0.11f, 0.17f, 0.90f));
    }

    /// <summary>
    /// Start — 订阅状态事件，初始化遥测窗口位置。
    /// </summary>
    private void Start()
    {
        if (controller != null)
        {
            if (controller.State != null)
                controller.State.OnChanged += OnStateChanged;
            else
                Debug.LogWarning("[MotionCaptureUI] controller.State 尚未初始化，事件订阅跳过");
        }

        // 初始化遥测窗口位置：默认固定在屏幕左下角
        if (!telemetryRectInitialized)
        {
            telemetryWindowRect = GetTelemetryBottomLeftRect();
            telemetryRectInitialized = true;
        }
    }

    /// <summary>
    /// OnDestroy — 取消事件订阅，防止内存泄漏。
    /// </summary>
    private void OnDestroy()
    {
        if (controller != null && controller.State != null)
            controller.State.OnChanged -= OnStateChanged;
    }

    // ═══════════════════════════════════════════════════════════════
    //  状态事件回调
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 当业务状态变化时被调用（通过 MotionCaptureState.OnChanged 事件）。
    /// 将业务状态映射到 UI 显示状态。
    /// </summary>
    private void OnStateChanged(MotionCaptureState s)
    {
        // 根据连接状态更新按钮文本
        connectLabel = s.IsConnected ? "turn off" : "turn on";

        // 自动标定由 Controller 触发；UI 只反映当前倒计时状态。
        isCalibratingUI = controller != null && controller.IsCalibrationCountdownActive;
    }

    // ═══════════════════════════════════════════════════════════════
    //  OnGUI 入口
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Unity 的 IMGUI 绘制入口（每帧可能调用多次）。
    /// 绘制三个区域：控制面板窗口、遥测数据窗口、中心开始按钮。
    /// </summary>
    private void OnGUI()
    {
        ApplyHighContrastSkin();

        // 绘制可拖动的控制面板窗口
        controlWindowRect = GUI.Window(CTRL_WINDOW_ID, controlWindowRect,
            DrawControlWindow, "Control Interface");

        // 如果开启锁定，每次绘制前都重新计算左下角位置，避免分辨率变化或拖拽导致偏移
        if (lockTelemetryToBottomLeft)
        {
            telemetryWindowRect = GetTelemetryBottomLeftRect();
        }

        // 绘制遥测数据窗口
        telemetryWindowRect = GUI.Window(TELE_WINDOW_ID, telemetryWindowRect,
            DrawTelemetryWindow, "Sensor Telemetry");

        // V59 肘膝角面板固定在画面右下角，并同时显示两种角度定义。
        kneeWindowRect = GetKneeBottomRightRect();
        kneeWindowRect = GUI.Window(KNEE_WINDOW_ID, kneeWindowRect,
            DrawKneeAngleWindow, "Joint Angles / 肘膝关节角度");

        // 绘制屏幕中心的开始按钮
        DrawCenterStartButton();
    }

    // ═══════════════════════════════════════════════════════════════
    //  控制面板窗口
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 绘制控制面板窗口的内容。
    /// 包含：端口选择、连接/断开、波特率、角度限制、平滑、稳定性参数、导出设置。
    /// </summary>
    private void DrawControlWindow(int id)
    {
        if (controller == null || controller.Serial == null || controller.State == null) return;

        float width = controlWindowRect.width;
        var serial = controller.Serial;
        var state = controller.State;

        // ── 端口选择区域 ──
        GUI.Label(new Rect(20, 20, 40, 20), "port");
        float portBtnW = Mathf.Max(110f, width - 80f);
        Rect portBtnRect = new Rect(60, 20, portBtnW, 22);

        // 显示当前选中端口名，或"(选择端口)"提示
        string[] ports = serial.AvailablePorts;
        int selIdx = serial.SelectedPortIndex;
        string portLabel = (ports != null && ports.Length > 0 && selIdx >= 0 && selIdx < ports.Length)
            ? ports[selIdx] : "(选择端口)";

        // 点击按钮展开/收起端口下拉菜单
        if (GUI.Button(portBtnRect, portLabel))
            portDropdownOpen = !portDropdownOpen;

        // 绘制端口下拉菜单
        HandlePortDropdown(portBtnRect, ports);

        // 无可用端口时显示手动输入框
        if (ports == null || ports.Length == 0)
        {
            string newPort = GUI.TextField(new Rect(60, 46, portBtnW, 20), serial.CurrentPort ?? "");
            if (newPort != serial.CurrentPort)
                OnPortManualInput?.Invoke(newPort);
        }

        // ── 连接/断开按钮 ──
        // 校准过程中禁用按钮，防止用户中途断开
        GUI.enabled = !isCalibratingUI;
        if (GUI.Button(new Rect(30, 40, 80, 40), connectLabel))
        {
            if (connectLabel == "turn on")
            {
                // 连接：解析波特率文本，发送连接请求
                int baud = ParseBaud(baudText, 115200);
                OnConnectRequested?.Invoke(serial.CurrentPort, baud);
                isCalibratingUI = false;  // 连接后由 Controller 自动等待稳定并启动标定
            }
            else
            {
                // 断开：发送断开请求
                OnDisconnectRequested?.Invoke();
                isCalibratingUI = false;
            }
        }
        GUI.enabled = true;

        // ── 刷新端口 & 重置按钮 ──
        if (GUI.Button(new Rect(20, 90, 120, 22), "刷新端口"))
            OnRefreshPortsRequested?.Invoke();

        if (GUI.Button(new Rect(150, 90, 70, 22), "重置"))
        {
            isCalibratingUI = false;
            OnResetRequested?.Invoke();
        }

        // ── 波特率输入 ──
        GUI.Label(new Rect(20, 115, 100, 20), "baud");
        baudText = GUI.TextField(new Rect(60, 115, 110, 20), baudText, 10);

        // ── 状态信息显示 ──
        GUI.Label(new Rect(20, 140, 200, 20), "策略: auto");           // 端口选择策略
        GUI.Label(new Rect(20, 165, 200, 20), $"端口: {serial.CurrentPort}");  // 当前端口

        // ── 角度限制开关 ──
        bool newLimits = GUI.Toggle(new Rect(20, 190, 200, 24), limitsEnabledUI, "角度限制");
        if (newLimits != limitsEnabledUI)
        {
            limitsEnabledUI = newLimits;
            OnLimitsToggled?.Invoke(limitsEnabledUI);   // 通知业务层
        }

        // ── 限幅方式切换（仅在角度限制开启时可用） ──
        GUI.enabled = limitsEnabledUI;
        string modeLabel = twistSwingEnabledUI ? "Twist+Swing" : "Euler";
        GUI.Label(new Rect(20, 215, 200, 20), "限幅方式:");
        if (GUI.Button(new Rect(20, 240, 210, 24), modeLabel))
        {
            twistSwingEnabledUI = !twistSwingEnabledUI;
            OnTwistSwingToggled?.Invoke(twistSwingEnabledUI);
        }
        GUI.enabled = true;

        // ── 平滑(Slerp)开关 ──
        bool newSmooth = GUI.Toggle(new Rect(20, 270, 200, 24), smoothingEnabledUI, "平滑(Slerp)");
        if (newSmooth != smoothingEnabledUI)
        {
            smoothingEnabledUI = newSmooth;
            OnSmoothingChanged?.Invoke(smoothingEnabledUI, controller.Config.smoothSpeed);
        }

        // ── 稳定性设置 ──
        // "要求所有设备稳定" 开关
        bool newReqAll = GUI.Toggle(new Rect(20, 330, 200, 24), requireAllDevicesUI, "要求所有设备稳定");
        if (newReqAll != requireAllDevicesUI)
        {
            requireAllDevicesUI = newReqAll;
            OnRequireAllDevicesChanged?.Invoke(requireAllDevicesUI);
        }

        // "最少稳定设备数" 输入框
        GUI.Label(new Rect(20, 360, 160, 20), $"最少稳定设备: {minStableDevicesUI}");
        string minStr = GUI.TextField(new Rect(150, 360, 50, 20), minStableDevicesUI.ToString(), 2);
        if (int.TryParse(minStr, out int parsed) && parsed != minStableDevicesUI)
        {
            minStableDevicesUI = Mathf.Clamp(parsed, 1, controller.Config.deviceCount);
            OnMinStableDevicesChanged?.Invoke(minStableDevicesUI);
        }

        // ── Excel 数据记录 ──
        GUI.Label(new Rect(20, 390, width - 40, 20), "Excel 自动记录（与人物开始运动同步）");

        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && state.IsConnected && !controller.IsLogging;
        if (GUI.Button(new Rect(20, 414, 100, 30), "手动开始"))
            OnStartRecordingRequested?.Invoke();

        GUI.enabled = wasEnabled && controller.IsLogging;
        if (GUI.Button(new Rect(130, 414, 100, 30), "停止并保存"))
            OnStopRecordingRequested?.Invoke();

        GUI.enabled = wasEnabled && controller.IsAiDiagnosticLogging;
        if (GUI.Button(new Rect(240, 414, Mathf.Max(60f, width - 260f), 30), "标记异常"))
            OnDiagnosticMarkerRequested?.Invoke();
        GUI.enabled = wasEnabled;

        GUI.Label(new Rect(20, 452, width - 40, 20), "日志自动保存目录:");
        string relativeLogDirectory = string.IsNullOrEmpty(controller.CurrentTestLogRelativeDirectory)
            ? @"Logs\点击连接后自动创建日期时间文件夹"
            : controller.CurrentTestLogRelativeDirectory;
        GUI.Label(new Rect(20, 474, width - 40, 22), relativeLogDirectory);

        string logStatus = controller.IsLogging
            ? $"内存记录中: {Path.GetFileName(controller.CurrentLogPath)}"
            : "未记录";
        GUI.Label(new Rect(20, 504, width - 40, 20), logStatus);

        string aiLogStatus = controller.IsAiDiagnosticLogging
            ? $"AI诊断自动记录: {Path.GetFileName(controller.AiDiagnosticLogPath)}"
            : !string.IsNullOrEmpty(controller.AiDiagnosticLogPath)
                ? $"AI诊断已保存: {Path.GetFileName(controller.AiDiagnosticLogPath)}"
                : "AI诊断自动记录: 点击连接后开始";
        GUI.Label(new Rect(20, 528, width - 40, 20), aiLogStatus);
        GUI.Label(new Rect(20, 554, width - 40, 20), $"稳定: {(state.IsStable ? "OK" : "等待")}");

        // V77.28：直接显示协议接收状态，便于区分“串口已打开”和“真正收到有效姿态帧”。
        var parser = serial.Parser;
        if (parser != null)
        {
            GUI.Label(new Rect(20, 578, width - 40, 20),
                $"协议 len:{parser.LastPayloadLength}  XOR错:{parser.ChecksumFailCount}  " +
                $"CRC错:{parser.Crc16FailCount}  重复ID:{parser.DuplicateLogicalIdConflictCount}");
        }

        if (!string.IsNullOrEmpty(controller.CalibrationCountdownStatus))
        {
            GUIStyle calibrationHint = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                normal = { textColor = UiTextSecondary }
            };
            GUI.Label(new Rect(20, 602, width - 40, 48),
                controller.CalibrationCountdownStatus, calibrationHint);
        }

        // ── 窗口缩放手柄 & 标题栏拖拽 ──
        DrawResizeHandle(ref controlWindowRect, ref isResizingControl, MIN_CTRL_W, MIN_CTRL_H);
        GUI.DragWindow(new Rect(0, 0, controlWindowRect.width, 22f));
    }

    // ═══════════════════════════════════════════════════════════════
    //  遥测数据窗口
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 绘制遥测数据窗口：9 行实时数据，并逐路显示在线、稳定和标定结果。
    /// </summary>
    private void DrawTelemetryWindow(int id)
    {
        if (controller == null || controller.Config == null) return;

        // 确保表格样式已初始化
        EnsureTableStyles();

        int deviceCount = controller.Config.deviceCount;

        // V1表头：恢复完整四元数、欧拉角和逐路状态。
        string[] headers = { "传感器/部位", "q0", "q1", "q2", "q3", "yaw", "pitch", "roll", "通信", "运行", "稳定", "标定结果", "接收Hz", "源Hz", "帧龄ms", "到达%", "源丢", "重复", "乱序", "故障" };
        float[] widths = { 104f, 45f, 45f, 45f, 45f, 45f, 45f, 45f, 52f, 56f, 52f, 74f, 55f, 55f, 62f, 55f, 52f, 48f, 48f, 58f };
        float headerH = 20f, headerY = 24f, rowH = 24f, startX = 8f;
        float startY = headerY + headerH + 4f;

        // ── 绘制表头行 ──
        float x = startX;
        for (int i = 0; i < headers.Length; i++)
        {
            GUI.Label(new Rect(x, headerY, widths[i], headerH), headers[i], tableHeaderStyle);
            x += widths[i];
        }

        // ── 绘制数据行（每个传感器一行） ──
        var quats = controller.TransformedQuaternions;
        for (int i = 0; i < deviceCount; i++)
        {
            float y = startY + rowH * i;
            x = startX;

            // 获取该设备经坐标转换后的四元数
            Quaternion q = (quats != null && quats.Length > i) ? quats[i] : Quaternion.identity;
            Vector3 euler = q.eulerAngles;

            bool online = controller.IsSensorOnline(i);
            bool runtimeReady = controller.IsSensorRuntimeReady(i);
            bool stable = controller.IsSensorStable(i);
            double ageMs = controller.GetSensorFrameAgeMilliseconds(i);
            string ageText = double.IsInfinity(ageMs) ? "--" : Mathf.Min(9999f, (float)ageMs).ToString("F0");
            MotionCaptureController.SensorCalibrationUiState calibrationState =
                controller.GetSensorCalibrationUiState(i);

            // 逐列绘制：设备/部位 → 四元数 → 欧拉角 → 在线/稳定/标定。
            GUI.Label(new Rect(x, y, widths[0], rowH),
                $"{i + 1:00} {controller.GetSensorRoleLabel(i)}", tableCellStyle); x += widths[0];
            GUI.Label(new Rect(x, y, widths[1], rowH), q.x.ToString("F3"), tableCellStyle); x += widths[1];
            GUI.Label(new Rect(x, y, widths[2], rowH), q.y.ToString("F3"), tableCellStyle); x += widths[2];
            GUI.Label(new Rect(x, y, widths[3], rowH), q.z.ToString("F3"), tableCellStyle); x += widths[3];
            GUI.Label(new Rect(x, y, widths[4], rowH), q.w.ToString("F3"), tableCellStyle); x += widths[4];
            GUI.Label(new Rect(x, y, widths[5], rowH), euler.z.ToString("F1"), tableCellStyle); x += widths[5];
            GUI.Label(new Rect(x, y, widths[6], rowH), euler.y.ToString("F1"), tableCellStyle); x += widths[6];
            GUI.Label(new Rect(x, y, widths[7], rowH), euler.x.ToString("F1"), tableCellStyle); x += widths[7];
            GUI.Label(new Rect(x, y, widths[8], rowH), online ? "在线" : "离线",
                online ? statusReadyStyle : statusOfflineStyle); x += widths[8];
            GUI.Label(new Rect(x, y, widths[9], rowH), controller.GetSensorRuntimeReadinessLabel(i),
                runtimeReady ? statusReadyStyle : statusWaitingStyle); x += widths[9];
            GUI.Label(new Rect(x, y, widths[10], rowH), stable ? "稳定" : "等待",
                stable ? statusReadyStyle : statusWaitingStyle); x += widths[10];
            GUI.Label(new Rect(x, y, widths[11], rowH),
                GetCalibrationStateText(calibrationState), GetCalibrationStateStyle(calibrationState)); x += widths[11];
            GUI.Label(new Rect(x, y, widths[12], rowH),
                controller.GetSensorFrameRateHz(i).ToString("F1"), tableCellStyle); x += widths[12];
            float sourceHz = controller.GetSensorSourceFrameRateHz(i);
            GUI.Label(new Rect(x, y, widths[13], rowH),
                sourceHz > 0f ? sourceHz.ToString("F1") : "--", tableCellStyle); x += widths[13];
            GUI.Label(new Rect(x, y, widths[14], rowH), ageText, tableCellStyle); x += widths[14];
            float delivery = controller.GetSensorDeliveryPercent(i);
            GUI.Label(new Rect(x, y, widths[15], rowH),
                sourceHz > 0f ? delivery.ToString("F0") : "--", tableCellStyle); x += widths[15];
            GUI.Label(new Rect(x, y, widths[16], rowH),
                controller.GetSensorSourceLostFrameCount(i).ToString(), tableCellStyle); x += widths[16];
            GUI.Label(new Rect(x, y, widths[17], rowH),
                controller.GetSensorSourceDuplicateFrameCount(i).ToString(), tableCellStyle); x += widths[17];
            GUI.Label(new Rect(x, y, widths[18], rowH),
                controller.GetSensorSourceOutOfOrderFrameCount(i).ToString(), tableCellStyle); x += widths[18];
            int faultCount = controller.GetSensorRuntimeFaultCount(i);
            string faultText = controller.LastRuntimeFaultSensorIndex == i
                ? $"触发#{faultCount}"
                : faultCount > 0 ? $"#{faultCount}" : "--";
            GUI.Label(new Rect(x, y, widths[19], rowH), faultText,
                controller.LastRuntimeFaultSensorIndex == i ? statusFailedStyle : tableCellStyle);
        }

        // 保持V1表格尺寸，只增加一行紧凑链路摘要。标定结果是历史锁存，通信是当前状态。
        GUI.Label(new Rect(10f, 274f, telemetryWindowRect.width - 20f, 20f),
            $"测试={controller.SensorTestSelectionSummary}｜通信=当前帧状态｜标定=历史结果｜" +
            $"队列 {controller.GlobalQueueCount}/{controller.GlobalQueueCapacity}  队满丢弃 {controller.GlobalQueueDroppedFrameCount}  " +
            $"恢复合并 {controller.BacklogDiscardedFrameCount}",
            tableCellStyle);
        GUI.Label(new Rect(10f, 294f, telemetryWindowRect.width - 20f, 20f),
            $"源端：丢 {controller.SourceLostFrameCount}  重复 {controller.SourceDuplicateFrameCount}  " +
            $"乱序 {controller.SourceOutOfOrderFrameCount}  CRC错 {controller.Crc16FailCount}  " +
            $"ID冲突 {controller.DuplicateLogicalIdConflictCount}｜标定自适应≤4s  运行闸门={controller.RuntimeGateSummary}",
            tableCellStyle);
        GUI.Label(new Rect(10f, 314f, telemetryWindowRect.width - 20f, 20f),
            string.IsNullOrEmpty(controller.LastRuntimeFaultSummary)
                ? "上次运行故障：无"
                : $"上次运行故障：{controller.LastRuntimeFaultSummary}",
            string.IsNullOrEmpty(controller.LastRuntimeFaultSummary) ? tableCellStyle : statusFailedStyle);

        // 固定左下角时，不允许拖动和缩放；关闭 lockTelemetryToBottomLeft 后恢复可拖动/缩放
        if (!lockTelemetryToBottomLeft)
        {
            DrawResizeHandle(ref telemetryWindowRect, ref isResizingTelemetry, MIN_TELE_W, MIN_TELE_H);
            GUI.DragWindow(new Rect(0, 0, telemetryWindowRect.width, 22f));
        }
    }

    /// <summary>
    /// V8 肘膝关节角度面板。06+07、08+09同时在线时显示时间配对膝角，并同步驱动小腿。
    /// </summary>
    private void DrawKneeAngleWindow(int id)
    {
        GUIStyle heading = new GUIStyle(GUI.skin.label)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = UiTextPrimary }
        };
        GUIStyle value = new GUIStyle(GUI.skin.label)
        {
            fontSize = 24,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = UiTextSecondary }
        };
        GUIStyle section = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = UiAccent }
        };
        GUIStyle hint = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = true,
            normal = { textColor = UiMuted }
        };

        if (controller == null)
        {
            GUI.Label(new Rect(18, 38, 450, 40), "Controller 未初始化", heading);
            return;
        }

        float leftElbow = Mathf.Clamp(controller.LeftElbowFlexionAngleDeg, 0f, 180f);
        float rightElbow = Mathf.Clamp(controller.RightElbowFlexionAngleDeg, 0f, 180f);
        float leftFlex = Mathf.Clamp(controller.LeftKneeFlexionAngleDeg, 0f, 180f);
        float rightFlex = Mathf.Clamp(controller.RightKneeFlexionAngleDeg, 0f, 180f);
        float leftIncluded = Mathf.Clamp(controller.LeftKneeIncludedAngleDeg, 0f, 180f);
        float rightIncluded = Mathf.Clamp(controller.RightKneeIncludedAngleDeg, 0f, 180f);

        float labelX = 18f;
        float valueX = 370f;
        float rowH = 36f;
        float y = 30f;

        GUI.Label(new Rect(labelX, y, 250f, 28f), "肘关节 / Elbow", section);
        y += 28f;
        GUI.Label(new Rect(labelX, y, 345f, rowH), "左肘屈曲角 / Left flexion", heading);
        GUI.Label(new Rect(valueX, y, 105f, rowH), $"{leftElbow:F1}°", value);
        y += rowH;
        GUI.Label(new Rect(labelX, y, 345f, rowH), "右肘屈曲角 / Right flexion", heading);
        GUI.Label(new Rect(valueX, y, 105f, rowH), $"{rightElbow:F1}°", value);

        y += rowH + 8f;
        GUI.Label(new Rect(labelX, y, 250f, 28f), "膝关节 / Knee", section);
        y += 28f;
        GUI.Label(new Rect(labelX, y, 345f, rowH), "左膝屈曲角 / Left flexion", heading);
        GUI.Label(new Rect(valueX, y, 105f, rowH), $"{leftFlex:F1}°", value);
        y += rowH;
        GUI.Label(new Rect(labelX, y, 345f, rowH), "左腿几何夹角 / Left included", heading);
        GUI.Label(new Rect(valueX, y, 105f, rowH), $"{leftIncluded:F1}°", value);
        y += rowH + 6f;
        GUI.Label(new Rect(labelX, y, 345f, rowH), "右膝屈曲角 / Right flexion", heading);
        GUI.Label(new Rect(valueX, y, 105f, rowH), $"{rightFlex:F1}°", value);
        y += rowH;
        GUI.Label(new Rect(labelX, y, 345f, rowH), "右腿几何夹角 / Right included", heading);
        GUI.Label(new Rect(valueX, y, 105f, rowH), $"{rightIncluded:F1}°", value);

        string leftKneeStatus = controller.LeftKneeMeasurementFresh ? "有效" : "无效/陈旧";
        string rightKneeStatus = controller.RightKneeMeasurementFresh ? "有效" : "无效/陈旧";
        GUI.Label(new Rect(18f, 370f, 462f, 48f),
            $"屈曲角：伸直≈0°；几何夹角：伸直≈180°。小腿骨骼已解锁。\n" +
            $"06+07/08+09配对：左{leftKneeStatus}｜右{rightKneeStatus}；仅07/09时为单骨骼诊断", hint);
    }

    private Rect GetKneeBottomRightRect()
    {
        float x = Mathf.Max(KNEE_MARGIN, Screen.width - KNEE_WINDOW_W - KNEE_MARGIN);
        float y = Mathf.Max(KNEE_MARGIN, Screen.height - KNEE_WINDOW_H - KNEE_MARGIN);
        return new Rect(x, y, KNEE_WINDOW_W, KNEE_WINDOW_H);
    }

    // ═══════════════════════════════════════════════════════════════
    //  中心开始按钮
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 绘制屏幕中心的圆形状态/开始按钮。
    /// 
    /// 状态显示优先级：
    ///   未连接 → 等待数据 → 校准中 → 等待稳定 → 点击开始
    /// 
    /// 就绪时按钮变为绿色，点击后触发 OnBeginDrivingRequested 事件。
    /// 同时支持校准完成 2 秒后自动开始（无需手动点击）。
    /// </summary>
    private void DrawCenterStartButton()
    {
        if (controller == null || controller.State == null) return;

        var state = controller.State;

        // V8.11无动作学习与顶部提示；九传感器全身标定完成后进入驱动。
        if (state.IsDriving) return;

        bool countdownActive = controller.IsCalibrationCountdownActive;
        isCalibratingUI = countdownActive;

        string label;
        if (!state.IsConnected)
        {
            label = "未连接";
        }
        else if (controller.IsCalibrationLockedWaitingForRuntime)
        {
            label = "标定已锁定\n等待运行数据";
        }
        else if (controller.IsRuntimeDriveSuspended)
        {
            label = "驱动已暂停\n等待链路恢复";
        }
        else if (!state.HasAnyData)
        {
            label = "等待数据";
        }
        else if (countdownActive)
        {
            if (controller.IsCalibrationSampling)
            {
                float remain = controller.CalibrationStableSamplingRemaining;
                if (remain > 0.05f)
                {
                    label = controller.CalibrationRejectedJumpFrames > 0
                        ? $"保持不动\n{remain:F1}s\n滤跳:{controller.CalibrationRejectedJumpFrames}"
                        : $"保持不动\n{remain:F1}s";
                }
                else
                {
                    label = "正在补采\n有效新帧";
                }
            }
            else
            {
                int remain = Mathf.CeilToInt(controller.CalibrationCountdownRemaining);
                label = $"摆好 A-Pose\n{remain}s";
            }
        }
        else if (!string.IsNullOrEmpty(controller.CalibrationCountdownStatus) &&
                 (controller.CalibrationCountdownStatus.Contains("失败") ||
                  controller.CalibrationCountdownStatus.Contains("中断") ||
                  controller.CalibrationCountdownStatus.Contains("尚未收到") ||
                  controller.CalibrationCountdownStatus.Contains("不存在") ||
                  controller.CalibrationCountdownStatus.Contains("非法") ||
                  controller.CalibrationCountdownStatus.Contains("过期") ||
                  controller.CalibrationCountdownStatus.Contains("失效") ||
                  controller.CalibrationCountdownStatus.Contains("未找到") ||
                  controller.CalibrationCountdownStatus.Contains("越界") ||
                  controller.CalibrationCountdownStatus.Contains("断开")))
        {
            // 旧版倒计时被某一路实时超时取消后会直接恢复成“点击准备”，用户看不到原因。
            // V77.30明确显示未完成状态，Console同时输出具体设备/原因。
            label = controller.AutomaticCalibrationEnabled
                ? "标定未完成\n自动重试中"
                : "标定未完成\n点击重试";
        }
        else if (!state.IsStable)
        {
            label = controller.AutomaticCalibrationEnabled
                ? "自动等待\n传感器稳定"
                : "等待稳定";
        }
        else
        {
            label = controller.AutomaticCalibrationEnabled
                ? $"数据已稳定\n自动标定\n{controller.AutomaticCalibrationHoldRemaining:F1}s"
                : "点击准备";
        }

        // V8由Controller锁存当前实际在线组合；缺席传感器不参与门控。
        bool canStart = state.IsConnected && state.HasAnyData && state.IsStable && !countdownActive &&
                        !controller.IsWaitingForRuntimeData;
        var tex = canStart ? btnCircleReady : btnCircleWaiting;

        int size = START_BUTTON_SIZE;
        var rect = new Rect((Screen.width - size) / 2f, (Screen.height - size) / 2f, size, size);
        if (tex != null) GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, true);

        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.RoundToInt(size * 0.17f),
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            normal = { textColor = UiTextPrimary }
        };

        // 深色阴影保证绿色草地、白色模型和灰色窗口上都能看清中心状态。
        var shadowStyle = new GUIStyle(style)
        {
            normal = { textColor = new Color(0f, 0f, 0f, 0.90f) }
        };
        GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), label, shadowStyle);
        GUI.Label(rect, label, style);

        if (canStart && GUI.Button(rect, GUIContent.none, GUIStyle.none))
            OnBeginDrivingRequested?.Invoke();
    }

    // ═══════════════════════════════════════════════════════════════
    //  端口下拉菜单
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 绘制端口选择下拉菜单。
    /// 最多显示 6 项，超出时出现滚动条。
    /// 点击菜单外区域自动关闭。
    /// </summary>
    private void HandlePortDropdown(Rect btnRect, string[] ports)
    {
        if (!portDropdownOpen) return;

        int count = ports != null ? ports.Length : 0;
        float listVisible = Mathf.Min(6, Mathf.Max(0, count));  // 最多显示 6 项
        float listH = listVisible * PORT_ITEM_HEIGHT + 4f;
        Rect dropRect = new Rect(btnRect.x, btnRect.y + btnRect.height + 2f, btnRect.width, listH);

        // 点击菜单外区域时关闭下拉菜单
        if (Event.current.type == EventType.MouseDown
            && !btnRect.Contains(Event.current.mousePosition)
            && !dropRect.Contains(Event.current.mousePosition))
        {
            portDropdownOpen = false;
            return;
        }

        // 绘制下拉菜单背景
        GUI.Box(dropRect, "");

        // 可滚动的端口列表
        Rect viewRect = new Rect(0, 0, dropRect.width - 20f, count * PORT_ITEM_HEIGHT);
        portDropdownScroll = GUI.BeginScrollView(dropRect, portDropdownScroll, viewRect);
        for (int i = 0; i < count; i++)
        {
            Rect itemRect = new Rect(2f, i * PORT_ITEM_HEIGHT, dropRect.width - 24f, PORT_ITEM_HEIGHT);
            if (GUI.Button(itemRect, ports[i]))
            {
                // 用户选中某个端口
                OnPortSelected?.Invoke(i);
                portDropdownOpen = false;
            }
        }
        GUI.EndScrollView();
    }


    /// <summary>
    /// 获取 Sensor Telemetry 固定在屏幕左下角时的窗口矩形。
    /// IMGUI 坐标系原点在左上角，所以左下角 y = Screen.height - height - margin。
    /// </summary>
    private Rect GetTelemetryBottomLeftRect()
    {
        float w = Mathf.Max(MIN_TELE_W, TELE_DEFAULT_W);
        float h = Mathf.Max(MIN_TELE_H, TELE_DEFAULT_H);

        float x = TELE_MARGIN;
        float y = Mathf.Max(TELE_MARGIN, Screen.height - h - TELE_MARGIN);

        return new Rect(x, y, w, h);
    }

    // ═══════════════════════════════════════════════════════════════
    //  通用工具方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 在窗口右下角绘制可拖拽的缩放手柄。
    /// 支持 MouseDown（开始缩放）→ MouseDrag（持续缩放）→ MouseUp（结束缩放）。
    /// 窗口尺寸不会小于指定的最小值。
    /// </summary>
    private void DrawResizeHandle(ref Rect windowRect, ref bool isResizing, float minW, float minH)
    {
        // 手柄位于窗口右下角
        Rect handleRect = new Rect(
            windowRect.width - RESIZE_HANDLE,
            windowRect.height - RESIZE_HANDLE,
            RESIZE_HANDLE, RESIZE_HANDLE);
        GUI.Box(handleRect, "");

        Event e = Event.current;

        // 鼠标按下在手柄区域 → 开始缩放
        if (e.type == EventType.MouseDown && handleRect.Contains(e.mousePosition))
        {
            isResizing = true;
            resizeStartMouse = e.mousePosition;
            resizeStartRect = windowRect;
            e.Use();  // 消费事件，防止被其他控件处理
        }

        // 拖拽中 → 实时更新窗口尺寸
        if (e.type == EventType.MouseDrag && isResizing)
        {
            Vector2 delta = e.mousePosition - resizeStartMouse;
            windowRect.width = Mathf.Max(minW, resizeStartRect.width + delta.x);
            windowRect.height = Mathf.Max(minH, resizeStartRect.height + delta.y);
            e.Use();
        }

        // 鼠标释放 → 结束缩放
        if (e.type == EventType.MouseUp && isResizing)
        {
            isResizing = false;
            e.Use();
        }
    }

    /// <summary>
    /// 确保表格样式已初始化。
    /// GUIStyle 必须在 OnGUI 上下文中创建（需要 GUI.skin），
    /// 因此使用延迟初始化模式。
    /// </summary>
    private void EnsureTableStyles()
    {
        if (tableHeaderStyle == null)
            tableHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = UiTextPrimary, background = tableHeaderBackground }
            };
        if (tableCellStyle == null)
            tableCellStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = UiTextPrimary, background = tableCellBackground }
            };
        if (statusOfflineStyle == null)
        {
            statusOfflineStyle = MakeStatusStyle(UiDanger);
            statusWaitingStyle = MakeStatusStyle(UiAccent);
            statusReadyStyle = MakeStatusStyle(UiTextSecondary);
            statusSuccessStyle = MakeStatusStyle(UiSuccess);
            statusLockedStyle = MakeStatusStyle(UiMuted);
            statusFailedStyle = MakeStatusStyle(UiDanger);
        }
    }

    private GUIStyle MakeStatusStyle(Color color)
    {
        return new GUIStyle(GUI.skin.box)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = color, background = tableCellBackground }
        };
    }

    private GUIStyle GetCalibrationStateStyle(
        MotionCaptureController.SensorCalibrationUiState state)
    {
        switch (state)
        {
            case MotionCaptureController.SensorCalibrationUiState.Offline:
                return statusOfflineStyle;
            case MotionCaptureController.SensorCalibrationUiState.WaitingForStability:
                return statusWaitingStyle;
            case MotionCaptureController.SensorCalibrationUiState.Ready:
            case MotionCaptureController.SensorCalibrationUiState.Sampling:
                return statusReadyStyle;
            case MotionCaptureController.SensorCalibrationUiState.Sampled:
                return statusSuccessStyle;
            case MotionCaptureController.SensorCalibrationUiState.Succeeded:
                return statusSuccessStyle;
            case MotionCaptureController.SensorCalibrationUiState.Failed:
                return statusFailedStyle;
            default:
                return statusLockedStyle;
        }
    }

    private static string GetCalibrationStateText(
        MotionCaptureController.SensorCalibrationUiState state)
    {
        switch (state)
        {
            case MotionCaptureController.SensorCalibrationUiState.Offline: return "离线";
            case MotionCaptureController.SensorCalibrationUiState.WaitingForStability: return "待稳定";
            case MotionCaptureController.SensorCalibrationUiState.Ready: return "就绪";
            case MotionCaptureController.SensorCalibrationUiState.Sampling: return "采集中";
            case MotionCaptureController.SensorCalibrationUiState.Sampled: return "已采满";
            case MotionCaptureController.SensorCalibrationUiState.Succeeded: return "成功";
            case MotionCaptureController.SensorCalibrationUiState.Locked: return "锁定";
            case MotionCaptureController.SensorCalibrationUiState.NotDriven: return "已排除";
            case MotionCaptureController.SensorCalibrationUiState.Failed: return "失败";
            default: return "未知";
        }
    }

    private void ApplyHighContrastSkin()
    {
        if (GUI.skin == null) return;

        SetAllTextStates(GUI.skin.label, UiTextPrimary);
        SetAllTextStates(GUI.skin.window, UiTextPrimary);
        SetAllTextStates(GUI.skin.toggle, UiTextPrimary);
        SetAllTextStates(GUI.skin.button, UiTextPrimary);
        SetAllTextStates(GUI.skin.box, UiTextPrimary);

        if (tableCellBackground != null)
            GUI.skin.window.normal.background = tableCellBackground;

        // 输入框保留浅底，但改用深蓝文字，避免白字与输入框背景混在一起。
        Color inputText = new Color(0.03f, 0.09f, 0.16f, 1f);
        SetAllTextStates(GUI.skin.textField, inputText);
    }

    private static void SetAllTextStates(GUIStyle style, Color color)
    {
        if (style == null) return;
        style.normal.textColor = color;
        style.hover.textColor = color;
        style.active.textColor = color;
        style.focused.textColor = color;
        style.onNormal.textColor = color;
        style.onHover.textColor = color;
        style.onActive.textColor = color;
        style.onFocused.textColor = color;
    }

    /// <summary>
    /// 安全地将文本解析为波特率整数。
    /// 解析失败或值 ≤ 0 时返回 fallback 默认值。
    /// </summary>
    private static int ParseBaud(string text, int fallback) =>
        int.TryParse(text, out int b) && b > 0 ? b : fallback;

    private static Texture2D MakeSolidTexture(Color color)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp
        };
        tex.SetPixel(0, 0, color);
        tex.Apply(false, true);
        return tex;
    }

    /// <summary>
    /// 程序化生成带渐变边缘的圆形纹理。
    /// 用于中心开始按钮，避免依赖外部图片资源。
    /// 
    /// 算法：遍历每个像素，计算到圆心的距离，
    /// 圆内用 fill 颜色填充，边缘用 rim 颜色渐变，圆外透明。
    /// </summary>
    /// <param name="size">纹理尺寸（像素，正方形）</param>
    /// <param name="fill">圆内填充色</param>
    /// <param name="rim">边缘颜色</param>
    private static Texture2D MakeCircleTexture(int size, Color fill, Color rim)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp
        };

        float r = size * 0.5f - 1f;           // 圆的半径
        float cx = r + 1f, cy = r + 1f;       // 圆心坐标
        float rimW = Mathf.Max(2f, size * 0.06f);  // 边缘渐变宽度

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);  // 到圆心的距离

                if (d <= r)
                {
                    // 圆内：靠近边缘的区域用 rim 和 fill 的渐变
                    Color c = fill;
                    if (d > r - rimW)
                        c = Color.Lerp(rim, fill, Mathf.InverseLerp(r, r - rimW, d));
                    tex.SetPixel(x, y, c);
                }
                else
                {
                    // 圆外：完全透明
                    tex.SetPixel(x, y, new Color(0, 0, 0, 0));
                }
            }
        }

        // 应用像素修改到 GPU 纹理，makeNoLongerReadable=true 释放 CPU 端内存
        tex.Apply(false, true);
        return tex;
    }
}

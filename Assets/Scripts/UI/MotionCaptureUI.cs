
using System;
using UnityEngine;

/// <summary>
/// IMGUI 界面层。
/// 
/// 纯粹负责绘制 UI 和采集用户输入，不直接修改任何业务状态。
/// 所有用户操作通过事件（Action）向外广播，由 MotionCaptureController 接收并处理。
/// 
/// 界面组成：
///   1. Control Interface 窗口 — 端口选择、连接/断开、动作参数调整
///   2. Sensor Telemetry 窗口 — 9 个传感器的实时四元数和欧拉角数据表格
///   3. 中心开始按钮 — 显示当前状态（未连接/等待数据/校准中/等待稳定/点击开始）
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

    // 用户是否已点击开始（防止重复触发）
    private bool hasStarted;

    // 是否正在校准中（显示"校准中"状态）
    private bool isCalibratingUI;

    // 校准完成的时间戳（用于 2 秒自动开始倒计时）
    private float calibratedTimestamp = -1f;

    // 波特率输入框的文本内容
    private string baudText = "115200";

    // 各个开关的 UI 状态（UI 层的本地副本，变化时通过事件通知业务层）
    private bool limitsEnabledUI;           // 角度限制开关
    private bool smoothingEnabledUI = true; // 平滑开关（默认开启）
    private bool twistSwingEnabledUI;       // Twist+Swing 模式开关
    private bool requireAllDevicesUI;       // 要求所有设备稳定
    private int minStableDevicesUI = 1;     // 最少稳定设备数

    // ── 端口下拉选择 ──
    private bool portDropdownOpen;          // 下拉菜单是否展开
    private Vector2 portDropdownScroll;     // 下拉列表的滚动位置
    private const float PORT_ITEM_HEIGHT = 20f;  // 每个端口项的高度

    // ── 窗口布局 ──
    private Rect controlWindowRect = new Rect(20f, 20f, 320f, 450f);   // 控制面板位置
    private Rect telemetryWindowRect;        // 遥测窗口位置（在 Start 中初始化）
    private bool telemetryRectInitialized;   // 遥测窗口是否已初始化过位置

    // 窗口 ID（IMGUI 要求每个窗口有唯一 ID）
    private const int CTRL_WINDOW_ID = 0xC0DE120;
    private const int TELE_WINDOW_ID = 0xC0DE123;

    // 窗口最小尺寸限制
    private const float MIN_CTRL_W = 260f, MIN_CTRL_H = 450f;
    private const float MIN_TELE_W = 560f, MIN_TELE_H = 280f;
    private const float TELE_DEFAULT_W = 620f, TELE_DEFAULT_H = 300f;
    private const float TELE_MARGIN = 20f;     // 遥测窗口距屏幕边缘的边距
    private const float RESIZE_HANDLE = 14f;   // 缩放手柄大小（像素）

    // 窗口缩放状态
    private bool isResizingControl, isResizingTelemetry;
    private Vector2 resizeStartMouse;       // 缩放开始时的鼠标位置
    private Rect resizeStartRect;           // 缩放开始时的窗口矩形

    // 遥测表格的样式（延迟初始化，因为 GUIStyle 需要在 OnGUI 中创建）
    private GUIStyle tableHeaderStyle, tableCellStyle;

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

        // 初始化遥测窗口位置：默认放在屏幕右下角
        if (!telemetryRectInitialized)
        {
            float w = Mathf.Max(MIN_TELE_W, TELE_DEFAULT_W);
            float h = Mathf.Max(MIN_TELE_H, TELE_DEFAULT_H);
            float x = Mathf.Max(TELE_MARGIN, Screen.width - w - TELE_MARGIN);
            float y = Mathf.Max(TELE_MARGIN, Screen.height - h - TELE_MARGIN);
            telemetryWindowRect = new Rect(x, y, w, h);
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

        // 连接中但未校准 → 显示"校准中"
        isCalibratingUI = s.IsConnected && !s.IsCalibrated;

        // 记录校准完成的时间（用于 2 秒自动开始倒计时）
        if (s.IsConnected && s.IsCalibrated && calibratedTimestamp < 0f)
            calibratedTimestamp = Time.time;

        // 断开连接时重置校准时间戳
        if (!s.IsConnected)
            calibratedTimestamp = -1f;
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
        // 绘制可拖动的控制面板窗口
        controlWindowRect = GUI.Window(CTRL_WINDOW_ID, controlWindowRect,
            DrawControlWindow, "Control Interface");

        // 绘制可拖动的遥测数据窗口
        telemetryWindowRect = GUI.Window(TELE_WINDOW_ID, telemetryWindowRect,
            DrawTelemetryWindow, "Sensor Telemetry");

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
                isCalibratingUI = true;   // 进入校准等待状态
                hasStarted = false;
            }
            else
            {
                // 断开：发送断开请求
                OnDisconnectRequested?.Invoke();
                hasStarted = false;
                isCalibratingUI = false;
            }
        }
        GUI.enabled = true;

        // ── 刷新端口 & 重置按钮 ──
        if (GUI.Button(new Rect(20, 90, 120, 22), "刷新端口"))
            OnRefreshPortsRequested?.Invoke();

        if (GUI.Button(new Rect(150, 90, 70, 22), "重置"))
        {
            hasStarted = false;
            calibratedTimestamp = -1f;
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

        // 系统稳定性状态
        GUI.Label(new Rect(20, 395, 200, 20), $"稳定: {(state.IsStable ? "OK" : "等待")}");

        // ── 窗口缩放手柄 & 标题栏拖拽 ──
        DrawResizeHandle(ref controlWindowRect, ref isResizingControl, MIN_CTRL_W, MIN_CTRL_H);
        GUI.DragWindow(new Rect(0, 0, controlWindowRect.width, 22f));
    }

    // ═══════════════════════════════════════════════════════════════
    //  遥测数据窗口
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 绘制遥测数据窗口：9 行（每个传感器一行）× 8 列的实时数据表格。
    /// 列：设备号、四元数 (q0-q3)、欧拉角 (yaw/pitch/roll)
    /// </summary>
    private void DrawTelemetryWindow(int id)
    {
        // 确保表格样式已初始化
        EnsureTableStyles();

        int deviceCount = controller.Config.deviceCount;

        // 表头定义
        string[] headers = { "number", "q0", "q1", "q2", "q3", "yaw", "pitch", "roll" };
        float[] widths = { 70f, 70f, 70f, 70f, 70f, 70f, 70f, 70f };
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

            // 逐列绘制：设备号 → 四元数分量 → 欧拉角分量
            GUI.Label(new Rect(x, y, widths[0], rowH), $"0x{i + 1:00}", tableCellStyle); x += widths[0];
            GUI.Label(new Rect(x, y, widths[1], rowH), q.x.ToString("F3"), tableCellStyle); x += widths[1];
            GUI.Label(new Rect(x, y, widths[2], rowH), q.y.ToString("F3"), tableCellStyle); x += widths[2];
            GUI.Label(new Rect(x, y, widths[3], rowH), q.z.ToString("F3"), tableCellStyle); x += widths[3];
            GUI.Label(new Rect(x, y, widths[4], rowH), q.w.ToString("F3"), tableCellStyle); x += widths[4];
            GUI.Label(new Rect(x, y, widths[5], rowH), euler.z.ToString("F1"), tableCellStyle); x += widths[5];
            GUI.Label(new Rect(x, y, widths[6], rowH), euler.y.ToString("F1"), tableCellStyle); x += widths[6];
            GUI.Label(new Rect(x, y, widths[7], rowH), euler.x.ToString("F1"), tableCellStyle);
        }

        // 窗口缩放手柄 & 标题栏拖拽
        DrawResizeHandle(ref telemetryWindowRect, ref isResizingTelemetry, MIN_TELE_W, MIN_TELE_H);
        GUI.DragWindow(new Rect(0, 0, telemetryWindowRect.width, 22f));
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
        var state = controller.State;

        // 已开始或已在驱动中则不再显示开始按钮
        if (hasStarted || state.IsDriving) return;

        // 根据当前状态决定按钮文本
        string label;
        if (!state.IsConnected) label = "未连接";
        else if (!state.HasAnyData) label = "等待数据";
        else if (isCalibratingUI && !state.IsCalibrated) label = "校准中";
        else label = state.IsStable ? "点击开始" : "等待稳定";

        // 判断是否满足开始条件：已连接 + 有数据 + 已校准 + 已稳定
        bool canStart = state.IsConnected && state.HasAnyData
                        && !isCalibratingUI && state.IsCalibrated && state.IsStable;

        // 选择按钮纹理：就绪（绿色）或等待（灰色）
        var tex = canStart ? btnCircleReady : btnCircleWaiting;

        // 居中绘制
        int size = START_BUTTON_SIZE;
        var rect = new Rect((Screen.width - size) / 2f, (Screen.height - size) / 2f, size, size);
        if (tex != null) GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, true);

        // 绘制按钮文本（居中、粗体、白色）
        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.RoundToInt(size * 0.2f),
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        GUI.Label(rect, label, style);

        // 用户点击：触发开始驱动
        if (canStart && GUI.Button(rect, GUIContent.none, GUIStyle.none))
        {
            hasStarted = true;
            isCalibratingUI = false;
            OnBeginDrivingRequested?.Invoke();
        }

        // 校准完成 2 秒后自动开始（无需用户手动点击）
        if (state.IsConnected && state.HasAnyData && state.IsCalibrated
            && !hasStarted && !state.IsDriving && calibratedTimestamp > 0f
            && Time.time - calibratedTimestamp >= 2f)
        {
            hasStarted = true;
            isCalibratingUI = false;
            OnBeginDrivingRequested?.Invoke();
        }
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
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
        if (tableCellStyle == null)
            tableCellStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
    }

    /// <summary>
    /// 安全地将文本解析为波特率整数。
    /// 解析失败或值 ≤ 0 时返回 fallback 默认值。
    /// </summary>
    private static int ParseBaud(string text, int fallback) =>
        int.TryParse(text, out int b) && b > 0 ? b : fallback;

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




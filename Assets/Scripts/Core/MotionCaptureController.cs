
using System.IO;
using UnityEngine;

/// <summary>
/// 动捕系统主控协调器（MonoBehaviour）。
/// 
/// 这是挂载到 Unity 场景 GameObject 上的核心组件。
/// 采用"薄协调者"模式——自身不包含数据处理或界面绘制逻辑，
/// 仅负责：
///   1. 在 Start() 中组装所有子系统
///   2. 在 Update()/LateUpdate() 中按正确顺序调用子系统的处理管线
///   3. 响应 UI 事件并将操作路由到对应子系统
///   4. 在退出/禁用时清理资源
/// 
/// 子系统职责：
///   - MotionCaptureState:   集中状态管理 + 变更事件
///   - SerialManager:        串口连接生命周期
///   - SensorDataProcessor:  数据处理流水线（出队→坐标转换→稳定性→校准→帧同步→应用）
///   - TelemetryLogger:      遥测数据文件记录
///   - RootMotionSolver:     根节点垂直位移补偿（下蹲贴地）
///   - MotionCaptureUI:      IMGUI 界面（同 GameObject 上的另一个组件）
/// 
/// 使用方式：
///   1. 在场景中创建 GameObject
///   2. 添加 MotionCaptureController 组件
///   3. 在 Inspector 中拖入 MotionCaptureConfig 资产
///   4. MotionCaptureUI 会自动添加（RequireComponent）
/// </summary>
public class MotionCaptureController : MonoBehaviour
{
    // ═══════════════════════════════════════════════════════════════
    //  Inspector 配置
    // ═══════════════════════════════════════════════════════════════

    [Header("配置资产")]
    [Tooltip("拖入在编辑器中创建的 MotionCaptureConfig ScriptableObject")]
    [SerializeField] private MotionCaptureConfig config;

    // ═══════════════════════════════════════════════════════════════
    //  子系统引用（对 UI 层只读暴露）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>全局配置资产引用（UI 层读取参数用）</summary>
    public MotionCaptureConfig Config => config;

    /// <summary>全局状态容器（UI 层订阅 OnChanged 事件用）</summary>
    public MotionCaptureState State { get; private set; }

    /// <summary>串口管理器（UI 层读取端口列表用）</summary>
    public SerialManager Serial { get; private set; }

    /// <summary>经坐标转换后的 9 个设备四元数数组（UI 遥测表格用）</summary>
    public Quaternion[] TransformedQuaternions => processor?.TransformedQuaternions;

    /// <summary>当前是否正在写入遥测日志</summary>
    public bool IsLogging => logger != null && logger.IsLogging;

    /// <summary>当前日志文件路径</summary>
    public string CurrentLogPath => logger?.CurrentLogPath ?? "";

    // ═══════════════════════════════════════════════════════════════
    //  私有子系统实例
    // ═══════════════════════════════════════════════════════════════

    /// <summary>传感器数据处理流水线</summary>
    private SensorDataProcessor processor;

    /// <summary>遥测数据记录器</summary>
    private TelemetryLogger logger;

    /// <summary>根节点垂直位移补偿器（解决下蹲悬浮问题）</summary>
    private RootMotionSolver rootSolver;

    /// <summary>
    /// 当前训练模式是否允许根节点补偿。各训练可按自己的落地策略开关。
    /// 配置资产中的 rootMotionEnabled 仍是总开关。
    /// </summary>
    private bool rootMotionEnabledForTraining = true;

    // ═══════════════════════════════════════════════════════════════
    //  骨骼与角色数据
    // ═══════════════════════════════════════════════════════════════

    /// <summary>9 个骨骼的 GameObject 引用（索引与传感器 ID 对应）</summary>
    private GameObject[] bones;

    /// <summary>每个骨骼在 T-Pose 时的 localRotation（用于重置和约束参考）</summary>
    private Quaternion[] restLocalRotations;

    /// <summary>角色根节点 Transform</summary>
    private Transform avatarRoot;

    /// <summary>角色在场景中的初始朝向（用于坐标系补偿）</summary>
    private Quaternion rootFacingOffset = Quaternion.identity;

    // ═══════════════════════════════════════════════════════════════
    //  运行时参数
    // ═══════════════════════════════════════════════════════════════

    /// <summary>当前使用的波特率</summary>
    private int selectedBaud;

    /// <summary>是否要求所有设备数据同步才进行驱动（可由 UI 切换）</summary>
    private bool requireAllDevices;

    /// <summary>已写入 session_config 的日志路径，避免每帧重复写配置。</summary>
    private string configuredLogPath = "";

    // ═══════════════════════════════════════════════════════════════
    //  公开读取方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>获取当前日志导出目录路径（UI 文本框显示用）</summary>
    public string GetExportDirectory() => logger?.GetExportDirectory() ?? "";

    /// <summary>读取设备最后一帧的实时帧龄；从未收帧或索引无效时返回 -1。</summary>
    public float GetDeviceFrameAgeSeconds(int deviceIndex)
    {
        return processor != null
            ? processor.GetDeviceFrameAgeSeconds(deviceIndex, Time.realtimeSinceStartup)
            : -1f;
    }

    /// <summary>诊断/回归测试：当前训练模式是否允许根节点补偿。</summary>
    public bool RootMotionEnabledForTraining => rootMotionEnabledForTraining;

    /// <summary>诊断/回归测试：根节点补偿当前是否实际运行。</summary>
    public bool IsRootMotionCompensationActive => rootSolver != null && rootSolver.Enabled;

    /// <summary>
    /// 按训练模式切换根节点补偿。关闭前先清除已有偏移并把人物根节点复位。
    /// </summary>
    public void SetRootMotionEnabledForTraining(bool enabled)
    {
        rootMotionEnabledForTraining = enabled;
        ApplyRootMotionTrainingMode();
    }

    /// <summary>游戏只读入口；未连接、未驱动时不向游戏提供可判定的姿态。</summary>
    public RehabPhotoGame.LowerBodyMeasurement ReadLowerBodyMeasurement(
        float timeoutSeconds, float maxSkewSeconds)
    {
        return ReadLowerBodyMeasurement(timeoutSeconds, maxSkewSeconds,
            RehabPhotoGame.TrainingLeg.Both);
    }

    public RehabPhotoGame.LowerBodyMeasurement ReadLowerBodyMeasurement(
        float timeoutSeconds, float maxSkewSeconds, RehabPhotoGame.TrainingLeg leg)
    {
        int requiredMask = leg == RehabPhotoGame.TrainingLeg.Left ? 3 :
            leg == RehabPhotoGame.TrainingLeg.Right ? 12 : 15;
        var sample = processor != null
            ? processor.ReadLowerBodyMeasurement(Time.realtimeSinceStartup,
                timeoutSeconds, maxSkewSeconds, requiredMask)
            : new RehabPhotoGame.LowerBodyMeasurement();
        if (!isActiveAndEnabled || State == null || Serial == null || !Serial.IsConnected ||
            !State.IsDriving || !State.IsCalibrated)
        {
            sample.IsValid = false;
            sample.FailureReason = "等待串口连接、站姿标定及开始驱动";
        }
        return sample;
    }

    /// <summary>游戏阶段事件进入既有 JSONL 文件，不写入屏幕日志。</summary>
    public void LogGameDiagnostic(string eventName, string detail)
    {
        logger?.LogEvent("knee_extension_" + eventName, detail);
    }

    /// <summary>把右腿坐姿/伸直校正同时应用于角度显示与人物骨骼驱动。</summary>
    public void ConfigureRightLegCalibration(
        float rawReadyKneeDeg,
        float rawStraightKneeDeg,
        float rawSeatedThighDeg)
    {
        processor?.ConfigureRightLegCalibration(
            rawReadyKneeDeg, rawStraightKneeDeg, rawSeatedThighDeg);
    }

    public void ClearRightLegCalibration()
    {
        processor?.ClearRightLegCalibration();
    }

    public void ConfigureSeatedLegCalibration(
        int leg,
        float rawReadyKneeDeg,
        float rawStraightKneeDeg,
        float rawSeatedThighDeg,
        float targetReadyKneeDeg,
        float targetSeatedThighDeg)
    {
        processor?.ConfigureSeatedLegCalibration(
            leg, rawReadyKneeDeg, rawStraightKneeDeg, rawSeatedThighDeg,
            targetReadyKneeDeg, targetSeatedThighDeg);
    }

    public void ClearSeatedLegCalibration(int leg)
    {
        processor?.ClearSeatedLegCalibration(leg);
    }

    public void SetSeatedTrainingVisual(
        bool enabled,
        int selectedLeg,
        bool switching,
        float sharedThighDeg,
        float sharedKneeDeg)
    {
        processor?.SetSeatedTrainingVisual(
            enabled, selectedLeg, switching, sharedThighDeg, sharedKneeDeg);
    }

    public void SetGroundedFeet(bool leftGrounded, bool rightGrounded)
    {
        rootSolver?.SetGroundedFeet(leftGrounded, rightGrounded);
    }

    public void LockSeatedVerticalOffset()
    {
        rootSolver?.LockCurrentVerticalOffset();
    }

    public void UnlockSeatedVerticalOffset()
    {
        rootSolver?.UnlockVerticalOffset();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Unity 生命周期
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Awake — 最早的初始化阶段。
    /// 检查配置资产是否已绑定，若缺失则禁用组件避免空引用。
    /// </summary>
    private void Awake()
    {
        if (config == null)
        {
            Debug.LogError("[MotionCaptureController] 缺少 MotionCaptureConfig 资产引用，请在 Inspector 中指定。");
            enabled = false;
            return;
        }
        config.Validate();

        // 在 Awake 中提前创建 State，保证其他脚本的 Start() 访问时已初始化
        State = new MotionCaptureState(config.deviceCount);
    }

    /// <summary>
    /// Start — 初始化所有子系统，查找骨骼对象，建立事件绑定。
    /// 
    /// 初始化顺序：
    ///   1. 创建子系统实例
    ///   2. 查找角色根节点并记录初始朝向
    ///   3. 遍历查找 9 个骨骼 GameObject，缓存绑定姿态
    ///   4. 枚举系统串口
    ///   5. 配置异常检测
    ///   6. 初始化根节点位移补偿器
    ///   7. 绑定 UI 事件
    /// </summary>
    private void Start()
    {
        // 退出全屏模式以便调试
        Screen.fullScreen = false;
        int n = config.deviceCount;

        // ── 1. 创建子系统实例（State 已在 Awake 中创建） ──
        Serial = new SerialManager();             // 串口管理
        processor = new SensorDataProcessor(config);  // 数据处理管线
        // Application.dataPath 在编辑器中指向项目 Assets，向上一级即项目根目录。
        // 因此默认日志固定写入“项目根目录/Logs”，不依赖启动 Unity 时的工作目录。
        string logsDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
        logger = new TelemetryLogger(logsDirectory, config.boneNames);
        selectedBaud = config.defaultBaud;
        requireAllDevices = config.requireAllDevices;
        State.OnChanged += HandleStateChanged;
        // 一进入 Play Mode 就建立本次测试日志；这样连接前的配置、骨骼缺失警告、
        // 串口连接失败等信息也不会丢失。
        if (logger.SaveEnabled)
            logger.Open();

        // ── 2. 查找角色根节点并记录初始朝向 ──
        // 角色可能不面朝 Z+，需要记录初始朝向用于坐标补偿
        ResolveAvatarRoot();
        rootFacingOffset = avatarRoot != null ? avatarRoot.rotation : Quaternion.identity;
        processor.SetRootFacingOffset(rootFacingOffset);

        // ── 3. 查找骨骼对象 & 缓存绑定姿态 ──
        // 按 config.boneNames 在场景中查找 9 个骨骼 GameObject
        // 记录每个骨骼的初始 localRotation（T-Pose），作为角度限制和重置的参考
        bones = new GameObject[n];
        restLocalRotations = new Quaternion[n];
        for (int i = 0; i < n; i++)
        {
            bones[i] = GameObject.Find(config.boneNames[i]);
            restLocalRotations[i] = bones[i] != null
                ? bones[i].transform.localRotation
                : Quaternion.identity;
        }
        // 将角度限制和绑定姿态传入旋转驱动器
        processor.InitConstraints(restLocalRotations);

        // ── 4. 枚举系统串口 ──
        Serial.RefreshPorts();
        // 尝试将选中端口对齐到配置中的默认端口（如 COM9）
        Serial.AlignToDefault(config.defaultPort);

        // ── 5. 配置异常检测 ──
        Serial.ConfigureAnomalyDetection(
            config.anomalyEnable, config.anomalyBufferSize, config.anomalyThreshold);

        // ── 6. 初始化根节点位移补偿器 ──
        // 查找足部骨骼，在 T-Pose 时记录地面参考线
        InitRootMotionSolver();

        // ── 7. 绑定 UI 事件 ──
        // 将 MotionCaptureUI 的各种按钮/开关事件绑定到对应的处理方法
        BindUIEvents();

        EnsureLogSessionConfigured();
        logger.LogEvent("controller_started", $"defaultPort={Serial.CurrentPort}, baud={selectedBaud}");
    }

    /// <summary>
    /// Update — 每帧数据处理管线。
    /// 
    /// 7 个阶段按顺序执行：
    ///   1. 出队串口数据（从后台线程队列取到主线程）
    ///   2. 坐标系转换（传感器→Unity Avatar 空间）
    ///   3. 稳定性监控（统计角速度）
    ///   4. 预校准（首次有数据时自动执行）
    ///   5. 刷新状态（变化时广播事件给 UI）
    ///   6. 驱动阶段（帧同步 + 插值 + 推送目标旋转）
    ///   7. 遥测日志自动管理
    /// </summary>
    private void Update()
    {
        if (processor == null || Serial == null) return;

        // ── 阶段 1：出队串口数据 ──
        // 后台线程持续将串口字节流解析为 (deviceId, Quaternion) 帧并推入队列
        // 这里在主线程一次性取出所有可用帧，回调 OnFrameDequeued 写日志
        processor.DequeueAll(Serial.Parser, State, OnFrameDequeued);

        // ── 阶段 2：坐标系转换 ──
        // 将传感器坐标系的四元数转换为 Unity 角色空间的四元数
        processor.TransformAll(State);

        // ── 阶段 3：稳定性监控 ──
        // 统计每个设备帧间的角度变化量，判断是否满足"连续 N 帧稳定"条件
        processor.UpdateStability(State);

        // ── 阶段 4：预校准 ──
        // 首次收到数据后自动执行一次校准，记录传感器初始姿态与骨骼初始姿态的偏差
        bool wasCalibrated = processor.Driver.IsCalibrated;
        if (Serial.IsConnected && State.CheckHasAnyData() && !wasCalibrated)
            processor.TryPreCalibrate(bones, State);
        if (!wasCalibrated && processor.Driver.IsCalibrated)
            logger.LogEvent("pre_calibrated", "首次收到有效数据后完成自动预校准");

        // ── 阶段 5：刷新状态（仅在变化时广播） ──
        // 计算系统整体稳定性，然后将 5 个核心状态传入 State.Refresh()
        // Refresh 内部做 diff 检查，仅在有变化时触发 OnChanged 事件
        bool isStable = processor.CheckStability(bones, State.GetDeviceHasDataArray());
        State.Refresh(
            Serial.IsConnected,
            State.CheckHasAnyData(),
            processor.Driver.IsCalibrated,
            State.IsDriving,
            isStable);

        // ── 阶段 6：驱动阶段 ──
        // 仅在用户点击"开始"后才进入驱动状态
        // 帧同步：对齐所有设备到统一时间戳 + 插值 → 推送目标旋转给 RotationDriver
        if (State.IsDriving)
            processor.SyncAndUpdateTargets(Serial.Parser, State, bones, requireAllDevices);

        // ── 阶段 7：遥测日志自动管理 ──
        // 根据 SaveEnabled 开关和连接状态自动打开/关闭日志文件
        logger.SyncState(Serial.IsConnected);
        EnsureLogSessionConfigured();
    }

    /// <summary>
    /// LateUpdate — 在动画系统之后将传感器旋转写入骨骼。
    /// 
    /// 三步流程：
    ///   1. 复位根节点到 T-Pose 位置（消除上帧位移补偿的干扰）
    ///   2. 应用传感器旋转到 9 个骨骼的 localRotation
    ///   3. 读取足部世界坐标，反算根节点垂直偏移使脚部贴地
    /// 
    /// 为什么用 LateUpdate 而不是 Update？
    /// Unity 的动画系统在 Update 和 LateUpdate 之间执行。
    /// 在 LateUpdate 写入 localRotation 可以覆盖动画系统的结果，
    /// 确保传感器数据拥有最终控制权。
    /// </summary>
    private void LateUpdate()
    {
        if (State == null || processor == null) return;

        if (State.IsDriving)
        {
            // Step 1: 复位根节点到 T-Pose 原始位置
            // 消除上一帧的位移偏移，确保本帧旋转计算基于一致的参考系
            rootSolver?.ResetRootPosition();

            // Step 2: 将传感器旋转写入骨骼 localRotation
            // RotationDriver.Apply() 内部处理平滑插值（Slerp）和角度限幅
            processor.ApplyToBones(bones);

            // Step 3: 根据足部世界坐标反算根节点垂直偏移
            // 下蹲时脚踝上升 → 根节点下沉 → 脚踝回到地面参考线
            rootSolver?.SolveAndApply();
        }

        // 在所有骨骼与根节点操作结束后记录最终结果，便于对比“输入、目标、实际”。
        logger?.LogRuntimeSnapshot(State, Serial, processor, bones, restLocalRotations,
            avatarRoot, rootSolver, selectedBaud, requireAllDevices);
    }

    /// <summary>程序退出时清理资源（关闭串口、关闭日志文件）</summary>
    private void OnApplicationQuit() => Cleanup();

    /// <summary>组件被禁用时清理资源</summary>
    private void OnDisable() => Cleanup();

    /// <summary>统一的资源清理方法</summary>
    private void Cleanup()
    {
        logger?.LogEvent("controller_cleanup", "组件禁用或应用退出");
        if (State != null) State.OnChanged -= HandleStateChanged;
        Serial?.Dispose();    // 断开串口连接
        logger?.Dispose();    // 关闭日志文件
    }

    // ═══════════════════════════════════════════════════════════════
    //  UI 事件处理（业务编排）
    //  MotionCaptureUI 通过事件通知 Controller 用户做了什么操作，
    //  Controller 将操作路由到对应的子系统方法。
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 绑定 UI 层的所有事件到对应的处理方法。
    /// 采用事件模式而非直接调用，实现 UI 与业务的解耦。
    /// </summary>
    private void BindUIEvents()
    {
        var ui = GetComponent<MotionCaptureUI>();
        if (ui == null) return;

        // 连接/断开
        ui.OnConnectRequested += HandleConnect;
        ui.OnDisconnectRequested += HandleDisconnect;
        // 端口管理
        ui.OnRefreshPortsRequested += HandleRefreshPorts;
        ui.OnPortSelected += HandlePortSelected;
        ui.OnPortManualInput += v => Serial.SetPortManual(v);
        // 驱动控制
        ui.OnBeginDrivingRequested += HandleBeginDriving;
        ui.OnResetRequested += HandleReset;
        // 旋转驱动参数
        ui.OnLimitsToggled += v => processor.Driver.SetLimitsEnabled(v);       // 角度限制开关
        ui.OnTwistSwingToggled += v => processor.Driver.SetTwistSwing(v);      // 限幅模式切换
        ui.OnSmoothingChanged += (en, spd) => processor.Driver.SetSmoothing(en, spd);  // 平滑开关
        // 稳定性参数
        ui.OnRequireAllDevicesChanged += v => requireAllDevices = v;
        ui.OnMinStableDevicesChanged += v => config.minStableDevices = v;
    }

    /// <summary>
    /// 处理"连接"请求：打开串口，成功后自动开始日志记录。
    /// </summary>
    private void HandleConnect(string port, int baud)
    {
        selectedBaud = baud;
        // 先打开日志，这样串口连接失败本身也会留下可排查记录。
        if (logger.SaveEnabled)
        {
            logger.Open();
            EnsureLogSessionConfigured();
            logger.LogEvent("connect_requested", $"port={port}, baud={baud}");
        }

        bool connected = Serial.Connect(port, baud);
        logger.LogEvent(connected ? "connect_succeeded" : "connect_failed",
            $"port={port}, baud={baud}");
        if (!connected)
            logger.Close("connect_failed");
    }

    /// <summary>
    /// 处理"断开"请求：关闭串口 + 关闭日志 + 更新状态。
    /// </summary>
    private void HandleDisconnect()
    {
        logger.LogEvent("disconnect_requested", $"port={Serial.CurrentPort}");
        Serial.Disconnect();
        State.SetConnected(false);
        logger.Close("serial_disconnected");
    }

    /// <summary>处理"刷新端口"请求：重新枚举系统串口列表</summary>
    private void HandleRefreshPorts()
    {
        Serial.RefreshPortsViaController();
    }

    /// <summary>
    /// 处理"开始驱动"请求：执行一次校准并进入驱动状态。
    /// 校准：记录当前传感器姿态与骨骼姿态的偏差作为零点。
    /// </summary>
    private void HandleBeginDriving()
    {
        logger.LogEvent("calibration_requested", "用户请求开始驱动");
        processor.Calibrate(bones, State);
        State.SetDriving(true);
        logger.LogEvent("driving_started", $"calibrated={processor.Driver.IsCalibrated}");
    }

    /// <summary>
    /// 处理"重置"请求：完全恢复到初始状态。
    /// 依次：停止驱动 → 清空解析器 → 断开串口 → 关闭日志 →
    ///       恢复骨骼到 T-Pose → 复位根节点 → 重置状态容器。
    /// </summary>
    private void HandleReset()
    {
        logger.LogEvent("reset_requested", "用户执行全量重置");
        State.SetDriving(false);           // 停止驱动
        Serial.ResetParser();              // 清空串口解析器的队列和缓冲
        Serial.Disconnect();               // 断开串口连接
        logger.Close("reset");             // 关闭日志文件

        processor.Reset(bones, restLocalRotations);  // 恢复骨骼到 T-Pose + 重建稳定性监控
        rootSolver?.Reset();               // 复位根节点到原始高度

        State.Reset();                     // 清除所有状态标志
    }

    /// <summary>处理端口选择事件：用户在下拉列表中选中了某个端口</summary>
    private void HandlePortSelected(int index)
    {
        Serial.SelectPort(index);
    }

    // ═══════════════════════════════════════════════════════════════
    //  帧数据回调
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 每次从串口队列出队一帧数据时的回调。
    /// 将原始数据写入遥测日志文件。
    /// </summary>
    private void OnFrameDequeued(int deviceId, Quaternion q, Vector3 euler)
    {
        logger.LogFrame(deviceId, q, euler, Serial?.Parser);
    }

    // ═══════════════════════════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>状态发生变化时立即写日志，而不是等待下一次周期快照。</summary>
    private void HandleStateChanged(MotionCaptureState state)
    {
        logger?.LogState(state);
    }

    /// <summary>每个新日志文件只写一次完整配置和骨骼绑定表。</summary>
    private void EnsureLogSessionConfigured()
    {
        if (logger == null || !logger.IsLogging) return;
        if (string.Equals(configuredLogPath, logger.CurrentLogPath, System.StringComparison.Ordinal)) return;

        logger.LogSessionConfiguration(config, bones, restLocalRotations,
            Serial?.CurrentPort ?? config.defaultPort, selectedBaud);
        configuredLogPath = logger.CurrentLogPath;
    }

    /// <summary>
    /// 通过名称在场景中查找角色根节点。
    /// 根节点名称由 config.avatarRootName 指定（默认 "renwu"）。
    /// </summary>
    private void ResolveAvatarRoot()
    {
        if (avatarRoot == null && !string.IsNullOrEmpty(config.avatarRootName))
        {
            var go = GameObject.Find(config.avatarRootName);
            if (go != null) avatarRoot = go.transform;
        }
    }

    /// <summary>
    /// 初始化根节点位移补偿器。
    /// 
    /// 查找足部骨骼的策略（由高到低优先级）：
    ///   1. 在 avatarRoot 子树中按名称递归查找（config.leftFootBoneName / rightFootBoneName）
    ///   2. 回退：取小腿骨骼（Calf）的第一个子节点作为 Foot 替代
    /// 
    /// 在 T-Pose 状态下调用，记录脚踝高度作为地面参考线。
    /// </summary>
    private void InitRootMotionSolver()
    {
        rootSolver = new RootMotionSolver();

        // 检查是否在配置中启用了根节点补偿
        if (!config.rootMotionEnabled)
        {
            rootSolver.Enabled = false;
            return;
        }

        // 根节点必须存在才能补偿
        if (avatarRoot == null)
        {
            Debug.LogWarning("[MotionCaptureController] avatarRoot 为 null，无法启用根节点补偿");
            rootSolver.Enabled = false;
            return;
        }

        // 策略 1：在 avatarRoot 子树中按名称递归查找足部骨骼
        // 限定在子树内搜索，避免场景中其他同名节点的干扰
        Transform lFoot = FindInHierarchy(avatarRoot, config.leftFootBoneName);
        Transform rFoot = FindInHierarchy(avatarRoot, config.rightFootBoneName);

        // 策略 2：回退——如果按名称找不到，取 Calf 骨骼的第一个子节点
        // 在 Bip01 骨骼体系中，Calf 的子节点通常就是 Foot
        if (lFoot == null && rFoot == null)
        {
            lFoot = FindChildFallback(config.boneNames[6]); // boneNames[6] = "Bip01 L Calf"
            rFoot = FindChildFallback(config.boneNames[8]); // boneNames[8] = "Bip01 R Calf"
        }

        // 传入根节点和足部骨骼，初始化补偿器
        rootSolver.Initialize(avatarRoot, lFoot, rFoot,
            config.rootMotionSmoothSpeed, config.rootMotionMaxDrop,
            config.rootMotionHorizontalEnabled,
            config.rootMotionHorizontalSmoothSpeed,
            config.rootMotionMaxHorizontalOffset);
        ApplyRootMotionTrainingMode();
    }

    private void ApplyRootMotionTrainingMode()
    {
        if (rootSolver == null) return;

        bool shouldEnable = config != null && config.rootMotionEnabled &&
            rootSolver.IsInitialized && rootMotionEnabledForTraining;
        if (!shouldEnable && rootSolver.Enabled)
            rootSolver.Reset();
        rootSolver.Enabled = shouldEnable;
    }

    /// <summary>
    /// 在指定根节点下递归查找名称匹配的 Transform。
    /// 深度优先搜索整棵子树。
    /// </summary>
    /// <param name="root">搜索起点</param>
    /// <param name="boneName">目标骨骼名称</param>
    /// <returns>找到的 Transform，未找到返回 null</returns>
    private static Transform FindInHierarchy(Transform root, string boneName)
    {
        if (string.IsNullOrEmpty(boneName)) return null;
        if (root.name == boneName) return root;       // 当前节点匹配
        foreach (Transform child in root)
        {
            var found = FindInHierarchy(child, boneName);   // 递归搜索子节点
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// 回退策略：通过父骨骼名查找其第一个子节点作为 Foot 替代。
    /// 适用于 Foot 骨骼命名不符合预期的模型。
    /// 例如：查找 "Bip01 L Calf" 的第一个子节点，通常就是 "Bip01 L Foot"。
    /// </summary>
    private static Transform FindChildFallback(string parentBoneName)
    {
        if (string.IsNullOrEmpty(parentBoneName)) return null;
        var go = GameObject.Find(parentBoneName);
        if (go == null || go.transform.childCount == 0) return null;
        return go.transform.GetChild(0);    // 取第一个子节点
    }
}

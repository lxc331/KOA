using System;
using System.IO.Ports;
using System.IO;
using UnityEngine;

/// <summary>
/// 业务控制器：负责串口收发、状态编排、稳定性检测与驱动 RotationDriver。
/// UI 通过事件与本类解耦交互。
/// </summary>
public class main : MonoBehaviour
{
    #region 字段与配置  

    public bool anomalyEnable = true; // 控制是否启用异常检测
    public int anomalyBufferSize = 10; // 保存最近几帧数据用于分析
    public float anomalyThreshold = 45.0f; // 角度异常阈值

    const int DEVICE_COUNT = 9; // 设备数量
    SerialParser SerialParser; // 解析器实例
    SerialController serialController; // 串口控制器封装
    string portName = "COM5"; // 当前选择或使用的串口名（默认与 init.json 保持一致，可被配置覆盖）
    string[] systemPorts = new string[0]; // 系统可用串口列表
    int selectedPortIdx = -1; // 选择的串口索引
    // UI 已独立管理 baud/label，这里不再需要
    int selectedBaud = 115200;
    bool isConnected = false;

    // 旋转驱动抽象
    RotationDriver rotationDriver;
    // 驱动阶段的复用缓存，避免每帧分配
    private SerialParser.SensorFrame[] latestFrames;
    private bool[] hasLatest;

    // 默认骨骼名（若 JSON 加载失败仍可正常查找，避免后续数组为 null）
    string[] bone_names = new string[]
    {
        "Bip01 L UpperArm", "Bip01 L Forearm", "Bip01 R UpperArm", "Bip01 R Forearm",
        "Bip01 Spine2", "Bip01 L Thigh", "Bip01 L Calf", "Bip01 R Thigh", "Bip01 R Calf"
    };
    Quaternion[] deviceQuat; // 存储每个设备的四元数数据 
    // 旧偏差与目标数组由 RotationDriver 接管
    float[] yawVals;  // 存储每个设备的yaw值数组
    float[] pitchVals; // 存储每个设备的pitch值数组
    float[] rollVals;  // 存储每个设备的roll值数组
    Quaternion[] transformedSensorQuaternions; // 传感器->Unity 坐标系转换后的当前四元数
    GameObject[] bones; // 存储9个关节游戏对象的引用     
    Quaternion[] restLocalRotations; // 存储每个骨骼的初始局部旋转（绑定姿态）
    // 事件：对外广播状态与数据更新，供 UI 订阅
    public event Action<bool,bool,bool,bool,bool> OnStatusChanged; // connected, hasData, calibrated, driving, stable
    public event Action<int, Vector3> OnEulerUpdated; // deviceId, eulerDeg

    // 平滑参数（RotationDriver 为单一事实来源，此处仅保留速度供 setter 使用）
    [SerializeField] private float smoothSpeed = 10f;  // 平滑速度因子

    // 每个骨骼的局部旋转限制（度数），按 (x,y,z) 顺序指定最小值和最大值
    // 可以在 Inspector 中针对每个骨骼调整以限制手臂/腿的运动范围
    [SerializeField] private Vector3[] minLocalAngles = new Vector3[]
    {
        new Vector3(-60,-60,-60),    // LeftArm 适度收窄防止过度肩部扭转
        new Vector3(0,-30,-45),       // LeftForeArm 肘屈伸最小 ~0° 其它轴降低
        new Vector3(-60,-60,-60),    // RightArm
        new Vector3(0,-30,-45),       // RightForeArm
        new Vector3(-30,-30,-30),    // Spine 适度收敛
        new Vector3(-50,-40,-40),    // LeftUpLeg
        new Vector3(-40,-30,-30),    // LeftLeg
        new Vector3(-50,-40,-40),    // RightUpLeg
        new Vector3(-40,-30,-30)     // RightLeg
    };
    [SerializeField] private Vector3[] maxLocalAngles = new Vector3[]
    {
        new Vector3(60,60,60),       // LeftArm
        new Vector3(145,30,45),      // LeftForeArm 肘屈伸最大 ~145° 旋前/旋后与侧偏收窄
        new Vector3(60,60,60),       // RightArm
        new Vector3(145,30,45),      // RightForeArm
        new Vector3(30,30,30),       // Spine
        new Vector3(50,40,40),       // LeftUpLeg
        new Vector3(40,30,30),       // LeftLeg
        new Vector3(50,40,40),       // RightUpLeg
        new Vector3(40,30,30)        // RightLeg
    };
    // 角度限制总开关由 RotationDriver 维护（通过 UI 事件设置），此处不再持有副本

    // 稳定检测与手动启动
    public int requiredStableFrames = 20; // 连续稳定帧数要求
    public float maxAvgAngularSpeedDeg = 3f; // 每帧允许的最大角速度（度）
    private bool isDriving = false; // 是否开始驱动动画
    private bool[] deviceHasData = new bool[DEVICE_COUNT];
    // 稳定性监控器
    private StabilityMonitor stabilityMonitor;
    public bool requireAllDevices = false; // 是否需要所有设备稳定
    public int minStableDevices = 1; // 至少多少设备稳定即可
    public bool ignoreBonesWithoutObject = true; // 忽略未找到骨骼对象 

    #endregion

    #region UI（IMGUI）字段
    private string connectLabel = "turn on";
    private int startButtonSize = 160;
    private Texture2D btnCircleWaiting, btnCircleReady;
    private bool limitsEnabledUI = false;   // 默认不开启角度限制，由 UI 控制
    private bool smoothingEnabledUI = true; // 默认启用平滑
    private bool twistSwingEnabledUI = false; // 默认使用 Euler 模式
    private bool requireAllDevicesUI = false;
    private int minStableDevicesUI = 1;
    private bool hasStarted = false;
    private bool isCalibratingUI = false;
    private float calibratedTimestamp = -1f;
    private Vector3[] eulerCache = new Vector3[DEVICE_COUNT];
    private string baudText = "115200";
    private GUIStyle tableHeaderStyle;
    private GUIStyle tableCellStyle;
    private const float telemetryDefaultWidth = 620f;
    private const float telemetryDefaultHeight = 300f;
    private const float telemetryMargin = 20f;
    private Rect telemetryWindowRect = new Rect(0f, 0f, telemetryDefaultWidth, telemetryDefaultHeight);
    private Rect controlWindowRect = new Rect(20f, 20f, 320f, 620f);
    private const int telemetryWindowId = 0xC0DE123;
    private const int controlWindowId = 0xC0DE120;
    private bool isResizingTelemetry;
    private bool isResizingControl;
    private Vector2 resizeStartMouse;
    private Rect resizeStartRect;
    private const float minControlWidth = 260f;
    private const float minControlHeight = 620f;
    private const float minTelemetryWidth = 560f;
    private const float minTelemetryHeight = 280f;
    private const float resizeHandleSize = 14f;
    [SerializeField] private string exportDirectory = "";
    [SerializeField] private bool saveEnabled = true;
    private StreamWriter telemetryWriter;
    private string currentLogPath = "";
    private bool telemetryRectInitialized;
    #endregion

    // 业务 API：供 UI 事件调用
    /// <summary>请求连接（端口、波特率）</summary>
    public void OnConnectRequested(string reqPort, int reqBaud)
    {
        portName = reqPort;
        selectedBaud = reqBaud;
        ToggleConnectLogic();
    }
    /// <summary>请求断开连接</summary>
    public void OnDisconnectRequested()
    {
        ToggleConnectLogic();
    }
    /// <summary>请求刷新系统端口列表</summary>
    public void OnRefreshPortsRequested()
    {
        RefreshPortsLogic();
    }
    /// <summary>请求开始驱动（校准并进入驱动态）</summary>
    public void OnBeginDrivingRequested()
    {
        BeginDrivingImpl();
    }
    /// <summary>请求重置（断开、清空、回到绑定姿态）</summary>
    public void OnResetRequested()
    {
        HandleResetRequest(); 
    }
    public bool GetIsStable()
    {
        return stabilityMonitor != null && stabilityMonitor.IsSystemStable(bones, ignoreBonesWithoutObject, deviceHasData, requiredStableFrames, requireAllDevices, minStableDevices);
    }
    public bool GetIsConnected() => isConnected;
    public bool GetHasAnyData()
    {
        for (int i = 0; i < DEVICE_COUNT; i++) if (deviceHasData[i]) return true;
        return false;
    }
    public Quaternion GetDeviceQuaternion(int deviceId)
    {
        if (deviceId < 0 || deviceId >= DEVICE_COUNT) return Quaternion.identity;
        return transformedSensorQuaternions[deviceId];
    }
    public Vector3 GetDeviceEulerDeg(int deviceId)
    {
        if (deviceId < 0 || deviceId >= DEVICE_COUNT) return Vector3.zero;
        return transformedSensorQuaternions[deviceId].eulerAngles;
    }
    // 原轮询状态接口已删除：通过 OnStatusChanged 事件外部可获知 calibrated/driving 状态

    // 刷新端口
    private void RefreshPortsLogic()
    {
        serialController.RefreshPorts(out systemPorts);
        if (systemPorts.Length > 0)
        {
            if (selectedPortIdx < 0 || selectedPortIdx >= systemPorts.Length) selectedPortIdx = 0;
            portName = systemPorts[selectedPortIdx];
        }
        else
        {
            selectedPortIdx = -1;
            portName = "";
        }
    }
    // 连接/断开
    private void ToggleConnectLogic()
    {
        if (!isConnected)
        {
            if (systemPorts.Length > 0 && selectedPortIdx >= 0 && selectedPortIdx < systemPorts.Length)
            {
                portName = systemPorts[selectedPortIdx];
            }
            bool ok = serialController.Connect(portName, selectedBaud);
            isConnected = ok;
            if (isConnected && saveEnabled)
            {
                OpenTelemetryLog();
            }
            OnStatusChanged?.Invoke(isConnected,
                GetHasAnyData(),
                rotationDriver != null && rotationDriver.IsCalibrated,
                isDriving,
                GetIsStable());
        }
        else
        {
            isConnected = false;
            serialController.Disconnect();
            CloseTelemetryLog();
            OnStatusChanged?.Invoke(false, false, false, false, false);
        }
    }
    private void BeginDrivingImpl()
    {
        rotationDriver.Calibrate(bones, transformedSensorQuaternions);
        isDriving = true;
        OnStatusChanged?.Invoke(isConnected,
            GetHasAnyData(),
            rotationDriver != null && rotationDriver.IsCalibrated,
            isDriving,
            GetIsStable());
    }
    private void HandleResetRequest()
    {
        // 停止驱动并清理状态
        isDriving = false;
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            deviceHasData[i] = false;
            transformedSensorQuaternions[i] = Quaternion.identity;
        }
        // 清空解析器缓冲与队列
        if (SerialParser != null)
        {
            SerialParser.Reset();
        }
        // 关闭串口连接，回到待机（turn on）状态
        if (serialController != null)
        {
            serialController.Disconnect();
        }
        CloseTelemetryLog();
        isConnected = false;
        // 恢复骨骼到绑定姿态并清除校准
        if (rotationDriver != null)
        {
            rotationDriver.ResetToRestPose(bones, restLocalRotations);
        }
        // 稳定性监控重置
        stabilityMonitor = new StabilityMonitor(DEVICE_COUNT);
        // 广播最新状态：未校准、未驱动
        OnStatusChanged?.Invoke(isConnected,
            GetHasAnyData(),
            rotationDriver != null && rotationDriver.IsCalibrated,
            isDriving,
            GetIsStable());
    }
    // 串口回调：SerialParser 通过事件将解析到的 (deviceId, quat) 派发到这里处理
    private void HandleSerialQuaternion(int deviceId, Quaternion currentQuaternion)
    {
        if (deviceId < 0 || deviceId >= DEVICE_COUNT) return;

        // 更新四元数并直接计算三个角度（移除调试与中间数组）
        deviceQuat[deviceId] = currentQuaternion;
        var e = currentQuaternion.eulerAngles;
        yawVals[deviceId] = e.z;
        pitchVals[deviceId] = e.y;
        rollVals[deviceId] = e.x;
        OnEulerUpdated?.Invoke(deviceId, new Vector3(rollVals[deviceId], pitchVals[deviceId], yawVals[deviceId]));
        LogFrame(deviceId, currentQuaternion, e);
    }

    // #region Unity

    // 初始化纹理与本地端口列表（UI相关）
    private void Awake()
    {
        btnCircleWaiting = MakeCircleTexture(startButtonSize, new Color(0.25f, 0.25f, 0.25f, 0.75f), new Color(0.55f, 0.55f, 0.55f, 1f));
        btnCircleReady = MakeCircleTexture(startButtonSize, new Color(0.12f, 0.55f, 0.12f, 0.85f), new Color(0.25f, 0.85f, 0.25f, 1f));
        RefreshPortsLocal();
        smoothingEnabledUI = true;
        limitsEnabledUI = false;
        twistSwingEnabledUI = false;
        if (string.IsNullOrEmpty(exportDirectory))
        {
            exportDirectory = Directory.GetCurrentDirectory();
        }
        if (!telemetryRectInitialized)
        {
            float w = Mathf.Max(minTelemetryWidth, telemetryDefaultWidth);
            float h = Mathf.Max(minTelemetryHeight, telemetryDefaultHeight);
            float x = Mathf.Max(telemetryMargin, Screen.width - w - telemetryMargin);
            float y = Mathf.Max(telemetryMargin, Screen.height - h - telemetryMargin);
            telemetryWindowRect = new Rect(x, y, w, h);
            telemetryRectInitialized = true;
        }
    }

    // 初始化：退出全屏、查找骨骼对象、初始化子系统
    void Start()
    {
        Screen.fullScreen = false;  //退出全屏  

        // 初始化解析与队列管理
        SerialParser = new SerialParser();
        // 初始化串口控制器（后台线程读取，无 MonoBehaviour 依赖）
        serialController = new SerialController(SerialParser);

        // 初始化数组
        deviceQuat = new Quaternion[DEVICE_COUNT];
        yawVals = new float[DEVICE_COUNT];
        pitchVals = new float[DEVICE_COUNT];
        rollVals = new float[DEVICE_COUNT];
        transformedSensorQuaternions = new Quaternion[DEVICE_COUNT];
        // 初始化为 identity，避免未接收到数据时使用 (0,0,0,0)
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            deviceQuat[i] = Quaternion.identity;
            transformedSensorQuaternions[i] = Quaternion.identity;
        }
        // 初始化旋转驱动器（默认开启平滑，关闭 Twist+Swing，改用 Euler；去抖阈值 1°）
        rotationDriver = new RotationDriver(DEVICE_COUNT, true, smoothSpeed, 1f, false);
        bones = new GameObject[DEVICE_COUNT];
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            deviceHasData[i] = false;
        }
        // 使用脚本中声明的默认骨骼名
        if (bone_names == null || bone_names.Length != DEVICE_COUNT)
        {
            bone_names = new string[]
            {
               "Bip01 L UpperArm", "Bip01 L Forearm", "Bip01 R UpperArm", "Bip01 R Forearm",
               "Bip01 Spine2", "Bip01 L Thigh", "Bip01 L Calf", "Bip01 R Thigh", "Bip01 R Calf"
            };
        }

        // 依据（可能来自 JSON）更新后的骨骼名查找骨骼对象
        for (int i = 0; i < bone_names.Length; i++)
        {
            bones[i] = GameObject.Find(bone_names[i]);
        }

        systemPorts = SerialPort.GetPortNames();
        if (systemPorts.Length > 0)
        {
            // 默认选择第一项；若之前指定的端口存在则保持
            int idx = Array.IndexOf(systemPorts, portName);
            if (idx < 0) idx = 0;
            selectedPortIdx = idx;
            portName = systemPorts[selectedPortIdx]; // 端口策略固定自动
        }
        else
        {
            selectedPortIdx = -1;
            portName = "";
        }

        // 配置异常检测
        if (anomalyEnable)
        {
            var detector = new AnomalyDetector
            {
                HistorySize = anomalyBufferSize,
                AngleThresholdDeg = anomalyThreshold
            };
            SerialParser.Detector = detector;
        }

        restLocalRotations = new Quaternion[DEVICE_COUNT];
        // 夹角限制由 RotationDriver 控制

        // 捕获绑定/初始局部旋转作为约束参考
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            if (bones[i] != null)
                restLocalRotations[i] = bones[i].transform.localRotation;
            else
                restLocalRotations[i] = Quaternion.identity;
        }
        // 将约束与绑定姿态缓存到驱动器，运行期复用
        rotationDriver.SetConstraints(minLocalAngles, maxLocalAngles, restLocalRotations);
        // 初始化稳定性监控器
        stabilityMonitor = new StabilityMonitor(DEVICE_COUNT);
        // 初始化可复用的帧缓存，避免 Update 中 per-frame 分配
        latestFrames = new SerialParser.SensorFrame[DEVICE_COUNT];
        hasLatest = new bool[DEVICE_COUNT];

        // 本地 UI 订阅核心事件消息
        OnStatusChanged += HandleStatusChanged;
        OnEulerUpdated += HandleEulerUpdated;
    }

    // 每帧更新：应用传感器数据与初始偏差到骨骼
    void Update()
    {
        // 从 SerialParser 的队列主动取数据（主线程），保证后续的转换与动画驱动都在 Unity 主线程执行
        if (SerialParser != null)
        {
            int dequeuedDeviceId;
            Quaternion dequeuedQ;
            while (SerialParser.TryDequeue(out dequeuedDeviceId, out dequeuedQ))
            {
                HandleSerialQuaternion(dequeuedDeviceId, dequeuedQ);
                deviceHasData[dequeuedDeviceId] = true;
            }
        }
        // 坐标系转换：按关节类型应用对应方法
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            if (!deviceHasData[i]) { transformedSensorQuaternions[i] = Quaternion.identity; continue; }
            transformedSensorQuaternions[i] = RotationDriver.MapSensorToUnity(i, deviceQuat[i]);
        }
        // 稳定性检测：统计每设备的角速度是否连续低于阈值
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            var curr = transformedSensorQuaternions[i];
            stabilityMonitor.UpdateDevice(i, curr, deviceHasData[i], maxAvgAngularSpeedDeg);
        }

        // 若已连接且已有数据，但尚未校准，则进行一次预校准
        if (isConnected && GetHasAnyData() && rotationDriver != null && !rotationDriver.IsCalibrated)
        {
            rotationDriver.Calibrate(bones, transformedSensorQuaternions);
        }

        // 广播状态（每帧）：连接、是否有数据、是否校准、是否在驱动、稳定性
        OnStatusChanged?.Invoke(isConnected,
            GetHasAnyData(),
            rotationDriver != null && rotationDriver.IsCalibrated,
            isDriving,
            GetIsStable());

        if (isDriving)
        {
            DateTime? newestTime = null;
            DateTime? oldestTime = null;
            int latestCount = 0;
            for (int i = 0; i < DEVICE_COUNT; i++) hasLatest[i] = false;
            for (int i = 0; i < DEVICE_COUNT; i++)
            {
                if (SerialParser.TryGetLatestFrame(i, out latestFrames[i]))
                {
                    hasLatest[i] = true;
                    latestCount++;
                    if (!newestTime.HasValue || latestFrames[i].Timestamp > newestTime.Value)
                        newestTime = latestFrames[i].Timestamp;
                    if (!oldestTime.HasValue || latestFrames[i].Timestamp < oldestTime.Value)
                        oldestTime = latestFrames[i].Timestamp;
                }
            }

            if (latestCount == 0 || !newestTime.HasValue) return;

            DateTime targetTime = newestTime.Value;
            int devicesApplied = 0;
            for (int i = 0; i < DEVICE_COUNT; i++)
            {
                if (!hasLatest[i])
                {
                    if (requireAllDevices)
                    {
                        // 缺失数据且要求所有设备同步，直接跳过驱动
                        return;
                    }
                    if (deviceHasData[i])
                    {
                        transformedSensorQuaternions[i] = RotationDriver.MapSensorToUnity(i, deviceQuat[i]);
                        devicesApplied++;
                    }
                    continue;
                }

                SerialParser.SensorFrame frame = latestFrames[i];
                if (frame.Timestamp != targetTime)
                {
                    if (!SerialParser.TryGetInterpolatedFrame(i, targetTime, out frame))
                    {
                        // 插值失败则使用最新帧
                        frame = latestFrames[i];
                    }
                }
                transformedSensorQuaternions[i] = RotationDriver.MapSensorToUnity(i, frame.Q);
                devicesApplied++;
            }

            if (devicesApplied == 0)
            {
                return;
            }

            if (!rotationDriver.IsCalibrated)
            {
                rotationDriver.Calibrate(bones, transformedSensorQuaternions);
            }
            rotationDriver.UpdateTargets(transformedSensorQuaternions);
        }
    }

    // 在 LateUpdate 写入 transform，确保在动画之后执行
    void LateUpdate()
    {
        if (!isDriving) return;
        // 由 RotationDriver 内部的 limitsEnabled 与 constraintsReady 控制限幅
        rotationDriver.Apply(bones);
    }

    // 应用退出时关闭串口
    private void OnApplicationQuit()
    {
        // 关闭串口连接
        if (serialController != null)
        {
            serialController.Disconnect();
        }
        CloseTelemetryLog();
    }
    // 组件禁用时关闭串口
    private void OnDisable()
    {
        if (serialController != null)
        {
            serialController.Disconnect();
        }
        CloseTelemetryLog();
    }
    // #endregion

    // #region UI（IMGUI）
    private void OnGUI()
    {
        DrawAll();
    }

    private void DrawAll()
    {
        var size = startButtonSize;
        controlWindowRect = GUI.Window(controlWindowId, controlWindowRect, DrawControlWindow, "Control Interface");
        telemetryWindowRect = GUI.Window(telemetryWindowId, telemetryWindowRect, DrawTelemetryWindow, "Sensor Telemetry");
        DrawCenterStart(size);
    }

    private void EnsureTableStyles()
    {
        if (tableHeaderStyle == null)
        {
            tableHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
        }
        if (tableCellStyle == null)
        {
            tableCellStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
        }
    }

    private void DrawControlWindow(int id)
    {
        float width = controlWindowRect.width;
        // 校准过程中禁用连接按钮
        GUI.enabled = !isCalibratingUI;
        if (GUI.Button(new Rect(30, 40, 80, 40), connectLabel))
        {
            if (connectLabel == "turn on")
            {
                selectedBaud = ParseBaud(baudText, 115200);
                OnConnectRequested(portName, selectedBaud);
                isCalibratingUI = true;
                hasStarted = false;
            }
            else
            {
                OnDisconnectRequested();
                hasStarted = false;
                isCalibratingUI = false;
            }
        }
        GUI.enabled = true;

        int height = 20;
        if (systemPorts != null && systemPorts.Length > 0)
        {
            GUI.Label(new Rect(20, 20, 40, 20), "port");
            selectedPortIdx = GUI.SelectionGrid(new Rect(60, 20, 110, height), selectedPortIdx < 0 ? 0 : selectedPortIdx, systemPorts, 1);
            if (selectedPortIdx >= 0 && selectedPortIdx < systemPorts.Length) portName = systemPorts[selectedPortIdx];
        }
        else
        {
            GUI.Label(new Rect(20, 20, 40, 20), "port");
            portName = GUI.TextField(new Rect(60, 20, 110, 20), portName ?? "");
        }

        if (GUI.Button(new Rect(20, 90, 120, 22), "刷新端口")) { OnRefreshPortsRequested(); RefreshPortsLocal(); }
        if (GUI.Button(new Rect(150, 90, 70, 22), "重置"))
        {
            hasStarted = false;
            calibratedTimestamp = -1f;
            OnResetRequested();
        }
        GUI.Label(new Rect(20, 115, 100, 20), "baud");
        baudText = GUI.TextField(new Rect(60, 115, 110, 20), baudText, 10);

        GUI.Label(new Rect(20, 140, 200, 20), $"策略: auto");
        GUI.Label(new Rect(20, 165, 200, 20), $"端口: {portName}");

        bool newLimits = GUI.Toggle(new Rect(20, 190, 200, 24), limitsEnabledUI, "角度限制");
        if (newLimits != limitsEnabledUI)
        {
            limitsEnabledUI = newLimits;
            if (rotationDriver != null) rotationDriver.SetLimitsEnabled(limitsEnabledUI);
        }

        GUI.enabled = limitsEnabledUI;
        GUI.Label(new Rect(20, 215, 200, 20), "限幅方式:");
        string currentModeLabel = twistSwingEnabledUI ? "Twist+Swing" : "Euler";
        if (GUI.Button(new Rect(20, 240, 210, 24), currentModeLabel))
        {
            twistSwingEnabledUI = !twistSwingEnabledUI;
            if (rotationDriver != null) rotationDriver.SetTwistSwing(twistSwingEnabledUI);
        }
        GUI.enabled = true;

        bool newSmoothing = GUI.Toggle(new Rect(20, 270, 200, 24), smoothingEnabledUI, "平滑(Slerp)");
        if (newSmoothing != smoothingEnabledUI)
        {
            smoothingEnabledUI = newSmoothing;
            if (rotationDriver != null) rotationDriver.SetSmoothing(smoothingEnabledUI, smoothSpeed);
        }

        bool newRequireAll = GUI.Toggle(new Rect(20, 330, 200, 24), requireAllDevicesUI, "要求所有设备稳定");
        if (newRequireAll != requireAllDevicesUI)
        {
            requireAllDevicesUI = newRequireAll;
            requireAllDevices = requireAllDevicesUI;
        }

        GUI.Label(new Rect(20, 360, 160, 20), $"最少稳定设备: {minStableDevicesUI}");
        string minStableStr = GUI.TextField(new Rect(150, 360, 50, 20), minStableDevicesUI.ToString(), 2);
        if (int.TryParse(minStableStr, out var parsed) && parsed != minStableDevicesUI)
        {
            minStableDevicesUI = Mathf.Clamp(parsed, 1, DEVICE_COUNT);
            minStableDevices = minStableDevicesUI;
        }

        saveEnabled = GUI.Toggle(new Rect(20, 390, width - 40, 20), saveEnabled, "保存到文件");
        GUI.Label(new Rect(20, 414, width - 40, 20), "数据目录:");
        exportDirectory = GUI.TextField(new Rect(20, 436, width - 40, 20), exportDirectory ?? "");
        if (!saveEnabled && telemetryWriter != null)
        {
            CloseTelemetryLog();
        }
        if (saveEnabled && telemetryWriter == null && isConnected)
        {
            OpenTelemetryLog();
        }

        GUI.Label(new Rect(20, 468, 260, 20), telemetryWriter != null ? $"记录中: {Path.GetFileName(currentLogPath)}" : "未记录");

        GUI.Label(new Rect(20, 495, 200, 20), $"稳定: {(GetIsStable() ? "OK" : "等待")}");

        DrawResizeHandle(ref controlWindowRect, ref isResizingControl, minControlWidth, minControlHeight);
        GUI.DragWindow(new Rect(0, 0, controlWindowRect.width, 22f));
    }

    private void DrawTelemetryWindow(int id)
    {
        EnsureTableStyles();
    string[] headers = { "number", "q0", "q1", "q2", "q3", "yaw", "pitch", "roll" };
    float[] widths = { 70f, 70f, 70f, 70f, 70f, 70f, 70f, 70f };
    float headerHeight = 20f;
    float headerY = 24f;
    float rowHeight = 24f;
    float startX = 8f;
    float startY = headerY + headerHeight + 4f;

        // 标题行
        float x = startX;
        for (int i = 0; i < headers.Length; i++)
        {
            GUI.Label(new Rect(x, headerY, widths[i], headerHeight), headers[i], tableHeaderStyle);
            x += widths[i];
        }

        // 数据行
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            float y = startY + rowHeight * i;
            x = startX;

            Quaternion q = transformedSensorQuaternions != null && transformedSensorQuaternions.Length > i
                ? transformedSensorQuaternions[i]
                : Quaternion.identity;
            Vector3 euler = q.eulerAngles;

            GUI.Label(new Rect(x, y, widths[0], rowHeight), $"0x{i + 1:00}", tableCellStyle); x += widths[0];
            GUI.Label(new Rect(x, y, widths[1], rowHeight), q.x.ToString("F3"), tableCellStyle); x += widths[1];
            GUI.Label(new Rect(x, y, widths[2], rowHeight), q.y.ToString("F3"), tableCellStyle); x += widths[2];
            GUI.Label(new Rect(x, y, widths[3], rowHeight), q.z.ToString("F3"), tableCellStyle); x += widths[3];
            GUI.Label(new Rect(x, y, widths[4], rowHeight), q.w.ToString("F3"), tableCellStyle); x += widths[4];
            GUI.Label(new Rect(x, y, widths[5], rowHeight), euler.z.ToString("F1"), tableCellStyle); x += widths[5];
            GUI.Label(new Rect(x, y, widths[6], rowHeight), euler.y.ToString("F1"), tableCellStyle); x += widths[6];
            GUI.Label(new Rect(x, y, widths[7], rowHeight), euler.x.ToString("F1"), tableCellStyle);
        }
        DrawResizeHandle(ref telemetryWindowRect, ref isResizingTelemetry, minTelemetryWidth, minTelemetryHeight);
        GUI.DragWindow(new Rect(0, 0, telemetryWindowRect.width, 22f));
    }

    private void DrawResizeHandle(ref Rect windowRect, ref bool isResizing, float minW, float minH)
    {
        Rect handleRect = new Rect(windowRect.width - resizeHandleSize, windowRect.height - resizeHandleSize, resizeHandleSize, resizeHandleSize);
        GUI.Box(handleRect, "");
        Event e = Event.current;
        if (e.type == EventType.MouseDown && handleRect.Contains(e.mousePosition))
        {
            isResizing = true;
            resizeStartMouse = e.mousePosition;
            resizeStartRect = windowRect;
            e.Use();
        }
        if (e.type == EventType.MouseDrag && isResizing)
        {
            Vector2 delta = e.mousePosition - resizeStartMouse;
            windowRect.width = Mathf.Max(minW, resizeStartRect.width + delta.x);
            windowRect.height = Mathf.Max(minH, resizeStartRect.height + delta.y);
            e.Use();
        }
        if (e.type == EventType.MouseUp && isResizing)
        {
            isResizing = false;
            e.Use();
        }
    }

    private void SaveTelemetryToFile()
    {
        // 兼容旧按钮调用：现在转为持续记录，不再单次导出
        if (saveEnabled && telemetryWriter == null)
        {
            OpenTelemetryLog();
        }
    }

    private void OpenTelemetryLog()
    {
        try
        {
            string dir = string.IsNullOrEmpty(exportDirectory) ? Directory.GetCurrentDirectory() : exportDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string fileName = DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
            currentLogPath = Path.Combine(dir, fileName);
            telemetryWriter = new StreamWriter(currentLogPath, false);
            telemetryWriter.AutoFlush = true;
            telemetryWriter.WriteLine("timestamp\tdevice\tq0\tq1\tq2\tq3\tyaw\tpitch\troll");
            Debug.Log($"Telemetry logging started: {currentLogPath}");
        }
        catch (Exception ex)
        {
            telemetryWriter = null;
            currentLogPath = "";
            Debug.LogError($"Failed to open telemetry log: {ex.Message}");
        }
    }

    private void CloseTelemetryLog()
    {
        try
        {
            if (telemetryWriter != null)
            {
                telemetryWriter.Flush();
                telemetryWriter.Close();
                telemetryWriter.Dispose();
            }
        }
        catch (Exception) { }
        telemetryWriter = null;
        currentLogPath = "";
    }

    private void LogFrame(int deviceId, Quaternion q, Vector3 euler)
    {
        if (!saveEnabled || telemetryWriter == null) return;
        string line = $"{DateTime.Now:O}\t0x{deviceId + 1:00}\t{q.x:F4}\t{q.y:F4}\t{q.z:F4}\t{q.w:F4}\t{euler.z:F2}\t{euler.y:F2}\t{euler.x:F2}";
        telemetryWriter.WriteLine(line);
    }

    private void DrawCenterStart(int size)
    {
        bool connected = GetIsConnected();
        bool hasData = GetHasAnyData();
        bool stableReady = GetIsStable();
        bool calibrated = rotationDriver != null && rotationDriver.IsCalibrated;
        bool driving = isDriving;

        if (hasStarted || driving)
            return;

        string label;
        if (!connected)
            label = "未连接";
        else if (!hasData)
            label = "等待数据";
        else if (isCalibratingUI && !calibrated)
            label = "校准中";
        else
            label = stableReady ? "点击开始" : "等待稳定";
        var tex = (connected && hasData && !isCalibratingUI && calibrated && stableReady) ? btnCircleReady : btnCircleWaiting;
        var centerRect = new Rect((Screen.width - size) / 2, (Screen.height - size) / 2, size, size);
        if (tex != null) GUI.DrawTexture(centerRect, tex, ScaleMode.StretchToFill, true);
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(size * 0.2f), fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        GUI.Label(centerRect, label, style);
        if (connected && hasData && calibrated && stableReady && GUI.Button(centerRect, GUIContent.none, GUIStyle.none))
        {
            hasStarted = true;
            isCalibratingUI = false;
            OnBeginDrivingRequested();
        }

        if (connected && hasData && calibrated && !hasStarted && !driving && calibratedTimestamp > 0f)
        {
            if (Time.time - calibratedTimestamp >= 2f)
            {
                hasStarted = true;
                isCalibratingUI = false;
                OnBeginDrivingRequested();
            }
        }
    }

    // 状态与欧拉角事件处理（更新 UI 本地状态）
    private void HandleStatusChanged(bool connected, bool hasData, bool calibrated, bool driving, bool stable)
    {
        isConnected = connected;
        connectLabel = isConnected ? "turn off" : "turn on";
        bool wasCalibrated = rotationDriver != null && rotationDriver.IsCalibrated;
        isDriving = driving;
        // UI：校准中状态
        isCalibratingUI = isConnected && !calibrated;
        if (isConnected && calibrated && !wasCalibrated)
        {
            calibratedTimestamp = Time.time;
        }
    }

    private void HandleEulerUpdated(int deviceId, Vector3 eulerDeg)
    {
        if (deviceId >= 0 && deviceId < eulerCache.Length)
            eulerCache[deviceId] = eulerDeg;
    }

    private void RefreshPortsLocal()
    {
        try
        {
            systemPorts = SerialPort.GetPortNames();
            if (systemPorts.Length > 0)
            {
                if (selectedPortIdx < 0 || selectedPortIdx >= systemPorts.Length) selectedPortIdx = 0;
                portName = systemPorts[selectedPortIdx];
            }
            else
            {
                selectedPortIdx = -1;
                portName = "";
            }
        }
        catch (Exception)
        {
            systemPorts = new string[0];
            selectedPortIdx = -1;
        }
    }

    private static int ParseBaud(string text, int fallback)
    {
        return int.TryParse(text, out var b) && b > 0 ? b : fallback;
    }

    private Texture2D MakeCircleTexture(int size, Color fill, Color rim)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float r = size * 0.5f - 1f;
        float cx = r + 1f, cy = r + 1f;
        float rimWidth = Mathf.Max(2f, size * 0.06f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d <= r)
                {
                    Color c = fill;
                    if (d > r - rimWidth)
                    {
                        float t = Mathf.InverseLerp(r, r - rimWidth, d);
                        c = Color.Lerp(rim, fill, t);
                    }
                    tex.SetPixel(x, y, c);
                }
                else
                {
                    tex.SetPixel(x, y, new Color(0, 0, 0, 0));
                }
            }
        }
        tex.Apply(false, true);
        return tex;
    }
    // #endregion
}
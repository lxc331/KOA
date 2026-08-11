using System;                     // 引入基础系统功能，如异常处理、时间等
using System.IO.Ports;            // 引入串口通信相关类
using System.IO;                  // 引入文件读写相关类
using UnityEngine;                // 引入Unity引擎核心功能

/// <summary>
/// 业务控制器：负责串口收发、状态编排、稳定性检测与驱动 RotationDriver。
/// UI 通过事件与本类解耦交互。
/// </summary>
public class main : MonoBehaviour  // 继承MonoBehaviour，使其可以挂载到Unity游戏对象上
{
    #region 字段与配置  

    // 公开字段，可在Unity编辑器中调整
    public bool anomalyEnable = true;                // 控制是否启用异常检测（检测传感器数据跳变）
    public int anomalyBufferSize = 10;               // 保存最近几帧数据用于分析异常
    public float anomalyThreshold = 45.0f;            // 角度异常阈值，超过此值视为异常跳变

    const int DEVICE_COUNT = 9;                       // 设备数量（传感器数量），固定为9个
    SerialParser SerialParser;                         // 解析器实例，负责从串口数据流中解析出传感器四元数
    SerialController serialController;                 // 串口控制器封装，管理串口的打开、关闭和数据读取（运行在后台线程）
    string portName = "COM9";                          // 当前选择或使用的串口名（默认与 init.json 保持一致，可被配置覆盖）
    string[] systemPorts = new string[0];              // 系统可用串口列表
    int selectedPortIdx = -1;                           // 当前选中的串口索引（用于UI下拉列表）
    // UI 已独立管理 baud/label，这里不再需要
    int selectedBaud = 115200;                          // 当前选择的波特率
    bool isConnected = false;                           // 串口是否已连接

    // 旋转驱动抽象
    RotationDriver rotationDriver;                      // 旋转驱动器，负责将传感器四元数应用到骨骼，并处理平滑、限制等
    private Transform avatarRoot;                       // 角色根节点，用于补偿初始场景朝向（使传感器姿态与角色初始朝向对齐）
    private string avatarRootName = "renwu";            // 角色根节点的名称，用于在场景中查找
    private Quaternion rootFacingOffset = Quaternion.identity; // 根节点的初始朝向偏移，用于坐标系补偿
    // 驱动阶段的复用缓存，避免每帧分配新数组
    private SerialParser.SensorFrame[] latestFrames;    // 存储每个设备的最新帧数据（用于插值）
    private bool[] hasLatest;                            // 标记每个设备是否有最新帧

    // 默认骨骼名（若 JSON 加载失败仍可正常查找，避免后续数组为 null）
    // string[] bone_names = new string[]
    // {
    //     "LeftArm", "LeftForeArm", "RightArm", "RightForeArm",
    //     "Spine", "LeftUpLeg", "LeftLeg", "RightUpLeg", "RightLeg"
    // };

    // 实际的骨骼名称（与模型中的Transform名称对应），共9个
    string[] bone_names = new string[]
    {
        "Bip01 L UpperArm", "Bip01 L Forearm", "Bip01 R UpperArm", "Bip01 R Forearm",
        "Bip01 Spine2", "Bip01 L Thigh", "Bip01 L Calf", "Bip01 R Thigh", "Bip01 R Calf"
    };

    Quaternion[] deviceQuat;                             // 存储每个设备的原始四元数数据（从串口解析后直接存储）
    // 旧偏差与目标数组由 RotationDriver 接管
    float[] yawVals;                                     // 存储每个设备的yaw值（偏航角）
    float[] pitchVals;                                   // 存储每个设备的pitch值（俯仰角）
    float[] rollVals;                                    // 存储每个设备的roll值（横滚角）
    Quaternion[] transformedSensorQuaternions;           // 传感器->Unity 坐标系转换后的当前四元数（最终用于驱动骨骼）
    GameObject[] bones;                                   // 存储9个关节游戏对象的引用，通过名称查找得到
    Quaternion[] restLocalRotations;                      // 存储每个骨骼的初始局部旋转（绑定姿态），用于约束和复位
    // 事件：对外广播状态与数据更新，供 UI 订阅
    public event Action<bool, bool, bool, bool, bool> OnStatusChanged; // 连接状态、是否有数据、是否校准、是否驱动、是否稳定
    public event Action<int, Vector3> OnEulerUpdated;               // 设备ID、欧拉角（度），用于UI更新

    // 平滑参数（RotationDriver 为单一事实来源，此处仅保留速度供 setter 使用）
    [SerializeField] private float smoothSpeed = 10f;   // 平滑速度因子，值越大平滑越快（接近目标越快）

    // 每个骨骼的局部旋转限制（度数），按 (x,y,z) 顺序指定最小值和最大值
    // 可以在 Inspector 中针对每个骨骼调整以限制手臂/腿的运动范围
    [SerializeField]
    private Vector3[] minLocalAngles = new Vector3[]
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
    [SerializeField]
    private Vector3[] maxLocalAngles = new Vector3[]
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
    public int requiredStableFrames = 20;                // 连续稳定帧数要求（即多少帧内角速度低于阈值才算稳定）
    public float maxAvgAngularSpeedDeg = 3f;             // 每帧允许的最大角速度（度），超过此值认为不稳定
    private bool isDriving = false;                       // 是否开始驱动动画（即已经校准并进入运动模式）
    private bool[] deviceHasData = new bool[DEVICE_COUNT]; // 标记每个设备是否收到过有效数据
    // 稳定性监控器
    private StabilityMonitor stabilityMonitor;            // 稳定性监控器实例，负责计算每个设备是否稳定
    public bool requireAllDevices = false;                // 是否需要所有设备稳定才能开始驱动（可由UI修改）
    public int minStableDevices = 1;                       // 至少多少设备稳定即可开始驱动（当requireAllDevices为false时生效）
    public bool ignoreBonesWithoutObject = false;          // 是否忽略未找到骨骼对象的设备（在稳定性检测中忽略它们）

    #endregion

    #region UI（IMGUI）字段
    // UI相关的私有字段
    private string connectLabel = "turn on";               // 连接按钮上的文字
    private int startButtonSize = 160;                      // 中央启动按钮的尺寸
    private Texture2D btnCircleWaiting, btnCircleReady;     // 中央按钮的纹理（等待状态和就绪状态）
    private bool limitsEnabledUI = false;                    // UI上角度限制开关的状态
    private bool smoothingEnabledUI = true;                  // UI上平滑开关的状态
    private bool twistSwingEnabledUI = false;                // UI上Twist+Swing模式开关（默认使用Euler模式）
    private bool requireAllDevicesUI = false;                // UI上“要求所有设备稳定”开关的状态
    private int minStableDevicesUI = 1;                       // UI上“最少稳定设备数”输入框的值
    private bool hasStarted = false;                          // 标记是否已经点击了中央启动按钮
    private bool isCalibratingUI = false;                     // UI上是否处于校准中状态（用于显示“校准中”）
    private float calibratedTimestamp = -1f;                  // 记录校准完成的时间戳（用于自动启动）
    private Vector3[] eulerCache = new Vector3[DEVICE_COUNT]; // 缓存每个设备的最新欧拉角，供UI显示
    private string baudText = "115200";                       // 波特率输入框的文本
    private GUIStyle tableHeaderStyle;                         // 表格标题的GUIStyle
    private GUIStyle tableCellStyle;                           // 表格单元格的GUIStyle
    private const float telemetryDefaultWidth = 620f;          // 遥测窗口默认宽度
    private const float telemetryDefaultHeight = 300f;         // 遥测窗口默认高度
    private const float telemetryMargin = 20f;                 // 遥测窗口距屏幕边缘的边距
    private Rect telemetryWindowRect = new Rect(0f, 0f, telemetryDefaultWidth, telemetryDefaultHeight); // 遥测窗口矩形
    private Rect controlWindowRect = new Rect(20f, 20f, 320f, 620f); // 控制窗口矩形
    private const int telemetryWindowId = 0xC0DE123;           // 遥测窗口的唯一ID
    private const int controlWindowId = 0xC0DE120;             // 控制窗口的唯一ID
    private bool isResizingTelemetry;                           // 是否正在调整遥测窗口大小
    private bool isResizingControl;                             // 是否正在调整控制窗口大小
    private Vector2 resizeStartMouse;                           // 开始调整大小时的鼠标位置
    private Rect resizeStartRect;                               // 开始调整大小时的窗口矩形
    private const float minControlWidth = 260f;                 // 控制窗口最小宽度
    private const float minControlHeight = 620f;                // 控制窗口最小高度
    private const float minTelemetryWidth = 560f;               // 遥测窗口最小宽度
    private const float minTelemetryHeight = 280f;              // 遥测窗口最小高度
    private const float resizeHandleSize = 14f;                 // 调整大小手柄的大小
    [SerializeField] private string exportDirectory = "";       // 数据导出目录
    [SerializeField] private bool saveEnabled = true;           // 是否启用数据保存
    private StreamWriter telemetryWriter;                        // 遥测数据写入器
    private string currentLogPath = "";                          // 当前日志文件的完整路径
    private bool telemetryRectInitialized;                       // 标记遥测窗口矩形是否已初始化
    // 端口下拉选择 UI 状态
    private bool portDropdownOpen = false;                       // 端口下拉列表是否展开
    private Vector2 portDropdownScroll = Vector2.zero;           // 端口下拉列表的滚动位置
    private const float portItemHeight = 20f;                    // 下拉列表中每个端口项的高度
    #endregion

    // 业务 API：供 UI 事件调用
    /// <summary>请求连接（端口、波特率）</summary>
    public void OnConnectRequested(string reqPort, int reqBaud)
    {
        portName = reqPort;          // 设置端口名
        selectedBaud = reqBaud;      // 设置波特率
        ToggleConnectLogic();         // 执行连接/断开逻辑（根据当前连接状态切换）
    }
    /// <summary>请求断开连接</summary>
    public void OnDisconnectRequested()
    {
        ToggleConnectLogic();         // 执行连接/断开逻辑（会断开）
    }
    /// <summary>请求刷新系统端口列表</summary>
    public void OnRefreshPortsRequested()
    {
        RefreshPortsLogic();          // 刷新端口列表
    }
    /// <summary>请求开始驱动（校准并进入驱动态）</summary>
    public void OnBeginDrivingRequested()
    {
        BeginDrivingImpl();           // 执行开始驱动的具体逻辑
    }
    /// <summary>请求重置（断开、清空、回到绑定姿态）</summary>
    public void OnResetRequested()
    {
        HandleResetRequest();          // 执行重置逻辑
    }
    // 获取系统是否稳定（用于UI显示）
    public bool GetIsStable()
    {
        // 通过稳定性监控器判断系统是否稳定
        return stabilityMonitor != null && stabilityMonitor.IsSystemStable(bones, ignoreBonesWithoutObject, deviceHasData, requiredStableFrames, requireAllDevices, minStableDevices);
    }
    public bool GetIsConnected() => isConnected;                      // 返回连接状态
    public bool GetHasAnyData()                                        // 返回是否有至少一个设备收到过数据
    {
        for (int i = 0; i < DEVICE_COUNT; i++) if (deviceHasData[i]) return true;
        return false;
    }
    public Quaternion GetDeviceQuaternion(int deviceId)               // 返回指定设备的转换后四元数
    {
        if (deviceId < 0 || deviceId >= DEVICE_COUNT) return Quaternion.identity;
        return transformedSensorQuaternions[deviceId];
    }
    public Vector3 GetDeviceEulerDeg(int deviceId)                    // 返回指定设备的欧拉角（度）
    {
        if (deviceId < 0 || deviceId >= DEVICE_COUNT) return Vector3.zero;
        return transformedSensorQuaternions[deviceId].eulerAngles;
    }
    // 原轮询状态接口已删除：通过 OnStatusChanged 事件外部可获知 calibrated/driving 状态

    // 刷新端口
    private void RefreshPortsLogic()
    {
        serialController.RefreshPorts(out systemPorts);   // 通过串口控制器获取可用端口列表
        if (systemPorts.Length > 0)                        // 如果有端口
        {
            if (selectedPortIdx < 0 || selectedPortIdx >= systemPorts.Length) selectedPortIdx = 0; // 默认选中第一个
            portName = systemPorts[selectedPortIdx];       // 更新当前端口名
        }
        else
        {
            selectedPortIdx = -1;                           // 无端口时索引设为-1
            portName = "";                                   // 端口名清空
        }
    }
    // 连接/断开
    private void ToggleConnectLogic()
    {
        if (!isConnected)                                    // 如果当前未连接
        {
            // 尝试从系统端口列表中获取当前选择的端口
            if (systemPorts.Length > 0 && selectedPortIdx >= 0 && selectedPortIdx < systemPorts.Length)
            {
                portName = systemPorts[selectedPortIdx];    // 使用选中的端口名
            }
            bool ok = serialController.Connect(portName, selectedBaud); // 尝试连接
            isConnected = ok;                                 // 更新连接状态
            if (isConnected && saveEnabled)                   // 如果连接成功且启用了保存
            {
                OpenTelemetryLog();                            // 打开遥测日志文件
            }
            // 触发状态更新事件
            OnStatusChanged?.Invoke(isConnected,
                GetHasAnyData(),
                rotationDriver != null && rotationDriver.IsCalibrated,
                isDriving,
                GetIsStable());
        }
        else                                                  // 如果当前已连接
        {
            isConnected = false;                               // 设置连接状态为false
            serialController.Disconnect();                     // 断开串口连接
            CloseTelemetryLog();                                // 关闭遥测日志
            // 触发状态更新事件（所有状态均为false）
            OnStatusChanged?.Invoke(false, false, false, false, false);
        }
    }
    private void BeginDrivingImpl()
    {
        // 调用RotationDriver的校准方法，传入骨骼数组和当前传感器四元数，计算初始偏差
        rotationDriver.Calibrate(bones, transformedSensorQuaternions);
        isDriving = true;                                      // 进入驱动态
        // 触发状态更新事件
        OnStatusChanged?.Invoke(isConnected,
            GetHasAnyData(),
            rotationDriver != null && rotationDriver.IsCalibrated,
            isDriving,
            GetIsStable());
    }
    private void HandleResetRequest()
    {
        // 停止驱动并清理状态
        isDriving = false;                                      // 退出驱动态
        for (int i = 0; i < DEVICE_COUNT; i++)                 // 清除所有设备的数据标志
        {
            deviceHasData[i] = false;
            transformedSensorQuaternions[i] = Quaternion.identity; // 重置转换后的四元数为单位四元数
        }
        // 清空解析器缓冲与队列
        if (SerialParser != null)
        {
            SerialParser.Reset();                                // 重置解析器内部状态
        }
        // 关闭串口连接，回到待机（turn on）状态
        if (serialController != null)
        {
            serialController.Disconnect();                       // 断开串口
        }
        CloseTelemetryLog();                                      // 关闭日志
        isConnected = false;                                      // 更新连接状态
        // 恢复骨骼到绑定姿态并清除校准
        if (rotationDriver != null)
        {
            rotationDriver.ResetToRestPose(bones, restLocalRotations); // 重置骨骼到初始局部旋转
        }
        // 稳定性监控重置
        stabilityMonitor = new StabilityMonitor(DEVICE_COUNT);   // 重新创建稳定性监控器
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
        if (deviceId < 0 || deviceId >= DEVICE_COUNT) return;    // 忽略无效设备ID

        // 更新四元数并直接计算三个角度（移除调试与中间数组）
        deviceQuat[deviceId] = currentQuaternion;                // 保存原始四元数
        var e = currentQuaternion.eulerAngles;                    // 获取欧拉角表示
        yawVals[deviceId] = e.z;                                   // 存储yaw（偏航）
        pitchVals[deviceId] = e.y;                                 // 存储pitch（俯仰）
        rollVals[deviceId] = e.x;                                  // 存储roll（横滚）
        OnEulerUpdated?.Invoke(deviceId, new Vector3(rollVals[deviceId], pitchVals[deviceId], yawVals[deviceId])); // 触发欧拉角更新事件
        LogFrame(deviceId, currentQuaternion, e);                 // 记录到日志文件（如果启用）
    }

    // #region Unity

    // 初始化纹理与本地端口列表（UI相关）
    private void Awake()
    {
        // 创建两个圆形纹理：等待状态（灰色）和就绪状态（绿色）
        btnCircleWaiting = MakeCircleTexture(startButtonSize, new Color(0.25f, 0.25f, 0.25f, 0.75f), new Color(0.55f, 0.55f, 0.55f, 1f));
        btnCircleReady = MakeCircleTexture(startButtonSize, new Color(0.12f, 0.55f, 0.12f, 0.85f), new Color(0.25f, 0.85f, 0.25f, 1f));
        RefreshPortsLocal();                                        // 刷新本地端口列表（调用Unity API）
        smoothingEnabledUI = true;                                  // 默认启用平滑
        limitsEnabledUI = false;                                    // 默认不启用角度限制
        twistSwingEnabledUI = false;                                // 默认使用Euler模式
        if (string.IsNullOrEmpty(exportDirectory))                  // 如果导出目录为空
        {
            exportDirectory = Directory.GetCurrentDirectory();      // 设置为当前工作目录
        }
        if (!telemetryRectInitialized)                              // 如果遥测窗口矩形未初始化
        {
            float w = Mathf.Max(minTelemetryWidth, telemetryDefaultWidth);   // 取最小宽度和默认宽度的较大值
            float h = Mathf.Max(minTelemetryHeight, telemetryDefaultHeight); // 取最小高度和默认高度的较大值
            float x = Mathf.Max(telemetryMargin, Screen.width - w - telemetryMargin); // 计算X坐标（右侧留边距）
            float y = Mathf.Max(telemetryMargin, Screen.height - h - telemetryMargin); // 计算Y坐标（底部留边距）
            telemetryWindowRect = new Rect(x, y, w, h);            // 设置窗口矩形
            telemetryRectInitialized = true;                        // 标记已初始化
        }
    }

    // 初始化：退出全屏、查找骨骼对象、初始化子系统
    void Start()
    {
        Screen.fullScreen = false;                                  // 退出全屏模式（便于调试）
        ResolveAvatarRoot();                                        // 查找角色根节点
        // 捕获角色当前朝向作为补偿基准
        rootFacingOffset = avatarRoot != null ? avatarRoot.rotation : Quaternion.identity; // 记录根节点初始旋转

        // 初始化解析与队列管理
        SerialParser = new SerialParser();                          // 创建串口解析器实例
        // 初始化串口控制器（后台线程读取，无 MonoBehaviour 依赖）
        serialController = new SerialController(SerialParser);      // 创建串口控制器，传入解析器

        // 初始化数组
        deviceQuat = new Quaternion[DEVICE_COUNT];                  // 分配原始四元数数组
        yawVals = new float[DEVICE_COUNT];                          // 分配yaw数组
        pitchVals = new float[DEVICE_COUNT];                        // 分配pitch数组
        rollVals = new float[DEVICE_COUNT];                         // 分配roll数组
        transformedSensorQuaternions = new Quaternion[DEVICE_COUNT]; // 分配转换后四元数数组
        // 初始化为 identity，避免未接收到数据时使用 (0,0,0,0)
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            deviceQuat[i] = Quaternion.identity;                    // 初始化为单位四元数
            transformedSensorQuaternions[i] = Quaternion.identity;  // 初始化为单位四元数
        }
        // 初始化旋转驱动器（默认开启平滑，关闭 Twist+Swing，改用 Euler；去抖阈值 5°）
        rotationDriver = new RotationDriver(DEVICE_COUNT, true, smoothSpeed, 5f, false); // 创建旋转驱动器
        bones = new GameObject[DEVICE_COUNT];                        // 分配骨骼引用数组
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            deviceHasData[i] = false;                                // 初始化数据标志为false
        }
        // 使用脚本中声明的默认骨骼名
        if (bone_names == null || bone_names.Length != DEVICE_COUNT) // 如果骨骼名数组无效
        {
            // 重新设置为默认骨骼名
            bone_names = new string[]
            {
                "Bip01 L UpperArm", "Bip01 L Forearm", "Bip01 R UpperArm", "Bip01 R Forearm",
                "Bip01 Spine2", "Bip01 L Thigh", "Bip01 L Calf", "Bip01 R Thigh", "Bip01 R Calf"
            };
        }

        // 依据（可能来自 JSON）更新后的骨骼名查找骨骼对象
        for (int i = 0; i < bone_names.Length; i++)
        {
            bones[i] = GameObject.Find(bone_names[i]);               // 在场景中查找对应名称的游戏对象
        }

        systemPorts = SerialPort.GetPortNames();                     // 获取系统当前可用的串口列表
        if (systemPorts.Length > 0)                                   // 如果有端口
        {
            // 默认选择第一项；若之前指定的端口存在则保持
            int idx = Array.IndexOf(systemPorts, portName);          // 查找之前设置的端口名是否在列表中
            if (idx < 0) idx = 0;                                     // 如果不存在则使用第一个
            selectedPortIdx = idx;                                    // 保存选中索引
            portName = systemPorts[selectedPortIdx];                 // 更新端口名
        }
        else
        {
            selectedPortIdx = -1;                                     // 无端口时索引为-1
            portName = "";                                            // 端口名为空
        }

        // 配置异常检测
        if (anomalyEnable)                                            // 如果启用了异常检测
        {
            var detector = new AnomalyDetector                        // 创建异常检测器
            {
                HistorySize = anomalyBufferSize,                      // 设置历史缓冲区大小
                AngleThresholdDeg = anomalyThreshold                  // 设置角度阈值
            };
            SerialParser.Detector = detector;                         // 将检测器赋给解析器
        }

        restLocalRotations = new Quaternion[DEVICE_COUNT];            // 分配初始局部旋转数组
        // 夹角限制由 RotationDriver 控制

        // 捕获绑定/初始局部旋转作为约束参考
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            if (bones[i] != null)                                     // 如果骨骼对象存在
                restLocalRotations[i] = bones[i].transform.localRotation; // 记录其局部旋转
            else
                restLocalRotations[i] = Quaternion.identity;          // 否则记录单位四元数
        }
        // 将约束与绑定姿态缓存到驱动器，运行期复用
        rotationDriver.SetConstraints(minLocalAngles, maxLocalAngles, restLocalRotations); // 设置角度限制和初始姿态
        // 初始化稳定性监控器
        stabilityMonitor = new StabilityMonitor(DEVICE_COUNT);       // 创建稳定性监控器实例
        // 初始化可复用的帧缓存，避免 Update 中 per-frame 分配
        latestFrames = new SerialParser.SensorFrame[DEVICE_COUNT];   // 分配最新帧数组
        hasLatest = new bool[DEVICE_COUNT];                          // 分配是否有最新帧标志数组

        // 本地 UI 订阅核心事件消息
        OnStatusChanged += HandleStatusChanged;                      // 订阅状态变化事件
        OnEulerUpdated += HandleEulerUpdated;                        // 订阅欧拉角更新事件
    }

    // 每帧更新：应用传感器数据与初始偏差到骨骼
    void Update()
    {
        // 从 SerialParser 的队列主动取数据（主线程），保证后续的转换与动画驱动都在 Unity 主线程执行
        if (SerialParser != null)
        {
            int dequeuedDeviceId;                                     // 用于接收出队的设备ID
            Quaternion dequeuedQ;                                     // 用于接收出队的四元数
            while (SerialParser.TryDequeue(out dequeuedDeviceId, out dequeuedQ)) // 循环取出所有待处理帧
            {
                HandleSerialQuaternion(dequeuedDeviceId, dequeuedQ);  // 处理该帧（更新数组、触发事件、记录日志）
                deviceHasData[dequeuedDeviceId] = true;               // 标记该设备已有数据
            }
        }
        // 坐标系转换：按关节类型应用对应方法
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            if (!deviceHasData[i]) { transformedSensorQuaternions[i] = Quaternion.identity; continue; } // 无数据则设为单位四元数
            transformedSensorQuaternions[i] = MapSensorToAvatarSpace(i, deviceQuat[i]); // 转换传感器坐标系到角色空间
        }
        // 稳定性检测：统计每设备的角速度是否连续低于阈值
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            var curr = transformedSensorQuaternions[i];               // 获取当前帧的转换后四元数
            stabilityMonitor.UpdateDevice(i, curr, deviceHasData[i], maxAvgAngularSpeedDeg); // 更新该设备的稳定性状态
        }

        // 若已连接且已有数据，但尚未校准，则进行一次预校准
        if (isConnected && GetHasAnyData() && rotationDriver != null && !rotationDriver.IsCalibrated)
        {
            rotationDriver.Calibrate(bones, transformedSensorQuaternions); // 自动校准（计算初始偏差）
        }

        // 广播状态（每帧）：连接、是否有数据、是否校准、是否在驱动、稳定性
        OnStatusChanged?.Invoke(isConnected,
            GetHasAnyData(),
            rotationDriver != null && rotationDriver.IsCalibrated,
            isDriving,
            GetIsStable());

        if (isDriving)                                               // 如果处于驱动态
        {
            DateTime? newestTime = null;                             // 最新时间戳（用于插值）
            DateTime? oldestTime = null;                             // 最旧时间戳（预留，未使用）
            int latestCount = 0;                                     // 拥有最新帧的设备数量
            for (int i = 0; i < DEVICE_COUNT; i++) hasLatest[i] = false; // 重置标记
            for (int i = 0; i < DEVICE_COUNT; i++)
            {
                if (SerialParser.TryGetLatestFrame(i, out latestFrames[i])) // 尝试获取每个设备的最新帧
                {
                    hasLatest[i] = true;                             // 标记有最新帧
                    latestCount++;                                   // 计数加1
                    if (!newestTime.HasValue || latestFrames[i].Timestamp > newestTime.Value) // 找出最新时间戳
                        newestTime = latestFrames[i].Timestamp;
                    if (!oldestTime.HasValue || latestFrames[i].Timestamp < oldestTime.Value) // 找出最旧时间戳
                        oldestTime = latestFrames[i].Timestamp;
                }
            }

            if (latestCount == 0 || !newestTime.HasValue) return;    // 如果没有设备有数据，跳过驱动

            DateTime targetTime = newestTime.Value;                  // 以最新时间戳为目标（用于插值同步）
            int devicesApplied = 0;                                   // 记录应用了数据的设备数
            for (int i = 0; i < DEVICE_COUNT; i++)
            {
                if (!hasLatest[i])                                    // 如果该设备没有最新帧
                {
                    if (requireAllDevices)                            // 如果需要所有设备同步
                    {
                        // 缺失数据且要求所有设备同步，直接跳过驱动（不更新任何骨骼）
                        return;
                    }
                    if (deviceHasData[i])                             // 如果该设备曾经有过数据
                    {
                        // 使用最近保存的原始四元数（未插值）
                        transformedSensorQuaternions[i] = MapSensorToAvatarSpace(i, deviceQuat[i]); // 转换后直接使用
                        devicesApplied++;                             // 应用计数加1
                    }
                    continue;
                }

                SerialParser.SensorFrame frame = latestFrames[i];    // 获取该设备的最新帧
                if (frame.Timestamp != targetTime)                   // 如果该帧的时间戳不等于目标时间（说明不是最新帧）
                {
                    // 尝试插值得到目标时间点的帧
                    if (!SerialParser.TryGetInterpolatedFrame(i, targetTime, out frame))
                    {
                        // 插值失败则使用最新帧
                        frame = latestFrames[i];
                    }
                }
                // 将插值后（或最新）的帧转换到角色空间
                transformedSensorQuaternions[i] = MapSensorToAvatarSpace(i, frame.Q);
                devicesApplied++;                                     // 应用计数加1
            }

            if (devicesApplied == 0)                                  // 如果没有设备应用数据，跳过
            {
                return;
            }

            if (!rotationDriver.IsCalibrated)                         // 如果尚未校准（可能因为之前没有数据，现在有了）
            {
                rotationDriver.Calibrate(bones, transformedSensorQuaternions); // 再次校准
            }
            rotationDriver.UpdateTargets(transformedSensorQuaternions); // 更新目标四元数（驱动器内部会计算平滑目标）
        }
    }

    // 在 LateUpdate 写入 transform，确保在动画之后执行
    void LateUpdate()
    {
        if (!isDriving) return;                                       // 未驱动则直接返回
        // 由 RotationDriver 内部的 limitsEnabled 与 constraintsReady 控制限幅
        rotationDriver.Apply(bones);                                  // 将最终旋转应用到骨骼Transform
    }

    // 应用退出时关闭串口
    private void OnApplicationQuit()
    {
        // 关闭串口连接
        if (serialController != null)
        {
            serialController.Disconnect();                            // 断开串口
        }
        CloseTelemetryLog();                                          // 关闭日志文件
    }
    // 组件禁用时关闭串口
    private void OnDisable()
    {
        if (serialController != null)
        {
            serialController.Disconnect();                            // 断开串口
        }
        CloseTelemetryLog();                                          // 关闭日志文件
    }
    // #endregion

    // #region UI（IMGUI）
    private void OnGUI()
    {
        DrawAll();                                                    // 绘制所有UI
    }

    private void DrawAll()
    {
        var size = startButtonSize;                                   // 获取启动按钮尺寸
        // 绘制控制窗口
        controlWindowRect = GUI.Window(controlWindowId, controlWindowRect, DrawControlWindow, "Control Interface");
        // 绘制遥测窗口
        telemetryWindowRect = GUI.Window(telemetryWindowId, telemetryWindowRect, DrawTelemetryWindow, "Sensor Telemetry");
        // 绘制中央启动按钮
        DrawCenterStart(size);
    }

    private void EnsureTableStyles()
    {
        if (tableHeaderStyle == null)                                 // 如果标题样式未创建
        {
            tableHeaderStyle = new GUIStyle(GUI.skin.label)          // 基于默认label样式
            {
                fontStyle = FontStyle.Bold,                           // 加粗
                alignment = TextAnchor.MiddleCenter,                  // 居中
                normal = { textColor = Color.white }                  // 白色文字
            };
        }
        if (tableCellStyle == null)                                   // 如果单元格样式未创建
        {
            tableCellStyle = new GUIStyle(GUI.skin.box)               // 基于默认box样式
            {
                alignment = TextAnchor.MiddleCenter,                  // 居中
                normal = { textColor = Color.white }                  // 白色文字
            };
        }
    }

    private void DrawControlWindow(int id)
    {
        float width = controlWindowRect.width;                        // 获取窗口当前宽度
        // 校准过程中禁用连接按钮
        GUI.enabled = !isCalibratingUI;                                // 如果正在校准，禁用按钮
        if (GUI.Button(new Rect(30, 40, 80, 40), connectLabel))      // 绘制连接/断开按钮
        {
            if (connectLabel == "turn on")                            // 如果是“turn on”（未连接）
            {
                selectedBaud = ParseBaud(baudText, 115200);           // 解析波特率输入框的值
                OnConnectRequested(portName, selectedBaud);           // 触发连接请求
                isCalibratingUI = true;                               // 进入校准中状态（UI显示）
                hasStarted = false;                                    // 重置启动标记
            }
            else                                                      // 如果是“turn off”（已连接）
            {
                OnDisconnectRequested();                              // 触发断开请求
                hasStarted = false;                                    // 重置启动标记
                isCalibratingUI = false;                               // 退出校准中状态
            }
        }
        GUI.enabled = true;                                           // 恢复所有控件可用

        int height = 20;                                              // 临时变量，未使用
        // 端口下拉选择（自适应宽度）
        GUI.Label(new Rect(20, 20, 40, 20), "port");                  // 显示“port”标签
        float portBtnWidth = Mathf.Max(110f, width - 80f);            // 计算按钮宽度，最小110，最大窗口宽度-80
        Rect portBtnRect = new Rect(60, 20, portBtnWidth, 22);        // 端口按钮矩形
        string currentPortLabel = (systemPorts != null && systemPorts.Length > 0 && selectedPortIdx >= 0 && selectedPortIdx < systemPorts.Length)
            ? systemPorts[selectedPortIdx]                            // 如果有选中端口，显示端口名
            : "(选择端口)";                                            // 否则显示提示
        if (GUI.Button(portBtnRect, currentPortLabel))                // 绘制端口选择按钮
        {
            portDropdownOpen = !portDropdownOpen;                      // 点击时切换下拉列表展开/收起
        }
        // 点击窗口其他位置时关闭下拉
        if (portDropdownOpen && Event.current.type == EventType.MouseDown) // 如果下拉展开且发生鼠标点击事件
        {
            // 计算下拉区域（用于点击外部关闭）
            int count = systemPorts != null ? systemPorts.Length : 0;
            float listVisible = Mathf.Min(6, Mathf.Max(0, count));    // 最多显示6项
            float listHeight = listVisible * portItemHeight + 4f;     // 下拉列表高度
            Rect dropRect = new Rect(portBtnRect.x, portBtnRect.y + portBtnRect.height + 2f, portBtnRect.width, listHeight); // 下拉列表区域
            // 如果鼠标点击不在按钮区域也不在下拉列表区域内，则关闭下拉
            if (!portBtnRect.Contains(Event.current.mousePosition) && !dropRect.Contains(Event.current.mousePosition))
            {
                portDropdownOpen = false;
            }
        }
        if (portDropdownOpen)                                          // 如果下拉列表展开
        {
            int count = systemPorts != null ? systemPorts.Length : 0;
            float listVisible = Mathf.Min(6, Mathf.Max(0, count));    // 最多显示6项
            float listHeight = listVisible * portItemHeight + 4f;     // 列表高度
            Rect dropRect = new Rect(portBtnRect.x, portBtnRect.y + portBtnRect.height + 2f, portBtnRect.width, listHeight); // 下拉列表区域
            GUI.Box(dropRect, "");                                     // 绘制一个背景框
            // 滚动区域
            Rect viewRect = new Rect(0, 0, dropRect.width - 20f, count * portItemHeight); // 内部视图大小
            portDropdownScroll = GUI.BeginScrollView(dropRect, portDropdownScroll, viewRect); // 开始滚动视图
            for (int i = 0; i < count; i++)                            // 遍历所有端口
            {
                Rect itemRect = new Rect(2f, i * portItemHeight, dropRect.width - 24f, portItemHeight); // 每个选项的位置
                string label = systemPorts[i];                         // 端口名
                if (GUI.Button(itemRect, label))                       // 绘制可点击的按钮
                {
                    selectedPortIdx = i;                               // 选中该端口
                    portName = systemPorts[i];                         // 更新端口名
                    portDropdownOpen = false;                          // 关闭下拉
                }
            }
            GUI.EndScrollView();                                        // 结束滚动视图
        }
        // 当系统没有端口时，允许手动输入
        if (systemPorts == null || systemPorts.Length == 0)
        {
            portName = GUI.TextField(new Rect(60, 46, portBtnWidth, 20), portName ?? ""); // 显示文本输入框
        }

        if (GUI.Button(new Rect(20, 90, 120, 22), "刷新端口")) { OnRefreshPortsRequested(); RefreshPortsLocal(); } // 刷新端口按钮
        if (GUI.Button(new Rect(150, 90, 70, 22), "重置"))            // 重置按钮
        {
            hasStarted = false;                                        // 重置启动标记
            calibratedTimestamp = -1f;                                 // 重置校准时间戳
            OnResetRequested();                                        // 触发重置请求
        }
        GUI.Label(new Rect(20, 115, 100, 20), "baud");                 // 波特率标签
        baudText = GUI.TextField(new Rect(60, 115, 110, 20), baudText, 10); // 波特率输入框

        GUI.Label(new Rect(20, 140, 200, 20), $"策略: auto");          // 显示策略（固定为auto）
        GUI.Label(new Rect(20, 165, 200, 20), $"端口: {portName}");   // 显示当前端口名

        bool newLimits = GUI.Toggle(new Rect(20, 190, 200, 24), limitsEnabledUI, "角度限制"); // 角度限制开关
        if (newLimits != limitsEnabledUI)                              // 如果状态改变
        {
            limitsEnabledUI = newLimits;                               // 更新UI状态
            if (rotationDriver != null) rotationDriver.SetLimitsEnabled(limitsEnabledUI); // 通知驱动器
        }

        GUI.enabled = limitsEnabledUI;                                 // 只有启用角度限制时，下面的限幅方式按钮才可交互
        GUI.Label(new Rect(20, 215, 200, 20), "限幅方式:");            // 限幅方式标签
        string currentModeLabel = twistSwingEnabledUI ? "Twist+Swing" : "Euler"; // 根据当前模式显示文本
        if (GUI.Button(new Rect(20, 240, 210, 24), currentModeLabel)) // 限幅方式切换按钮
        {
            twistSwingEnabledUI = !twistSwingEnabledUI;               // 切换模式
            if (rotationDriver != null) rotationDriver.SetTwistSwing(twistSwingEnabledUI); // 通知驱动器
        }
        GUI.enabled = true;                                            // 恢复所有控件可用

        bool newSmoothing = GUI.Toggle(new Rect(20, 270, 200, 24), smoothingEnabledUI, "平滑(Slerp)"); // 平滑开关
        if (newSmoothing != smoothingEnabledUI)                        // 如果状态改变
        {
            smoothingEnabledUI = newSmoothing;                         // 更新UI状态
            if (rotationDriver != null) rotationDriver.SetSmoothing(smoothingEnabledUI, smoothSpeed); // 通知驱动器
        }

        bool newRequireAll = GUI.Toggle(new Rect(20, 330, 200, 24), requireAllDevicesUI, "要求所有设备稳定"); // 要求所有设备稳定开关
        if (newRequireAll != requireAllDevicesUI)                      // 如果状态改变
        {
            requireAllDevicesUI = newRequireAll;                       // 更新UI状态
            requireAllDevices = requireAllDevicesUI;                   // 同步到业务字段
        }

        GUI.Label(new Rect(20, 360, 160, 20), $"最少稳定设备: {minStableDevicesUI}"); // 显示最少稳定设备数
        string minStableStr = GUI.TextField(new Rect(150, 360, 50, 20), minStableDevicesUI.ToString(), 2); // 输入框
        if (int.TryParse(minStableStr, out var parsed) && parsed != minStableDevicesUI) // 如果解析成功且值改变
        {
            minStableDevicesUI = Mathf.Clamp(parsed, 1, DEVICE_COUNT); // 限制范围
            minStableDevices = minStableDevicesUI;                     // 同步到业务字段
        }

        saveEnabled = GUI.Toggle(new Rect(20, 390, width - 40, 20), saveEnabled, "保存到文件"); // 保存开关
        GUI.Label(new Rect(20, 414, width - 40, 20), "数据目录:");     // 数据目录标签
        exportDirectory = GUI.TextField(new Rect(20, 436, width - 40, 20), exportDirectory ?? ""); // 目录输入框
        if (!saveEnabled && telemetryWriter != null)                   // 如果关闭保存但日志文件还在打开
        {
            CloseTelemetryLog();                                        // 关闭日志
        }
        if (saveEnabled && telemetryWriter == null && isConnected)     // 如果开启保存且日志未打开且已连接
        {
            OpenTelemetryLog();                                         // 打开日志
        }

        GUI.Label(new Rect(20, 468, 260, 20), telemetryWriter != null ? $"记录中: {Path.GetFileName(currentLogPath)}" : "未记录"); // 显示记录状态

        GUI.Label(new Rect(20, 495, 200, 20), $"稳定: {(GetIsStable() ? "OK" : "等待")}"); // 显示稳定性状态

        DrawResizeHandle(ref controlWindowRect, ref isResizingControl, minControlWidth, minControlHeight); // 绘制调整大小手柄
        GUI.DragWindow(new Rect(0, 0, controlWindowRect.width, 22f)); // 允许拖动窗口（标题栏区域）
    }

    private void DrawTelemetryWindow(int id)
    {
        EnsureTableStyles();                                          // 确保表格样式已创建
        string[] headers = { "number", "q0", "q1", "q2", "q3", "yaw", "pitch", "roll" }; // 表头
        float[] widths = { 70f, 70f, 70f, 70f, 70f, 70f, 70f, 70f }; // 每列宽度
        float headerHeight = 20f;                                     // 表头高度
        float headerY = 24f;                                          // 表头的Y坐标
        float rowHeight = 24f;                                        // 每行高度
        float startX = 8f;                                            // 起始X坐标
        float startY = headerY + headerHeight + 4f;                   // 第一行数据的Y坐标

        // 标题行
        float x = startX;
        for (int i = 0; i < headers.Length; i++)
        {
            GUI.Label(new Rect(x, headerY, widths[i], headerHeight), headers[i], tableHeaderStyle); // 绘制表头
            x += widths[i];
        }

        // 数据行
        for (int i = 0; i < DEVICE_COUNT; i++)
        {
            float y = startY + rowHeight * i;                         // 当前行的Y坐标
            x = startX;

            Quaternion q = transformedSensorQuaternions != null && transformedSensorQuaternions.Length > i
                ? transformedSensorQuaternions[i]                     // 获取转换后的四元数
                : Quaternion.identity;                                 // 默认单位四元数
            Vector3 euler = q.eulerAngles;                             // 获取欧拉角

            GUI.Label(new Rect(x, y, widths[0], rowHeight), $"0x{i + 1:00}", tableCellStyle); x += widths[0]; // 设备ID
            GUI.Label(new Rect(x, y, widths[1], rowHeight), q.x.ToString("F3"), tableCellStyle); x += widths[1]; // q0
            GUI.Label(new Rect(x, y, widths[2], rowHeight), q.y.ToString("F3"), tableCellStyle); x += widths[2]; // q1
            GUI.Label(new Rect(x, y, widths[3], rowHeight), q.z.ToString("F3"), tableCellStyle); x += widths[3]; // q2
            GUI.Label(new Rect(x, y, widths[4], rowHeight), q.w.ToString("F3"), tableCellStyle); x += widths[4]; // q3
            GUI.Label(new Rect(x, y, widths[5], rowHeight), euler.z.ToString("F1"), tableCellStyle); x += widths[5]; // yaw
            GUI.Label(new Rect(x, y, widths[6], rowHeight), euler.y.ToString("F1"), tableCellStyle); x += widths[6]; // pitch
            GUI.Label(new Rect(x, y, widths[7], rowHeight), euler.x.ToString("F1"), tableCellStyle); // roll
        }
        DrawResizeHandle(ref telemetryWindowRect, ref isResizingTelemetry, minTelemetryWidth, minTelemetryHeight); // 绘制调整大小手柄
        GUI.DragWindow(new Rect(0, 0, telemetryWindowRect.width, 22f)); // 允许拖动窗口
    }

    private void DrawResizeHandle(ref Rect windowRect, ref bool isResizing, float minW, float minH)
    {
        Rect handleRect = new Rect(windowRect.width - resizeHandleSize, windowRect.height - resizeHandleSize, resizeHandleSize, resizeHandleSize); // 右下角手柄区域
        GUI.Box(handleRect, "");                                      // 绘制一个空白框作为手柄
        Event e = Event.current;                                      // 获取当前事件
        if (e.type == EventType.MouseDown && handleRect.Contains(e.mousePosition)) // 如果鼠标按下且在手柄区域内
        {
            isResizing = true;                                        // 开始调整大小
            resizeStartMouse = e.mousePosition;                      // 记录起始鼠标位置
            resizeStartRect = windowRect;                             // 记录起始窗口矩形
            e.Use();                                                  // 消耗事件，防止传递给其他控件
        }
        if (e.type == EventType.MouseDrag && isResizing)             // 如果正在拖动且鼠标移动
        {
            Vector2 delta = e.mousePosition - resizeStartMouse;      // 计算鼠标移动距离
            windowRect.width = Mathf.Max(minW, resizeStartRect.width + delta.x); // 调整宽度，不小于最小值
            windowRect.height = Mathf.Max(minH, resizeStartRect.height + delta.y); // 调整高度，不小于最小值
            e.Use();                                                  // 消耗事件
        }
        if (e.type == EventType.MouseUp && isResizing)               // 如果鼠标松开且正在调整大小
        {
            isResizing = false;                                       // 结束调整
            e.Use();                                                  // 消耗事件
        }
    }

    private void SaveTelemetryToFile()
    {
        // 兼容旧按钮调用：现在转为持续记录，不再单次导出
        if (saveEnabled && telemetryWriter == null)                  // 如果启用了保存且日志未打开
        {
            OpenTelemetryLog();                                       // 打开日志
        }
    }

    private void OpenTelemetryLog()
    {
        try
        {
            string dir = string.IsNullOrEmpty(exportDirectory) ? Directory.GetCurrentDirectory() : exportDirectory; // 确定目录
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); // 如果目录不存在则创建
            string fileName = DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"; // 文件名：时间戳.txt
            currentLogPath = Path.Combine(dir, fileName);            // 完整路径
            telemetryWriter = new StreamWriter(currentLogPath, false); // 创建写入器（覆盖模式）
            telemetryWriter.AutoFlush = true;                        // 自动刷新缓冲区
            telemetryWriter.WriteLine("timestamp\tdevice\tq0\tq1\tq2\tq3\tyaw\tpitch\troll"); // 写入标题行
            Debug.Log($"Telemetry logging started: {currentLogPath}"); // 输出日志
        }
        catch (Exception ex)                                          // 如果发生异常
        {
            telemetryWriter = null;                                   // 置空写入器
            currentLogPath = "";                                      // 清空路径
            Debug.LogError($"Failed to open telemetry log: {ex.Message}"); // 输出错误
        }
    }

    private void CloseTelemetryLog()
    {
        try
        {
            if (telemetryWriter != null)                              // 如果写入器存在
            {
                telemetryWriter.Flush();                              // 刷新缓冲区
                telemetryWriter.Close();                              // 关闭
                telemetryWriter.Dispose();                            // 释放资源
            }
        }
        catch (Exception) { }                                         // 忽略异常
        telemetryWriter = null;                                       // 置空
        currentLogPath = "";                                          // 清空路径
    }

    private void LogFrame(int deviceId, Quaternion q, Vector3 euler)
    {
        if (!saveEnabled || telemetryWriter == null) return;         // 如果未启用保存或写入器为空，则返回
        string line = $"{DateTime.Now:O}\t0x{deviceId + 1:00}\t{q.x:F4}\t{q.y:F4}\t{q.z:F4}\t{q.w:F4}\t{euler.z:F2}\t{euler.y:F2}\t{euler.x:F2}"; // 格式化为制表符分隔的文本
        telemetryWriter.WriteLine(line);                              // 写入一行
    }

    private void DrawCenterStart(int size)
    {
        bool connected = GetIsConnected();                            // 获取连接状态
        bool hasData = GetHasAnyData();                               // 获取是否有数据
        bool stableReady = GetIsStable();                             // 获取是否稳定
        bool calibrated = rotationDriver != null && rotationDriver.IsCalibrated; // 获取是否已校准
        bool driving = isDriving;                                     // 获取是否正在驱动

        if (hasStarted || driving)                                    // 如果已经点击开始或正在驱动，则不显示中央按钮
            return;

        string label;                                                 // 按钮上显示的文字
        if (!connected)                                               // 未连接
            label = "未连接";
        else if (!hasData)                                            // 已连接但无数据
            label = "等待数据";
        else if (isCalibratingUI && !calibrated)                      // 正在校准且未完成
            label = "校准中";
        else                                                          // 其他情况（就绪）
            label = stableReady ? "点击开始" : "等待稳定";
        var tex = (connected && hasData && !isCalibratingUI && calibrated && stableReady) ? btnCircleReady : btnCircleWaiting; // 选择纹理
        var centerRect = new Rect((Screen.width - size) / 2, (Screen.height - size) / 2, size, size); // 计算中央矩形
        if (tex != null) GUI.DrawTexture(centerRect, tex, ScaleMode.StretchToFill, true); // 绘制圆形背景
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(size * 0.2f), fontStyle = FontStyle.Bold, normal = { textColor = Color.white } }; // 文字样式
        GUI.Label(centerRect, label, style);                          // 绘制文字
        if (connected && hasData && calibrated && stableReady && GUI.Button(centerRect, GUIContent.none, GUIStyle.none)) // 如果就绪且点击了按钮（透明按钮）
        {
            hasStarted = true;                                        // 标记已开始
            isCalibratingUI = false;                                  // 退出校准中状态
            OnBeginDrivingRequested();                                // 触发开始驱动请求
        }

        // 自动开始：如果已连接、有数据、已校准、未开始、未驱动，且校准完成时间超过2秒，则自动开始
        if (connected && hasData && calibrated && !hasStarted && !driving && calibratedTimestamp > 0f)
        {
            if (Time.time - calibratedTimestamp >= 2f)               // 如果校准完成已过2秒
            {
                hasStarted = true;                                    // 标记已开始
                isCalibratingUI = false;                              // 退出校准中状态
                OnBeginDrivingRequested();                            // 触发开始驱动请求
            }
        }
    }

    // 状态与欧拉角事件处理（更新 UI 本地状态）
    private void HandleStatusChanged(bool connected, bool hasData, bool calibrated, bool driving, bool stable)
    {
        isConnected = connected;                                      // 更新连接状态
        connectLabel = isConnected ? "turn off" : "turn on";         // 更新按钮文字
        bool wasCalibrated = rotationDriver != null && rotationDriver.IsCalibrated; // 之前是否已校准（未使用）
        isDriving = driving;                                          // 更新驱动状态
        // UI：校准中状态
        isCalibratingUI = isConnected && !calibrated;                 // 如果已连接但未校准，则视为校准中
        if (isConnected && calibrated && !wasCalibrated)              // 如果刚完成校准
        {
            calibratedTimestamp = Time.time;                          // 记录校准完成时间戳
        }
    }

    private void HandleEulerUpdated(int deviceId, Vector3 eulerDeg)
    {
        if (deviceId >= 0 && deviceId < eulerCache.Length)            // 确保索引有效
            eulerCache[deviceId] = eulerDeg;                          // 缓存欧拉角（供UI使用，但当前UI未直接使用）
    }

    private void RefreshPortsLocal()
    {
        try
        {
            systemPorts = SerialPort.GetPortNames();                  // 获取系统串口列表
            if (systemPorts.Length > 0)                               // 如果有端口
            {
                if (selectedPortIdx < 0 || selectedPortIdx >= systemPorts.Length) selectedPortIdx = 0; // 默认选中第一个
                portName = systemPorts[selectedPortIdx];              // 更新端口名
            }
            else
            {
                selectedPortIdx = -1;                                 // 无端口时索引为-1
                portName = "";                                         // 端口名为空
            }
        }
        catch (Exception)                                             // 如果发生异常
        {
            systemPorts = new string[0];                              // 设为空数组
            selectedPortIdx = -1;                                     // 索引为-1
        }
    }

    private static int ParseBaud(string text, int fallback)
    {
        return int.TryParse(text, out var b) && b > 0 ? b : fallback; // 尝试解析，如果失败或非正数则返回fallback
    }

    // 通过名称或直接引用获取 avatarRoot（优先已有引用）
    private void ResolveAvatarRoot()
    {
        if (avatarRoot == null && !string.IsNullOrEmpty(avatarRootName)) // 如果根节点为空且名称不为空
        {
            var go = GameObject.Find(avatarRootName);                 // 在场景中查找该名称的游戏对象
            if (go != null) avatarRoot = go.transform;                // 获取其Transform组件
        }
    }

    // 将传感器姿态映射到 Unity 并叠加 Avatar 根节点的初始朝向补偿
    private Quaternion MapSensorToAvatarSpace(int index, Quaternion rawSensorQ)
    {
        var unityQ = RotationDriver.MapSensorToUnity(index, rawSensorQ); // 调用静态方法进行坐标系转换（传感器坐标系到Unity坐标系）
        return avatarRoot != null ? (rootFacingOffset * unityQ) : unityQ; // 如果有根节点，则乘以根节点初始朝向偏移（补偿场景朝向）
    }

    // 创建一个圆形的纹理，用于中央按钮的背景
    private Texture2D MakeCircleTexture(int size, Color fill, Color rim)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false); // 创建指定大小的纹理
        tex.wrapMode = TextureWrapMode.Clamp;                         // 设置包裹模式为Clamp
        float r = size * 0.5f - 1f;                                    // 内圆半径（减去1像素避免边缘）
        float cx = r + 1f, cy = r + 1f;                                // 圆心坐标
        float rimWidth = Mathf.Max(2f, size * 0.06f);                  // 边缘宽度（不小于2像素）
        for (int y = 0; y < size; y++)                                 // 遍历每个像素
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - cx, dy = y - cy;                        // 计算到圆心的距离
                float d = Mathf.Sqrt(dx * dx + dy * dy);               // 距离
                if (d <= r)                                            // 如果在圆内
                {
                    Color c = fill;                                    // 默认填充色
                    if (d > r - rimWidth)                              // 如果在边缘区域
                    {
                        float t = Mathf.InverseLerp(r, r - rimWidth, d); // 计算插值因子
                        c = Color.Lerp(rim, fill, t);                  // 边缘颜色渐变
                    }
                    tex.SetPixel(x, y, c);                             // 设置像素颜色
                }
                else
                {
                    tex.SetPixel(x, y, new Color(0, 0, 0, 0));         // 圆外设为透明
                }
            }
        }
        tex.Apply(false, true);                                        // 应用更改，并标记为不可读（节省内存）
        return tex;                                                    // 返回纹理
    }
    // #endregion
}
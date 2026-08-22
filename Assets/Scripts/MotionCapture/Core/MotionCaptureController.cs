using System;
using System.IO;
using UnityEngine;

/// <summary>
/// V8.20 九传感器时间配对驱动、断流保持与错峰自恢复版。
/// - 强制选择01~09，所有传感器均参与稳定检查、标定和骨骼驱动；
/// - 01/02驱动左大臂/左小臂，03/04驱动右大臂/右小臂，05驱动躯干；
/// - 06+07、08+09分别驱动左右大小腿及膝关节；
/// - 保留V8.10右大臂连续局部Delta三轴矩阵，不启用动作识别、姿态吸附或四动作教学；
/// - 本版用于向技术人员完整暴露当前全身链路问题，不再默认隔离03。
/// </summary>
public class MotionCaptureController : MonoBehaviour
{
    public const string BuildVersion = "V8.20-PAIR-HOLD-RESYNC-20260822";
    private const int CalibrationSamplesPerRequiredSensor = 5;

    public enum SensorCalibrationUiState
    {
        Offline,
        WaitingForStability,
        Ready,
        Sampling,
        Sampled,
        Succeeded,
        Locked,
        NotDriven,
        Failed
    }

    public enum SensorTestSelectionMode
    {
        /// <summary>只允许manualTestSensorIds中列出的设备参与标定和驱动。</summary>
        ManualIdList = 0,
        /// <summary>自动接管所有当前在线设备，保留V8.3行为。</summary>
        AutoAllOnline = 1
    }

    [Header("配置资产")]
    [SerializeField] private MotionCaptureConfig config;

    [Header("稳定标定")]
    [SerializeField] private bool useStableCalibration = true;
    [SerializeField] private int leftLegCalibrationSampleFramesRequired = CalibrationSamplesPerRequiredSensor;
    [SerializeField] private int rightLegCalibrationSampleFramesRequired = CalibrationSamplesPerRequiredSensor;

    [Header("V59 单人标定倒计时")]
    [Tooltip("点击中心按钮后留给穿戴者摆好初始 A-Pose 的总时间。建议保持在 5~8 秒，本版默认为 6 秒。")]
    [SerializeField, Range(5f, 8f)] private float calibrationCountdownSeconds = 6f;
    [Tooltip("倒计时末段用于固定时长平均采样。标定使用每设备最近一次有效帧，不受实时驱动超时影响；单帧硬跳变会被忽略。") ]
    [SerializeField, Range(1f, 3f)] private float armCalibrationSamplingSeconds = 2f;
    [Tooltip("稳定采样期间，用于拒绝单帧硬跳变的角度阈值下限。不会因累计慢漂或短暂无包反复重启标定。") ]
    [SerializeField, Range(0.5f, 8f)] private float armCalibrationMaxDriftDeg = 2.5f;
    [Tooltip("标定锁存样本允许的年龄上限。实际门限按每路Hz自适应为约4个采样周期，并限制在0.5~4秒。")]
    [SerializeField, Range(3.5f, 4f)] private float calibrationSampleMaxAgeSeconds = 4f;
    [Tooltip("每个参与驱动的手臂传感器各自需要接受多少个唯一新帧。01和03分别累计，互不等待。")]
    [SerializeField, Range(3, 30)] private int armCalibrationMinimumUniqueSamples = CalibrationSamplesPerRequiredSensor;

    [Header("V2 单人自动标定")]
    [Tooltip("开启后无需点击中心按钮：必需传感器稳定后自动进入倒计时和采样。中心按钮仍保留为手动重试入口。")]
    [SerializeField] private bool automaticCalibrationEnabled = true;
    [Tooltip("必需传感器全部稳定后，再持续保持多久才自动开始倒计时，避免刚变绿就误触发。")]
    [SerializeField, Range(0.3f, 3f)] private float automaticCalibrationStableHoldSeconds = 0.8f;
    [Tooltip("自动标定因断流或非法数据中断后，等待多久再尝试。")]
    [SerializeField, Range(1f, 10f)] private float automaticCalibrationRetryDelaySeconds = 2f;

    [Header("V2 独立采样与有限等待")]
    [Tooltip("进入采样阶段后允许独立补采的最长时间。超时会指出具体未完成传感器并自动重试，不再无限卡住。")]
    [SerializeField, Range(12f, 30f)] private float independentCalibrationMaxSamplingSeconds = 20f;
    [Tooltip("同一传感器连续两帧落在新的稳定簇时，判定为链路跳变后的新基线并仅重启该路采样。")]
    [SerializeField, Range(2, 4)] private int independentCalibrationReanchorFrames = 2;

    [Header("运行模式")]
    [Tooltip("旧场景兼容字段。V8任意组合模式下不再依赖它决定必须连接哪些传感器。")]
    public bool armOnlyMode = false;

    [Header("V8.20 Zigbee错峰传输与自动恢复")]
    [Tooltip("连接后广播同步命令，并在节点漏同步或重启后自动重发。旧固件会忽略该命令。")]
    [SerializeField] private bool configureZigbeeScheduleOnConnect = true;
    [Tooltip("第一阶段固定使用每路8Hz，九路合计约72包/秒，为无线维护和重发留余量。")]
    [SerializeField, Range(1, 10)] private int zigbeeScheduledTransmitRateHz = 8;
    [Tooltip("仍有V2节点未同步时的重发间隔。重复同步帧很短，不会占用姿态主链路。")]
    [SerializeField, Range(1f, 10f)] private float zigbeeScheduleRetrySeconds = 3f;
    [Tooltip("全部节点同步后仍定期维护一次，用于自动恢复测试中途重启的节点。")]
    [SerializeField, Range(10f, 60f)] private float zigbeeScheduleMaintenanceSeconds = 30f;

    [Header("V8.11 九传感器全身诊断")]
    [Tooltip("开启后强制01~09全部参与标定和驱动，覆盖旧场景中只测试03的序列化设置。")]
    [SerializeField] private bool fullBodyDiagnosticMode = true;
    [Tooltip("手动列表：只允许下方列出的ID参与标定与骨骼驱动；自动全部在线：接管当前在线设备。全身诊断模式会强制使用手动01~09。")]
    [SerializeField] private SensorTestSelectionMode sensorTestSelectionMode = SensorTestSelectionMode.ManualIdList;
    [Tooltip("全身诊断默认选择01~09。仅接受01~09，使用逗号、空格或分号分隔。")]
    [SerializeField] private string manualTestSensorIds = "01,02,03,04,05,06,07,08,09";
    [Tooltip("旧版自动选择开关，仅在模式为AutoAllOnline时使用。")]
    [SerializeField] private bool autoSelectAvailableSensors = true;
    [Tooltip("同侧大腿未连接而小腿单独在线时，允许小腿进入独立骨骼诊断驱动。大小腿同时在线时仍优先使用相对膝关节驱动。")]
    [SerializeField] private bool allowStandaloneCalfTesting = true;
    [Tooltip("兼容旧场景：旧版把driveLeftCalf/driveRightCalf序列化为false。开启后V8启动时将两侧小腿驱动恢复为true；关闭后完全服从下方两侧小腿开关。")]
    public bool unlockCalfDrivingForV8 = true;
    [Tooltip("01/03在线时暂时锁定同侧02/04，避免小臂独立世界旋转干扰大臂四动作判断；只有02或04单独在线时仍允许独立测试。")]
    [SerializeField] private bool isolateUpperArmTestingFromForearms = true;

    [Header("V77.30 集中数据中心") ]
    [Tooltip("统一快照相对最新数据回退的插值缓冲。实时动捕默认0ms；只有确认存在时间撕裂时才小幅增加。")]
    [SerializeField, Range(0f, 0.08f)] private float centralizedSyncDelaySeconds = 0f;
    [Tooltip("标定/等待阶段自适应在线判定的最小门限；低频设备会按约4个采样周期放宽，最多4秒。")]
    [SerializeField, Range(0.2f, 2f)] private float centralizedDeviceTimeoutSeconds = 0.500f;
    [Tooltip("人物驱动阶段的严格新鲜度门限。超过该时间立即暂停骨骼驱动，但保留已完成标定等待链路恢复。")]
    [SerializeField, Range(0.5f, 2f)] private float runtimeDeviceTimeoutSeconds = 1.000f;
    [Tooltip("九路进入人物驱动前的最低实际接收频率。目标固件为10Hz；低于此值只保留标定和诊断，不消费姿态。")]
    [SerializeField, Range(2f, 9f)] private float runtimeMinimumFrameRateHz = 5.0f;
    [Tooltip("标定锁定或运行暂停后，每路至少再收到多少个唯一新帧才允许进入/恢复驱动。")]
    [SerializeField, Range(2, 8)] private int runtimeReadinessMinimumUniqueFrames = 3;
    [Tooltip("九路同时满足帧龄、频率和新帧数后，还需连续保持多久才进入人物驱动。")]
    [SerializeField, Range(0.5f, 2f)] private float runtimeReadinessHoldSeconds = 1.0f;

    [Header("V8.16 低频运行兼容")]
    [Tooltip("当前硬件实际到达Unity的单路接收频率约0.6~1.9Hz，而设备自身上报源Hz约10~20Hz。开启后，不再用固定5Hz作为进入运行的硬阻断条件，而改用每路实测Hz自适应帧龄门限。这样标定完成后可以进入驱动，同时仍会在真正断流时暂停。")]
    [SerializeField] private bool lowRateRuntimeCompatibilityEnabled = true;
    [Tooltip("低频兼容时，运行新鲜度门限约等于该设备若干个实际采样周期，并限制在0.5~4秒。2.5表示允许约2.5个周期的抖动。")]
    [SerializeField, Range(1.5f, 4f)] private float runtimeAdaptiveTimeoutCycles = 2.5f;

    [Tooltip("插值两侧帧间隔超过该值时禁止跨空洞插值，改用最近一帧。")]
    [SerializeField, Range(0.02f, 0.5f)] private float centralizedMaxInterpolationGapSeconds = 0.120f;

    [Header("V7 低频短时补偿")]
    [Tooltip("仅对01/03/06/08启用严格限幅的短时趋势预测；不会跨数秒断流持续外推。")]
    [SerializeField] private bool lowFrequencyPredictionEnabled = true;
    [SerializeField, Range(0.10f, 0.25f)] private float maxPredictionHorizonSeconds = 0.20f;
    [SerializeField, Range(2f, 10f)] private float maxPredictionAngleDeg = 7f;
    [SerializeField, Range(15f, 60f)] private float maxPredictionAngularSpeedDegPerSec = 35f;

    [Header("V8 大小腿驱动与膝角测量")]
    [Tooltip("同侧大小腿同时在线时，只接受时间差不超过该值的06/07、08/09数据对。") ]
    [SerializeField, Range(0.15f, 0.80f)] private float kneeMeasurementMaxPairSkewSeconds = 0.50f;
    [Tooltip("配对数据超过该年龄后保持最后可信角度并标记陈旧，不继续输出新错误值。")]
    [SerializeField, Range(0.2f, 1f)] private float kneeMeasurementMaxFreshAgeSeconds = 0.50f;
    [Tooltip("人物小腿实际驱动使用的严格配对门限。8Hz九时隙一轮最大理论错位约111ms，默认200ms留少量抖动余量。")]
    [SerializeField, Range(0.10f, 0.40f)] private float legDriveMaxPairSkewSeconds = 0.20f;
    [Tooltip("时间配对姿态超过该年龄时只保持小腿最后姿势，不再把旧的大腿/小腿组合写入骨骼。")]
    [SerializeField, Range(0.25f, 1f)] private float legDriveMaxPairAgeSeconds = 0.65f;

    [Header("V77.30 低时延输出")]
    [Tooltip("腿部输入层低通。默认关闭，只保留最终骨骼输出层Slerp，避免双重平滑。")]
    [SerializeField] private bool legInputLowPassEnabled = false;
    [Tooltip("UI平滑开关的运行时状态。关闭后手臂和大腿目标都会直接写入。")]
    [SerializeField] private bool runtimeSmoothingEnabled = true;
    [Tooltip("腿部唯一输出层的平滑速度。30时约77ms达到目标90%，明显低于旧版双层约390ms。")]
    [SerializeField, Range(5f, 60f)] private float legOutputSmoothingSpeed = 30f;
    [Tooltip("即使通信恢复后目标相差很大，腿部每秒最多追赶的角度，避免一帧跳到新姿态。")]
    [SerializeField, Range(90f, 540f)] private float legMaximumAngularSpeedDegPerSec = 240f;
    [Tooltip("手臂和躯干恢复后的最大追赶角速度。")]
    [SerializeField, Range(90f, 720f)] private float upperBodyMaximumAngularSpeedDegPerSec = 300f;

    [Header("左腿调试开关 - Inspector 必须显示")]
    public bool driveLeftLeg = true;
    public bool driveLeftCalf = true;
    public Vector3 leftThighBoneAxisOffsetEuler = Vector3.zero;
    public LeftThighAxisInvertMode leftThighAxisInvertMode = LeftThighAxisInvertMode.None;

    [Header("左大腿 Twist 限制测试 - 前踢翻折重点调这里")]
    public bool limitLeftThighTwist = false;
    public LeftThighTwistAxisMode leftThighTwistAxisMode = LeftThighTwistAxisMode.LocalY;
    public float maxLeftThighTwistDeg = 0f;
    public LeftThighApplyOrder leftThighApplyOrder = LeftThighApplyOrder.RestThenDelta;

    [Header("左腿其它调试")]
    public Vector3 leftCalfBoneAxisOffsetEuler = Vector3.zero;
    public bool forceLeftThighRestForDebug = false;
    public bool leftThighStaticCheckLogEnabled = false;

    [Header("右腿调试开关 - Inspector 必须显示")]
    public bool driveRightLeg = true;
    public bool driveRightCalf = true;
    public Vector3 rightThighBoneAxisOffsetEuler = Vector3.zero;
    public RightThighAxisInvertMode rightThighAxisInvertMode = RightThighAxisInvertMode.None;
    public RightThighEulerRemapMode rightThighEulerRemapMode = RightThighEulerRemapMode.None;

    [Header("右大腿 Twist 限制测试")]
    public bool limitRightThighTwist = false;
    public RightThighTwistAxisMode rightThighTwistAxisMode = RightThighTwistAxisMode.LocalY;
    public float maxRightThighTwistDeg = 0f;
    public RightThighApplyOrder rightThighApplyOrder = RightThighApplyOrder.RestThenDelta;

    [Header("右腿其它调试")]
    public Vector3 rightCalfBoneAxisOffsetEuler = Vector3.zero;
    public bool forceRightThighRestForDebug = false;
    public bool rightThighStaticCheckLogEnabled = false;

    [Header("V8.11 全身手臂驱动 - 01/02/03/04全部开启") ]
    public bool driveArms = true;
    public bool driveLeftArm = true;
    public bool driveLeftForeArm = true;
    public bool driveRightArm = true;
    public bool driveRightForeArm = true;
    public float armSmoothingSpeed = 20f;
    public float armMinAngleThresholdDeg = 0.2f;
    [Tooltip("V8.11保留V8.10右大臂主路径：03在传感器局部空间计算相对A-Pose的旋转增量，再经连续三轴矩阵转换为Avatar肩关节Swing。02/04同时解锁驱动小臂。") ]
    public bool useRightArmCalibratedDeltaSwing = true;
    [Tooltip("旧V8.5回归开关：完整四元数会把大臂长轴twist写入肩关节，默认关闭。")]
    public bool useRightArmFullQuaternionDelta = false;
    [Tooltip("03号右大臂传感器骨段轴。仅作为V8.10主路径被手动关闭后的V8.6回退路径。")]
    public ArmPoseDriver.SegmentAxisMode rightArmSensorAxisMode = ArmPoseDriver.SegmentAxisMode.AutoFromCalibration;
    [HideInInspector] public bool useRightArmFixedReferenceProfile = false;
    [HideInInspector] public float rightArmProfileInterpolationPower = 4f;
    [HideInInspector] public float rightArmProfileExactMatchAngleDeg = 5f;
    [HideInInspector] public float rightArmProfileFallbackAngleDeg = 95f;
    [HideInInspector] public bool enableRightArmFourPoseAxisLearning = false;
    [HideInInspector] public float rightArmPoseInitialPrepareSeconds = 3f;
    [HideInInspector] public float rightArmPoseTransitionSeconds = 1f;
    [HideInInspector] public float rightArmPoseCaptureSeconds = 2f;
    public Vector3 leftArmBoneAxisOffsetEuler = Vector3.zero;
    public Vector3 leftForeArmBoneAxisOffsetEuler = Vector3.zero;
    public Vector3 rightArmBoneAxisOffsetEuler = Vector3.zero;
    public Vector3 rightForeArmBoneAxisOffsetEuler = Vector3.zero;
    [Tooltip("V7固定为false：02号不参与左小臂驱动；字段仅为旧场景序列化兼容保留。")]
    public bool driveLeftForeArmRelativeToLeftArm = false;
    [Tooltip("过滤左小臂沿骨段长轴的滚转，只保留肘部相对摆动，避免小臂自旋。")]
    public bool suppressLeftForeArmAxialTwist = true;
    [Tooltip("左大臂前伸时，按实时向前分量加入少量向身体外侧的连续轴校准；0表示关闭。不是动作识别。") ]
    [Range(-25f, 25f)] public float leftArmForwardOutwardCompensationDeg = 0f;
    [Tooltip("旧场景兼容字段。V77.24固定为0；右大臂只使用独立硬件坐标基与连续骨段方向，不叠加delta框架角。") ]
    [Range(-180f, 180f)] public float rightArmDeltaFrameCorrectionDeg = 0f;

    [Header("旧右前臂兼容参数 - 本轮小臂锁定，不参与输出")]
    [Tooltip("旧场景兼容字段。V77.16小臂锁定时不参与输出。")]
    public Vector3 rightForeArmDeltaAxisOffsetEuler = new Vector3(0f, 1f, 180f);

    [Header("V77.24 右大臂独立连续骨段方向") ]
    [Tooltip("右上臂读取0x03；使用本次标定自动求肩→肘局部长轴；V8.3合法正交轴=(+y,-z,-x,w)，右大臂骨段轴使用局部-Z；旧Correction固定为None。") ]
    public ArmPoseDriver.RightArmCorrectionMode rightArmCorrectionMode = ArmPoseDriver.RightArmCorrectionMode.None;

    [Tooltip("只影响右前臂。右上臂已经正确后，再用它修正右前臂镜像/轴向。修改后必须重新标定。")]
    public ArmPoseDriver.RightForeArmCorrectionMode rightForeArmCorrectionMode = ArmPoseDriver.RightForeArmCorrectionMode.None;
    public bool useRightForeArmRelativeToRightArm = false;

    [Header("旧右前臂驱动模式 - 本轮不参与输出")]
    [Tooltip("旧场景兼容字段。V77.16小臂锁定时不参与输出。")]
    public ArmPoseDriver.RightForeArmDriveMode rightForeArmDriveMode = ArmPoseDriver.RightForeArmDriveMode.AbsoluteWorld;
    public ArmPoseDriver.AxisMode rightForeArmSensorBendAxis = ArmPoseDriver.AxisMode.PositiveX;
    public ArmPoseDriver.AxisMode rightForeArmAvatarBendAxis = ArmPoseDriver.AxisMode.PositiveZ;
    [Tooltip("右前臂 ElbowHinge 的 Avatar 弯曲轴解释空间。小臂向前正确、向上/向下错误时，优先改为 UpperArmLocalPreRest。修改后建议重新 Play/重新标定。")]
    public ArmPoseDriver.RightForeArmAvatarAxisSpace rightForeArmAvatarAxisSpace = ArmPoseDriver.RightForeArmAvatarAxisSpace.ForeArmLocalPostRest;
    public float rightForeArmBendSign = 1f;
    public float rightForeArmBendScale = 1f;
    public float rightForeArmBendOffsetDeg = 0f;
    public bool clampRightForeArmBend = true;
    public float rightForeArmMinBendDeg = -10f;
    public float rightForeArmMaxBendDeg = 150f;
    public bool rightForeArmDebugLog = true;

    [Header("V3 连续手臂映射")]
    [Tooltip("V7固定为true：左右小臂均保持标定姿态，只随大臂父骨骼整体运动。")]
    public bool lockForeArmsToCalibrationRest = true;

    [Header("连续手臂参数锁定")]
    [Tooltip("开启后固定使用V77.24连续映射参数，避免旧场景序列化参数干扰。") ]
    public bool lockContinuousArmPreset = true;

    [Header("V69.3 肘部接近伸直过渡")]
    [Tooltip("只依据同侧大臂与小臂传感器的相对角度判断，不比较左右手。")]
    public bool useElbowStraightBlend = false;
    [Range(150f, 180f)] public float elbowStraightFullIncludedAngleDeg = 165f;
    [Range(120f, 179f)] public float elbowStraightReleaseIncludedAngleDeg = 150f;

    public MotionCaptureConfig Config => config;
    public MotionCaptureState State { get; private set; }
    public SerialManager Serial { get; private set; }
    public Quaternion[] TransformedQuaternions => processor?.TransformedQuaternions;
    public MotionDataHub DataHub => processor?.DataHub;
    public bool IsLogging => logger != null && logger.IsLogging;
    public string CurrentLogPath => logger?.CurrentLogPath ?? "";
    public bool IsAiDiagnosticLogging => aiDiagnosticLogger != null && aiDiagnosticLogger.IsLogging;
    public string AiDiagnosticLogPath => aiDiagnosticLogger?.CurrentPath ?? "";
    public string CurrentTestLogRelativeDirectory { get; private set; } = string.Empty;
    public SensorTestSelectionMode CurrentSensorTestSelectionMode => sensorTestSelectionMode;
    public string ManualTestSensorIds => manualTestSensorIds ?? string.Empty;
    public string SensorTestSelectionSummary => fullBodyDiagnosticMode
        ? "全身诊断[01-09]"
        : sensorTestSelectionMode == SensorTestSelectionMode.ManualIdList
            ? $"手动[{GetNormalizedManualSensorIdList()}]"
            : "自动全部在线";
    public bool IsCalibrationCountdownActive => calibrationCountdownActive;
    public bool IsCalibrationSampling => calibrationCountdownActive && calibrationSamplingActive;
    public float CalibrationCountdownRemaining
    {
        get
        {
            if (!calibrationCountdownActive) return 0f;
            return Mathf.Max(0f, calibrationCountdownSeconds - (Time.time - calibrationCountdownStartTime));
        }
    }
    public float CalibrationStableSamplingRemaining
    {
        get
        {
            if (!calibrationCountdownActive || !calibrationSamplingActive || armSamplingWindowStartTime < 0f)
                return armCalibrationSamplingSeconds;
            return Mathf.Max(0f, armCalibrationSamplingSeconds - (Time.time - armSamplingWindowStartTime));
        }
    }
    public string CalibrationCountdownStatus => calibrationCountdownStatus;
    public bool AutomaticCalibrationEnabled => automaticCalibrationEnabled;
    public bool IsCalibrationLockedWaitingForRuntime => calibrationLockedWaitingForRuntime;
    public bool IsRuntimeDriveSuspended => runtimeDriveSuspended;
    public bool IsWaitingForRuntimeData => calibrationLockedWaitingForRuntime || runtimeDriveSuspended;
    public string LastRuntimeFaultSummary => lastRuntimeFaultSummary ?? string.Empty;
    public int LastRuntimeFaultSensorIndex => lastRuntimeFaultSensorIndex;
    public bool LowRateRuntimeCompatibilityEnabled => lowRateRuntimeCompatibilityEnabled;
    public float RuntimeMinimumFrameRateHz => Mathf.Clamp(runtimeMinimumFrameRateHz, 2f, 9f);
    public int RuntimeReadinessMinimumUniqueFrames => Mathf.Clamp(runtimeReadinessMinimumUniqueFrames, 2, 8);
    public string RuntimeGateSummary => lowRateRuntimeCompatibilityEnabled
        ? $"低频兼容=开/帧龄按{runtimeAdaptiveTimeoutCycles:F1}周期自适应≤4s/固定{RuntimeMinimumFrameRateHz:F1}Hz不阻断/新帧≥{RuntimeReadinessMinimumUniqueFrames}"
        : $"严格模式=帧龄≤{runtimeDeviceTimeoutSeconds:F1}s/接收≥{RuntimeMinimumFrameRateHz:F1}Hz/新帧≥{RuntimeReadinessMinimumUniqueFrames}";
    public float AutomaticCalibrationHoldRemaining
    {
        get
        {
            if (!automaticCalibrationEnabled || automaticCalibrationStableSince < 0f)
                return automaticCalibrationStableHoldSeconds;
            return Mathf.Max(0f,
                automaticCalibrationStableHoldSeconds - (Time.time - automaticCalibrationStableSince));
        }
    }
    public int CalibrationRejectedJumpFrames => SumCalibrationCounters(calibrationRejectedSampleCounts);
    public float CalibrationLastMaxStepDeg => MaxCalibrationValue(calibrationLastStepDeg);
    public float LeftElbowFlexionAngleDeg => armDriver != null ? armDriver.CurrentLeftElbowFlexionAngleDeg : 0f;
    public float RightElbowFlexionAngleDeg => armDriver != null ? armDriver.CurrentRightElbowFlexionAngleDeg : 0f;
    // 兼容旧 UI：KneeAngleDeg 仍表示医学屈曲角（伸直约 0°）。
    public float LeftKneeAngleDeg => LeftKneeFlexionAngleDeg;
    public float RightKneeAngleDeg => RightKneeFlexionAngleDeg;
    public float LeftKneeFlexionAngleDeg => leftLegDriver != null ? leftLegDriver.CurrentKneeFlexionAngleDeg : 0f;
    public float RightKneeFlexionAngleDeg => rightLegDriver != null ? rightLegDriver.CurrentKneeFlexionAngleDeg : 0f;
    public float LeftKneeIncludedAngleDeg => leftLegDriver != null ? leftLegDriver.CurrentKneeIncludedAngleDeg : 180f;
    public float RightKneeIncludedAngleDeg => rightLegDriver != null ? rightLegDriver.CurrentKneeIncludedAngleDeg : 180f;
    public bool LeftKneeMeasurementFresh => leftLegDriver != null && leftLegDriver.IsKneeMeasurementFresh;
    public bool RightKneeMeasurementFresh => rightLegDriver != null && rightLegDriver.IsKneeMeasurementFresh;
    public bool LeftLegDrivePairFresh => leftCalfParticipatesInCalibration && !leftLegPairHeld &&
                                         lastLeftLegDrivePairTimestampUtc != DateTime.MinValue;
    public bool RightLegDrivePairFresh => rightCalfParticipatesInCalibration && !rightLegPairHeld &&
                                          lastRightLegDrivePairTimestampUtc != DateTime.MinValue;
    public long LeftLegPairHoldCount => leftLegPairHoldCount;
    public long RightLegPairHoldCount => rightLegPairHoldCount;

    private SensorDataProcessor processor;
    private TelemetryLogger logger;
    private AiDiagnosticLogger aiDiagnosticLogger;
    private float nextAiDiagnosticSnapshotTime;
    private string lastAiDiagnosticState = string.Empty;
    private int aiDiagnosticMarkerCount;

    private GameObject[] bones;
    private Quaternion[] restLocalRotations;

    private Transform avatarRoot;
    private Quaternion rootFacingOffset = Quaternion.identity;
    private Quaternion avatarRootBaseRotation = Quaternion.identity;

    private int selectedBaud;
    private bool requireAllDevices;

    private LeftLegPoseDriver leftLegDriver;
    private RightLegPoseDriver rightLegDriver;
    private ArmPoseDriver armDriver;
    private StandaloneBonePoseDriver leftStandaloneCalfDriver;
    private StandaloneBonePoseDriver rightStandaloneCalfDriver;
    private StandaloneBonePoseDriver[] genericStandaloneDrivers;
    private float kneeMeasurementCalibrationDeadlineTime = -1f;
    private Quaternion[] leftLegDriveInput;
    private Quaternion[] rightLegDriveInput;
    private bool leftLegPairHeld;
    private bool rightLegPairHeld;
    private long leftLegPairHoldCount;
    private long rightLegPairHoldCount;
    private DateTime lastLeftLegDrivePairTimestampUtc = DateTime.MinValue;
    private DateTime lastRightLegDrivePairTimestampUtc = DateTime.MinValue;
    private double lastLeftLegDrivePairSkewSeconds = double.PositiveInfinity;
    private double lastRightLegDrivePairSkewSeconds = double.PositiveInfinity;
    private float nextZigbeeScheduleSyncTime = -1f;
    private uint zigbeeScheduleToken;
    private int lastReportedSynchronizedSourceCount = -1;
    private long[] observedSourceRestartCounts;
    private float[] lastLegInputStepLogTimes;

    // V2 单人标定状态：每一路只消费自己的真实新帧，任何一路都不会阻塞其他传感器累计。
    private bool calibrationCountdownActive;
    private bool calibrationSamplingActive;
    private float calibrationCountdownStartTime = -1f;
    private float armSamplingWindowStartTime = -1f;
    private string calibrationCountdownStatus = string.Empty;
    private float automaticCalibrationStableSince = -1f;
    private float automaticCalibrationNextAttemptTime;
    // 标定完成和运行中掉线是两个不同状态。二者都停止消费姿态，但绝不清空DataHub诊断数据。
    private bool calibrationLockedWaitingForRuntime;
    private bool runtimeDriveSuspended;
    private float runtimeRecoveryFreshSince = -1f;
    private long[] runtimeReadinessStartSequences;
    private int[] runtimeFaultCounts;
    private bool[] runtimeInputUnavailable;
    private int lastRuntimeFaultSensorIndex = -1;
    private string lastRuntimeFaultSummary = string.Empty;
    private bool[] sensorCalibrationSucceeded;
    private bool[] sensorCalibrationFailed;
    private Quaternion[] calibrationPreviousAccepted;
    private Quaternion[] calibrationPendingJump;
    private bool[] calibrationHasPreviousAccepted;
    private bool[] calibrationHasPendingJump;
    private int[] calibrationPendingJumpCounts;
    private int[] calibrationAcceptedSampleCounts;
    private int[] calibrationRejectedSampleCounts;
    private int[] calibrationRestartCounts;
    private float[] calibrationLastStepDeg;
    private long[] calibrationLastConsumedSequences;
    private readonly Quaternion[] armSamplingHemisphereReference = new Quaternion[4];
    private readonly Vector4[] armSamplingQuaternionSums = new Vector4[4];
    private bool leftArmParticipatesInCalibration;
    private bool rightArmParticipatesInCalibration;
    private bool leftLegParticipatesInCalibration;
    private bool rightLegParticipatesInCalibration;
    private bool leftCalfParticipatesInCalibration;
    private bool rightCalfParticipatesInCalibration;
    private bool leftStandaloneCalfParticipatesInCalibration;
    private bool rightStandaloneCalfParticipatesInCalibration;
    private bool[] genericStandaloneParticipatesInCalibration;
    private Vector4[] standaloneSamplingQuaternionSums;
    private Quaternion[] standaloneSamplingHemisphereReferences;

    private const int LeftArmIndex = (int)BoneIndex.LeftArm;
    private const int LeftForeArmIndex = (int)BoneIndex.LeftForeArm;
    private const int RightArmIndex = (int)BoneIndex.RightArm;
    private const int RightForeArmIndex = (int)BoneIndex.RightForeArm;

    // Avatar 骨骼索引：始终用于 GetBoneTransform / restLocalRotations。
    private const int LeftThighIndex = (int)BoneIndex.LeftUpLeg;
    private const int LeftCalfIndex = (int)BoneIndex.LeftLeg;
    private const int RightThighIndex = (int)BoneIndex.RightUpLeg;
    private const int RightCalfIndex = (int)BoneIndex.RightLeg;

    // V77.24 输入源索引：继续保持人体解剖侧直连。现实左腿只驱动 Avatar 左腿，
    // 现实右腿只驱动 Avatar 右腿；人物正面对屏幕时，Avatar 左侧显示在画面右边。
    private const int LeftThighSensorIndex = LeftLegPoseDriver.LeftThighIndex;
    private const int LeftCalfSensorIndex = LeftLegPoseDriver.LeftCalfIndex;
    private const int RightThighSensorIndex = RightLegPoseDriver.RightThighIndex;
    private const int RightCalfSensorIndex = RightLegPoseDriver.RightCalfIndex;

    [SerializeField, Tooltip("开启后每0.25秒输出传感器姿态、骨骼目标和实际骨骼姿态，用于下一轮轴向诊断。")]
    private bool legDebugLogging = false;
    private bool legBindingsValid = true;

    public string GetExportDirectory() => logger?.GetExportDirectory() ?? "";
    private void Reset()
    {
        ApplyFullBodyDiagnosticPreset();
        ApplyContinuousArmPreset();
        ApplyV58LegPreset();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        ApplyFullBodyDiagnosticPreset();
        ApplyContinuousArmPreset();
        ApplyV58LegPreset();
    }
#endif

    /// <summary>
    /// V8.11全身诊断预设。该预设在Awake/Start/标定入口重复应用，
    /// 用于覆盖旧场景中遗留的“只允许03”“锁定小臂”或关闭腿部等序列化值。
    /// </summary>
    private void ApplyFullBodyDiagnosticPreset()
    {
        // V8.11是专用全身诊断包，运行时不允许旧场景或Inspector关闭该模式。
        fullBodyDiagnosticMode = true;

        sensorTestSelectionMode = SensorTestSelectionMode.ManualIdList;
        manualTestSensorIds = "01,02,03,04,05,06,07,08,09";
        autoSelectAvailableSensors = true;
        armOnlyMode = false;

        driveArms = true;
        driveLeftArm = true;
        driveLeftForeArm = true;
        driveRightArm = true;
        driveRightForeArm = true;

        driveLeftLeg = legBindingsValid;
        driveLeftCalf = legBindingsValid;
        driveRightLeg = legBindingsValid;
        driveRightCalf = legBindingsValid;
        unlockCalfDrivingForV8 = true;
        allowStandaloneCalfTesting = true;

        // 全身模式中02/04必须和01/03同时参与，不再按“只测大臂”逻辑隔离。
        isolateUpperArmTestingFromForearms = false;
        lockForeArmsToCalibrationRest = false;
    }

    /// <summary>
    /// V8 连续手臂参数：保留驱动开关，只冻结平滑和连续坐标校准；
    /// 不设置任何动作锚点、动作名称、姿态识别或目标吸附。
    /// </summary>
    private void ApplyContinuousArmPreset()
    {
        if (!lockContinuousArmPreset) return;

        // V8保留Inspector中的总开关和左右侧开关，允许01或03单独参与，
        // 也允许在腿部调试时完全关闭手臂。这里只冻结映射参数，不再强制开启通道。

        // V8.11全身诊断：02/04作为真实输入驱动小臂；非全身模式仍保留旧锁定策略。
        lockForeArmsToCalibrationRest = !fullBodyDiagnosticMode;
        driveLeftForeArmRelativeToLeftArm = false;
        suppressLeftForeArmAxialTwist = true;

        // 仅做连续低通平滑和微小角度去抖，不做动作分类、姿态吸附或关节目标重写。
        armSmoothingSpeed = 20f;
        armMinAngleThresholdDeg = 0.2f;

        // V8.10左臂保持V8.2已验证路径；右臂使用传感器局部Delta与连续三轴矩阵。
        // 明确关闭完整四元数twist、V8.7教学和V8.8固定参考姿态吸附。
        useRightArmCalibratedDeltaSwing = true;
        useRightArmFullQuaternionDelta = false;
        rightArmSensorAxisMode = ArmPoseDriver.SegmentAxisMode.AutoFromCalibration;
        useRightArmFixedReferenceProfile = false;
        rightArmProfileInterpolationPower = 4f; // 旧字段，仅保留场景兼容
        rightArmProfileExactMatchAngleDeg = 5f; // 旧字段，仅保留场景兼容
        rightArmProfileFallbackAngleDeg = 95f;  // 旧字段，仅保留场景兼容
        enableRightArmFourPoseAxisLearning = false;
        rightArmPoseInitialPrepareSeconds = 3f;
        rightArmPoseTransitionSeconds = 1f;
        rightArmPoseCaptureSeconds = 2f;
        leftArmForwardOutwardCompensationDeg = 0f;
        rightArmDeltaFrameCorrectionDeg = 0f;
        leftArmBoneAxisOffsetEuler = Vector3.zero;
        rightArmBoneAxisOffsetEuler = Vector3.zero;
        leftForeArmBoneAxisOffsetEuler = Vector3.zero;
        rightForeArmBoneAxisOffsetEuler = Vector3.zero;

        rightArmCorrectionMode = ArmPoseDriver.RightArmCorrectionMode.None;
        rightForeArmCorrectionMode = ArmPoseDriver.RightForeArmCorrectionMode.None;

        // 前臂字段保留场景兼容；V8.11全身诊断中02/04使用完整连续世界姿态映射。
        rightForeArmDeltaAxisOffsetEuler = Vector3.zero;
        useRightForeArmRelativeToRightArm = false;
        rightForeArmDriveMode = ArmPoseDriver.RightForeArmDriveMode.AbsoluteWorld;
        rightForeArmSensorBendAxis = ArmPoseDriver.AxisMode.PositiveX;
        rightForeArmAvatarBendAxis = ArmPoseDriver.AxisMode.PositiveZ;
        rightForeArmAvatarAxisSpace = ArmPoseDriver.RightForeArmAvatarAxisSpace.ForeArmLocalPostRest;
        rightForeArmBendSign = 1f;
        rightForeArmBendScale = 1f;
        rightForeArmBendOffsetDeg = 0f;
        clampRightForeArmBend = true;
        rightForeArmMinBendDeg = -10f;
        rightForeArmMaxBendDeg = 150f;
        rightForeArmDebugLog = false;
        useElbowStraightBlend = false;
    }

    /// <summary>
    /// V69 腿部设置：恢复 V57 中按左右腿分别配置的轴映射和局部增量链路。
    /// 小腿已解锁；06+07、08+09用于同侧大小腿相对驱动，07/09也允许单独诊断。
    /// </summary>
    private void ApplyV58LegPreset()
    {
        // V8不再用armOnlyMode或固定01/03/06/08列表决定能否测试。
        // 运行时会根据本次实际在线的传感器锁存参与列表。

        // V4统一使用5个真实唯一新帧。实测08约1.2Hz，旧15帧目标在10秒内
        // 数学上无法完成；5帧仍会做四元数平均，并能落在约5~8秒标定窗口内。
        armCalibrationMinimumUniqueSamples = CalibrationSamplesPerRequiredSensor;
        leftLegCalibrationSampleFramesRequired = CalibrationSamplesPerRequiredSensor;
        rightLegCalibrationSampleFramesRequired = CalibrationSamplesPerRequiredSensor;
        independentCalibrationMaxSamplingSeconds = Mathf.Max(20f, independentCalibrationMaxSamplingSeconds);
        centralizedDeviceTimeoutSeconds = 0.500f;
        calibrationSampleMaxAgeSeconds = 4.000f;
        runtimeDeviceTimeoutSeconds = 1.000f;
        runtimeMinimumFrameRateHz = 5.000f;
        runtimeReadinessMinimumUniqueFrames = 3;
        runtimeReadinessHoldSeconds = 1.000f;
        kneeMeasurementMaxFreshAgeSeconds = 0.500f;
        legDriveMaxPairSkewSeconds = 0.200f;
        legDriveMaxPairAgeSeconds = 0.650f;
        legMaximumAngularSpeedDegPerSec = 240f;
        upperBodyMaximumAngularSpeedDegPerSec = 300f;

        // 保留Inspector中的驱动选择，不再在预设函数里强制开启两条腿或锁死小腿。
        // V8默认允许左右小腿驱动；若小腿未连接，本轮自动退化为仅大腿测试。
        driveLeftLeg = driveLeftLeg && legBindingsValid;
        driveRightLeg = driveRightLeg && legBindingsValid;
        driveLeftCalf = driveLeftCalf && legBindingsValid;
        driveRightCalf = driveRightCalf && legBindingsValid;

        // V77.24：恢复V77.3/V77.5已验证左腿基线。
        // F_ZXY硬件轴 + InvertY局部增量 + 长轴twist过滤，撤销后续B_HandLike方向映射试验。
        leftThighBoneAxisOffsetEuler = Vector3.zero;
        leftThighAxisInvertMode = LeftThighAxisInvertMode.InvertY;
        limitLeftThighTwist = true;
        leftThighTwistAxisMode = LeftThighTwistAxisMode.LocalY;
        maxLeftThighTwistDeg = 0f;
        leftThighApplyOrder = LeftThighApplyOrder.RestThenDelta;
        leftCalfBoneAxisOffsetEuler = Vector3.zero;
        forceLeftThighRestForDebug = false;
        leftThighStaticCheckLogEnabled = false;

        // V7：保留V5统一基础链路与V6右侧横向符号；新增只在屈伸占主导时生效的串轴抑制。
        rightThighBoneAxisOffsetEuler = Vector3.zero;
        rightThighAxisInvertMode = RightThighAxisInvertMode.InvertY;
        rightThighEulerRemapMode = RightThighEulerRemapMode.None;
        limitRightThighTwist = true;
        rightThighTwistAxisMode = RightThighTwistAxisMode.LocalY;
        maxRightThighTwistDeg = 0f;
        rightThighApplyOrder = RightThighApplyOrder.RestThenDelta;
        rightCalfBoneAxisOffsetEuler = Vector3.zero;
        forceRightThighRestForDebug = false;
        rightThighStaticCheckLogEnabled = false;
    }

    private void Awake()
    {
        ApplyFullBodyDiagnosticPreset();
        // Unity窗口失焦时仍持续消费串口，避免后台线程累积满256帧后一次性恢复。
        Application.runInBackground = true;

        Debug.LogWarning("\n==================================================\n" +
            "[V8.20 ACTIVE] MotionCaptureController.Awake\n" +
            "Build=" + BuildVersion + "\n" +
            "模式：强制选择01~09，九路全部参与稳定检查、标定和驱动\n" +
            "上肢：01/02驱动左大臂/左小臂，03/04驱动右大臂/右小臂\n" +
            "躯干：05驱动Spine1\n" +
            "下肢：06+07驱动左大小腿，08+09驱动右大小腿\n" +
            "在线/稳定：按每路实测Hz自适应离线宽限；单次尖峰不立即清空稳定状态\n" +
            "界面：保留V1高对比深色遥测表；通信与历史标定结果分栏显示\n" +
            "低频：01/03/06/08启用200ms/7°限幅短时预测；积压仍只取最新姿态\n" +
            "膝角/小腿驱动：仅消费严格时间配对数据；配对空档保持最后安全姿势\n" +
            "数据：SerialParser原始校验 -> MotionDataHub最新快照/超时 -> 单一快照分发\n" +
            "运行闸门：标定先锁定；九路各自达到1秒帧龄、5Hz和3个新帧后才驱动\n" +
            "故障隔离：单路断流保持最后骨骼姿势；恢复时限速追赶，不清空诊断\n" +
            "==================================================");

        if (config == null)
        {
            Debug.LogError("[MotionCaptureController] 缺少 MotionCaptureConfig 资产引用，请在 Inspector 中指定。");
            enabled = false;
            return;
        }

        config.Validate();
        State = new MotionCaptureState(config.deviceCount);
        sensorCalibrationSucceeded = new bool[config.deviceCount];
        sensorCalibrationFailed = new bool[config.deviceCount];
        calibrationPreviousAccepted = new Quaternion[config.deviceCount];
        calibrationPendingJump = new Quaternion[config.deviceCount];
        calibrationHasPreviousAccepted = new bool[config.deviceCount];
        calibrationHasPendingJump = new bool[config.deviceCount];
        calibrationPendingJumpCounts = new int[config.deviceCount];
        calibrationAcceptedSampleCounts = new int[config.deviceCount];
        calibrationRejectedSampleCounts = new int[config.deviceCount];
        calibrationRestartCounts = new int[config.deviceCount];
        calibrationLastStepDeg = new float[config.deviceCount];
        calibrationLastConsumedSequences = new long[config.deviceCount];
        runtimeReadinessStartSequences = new long[config.deviceCount];
        runtimeFaultCounts = new int[config.deviceCount];
        runtimeInputUnavailable = new bool[config.deviceCount];
        leftLegDriveInput = new Quaternion[config.deviceCount];
        rightLegDriveInput = new Quaternion[config.deviceCount];
        observedSourceRestartCounts = new long[config.deviceCount];
        lastLegInputStepLogTimes = new float[config.deviceCount];
        standaloneSamplingQuaternionSums = new Vector4[config.deviceCount];
        standaloneSamplingHemisphereReferences = new Quaternion[config.deviceCount];
    }

    private void Start()
    {
        ApplyFullBodyDiagnosticPreset();
        ApplyContinuousArmPreset();
        ApplyV58LegPreset();

        Screen.fullScreen = false;

        Serial = new SerialManager();
        processor = new SensorDataProcessor(config);
        if (processor.DataHub != null)
        {
            processor.DataHub.SynchronizationDelaySeconds = centralizedSyncDelaySeconds;
            // V4以3秒为最小门限，MotionDataHub再按每路实测Hz扩展到约4个采样周期。
            // 08实测约1.2Hz时宽限约3.3秒，避免正常轮询抖动被误判离线。
            processor.DataHub.OfflineTimeoutSeconds = Mathf.Clamp(
                centralizedDeviceTimeoutSeconds, 0.2f, 1.0f);
            processor.DataHub.MaxInterpolationGapSeconds = centralizedMaxInterpolationGapSeconds;
            processor.DataHub.LowFrequencyPredictionEnabled = lowFrequencyPredictionEnabled;
            processor.DataHub.MaxPredictionHorizonSeconds = Mathf.Clamp(
                maxPredictionHorizonSeconds, 0.10f, 0.25f);
            processor.DataHub.MaxPredictionAngleDeg = Mathf.Clamp(
                maxPredictionAngleDeg, 2f, 10f);
            processor.DataHub.MaxPredictionAngularSpeedDegPerSec = Mathf.Clamp(
                maxPredictionAngularSpeedDegPerSec, 15f, 60f);
            processor.DataHub.MaximumSourceBacklogAgeSeconds = 0.750f;
        }
        logger = new TelemetryLogger(
            string.IsNullOrEmpty(config.defaultPort) ? Directory.GetCurrentDirectory() : "");
        aiDiagnosticLogger = new AiDiagnosticLogger();

        selectedBaud = config.defaultBaud;
        requireAllDevices = config.requireAllDevices;

        // V8保留Inspector开关；未连接的已启用传感器会被自动忽略，
        // 已连接的任意单个或多个传感器可独立进入本轮标定。

        leftLegDriver = new LeftLegPoseDriver
        {
            InputLowPassEnabled = legInputLowPassEnabled,
            SmoothingEnabled = runtimeSmoothingEnabled,
            SmoothingSpeed = legOutputSmoothingSpeed,
            MaximumAngularSpeedDegPerSec = legMaximumAngularSpeedDegPerSec,
            DebugLogInterval = 0.25f,
            CalibrationSampleFramesRequired = Mathf.Max(1, leftLegCalibrationSampleFramesRequired),
            DriveCalf = driveLeftCalf,
            ThighBoneAxisOffsetEuler = leftThighBoneAxisOffsetEuler,
            ThighAxisInvertMode = leftThighAxisInvertMode,
            LimitThighTwist = limitLeftThighTwist,
            ThighTwistAxisMode = leftThighTwistAxisMode,
            MaxThighTwistDeg = maxLeftThighTwistDeg,
            ThighApplyOrder = leftThighApplyOrder,
            CalfBoneAxisOffsetEuler = leftCalfBoneAxisOffsetEuler,
            ForceThighRestForDebug = forceLeftThighRestForDebug,
            StaticCheckLogEnabled = false,
            KneeDebugLogEnabled = false
        };

        rightLegDriver = new RightLegPoseDriver
        {
            InputLowPassEnabled = legInputLowPassEnabled,
            SmoothingEnabled = runtimeSmoothingEnabled,
            SmoothingSpeed = legOutputSmoothingSpeed,
            MaximumAngularSpeedDegPerSec = legMaximumAngularSpeedDegPerSec,
            DebugLogInterval = 0.25f,
            CalibrationSampleFramesRequired = Mathf.Max(1, rightLegCalibrationSampleFramesRequired),
            DriveCalf = driveRightCalf,
            InvertThighLateralDirection = false,
            ThighBoneAxisOffsetEuler = rightThighBoneAxisOffsetEuler,
            ThighAxisInvertMode = rightThighAxisInvertMode,
            ThighEulerRemapMode = rightThighEulerRemapMode,
            LimitThighTwist = limitRightThighTwist,
            ThighTwistAxisMode = rightThighTwistAxisMode,
            MaxThighTwistDeg = maxRightThighTwistDeg,
            ThighApplyOrder = rightThighApplyOrder,
            CalfBoneAxisOffsetEuler = rightCalfBoneAxisOffsetEuler,
            ForceThighRestForDebug = forceRightThighRestForDebug,
            StaticCheckLogEnabled = false,
            KneeDebugLogEnabled = false
        };

        armDriver = new ArmPoseDriver();
        leftStandaloneCalfDriver = new StandaloneBonePoseDriver
        {
            SmoothingEnabled = runtimeSmoothingEnabled,
            SmoothingSpeed = legOutputSmoothingSpeed,
            MaximumAngularSpeedDegPerSec = legMaximumAngularSpeedDegPerSec
        };
        rightStandaloneCalfDriver = new StandaloneBonePoseDriver
        {
            SmoothingEnabled = runtimeSmoothingEnabled,
            SmoothingSpeed = legOutputSmoothingSpeed,
            MaximumAngularSpeedDegPerSec = legMaximumAngularSpeedDegPerSec
        };
        int genericCount = config != null ? Mathf.Max(9, config.deviceCount) : 9;
        genericStandaloneDrivers = new StandaloneBonePoseDriver[genericCount];
        genericStandaloneParticipatesInCalibration = new bool[genericCount];
        for (int i = 0; i < genericCount; i++)
        {
            genericStandaloneDrivers[i] = new StandaloneBonePoseDriver
            {
                SmoothingEnabled = runtimeSmoothingEnabled,
                SmoothingSpeed = legOutputSmoothingSpeed,
                MaximumAngularSpeedDegPerSec = upperBodyMaximumAngularSpeedDegPerSec
            };
        }
        ApplyInspectorSettingsToArmDriver();
        Debug.LogWarning("[V8.11全身诊断/右大臂输入确认] 03 -> Avatar右大臂；只做A-Pose标定；随后使用传感器局部Delta连续三轴矩阵；无动作识别、无姿态吸附、无顶部提示");

        ResolveAvatarRoot();
        avatarRootBaseRotation = avatarRoot != null ? avatarRoot.rotation : Quaternion.identity;
        rootFacingOffset = avatarRootBaseRotation;
        processor.SetRootFacingOffset(rootFacingOffset);

        CacheBonesAndRestPose();

        legBindingsValid = ValidateIndependentLegBindings(out string legBindingReason);
        if (!legBindingsValid)
        {
            Debug.LogError($"[腿部映射已阻断] {legBindingReason}");
            driveLeftLeg = false;
            driveLeftCalf = false;
            driveRightLeg = false;
            driveRightCalf = false;
        }
        else if (unlockCalfDrivingForV8)
        {
            // 旧场景会把上一版序列化的false保留下来。迁移开关默认解除小腿锁定；
            // 未连接的小腿会被动态忽略，因此不会影响仅大腿或单传感器测试。
            driveLeftCalf = true;
            driveRightCalf = true;
        }

        Serial.RefreshPorts();
        Serial.AlignToDefault(config.defaultPort);
        Serial.ConfigureAnomalyDetection(
            config.anomalyEnable, config.anomalyBufferSize, config.anomalyThreshold);

        BindUIEvents();

        Debug.LogWarning($"[V8.20 ACTIVE][MotionCaptureController.Start] Build={BuildVersion}；{zigbeeScheduledTransmitRateHz}Hz错峰自动重同步；测试选择={SensorTestSelectionSummary}；腿部配对≤{legDriveMaxPairSkewSeconds * 1000f:F0}ms/年龄≤{legDriveMaxPairAgeSeconds * 1000f:F0}ms；单路断流保持；恢复限速腿={legMaximumAngularSpeedDegPerSec:F0}°/s、上肢={upperBodyMaximumAngularSpeedDegPerSec:F0}°/s；AI诊断日志=连接即增量写盘；后台运行={Application.runInBackground}");
    }

    private void Update()
    {
        if (processor == null || Serial == null) return;

        // 本帧只从唯一入口消费串口数据。稳定计数在真实新帧回调中更新。
        processor.UpdateFromParser(Serial.Parser, State, OnFrameDequeued);
        MaintainZigbeeScheduleSynchronization();
        processor.UpdateStability(State);
        bool runtimeFaultStoppedDriving = TryHandleRuntimeLinkFault();
        if (!runtimeFaultStoppedDriving)
            TryResumeSuspendedDriving();
        if (State.IsDriving && !runtimeFaultStoppedDriving)
            UpdateKneeMeasurements();

        bool isStable = CalibrationInputsStable(out _);

        UpdateCalibrationCountdown();

        bool leftReady = !leftLegParticipatesInCalibration || (leftLegDriver != null && leftLegDriver.IsCalibrated);
        bool rightReady = !rightLegParticipatesInCalibration || (rightLegDriver != null && rightLegDriver.IsCalibrated);
        bool armsReady = !(leftArmParticipatesInCalibration || rightArmParticipatesInCalibration) ||
                         (armDriver != null && armDriver.IsCalibrated);
        bool genericReady = AreGenericStandaloneParticipantsCalibrated();

        State.Refresh(
            Serial.IsConnected,
            State.CheckHasAnyData(),
            leftReady && rightReady && armsReady && genericReady,
            State.IsDriving,
            isStable);

        // V2：自动标定由 Controller 驱动，不依赖 OnGUI 或鼠标点击。
        // 因此穿戴者可以一直保持 A-Pose，数据稳定后系统会自行开始倒计时和采样。
        UpdateAutomaticCalibration();

        if (State.IsDriving && legDebugLogging)
        {
            if (driveLeftLeg && leftLegDriver != null)
            {
                leftLegDriver.TryLogDebug(
                    processor.TransformedQuaternions,
                    GetBoneTransform(LeftThighIndex),
                    GetBoneTransform(LeftCalfIndex));
            }

            if (driveRightLeg && rightLegDriver != null)
            {
                rightLegDriver.TryLogDebug(
                    processor.TransformedQuaternions,
                    GetBoneTransform(RightThighIndex),
                    GetBoneTransform(RightCalfIndex));
            }
        }

        logger.SyncState(Serial.IsConnected);
        logger.FlushIfDue();
        UpdateAiDiagnosticLog();
    }

    private void LateUpdate()
    {
        if (State == null) return;
        if (!State.IsDriving)
        {
            // 等待运行数据或链路暂停时只恢复人物姿势；绝不调用processor.Reset，
            // 因而九路Hz、帧龄、最后四元数和协议错误计数会继续保留并更新。
            if (IsWaitingForRuntimeData)
                ResetAllBonesToRest();
            return;
        }
        if (bones == null || restLocalRotations == null) return;

        ResetUndrivenBonesToRest();

        ApplyInspectorSettingsToLeftLegDriver();
        ApplyInspectorSettingsToRightLegDriver();
        ApplyContinuousArmPreset();
        ApplyInspectorSettingsToArmDriver();

        bool leftThighFresh = IsSensorFreshForDriving(LeftThighSensorIndex);
        bool leftCalfFresh = IsSensorFreshForDriving(LeftCalfSensorIndex);
        bool rightThighFresh = IsSensorFreshForDriving(RightThighSensorIndex);
        bool rightCalfFresh = IsSensorFreshForDriving(RightCalfSensorIndex);

        // V8.20：单路短时断流只停止消费该路，不把骨骼写回Rest。
        // 这样不会在“超时/恢复”之间反复跳回初始姿势；恢复时由驱动器限速追赶。
        bool leftPairAvailable = PrepareTimePairedLegDriveInput(
            true, leftThighFresh, leftCalfFresh, out Quaternion[] leftInput);
        bool rightPairAvailable = PrepareTimePairedLegDriveInput(
            false, rightThighFresh, rightCalfFresh, out Quaternion[] rightInput);

        if (leftLegParticipatesInCalibration && leftLegDriver != null && leftLegDriver.IsCalibrated &&
            leftThighFresh)
        {
            bool applied = leftLegDriver.ApplyAvailable(
                leftInput,
                GetBoneTransform(LeftThighIndex),
                GetBoneTransform(LeftCalfIndex),
                leftThighFresh,
                leftPairAvailable);
            if (!applied && !string.IsNullOrEmpty(leftLegDriver.LastError))
                Debug.LogWarning($"[LeftLegDrive] 本帧未应用：{leftLegDriver.LastError}");
        }

        if (rightLegParticipatesInCalibration && rightLegDriver != null && rightLegDriver.IsCalibrated &&
            rightThighFresh)
        {
            bool applied = rightLegDriver.ApplyAvailable(
                rightInput,
                GetBoneTransform(RightThighIndex),
                GetBoneTransform(RightCalfIndex),
                rightThighFresh,
                rightPairAvailable);
            if (!applied && !string.IsNullOrEmpty(rightLegDriver.LastError))
                Debug.LogWarning($"[RightLegDrive] 本帧未应用：{rightLegDriver.LastError}");
        }

        Quaternion[] transformed = processor?.TransformedQuaternions;
        if (leftStandaloneCalfParticipatesInCalibration && leftStandaloneCalfDriver != null &&
            leftStandaloneCalfDriver.IsCalibrated && IsSensorFreshForDriving(LeftCalfSensorIndex) &&
            transformed != null && transformed.Length > LeftCalfSensorIndex)
        {
            leftStandaloneCalfDriver.SmoothingEnabled = runtimeSmoothingEnabled;
            leftStandaloneCalfDriver.SmoothingSpeed = Mathf.Max(0.01f, legOutputSmoothingSpeed);
            leftStandaloneCalfDriver.MaximumAngularSpeedDegPerSec = Mathf.Clamp(legMaximumAngularSpeedDegPerSec, 90f, 540f);
            bool applied = leftStandaloneCalfDriver.Apply(
                transformed[LeftCalfSensorIndex], GetBoneTransform(LeftCalfIndex));
            if (!applied && !string.IsNullOrEmpty(leftStandaloneCalfDriver.LastError))
                Debug.LogWarning($"[LeftStandaloneCalfDrive] 本帧未应用：{leftStandaloneCalfDriver.LastError}");
        }

        if (rightStandaloneCalfParticipatesInCalibration && rightStandaloneCalfDriver != null &&
            rightStandaloneCalfDriver.IsCalibrated && IsSensorFreshForDriving(RightCalfSensorIndex) &&
            transformed != null && transformed.Length > RightCalfSensorIndex)
        {
            rightStandaloneCalfDriver.SmoothingEnabled = runtimeSmoothingEnabled;
            rightStandaloneCalfDriver.SmoothingSpeed = Mathf.Max(0.01f, legOutputSmoothingSpeed);
            rightStandaloneCalfDriver.MaximumAngularSpeedDegPerSec = Mathf.Clamp(legMaximumAngularSpeedDegPerSec, 90f, 540f);
            bool applied = rightStandaloneCalfDriver.Apply(
                transformed[RightCalfSensorIndex], GetBoneTransform(RightCalfIndex));
            if (!applied && !string.IsNullOrEmpty(rightStandaloneCalfDriver.LastError))
                Debug.LogWarning($"[RightStandaloneCalfDrive] 本帧未应用：{rightStandaloneCalfDriver.LastError}");
        }

        // 05脊柱是01/03的父层级，必须先写入，再由大臂根据当前父世界姿态换算局部旋转。
        ApplyGenericStandaloneDriverForIndex((int)BoneIndex.Spine, transformed);

        bool leftArmFresh = IsSensorFreshForDriving(LeftArmIndex);
        bool leftForeArmFresh = IsSensorFreshForDriving(LeftForeArmIndex);
        bool rightArmFresh = IsSensorFreshForDriving(RightArmIndex);
        bool rightForeArmFresh = IsSensorFreshForDriving(RightForeArmIndex);
        // 手臂也采用同样的“断流保持”策略；不可用通道不会传给ArmPoseDriver。

        if ((leftArmParticipatesInCalibration || rightArmParticipatesInCalibration) &&
            armDriver != null && armDriver.IsCalibrated &&
            (leftArmFresh || leftForeArmFresh || rightArmFresh || rightForeArmFresh))
        {
            armDriver.LeftForeArmInputAvailable = fullBodyDiagnosticMode && leftForeArmFresh;
            bool armApplied = armDriver.ApplyAvailable(
                processor.TransformedQuaternions,
                GetBoneTransform(LeftArmIndex),
                GetBoneTransform(LeftForeArmIndex),
                GetBoneTransform(RightArmIndex),
                GetBoneTransform(RightForeArmIndex),
                avatarRoot,
                leftArmFresh,
                leftForeArmFresh,
                rightArmFresh,
                rightForeArmFresh);

            if (!armApplied && !string.IsNullOrEmpty(armDriver.LastError))
                Debug.LogWarning($"[ArmDrive] 本帧未应用：{armDriver.LastError}");
        }

        // 非全身诊断模式下，02/04仍可走通用单传感器路径；全身模式由ArmPoseDriver直接驱动。
        ApplyGenericStandaloneDriverForIndex(LeftForeArmIndex, transformed);
        ApplyGenericStandaloneDriverForIndex(RightForeArmIndex, transformed);
    }

    private void ApplyGenericStandaloneDriverForIndex(int sensorIndex, Quaternion[] transformed)
    {
        if (!IsGenericStandaloneParticipant(sensorIndex) || transformed == null || processor == null ||
            genericStandaloneDrivers == null || sensorIndex < 0 ||
            sensorIndex >= transformed.Length || sensorIndex >= genericStandaloneDrivers.Length ||
            !IsSensorFreshForDriving(sensorIndex))
            return;

        StandaloneBonePoseDriver driver = genericStandaloneDrivers[sensorIndex];
        if (driver == null || !driver.IsCalibrated) return;
        driver.SmoothingEnabled = runtimeSmoothingEnabled;
        driver.SmoothingSpeed = Mathf.Max(0.01f, legOutputSmoothingSpeed);
        driver.MaximumAngularSpeedDegPerSec = Mathf.Clamp(upperBodyMaximumAngularSpeedDegPerSec, 90f, 720f);
        if (!driver.Apply(transformed[sensorIndex], GetBoneTransform(sensorIndex)) &&
            !string.IsNullOrEmpty(driver.LastError))
            Debug.LogWarning($"[GenericStandaloneDrive {sensorIndex + 1:00}] 本帧未应用：{driver.LastError}");
    }

    private void OnApplicationQuit() => Cleanup();
    private void OnDisable() => Cleanup();

    private void Cleanup()
    {
        WriteAiDiagnosticSnapshot("cleanup_final");
        aiDiagnosticLogger?.LogEvent("session_closing", GetAiDiagnosticStateName(), "Unity组件退出或停用");
        aiDiagnosticLogger?.Close("unity_cleanup");
        Serial?.Dispose();
        logger?.Dispose();
    }

    private void UpdateAiDiagnosticLog()
    {
        if (aiDiagnosticLogger == null || !aiDiagnosticLogger.IsLogging) return;

        string stateName = GetAiDiagnosticStateName();
        if (!string.Equals(stateName, lastAiDiagnosticState, StringComparison.Ordinal))
        {
            aiDiagnosticLogger.LogEvent(
                "state_changed",
                stateName,
                string.IsNullOrEmpty(lastAiDiagnosticState)
                    ? $"初始状态->{stateName}"
                    : $"{lastAiDiagnosticState}->{stateName}",
                calibrationCountdownStatus);
            lastAiDiagnosticState = stateName;
            WriteAiDiagnosticSnapshot("state_changed");
        }

        if (Time.unscaledTime < nextAiDiagnosticSnapshotTime) return;
        nextAiDiagnosticSnapshotTime = Time.unscaledTime + 1f;
        WriteAiDiagnosticSnapshot("periodic");
    }

    private string GetAiDiagnosticStateName()
    {
        if (Serial == null || !Serial.IsConnected) return "DISCONNECTED";
        if (State != null && State.IsDriving) return "DRIVING";
        if (runtimeDriveSuspended) return "RUNTIME_SUSPENDED";
        if (calibrationLockedWaitingForRuntime) return "CALIBRATION_LOCKED_WAITING_RUNTIME";
        if (calibrationCountdownActive && calibrationSamplingActive) return "CALIBRATION_SAMPLING";
        if (calibrationCountdownActive) return "CALIBRATION_COUNTDOWN";
        if (State == null || !State.HasAnyData) return "WAITING_DATA";
        if (!State.IsStable) return "WAITING_STABILITY";
        return "CONNECTED_READY";
    }

    private void WriteAiDiagnosticSnapshot(string trigger)
    {
        if (aiDiagnosticLogger == null || !aiDiagnosticLogger.IsLogging) return;

        SerialParser parser = Serial != null ? Serial.Parser : null;
        var parserSnapshot = new AiDiagnosticLogger.ParserSnapshot
        {
            Connected = Serial != null && Serial.IsConnected,
            Port = Serial != null ? Serial.CurrentPort : string.Empty,
            Baud = selectedBaud,
            PayloadLength = parser != null ? parser.LastPayloadLength : 0,
            XorFailures = parser != null ? parser.ChecksumFailCount : 0,
            CrcFailures = parser != null ? parser.Crc16FailCount : 0,
            InvalidPayloadLengths = parser != null ? parser.InvalidPayloadLengthCount : 0,
            InvalidQuaternions = parser != null ? parser.InvalidQuaternionCount : 0,
            InvalidDeviceIds = parser != null ? parser.InvalidDeviceIdCount : 0,
            ParityErrors = parser != null ? parser.ParityErrorCount : 0,
            FrameErrors = parser != null ? parser.FrameErrorCount : 0,
            OverrunErrors = parser != null ? parser.OverrunErrorCount : 0,
            DuplicateIdConflicts = parser != null ? parser.DuplicateLogicalIdConflictCount : 0,
            QueueDepth = parser != null ? parser.QueueCount : 0,
            QueueCapacity = parser != null ? parser.GlobalQueueCapacity : 0,
            QueueDrops = parser != null ? parser.GlobalQueueDroppedFrameCount : 0,
            BacklogDiscarded = BacklogDiscardedFrameCount
        };

        int count = config != null ? Mathf.Max(0, config.deviceCount) : 9;
        var sensors = new AiDiagnosticLogger.SensorSnapshot[count];
        Quaternion[] quaternions = TransformedQuaternions;
        for (int i = 0; i < count; i++)
        {
            bool hasV2 = parser != null && parser.HasV2Source(i);
            sensors[i] = new AiDiagnosticLogger.SensorSnapshot
            {
                Id = i + 1,
                Role = GetSensorRoleLabel(i),
                Required = fullBodyDiagnosticMode ? i < 9 : IsSensorRequiredForCurrentDrive(i),
                Online = IsSensorOnline(i),
                RuntimeReady = IsSensorRuntimeReady(i),
                Stable = IsSensorStable(i),
                Calibration = GetSensorCalibrationUiState(i).ToString(),
                ReceiveHz = GetSensorFrameRateHz(i),
                SourceHz = hasV2 ? parser.GetSourceReportedFrameRateHz(i) : 0f,
                AgeMs = GetSensorFrameAgeMilliseconds(i),
                DeliveryPercent = hasV2 ? parser.GetSourceDeliveryPercent(i) : 0f,
                SourceLost = hasV2 ? parser.GetSourceLostFrameCount(i) : 0L,
                SourceDuplicate = hasV2 ? parser.GetSourceDuplicateFrameCount(i) : 0L,
                SourceOutOfOrder = hasV2 ? parser.GetSourceOutOfOrderFrameCount(i) : 0L,
                SourceRestart = hasV2 ? parser.GetSourceRestartCount(i) : 0L,
                DuplicateLogicalId = hasV2 ? parser.GetDuplicateLogicalIdCount(i) : 0L,
                HardwareId = hasV2 ? parser.GetHardwareId(i) : 0u,
                SourceSequence = hasV2 ? parser.GetLastSourceSequence(i) : 0u,
                SenderTickMs = hasV2 ? parser.GetLastSenderTickMs(i) : 0u,
                SourceFlags = hasV2 ? parser.GetLastSourceFlags(i) : 0,
                SourceClockReliable = hasV2 && parser.IsSourceClockReliable(i),
                SourceMainClockHealthy = hasV2 && parser.IsSourceMainClockHealthy(i),
                SourceSlottedTransmit = hasV2 && parser.IsSourceSlottedTransmit(i),
                SourceLinkSynchronized = hasV2 && parser.IsSourceLinkSynchronized(i),
                SourceBacklogAgeMs = hasV2 ? parser.GetSourceBacklogAgeMs(i) : 0f,
                SourceMaximumBacklogAgeMs = hasV2 ? parser.GetSourceMaximumBacklogAgeMs(i) : 0f,
                SourceStaleRejected = processor?.DataHub != null
                    ? processor.DataHub.GetStaleSourceFrameCount(i)
                    : 0L,
                InputSequenceGap = GetSensorInputSequenceGapCount(i),
                CalibrationAccepted = GetSensorCalibrationAcceptedSamples(i),
                CalibrationRequired = GetSensorCalibrationRequiredSamples(i),
                CalibrationRejected = GetSensorCalibrationRejectedSamples(i),
                CalibrationRestarts = GetSensorCalibrationRestartCount(i),
                RuntimeFaults = GetSensorRuntimeFaultCount(i),
                LegPairRequired = i == LeftThighSensorIndex || i == LeftCalfSensorIndex
                    ? leftCalfParticipatesInCalibration
                    : (i == RightThighSensorIndex || i == RightCalfSensorIndex) && rightCalfParticipatesInCalibration,
                LegPairFresh = i == LeftThighSensorIndex || i == LeftCalfSensorIndex
                    ? leftCalfParticipatesInCalibration && LeftLegDrivePairFresh
                    : (i == RightThighSensorIndex || i == RightCalfSensorIndex) &&
                      rightCalfParticipatesInCalibration && RightLegDrivePairFresh,
                LegPairSkewMs = i == LeftThighSensorIndex || i == LeftCalfSensorIndex
                    ? lastLeftLegDrivePairSkewSeconds * 1000d
                    : (i == RightThighSensorIndex || i == RightCalfSensorIndex)
                        ? lastRightLegDrivePairSkewSeconds * 1000d
                        : double.PositiveInfinity,
                LegPairAgeMs = GetLegDrivePairAgeMilliseconds(i),
                LegPairHoldCount = i == LeftThighSensorIndex || i == LeftCalfSensorIndex
                    ? leftLegPairHoldCount
                    : (i == RightThighSensorIndex || i == RightCalfSensorIndex) ? rightLegPairHoldCount : 0L,
                Q = quaternions != null && i < quaternions.Length
                    ? quaternions[i]
                    : Quaternion.identity
            };
        }

        aiDiagnosticLogger.LogSnapshot(
            GetAiDiagnosticStateName(),
            string.IsNullOrEmpty(trigger)
                ? calibrationCountdownStatus
                : $"[{trigger}] {calibrationCountdownStatus}",
            LastRuntimeFaultSummary,
            parserSnapshot,
            sensors);
    }

    private void BindUIEvents()
    {
        MotionCaptureUI ui = GetComponent<MotionCaptureUI>();
        if (ui == null) return;

        ui.OnConnectRequested += HandleConnect;
        ui.OnDisconnectRequested += HandleDisconnect;
        ui.OnRefreshPortsRequested += HandleRefreshPorts;
        ui.OnPortSelected += HandlePortSelected;
        ui.OnPortManualInput += v => Serial.SetPortManual(v);
        ui.OnBeginDrivingRequested += HandleBeginDriving;
        ui.OnResetRequested += HandleReset;

        ui.OnSmoothingChanged += (en, spd) =>
        {
            runtimeSmoothingEnabled = en;
            legOutputSmoothingSpeed = Mathf.Max(0.01f, spd);

            if (leftLegDriver != null)
            {
                leftLegDriver.SmoothingEnabled = en;
                leftLegDriver.SmoothingSpeed = legOutputSmoothingSpeed;
            }

            if (rightLegDriver != null)
            {
                rightLegDriver.SmoothingEnabled = en;
                rightLegDriver.SmoothingSpeed = legOutputSmoothingSpeed;
            }

            if (armDriver != null)
            {
                armDriver.SmoothingEnabled = runtimeSmoothingEnabled;
                armDriver.SmoothingSpeed = Mathf.Max(0.01f, armSmoothingSpeed);
            }
        };

        ui.OnRequireAllDevicesChanged += v =>
        {
            requireAllDevices = v;
            config.requireAllDevices = v;
        };
        ui.OnMinStableDevicesChanged += v => config.minStableDevices = v;
        ui.OnSaveEnabledChanged += v => logger.SaveEnabled = v;
        ui.OnStartRecordingRequested += HandleStartRecording;
        ui.OnStopRecordingRequested += HandleStopRecording;
        ui.OnDiagnosticMarkerRequested += HandleDiagnosticMarker;

        ui.OnLimitsToggled += _ => { };
        ui.OnTwistSwingToggled += _ => { };
    }

    private void HandleConnect(string port, int baud)
    {
        selectedBaud = baud;

        // 每次点击连接都视为一次独立测试。先结束上次Excel，再为本次测试创建
        // 项目相对目录 Logs/yyyyMMdd_HHmmss_fff，AI日志和Excel统一写入其中。
        logger?.Close();
        string diagnosticDirectory = CreateTestLogDirectory();
        logger?.SetExportDirectory(diagnosticDirectory);
        bool diagnosticOpened = aiDiagnosticLogger != null && aiDiagnosticLogger.Open(
            diagnosticDirectory,
            BuildVersion,
            Application.productName,
            Application.unityVersion,
            port,
            baud,
            config != null ? config.deviceCount : 9);
        nextAiDiagnosticSnapshotTime = Time.unscaledTime;
        lastAiDiagnosticState = string.Empty;
        aiDiagnosticMarkerCount = 0;
        if (diagnosticOpened)
            aiDiagnosticLogger.LogEvent("connect_requested", "CONNECTING", $"请求打开{port}@{baud}");
        else if (aiDiagnosticLogger != null)
            Debug.LogError($"[AI诊断日志] 创建失败：{aiDiagnosticLogger.LastError}");

        ResetRuntimeLinkState(true);
        ResetAutomaticCalibrationState();
        ResetArmSamplingState();
        ClearSensorCalibrationResults();
        calibrationCountdownStatus = "连接后将自动等待传感器稳定";
        // 新连接必须从空队列和空数据中心开始，禁止上一次连接的旧帧参与标定。
        Serial.ResetParser();
        processor?.DataHub?.Reset();
        ClearDeviceAvailability();
        bool connected = Serial.Connect(port, baud);
        bool scheduleCommandSent = false;
        zigbeeScheduleToken = unchecked((uint)Environment.TickCount);
        if (connected && configureZigbeeScheduleOnConnect)
        {
            int rateHz = Mathf.Clamp(zigbeeScheduledTransmitRateHz, 1, 10);
            scheduleCommandSent = Serial.ConfigureScheduledLink(rateHz, zigbeeScheduleToken);
            nextZigbeeScheduleSyncTime = Time.unscaledTime + Mathf.Clamp(zigbeeScheduleRetrySeconds, 1f, 10f);
            aiDiagnosticLogger?.LogEvent(
                scheduleCommandSent ? "zigbee_schedule_sent" : "zigbee_schedule_send_failed",
                scheduleCommandSent ? "LINK_CONFIGURED" : "LINK_CONFIG_FAILED",
                $"广播错峰配置：{rateHz}Hz，Token=0x{zigbeeScheduleToken:X8}；节点V2标志bit4用于确认，漏收或重启将自动重发");
        }
        else
        {
            nextZigbeeScheduleSyncTime = -1f;
        }
        aiDiagnosticLogger?.LogEvent(
            connected ? "connect_succeeded" : "connect_failed",
            connected ? "CONNECTED" : "CONNECT_FAILED",
            connected
                ? $"串口已打开：{port}@{baud}；错峰同步={(configureZigbeeScheduleOnConnect ? (scheduleCommandSent ? "已发送" : "发送失败") : "关闭")}"
                : $"串口打开失败：{port}@{baud}");
        WriteAiDiagnosticSnapshot("connect_result");
    }

    private string CreateTestLogDirectory()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string logsRoot = Path.Combine(projectRoot, "Logs");
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string folderName = timestamp;
        string candidate = Path.Combine(logsRoot, folderName);
        try
        {
            Directory.CreateDirectory(logsRoot);
            int suffix = 2;
            while (Directory.Exists(candidate))
            {
                folderName = $"{timestamp}_{suffix}";
                candidate = Path.Combine(logsRoot, folderName);
                suffix++;
            }

            Directory.CreateDirectory(candidate);
            CurrentTestLogRelativeDirectory = Path.Combine("Logs", folderName);
        }
        catch (Exception ex)
        {
            // 日志目录异常不能阻止串口主程序继续连接；后续两个日志器会各自报告创建失败。
            CurrentTestLogRelativeDirectory = "Logs（创建失败）";
            Debug.LogError($"[测试日志目录] 无法创建 {candidate}：{ex.Message}");
        }
        return candidate;
    }

    private void HandleStartRecording()
    {
        if (logger == null) return;
        if (State == null || !State.IsDriving)
        {
            Debug.LogWarning("[Excel记录] V69 会在人物开始运动时自动记录；请先完成倒计时标定并进入驱动。");
            return;
        }
        logger.SaveEnabled = true;
        logger.Open();
    }

    private void HandleStopRecording()
    {
        logger?.Close();
    }

    private void HandleDiagnosticMarker()
    {
        if (aiDiagnosticLogger == null || !aiDiagnosticLogger.IsLogging) return;
        aiDiagnosticMarkerCount++;
        aiDiagnosticLogger.LogEvent(
            "user_problem_marker",
            GetAiDiagnosticStateName(),
            $"客户手动标记第{aiDiagnosticMarkerCount}个异常时刻",
            calibrationCountdownStatus);
        WriteAiDiagnosticSnapshot($"user_problem_marker_{aiDiagnosticMarkerCount}");
    }

    private void HandleDisconnect()
    {
        WriteAiDiagnosticSnapshot("disconnect_final");
        aiDiagnosticLogger?.LogEvent("disconnect_requested", GetAiDiagnosticStateName(), "用户请求断开连接");
        ResetRuntimeLinkState(true);
        CancelCalibrationCountdown("已断开连接");
        ResetAutomaticCalibrationState();
        ClearSensorCalibrationResults();
        Serial.Disconnect();
        Serial.ResetParser();
        logger.Close();
        State.SetConnected(false);

        leftLegDriver?.Reset();
        rightLegDriver?.Reset();
        armDriver?.Reset();
        leftStandaloneCalfDriver?.Reset();
        rightStandaloneCalfDriver?.Reset();
        ResetGenericStandaloneDrivers();
        ResetArmSamplingState();
        kneeMeasurementCalibrationDeadlineTime = -1f;
        processor?.DataHub?.Reset();
        ClearDeviceAvailability();
        aiDiagnosticLogger?.Close("user_disconnect");
    }

    private void HandleRefreshPorts()
    {
        Serial.RefreshPortsViaController();
    }

    private void HandlePortSelected(int index)
    {
        Serial.SelectPort(index);
    }

    private void HandleBeginDriving()
    {
        if (State == null || Serial == null || !Serial.IsConnected)
        {
            Debug.LogWarning("[准备标定被阻断] 请先连接传感器。");
            return;
        }

        if (State.IsDriving || calibrationCountdownActive || IsWaitingForRuntimeData)
            return;

        if (!CalibrationInputsStable(out string reason))
        {
            Debug.LogWarning($"[准备标定被阻断] {reason}");
            calibrationCountdownStatus = reason;
            return;
        }

        if (armDriver == null || leftLegDriver == null || rightLegDriver == null)
        {
            Debug.LogError("[准备标定被阻断] 骨骼驱动器未初始化");
            return;
        }

        StartCalibrationCountdown();
    }

    /// <summary>
    /// V2 无点击单人流程：必需输入稳定后先锁存一个短保持时间，再调用与手动按钮
    /// 完全相同的标定入口。中心按钮仍可用于主动重试，但不再是完成标定的必要条件。
    /// </summary>
    private void UpdateAutomaticCalibration()
    {
        if (IsWaitingForRuntimeData)
        {
            automaticCalibrationStableSince = -1f;
            return;
        }

        if (!automaticCalibrationEnabled || State == null || Serial == null ||
            !Serial.IsConnected || !State.HasAnyData || State.IsDriving || calibrationCountdownActive)
        {
            automaticCalibrationStableSince = -1f;
            return;
        }

        if (Time.time < automaticCalibrationNextAttemptTime)
            return;

        if (!CalibrationInputsStable(out string reason))
        {
            automaticCalibrationStableSince = -1f;
            if (string.IsNullOrEmpty(calibrationCountdownStatus) ||
                calibrationCountdownStatus.StartsWith("自动等待") ||
                calibrationCountdownStatus.StartsWith("连接后"))
            {
                calibrationCountdownStatus = $"自动等待：{reason}";
            }
            return;
        }

        if (automaticCalibrationStableSince < 0f)
        {
            automaticCalibrationStableSince = Time.time;
            calibrationCountdownStatus = "必需传感器已稳定，自动标定即将开始";
            return;
        }

        float holdSeconds = Mathf.Clamp(automaticCalibrationStableHoldSeconds, 0.3f, 3f);
        if (Time.time - automaticCalibrationStableSince < holdSeconds)
            return;

        HandleBeginDriving();

        // 如果入口检查仍阻断（例如恰好在调用时断流），不要每帧重复触发和刷屏。
        if (!calibrationCountdownActive && !State.IsDriving)
        {
            automaticCalibrationStableSince = -1f;
            automaticCalibrationNextAttemptTime = Time.time +
                Mathf.Max(1f, automaticCalibrationRetryDelaySeconds);
        }
    }

    /// <summary>
    /// V8只检查“本次实际在线且允许驱动”的传感器。未连接设备不再阻断，
    /// 因此可以只接一个传感器，也可以接任意多个传感器。
    /// </summary>
    private bool CalibrationInputsStable(out string reason)
    {
        if (Serial != null && Serial.Parser != null &&
            Serial.Parser.DuplicateLogicalIdConflictCount > 0)
        {
            reason = "检测到不同硬件使用了相同设备ID；请修正设备01~09身份后重新连接";
            return false;
        }

        bool anyAvailable = false;

        bool allowLeftArm = IsSensorSelectedForTesting(LeftArmIndex, driveArms && driveLeftArm);
        bool allowLeftForeArm = IsSensorSelectedForTesting(LeftForeArmIndex, driveLeftForeArm);
        bool allowRightArm = IsSensorSelectedForTesting(RightArmIndex, driveArms && driveRightArm);
        bool allowRightForeArm = IsSensorSelectedForTesting(RightForeArmIndex, driveRightForeArm);
        bool allowSpine = IsSensorSelectedForTesting((int)BoneIndex.Spine, false);
        bool allowLeftThigh = IsSensorSelectedForTesting(LeftThighSensorIndex, driveLeftLeg);
        bool allowLeftCalfSelected = IsSensorSelectedForTesting(LeftCalfSensorIndex, driveLeftCalf);
        bool allowRightThigh = IsSensorSelectedForTesting(RightThighSensorIndex, driveRightLeg);
        bool allowRightCalfSelected = IsSensorSelectedForTesting(RightCalfSensorIndex, driveRightCalf);

        bool leftArmAvailable = allowLeftArm && IsSensorAvailableForSession(LeftArmIndex, LeftArmIndex);
        bool rightArmAvailable = allowRightArm && IsSensorAvailableForSession(RightArmIndex, RightArmIndex);

        if (!CheckCandidateStable(allowLeftArm,
                LeftArmIndex, LeftArmIndex, "左大臂01", ref anyAvailable, out reason))
            return false;
        if (!CheckCandidateStable(
                allowLeftForeArm && (fullBodyDiagnosticMode ||
                    !(isolateUpperArmTestingFromForearms && leftArmAvailable)),
                LeftForeArmIndex, LeftForeArmIndex, "左小臂02单独", ref anyAvailable, out reason))
            return false;

        if (!CheckCandidateStable(allowRightArm,
                RightArmIndex, RightArmIndex, "右大臂03", ref anyAvailable, out reason))
            return false;
        if (!CheckCandidateStable(
                allowRightForeArm && (fullBodyDiagnosticMode ||
                    !(isolateUpperArmTestingFromForearms && rightArmAvailable)),
                RightForeArmIndex, RightForeArmIndex, "右小臂04单独", ref anyAvailable, out reason))
            return false;

        if (!CheckCandidateStable(allowSpine,
                (int)BoneIndex.Spine, (int)BoneIndex.Spine, "脊柱05单独",
                ref anyAvailable, out reason))
            return false;

        bool leftThighAvailable = allowLeftThigh && IsSensorAvailableForSession(
            LeftThighSensorIndex, LeftThighIndex);
        bool rightThighAvailable = allowRightThigh && IsSensorAvailableForSession(
            RightThighSensorIndex, RightThighIndex);

        if (!CheckCandidateStable(allowLeftThigh,
                LeftThighSensorIndex, LeftThighIndex, "左大腿06",
                ref anyAvailable, out reason))
            return false;
        if (!CheckCandidateStable(allowRightThigh,
                RightThighSensorIndex, RightThighIndex, "右大腿08",
                ref anyAvailable, out reason))
            return false;

        bool allowLeftCalf = allowLeftCalfSelected &&
            (leftThighAvailable || allowStandaloneCalfTesting);
        bool allowRightCalf = allowRightCalfSelected &&
            (rightThighAvailable || allowStandaloneCalfTesting);

        if (!CheckCandidateStable(allowLeftCalf,
                LeftCalfSensorIndex, LeftCalfIndex, "左小腿07",
                ref anyAvailable, out reason))
            return false;
        if (!CheckCandidateStable(allowRightCalf,
                RightCalfSensorIndex, RightCalfIndex, "右小腿09",
                ref anyAvailable, out reason))
            return false;

        if (!anyAvailable)
        {
            reason = sensorTestSelectionMode == SensorTestSelectionMode.ManualIdList
                ? $"手动测试列表[{GetNormalizedManualSensorIdList()}]中没有可用且稳定的传感器"
                : "尚未收到01~09任意一个可驱动传感器的有效数据";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool CheckCandidateStable(
        bool enabledForTesting,
        int sensorIndex,
        int boneIndex,
        string label,
        ref bool anyAvailable,
        out string reason)
    {
        reason = string.Empty;
        if (!enabledForTesting)
            return true;

        bool available = IsSensorAvailableForSession(sensorIndex, boneIndex);
        if (!available)
        {
            bool mustExist = sensorTestSelectionMode == SensorTestSelectionMode.ManualIdList ||
                             !autoSelectAvailableSensors;
            if (mustExist)
            {
                reason = $"{label}已被本轮选择，但未在线或目标骨骼不可用";
                return false;
            }
            return true;
        }

        anyAvailable = true;
        if (!processor.IsDeviceStable(sensorIndex))
        {
            reason = $"{label}已在线但尚未稳定：{processor.GetStableFrameCount(sensorIndex)}/" +
                     $"{config.requiredStableFrames}";
            return false;
        }
        return true;
    }

    private bool IsSensorAvailableForSession(int sensorIndex, int boneIndex)
    {
        if (processor == null || !processor.IsDeviceOnline(sensorIndex))
            return false;
        return CalibrationSensorReady(
            sensorIndex, boneIndex, GetSensorRoleLabel(sensorIndex), out _);
    }

    private bool IsSensorSelectedForTesting(int sensorIndex, bool legacyEnabled)
    {
        if (sensorIndex < 0 || sensorIndex > 8) return false;
        if (sensorTestSelectionMode == SensorTestSelectionMode.ManualIdList)
            return IsManualSensorSelected(sensorIndex);
        return autoSelectAvailableSensors || legacyEnabled;
    }

    private bool IsManualSensorSelected(int sensorIndex)
    {
        if (sensorIndex < 0 || sensorIndex > 8) return false;
        string text = manualTestSensorIds ?? string.Empty;
        int value = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            char c = i < text.Length ? text[i] : '\0';
            if (c >= '0' && c <= '9')
            {
                int digit = c - '0';
                value = value < 0 ? digit : value * 10 + digit;
                continue;
            }

            if (value >= 1 && value <= 9 && value - 1 == sensorIndex)
                return true;
            value = -1;
        }
        return false;
    }

    private string GetNormalizedManualSensorIdList()
    {
        string result = string.Empty;
        for (int i = 0; i < 9; i++)
        {
            if (!IsManualSensorSelected(i)) continue;
            if (result.Length > 0) result += ",";
            result += (i + 1).ToString("00");
        }
        return result.Length > 0 ? result : "未选择";
    }

    private void ResetAutomaticCalibrationState()
    {
        automaticCalibrationStableSince = -1f;
        automaticCalibrationNextAttemptTime = 0f;
    }

    private void ResetRuntimeLinkState(bool clearFaultHistory)
    {
        calibrationLockedWaitingForRuntime = false;
        runtimeDriveSuspended = false;
        runtimeRecoveryFreshSince = -1f;
        leftLegPairHeld = false;
        rightLegPairHeld = false;
        leftLegPairHoldCount = 0L;
        rightLegPairHoldCount = 0L;
        lastLeftLegDrivePairTimestampUtc = DateTime.MinValue;
        lastRightLegDrivePairTimestampUtc = DateTime.MinValue;
        lastLeftLegDrivePairSkewSeconds = double.PositiveInfinity;
        lastRightLegDrivePairSkewSeconds = double.PositiveInfinity;
        nextZigbeeScheduleSyncTime = -1f;
        lastReportedSynchronizedSourceCount = -1;
        if (observedSourceRestartCounts != null)
            Array.Clear(observedSourceRestartCounts, 0, observedSourceRestartCounts.Length);
        if (lastLegInputStepLogTimes != null)
            Array.Clear(lastLegInputStepLogTimes, 0, lastLegInputStepLogTimes.Length);
        if (runtimeReadinessStartSequences != null)
        {
            for (int i = 0; i < runtimeReadinessStartSequences.Length; i++)
                runtimeReadinessStartSequences[i] = -1L;
        }

        if (!clearFaultHistory) return;
        lastRuntimeFaultSensorIndex = -1;
        lastRuntimeFaultSummary = string.Empty;
        if (runtimeFaultCounts != null)
            Array.Clear(runtimeFaultCounts, 0, runtimeFaultCounts.Length);
        if (runtimeInputUnavailable != null)
            Array.Clear(runtimeInputUnavailable, 0, runtimeInputUnavailable.Length);
    }

    private void ClearSensorCalibrationResults()
    {
        if (sensorCalibrationSucceeded == null && config != null)
            sensorCalibrationSucceeded = new bool[config.deviceCount];
        if (sensorCalibrationFailed == null && config != null)
            sensorCalibrationFailed = new bool[config.deviceCount];
        if (sensorCalibrationSucceeded == null) return;
        for (int i = 0; i < sensorCalibrationSucceeded.Length; i++)
        {
            sensorCalibrationSucceeded[i] = false;
            if (sensorCalibrationFailed != null && i < sensorCalibrationFailed.Length)
                sensorCalibrationFailed[i] = false;
        }
    }

    private void MarkSensorCalibrationSucceeded(int sensorIndex)
    {
        if (sensorCalibrationSucceeded == null || sensorIndex < 0 ||
            sensorIndex >= sensorCalibrationSucceeded.Length) return;
        sensorCalibrationSucceeded[sensorIndex] = true;
        if (sensorCalibrationFailed != null && sensorIndex < sensorCalibrationFailed.Length)
            sensorCalibrationFailed[sensorIndex] = false;
    }

    private void MarkSensorCalibrationFailed(int sensorIndex)
    {
        if (sensorCalibrationFailed == null || sensorIndex < 0 ||
            sensorIndex >= sensorCalibrationFailed.Length) return;
        sensorCalibrationFailed[sensorIndex] = true;
        if (sensorCalibrationSucceeded != null && sensorIndex < sensorCalibrationSucceeded.Length)
            sensorCalibrationSucceeded[sensorIndex] = false;
    }

    public bool IsSensorOnline(int sensorIndex)
    {
        if (processor == null || !IndexInRange(sensorIndex)) return false;
        // “通信在线”只表达DataHub当前链路状态；运行阶段的严格门限单独显示，
        // 避免一台设备触发暂停后把其余八台错误显示为离线。
        return processor.IsDeviceOnline(sensorIndex);
    }

    public bool IsSensorStable(int sensorIndex) =>
        processor != null && IndexInRange(sensorIndex) && processor.IsDeviceStable(sensorIndex);

    public float GetSensorFrameRateHz(int sensorIndex) =>
        processor != null && IndexInRange(sensorIndex)
            ? processor.GetDeviceFrameRateHz(sensorIndex)
            : 0f;

    public float GetSensorSourceFrameRateHz(int sensorIndex) =>
        Serial?.Parser != null && IndexInRange(sensorIndex)
            ? Serial.Parser.GetSourceReportedFrameRateHz(sensorIndex)
            : 0f;

    public float GetSensorDeliveryPercent(int sensorIndex) =>
        Serial?.Parser != null && IndexInRange(sensorIndex)
            ? Serial.Parser.GetSourceDeliveryPercent(sensorIndex)
            : 0f;

    public int SlottedSourceCount => CountSources(false);
    public int SynchronizedSourceCount => CountSources(true);

    private void MaintainZigbeeScheduleSynchronization()
    {
        if (!configureZigbeeScheduleOnConnect || Serial == null || !Serial.IsConnected ||
            Serial.Parser == null)
            return;

        SerialParser parser = Serial.Parser;
        int deviceCount = config != null ? Mathf.Max(0, config.deviceCount) : 9;
        int v2SourceCount = 0;
        int synchronizedCount = 0;
        bool sourceRestarted = false;
        int restartedSensor = -1;

        for (int i = 0; i < deviceCount; i++)
        {
            if (!parser.HasV2Source(i)) continue;
            v2SourceCount++;
            if (parser.IsSourceLinkSynchronized(i))
                synchronizedCount++;

            long restartCount = parser.GetSourceRestartCount(i);
            if (observedSourceRestartCounts != null && i < observedSourceRestartCounts.Length)
            {
                if (restartCount > observedSourceRestartCounts[i])
                {
                    sourceRestarted = true;
                    restartedSensor = i;
                }
                observedSourceRestartCounts[i] = restartCount;
            }
        }

        if (synchronizedCount != lastReportedSynchronizedSourceCount)
        {
            aiDiagnosticLogger?.LogEvent(
                "zigbee_sync_status_changed",
                "LINK_MONITOR",
                $"V2错峰同步状态：{synchronizedCount}/{v2SourceCount}",
                $"slotted={SlottedSourceCount}, synchronized={synchronizedCount}, v2={v2SourceCount}");
            lastReportedSynchronizedSourceCount = synchronizedCount;
        }

        float now = Time.unscaledTime;
        if (sourceRestarted || nextZigbeeScheduleSyncTime < 0f)
            nextZigbeeScheduleSyncTime = now;
        if (now < nextZigbeeScheduleSyncTime)
            return;

        bool allKnownSourcesSynchronized = v2SourceCount >= deviceCount && synchronizedCount >= v2SourceCount;
        string reason = sourceRestarted
            ? $"检测到{restartedSensor + 1:00}源端重启"
            : allKnownSourcesSynchronized
                ? "周期维护"
                : $"仍有节点未同步({synchronizedCount}/{Mathf.Max(v2SourceCount, deviceCount)})";

        int rateHz = Mathf.Clamp(zigbeeScheduledTransmitRateHz, 1, 10);
        zigbeeScheduleToken = unchecked(zigbeeScheduleToken + 1u);
        bool sent = Serial.ConfigureScheduledLink(rateHz, zigbeeScheduleToken);
        aiDiagnosticLogger?.LogEvent(
            sent ? "zigbee_schedule_resync_sent" : "zigbee_schedule_resync_failed",
            sent ? "LINK_RESYNC" : "LINK_RESYNC_FAILED",
            $"{reason}，重发{rateHz}Hz错峰配置，Token=0x{zigbeeScheduleToken:X8}",
            $"v2={v2SourceCount}, synchronized={synchronizedCount}, restart_sensor={(restartedSensor >= 0 ? (restartedSensor + 1).ToString("00") : "--")}");

        float nextDelay = allKnownSourcesSynchronized
            ? Mathf.Clamp(zigbeeScheduleMaintenanceSeconds, 10f, 60f)
            : Mathf.Clamp(zigbeeScheduleRetrySeconds, 1f, 10f);
        nextZigbeeScheduleSyncTime = now + nextDelay;
    }

    private int CountSources(bool synchronizedOnly)
    {
        SerialParser parser = Serial?.Parser;
        if (parser == null)
            return 0;

        int count = 0;
        int deviceCount = config != null ? Mathf.Max(0, config.deviceCount) : 9;
        for (int i = 0; i < deviceCount; i++)
        {
            bool enabled = synchronizedOnly
                ? parser.IsSourceLinkSynchronized(i)
                : parser.IsSourceSlottedTransmit(i);
            if (enabled)
                count++;
        }
        return count;
    }

    public bool IsSensorRuntimeReady(int sensorIndex)
    {
        if (!IsSensorRequiredForCurrentDrive(sensorIndex)) return false;
        return !TryGetSensorRuntimeIssue(sensorIndex, IsWaitingForRuntimeData, out _);
    }

    public string GetSensorRuntimeReadinessLabel(int sensorIndex)
    {
        if (!IsSensorRequiredForCurrentDrive(sensorIndex)) return "--";
        if (!TryGetSensorRuntimeIssue(sensorIndex, IsWaitingForRuntimeData, out string issue))
        {
            float minimumHz = Mathf.Clamp(runtimeMinimumFrameRateHz, 2f, 9f);
            if (lowRateRuntimeCompatibilityEnabled && GetSensorFrameRateHz(sensorIndex) < minimumHz)
                return "低频可跑";
            return "就绪";
        }
        if (issue.StartsWith("帧龄", StringComparison.Ordinal) || issue == "无有效帧") return "超时";
        if (issue.StartsWith("接收", StringComparison.Ordinal)) return "低频";
        if (issue.StartsWith("新帧", StringComparison.Ordinal)) return issue;
        return "等待";
    }

    public int GetSensorRuntimeFaultCount(int sensorIndex) =>
        runtimeFaultCounts != null && sensorIndex >= 0 && sensorIndex < runtimeFaultCounts.Length
            ? runtimeFaultCounts[sensorIndex]
            : 0;

    public float GetSensorCalibrationFreshnessTimeoutSeconds(int sensorIndex) =>
        GetCalibrationFreshnessTimeoutSeconds(sensorIndex);

    public double GetSensorFrameAgeMilliseconds(int sensorIndex)
    {
        if (processor == null || !IndexInRange(sensorIndex)) return double.PositiveInfinity;
        double ageSeconds = processor.GetDeviceDataAgeSeconds(sensorIndex);
        return double.IsInfinity(ageSeconds) ? double.PositiveInfinity : ageSeconds * 1000.0;
    }

    public long GetSensorSequence(int sensorIndex) =>
        processor != null && IndexInRange(sensorIndex)
            ? processor.GetCalibrationSampleSequence(sensorIndex)
            : -1;

    public long GetSensorInputSequenceGapCount(int sensorIndex) =>
        processor != null && IndexInRange(sensorIndex)
            ? processor.GetInputSequenceGapCount(sensorIndex)
            : 0;

    public float GetSensorInputStepAngleDeg(int sensorIndex) =>
        processor != null && IndexInRange(sensorIndex)
            ? processor.GetLastAcceptedStepAngleDeg(sensorIndex)
            : 0f;

    public int GetSensorCalibrationAcceptedSamples(int sensorIndex)
    {
        return calibrationAcceptedSampleCounts != null && IndexInRange(sensorIndex)
            ? calibrationAcceptedSampleCounts[sensorIndex]
            : 0;
    }

    public int GetSensorCalibrationRejectedSamples(int sensorIndex)
    {
        return calibrationRejectedSampleCounts != null && IndexInRange(sensorIndex)
            ? calibrationRejectedSampleCounts[sensorIndex]
            : 0;
    }

    public int GetSensorCalibrationRestartCount(int sensorIndex) =>
        calibrationRestartCounts != null && IndexInRange(sensorIndex)
            ? calibrationRestartCounts[sensorIndex]
            : 0;

    public int GetSensorCalibrationRequiredSamples(int sensorIndex)
    {
        if (IsArmSensorRequiredForCalibration(sensorIndex) || IsGenericStandaloneParticipant(sensorIndex))
            return Mathf.Clamp(armCalibrationMinimumUniqueSamples, 3, 30);
        if (sensorIndex == LeftThighSensorIndex && leftLegParticipatesInCalibration)
            return Mathf.Max(1, leftLegCalibrationSampleFramesRequired);
        if (sensorIndex == RightThighSensorIndex && rightLegParticipatesInCalibration)
            return Mathf.Max(1, rightLegCalibrationSampleFramesRequired);
        if (sensorIndex == LeftCalfSensorIndex &&
            (leftCalfParticipatesInCalibration || leftStandaloneCalfParticipatesInCalibration))
            return Mathf.Max(1, leftLegCalibrationSampleFramesRequired);
        if (sensorIndex == RightCalfSensorIndex &&
            (rightCalfParticipatesInCalibration || rightStandaloneCalfParticipatesInCalibration))
            return Mathf.Max(1, rightLegCalibrationSampleFramesRequired);
        return 0;
    }

    public int GlobalQueueDroppedFrameCount =>
        Serial != null && Serial.Parser != null ? Serial.Parser.GlobalQueueDroppedFrameCount : 0;
    public int GlobalQueueCount =>
        Serial != null && Serial.Parser != null ? Serial.Parser.QueueCount : 0;
    public int GlobalQueueCapacity =>
        Serial != null && Serial.Parser != null ? Serial.Parser.GlobalQueueCapacity : 256;
    public int Crc16FailCount =>
        Serial != null && Serial.Parser != null ? Serial.Parser.Crc16FailCount : 0;
    public int DuplicateLogicalIdConflictCount =>
        Serial != null && Serial.Parser != null ? Serial.Parser.DuplicateLogicalIdConflictCount : 0;
    public long SourceLostFrameCount => SumParserSourceCounter(
        parser => parser.GetSourceLostFrameCount);
    public long SourceDuplicateFrameCount => SumParserSourceCounter(
        parser => parser.GetSourceDuplicateFrameCount);
    public long SourceOutOfOrderFrameCount => SumParserSourceCounter(
        parser => parser.GetSourceOutOfOrderFrameCount);
    public long GetSensorSourceLostFrameCount(int sensorIndex) =>
        Serial != null && Serial.Parser != null && IndexInRange(sensorIndex)
            ? Serial.Parser.GetSourceLostFrameCount(sensorIndex)
            : 0L;
    public long GetSensorSourceDuplicateFrameCount(int sensorIndex) =>
        Serial != null && Serial.Parser != null && IndexInRange(sensorIndex)
            ? Serial.Parser.GetSourceDuplicateFrameCount(sensorIndex)
            : 0L;
    public long GetSensorSourceOutOfOrderFrameCount(int sensorIndex) =>
        Serial != null && Serial.Parser != null && IndexInRange(sensorIndex)
            ? Serial.Parser.GetSourceOutOfOrderFrameCount(sensorIndex)
            : 0L;
    public long BacklogDiscardedFrameCount =>
        processor != null ? processor.BacklogDiscardedFrameCount : 0;
    public int LastBacklogDiscardedFrameCount =>
        processor != null ? processor.LastBacklogDiscardedFrameCount : 0;
    public int LastInputQueueDepth =>
        processor != null ? processor.LastInputQueueDepth : 0;

    private long SumParserSourceCounter(Func<SerialParser, Func<int, long>> selector)
    {
        SerialParser parser = Serial != null ? Serial.Parser : null;
        if (parser == null || config == null) return 0L;
        Func<int, long> counter = selector(parser);
        long total = 0L;
        for (int i = 0; i < config.deviceCount; i++)
            total += counter(i);
        return total;
    }

    public string GetCalibrationProgressSummary()
    {
        string summary = string.Empty;
        int count = config != null ? Mathf.Max(0, config.deviceCount) : 9;
        for (int sensorIndex = 0; sensorIndex < count; sensorIndex++)
        {
            int required = GetSensorCalibrationRequiredSamples(sensorIndex);
            if (required <= 0) continue;
            if (summary.Length > 0) summary += "  ";
            summary += $"{sensorIndex + 1:00}:{GetSensorCalibrationAcceptedSamples(sensorIndex)}/{required}";
        }
        return summary;
    }

    public string GetSensorRoleLabel(int sensorIndex)
    {
        string[] roles =
        {
            "左大臂", "左小臂", "右大臂", "右小臂", "腰部",
            "左大腿", "左小腿", "右大腿", "右小腿"
        };
        return sensorIndex >= 0 && sensorIndex < roles.Length ? roles[sensorIndex] : "未知";
    }

    public SensorCalibrationUiState GetSensorCalibrationUiState(int sensorIndex)
    {
        if (!IndexInRange(sensorIndex)) return SensorCalibrationUiState.Offline;
        if (sensorTestSelectionMode == SensorTestSelectionMode.ManualIdList &&
            !IsManualSensorSelected(sensorIndex))
            return SensorCalibrationUiState.NotDriven;

        if (sensorCalibrationSucceeded != null &&
            sensorIndex < sensorCalibrationSucceeded.Length &&
            sensorCalibrationSucceeded[sensorIndex])
        {
            return SensorCalibrationUiState.Succeeded;
        }

        if (sensorCalibrationFailed != null &&
            sensorIndex < sensorCalibrationFailed.Length &&
            sensorCalibrationFailed[sensorIndex])
        {
            return SensorCalibrationUiState.Failed;
        }

        if ((sensorIndex == LeftForeArmIndex || sensorIndex == RightForeArmIndex) &&
            !fullBodyDiagnosticMode &&
            !IsGenericStandaloneParticipant(sensorIndex) &&
            !IsArmSensorRequiredForCalibration(sensorIndex))
            return SensorCalibrationUiState.Locked;

        if (sensorIndex == (int)BoneIndex.Spine &&
            !fullBodyDiagnosticMode && !IsGenericStandaloneParticipant(sensorIndex))
            return SensorCalibrationUiState.NotDriven;

        bool potentiallyParticipates =
            (sensorIndex == LeftArmIndex && leftArmParticipatesInCalibration) ||
            (sensorIndex == RightArmIndex && rightArmParticipatesInCalibration) ||
            (sensorIndex == LeftThighSensorIndex && leftLegParticipatesInCalibration) ||
            (sensorIndex == RightThighSensorIndex && rightLegParticipatesInCalibration) ||
            (sensorIndex == LeftCalfSensorIndex &&
                (leftCalfParticipatesInCalibration || leftStandaloneCalfParticipatesInCalibration)) ||
            (sensorIndex == RightCalfSensorIndex &&
                (rightCalfParticipatesInCalibration || rightStandaloneCalfParticipatesInCalibration)) ||
            (fullBodyDiagnosticMode && sensorIndex == LeftForeArmIndex && driveLeftForeArm) ||
            (fullBodyDiagnosticMode && sensorIndex == RightForeArmIndex && driveRightForeArm) ||
            (fullBodyDiagnosticMode && sensorIndex == (int)BoneIndex.Spine) ||
            IsGenericStandaloneParticipant(sensorIndex);

        if (!potentiallyParticipates)
            return SensorCalibrationUiState.NotDriven;
        if (!IsSensorOnline(sensorIndex))
            return SensorCalibrationUiState.Offline;
        if (calibrationCountdownActive)
        {
            int required = GetSensorCalibrationRequiredSamples(sensorIndex);
            if (required > 0 && GetSensorCalibrationAcceptedSamples(sensorIndex) >= required)
                return SensorCalibrationUiState.Sampled;
            return calibrationSamplingActive
                ? SensorCalibrationUiState.Sampling
                : SensorCalibrationUiState.WaitingForStability;
        }
        if (IsSensorStable(sensorIndex))
            return SensorCalibrationUiState.Ready;
        return SensorCalibrationUiState.WaitingForStability;
    }

    /// <summary>
    /// 单人操作流程：稳定后自动预留准备时间，再固定采样 2 秒。
    /// 标定读取集中数据中心锁存的最近有效帧；实时驱动超时不会取消倒计时。
    /// </summary>
    private void StartCalibrationCountdown()
    {
        ApplyFullBodyDiagnosticPreset();
        // 在倒计时开始瞬间锁存本轮允许的在线组合；之后未选择设备绝不会加入驱动。
        // 手动列表模式即使其他设备仍持续发送数据，也只接管manualTestSensorIds中的ID。
        bool allowLeftArm = IsSensorSelectedForTesting(LeftArmIndex, driveArms && driveLeftArm);
        bool allowRightArm = IsSensorSelectedForTesting(RightArmIndex, driveArms && driveRightArm);
        bool allowLeftLeg = IsSensorSelectedForTesting(LeftThighSensorIndex, driveLeftLeg);
        bool allowRightLeg = IsSensorSelectedForTesting(RightThighSensorIndex, driveRightLeg);
        bool allowLeftCalf = IsSensorSelectedForTesting(LeftCalfSensorIndex, driveLeftCalf);
        bool allowRightCalf = IsSensorSelectedForTesting(RightCalfSensorIndex, driveRightCalf);

        leftArmParticipatesInCalibration = allowLeftArm &&
            IsSensorAvailableForSession(LeftArmIndex, LeftArmIndex);
        rightArmParticipatesInCalibration = allowRightArm &&
            IsSensorAvailableForSession(RightArmIndex, RightArmIndex);

        leftLegParticipatesInCalibration = allowLeftLeg &&
            IsSensorAvailableForSession(LeftThighSensorIndex, LeftThighIndex);
        rightLegParticipatesInCalibration = allowRightLeg &&
            IsSensorAvailableForSession(RightThighSensorIndex, RightThighIndex);

        bool leftCalfAvailable = allowLeftCalf &&
            IsSensorAvailableForSession(LeftCalfSensorIndex, LeftCalfIndex);
        bool rightCalfAvailable = allowRightCalf &&
            IsSensorAvailableForSession(RightCalfSensorIndex, RightCalfIndex);

        leftCalfParticipatesInCalibration = leftLegParticipatesInCalibration && leftCalfAvailable;
        rightCalfParticipatesInCalibration = rightLegParticipatesInCalibration && rightCalfAvailable;
        leftStandaloneCalfParticipatesInCalibration =
            !leftLegParticipatesInCalibration && leftCalfAvailable && allowStandaloneCalfTesting;
        rightStandaloneCalfParticipatesInCalibration =
            !rightLegParticipatesInCalibration && rightCalfAvailable && allowStandaloneCalfTesting;

        ClearGenericStandaloneParticipants();
        // 全身诊断时02/04由ArmPoseDriver与01/03同组标定，不再重复进入通用独立驱动器。
        TryEnableGenericStandaloneParticipant(LeftForeArmIndex,
            !fullBodyDiagnosticMode &&
            IsSensorSelectedForTesting(LeftForeArmIndex, driveLeftForeArm) &&
            !(isolateUpperArmTestingFromForearms && leftArmParticipatesInCalibration));
        TryEnableGenericStandaloneParticipant(RightForeArmIndex,
            !fullBodyDiagnosticMode &&
            IsSensorSelectedForTesting(RightForeArmIndex, driveRightForeArm) &&
            !(isolateUpperArmTestingFromForearms && rightArmParticipatesInCalibration));
        TryEnableGenericStandaloneParticipant((int)BoneIndex.Spine,
            IsSensorSelectedForTesting((int)BoneIndex.Spine, false));

        bool anyParticipant = leftArmParticipatesInCalibration ||
                              rightArmParticipatesInCalibration ||
                              leftLegParticipatesInCalibration ||
                              rightLegParticipatesInCalibration ||
                              leftStandaloneCalfParticipatesInCalibration ||
                              rightStandaloneCalfParticipatesInCalibration ||
                              HasAnyGenericStandaloneParticipant();
        if (!anyParticipant)
        {
            calibrationCountdownStatus = $"测试选择{SensorTestSelectionSummary}中没有可参与本轮标定的在线传感器";
            return;
        }

        ApplyContinuousArmPreset();
        ApplyV58LegPreset();
        ApplyInspectorSettingsToLeftLegDriver();
        ApplyInspectorSettingsToRightLegDriver();
        ApplyInspectorSettingsToArmDriver();

        armDriver?.Reset();
        leftLegDriver?.Reset();
        rightLegDriver?.Reset();
        leftStandaloneCalfDriver?.Reset();
        rightStandaloneCalfDriver?.Reset();
        ResetGenericStandaloneDrivers();
        ResetArmSamplingState(false);
        ClearSensorCalibrationResults();

        calibrationCountdownSeconds = Mathf.Clamp(calibrationCountdownSeconds, 5f, 8f);
        armCalibrationSamplingSeconds = Mathf.Clamp(armCalibrationSamplingSeconds, 1f, 3f);
        armCalibrationMinimumUniqueSamples = CalibrationSamplesPerRequiredSensor;
        leftLegCalibrationSampleFramesRequired = CalibrationSamplesPerRequiredSensor;
        rightLegCalibrationSampleFramesRequired = CalibrationSamplesPerRequiredSensor;
        calibrationSampleMaxAgeSeconds = Mathf.Clamp(calibrationSampleMaxAgeSeconds, 3.5f, 4f);
        independentCalibrationMaxSamplingSeconds = Mathf.Clamp(
            Mathf.Max(20f, independentCalibrationMaxSamplingSeconds), 12f, 30f);
        runtimeDeviceTimeoutSeconds = Mathf.Clamp(runtimeDeviceTimeoutSeconds, 0.5f, 2f);
        independentCalibrationReanchorFrames = Mathf.Clamp(independentCalibrationReanchorFrames, 2, 4);
        if (leftLegDriver != null)
            leftLegDriver.CalibrationSampleFramesRequired = Mathf.Max(1, leftLegCalibrationSampleFramesRequired);
        if (rightLegDriver != null)
            rightLegDriver.CalibrationSampleFramesRequired = Mathf.Max(1, rightLegCalibrationSampleFramesRequired);

        calibrationCountdownStartTime = Time.time;
        calibrationCountdownActive = true;
        calibrationSamplingActive = false;
        calibrationCountdownStatus = "已锁存当前在线传感器组合，请保持初始姿态";

        Debug.Log($"[V8.11全身标定] 选择={SensorTestSelectionSummary}；参与：01={leftArmParticipatesInCalibration}, " +
            $"02={IsArmSensorRequiredForCalibration(LeftForeArmIndex)}, 03={rightArmParticipatesInCalibration}, " +
            $"04={IsArmSensorRequiredForCalibration(RightForeArmIndex)}, 05={IsGenericStandaloneParticipant((int)BoneIndex.Spine)}, 06={leftLegParticipatesInCalibration}, " +
            $"07配对={leftCalfParticipatesInCalibration}, 07单独={leftStandaloneCalfParticipatesInCalibration}, " +
            $"08={rightLegParticipatesInCalibration}, 09配对={rightCalfParticipatesInCalibration}, " +
            $"09单独={rightStandaloneCalfParticipatesInCalibration}, " +
            $"02单独={IsGenericStandaloneParticipant(LeftForeArmIndex)}, " +
            $"04单独={IsGenericStandaloneParticipant(RightForeArmIndex)}, " +
            $"05单独={IsGenericStandaloneParticipant((int)BoneIndex.Spine)}");
    }

    private void UpdateCalibrationCountdown()
    {
        if (!calibrationCountdownActive) return;

        if (Serial == null || !Serial.IsConnected)
        {
            CancelCalibrationCountdown("连接已断开，请重新连接后标定");
            return;
        }

        float elapsed = Time.time - calibrationCountdownStartTime;
        float preparationSeconds = Mathf.Max(0f, calibrationCountdownSeconds - armCalibrationSamplingSeconds);

        if (elapsed < preparationSeconds)
        {
            calibrationSamplingActive = false;
            calibrationCountdownStatus = "自动标定倒计时，请保持图1的初始 A-Pose";
            return;
        }

        calibrationSamplingActive = true;

        if (armSamplingWindowStartTime < 0f)
        {
            BeginIndependentCalibrationSampling();
        }

        TryAccumulateIndependentCalibrationSamples();
        calibrationCountdownStatus = $"保持 A-Pose，逐路独立采样：{GetCalibrationProgressSummary()}";

        float stableDuration = Time.time - armSamplingWindowStartTime;
        bool minimumTimeElapsed = elapsed >= calibrationCountdownSeconds &&
            stableDuration >= armCalibrationSamplingSeconds;
        string waitReason = string.Empty;
        if (minimumTimeElapsed && CanCompleteCalibrationSampling(out waitReason))
        {
            Quaternion[] averagedQuaternions = BuildAveragedCalibrationQuaternions();
            CompleteCalibrationAndBeginDriving(averagedQuaternions);
            return;
        }

        if (minimumTimeElapsed && !string.IsNullOrEmpty(waitReason))
            calibrationCountdownStatus = waitReason;

        float maxSamplingSeconds = Mathf.Clamp(independentCalibrationMaxSamplingSeconds, 12f, 30f);
        if (stableDuration >= maxSamplingSeconds)
        {
            MarkIncompleteCalibrationSensorsFailed();
            CancelCalibrationCountdown(BuildIndependentSamplingTimeoutReason());
        }
    }

    private bool TryGetCurrentArmCalibrationQuaternions(out Quaternion[] armQuats, out string reason)
    {
        armQuats = null;
        reason = string.Empty;

        // 标定不再读取 State.GetDeviceHasData。该状态受实时0.8秒超时控制，
        // 九设备轮询或短暂无包会把某一路瞬时置为false并取消整个倒计时。
        // 这里读取集中数据中心锁存的“每设备最近一次有效帧”。
        if (!ArmCalibrationInputsReady(out reason))
            return false;

        Quaternion[] transformed = processor?.CalibrationQuaternions ?? processor?.TransformedQuaternions;
        if (transformed == null || transformed.Length <= RightForeArmIndex)
        {
            reason = "手臂坐标转换数据不完整";
            return false;
        }

        armQuats = new Quaternion[4];
        for (int i = 0; i < 4; i++)
        {
            if (!IsArmSensorRequiredForCalibration(i))
            {
                // 小臂锁定时，ArmPoseDriver只使用标定局部骨骼姿态；用同侧大臂姿态
                // 填充兼容槽位，避免02/04离线或陈旧数据阻断标定。
                int sameSideUpperArmIndex = i == LeftForeArmIndex ? LeftArmIndex : RightArmIndex;
                armQuats[i] = armQuats[sameSideUpperArmIndex];
                continue;
            }

            Quaternion q = transformed[i];
            if (!IsQuaternionFinite(q))
            {
                reason = $"手臂传感器 {i + 1} 四元数非法";
                return false;
            }
            armQuats[i] = NormalizeQuaternionSafe(q);
        }
        return true;
    }

    private void ResetArmSamplingState(bool clearParticipants = true)
    {
        calibrationSamplingActive = false;
        armSamplingWindowStartTime = -1f;
        if (clearParticipants)
        {
            leftArmParticipatesInCalibration = false;
            rightArmParticipatesInCalibration = false;
            leftLegParticipatesInCalibration = false;
            rightLegParticipatesInCalibration = false;
            leftCalfParticipatesInCalibration = false;
            rightCalfParticipatesInCalibration = false;
            leftStandaloneCalfParticipatesInCalibration = false;
            rightStandaloneCalfParticipatesInCalibration = false;
            ClearGenericStandaloneParticipants();
        }

        if (calibrationLastConsumedSequences == null) return;
        for (int i = 0; i < calibrationLastConsumedSequences.Length; i++)
        {
            calibrationPreviousAccepted[i] = Quaternion.identity;
            calibrationPendingJump[i] = Quaternion.identity;
            calibrationHasPreviousAccepted[i] = false;
            calibrationHasPendingJump[i] = false;
            calibrationPendingJumpCounts[i] = 0;
            calibrationAcceptedSampleCounts[i] = 0;
            calibrationRejectedSampleCounts[i] = 0;
            calibrationRestartCounts[i] = 0;
            calibrationLastStepDeg[i] = 0f;
            calibrationLastConsumedSequences[i] = -1;
            if (standaloneSamplingQuaternionSums != null && i < standaloneSamplingQuaternionSums.Length)
                standaloneSamplingQuaternionSums[i] = Vector4.zero;
            if (standaloneSamplingHemisphereReferences != null && i < standaloneSamplingHemisphereReferences.Length)
                standaloneSamplingHemisphereReferences[i] = Quaternion.identity;
        }

        for (int i = 0; i < 4; i++)
        {
            armSamplingHemisphereReference[i] = Quaternion.identity;
            armSamplingQuaternionSums[i] = Vector4.zero;
        }

        leftLegDriver?.ClearCalibrationSamples();
        rightLegDriver?.ClearCalibrationSamples();
    }

    private void BeginIndependentCalibrationSampling()
    {
        armSamplingWindowStartTime = Time.time;
        calibrationSamplingActive = true;
        // lastConsumed 保持 -1，使每个参与通道都能先独立接收其当前最新有效帧。
        TryAccumulateIndependentCalibrationSamples();
    }

    private Quaternion[] BuildAveragedCalibrationQuaternions()
    {
        Quaternion[] source = processor?.CalibrationQuaternions ?? processor?.TransformedQuaternions;
        Quaternion[] averaged = source != null
            ? (Quaternion[])source.Clone()
            : new Quaternion[Mathf.Max(4, config != null ? config.deviceCount : 4)];

        // 平均本轮所有真正参与的手臂传感器。全身诊断中01/02/03/04均使用各自真实样本。
        int[] armIndices = { LeftArmIndex, LeftForeArmIndex, RightArmIndex, RightForeArmIndex };
        for (int i = 0; i < armIndices.Length; i++)
        {
            int sensorIndex = armIndices[i];
            if (!IsArmSensorRequiredForCalibration(sensorIndex))
                continue;
            if (calibrationAcceptedSampleCounts == null ||
                calibrationAcceptedSampleCounts[sensorIndex] <= 0)
                continue;

            Vector4 sum = armSamplingQuaternionSums[sensorIndex];
            averaged[sensorIndex] = NormalizeQuaternionSafe(
                new Quaternion(sum.x, sum.y, sum.z, sum.w));
        }

        // 非全身/小臂锁定模式下，才使用同侧大臂填充兼容槽位。
        if (averaged.Length > LeftForeArmIndex && !IsArmSensorRequiredForCalibration(LeftForeArmIndex))
            averaged[LeftForeArmIndex] = averaged[LeftArmIndex];
        if (averaged.Length > RightForeArmIndex && !IsArmSensorRequiredForCalibration(RightForeArmIndex))
            averaged[RightForeArmIndex] = averaged[RightArmIndex];
        return averaged;
    }

    private void CompleteCalibrationAndBeginDriving(Quaternion[] armCalibrationQuaternions)
    {
        calibrationCountdownActive = false;
        calibrationSamplingActive = false;
        calibrationLockedWaitingForRuntime = false;
        runtimeDriveSuspended = false;
        runtimeRecoveryFreshSince = -1f;

        // 参与列表已在倒计时开始时锁存。全身诊断预设仅恢复通道开关，不改变锁存结果。
        ApplyFullBodyDiagnosticPreset();
        ApplyContinuousArmPreset();
        ApplyV58LegPreset();
        ApplyInspectorSettingsToLeftLegDriver();
        ApplyInspectorSettingsToRightLegDriver();
        ApplyInspectorSettingsToArmDriver();

        Quaternion[] calibrationInput = processor?.CalibrationQuaternions ?? processor?.TransformedQuaternions;

        if (leftLegParticipatesInCalibration && leftLegDriver != null && !leftLegDriver.IsCalibrated)
        {
            bool ok;
            string reason;
            if (useStableCalibration)
                ok = TryCommitStableLeftCalibration(out reason);
            else
                ok = leftLegDriver.TryCalibrate(
                    calibrationInput,
                    GetBoneTransform(LeftThighIndex),
                    GetBoneTransform(LeftCalfIndex),
                    restLocalRotations[LeftThighIndex],
                    restLocalRotations[LeftCalfIndex],
                    out reason);

            if (!ok)
            {
                MarkSensorCalibrationFailed(LeftThighSensorIndex);
                if (leftCalfParticipatesInCalibration)
                    MarkSensorCalibrationFailed(LeftCalfSensorIndex);
                CancelCalibrationCountdown($"左腿标定失败：{reason}");
                Debug.LogWarning($"[开始驱动被阻断] {calibrationCountdownStatus}");
                return;
            }

            MarkSensorCalibrationSucceeded(LeftThighSensorIndex);
            if (leftCalfParticipatesInCalibration)
                MarkSensorCalibrationSucceeded(LeftCalfSensorIndex);
        }

        if (rightLegParticipatesInCalibration && rightLegDriver != null && !rightLegDriver.IsCalibrated)
        {
            bool ok;
            string reason;
            if (useStableCalibration)
                ok = TryCommitStableRightCalibration(out reason);
            else
                ok = rightLegDriver.TryCalibrate(
                    calibrationInput,
                    GetBoneTransform(RightThighIndex),
                    GetBoneTransform(RightCalfIndex),
                    restLocalRotations[RightThighIndex],
                    restLocalRotations[RightCalfIndex],
                    out reason);

            if (!ok)
            {
                MarkSensorCalibrationFailed(RightThighSensorIndex);
                if (rightCalfParticipatesInCalibration)
                    MarkSensorCalibrationFailed(RightCalfSensorIndex);
                CancelCalibrationCountdown($"右腿标定失败：{reason}");
                Debug.LogWarning($"[开始驱动被阻断] {calibrationCountdownStatus}");
                return;
            }

            MarkSensorCalibrationSucceeded(RightThighSensorIndex);
            if (rightCalfParticipatesInCalibration)
                MarkSensorCalibrationSucceeded(RightCalfSensorIndex);
        }

        if (leftStandaloneCalfParticipatesInCalibration)
        {
            Quaternion average = GetStandaloneCalibrationAverage(LeftCalfSensorIndex);
            string reason = "左小腿独立驱动器未初始化";
            bool ok = leftStandaloneCalfDriver != null && leftStandaloneCalfDriver.TryCalibrate(
                average, GetBoneTransform(LeftCalfIndex), restLocalRotations[LeftCalfIndex], out reason);
            if (!ok)
            {
                MarkSensorCalibrationFailed(LeftCalfSensorIndex);
                CancelCalibrationCountdown($"左小腿单传感器标定失败：{reason}");
                return;
            }
            MarkSensorCalibrationSucceeded(LeftCalfSensorIndex);
        }

        if (rightStandaloneCalfParticipatesInCalibration)
        {
            Quaternion average = GetStandaloneCalibrationAverage(RightCalfSensorIndex);
            string reason = "右小腿独立驱动器未初始化";
            bool ok = rightStandaloneCalfDriver != null && rightStandaloneCalfDriver.TryCalibrate(
                average, GetBoneTransform(RightCalfIndex), restLocalRotations[RightCalfIndex], out reason);
            if (!ok)
            {
                MarkSensorCalibrationFailed(RightCalfSensorIndex);
                CancelCalibrationCountdown($"右小腿单传感器标定失败：{reason}");
                return;
            }
            MarkSensorCalibrationSucceeded(RightCalfSensorIndex);
        }

        if (genericStandaloneParticipatesInCalibration != null)
        {
            for (int sensorIndex = 0; sensorIndex < genericStandaloneParticipatesInCalibration.Length; sensorIndex++)
            {
                if (!genericStandaloneParticipatesInCalibration[sensorIndex]) continue;
                Quaternion average = GetStandaloneCalibrationAverage(sensorIndex);
                string reason = "通用单传感器驱动器未初始化";
                bool ok = genericStandaloneDrivers != null &&
                          sensorIndex < genericStandaloneDrivers.Length &&
                          genericStandaloneDrivers[sensorIndex] != null &&
                          genericStandaloneDrivers[sensorIndex].TryCalibrate(
                              average,
                              GetBoneTransform(sensorIndex),
                              restLocalRotations[sensorIndex],
                              out reason);
                if (!ok)
                {
                    MarkSensorCalibrationFailed(sensorIndex);
                    CancelCalibrationCountdown($"{sensorIndex + 1:00}{GetSensorRoleLabel(sensorIndex)}单传感器标定失败：{reason}");
                    return;
                }
                MarkSensorCalibrationSucceeded(sensorIndex);
            }
        }

        if (leftArmParticipatesInCalibration || rightArmParticipatesInCalibration)
        {
            if (!AreArmCalibrationInputsFresh())
            {
                CancelCalibrationCountdown("当前参与标定的手臂数据超过各自自适应新鲜度门限，禁止使用旧姿态进入驱动");
                return;
            }

            Quaternion[] calibrationData = armCalibrationQuaternions ?? calibrationInput;
            bool ok = TryCalibrateArms(calibrationData, out string armCalibrateReason);
            if (!ok)
            {
                if (leftArmParticipatesInCalibration) MarkSensorCalibrationFailed(LeftArmIndex);
                if (IsArmSensorRequiredForCalibration(LeftForeArmIndex)) MarkSensorCalibrationFailed(LeftForeArmIndex);
                if (rightArmParticipatesInCalibration) MarkSensorCalibrationFailed(RightArmIndex);
                if (IsArmSensorRequiredForCalibration(RightForeArmIndex)) MarkSensorCalibrationFailed(RightForeArmIndex);
                CancelCalibrationCountdown($"手臂标定失败：{armCalibrateReason}");
                Debug.LogWarning($"[开始驱动被阻断] {calibrationCountdownStatus}");
                return;
            }

            if (leftArmParticipatesInCalibration) MarkSensorCalibrationSucceeded(LeftArmIndex);
            if (IsArmSensorRequiredForCalibration(LeftForeArmIndex)) MarkSensorCalibrationSucceeded(LeftForeArmIndex);
            if (rightArmParticipatesInCalibration) MarkSensorCalibrationSucceeded(RightArmIndex);
            if (IsArmSensorRequiredForCalibration(RightForeArmIndex)) MarkSensorCalibrationSucceeded(RightForeArmIndex);
        }

        if (leftCalfParticipatesInCalibration || rightCalfParticipatesInCalibration)
        {
            TryInitializeKneeMeasurements();
            kneeMeasurementCalibrationDeadlineTime = Time.time + 2f;
        }
        else
        {
            kneeMeasurementCalibrationDeadlineTime = -1f;
        }

        // V8.18: calibration completion immediately enters independent drive.
        // Each bone has its own freshness gate, so no global nine-channel
        // overlap window is required and one slow channel cannot delay start.
        State.SetDriving(true);
        calibrationLockedWaitingForRuntime = false;
        runtimeDriveSuspended = false;
        runtimeRecoveryFreshSince = -1f;
        CaptureRuntimeReadinessBaseline();
        ResetDriverSmoothingWithoutClearingCalibration();
        calibrationCountdownStatus = "标定完成，已进入逐骨骼独立驱动；缺帧骨骼单独冻结，其余骨骼继续更新";

        // 从标定锁定时开始记录通信诊断，使“始终达不到运行门限”的会话也能导出分析。
        if (logger != null && logger.SaveEnabled && !logger.IsLogging && !logger.Open())
            Debug.LogWarning("[Excel记录] 标定已锁定，但通信诊断记录启动失败，请检查导出目录。");

        aiDiagnosticLogger?.LogEvent(
            "independent_runtime_started",
            "DRIVING",
            calibrationCountdownStatus,
            "per_bone_freshness_gate=true, global_nine_sensor_gate=false");
        WriteAiDiagnosticSnapshot("independent_runtime_started");
        Debug.LogWarning($"[V8.20][IndependentRuntimeStarted] {calibrationCountdownStatus}");
    }

    private void CancelCalibrationCountdown(string reason)
    {
        calibrationCountdownActive = false;
        calibrationSamplingActive = false;
        calibrationCountdownStartTime = -1f;
        armSamplingWindowStartTime = -1f;
        calibrationCountdownStatus = reason ?? string.Empty;
        automaticCalibrationStableSince = -1f;
        automaticCalibrationNextAttemptTime = Time.time + Mathf.Max(1f, automaticCalibrationRetryDelaySeconds);

        if (!string.IsNullOrEmpty(calibrationCountdownStatus) &&
            calibrationCountdownStatus != "已断开连接" &&
            calibrationCountdownStatus != "已重置")
        {
            Debug.LogWarning($"[V8 自动标定结束/中断] {calibrationCountdownStatus}");
        }
    }

    private static Quaternion NormalizeQuaternionSafe(Quaternion q)
    {
        float sqr = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (sqr < 0.0000001f || float.IsNaN(sqr) || float.IsInfinity(sqr))
            return Quaternion.identity;
        float inv = 1f / Mathf.Sqrt(sqr);
        return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
    }

    private static int SumCalibrationCounters(int[] values)
    {
        if (values == null) return 0;
        int sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i];
        return sum;
    }

    private static float MaxCalibrationValue(float[] values)
    {
        if (values == null) return 0f;
        float max = 0f;
        for (int i = 0; i < values.Length; i++) max = Mathf.Max(max, values[i]);
        return max;
    }

    private void HandleReset()
    {
        WriteAiDiagnosticSnapshot("reset_final");
        aiDiagnosticLogger?.LogEvent("reset_requested", GetAiDiagnosticStateName(), "用户点击重置");
        ResetRuntimeLinkState(true);
        CancelCalibrationCountdown("已重置");
        ResetAutomaticCalibrationState();
        ResetArmSamplingState();
        ClearSensorCalibrationResults();
        State.SetDriving(false);
        // 先停止后台串口线程，再清空解析缓存，避免 AppendBytes 与 Reset 并发修改 parseBuffer。
        Serial.Disconnect();
        Serial.ResetParser();
        logger.Close();

        leftLegDriver?.Reset();
        rightLegDriver?.Reset();
        armDriver?.Reset();
        leftStandaloneCalfDriver?.Reset();
        rightStandaloneCalfDriver?.Reset();
        ResetGenericStandaloneDrivers();
        kneeMeasurementCalibrationDeadlineTime = -1f;
        processor?.Reset(bones, restLocalRotations);

        ResetAllBonesToRest();

        if (avatarRoot != null)
            avatarRoot.rotation = avatarRootBaseRotation;

        State.Reset();
        aiDiagnosticLogger?.Close("user_reset");
    }

    private void TryInitializeKneeMeasurements()
    {
        TryInitializeLeftKneeMeasurement();
        TryInitializeRightKneeMeasurement();
    }

    private bool TryInitializeLeftKneeMeasurement()
    {
        if (leftLegDriver == null || processor == null ||
            leftLegDriver.IsKneeMeasurementCalibrated)
            return leftLegDriver != null && leftLegDriver.IsKneeMeasurementCalibrated;

        if (!processor.TryGetTimePairedAvatarRotations(
                LeftThighSensorIndex,
                LeftCalfSensorIndex,
                kneeMeasurementMaxPairSkewSeconds,
                out Quaternion thighQ,
                out Quaternion calfQ,
                out DateTime pairTime,
                out double skew))
            return false;

        double age = Math.Max(0d, (DateTime.UtcNow - pairTime).TotalSeconds);
        if (age > kneeMeasurementMaxFreshAgeSeconds)
            return false;

        bool ok = leftLegDriver.TryCalibrateKneeMeasurement(
            thighQ,
            calfQ,
            GetBoneTransform(LeftThighIndex),
            GetBoneTransform(LeftCalfIndex),
            out string reason);
        if (ok)
            Debug.Log($"[V7左膝测量] 06/07时间配对铰链零位完成，错位={skew * 1000d:F0}ms");
        else if (!string.IsNullOrEmpty(reason))
            Debug.LogWarning($"[V7左膝测量] 标定失败：{reason}");
        return ok;
    }

    private bool TryInitializeRightKneeMeasurement()
    {
        if (rightLegDriver == null || processor == null ||
            rightLegDriver.IsKneeMeasurementCalibrated)
            return rightLegDriver != null && rightLegDriver.IsKneeMeasurementCalibrated;

        if (!processor.TryGetTimePairedAvatarRotations(
                RightThighSensorIndex,
                RightCalfSensorIndex,
                kneeMeasurementMaxPairSkewSeconds,
                out Quaternion thighQ,
                out Quaternion calfQ,
                out DateTime pairTime,
                out double skew))
            return false;

        double age = Math.Max(0d, (DateTime.UtcNow - pairTime).TotalSeconds);
        if (age > kneeMeasurementMaxFreshAgeSeconds)
            return false;

        bool ok = rightLegDriver.TryCalibrateKneeMeasurement(
            thighQ,
            calfQ,
            GetBoneTransform(RightThighIndex),
            GetBoneTransform(RightCalfIndex),
            out string reason);
        if (ok)
            Debug.Log($"[V7右膝测量] 08/09时间配对铰链零位完成，错位={skew * 1000d:F0}ms");
        else if (!string.IsNullOrEmpty(reason))
            Debug.LogWarning($"[V7右膝测量] 标定失败：{reason}");
        return ok;
    }

    private void UpdateKneeMeasurements()
    {
        if (processor == null) return;

        // 标定完成后的短窗口仍允许07/09稍晚到达；窗口结束后不再用动作姿态
        // 自动重建零位，避免把一次屈膝误当成新的0°。
        if (Time.time <= kneeMeasurementCalibrationDeadlineTime)
        {
            TryInitializeLeftKneeMeasurement();
            TryInitializeRightKneeMeasurement();
        }

        UpdateLeftKneeMeasurement();
        UpdateRightKneeMeasurement();
    }

    private void UpdateLeftKneeMeasurement()
    {
        if (leftLegDriver == null || !leftLegDriver.IsKneeMeasurementCalibrated)
            return;

        if (!processor.TryGetTimePairedAvatarRotations(
                LeftThighSensorIndex,
                LeftCalfSensorIndex,
                kneeMeasurementMaxPairSkewSeconds,
                out Quaternion thighQ,
                out Quaternion calfQ,
                out DateTime pairTime,
                out double skew))
        {
            leftLegDriver.MarkKneeMeasurementStale(double.PositiveInfinity);
            return;
        }

        double age = Math.Max(0d, (DateTime.UtcNow - pairTime).TotalSeconds);
        leftLegDriver.UpdateKneeMeasurement(
            thighQ,
            calfQ,
            pairTime,
            skew,
            age,
            kneeMeasurementMaxFreshAgeSeconds);
    }

    private void UpdateRightKneeMeasurement()
    {
        if (rightLegDriver == null || !rightLegDriver.IsKneeMeasurementCalibrated)
            return;

        if (!processor.TryGetTimePairedAvatarRotations(
                RightThighSensorIndex,
                RightCalfSensorIndex,
                kneeMeasurementMaxPairSkewSeconds,
                out Quaternion thighQ,
                out Quaternion calfQ,
                out DateTime pairTime,
                out double skew))
        {
            rightLegDriver.MarkKneeMeasurementStale(double.PositiveInfinity);
            return;
        }

        double age = Math.Max(0d, (DateTime.UtcNow - pairTime).TotalSeconds);
        rightLegDriver.UpdateKneeMeasurement(
            thighQ,
            calfQ,
            pairTime,
            skew,
            age,
            kneeMeasurementMaxFreshAgeSeconds);
    }

    private void OnFrameDequeued(int deviceId, Quaternion q, Vector3 euler)
    {
        if (deviceId >= LeftThighSensorIndex && deviceId <= RightCalfSensorIndex && processor != null)
        {
            float stepDeg = processor.GetLastAcceptedStepAngleDeg(deviceId);
            bool logWindowOpen = lastLegInputStepLogTimes == null || deviceId >= lastLegInputStepLogTimes.Length ||
                                 Time.unscaledTime - lastLegInputStepLogTimes[deviceId] >= 0.50f;
            if (stepDeg >= 30f && logWindowOpen)
            {
                if (lastLegInputStepLogTimes != null && deviceId < lastLegInputStepLogTimes.Length)
                    lastLegInputStepLogTimes[deviceId] = Time.unscaledTime;
                SerialParser stepParser = Serial != null ? Serial.Parser : null;
                aiDiagnosticLogger?.LogEvent(
                    "leg_input_large_step",
                    State != null && State.IsDriving ? "DRIVING" : "CALIBRATION_OR_WAITING",
                    $"{deviceId + 1:00}{GetSensorRoleLabel(deviceId)}相邻有效输入跳变{stepDeg:F1}°；骨骼输出将按最大角速度限幅",
                    $"sensor={deviceId + 1:00}, step_deg={stepDeg:F2}, source_seq={(stepParser != null ? stepParser.GetLastSourceSequence(deviceId) : 0u)}, " +
                    $"source_lost={(stepParser != null ? stepParser.GetSourceLostFrameCount(deviceId) : 0L)}, q=({q.x:F5},{q.y:F5},{q.z:F5},{q.w:F5})");
            }
        }

        if (State != null && State.IsDriving &&
            (deviceId == LeftThighSensorIndex || deviceId == LeftCalfSensorIndex ||
             deviceId == RightThighSensorIndex || deviceId == RightCalfSensorIndex))
        {
            // 当前真实腿部新帧已进入MotionDataHub历史。先更新配对膝角，再写Excel，
            // 避免小腿工作表继续记录上一帧或固定0°。
            UpdateKneeMeasurements();
        }

        SerialParser parser = Serial != null ? Serial.Parser : null;
        bool hasV2 = parser != null && parser.HasV2Source(deviceId);
        logger.LogFrame(
            deviceId,
            q,
            euler,
            hasV2 ? (byte)2 : (byte)1,
            hasV2 ? parser.GetHardwareId(deviceId) : 0u,
            hasV2 ? parser.GetLastSourceSequence(deviceId) : 0u,
            hasV2 ? parser.GetLastSenderTickMs(deviceId) : 0u,
            hasV2 ? parser.GetSourceLostFrameCount(deviceId) : 0L,
            hasV2 ? parser.GetSourceDuplicateFrameCount(deviceId) : 0L,
            hasV2 ? parser.GetSourceOutOfOrderFrameCount(deviceId) : 0L,
            hasV2 ? parser.GetDuplicateLogicalIdCount(deviceId) : 0L,
            GetSensorFrameRateHz(deviceId),
            hasV2 ? parser.GetSourceReportedFrameRateHz(deviceId) : 0f,
            hasV2 ? parser.GetSourceDeliveryPercent(deviceId) : 0f,
            LeftElbowFlexionAngleDeg,
            RightElbowFlexionAngleDeg,
            LeftKneeFlexionAngleDeg,
            LeftKneeIncludedAngleDeg,
            RightKneeFlexionAngleDeg,
            RightKneeIncludedAngleDeg);
    }

    private void ResolveAvatarRoot()
    {
        if (avatarRoot == null && !string.IsNullOrEmpty(config.avatarRootName))
        {
            GameObject go = GameObject.Find(config.avatarRootName);
            if (go != null) avatarRoot = go.transform;
        }
    }

    private void CacheBonesAndRestPose()
    {
        int n = config.deviceCount;
        bones = new GameObject[n];
        restLocalRotations = new Quaternion[n];

        for (int i = 0; i < n; i++)
        {
            bones[i] = GameObject.Find(config.boneNames[i]);
            restLocalRotations[i] = bones[i] != null
                ? bones[i].transform.localRotation
                : Quaternion.identity;
        }

        if (driveArms)
        {
            LogMissingBone(LeftArmIndex, "左上臂");
            LogMissingBone(LeftForeArmIndex, "左前臂");
            LogMissingBone(RightArmIndex, "右上臂");
            LogMissingBone(RightForeArmIndex, "右前臂");
        }

        if (driveLeftLeg)
        {
            LogMissingBone(LeftThighIndex, "左大腿");
            LogMissingBone(LeftCalfIndex, "左小腿");
        }

        if (driveRightLeg)
        {
            LogMissingBone(RightThighIndex, "右大腿");
            LogMissingBone(RightCalfIndex, "右小腿");
        }
    }

    private void LogMissingBone(int index, string label)
    {
        if (!IndexInRange(index))
        {
            Debug.LogError($"[MotionCaptureController] {label}索引 {index} 超出 config.deviceCount={config.deviceCount}。如果使用物理 8/9 号传感器，deviceCount 至少要设置为 9。");
            return;
        }

        if (bones[index] == null)
            Debug.LogError($"[MotionCaptureController] 未找到{label}骨骼：{config.boneNames[index]}");
    }

    private Transform GetBoneTransform(int index)
    {
        if (bones == null) return null;
        if (index < 0 || index >= bones.Length) return null;
        return bones[index] != null ? bones[index].transform : null;
    }

    private void ResetAllBonesToRest()
    {
        if (bones == null || restLocalRotations == null) return;

        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] != null)
                bones[i].transform.localRotation = restLocalRotations[i];
        }
    }

    /// <summary>
    /// 每帧只重置“没有被当前驱动器接管”的骨骼。
    /// 不能重置正在驱动的骨骼，否则平滑会丢失上一帧状态。
    /// </summary>
    private void ResetUndrivenBonesToRest()
    {
        if (bones == null || restLocalRotations == null) return;

        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) continue;
            if (IsBoneDrivenThisFrame(i)) continue;

            bones[i].transform.localRotation = restLocalRotations[i];
        }
    }

    private bool IsBoneDrivenThisFrame(int index)
    {
        if (index == LeftArmIndex && leftArmParticipatesInCalibration) return true;
        if (index == LeftForeArmIndex && leftArmParticipatesInCalibration && driveLeftForeArm) return true;
        if (index == RightArmIndex && rightArmParticipatesInCalibration) return true;
        if (index == RightForeArmIndex && rightArmParticipatesInCalibration && driveRightForeArm) return true;

        if (index == LeftThighIndex && leftLegParticipatesInCalibration) return true;
        if (index == LeftCalfIndex &&
            (leftCalfParticipatesInCalibration || leftStandaloneCalfParticipatesInCalibration)) return true;
        if (index == RightThighIndex && rightLegParticipatesInCalibration) return true;
        if (index == RightCalfIndex &&
            (rightCalfParticipatesInCalibration || rightStandaloneCalfParticipatesInCalibration)) return true;
        if (IsGenericStandaloneParticipant(index)) return true;

        return false;
    }

    /// <summary>
    /// 防止错误骨骼绑定造成单侧传感器同时带动左右腿。
    /// </summary>
    private bool ValidateIndependentLegBindings(out string reason)
    {
        reason = string.Empty;
        Transform leftThigh = GetBoneTransform(LeftThighIndex);
        Transform leftCalf = GetBoneTransform(LeftCalfIndex);
        Transform rightThigh = GetBoneTransform(RightThighIndex);
        Transform rightCalf = GetBoneTransform(RightCalfIndex);

        // 缺少某一侧骨骼时，仅由动态可用性检查忽略该通道，不再连带封锁另一侧。
        if (leftThigh == null || leftCalf == null || rightThigh == null || rightCalf == null)
        {
            Debug.LogWarning(
                "[V8腿部骨骼检查] 部分腿部骨骼未找到；缺失通道将被忽略，已有骨骼仍可单独测试。" +
                $" 左大腿={leftThigh != null}, 左小腿={leftCalf != null}, " +
                $"右大腿={rightThigh != null}, 右小腿={rightCalf != null}");
        }

        bool sameSideDuplicate =
            (leftThigh != null && leftCalf != null && leftThigh == leftCalf) ||
            (rightThigh != null && rightCalf != null && rightThigh == rightCalf);
        bool crossSideDuplicate =
            (leftThigh != null && (leftThigh == rightThigh || leftThigh == rightCalf)) ||
            (leftCalf != null && (leftCalf == rightThigh || leftCalf == rightCalf));

        if (sameSideDuplicate || crossSideDuplicate)
        {
            reason = "腿部多个索引引用了同一个Transform。程序已停止腿部驱动，避免单个传感器同时带动多个骨骼。";
            return false;
        }

        Debug.Log($"[V8腿部目标骨骼确认] Avatar左 5/6 -> " +
            $"{(leftThigh != null ? leftThigh.name : "未找到")}/{(leftCalf != null ? leftCalf.name : "未找到")}；" +
            $"Avatar右 7/8 -> {(rightThigh != null ? rightThigh.name : "未找到")}/{(rightCalf != null ? rightCalf.name : "未找到")}");
        Debug.LogWarning($"[V8腿部输入源确认] Avatar左腿读取06/07，Avatar右腿读取08/09；" +
            "同侧成对在线时使用相对膝驱动，只有小腿在线时使用独立诊断驱动；人体左右侧不交换");
        return true;
    }

    private void ApplyInspectorSettingsToLeftLegDriver()
    {
        if (leftLegDriver == null) return;
        leftLegDriver.CalibrationSampleFramesRequired = Mathf.Max(1, leftLegCalibrationSampleFramesRequired);
        bool sessionActive = calibrationCountdownActive || (State != null && State.IsDriving) ||
                             leftLegParticipatesInCalibration || leftStandaloneCalfParticipatesInCalibration;
        leftLegDriver.DriveCalf = sessionActive ? leftCalfParticipatesInCalibration : driveLeftCalf;
        leftLegDriver.ThighBoneAxisOffsetEuler = leftThighBoneAxisOffsetEuler;
        leftLegDriver.ThighAxisInvertMode = leftThighAxisInvertMode;
        leftLegDriver.LimitThighTwist = limitLeftThighTwist;
        leftLegDriver.ThighTwistAxisMode = leftThighTwistAxisMode;
        leftLegDriver.MaxThighTwistDeg = maxLeftThighTwistDeg;
        leftLegDriver.ThighApplyOrder = leftThighApplyOrder;
        leftLegDriver.CalfBoneAxisOffsetEuler = leftCalfBoneAxisOffsetEuler;
        leftLegDriver.ForceThighRestForDebug = false;
        leftLegDriver.StaticCheckLogEnabled = false;
        leftLegDriver.KneeDebugLogEnabled = false;
        leftLegDriver.InputLowPassEnabled = legInputLowPassEnabled;
        leftLegDriver.SmoothingEnabled = runtimeSmoothingEnabled;
        leftLegDriver.SmoothingSpeed = Mathf.Max(0.01f, legOutputSmoothingSpeed);
        leftLegDriver.MaximumAngularSpeedDegPerSec = Mathf.Clamp(legMaximumAngularSpeedDegPerSec, 90f, 540f);
        leftLegDriver.MinBoneAngleThresholdDeg = 0f;
    }

    private void ApplyInspectorSettingsToRightLegDriver()
    {
        if (rightLegDriver == null) return;
        rightLegDriver.CalibrationSampleFramesRequired = Mathf.Max(1, rightLegCalibrationSampleFramesRequired);
        bool sessionActive = calibrationCountdownActive || (State != null && State.IsDriving) ||
                             rightLegParticipatesInCalibration || rightStandaloneCalfParticipatesInCalibration;
        rightLegDriver.DriveCalf = sessionActive ? rightCalfParticipatesInCalibration : driveRightCalf;
        // V7回退到V5较接近正确的右大腿基础映射。
        // V6的横向二次反转与屈伸串扰抑制均未通过实体回归，不再叠加猜测性修正。
        rightLegDriver.InvertThighLateralDirection = false;
        rightLegDriver.SuppressSagittalLateralCrossTalk = false;
        rightLegDriver.ThighBoneAxisOffsetEuler = rightThighBoneAxisOffsetEuler;
        rightLegDriver.ThighAxisInvertMode = rightThighAxisInvertMode;
        rightLegDriver.ThighEulerRemapMode = rightThighEulerRemapMode;
        rightLegDriver.LimitThighTwist = limitRightThighTwist;
        rightLegDriver.ThighTwistAxisMode = rightThighTwistAxisMode;
        rightLegDriver.MaxThighTwistDeg = maxRightThighTwistDeg;
        rightLegDriver.ThighApplyOrder = rightThighApplyOrder;
        rightLegDriver.CalfBoneAxisOffsetEuler = rightCalfBoneAxisOffsetEuler;
        rightLegDriver.ForceThighRestForDebug = false;
        rightLegDriver.StaticCheckLogEnabled = false;
        rightLegDriver.KneeDebugLogEnabled = false;
        rightLegDriver.InputLowPassEnabled = legInputLowPassEnabled;
        rightLegDriver.SmoothingEnabled = runtimeSmoothingEnabled;
        rightLegDriver.SmoothingSpeed = Mathf.Max(0.01f, legOutputSmoothingSpeed);
        rightLegDriver.MaximumAngularSpeedDegPerSec = Mathf.Clamp(legMaximumAngularSpeedDegPerSec, 90f, 540f);
        rightLegDriver.MinBoneAngleThresholdDeg = 0f;
    }

    private void ApplyInspectorSettingsToArmDriver()
    {
        if (armDriver == null) return;

        bool sessionActive = calibrationCountdownActive || (State != null && State.IsDriving) ||
                             leftArmParticipatesInCalibration || rightArmParticipatesInCalibration;
        armDriver.DriveLeftArm = sessionActive ? leftArmParticipatesInCalibration : driveLeftArm;
        armDriver.DriveLeftForeArm = driveLeftForeArm && armDriver.DriveLeftArm;
        armDriver.DriveRightArm = sessionActive ? rightArmParticipatesInCalibration : driveRightArm;
        armDriver.DriveRightForeArm = driveRightForeArm && armDriver.DriveRightArm;
        armDriver.LockForeArmsToCalibrationRest = lockForeArmsToCalibrationRest;
        armDriver.DriveLeftForeArmRelativeToUpperArm = false;
        armDriver.SuppressLeftForeArmAxialTwist = suppressLeftForeArmAxialTwist;
        armDriver.SmoothingEnabled = runtimeSmoothingEnabled;
        armDriver.SmoothingSpeed = Mathf.Max(0.01f, armSmoothingSpeed);
        armDriver.MaximumAngularSpeedDegPerSec = Mathf.Clamp(upperBodyMaximumAngularSpeedDegPerSec, 90f, 720f);
        armDriver.MinAngleThresholdDeg = Mathf.Max(0f, armMinAngleThresholdDeg);
        // V8.10主路径固定开启，避免旧场景或Inspector残值退回V8.5/V8.8错误路径。
        useRightArmCalibratedDeltaSwing = true;
        useRightArmFullQuaternionDelta = false;
        armDriver.UseRightArmCalibratedDeltaSwing = true;
        armDriver.UseRightArmFullQuaternionDelta = false;
        armDriver.RightArmSensorAxisMode = rightArmSensorAxisMode;
        // V8.11无条件禁用V8.8固定参考姿态吸附，避免旧场景序列化true重新启用。
        useRightArmFixedReferenceProfile = false;
        armDriver.UseRightArmFixedReferenceProfile = false;
        armDriver.RightArmProfileInterpolationPower = Mathf.Clamp(rightArmProfileInterpolationPower, 1f, 8f);
        armDriver.RightArmProfileExactMatchAngleDeg = Mathf.Clamp(rightArmProfileExactMatchAngleDeg, 0.5f, 15f);
        armDriver.RightArmProfileFallbackAngleDeg = Mathf.Clamp(rightArmProfileFallbackAngleDeg, 40f, 140f);
        armDriver.EnableRightArmFourPoseAxisLearning = false;
        armDriver.RightArmPoseInitialPrepareSeconds = Mathf.Clamp(rightArmPoseInitialPrepareSeconds, 1f, 6f);
        armDriver.RightArmPoseTransitionSeconds = Mathf.Clamp(rightArmPoseTransitionSeconds, 0.5f, 5f);
        armDriver.RightArmPoseCaptureSeconds = Mathf.Clamp(rightArmPoseCaptureSeconds, 1f, 4f);
        armDriver.LeftArmBoneAxisOffsetEuler = leftArmBoneAxisOffsetEuler;
        armDriver.LeftForeArmBoneAxisOffsetEuler = leftForeArmBoneAxisOffsetEuler;
        armDriver.RightArmBoneAxisOffsetEuler = rightArmBoneAxisOffsetEuler;
        armDriver.RightForeArmBoneAxisOffsetEuler = rightForeArmBoneAxisOffsetEuler;
        armDriver.LeftArmForwardOutwardCompensationDeg = leftArmForwardOutwardCompensationDeg;
        // 旧固定轴字段仅保留二进制/场景兼容；V8.10主路径不读取固定动作参考。
        armDriver.RightArmFixedSegmentAxisLocal = Vector3.back;
        rightArmDeltaFrameCorrectionDeg = 0f;
        armDriver.RightArmDeltaFrameCorrectionDeg = 0f;
        armDriver.RightForeArmDeltaAxisOffsetEuler = rightForeArmDeltaAxisOffsetEuler;
        // 强制关闭旧Correction与单角度delta校正；右上臂由V8.10局部Delta连续三轴矩阵驱动。
        rightArmCorrectionMode = ArmPoseDriver.RightArmCorrectionMode.None;
        armDriver.RightArmCorrection = ArmPoseDriver.RightArmCorrectionMode.None;
        armDriver.RightForeArmCorrection = rightForeArmCorrectionMode;
        armDriver.UseRightForeArmRelativeToRightArm = useRightForeArmRelativeToRightArm;
        armDriver.RightForeArmMode = rightForeArmDriveMode;
        armDriver.RightForeArmSensorBendAxis = rightForeArmSensorBendAxis;
        armDriver.RightForeArmAvatarBendAxis = rightForeArmAvatarBendAxis;
        armDriver.RightForeArmAvatarAxisSpaceMode = rightForeArmAvatarAxisSpace;
        armDriver.RightForeArmBendSign = rightForeArmBendSign;
        armDriver.RightForeArmBendScale = Mathf.Max(0.01f, rightForeArmBendScale);
        armDriver.RightForeArmBendOffsetDeg = rightForeArmBendOffsetDeg;
        armDriver.ClampRightForeArmBend = clampRightForeArmBend;
        armDriver.RightForeArmMinBendDeg = rightForeArmMinBendDeg;
        armDriver.RightForeArmMaxBendDeg = rightForeArmMaxBendDeg;
        armDriver.RightForeArmDebugLog = rightForeArmDebugLog;
        armDriver.UseElbowStraightBlend = false;
        armDriver.ElbowStraightFullIncludedAngleDeg = Mathf.Clamp(elbowStraightFullIncludedAngleDeg, 150f, 180f);
        armDriver.ElbowStraightReleaseIncludedAngleDeg = Mathf.Clamp(
            elbowStraightReleaseIncludedAngleDeg, 120f, armDriver.ElbowStraightFullIncludedAngleDeg - 0.1f);
        armDriver.UseIndependentHierarchy = false;
        armDriver.SuppressForeArmAxialTwist = false;
    }


    private bool TryHandleRuntimeLinkFault()
    {
        if (State == null || !State.IsDriving)
            return false;

        // V8.18: channel freshness is handled per bone in LateUpdate.  Keep
        // global suspension only for faults that make channel identity unsafe.
        if (Serial != null && Serial.IsConnected &&
            (Serial.Parser == null || Serial.Parser.DuplicateLogicalIdConflictCount == 0))
        {
            UpdateIndependentRuntimeInputDiagnostics();
            return false;
        }

        string reason = string.Empty;
        int faultSensorIndex = -1;
        if (Serial == null || !Serial.IsConnected)
        {
            reason = "串口连接意外中断";
        }
        else if (Serial.Parser != null && Serial.Parser.DuplicateLogicalIdConflictCount > 0)
        {
            reason = "检测到重复设备ID，不同硬件正在竞争同一姿态通道";
        }
        else if (AreAllRequiredDriveSensorsRuntimeReady(false, out _, out _))
            return false;
        else
            AreAllRequiredDriveSensorsRuntimeReady(false, out reason, out faultSensorIndex);

        if (string.IsNullOrEmpty(reason))
            return false;

        StopDrivingForRuntimeLinkFault(reason, faultSensorIndex);
        return true;
    }

    private void StopDrivingForRuntimeLinkFault(string reason, int faultSensorIndex)
    {
        lastRuntimeFaultSensorIndex = faultSensorIndex;
        lastRuntimeFaultSummary = reason ?? "未知通信故障";
        if (runtimeFaultCounts != null && faultSensorIndex >= 0 && faultSensorIndex < runtimeFaultCounts.Length)
            runtimeFaultCounts[faultSensorIndex]++;

        string message = $"通信故障，已停止旧姿态消费并恢复人物：{lastRuntimeFaultSummary}；保留标定和九路诊断，等待新数据恢复";
        State.SetDriving(false);
        calibrationLockedWaitingForRuntime = false;
        runtimeDriveSuspended = true;
        runtimeRecoveryFreshSince = -1f;
        calibrationCountdownActive = false;
        calibrationSamplingActive = false;
        automaticCalibrationStableSince = -1f;
        kneeMeasurementCalibrationDeadlineTime = -1f;

        // 只恢复人物骨骼，不清空DataHub、解析器或设备可用状态。
        // 后续新帧仍会持续进入DataHub和Excel，但LateUpdate不会消费它们驱动人物。
        ResetAllBonesToRest();
        ResetDriverSmoothingWithoutClearingCalibration();
        CaptureRuntimeReadinessBaseline();
        calibrationCountdownStatus = message;

        // 先完成状态切换，再记录故障快照，确保日志中的state与现场界面一致。
        aiDiagnosticLogger?.LogEvent(
            "runtime_link_fault",
            "RUNTIME_SUSPENDED",
            message,
            faultSensorIndex >= 0 ? $"trigger_sensor={faultSensorIndex + 1:00}" : "trigger_sensor=serial_or_id_conflict");
        WriteAiDiagnosticSnapshot("runtime_link_fault");
        Debug.LogError($"[RuntimeLinkFault] {message}");
    }

    private bool TryResumeSuspendedDriving()
    {
        if (!IsWaitingForRuntimeData || State == null)
            return false;

        if (Serial == null || !Serial.IsConnected)
        {
            runtimeRecoveryFreshSince = -1f;
            calibrationCountdownStatus = "等待运行数据：串口尚未恢复连接；诊断数据保留至手动断开/重置";
            return false;
        }

        if (Serial.Parser != null && Serial.Parser.DuplicateLogicalIdConflictCount > 0)
        {
            runtimeRecoveryFreshSince = -1f;
            calibrationCountdownStatus = "等待运行数据：仍存在重复设备ID，禁止进入人物驱动";
            return false;
        }

        if (!AreAllRequiredDriveSensorsRuntimeReady(true, out string waitReason, out _))
        {
            runtimeRecoveryFreshSince = -1f;
            calibrationCountdownStatus = calibrationLockedWaitingForRuntime
                ? $"标定已锁定，等待运行数据：{waitReason}"
                : $"驱动已暂停，保留标定和诊断：{waitReason}；上次触发={lastRuntimeFaultSummary}";
            return false;
        }

        if (runtimeRecoveryFreshSince < 0f)
        {
            runtimeRecoveryFreshSince = Time.time;
            calibrationCountdownStatus = $"九路运行条件已满足，连续确认{runtimeReadinessHoldSeconds:F1}秒后自动开始驱动";
            return false;
        }

        if (Time.time - runtimeRecoveryFreshSince < Mathf.Clamp(runtimeReadinessHoldSeconds, 0.5f, 2f))
            return false;

        bool driversReady = (!leftLegParticipatesInCalibration || (leftLegDriver != null && leftLegDriver.IsCalibrated)) &&
                            (!rightLegParticipatesInCalibration || (rightLegDriver != null && rightLegDriver.IsCalibrated)) &&
                            (!(leftArmParticipatesInCalibration || rightArmParticipatesInCalibration) ||
                             (armDriver != null && armDriver.IsCalibrated)) &&
                            AreGenericStandaloneParticipantsCalibrated();
        if (!driversReady)
        {
            calibrationLockedWaitingForRuntime = false;
            runtimeDriveSuspended = false;
            runtimeRecoveryFreshSince = -1f;
            calibrationCountdownStatus = "通信已恢复，但标定状态不完整，等待重新标定";
            return false;
        }

        bool wasRuntimeRecovery = runtimeDriveSuspended;
        calibrationLockedWaitingForRuntime = false;
        runtimeDriveSuspended = false;
        runtimeRecoveryFreshSince = -1f;
        State.SetDriving(true);
        calibrationCountdownStatus = wasRuntimeRecovery
            ? "通信已恢复，沿用本次标定继续运动"
            : "标定已锁定且九路运行数据达标，已开始运动";
        if (leftCalfParticipatesInCalibration || rightCalfParticipatesInCalibration)
        {
            TryInitializeKneeMeasurements();
            kneeMeasurementCalibrationDeadlineTime = Time.time + 2f;
        }
        if (logger != null && logger.SaveEnabled && !logger.IsLogging)
            logger.Open();
        aiDiagnosticLogger?.LogEvent(
            wasRuntimeRecovery ? "runtime_recovered" : "runtime_gate_passed",
            "DRIVING",
            calibrationCountdownStatus,
            $"low_rate_compat={lowRateRuntimeCompatibilityEnabled}, min_receive_hz={runtimeMinimumFrameRateHz:F1}, base_age_s={runtimeDeviceTimeoutSeconds:F1}, warmup_frames={runtimeReadinessMinimumUniqueFrames}");
        WriteAiDiagnosticSnapshot(wasRuntimeRecovery ? "runtime_recovered" : "runtime_gate_passed");
        Debug.LogWarning(
            $"[V8.20][GlobalLinkRecovered] Build={BuildVersion}；{RuntimeGateSummary}、各新增≥{runtimeReadinessMinimumUniqueFrames}帧并连续{runtimeReadinessHoldSeconds:F1}s；" +
            (wasRuntimeRecovery ? "沿用已锁存标定恢复驱动" : "首次进入驱动"));
        return true;
    }

    private bool AreAllRequiredDriveSensorsRuntimeReady(
        bool requireWarmupFrames,
        out string reason,
        out int firstFaultSensorIndex)
    {
        reason = string.Empty;
        firstFaultSensorIndex = -1;
        if (config == null || processor == null)
        {
            reason = "数据处理器未初始化";
            return false;
        }

        bool anyRequired = false;
        int issueCount = 0;
        for (int sensorIndex = 0; sensorIndex < config.deviceCount; sensorIndex++)
        {
            if (!IsSensorRequiredForCurrentDrive(sensorIndex)) continue;
            anyRequired = true;
            if (!TryGetSensorRuntimeIssue(sensorIndex, requireWarmupFrames, out string issue))
                continue;

            if (firstFaultSensorIndex < 0)
                firstFaultSensorIndex = sensorIndex;
            if (issueCount > 0)
                reason += "；";
            reason += $"{sensorIndex + 1:00}{GetSensorRoleLabel(sensorIndex)}{issue}";
            issueCount++;
        }

        if (!anyRequired)
        {
            reason = "没有参与运行的传感器";
            return false;
        }
        if (issueCount > 0)
            return false;

        reason = "九路均已满足运行条件";
        return true;
    }

    private bool TryGetSensorRuntimeIssue(int sensorIndex, bool requireWarmupFrames, out string issue)
    {
        issue = string.Empty;
        if (processor == null || !IndexInRange(sensorIndex) || !processor.HasCalibrationSample(sensorIndex))
        {
            issue = "无有效帧";
            return true;
        }

        float frameRateHz = GetSensorFrameRateHz(sensorIndex);
        float timeoutSeconds = Mathf.Clamp(runtimeDeviceTimeoutSeconds, 0.5f, 2f);
        double ageMs = GetSensorFrameAgeMilliseconds(sensorIndex);
        float timeoutMs = timeoutSeconds * 1000f;
        if (double.IsInfinity(ageMs) || ageMs > timeoutMs)
        {
            issue = double.IsInfinity(ageMs)
                ? "无有效帧"
                : $"帧龄{ageMs:F0}ms>{timeoutMs:F0}ms";
            return true;
        }

        // V8.15 的固定5Hz硬门槛会让当前硬件永远停在“标定已锁定，等待运行数据”：
        // 截图中实际接收仅约0.6~1.9Hz，而源端上报约10~20Hz。V8.16低频兼容模式下，
        // 保留频率显示用于诊断，但不再把“低于5Hz”作为禁止进入人物驱动的理由。
        float minimumHz = Mathf.Clamp(runtimeMinimumFrameRateHz, 2f, 9f);
        if (!lowRateRuntimeCompatibilityEnabled && frameRateHz < minimumHz)
        {
            issue = $"接收{frameRateHz:F1}Hz<{minimumHz:F1}Hz";
            return true;
        }

        if (requireWarmupFrames)
        {
            int freshFrames = GetSensorRuntimeWarmupFrameCount(sensorIndex);
            int requiredFrames = Mathf.Clamp(runtimeReadinessMinimumUniqueFrames, 2, 8);
            if (freshFrames < requiredFrames)
            {
                issue = $"新帧{freshFrames}/{requiredFrames}";
                return true;
            }
        }
        return false;
    }

    private float GetRuntimeFreshnessTimeoutSeconds(int sensorIndex, float measuredHz)
    {
        // 严格模式保持V8.15行为，兼容现有场景中已经序列化的1.0秒值。
        float baseTimeout = Mathf.Clamp(runtimeDeviceTimeoutSeconds, 0.5f, 2f);
        if (!lowRateRuntimeCompatibilityEnabled)
            return baseTimeout;

        // 当前实际到达频率低时，固定1秒门限会让0.6/0.8/0.9Hz通道周期性超时。
        // 这里按实测频率允许约2.5个周期，并以4秒封顶；真正断流仍会被检测到。
        if (measuredHz <= 0.05f)
            return Mathf.Max(baseTimeout, 4f);

        float cycles = Mathf.Clamp(runtimeAdaptiveTimeoutCycles, 1.5f, 4f);
        float adaptiveTimeout = cycles / Mathf.Max(0.05f, measuredHz);
        return Mathf.Clamp(Mathf.Max(baseTimeout, adaptiveTimeout), 0.5f, 4f);
    }

    private void CaptureRuntimeReadinessBaseline()
    {
        if (config == null || processor == null) return;
        if (runtimeReadinessStartSequences == null ||
            runtimeReadinessStartSequences.Length != config.deviceCount)
            runtimeReadinessStartSequences = new long[config.deviceCount];

        for (int i = 0; i < runtimeReadinessStartSequences.Length; i++)
        {
            runtimeReadinessStartSequences[i] = IsSensorRequiredForCurrentDrive(i)
                ? processor.GetCalibrationSampleSequence(i)
                : -1L;
        }
    }

    private int GetSensorRuntimeWarmupFrameCount(int sensorIndex)
    {
        if (processor == null || runtimeReadinessStartSequences == null || sensorIndex < 0 ||
            sensorIndex >= runtimeReadinessStartSequences.Length)
            return 0;
        long baseline = runtimeReadinessStartSequences[sensorIndex];
        long current = processor.GetCalibrationSampleSequence(sensorIndex);
        if (baseline < 0L || current < baseline) return 0;
        long delta = current - baseline;
        return delta > int.MaxValue ? int.MaxValue : (int)delta;
    }

    private void ResetDriverSmoothingWithoutClearingCalibration()
    {
        leftLegDriver?.ResetSmoothingState();
        rightLegDriver?.ResetSmoothingState();
        armDriver?.ResetSmoothingState();
        leftStandaloneCalfDriver?.ResetSmoothingState();
        rightStandaloneCalfDriver?.ResetSmoothingState();
        if (genericStandaloneDrivers == null) return;
        for (int i = 0; i < genericStandaloneDrivers.Length; i++)
            genericStandaloneDrivers[i]?.ResetSmoothingState();
    }

    private bool IsSensorRequiredForCurrentDrive(int sensorIndex)
    {
        if (IsArmSensorRequiredForCalibration(sensorIndex)) return true;
        if (sensorIndex == LeftThighSensorIndex) return leftLegParticipatesInCalibration;
        if (sensorIndex == RightThighSensorIndex) return rightLegParticipatesInCalibration;
        if (sensorIndex == LeftCalfSensorIndex)
            return leftCalfParticipatesInCalibration || leftStandaloneCalfParticipatesInCalibration;
        if (sensorIndex == RightCalfSensorIndex)
            return rightCalfParticipatesInCalibration || rightStandaloneCalfParticipatesInCalibration;
        return IsGenericStandaloneParticipant(sensorIndex);
    }

    private bool IsSensorFreshForDriving(int sensorIndex)
    {
        if (processor == null || !IndexInRange(sensorIndex) ||
            !processor.HasCalibrationSample(sensorIndex))
            return false;
        double age = processor.GetDeviceDataAgeSeconds(sensorIndex);
        // Runtime remains strict and independent from the adaptive calibration
        // window. At the target 8-10 Hz this permits many missed polls without
        // ever replaying seconds-old pose data.
        float timeout = Mathf.Clamp(runtimeDeviceTimeoutSeconds, 0.5f, 2f);
        return age <= timeout;
    }

    private bool PrepareTimePairedLegDriveInput(
        bool leftSide,
        bool thighFresh,
        bool calfFresh,
        out Quaternion[] driveInput)
    {
        Quaternion[] source = processor != null ? processor.TransformedQuaternions : null;
        driveInput = leftSide ? leftLegDriveInput : rightLegDriveInput;
        if (source == null || driveInput == null || driveInput.Length < source.Length)
        {
            SetLegPairDriveHeld(leftSide, true, "驱动输入缓冲不可用", double.PositiveInfinity, double.PositiveInfinity);
            return false;
        }

        Array.Copy(source, driveInput, source.Length);
        bool pairRequired = leftSide ? leftCalfParticipatesInCalibration : rightCalfParticipatesInCalibration;
        if (!pairRequired)
            return false;

        if (!thighFresh || !calfFresh)
        {
            SetLegPairDriveHeld(
                leftSide,
                true,
                !thighFresh ? "大腿输入超时" : "小腿输入超时",
                double.PositiveInfinity,
                double.PositiveInfinity);
            return false;
        }

        int thighSensorIndex = leftSide ? LeftThighSensorIndex : RightThighSensorIndex;
        int calfSensorIndex = leftSide ? LeftCalfSensorIndex : RightCalfSensorIndex;
        if (!processor.TryGetTimePairedAvatarRotations(
                thighSensorIndex,
                calfSensorIndex,
                Mathf.Clamp(legDriveMaxPairSkewSeconds, 0.10f, 0.40f),
                out Quaternion pairedThigh,
                out Quaternion pairedCalf,
                out DateTime pairTimestampUtc,
                out double pairSkewSeconds))
        {
            SetLegPairDriveHeld(leftSide, true, "没有满足门限的时间配对帧", double.PositiveInfinity, double.PositiveInfinity);
            return false;
        }

        double pairAgeSeconds = pairTimestampUtc == DateTime.MinValue
            ? double.PositiveInfinity
            : Math.Max(0d, (DateTime.UtcNow - pairTimestampUtc).TotalSeconds);
        if (pairAgeSeconds > Mathf.Clamp(legDriveMaxPairAgeSeconds, 0.25f, 1f))
        {
            SetLegPairDriveHeld(leftSide, true, "最新配对帧已经陈旧", pairSkewSeconds, pairAgeSeconds);
            return false;
        }

        // 大腿继续使用自己的当前最新姿态；小腿只借用“配对时刻的相对旋转”。
        // syntheticCalf使Inverse(currentThigh)*syntheticCalf严格等于
        // Inverse(pairedThigh)*pairedCalf，既不拖慢髋关节，也不会混用两个时刻计算膝盖。
        Quaternion currentThigh = source[thighSensorIndex].normalized;
        driveInput[thighSensorIndex] = currentThigh;
        driveInput[calfSensorIndex] = ComposeTimePairedCalfForCurrentThigh(
            currentThigh, pairedThigh, pairedCalf);

        if (leftSide)
        {
            lastLeftLegDrivePairTimestampUtc = pairTimestampUtc;
            lastLeftLegDrivePairSkewSeconds = pairSkewSeconds;
        }
        else
        {
            lastRightLegDrivePairTimestampUtc = pairTimestampUtc;
            lastRightLegDrivePairSkewSeconds = pairSkewSeconds;
        }
        SetLegPairDriveHeld(leftSide, false, "时间配对恢复", pairSkewSeconds, pairAgeSeconds);
        return true;
    }

    public static Quaternion ComposeTimePairedCalfForCurrentThigh(
        Quaternion currentThigh,
        Quaternion pairedThigh,
        Quaternion pairedCalf)
    {
        currentThigh = currentThigh.normalized;
        Quaternion pairedRelative =
            (Quaternion.Inverse(pairedThigh.normalized) * pairedCalf.normalized).normalized;
        return (currentThigh * pairedRelative).normalized;
    }

    private double GetLegDrivePairAgeMilliseconds(int sensorIndex)
    {
        DateTime pairTimestampUtc;
        if (sensorIndex == LeftThighSensorIndex || sensorIndex == LeftCalfSensorIndex)
            pairTimestampUtc = lastLeftLegDrivePairTimestampUtc;
        else if (sensorIndex == RightThighSensorIndex || sensorIndex == RightCalfSensorIndex)
            pairTimestampUtc = lastRightLegDrivePairTimestampUtc;
        else
            return double.PositiveInfinity;

        return pairTimestampUtc == DateTime.MinValue
            ? double.PositiveInfinity
            : Math.Max(0d, (DateTime.UtcNow - pairTimestampUtc).TotalMilliseconds);
    }

    private void SetLegPairDriveHeld(
        bool leftSide,
        bool held,
        string reason,
        double pairSkewSeconds,
        double pairAgeSeconds)
    {
        bool previous = leftSide ? leftLegPairHeld : rightLegPairHeld;
        if (previous == held) return;

        if (leftSide)
        {
            leftLegPairHeld = held;
            if (held) leftLegPairHoldCount++;
        }
        else
        {
            rightLegPairHeld = held;
            if (held) rightLegPairHoldCount++;
        }

        string side = leftSide ? "左腿06/07" : "右腿08/09";
        string details =
            $"side={(leftSide ? "left" : "right")}, pair_skew_ms={(double.IsInfinity(pairSkewSeconds) ? -1d : pairSkewSeconds * 1000d):F0}, " +
            $"pair_age_ms={(double.IsInfinity(pairAgeSeconds) ? -1d : pairAgeSeconds * 1000d):F0}, " +
            $"hold_count={(leftSide ? leftLegPairHoldCount : rightLegPairHoldCount)}";
        aiDiagnosticLogger?.LogEvent(
            held ? "leg_pair_drive_held" : "leg_pair_drive_resumed",
            "DRIVING",
            held
                ? $"{side}配对不可用，仅保持小腿最后安全姿势：{reason}"
                : $"{side}配对恢复，按限速继续驱动",
            details);
    }

    private void UpdateIndependentRuntimeInputDiagnostics()
    {
        if (config == null) return;
        if (runtimeInputUnavailable == null || runtimeInputUnavailable.Length != config.deviceCount)
            runtimeInputUnavailable = new bool[config.deviceCount];

        for (int sensorIndex = 0; sensorIndex < config.deviceCount; sensorIndex++)
        {
            if (!IsSensorRequiredForCurrentDrive(sensorIndex)) continue;
            bool unavailable = !IsSensorFreshForDriving(sensorIndex);
            if (unavailable == runtimeInputUnavailable[sensorIndex]) continue;

            runtimeInputUnavailable[sensorIndex] = unavailable;
            string sensorName = $"{sensorIndex + 1:00}{GetSensorRoleLabel(sensorIndex)}";
            if (unavailable)
            {
                if (runtimeFaultCounts != null && sensorIndex < runtimeFaultCounts.Length)
                    runtimeFaultCounts[sensorIndex]++;
                lastRuntimeFaultSensorIndex = sensorIndex;
                lastRuntimeFaultSummary = $"{sensorName}输入超时，已只冻结对应骨骼";
                aiDiagnosticLogger?.LogEvent(
                    "sensor_drive_frozen",
                    "DRIVING",
                    lastRuntimeFaultSummary,
                    $"trigger_sensor={sensorIndex + 1:00}");
            }
            else
            {
                aiDiagnosticLogger?.LogEvent(
                    "sensor_drive_resumed",
                    "DRIVING",
                    $"{sensorName}新数据恢复，已恢复对应骨骼驱动");
            }
        }
    }

    private void ClearDeviceAvailability()
    {
        if (State == null || config == null) return;
        for (int i = 0; i < config.deviceCount; i++)
            State.SetDeviceHasData(i, false);
    }

    private bool AreLegDriveInputsFresh(int thighSensorIndex, int calfSensorIndex, bool calfRequired)
    {
        if (!IsSensorFreshForDriving(thighSensorIndex))
            return false;
        return !calfRequired || IsSensorFreshForDriving(calfSensorIndex);
    }

    private bool AreArmDriveInputsFresh()
    {
        if (processor == null) return false;
        if (leftArmParticipatesInCalibration && !IsSensorFreshForDriving(LeftArmIndex))
            return false;
        if (rightArmParticipatesInCalibration && !IsSensorFreshForDriving(RightArmIndex))
            return false;
        if (IsArmSensorRequiredForCalibration(LeftForeArmIndex) && !IsSensorFreshForDriving(LeftForeArmIndex))
            return false;
        if (IsArmSensorRequiredForCalibration(RightForeArmIndex) && !IsSensorFreshForDriving(RightForeArmIndex))
            return false;
        return leftArmParticipatesInCalibration || rightArmParticipatesInCalibration;
    }

    private bool AreLegCalibrationInputsFresh(int thighSensorIndex, int calfSensorIndex, bool calfRequired)
    {
        if (!HasFreshCalibrationSample(thighSensorIndex)) return false;
        return !calfRequired || HasFreshCalibrationSample(calfSensorIndex);
    }

    private bool AreArmCalibrationInputsFresh()
    {
        if (processor == null) return false;
        if (leftArmParticipatesInCalibration && !HasFreshCalibrationSample(LeftArmIndex)) return false;
        if (rightArmParticipatesInCalibration && !HasFreshCalibrationSample(RightArmIndex)) return false;
        if (IsArmSensorRequiredForCalibration(LeftForeArmIndex) && !HasFreshCalibrationSample(LeftForeArmIndex)) return false;
        if (IsArmSensorRequiredForCalibration(RightForeArmIndex) && !HasFreshCalibrationSample(RightForeArmIndex)) return false;
        return leftArmParticipatesInCalibration || rightArmParticipatesInCalibration;
    }

    private bool LeftThighSensorReady()
    {
        return ThighSensorReady(
            LeftThighSensorIndex,
            LeftThighIndex,
            "Avatar左大腿输入源",
            out _);
    }

    private bool RightThighSensorReady()
    {
        return ThighSensorReady(
            RightThighSensorIndex,
            RightThighIndex,
            "Avatar右大腿输入源",
            out _);
    }

    private bool ThighSensorReady(
        int thighSensorIndex,
        int thighBoneIndex,
        string thighLabel,
        out string reason)
    {
        reason = "";
        if (State == null)
        {
            reason = "State 未初始化";
            return false;
        }
        if (!IndexInRange(thighSensorIndex))
        {
            reason = $"{thighLabel}传感器索引 {thighSensorIndex} 越界。当前 config.deviceCount={config.deviceCount}";
            return false;
        }
        if (!State.GetDeviceHasData(thighSensorIndex))
        {
            reason = $"{thighLabel}传感器（代码索引 {thighSensorIndex}）尚未到数";
            return false;
        }
        Quaternion[] qs = processor?.TransformedQuaternions;
        if (qs == null || qs.Length <= thighSensorIndex || !IsQuaternionFinite(qs[thighSensorIndex]))
        {
            reason = $"{thighLabel} transformed quaternion 不存在或非法";
            return false;
        }
        if (GetBoneTransform(thighBoneIndex) == null)
        {
            reason = $"{thighLabel}目标骨骼未找到：请检查 MotionCaptureConfig.boneNames[{thighBoneIndex}]";
            return false;
        }
        return true;
    }

    private bool LeftLegSensorsReady(out string reason)
    {
        return LegSensorsReady(
            LeftThighSensorIndex,
            LeftCalfSensorIndex,
            LeftThighIndex,
            LeftCalfIndex,
            driveLeftCalf,
            "Avatar左大腿输入源",
            "Avatar左小腿输入源",
            out reason);
    }

    private bool RightLegSensorsReady(out string reason)
    {
        return LegSensorsReady(
            RightThighSensorIndex,
            RightCalfSensorIndex,
            RightThighIndex,
            RightCalfIndex,
            driveRightCalf,
            "Avatar右大腿输入源",
            "Avatar右小腿输入源",
            out reason);
    }

    /// <summary>
    /// 标定专用数据门控。每路按实测Hz使用约4个采样周期，最大不超过4秒；
    /// 与运行期固定1秒的严格门限完全分离。
    /// </summary>
    private bool HasCalibrationSample(int sensorIndex)
    {
        return processor != null && processor.HasCalibrationSample(sensorIndex);
    }


    private bool HasFreshCalibrationSample(int sensorIndex)
    {
        return processor != null && processor.HasCalibrationSample(sensorIndex) &&
               processor.GetCalibrationSampleAgeSeconds(sensorIndex) <=
               GetCalibrationFreshnessTimeoutSeconds(sensorIndex);
    }

    private float GetCalibrationFreshnessTimeoutSeconds(int sensorIndex)
    {
        float maximum = Mathf.Clamp(calibrationSampleMaxAgeSeconds, 3.5f, 4f);
        if (processor == null || !IndexInRange(sensorIndex)) return maximum;
        float adaptive = processor.GetDeviceEffectiveOfflineTimeoutSeconds(sensorIndex);
        if (adaptive <= 0f) return maximum;
        return Mathf.Clamp(adaptive, 0.5f, maximum);
    }

    private bool CalibrationSensorReady(
        int sensorIndex,
        int boneIndex,
        string label,
        out string reason)
    {
        reason = string.Empty;

        if (!IndexInRange(sensorIndex))
        {
            reason = $"{label}索引 {sensorIndex} 越界。当前 config.deviceCount={config.deviceCount}";
            return false;
        }

        if (processor == null || !processor.HasCalibrationSample(sensorIndex))
        {
            reason = $"{label}传感器（代码索引 {sensorIndex}）本次连接后尚未收到有效帧";
            return false;
        }

        double age = processor.GetCalibrationSampleAgeSeconds(sensorIndex);
        float freshnessTimeout = GetCalibrationFreshnessTimeoutSeconds(sensorIndex);
        if (age > freshnessTimeout)
        {
            reason = $"{label}数据已过期（{age:F2}s > 自适应门限{freshnessTimeout:F2}s，" +
                     $"实测{GetSensorFrameRateHz(sensorIndex):F1}Hz），请检查该传感器是否仍在发送";
            return false;
        }

        Quaternion[] qs = processor.CalibrationQuaternions;
        if (qs == null || qs.Length <= sensorIndex || !IsQuaternionFinite(qs[sensorIndex]))
        {
            reason = $"{label}标定四元数不存在或非法";
            return false;
        }

        if (GetBoneTransform(boneIndex) == null)
        {
            reason = $"{label}目标骨骼未找到：请检查 MotionCaptureConfig.boneNames[{boneIndex}]";
            return false;
        }

        return true;
    }

    private bool IsArmSensorRequiredForCalibration(int sensorIndex)
    {
        if (sensorIndex == LeftArmIndex) return leftArmParticipatesInCalibration;
        if (sensorIndex == RightArmIndex) return rightArmParticipatesInCalibration;
        if (sensorIndex == LeftForeArmIndex)
            return fullBodyDiagnosticMode && leftArmParticipatesInCalibration && driveLeftForeArm;
        if (sensorIndex == RightForeArmIndex)
            return fullBodyDiagnosticMode && rightArmParticipatesInCalibration && driveRightForeArm;
        return false;
    }

    private void ClearGenericStandaloneParticipants()
    {
        if (genericStandaloneParticipatesInCalibration == null) return;
        for (int i = 0; i < genericStandaloneParticipatesInCalibration.Length; i++)
            genericStandaloneParticipatesInCalibration[i] = false;
    }

    private bool IsGenericStandaloneCandidate(int sensorIndex)
    {
        return sensorIndex == LeftForeArmIndex ||
               sensorIndex == RightForeArmIndex ||
               sensorIndex == (int)BoneIndex.Spine;
    }

    private void TryEnableGenericStandaloneParticipant(int sensorIndex, bool allowed)
    {
        if (!allowed || !IsGenericStandaloneCandidate(sensorIndex) ||
            genericStandaloneParticipatesInCalibration == null ||
            sensorIndex < 0 || sensorIndex >= genericStandaloneParticipatesInCalibration.Length)
            return;
        genericStandaloneParticipatesInCalibration[sensorIndex] =
            IsSensorAvailableForSession(sensorIndex, sensorIndex);
    }

    private bool IsGenericStandaloneParticipant(int sensorIndex)
    {
        return genericStandaloneParticipatesInCalibration != null &&
               sensorIndex >= 0 && sensorIndex < genericStandaloneParticipatesInCalibration.Length &&
               genericStandaloneParticipatesInCalibration[sensorIndex];
    }

    private bool HasAnyGenericStandaloneParticipant()
    {
        if (genericStandaloneParticipatesInCalibration == null) return false;
        for (int i = 0; i < genericStandaloneParticipatesInCalibration.Length; i++)
            if (genericStandaloneParticipatesInCalibration[i]) return true;
        return false;
    }

    private void ResetGenericStandaloneDrivers()
    {
        if (genericStandaloneDrivers == null) return;
        for (int i = 0; i < genericStandaloneDrivers.Length; i++)
            genericStandaloneDrivers[i]?.Reset();
    }

    private bool AreGenericStandaloneParticipantsCalibrated()
    {
        if (genericStandaloneParticipatesInCalibration == null) return true;
        for (int i = 0; i < genericStandaloneParticipatesInCalibration.Length; i++)
        {
            if (!genericStandaloneParticipatesInCalibration[i]) continue;
            if (genericStandaloneDrivers == null || i >= genericStandaloneDrivers.Length ||
                genericStandaloneDrivers[i] == null || !genericStandaloneDrivers[i].IsCalibrated)
                return false;
        }
        return true;
    }

    private bool ArmCalibrationInputsReady(out string reason)
    {
        int[] indices = { LeftArmIndex, LeftForeArmIndex, RightArmIndex, RightForeArmIndex };
        string[] labels = { "左上臂", "左前臂", "右上臂", "右前臂" };

        for (int i = 0; i < indices.Length; i++)
        {
            if (!IsArmSensorRequiredForCalibration(indices[i])) continue;
            if (!CalibrationSensorReady(indices[i], indices[i], labels[i], out reason))
                return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool LeftLegCalibrationInputsReady(out string reason)
    {
        if (!CalibrationSensorReady(
                LeftThighSensorIndex, LeftThighIndex, "Avatar左大腿输入源", out reason))
            return false;

        if (driveLeftCalf && !CalibrationSensorReady(
                LeftCalfSensorIndex, LeftCalfIndex, "Avatar左小腿输入源", out reason))
            return false;

        reason = string.Empty;
        return true;
    }

    private bool RightLegCalibrationInputsReady(out string reason)
    {
        if (!CalibrationSensorReady(
                RightThighSensorIndex, RightThighIndex, "Avatar右大腿输入源", out reason))
            return false;

        if (driveRightCalf && !CalibrationSensorReady(
                RightCalfSensorIndex, RightCalfIndex, "Avatar右小腿输入源", out reason))
            return false;

        reason = string.Empty;
        return true;
    }

    private bool ArmCalibrationInputsStable(out string reason)
    {
        if (!ArmCalibrationInputsReady(out reason))
            return false;

        int[] indices = { LeftArmIndex, LeftForeArmIndex, RightArmIndex, RightForeArmIndex };
        string[] labels = { "左上臂", "左前臂", "右上臂", "右前臂" };
        for (int i = 0; i < indices.Length; i++)
        {
            int index = indices[i];
            if (!IsArmSensorRequiredForCalibration(index)) continue;
            if (!processor.IsDeviceOnline(index))
            {
                reason = $"{labels[i]}实时数据未在线，请等待新帧";
                return false;
            }
            if (!processor.IsDeviceStable(index))
            {
                reason = $"{labels[i]}尚未稳定：{processor.GetStableFrameCount(index)}/{config.requiredStableFrames}（按{config.requiredStableDurationSeconds:F1}s稳定时长换算）";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private void TryAccumulateIndependentCalibrationSamples()
    {
        if (processor == null || calibrationLastConsumedSequences == null) return;
        Quaternion[] input = processor.CalibrationQuaternions;
        if (input == null) return;

        for (int sensorIndex = 0; sensorIndex < calibrationLastConsumedSequences.Length; sensorIndex++)
        {
            if (!IsSensorParticipatingInCurrentCalibration(sensorIndex)) continue;

            int required = GetSensorCalibrationRequiredSamples(sensorIndex);
            if (required <= 0 || calibrationAcceptedSampleCounts[sensorIndex] >= required)
                continue;

            long sequence = processor.GetCalibrationSampleSequence(sensorIndex);
            if (sequence < 0 || sequence <= calibrationLastConsumedSequences[sensorIndex])
                continue;

            // 无论接受还是拒绝，都只消费该路自己的这个序号，避免同一异常帧被重复统计。
            calibrationLastConsumedSequences[sensorIndex] = sequence;

            if (input.Length <= sensorIndex || !IsQuaternionFinite(input[sensorIndex]))
            {
                calibrationRejectedSampleCounts[sensorIndex]++;
                continue;
            }

            Quaternion q = NormalizeQuaternionSafe(input[sensorIndex]);
            TryAcceptIndependentCalibrationSample(sensorIndex, q, input);
        }
    }

    private void TryAcceptIndependentCalibrationSample(
        int sensorIndex,
        Quaternion q,
        Quaternion[] calibrationInput)
    {
        float hardJumpThreshold = Mathf.Max(6f, armCalibrationMaxDriftDeg);

        if (!calibrationHasPreviousAccepted[sensorIndex])
        {
            AcceptIndependentCalibrationSample(sensorIndex, q, calibrationInput);
            return;
        }

        float stepDeg = Quaternion.Angle(calibrationPreviousAccepted[sensorIndex], q);
        calibrationLastStepDeg[sensorIndex] = stepDeg;
        if (stepDeg <= hardJumpThreshold)
        {
            calibrationHasPendingJump[sensorIndex] = false;
            calibrationPendingJumpCounts[sensorIndex] = 0;
            AcceptIndependentCalibrationSample(sensorIndex, q, calibrationInput);
            return;
        }

        calibrationRejectedSampleCounts[sensorIndex]++;
        if (calibrationHasPendingJump[sensorIndex] &&
            Quaternion.Angle(calibrationPendingJump[sensorIndex], q) <= hardJumpThreshold)
        {
            calibrationPendingJumpCounts[sensorIndex]++;
        }
        else
        {
            calibrationPendingJump[sensorIndex] = q;
            calibrationHasPendingJump[sensorIndex] = true;
            calibrationPendingJumpCounts[sensorIndex] = 1;
        }

        // 单个孤立尖峰会被丢弃；若后续帧在新位置连续稳定，则只重启这一条通道，
        // 防止永远拿新稳定簇与旧参考比较而永久卡死。
        int reanchorFrames = Mathf.Clamp(independentCalibrationReanchorFrames, 2, 4);
        if (calibrationPendingJumpCounts[sensorIndex] >= reanchorFrames)
        {
            ResetIndependentCalibrationChannel(sensorIndex);
            calibrationRestartCounts[sensorIndex]++;
            AcceptIndependentCalibrationSample(sensorIndex, q, calibrationInput);
        }
    }

    private void AcceptIndependentCalibrationSample(
        int sensorIndex,
        Quaternion q,
        Quaternion[] calibrationInput)
    {
        bool accepted = true;
        string sampleReason = string.Empty;
        if (IsArmSensorRequiredForCalibration(sensorIndex))
        {
            if (calibrationAcceptedSampleCounts[sensorIndex] == 0)
                armSamplingHemisphereReference[sensorIndex] = q;
            if (Quaternion.Dot(q, armSamplingHemisphereReference[sensorIndex]) < 0f)
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            armSamplingQuaternionSums[sensorIndex] += new Vector4(q.x, q.y, q.z, q.w);
        }
        else if (sensorIndex == LeftThighSensorIndex && leftLegDriver != null)
        {
            accepted = leftLegDriver.TryAccumulateCalibrationSampleForSensor(
                sensorIndex, q, out sampleReason);
            if (!accepted) Debug.LogWarning($"[V7左腿独立采样] {sampleReason}");
        }
        else if (sensorIndex == RightThighSensorIndex && rightLegDriver != null)
        {
            accepted = rightLegDriver.TryAccumulateCalibrationSampleForSensor(
                sensorIndex, q, out sampleReason);
            if (!accepted) Debug.LogWarning($"[V8右腿独立采样] {sampleReason}");
        }
        else if (sensorIndex == LeftCalfSensorIndex && leftCalfParticipatesInCalibration &&
                 leftLegDriver != null)
        {
            accepted = leftLegDriver.TryAccumulateCalibrationSampleForSensor(
                sensorIndex, q, out sampleReason);
            if (!accepted) Debug.LogWarning($"[V7左小腿独立采样] {sampleReason}");
        }
        else if (sensorIndex == RightCalfSensorIndex && rightCalfParticipatesInCalibration &&
                 rightLegDriver != null)
        {
            accepted = rightLegDriver.TryAccumulateCalibrationSampleForSensor(
                sensorIndex, q, out sampleReason);
            if (!accepted) Debug.LogWarning($"[V8右小腿独立采样] {sampleReason}");
        }
        else if (sensorIndex == LeftCalfSensorIndex && leftStandaloneCalfParticipatesInCalibration)
        {
            AccumulateStandaloneQuaternion(sensorIndex, ref q);
        }
        else if (sensorIndex == RightCalfSensorIndex && rightStandaloneCalfParticipatesInCalibration)
        {
            AccumulateStandaloneQuaternion(sensorIndex, ref q);
        }
        else if (IsGenericStandaloneParticipant(sensorIndex))
        {
            AccumulateStandaloneQuaternion(sensorIndex, ref q);
        }

        if (!accepted)
        {
            calibrationRejectedSampleCounts[sensorIndex]++;
            return;
        }

        calibrationPreviousAccepted[sensorIndex] = q;
        calibrationHasPreviousAccepted[sensorIndex] = true;
        calibrationHasPendingJump[sensorIndex] = false;
        calibrationPendingJumpCounts[sensorIndex] = 0;
        calibrationAcceptedSampleCounts[sensorIndex]++;
    }

    private void AccumulateStandaloneQuaternion(int sensorIndex, ref Quaternion q)
    {
        if (standaloneSamplingQuaternionSums == null ||
            standaloneSamplingHemisphereReferences == null ||
            sensorIndex < 0 || sensorIndex >= standaloneSamplingQuaternionSums.Length)
            return;

        if (calibrationAcceptedSampleCounts[sensorIndex] == 0)
            standaloneSamplingHemisphereReferences[sensorIndex] = q;
        if (Quaternion.Dot(q, standaloneSamplingHemisphereReferences[sensorIndex]) < 0f)
            q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        standaloneSamplingQuaternionSums[sensorIndex] += new Vector4(q.x, q.y, q.z, q.w);
    }

    private Quaternion GetStandaloneCalibrationAverage(int sensorIndex)
    {
        if (standaloneSamplingQuaternionSums == null ||
            sensorIndex < 0 || sensorIndex >= standaloneSamplingQuaternionSums.Length ||
            calibrationAcceptedSampleCounts == null ||
            calibrationAcceptedSampleCounts[sensorIndex] <= 0)
            return Quaternion.identity;

        Vector4 sum = standaloneSamplingQuaternionSums[sensorIndex];
        return NormalizeQuaternionSafe(new Quaternion(sum.x, sum.y, sum.z, sum.w));
    }

    private void ResetIndependentCalibrationChannel(int sensorIndex)
    {
        calibrationAcceptedSampleCounts[sensorIndex] = 0;
        calibrationPreviousAccepted[sensorIndex] = Quaternion.identity;
        calibrationHasPreviousAccepted[sensorIndex] = false;
        calibrationHasPendingJump[sensorIndex] = false;
        calibrationPendingJumpCounts[sensorIndex] = 0;

        if (sensorIndex >= 0 && sensorIndex < 4)
        {
            armSamplingHemisphereReference[sensorIndex] = Quaternion.identity;
            armSamplingQuaternionSums[sensorIndex] = Vector4.zero;
        }
        else if (sensorIndex == LeftThighSensorIndex || sensorIndex == LeftCalfSensorIndex)
        {
            leftLegDriver?.ClearCalibrationSamplesForSensor(sensorIndex);
        }
        else if (sensorIndex == RightThighSensorIndex || sensorIndex == RightCalfSensorIndex)
        {
            rightLegDriver?.ClearCalibrationSamplesForSensor(sensorIndex);
        }

        if (standaloneSamplingQuaternionSums != null &&
            sensorIndex >= 0 && sensorIndex < standaloneSamplingQuaternionSums.Length)
            standaloneSamplingQuaternionSums[sensorIndex] = Vector4.zero;
        if (standaloneSamplingHemisphereReferences != null &&
            sensorIndex >= 0 && sensorIndex < standaloneSamplingHemisphereReferences.Length)
            standaloneSamplingHemisphereReferences[sensorIndex] = Quaternion.identity;
    }

    private bool IsSensorParticipatingInCurrentCalibration(int sensorIndex)
    {
        if (IsArmSensorRequiredForCalibration(sensorIndex)) return true;
        if (sensorIndex == LeftThighSensorIndex) return leftLegParticipatesInCalibration;
        if (sensorIndex == RightThighSensorIndex) return rightLegParticipatesInCalibration;
        if (sensorIndex == LeftCalfSensorIndex)
            return leftCalfParticipatesInCalibration || leftStandaloneCalfParticipatesInCalibration;
        if (sensorIndex == RightCalfSensorIndex)
            return rightCalfParticipatesInCalibration || rightStandaloneCalfParticipatesInCalibration;
        if (IsGenericStandaloneParticipant(sensorIndex)) return true;
        return false;
    }

    private bool CanCompleteCalibrationSampling(out string reason)
    {
        int count = config != null ? config.deviceCount : 9;
        for (int sensorIndex = 0; sensorIndex < count; sensorIndex++)
        {
            if (!IsSensorParticipatingInCurrentCalibration(sensorIndex)) continue;
            int required = GetSensorCalibrationRequiredSamples(sensorIndex);
            int accepted = GetSensorCalibrationAcceptedSamples(sensorIndex);
            if (accepted < required)
            {
                reason = $"逐路补采中：{sensorIndex + 1:00}{GetSensorRoleLabel(sensorIndex)} " +
                    $"{accepted}/{required}，拒绝{GetSensorCalibrationRejectedSamples(sensorIndex)}";
                return false;
            }
        }

        if ((leftArmParticipatesInCalibration || rightArmParticipatesInCalibration) && !AreArmCalibrationInputsFresh())
        {
            reason = "等待当前参与标定的手臂数据在各自自适应门限内恢复";
            return false;
        }

        if (leftLegParticipatesInCalibration &&
            !AreLegCalibrationInputsFresh(LeftThighSensorIndex, LeftCalfSensorIndex, leftCalfParticipatesInCalibration))
        {
            reason = "等待左腿标定数据在各自自适应门限内恢复";
            return false;
        }

        if (rightLegParticipatesInCalibration &&
            !AreLegCalibrationInputsFresh(RightThighSensorIndex, RightCalfSensorIndex, rightCalfParticipatesInCalibration))
        {
            reason = "等待右腿标定数据在各自自适应门限内恢复";
            return false;
        }

        if (leftStandaloneCalfParticipatesInCalibration && !HasFreshCalibrationSample(LeftCalfSensorIndex))
        {
            reason = "等待左小腿07实时数据恢复在线";
            return false;
        }
        if (rightStandaloneCalfParticipatesInCalibration && !HasFreshCalibrationSample(RightCalfSensorIndex))
        {
            reason = "等待右小腿09实时数据恢复在线";
            return false;
        }

        if (genericStandaloneParticipatesInCalibration != null)
        {
            for (int i = 0; i < genericStandaloneParticipatesInCalibration.Length; i++)
            {
                if (genericStandaloneParticipatesInCalibration[i] && !HasFreshCalibrationSample(i))
                {
                    reason = $"等待{i + 1:00}{GetSensorRoleLabel(i)}实时数据恢复在线";
                    return false;
                }
            }
        }

        reason = string.Empty;
        return true;
    }

    private void MarkIncompleteCalibrationSensorsFailed()
    {
        if (calibrationAcceptedSampleCounts == null) return;
        for (int i = 0; i < calibrationAcceptedSampleCounts.Length; i++)
        {
            if (!IsSensorParticipatingInCurrentCalibration(i)) continue;
            int required = GetSensorCalibrationRequiredSamples(i);
            if (required > 0 &&
                (calibrationAcceptedSampleCounts[i] < required || !HasFreshCalibrationSample(i)))
                MarkSensorCalibrationFailed(i);
        }
    }

    private string BuildIndependentSamplingTimeoutReason()
    {
        string reason = "独立采样超时";
        if (calibrationAcceptedSampleCounts == null) return reason;

        for (int i = 0; i < calibrationAcceptedSampleCounts.Length; i++)
        {
            if (!IsSensorParticipatingInCurrentCalibration(i)) continue;
            int required = GetSensorCalibrationRequiredSamples(i);
            if (required <= 0) continue;
            bool completeAndFresh = calibrationAcceptedSampleCounts[i] >= required && HasFreshCalibrationSample(i);
            if (completeAndFresh) continue;

            double ageMs = GetSensorFrameAgeMilliseconds(i);
            string ageText = double.IsInfinity(ageMs) ? "无帧" : $"{ageMs:F0}ms";
            reason += $"；{i + 1:00}{GetSensorRoleLabel(i)} " +
                $"采{calibrationAcceptedSampleCounts[i]}/{required} 拒{calibrationRejectedSampleCounts[i]} " +
                $"Hz{GetSensorFrameRateHz(i):F1} 龄{ageText} 在线{(IsSensorOnline(i) ? "是" : "否")}";
        }
        return reason;
    }

    private bool ArmSensorsReady(out string reason)
    {
        reason = "";

        if (State == null)
        {
            reason = "State 未初始化";
            return false;
        }

        int[] indices = { LeftArmIndex, LeftForeArmIndex, RightArmIndex, RightForeArmIndex };
        string[] labels = { "左上臂", "左前臂", "右上臂", "右前臂" };

        Quaternion[] qs = processor?.TransformedQuaternions;
        if (qs == null)
        {
            reason = "坐标转换后的手臂四元数数组不存在";
            return false;
        }

        for (int i = 0; i < indices.Length; i++)
        {
            int index = indices[i];
            string label = labels[i];

            if (!IndexInRange(index))
            {
                reason = $"{label}索引 {index} 越界。当前 config.deviceCount={config.deviceCount}，手臂需要至少 4 个设备。";
                return false;
            }

            if (!State.GetDeviceHasData(index))
            {
                reason = $"{label}传感器（代码索引 {index}）尚未到数";
                return false;
            }

            if (qs.Length <= index || !IsQuaternionFinite(qs[index]))
            {
                reason = $"{label} transformed quaternion 不存在或非法";
                return false;
            }

            if (GetBoneTransform(index) == null)
            {
                reason = $"{label}骨骼未找到：请检查 MotionCaptureConfig.boneNames[{index}]";
                return false;
            }
        }

        return true;
    }

    private bool LegSensorsReady(
        int thighSensorIndex,
        int calfSensorIndex,
        int thighBoneIndex,
        int calfBoneIndex,
        bool needCalfSensor,
        string thighLabel,
        string calfLabel,
        out string reason)
    {
        reason = "";

        if (State == null)
        {
            reason = "State 未初始化";
            return false;
        }

        if (!IndexInRange(thighSensorIndex) || (needCalfSensor && !IndexInRange(calfSensorIndex)))
        {
            reason = $"{thighLabel}/{calfLabel}输入源索引越界。" +
                $"thighSensorIndex={thighSensorIndex}, calfSensorIndex={calfSensorIndex}, deviceCount={config.deviceCount}";
            return false;
        }

        if (!State.GetDeviceHasData(thighSensorIndex))
        {
            reason = $"{thighLabel}传感器（代码索引 {thighSensorIndex}）尚未到数";
            return false;
        }

        if (needCalfSensor && !State.GetDeviceHasData(calfSensorIndex))
        {
            reason = $"{calfLabel}传感器（代码索引 {calfSensorIndex}）尚未到数";
            return false;
        }

        Quaternion[] qs = processor?.TransformedQuaternions;
        int requiredSensorIndex = needCalfSensor
            ? Mathf.Max(thighSensorIndex, calfSensorIndex)
            : thighSensorIndex;
        if (qs == null || qs.Length <= requiredSensorIndex)
        {
            reason = $"坐标转换后的{thighLabel}/{calfLabel}四元数不存在";
            return false;
        }

        if (!IsQuaternionFinite(qs[thighSensorIndex]))
        {
            reason = $"{thighLabel} transformed quaternion 非法";
            return false;
        }

        if (needCalfSensor && !IsQuaternionFinite(qs[calfSensorIndex]))
        {
            reason = $"{calfLabel} transformed quaternion 非法";
            return false;
        }

        Transform thigh = GetBoneTransform(thighBoneIndex);
        Transform calf = GetBoneTransform(calfBoneIndex);
        if (thigh == null || calf == null)
        {
            reason = $"{thighLabel}/{calfLabel}目标骨骼未找到";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 自动左腿稳定标定。
    /// </summary>
    private void AutoCalibrateLeftLegIfPossible(bool isStable)
    {
        if (!driveLeftLeg) return;
        if (leftLegDriver == null) return;
        if (leftLegDriver.IsCalibrated) return;
        if (!Serial.IsConnected) return;

        ApplyInspectorSettingsToLeftLegDriver();

        if (!LeftLegSensorsReady(out _))
        {
            leftLegDriver.ClearCalibrationSamples();
            return;
        }

        if (useStableCalibration)
        {
            if (!isStable)
            {
                leftLegDriver.ClearCalibrationSamples();
                return;
            }

            bool sampleOk = leftLegDriver.TryAccumulateCalibrationSample(
                processor.TransformedQuaternions,
                out string sampleReason);

            if (!sampleOk)
            {
                Debug.LogWarning($"[AutoCalibrateLeftLeg] 累计标定样本失败：{sampleReason}");
                return;
            }

            if (leftLegDriver.HasEnoughCalibrationSamples)
            {
                bool committed = TryCommitStableLeftCalibration(out string commitReason);
                if (!committed)
                    Debug.LogWarning($"[AutoCalibrateLeftLeg] 提交稳定标定失败：{commitReason}");
                else
                    Debug.Log("[AutoCalibrateLeftLeg] 左腿稳定标定已自动完成");
            }
        }
        else
        {
            bool ok = leftLegDriver.TryCalibrate(
                processor.TransformedQuaternions,
                GetBoneTransform(LeftThighIndex),
                GetBoneTransform(LeftCalfIndex),
                restLocalRotations[LeftThighIndex],
                restLocalRotations[LeftCalfIndex],
                out string reason);

            if (!ok)
                Debug.LogWarning($"[AutoCalibrateLeftLeg] 自动单帧标定失败：{reason}");
        }
    }

    /// <summary>
    /// 自动右腿稳定标定。
    /// </summary>
    private void AutoCalibrateRightLegIfPossible(bool isStable)
    {
        if (!driveRightLeg) return;
        if (rightLegDriver == null) return;
        if (rightLegDriver.IsCalibrated) return;
        if (!Serial.IsConnected) return;

        ApplyInspectorSettingsToRightLegDriver();

        if (!RightLegSensorsReady(out _))
        {
            rightLegDriver.ClearCalibrationSamples();
            return;
        }

        if (useStableCalibration)
        {
            if (!isStable)
            {
                rightLegDriver.ClearCalibrationSamples();
                return;
            }

            bool sampleOk = rightLegDriver.TryAccumulateCalibrationSample(
                processor.TransformedQuaternions,
                out string sampleReason);

            if (!sampleOk)
            {
                Debug.LogWarning($"[AutoCalibrateRightLeg] 累计标定样本失败：{sampleReason}");
                return;
            }

            if (rightLegDriver.HasEnoughCalibrationSamples)
            {
                bool committed = TryCommitStableRightCalibration(out string commitReason);
                if (!committed)
                    Debug.LogWarning($"[AutoCalibrateRightLeg] 提交稳定标定失败：{commitReason}");
                else
                    Debug.Log("[AutoCalibrateRightLeg] 右腿稳定标定已自动完成");
            }
        }
        else
        {
            bool ok = rightLegDriver.TryCalibrate(
                processor.TransformedQuaternions,
                GetBoneTransform(RightThighIndex),
                GetBoneTransform(RightCalfIndex),
                restLocalRotations[RightThighIndex],
                restLocalRotations[RightCalfIndex],
                out string reason);

            if (!ok)
                Debug.LogWarning($"[AutoCalibrateRightLeg] 自动单帧标定失败：{reason}");
        }
    }

    /// <summary>
    /// 自动手臂标定。手臂采用 Unity_DLL_Source 的世界空间偏移标定法。
    /// </summary>
    private void AutoCalibrateArmsIfPossible(bool isStable)
    {
        if (!driveArms) return;
        if (armDriver == null) return;
        if (armDriver.IsCalibrated) return;
        if (!Serial.IsConnected) return;

        ApplyInspectorSettingsToArmDriver();

        if (!ArmSensorsReady(out _)) return;
        if (useStableCalibration && !isStable) return;

        bool ok = TryCalibrateArms(out string reason);
        if (!ok)
            Debug.LogWarning($"[AutoCalibrateArms] 手臂自动标定失败：{reason}");
        else
            Debug.Log("[AutoCalibrateArms] 手臂稳定标定已自动完成");
    }

    private bool TryCalibrateArms(out string reason)
    {
        return TryCalibrateArms(processor?.TransformedQuaternions, out reason);
    }

    private bool TryCalibrateArms(Quaternion[] calibrationQuaternions, out string reason)
    {
        reason = "";

        if (armDriver == null)
        {
            reason = "armDriver 未初始化";
            return false;
        }

        if (calibrationQuaternions == null)
        {
            reason = "手臂标定四元数为空";
            return false;
        }

        ApplyInspectorSettingsToArmDriver();

        return armDriver.TryCalibrate(
            calibrationQuaternions,
            GetBoneTransform(LeftArmIndex),
            GetBoneTransform(LeftForeArmIndex),
            GetBoneTransform(RightArmIndex),
            GetBoneTransform(RightForeArmIndex),
            avatarRoot,
            restLocalRotations[LeftArmIndex],
            restLocalRotations[LeftForeArmIndex],
            restLocalRotations[RightArmIndex],
            restLocalRotations[RightForeArmIndex],
            out reason);
    }

    private bool TryCommitStableLeftCalibration(out string reason)
    {
        reason = "";

        if (leftLegDriver == null)
        {
            reason = "leftLegDriver 未初始化";
            return false;
        }

        Transform thigh = GetBoneTransform(LeftThighIndex);
        Transform calf = GetBoneTransform(LeftCalfIndex);

        bool ok = leftLegDriver.TryCommitCalibration(
            thigh,
            calf,
            restLocalRotations[LeftThighIndex],
            restLocalRotations[LeftCalfIndex],
            out reason);

        return ok;
    }

    private bool TryCommitStableRightCalibration(out string reason)
    {
        reason = "";

        if (rightLegDriver == null)
        {
            reason = "rightLegDriver 未初始化";
            return false;
        }

        if (!IndexInRange(RightThighIndex) || !IndexInRange(RightCalfIndex))
        {
            reason = $"右腿索引越界：RightThighIndex={RightThighIndex}, RightCalfIndex={RightCalfIndex}, deviceCount={config.deviceCount}";
            return false;
        }

        Transform thigh = GetBoneTransform(RightThighIndex);
        Transform calf = GetBoneTransform(RightCalfIndex);

        bool ok = rightLegDriver.TryCommitCalibration(
            thigh,
            calf,
            restLocalRotations[RightThighIndex],
            restLocalRotations[RightCalfIndex],
            out reason);

        return ok;
    }

    private bool IndexInRange(int index)
    {
        return config != null && index >= 0 && index < config.deviceCount;
    }

    private static bool IsQuaternionFinite(Quaternion q)
    {
        return IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w);
    }

    private static bool IsFinite(float v)
    {
        return !float.IsNaN(v) && !float.IsInfinity(v);
    }
}

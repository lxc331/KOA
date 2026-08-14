using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 左大腿 delta 单轴反转测试模式。
/// 用于在保留 RotationDriver.F_ZXY 基础映射的情况下，只测试大腿局部 X/Y/Z 的符号。
/// </summary>
public enum LeftThighAxisInvertMode
{
    None = 0,
    InvertX = 1,
    InvertY = 2,
    InvertZ = 3
}

/// <summary>
/// 左大腿 twist 限制轴。
/// 这里的轴是 reorientedThighDelta 所在的骨骼局部轴，用于抑制大腿长轴自旋。
/// </summary>
public enum LeftThighTwistAxisMode
{
    LocalX = 0,
    LocalY = 1,
    LocalZ = 2
}

/// <summary>
/// 左大腿 delta 应用顺序。
/// RestThenDelta 是原始逻辑；DeltaThenRest 用于排查骨骼空间顺序错误。
/// </summary>
public enum LeftThighApplyOrder
{
    RestThenDelta = 0,
    DeltaThenRest = 1
}


/// <summary>
/// 左腿驱动器：左大腿保持原有可用逻辑；左小腿改为“相对大腿 + 膝盖单轴 hinge”驱动。
///
/// 传感器索引：
/// 物理 6号 = 代码索引 5 = 左大腿
/// 物理 7号 = 代码索引 6 = 左小腿
///
/// 关键原则：
/// 1. 大腿可以使用完整 3D delta 姿态驱动。
/// 2. 小腿不能直接使用完整 3D Quaternion 驱动，否则会出现左右甩、外翻、扭转。
/// 3. 小腿只从 sensor7 相对 sensor6 的旋转中提取“膝盖弯曲角”，然后只绕模型膝盖 hinge 轴旋转。
/// 4. 本类不使用 bone.localRotation 作为 Slerp 起点，避免 MotionCaptureController 每帧 ResetAllBonesToRest() 打断平滑。
/// </summary>
public sealed class LeftLegPoseDriver
{
    private bool preserveInactiveCalfPose;
    public const int LeftThighIndex = 5;
    public const int LeftCalfIndex = 6;

    public bool IsCalibrated { get; private set; }
    public string LastError { get; private set; } = "";

    public Quaternion ThighSensorReference { get; private set; } = Quaternion.identity;
    public Quaternion CalfSensorReference { get; private set; } = Quaternion.identity;

    public Quaternion ThighBoneRestLocal { get; private set; } = Quaternion.identity;
    public Quaternion ThighBoneRestWorld { get; private set; } = Quaternion.identity;
    public Quaternion CalfBoneRestLocal { get; private set; } = Quaternion.identity;

    public Quaternion LastAppliedThighLocal { get; private set; } = Quaternion.identity;
    public Quaternion LastAppliedCalfLocal { get; private set; } = Quaternion.identity;

    public float CurrentKneeRelativeAngleDeg { get; private set; }
    /// <summary>医学屈曲角：伸直约 0°，屈膝后增大。</summary>
    public float CurrentKneeFlexionAngleDeg => CurrentKneeRelativeAngleDeg;
    /// <summary>大小腿几何夹角：伸直约 180°，屈膝后减小。</summary>
    public float CurrentKneeIncludedAngleDeg => Mathf.Clamp(180f - CurrentKneeRelativeAngleDeg, 0f, 180f);
    public Quaternion CurrentKneeRelativeRotation { get; private set; } = Quaternion.identity;
    public Quaternion KneeSensorRelativeReference => kneeSensorRelativeReference;
    public bool IsKneeMeasurementCalibrated { get; private set; }
    public bool IsKneeMeasurementFresh { get; private set; }
    public double KneeMeasurementPairSkewSeconds { get; private set; } = double.PositiveInfinity;
    public double KneeMeasurementAgeSeconds { get; private set; } = double.PositiveInfinity;

    /// <summary>
    /// 当前版本默认驱动小腿。如果你只想验证大腿，可以在 MotionCaptureController 里把它设成 false。
    /// </summary>
    public bool DriveCalf { get; set; } = true;

    /// <summary>
    /// 只用于强制确认左大腿 Rest 姿态是否正确。正常测试必须保持 false。
    /// </summary>
    public bool ForceThighRestForDebug { get; set; } = false;

    public Vector3 ThighBoneAxisOffsetEuler
    {
        get => thighBoneAxisOffsetEuler;
        set => thighBoneAxisOffsetEuler = value;
    }

    /// <summary>
    /// 左大腿单轴反转测试。
    /// 当前排查阶段建议：RotationDriver 使用 F_ZXY，本参数依次测试 None / InvertZ / InvertX / InvertY。
    /// </summary>
    public LeftThighAxisInvertMode ThighAxisInvertMode
    {
        get => thighAxisInvertMode;
        set => thighAxisInvertMode = value;
    }

    /// <summary>
    /// 是否限制左大腿长轴自旋。用于解决“前踢幅度一大就外侧翻折上去”。
    /// </summary>
    public bool LimitThighTwist
    {
        get => limitThighTwist;
        set => limitThighTwist = value;
    }

    /// <summary>
    /// 左大腿 twist 分解轴。大多数 Humanoid 腿骨长轴是 LocalY；如果前踢仍翻，依次测 LocalX / LocalZ。
    /// </summary>
    public LeftThighTwistAxisMode ThighTwistAxisMode
    {
        get => thighTwistAxisMode;
        set => thighTwistAxisMode = value;
    }

    /// <summary>
    /// 左大腿允许保留的最大 twist 角。建议 0~15 度，排查阶段先用 0。
    /// </summary>
    public float MaxThighTwistDeg
    {
        get => maxThighTwistDeg;
        set => maxThighTwistDeg = Mathf.Clamp(value, 0f, 180f);
    }

    /// <summary>
    /// 大腿 delta 应用顺序。默认 RestThenDelta；如果前踢方向仍明显错，再测 DeltaThenRest。
    /// </summary>
    public LeftThighApplyOrder ThighApplyOrder
    {
        get => thighApplyOrder;
        set => thighApplyOrder = value;
    }

    /// <summary>
    /// 当前小腿已经不再使用完整 3D Quaternion 驱动，因此这个值默认建议保持 0。
    /// 保留该属性是为了兼容 MotionCaptureController.cs 里已有的赋值。
    /// </summary>
    public Vector3 CalfBoneAxisOffsetEuler
    {
        get => calfBoneAxisOffsetEuler;
        set => calfBoneAxisOffsetEuler = value;
    }

    /// <summary>
    /// 传感器相对旋转中，哪根轴代表膝盖弯曲。
    /// 如果弯膝没有反应，优先试 Vector3.left / Vector3.up / Vector3.forward。
    /// </summary>
    public Vector3 CalfSensorBendAxis
    {
        get => calfSensorBendAxis;
        set => calfSensorBendAxis = SafeAxis(value, Vector3.right);
    }

    /// <summary>
    /// 模型小腿 localRotation 中，哪根本地轴是膝盖 hinge 轴。
    /// 如果小腿不是向后自然弯曲，优先改这个轴。
    /// </summary>
    public Vector3 CalfBoneHingeAxis
    {
        get => calfBoneHingeAxis;
        set => calfBoneHingeAxis = SafeAxis(value, Vector3.right);
    }

    public float KneeMinAngleDeg
    {
        get => kneeMinAngleDeg;
        set => kneeMinAngleDeg = value;
    }

    public float KneeMaxAngleDeg
    {
        get => kneeMaxAngleDeg;
        set => kneeMaxAngleDeg = Mathf.Max(value, kneeMinAngleDeg);
    }

    public bool InvertKneeAngle
    {
        get => invertKneeAngle;
        set => invertKneeAngle = value;
    }

    /// <summary>
    /// 只用于测试模型小腿 hinge 轴。
    /// -1 表示关闭；设置为 60 时，小腿会强制弯曲 60 度，不再使用传感器 7 的弯膝角。
    /// 正常运行必须保持 -1。
    /// </summary>
    public float DebugForcedKneeAngleDeg
    {
        get => debugForcedKneeAngleDeg;
        set => debugForcedKneeAngleDeg = value;
    }

    /// <summary>
    /// 是否允许左小腿相对大腿做少量左右摆动。
    /// 关闭时，小腿仍然只做单轴膝盖弯曲。
    /// </summary>
    public bool EnableCalfLateralSwing
    {
        get => enableCalfLateralSwing;
        set => enableCalfLateralSwing = value;
    }

    /// <summary>
    /// 从 rawKneeDelta.eulerAngles 的哪一轴提取左右摆动角。
    /// 0=X，1=Y，2=Z。当前 Z 已用于前后弯膝，因此左右摆动优先测试 X 或 Y。
    /// </summary>
    public int CalfLateralEulerAxis
    {
        get => calfLateralEulerAxis;
        set => calfLateralEulerAxis = Mathf.Clamp(value, 0, 2);
    }

    /// <summary>
    /// 模型小腿 localRotation 中，哪根本地轴用于左右摆动。
    /// </summary>
    public Vector3 CalfBoneLateralAxis
    {
        get => calfBoneLateralAxis;
        set => calfBoneLateralAxis = SafeAxis(value, Vector3.up);
    }

    public bool InvertCalfLateralAngle
    {
        get => invertCalfLateralAngle;
        set => invertCalfLateralAngle = value;
    }

    public float CalfLateralGain
    {
        get => calfLateralGain;
        set => calfLateralGain = Mathf.Max(0f, value);
    }

    public float CalfLateralDeadZoneDeg
    {
        get => calfLateralDeadZoneDeg;
        set => calfLateralDeadZoneDeg = Mathf.Max(0f, value);
    }

    public float CalfMaxLateralAngleDeg
    {
        get => calfMaxLateralAngleDeg;
        set => calfMaxLateralAngleDeg = Mathf.Max(0f, value);
    }

    /// <summary>
    /// 只用于测试小腿左右摆动骨骼轴。-9999 表示关闭。
    /// 例如设置 25，表示强制小腿向一侧摆 25 度；设置 -25，表示向另一侧摆。
    /// </summary>
    public float DebugForcedKneeLateralAngleDeg
    {
        get => debugForcedKneeLateralAngleDeg;
        set => debugForcedKneeLateralAngleDeg = value;
    }

    /// <summary>
    /// 左右摆动和前后弯膝的组合顺序。若组合动作异常，可切换测试。
    /// true: Rest * Lateral * Bend；false: Rest * Bend * Lateral。
    /// </summary>
    public bool ApplyCalfLateralBeforeBend
    {
        get => applyCalfLateralBeforeBend;
        set => applyCalfLateralBeforeBend = value;
    }

    public float ThighRotationGain
    {
        get => thighRotationGain;
        set => thighRotationGain = Mathf.Max(0f, value);
    }

    public float ThighDeadZoneDeg
    {
        get => thighDeadZoneDeg;
        set => thighDeadZoneDeg = Mathf.Max(0f, value);
    }

    public float CalfDeadZoneDeg
    {
        get => calfDeadZoneDeg;
        set => calfDeadZoneDeg = Mathf.Max(0f, value);
    }

    public float SensorFilterSpeed
    {
        get => sensorFilterSpeed;
        set => sensorFilterSpeed = Mathf.Max(0.01f, value);
    }

    /// <summary>V77.30 默认关闭输入层低通，避免与骨骼输出 Slerp 叠加时滞。</summary>
    public bool InputLowPassEnabled { get; set; } = false;

    public bool SmoothingEnabled { get; set; } = true;
    public float SmoothingSpeed { get; set; } = 10f;
    public float DebugLogInterval { get; set; } = 0.25f;
    public bool StaticCheckLogEnabled { get; set; } = true;
    public bool KneeDebugLogEnabled { get; set; } = true;

    public int CalibrationSampleFramesRequired
    {
        get => calibrationSampleFramesRequired;
        set => calibrationSampleFramesRequired = Mathf.Max(1, value);
    }

    public int CurrentCalibrationSampleCount => thighCalibrationSamples.Count;
    public bool HasEnoughCalibrationSamples => thighCalibrationSamples.Count >= calibrationSampleFramesRequired;
    public int CalibrationSampleCount => thighCalibrationSamples.Count;
    public int ThighCalibrationSampleCount => thighCalibrationSamples.Count;
    public int CalfCalibrationSampleCount => calfCalibrationSamples.Count;

    public float MinBoneAngleThresholdDeg
    {
        get => minBoneAngleThresholdDeg;
        set => minBoneAngleThresholdDeg = Mathf.Max(0f, value);
    }

    public int ThighAnomalyHistorySize
    {
        get => thighAnomalyHistorySize;
        set => thighAnomalyHistorySize = Mathf.Max(2, value);
    }

    public int CalfAnomalyHistorySize
    {
        get => calfAnomalyHistorySize;
        set => calfAnomalyHistorySize = Mathf.Max(2, value);
    }

    public float ThighAnomalyThresholdDeg
    {
        get => thighAnomalyThresholdDeg;
        set => thighAnomalyThresholdDeg = Mathf.Max(1f, value);
    }

    public float CalfAnomalyThresholdDeg
    {
        get => calfAnomalyThresholdDeg;
        set => calfAnomalyThresholdDeg = Mathf.Max(1f, value);
    }

    public float LastRefToNowAngleDeg { get; private set; }
    public float LastRawThighDeltaAngleDeg { get; private set; }
    public float LastRawKneeSignedAngleDeg { get; private set; }
    public float LastClampedKneeAngleDeg { get; private set; }
    public float CurrentKneeLateralAngleDeg { get; private set; }
    public float LastRawKneeLateralAngleDeg { get; private set; }
    public float LastClampedKneeLateralAngleDeg { get; private set; }
    public Quaternion LastThighTargetLocal { get; private set; } = Quaternion.identity;
    public Quaternion LastCalfTargetLocal { get; private set; } = Quaternion.identity;

    private float lastDebugLogTime = -999f;
    private float lastStaticCheckLogTime = -999f;
    private float lastKneeDebugLogTime = -999f;

    private Quaternion kneeSensorRelativeReference = Quaternion.identity;
    private Quaternion kneeMeasurementRelativeReference = Quaternion.identity;
    private DateTime lastKneeMeasurementPairTimestampUtc = DateTime.MinValue;
    private const float KneeMeasurementMaxOffAxisDeg = 35f;

    // V77.1：标定时根据“髋 -> 膝”真实骨段方向自动得到大腿长轴。
    // 旧版固定用 LocalY 去除 twist，可能把内收/外展误当成 twist 一并删掉。
    private Vector3 calibratedThighLongAxisLocal = Vector3.up;

    private Vector3 thighBoneAxisOffsetEuler = Vector3.zero;
    private LeftThighAxisInvertMode thighAxisInvertMode = LeftThighAxisInvertMode.InvertY;
    private bool limitThighTwist = true;
    private LeftThighTwistAxisMode thighTwistAxisMode = LeftThighTwistAxisMode.LocalY;
    private float maxThighTwistDeg = 0f;
    private LeftThighApplyOrder thighApplyOrder = LeftThighApplyOrder.RestThenDelta;
    private Vector3 calfBoneAxisOffsetEuler = Vector3.zero;

    // 小腿 hinge 默认参数。测试时主要改这 3 个值。
    private Vector3 calfSensorBendAxis = Vector3.forward;
    private Vector3 calfBoneHingeAxis = Vector3.forward;
    private bool invertKneeAngle = true;
    private float kneeMinAngleDeg = 0f;
    private float kneeMaxAngleDeg = 150f;

    // 只用于排查模型小腿的 hinge 轴。-1 表示关闭；60 表示强制小腿弯曲 60 度。
    private float debugForcedKneeAngleDeg = -1f;

    // 左右摆动辅助自由度。当前膝盖主弯曲已经使用 EulerZ + bone forward。
    // 若你想让小腿相对大腿也能左右摆动，需要开启第二自由度。
    private bool enableCalfLateralSwing = true;
    private int calfLateralEulerAxis = 0;              // 0=X, 1=Y, 2=Z。默认先用 X 测左右摆动。
    private Vector3 calfBoneLateralAxis = Vector3.up; // 默认先绕模型本地 Y 轴测试左右摆动。
    private bool invertCalfLateralAngle = true;
    private float calfLateralGain = 1.0f;
    private float calfLateralDeadZoneDeg = 3.0f;
    private float calfMaxLateralAngleDeg = 35.0f;     // 左右摆动不要太大，否则容易像断腿/外翻。
    private float debugForcedKneeLateralAngleDeg = -9999f;
    private bool applyCalfLateralBeforeBend = true;

    private float thighRotationGain = 1.0f;
    private float thighDeadZoneDeg = 2.0f;
    private float calfDeadZoneDeg = 3.0f;
    private float sensorFilterSpeed = 10.0f;

    private int calibrationSampleFramesRequired = 15;
    private float minBoneAngleThresholdDeg = 0.8f;

    private int thighAnomalyHistorySize = 8;
    private int calfAnomalyHistorySize = 8;
    private float thighAnomalyThresholdDeg = 180f;
    private float calfAnomalyThresholdDeg = 180f;

    private Quaternion filteredThighNow = Quaternion.identity;
    private Quaternion filteredCalfNow = Quaternion.identity;
    private bool filterInitialized = false;


    /// <summary>
    /// 骨骼输出平滑缓存。不能使用 bone.localRotation 作为平滑起点，
    /// 因为 MotionCaptureController 可能会每帧 ResetAllBonesToRest()。
    /// </summary>
    private Quaternion smoothedThighLocal = Quaternion.identity;
    private Quaternion smoothedCalfLocal = Quaternion.identity;
    private bool hasSmoothedThighLocal = false;
    private bool hasSmoothedCalfLocal = false;

    private readonly List<Quaternion> thighCalibrationSamples = new List<Quaternion>();
    private readonly List<Quaternion> calfCalibrationSamples = new List<Quaternion>();

    private readonly Queue<Quaternion> thighHistory = new Queue<Quaternion>();
    private readonly Queue<Quaternion> calfHistory = new Queue<Quaternion>();

    public void Reset()
    {
        IsCalibrated = false;
        LastError = "";

        ThighSensorReference = Quaternion.identity;
        CalfSensorReference = Quaternion.identity;

        ThighBoneRestLocal = Quaternion.identity;
        ThighBoneRestWorld = Quaternion.identity;
        CalfBoneRestLocal = Quaternion.identity;
        calibratedThighLongAxisLocal = Vector3.up;

        LastAppliedThighLocal = Quaternion.identity;
        LastAppliedCalfLocal = Quaternion.identity;
        LastThighTargetLocal = Quaternion.identity;
        LastCalfTargetLocal = Quaternion.identity;

        CurrentKneeRelativeRotation = Quaternion.identity;
        kneeSensorRelativeReference = Quaternion.identity;
        CurrentKneeRelativeAngleDeg = 0f;
        CurrentKneeLateralAngleDeg = 0f;
        LastRefToNowAngleDeg = 0f;
        LastRawThighDeltaAngleDeg = 0f;
        LastRawKneeSignedAngleDeg = 0f;
        LastClampedKneeAngleDeg = 0f;
        LastRawKneeLateralAngleDeg = 0f;
        LastClampedKneeLateralAngleDeg = 0f;
        IsKneeMeasurementCalibrated = false;
        IsKneeMeasurementFresh = false;
        KneeMeasurementPairSkewSeconds = double.PositiveInfinity;
        KneeMeasurementAgeSeconds = double.PositiveInfinity;
        kneeMeasurementRelativeReference = Quaternion.identity;
        lastKneeMeasurementPairTimestampUtc = DateTime.MinValue;

        lastDebugLogTime = -999f;
        lastStaticCheckLogTime = -999f;
        lastKneeDebugLogTime = -999f;

        filteredThighNow = Quaternion.identity;
        filteredCalfNow = Quaternion.identity;
        filterInitialized = false;

        thighHistory.Clear();
        calfHistory.Clear();

        ResetSmoothingState();
        ClearCalibrationSamples();
    }

    public void ResetSmoothingState()
    {
        smoothedThighLocal = Quaternion.identity;
        smoothedCalfLocal = Quaternion.identity;
        hasSmoothedThighLocal = false;
        hasSmoothedCalfLocal = false;
    }

    public void ClearCalibrationSamples()
    {
        thighCalibrationSamples.Clear();
        calfCalibrationSamples.Clear();
    }

    /// <summary>按真实到包通道分别累计，禁止用大腿新帧顺带重复计入小腿旧值。</summary>
    public bool TryAccumulateCalibrationSampleForSensor(
        int sensorIndex,
        Quaternion sensorQuaternion,
        out string reason)
    {
        reason = string.Empty;
        bool isThigh = sensorIndex == LeftThighIndex;
        bool isCalf = sensorIndex == LeftCalfIndex;
        if (!isThigh && !isCalf)
        {
            reason = $"左腿标定通道索引无效：{sensorIndex}";
            return false;
        }
        if (isCalf && !DriveCalf)
        {
            reason = "当前未启用左小腿驱动，不累计左小腿配对样本";
            return false;
        }

        Quaternion q = NormalizeSafe(sensorQuaternion);
        if (!IsQuaternionFinite(q))
        {
            reason = isThigh ? "左大腿标定四元数非法" : "左小腿标定四元数非法";
            LastError = reason;
            return false;
        }

        List<Quaternion> samples = isThigh ? thighCalibrationSamples : calfCalibrationSamples;
        if (samples.Count > 0)
            q = Hemispherize(samples[samples.Count - 1], q);
        samples.Add(q);
        while (samples.Count > calibrationSampleFramesRequired)
            samples.RemoveAt(0);
        LastError = string.Empty;
        return true;
    }

    public void ClearCalibrationSamplesForSensor(int sensorIndex)
    {
        if (sensorIndex == LeftThighIndex) thighCalibrationSamples.Clear();
        else if (sensorIndex == LeftCalfIndex) calfCalibrationSamples.Clear();
    }

    public bool TryAccumulateCalibrationSample(Quaternion[] transformedQuaternions, out string reason)
    {
        reason = "";

        int requiredIndex = DriveCalf ? LeftCalfIndex : LeftThighIndex;
        if (transformedQuaternions == null || transformedQuaternions.Length <= requiredIndex)
        {
            reason = "transformedQuaternions 不存在或长度不足，无法累计左腿标定样本";
            LastError = reason;
            return false;
        }

        Quaternion thighQ = NormalizeSafe(transformedQuaternions[LeftThighIndex]);
        Quaternion calfQ = DriveCalf ? NormalizeSafe(transformedQuaternions[LeftCalfIndex]) : Quaternion.identity;

        if (!IsQuaternionFinite(thighQ))
        {
            reason = "左大腿传感器四元数非法，无法累计标定样本";
            LastError = reason;
            return false;
        }

        if (DriveCalf && !IsQuaternionFinite(calfQ))
        {
            reason = "左小腿传感器四元数非法，无法累计标定样本";
            LastError = reason;
            return false;
        }

        if (thighCalibrationSamples.Count > 0)
            thighQ = Hemispherize(thighCalibrationSamples[thighCalibrationSamples.Count - 1], thighQ);

        if (DriveCalf && calfCalibrationSamples.Count > 0)
            calfQ = Hemispherize(calfCalibrationSamples[calfCalibrationSamples.Count - 1], calfQ);

        thighCalibrationSamples.Add(thighQ);
        if (DriveCalf)
            calfCalibrationSamples.Add(calfQ);

        if (thighCalibrationSamples.Count > calibrationSampleFramesRequired)
            thighCalibrationSamples.RemoveAt(0);

        if (DriveCalf && calfCalibrationSamples.Count > calibrationSampleFramesRequired)
            calfCalibrationSamples.RemoveAt(0);

        LastError = "";
        return true;
    }

    public bool TryCommitCalibration(
        Transform thigh,
        Transform calf,
        Quaternion thighRestLocal,
        Quaternion calfRestLocal,
        out string reason)
    {
        reason = "";

        if (thigh == null || (DriveCalf && calf == null))
        {
            reason = "左大腿或左小腿骨骼未找到，无法提交左腿标定";
            LastError = reason;
            return false;
        }

        if (thighCalibrationSamples.Count < calibrationSampleFramesRequired ||
            (DriveCalf && calfCalibrationSamples.Count < calibrationSampleFramesRequired))
        {
            reason = $"标定样本不足，需要 {calibrationSampleFramesRequired} 帧，当前只有 {thighCalibrationSamples.Count}/{calfCalibrationSamples.Count}";
            LastError = reason;
            return false;
        }

        Quaternion thighAvg = AverageQuaternions(thighCalibrationSamples);
        Quaternion calfAvg = DriveCalf && calfCalibrationSamples.Count > 0 ? AverageQuaternions(calfCalibrationSamples) : Quaternion.identity;

        ThighSensorReference = NormalizeSafe(thighAvg);
        CalfSensorReference = NormalizeSafe(calfAvg);

        ThighBoneRestLocal = NormalizeSafe(thighRestLocal);
        Quaternion thighParentWorldAtCalibration = thigh.parent != null ? thigh.parent.rotation : Quaternion.identity;
        ThighBoneRestWorld = NormalizeSafe(thighParentWorldAtCalibration * ThighBoneRestLocal);
        CalfBoneRestLocal = NormalizeSafe(calfRestLocal);
        calibratedThighLongAxisLocal = ResolveCalibratedThighLongAxisLocal(
            thigh, calf, ThighBoneRestWorld);

        kneeSensorRelativeReference = DriveCalf
            ? NormalizeSafe(Quaternion.Inverse(ThighSensorReference) * CalfSensorReference)
            : Quaternion.identity;

        CurrentKneeRelativeRotation = kneeSensorRelativeReference;
        CurrentKneeRelativeAngleDeg = 0f;
        CurrentKneeLateralAngleDeg = 0f;
        LastRawKneeSignedAngleDeg = 0f;
        LastClampedKneeAngleDeg = 0f;
        LastRawKneeLateralAngleDeg = 0f;
        LastClampedKneeLateralAngleDeg = 0f;

        filteredThighNow = ThighSensorReference;
        filteredCalfNow = CalfSensorReference;
        filterInitialized = true;

        thighHistory.Clear();
        calfHistory.Clear();
        EnqueueHistory(thighHistory, ThighSensorReference, thighAnomalyHistorySize);
        EnqueueHistory(calfHistory, CalfSensorReference, calfAnomalyHistorySize);

        ResetSmoothingState();
        smoothedThighLocal = ThighBoneRestLocal;
        smoothedCalfLocal = CalfBoneRestLocal;
        hasSmoothedThighLocal = true;
        hasSmoothedCalfLocal = true;

        LastAppliedThighLocal = smoothedThighLocal;
        LastAppliedCalfLocal = smoothedCalfLocal;
        LastThighTargetLocal = ThighBoneRestLocal;
        LastCalfTargetLocal = CalfBoneRestLocal;
        LastRefToNowAngleDeg = 0f;
        LastRawThighDeltaAngleDeg = 0f;
        lastStaticCheckLogTime = -999f;
        lastKneeDebugLogTime = -999f;

        IsCalibrated = true;
        LastError = "";

        Debug.Log(
            "[LeftLegPoseDriver] 左腿稳定标定完成\n" +
            $"  标定样本数 = {thighCalibrationSamples.Count}\n" +
            $"  左大腿参考传感器 Euler = {ThighSensorReference.eulerAngles}\n" +
            $"  左小腿参考传感器 Euler = {CalfSensorReference.eulerAngles}\n" +
            $"  膝关节参考相对 Euler = {kneeSensorRelativeReference.eulerAngles}\n" +
            $"  左大腿休息 localEuler = {ThighBoneRestLocal.eulerAngles}\n" +
            $"  左小腿休息 localEuler = {CalfBoneRestLocal.eulerAngles}\n" +
            $"  左大腿骨骼轴补偿 Euler = {thighBoneAxisOffsetEuler}\n" +
            $"  左大腿单轴反转模式 = {thighAxisInvertMode}\n" +
            $"  LimitThighTwist = {limitThighTwist}\n" +
            $"  ThighTwistAxis(旧Inspector兼容) = {thighTwistAxisMode}\n" +
            $"  左大腿自动长轴(local) = {calibratedThighLongAxisLocal}\n" +
            $"  MaxThighTwistDeg = {maxThighTwistDeg:F1}°\n" +
            $"  ThighApplyOrder = {thighApplyOrder}\n" +
            $"  左小腿骨骼轴补偿 Euler = {calfBoneAxisOffsetEuler}，当前小腿 hinge 模式下建议保持 0\n" +
            $"  小腿传感器弯曲轴 = {calfSensorBendAxis}\n" +
            $"  小腿骨骼 hinge 轴 = {calfBoneHingeAxis}\n" +
            $"  膝盖角度范围 = {kneeMinAngleDeg:F1}° ~ {kneeMaxAngleDeg:F1}°\n" +
            $"  InvertKneeAngle = {invertKneeAngle}\n" +
            $"  DebugForcedKneeAngleDeg = {debugForcedKneeAngleDeg:F1}，-1 表示关闭\n" +
            $"  EnableCalfLateralSwing = {enableCalfLateralSwing}\n" +
            $"  LateralEulerAxis = {AxisIndexToName(calfLateralEulerAxis)}\n" +
            $"  CalfBoneLateralAxis = {calfBoneLateralAxis}\n" +
            $"  InvertCalfLateralAngle = {invertCalfLateralAngle}\n" +
            $"  CalfMaxLateralAngleDeg = {calfMaxLateralAngleDeg:F1}°\n" +
            $"  DebugForcedKneeLateralAngleDeg = {debugForcedKneeLateralAngleDeg:F1}，-9999 表示关闭\n" +
            $"  ApplyCalfLateralBeforeBend = {applyCalfLateralBeforeBend}\n" +
            $"  左大腿旋转增益 = {thighRotationGain:F2}\n" +
            $"  左大腿死区 = {thighDeadZoneDeg:F2}°\n" +
            $"  左小腿死区 = {calfDeadZoneDeg:F2}°\n" +
            $"  传感器滤波速度 = {sensorFilterSpeed:F2}\n" +
            $"  骨骼最小输出角阈值 = {minBoneAngleThresholdDeg:F2}°\n" +
            $"  DriveCalf = {DriveCalf}\n" +
            $"  ForceThighRestForDebug = {ForceThighRestForDebug}");

        ClearCalibrationSamples();
        return true;
    }

    public bool TryCalibrate(
        Quaternion[] transformedQuaternions,
        Transform thigh,
        Transform calf,
        Quaternion thighRestLocal,
        Quaternion calfRestLocal,
        out string reason)
    {
        ClearCalibrationSamples();

        if (!TryAccumulateCalibrationSample(transformedQuaternions, out reason))
            return false;

        int oldRequired = calibrationSampleFramesRequired;
        calibrationSampleFramesRequired = 1;

        bool ok = TryCommitCalibration(thigh, calf, thighRestLocal, calfRestLocal, out reason);

        calibrationSampleFramesRequired = oldRequired;
        return ok;
    }

    /// <summary>
    /// 标定时间配对膝角测量零位。V8同时允许驱动Avatar小腿；保存时间配对后的06/07相对姿态零位，
    /// 运行时只提取已经验证过的膝铰链Euler Z分量，避免把07的长轴漂移误算成屈膝。
    /// </summary>
    public bool TryCalibrateKneeMeasurement(
        Quaternion thighSensor,
        Quaternion calfSensor,
        Transform thigh,
        Transform calf,
        out string reason)
    {
        reason = string.Empty;
        thighSensor = NormalizeSafe(thighSensor);
        calfSensor = NormalizeSafe(calfSensor);
        if (!IsQuaternionFinite(thighSensor) || !IsQuaternionFinite(calfSensor))
        {
            reason = "左膝测量标定四元数非法";
            return false;
        }
        if (thigh == null || calf == null)
        {
            reason = "左膝测量所需大小腿骨骼为空";
            return false;
        }

        kneeMeasurementRelativeReference = NormalizeSafe(
            Quaternion.Inverse(thighSensor) * calfSensor);
        lastKneeMeasurementPairTimestampUtc = DateTime.MinValue;
        IsKneeMeasurementCalibrated = true;
        IsKneeMeasurementFresh = true;
        CurrentKneeRelativeAngleDeg = 0f;
        CurrentKneeRelativeRotation = Quaternion.identity;
        KneeMeasurementPairSkewSeconds = 0d;
        KneeMeasurementAgeSeconds = 0d;
        return true;
    }

    /// <summary>用已做时间配对的06/07姿态更新左膝医学屈曲角。</summary>
    public bool UpdateKneeMeasurement(
        Quaternion thighSensor,
        Quaternion calfSensor,
        DateTime pairTimestampUtc,
        double pairSkewSeconds,
        double dataAgeSeconds,
        double maxFreshAgeSeconds)
    {
        KneeMeasurementPairSkewSeconds = pairSkewSeconds;
        KneeMeasurementAgeSeconds = dataAgeSeconds;
        IsKneeMeasurementFresh = IsKneeMeasurementCalibrated &&
            dataAgeSeconds <= Math.Max(0.05, maxFreshAgeSeconds);
        if (!IsKneeMeasurementCalibrated || !IsKneeMeasurementFresh ||
            pairTimestampUtc == DateTime.MinValue ||
            pairTimestampUtc <= lastKneeMeasurementPairTimestampUtc)
            return false;

        thighSensor = NormalizeSafe(thighSensor);
        calfSensor = NormalizeSafe(calfSensor);
        if (!IsQuaternionFinite(thighSensor) || !IsQuaternionFinite(calfSensor))
            return false;

        Quaternion relativeNow = NormalizeSafe(
            Quaternion.Inverse(thighSensor) * calfSensor);
        Quaternion relativeDelta = NormalizeSafe(
            relativeNow * Quaternion.Inverse(kneeMeasurementRelativeReference));
        Vector3 e = relativeDelta.eulerAngles;
        e.x = NormalizeAngle(e.x);
        e.y = NormalizeAngle(e.y);
        e.z = NormalizeAngle(e.z);

        float offAxisDeg = Mathf.Sqrt(e.x * e.x + e.y * e.y);
        if (offAxisDeg > KneeMeasurementMaxOffAxisDeg)
        {
            // 数据8中07在膝盖未专门弯曲时出现>100°非铰链漂移。
            // 该帧不能被解释为膝屈曲：保持最后可信值，并明确标记无效。
            IsKneeMeasurementFresh = false;
            lastKneeMeasurementPairTimestampUtc = pairTimestampUtc;
            return false;
        }

        float flexionDeg = Mathf.Abs(e.z);
        if (flexionDeg < Mathf.Max(0f, calfDeadZoneDeg))
            flexionDeg = 0f;

        CurrentKneeRelativeAngleDeg = Mathf.Clamp(
            flexionDeg,
            kneeMinAngleDeg,
            kneeMaxAngleDeg);
        CurrentKneeRelativeRotation = relativeNow;
        LastRawKneeSignedAngleDeg = flexionDeg;
        LastClampedKneeAngleDeg = CurrentKneeRelativeAngleDeg;
        lastKneeMeasurementPairTimestampUtc = pairTimestampUtc;
        return true;
    }

    public void MarkKneeMeasurementStale(double dataAgeSeconds)
    {
        KneeMeasurementAgeSeconds = dataAgeSeconds;
        IsKneeMeasurementFresh = false;
    }
	
	private static float NormalizeAngle(float angle)
	{
		angle %= 360f;

		if (angle > 180f)
			angle -= 360f;

		if (angle < -180f)
			angle += 360f;

		return angle;
	}

    public bool Apply(Quaternion[] transformedQuaternions, Transform thigh, Transform calf)
    {
        if (!IsCalibrated)
        {
            LastError = "左腿尚未标定";
            return false;
        }

        int requiredIndex = DriveCalf ? LeftCalfIndex : LeftThighIndex;
        if (transformedQuaternions == null || transformedQuaternions.Length <= requiredIndex)
        {
            LastError = "transformedQuaternions 不存在或长度不足";
            return false;
        }

        if (thigh == null || (DriveCalf && calf == null))
        {
            LastError = "左大腿或左小腿骨骼为空";
            return false;
        }

        Quaternion thighRaw = NormalizeSafe(transformedQuaternions[LeftThighIndex]);
        Quaternion calfRaw = DriveCalf ? NormalizeSafe(transformedQuaternions[LeftCalfIndex]) : CalfSensorReference;

        if (!IsQuaternionFinite(thighRaw) || (DriveCalf && !IsQuaternionFinite(calfRaw)))
        {
            LastError = "左腿传感器当前四元数非法";
            return false;
        }

        // 1) 半球对齐，避免 q 和 -q 表示同一姿态时出现数值跳变。
        thighRaw = Hemispherize(thighHistory.Count > 0 ? GetLast(thighHistory) : ThighSensorReference, thighRaw);
        if (DriveCalf)
            calfRaw = Hemispherize(calfHistory.Count > 0 ? GetLast(calfHistory) : CalfSensorReference, calfRaw);

        // 2) V77.25：输入异常过滤已由 MotionDataHub 集中执行一次。
        // 驱动层不再进行历史平均替换，避免快速前踢被二次误判后冻结或画弧。
        Quaternion thighNow = thighRaw;
        Quaternion calfNow = DriveCalf ? calfRaw : CalfSensorReference;

        // 3) V77.30：默认直通当前传感器姿态，只保留最终骨骼输出层平滑。
        if (!InputLowPassEnabled)
        {
            filteredThighNow = thighNow;
            filteredCalfNow = calfNow;
            filterInitialized = true;
        }
        else if (!filterInitialized)
        {
            filteredThighNow = thighNow;
            filteredCalfNow = calfNow;
            filterInitialized = true;
        }
        else
        {
            float ft = 1f - Mathf.Exp(-sensorFilterSpeed * Time.deltaTime);
            filteredThighNow = Quaternion.Slerp(filteredThighNow, thighNow, ft);
            if (DriveCalf)
                filteredCalfNow = Quaternion.Slerp(filteredCalfNow, calfNow, ft);
        }

        thighNow = NormalizeSafe(filteredThighNow);
        calfNow = NormalizeSafe(filteredCalfNow);

        // 4) 写回历史。
        EnqueueHistory(thighHistory, thighNow, thighAnomalyHistorySize);
        if (DriveCalf)
            EnqueueHistory(calfHistory, calfNow, calfAnomalyHistorySize);

        Quaternion thighAxisOffset = Quaternion.Euler(thighBoneAxisOffsetEuler);

        // 5) V69：恢复 V57 中已经用于大腿测试的局部增量链路。
        // inverse(reference) * now 保留传感器自身安装坐标中的动作增量，
        // 再通过左大腿 InvertY 将真人前踢映射到 Avatar 髋关节屈伸轴。
        Quaternion rawThighDelta =
            NormalizeSafe(Quaternion.Inverse(ThighSensorReference) * thighNow);

        Quaternion mappedThighDelta = ApplyThighAxisInvert(rawThighDelta, thighAxisInvertMode);

        LastRefToNowAngleDeg = Quaternion.Angle(ThighSensorReference, thighNow);
        LastRawThighDeltaAngleDeg = Quaternion.Angle(Quaternion.identity, mappedThighDelta);

        Quaternion effectiveThighDelta =
            ApplyDeadZoneAndGain(mappedThighDelta, thighDeadZoneDeg, thighRotationGain);

        Quaternion reorientedThighDelta =
            NormalizeSafe(thighAxisOffset * effectiveThighDelta * Quaternion.Inverse(thighAxisOffset));

        // V77.1：围绕标定得到的真实“髋到膝”长轴去除 twist；保留前后踢与内收/外展两个摆动自由度。
        if (limitThighTwist)
        {
            reorientedThighDelta = LimitTwistAroundAxis(
                reorientedThighDelta,
                calibratedThighLongAxisLocal,
                maxThighTwistDeg);
        }

        Quaternion thighTarget;
        if (thighApplyOrder == LeftThighApplyOrder.DeltaThenRest)
            thighTarget = NormalizeSafe(reorientedThighDelta * ThighBoneRestLocal);
        else
            thighTarget = NormalizeSafe(ThighBoneRestLocal * reorientedThighDelta);

        LastThighTargetLocal = thighTarget;

        // V8：DriveCalf=false时允许仅大腿测试；DriveCalf=true时同步驱动同侧小腿。
        // 这样可以避免小腿传感器噪声或未连接导致大腿标定失败，也避免小腿数据引起的腿部自旋。
        if (!DriveCalf)
        {
            CurrentKneeLateralAngleDeg = 0f;
            LastRawKneeLateralAngleDeg = 0f;
            LastClampedKneeLateralAngleDeg = 0f;
            LastCalfTargetLocal = CalfBoneRestLocal;

            if (ForceThighRestForDebug)
                ApplyThighRotation(thigh, ThighBoneRestLocal);
            else
                ApplyThighRotation(thigh, thighTarget);

            if (calf != null && !preserveInactiveCalfPose)
            {
                calf.localRotation = CalfBoneRestLocal;
                smoothedCalfLocal = CalfBoneRestLocal;
                hasSmoothedCalfLocal = true;
            }

            LastAppliedThighLocal = thigh.localRotation;
            LastAppliedCalfLocal = calf != null ? calf.localRotation : CalfBoneRestLocal;
            LastError = "";
            return true;
        }

        // 6) 左小腿目标姿态：sensor7 相对 sensor6，只提取膝盖弯曲角，然后单轴 hinge 驱动。
        Quaternion kneeSensorRelativeNow =
            NormalizeSafe(Quaternion.Inverse(thighNow) * calfNow);

        CurrentKneeRelativeRotation = kneeSensorRelativeNow;

        Quaternion rawKneeDelta =
            NormalizeSafe(kneeSensorRelativeNow * Quaternion.Inverse(kneeSensorRelativeReference));
		
        Vector3 rawKneeEuler = rawKneeDelta.eulerAngles;
        Vector3 normKneeEuler = new Vector3(
            NormalizeAngle(rawKneeEuler.x),
            NormalizeAngle(rawKneeEuler.y),
            NormalizeAngle(rawKneeEuler.z));

        // 主自由度：前后弯膝。你当前项目里这一项已经验证为 EulerZ + bone forward。
        float rawKneeSignedAngle = normKneeEuler.z;
        // float rawKneeSignedAngle = ExtractSignedAngleAroundAxis(rawKneeDelta, calfSensorBendAxis);

        if (invertKneeAngle)
            rawKneeSignedAngle = -rawKneeSignedAngle;

        LastRawKneeSignedAngleDeg = rawKneeSignedAngle;

        float kneeAngle = rawKneeSignedAngle;

        if (Mathf.Abs(kneeAngle) < calfDeadZoneDeg)
            kneeAngle = 0f;

        // 主膝盖弯曲只允许 0~150 度，避免反关节。
        kneeAngle = Mathf.Clamp(kneeAngle, kneeMinAngleDeg, kneeMaxAngleDeg);

        // 调试模式：绕过传感器角度，只测试模型小腿绕哪根骨骼轴弯曲。
        // 正常运行时 debugForcedKneeAngleDeg 必须保持 -1。
        if (debugForcedKneeAngleDeg >= 0f)
            kneeAngle = Mathf.Clamp(debugForcedKneeAngleDeg, kneeMinAngleDeg, kneeMaxAngleDeg);

        // 第二自由度：左右摆动。
        // 你之前的小腿不能左右摆，是因为旧代码只使用 EulerZ 生成一个 hinge 角。
        // 这里额外从 X/Y/Z 中选择一轴作为左右摆动输入，再绕 calfBoneLateralAxis 输出。
        float rawKneeLateralAngle = GetEulerByAxis(normKneeEuler, calfLateralEulerAxis);

        if (invertCalfLateralAngle)
            rawKneeLateralAngle = -rawKneeLateralAngle;

        rawKneeLateralAngle *= calfLateralGain;
        LastRawKneeLateralAngleDeg = rawKneeLateralAngle;

        float kneeLateralAngle = enableCalfLateralSwing ? rawKneeLateralAngle : 0f;

        if (Mathf.Abs(kneeLateralAngle) < calfLateralDeadZoneDeg)
            kneeLateralAngle = 0f;

        kneeLateralAngle = Mathf.Clamp(kneeLateralAngle, -calfMaxLateralAngleDeg, calfMaxLateralAngleDeg);

        // 调试模式：强制左右摆动角，便于确认 calfBoneLateralAxis 是否正确。
        // 正常运行时 debugForcedKneeLateralAngleDeg 必须保持 -9999。
        if (debugForcedKneeLateralAngleDeg > -9990f)
            kneeLateralAngle = Mathf.Clamp(debugForcedKneeLateralAngleDeg, -calfMaxLateralAngleDeg, calfMaxLateralAngleDeg);

        CurrentKneeRelativeAngleDeg = kneeAngle;
        CurrentKneeLateralAngleDeg = kneeLateralAngle;
        LastClampedKneeAngleDeg = kneeAngle;
        LastClampedKneeLateralAngleDeg = kneeLateralAngle;

        Quaternion kneeBendLocal =
            Quaternion.AngleAxis(kneeAngle, SafeAxis(calfBoneHingeAxis, Vector3.forward));

        Quaternion kneeLateralLocal =
            Quaternion.AngleAxis(kneeLateralAngle, SafeAxis(calfBoneLateralAxis, Vector3.up));

        Quaternion calfTarget;
        if (applyCalfLateralBeforeBend)
            calfTarget = NormalizeSafe(CalfBoneRestLocal * kneeLateralLocal * kneeBendLocal);
        else
            calfTarget = NormalizeSafe(CalfBoneRestLocal * kneeBendLocal * kneeLateralLocal);

        LastCalfTargetLocal = calfTarget;

        // 7) 调试日志。
        LogStaticCheck(thighNow, mappedThighDelta, thighTarget);
        LogKneeCheck(rawKneeDelta, rawKneeSignedAngle, kneeAngle, rawKneeLateralAngle, kneeLateralAngle, calfTarget);

        // 8) 最终骨骼输出。
        if (ForceThighRestForDebug)
        {
            ApplyThighRotation(thigh, ThighBoneRestLocal);
        }
        else
        {
            ApplyThighRotation(thigh, thighTarget);
        }

        if (DriveCalf)
        {
            ApplyCalfRotation(calf, calfTarget);
        }
        else
        {
            calf.localRotation = CalfBoneRestLocal;
            smoothedCalfLocal = CalfBoneRestLocal;
            hasSmoothedCalfLocal = true;
        }

        LastAppliedThighLocal = thigh.localRotation;
        LastAppliedCalfLocal = calf.localRotation;

        LastError = "";
        return true;
    }

    public bool ApplyAvailable(
        Quaternion[] transformedQuaternions,
        Transform thigh,
        Transform calf,
        bool thighInputAvailable,
        bool calfInputAvailable)
    {
        if (!thighInputAvailable)
            return false;

        bool configuredDriveCalf = DriveCalf;
        preserveInactiveCalfPose = configuredDriveCalf && !calfInputAvailable;
        try
        {
            DriveCalf = configuredDriveCalf && calfInputAvailable;
            return Apply(transformedQuaternions, thigh, calf);
        }
        finally
        {
            DriveCalf = configuredDriveCalf;
            preserveInactiveCalfPose = false;
        }
    }

    public void TryLogDebug(Quaternion[] transformedQuaternions, Transform thigh, Transform calf)
    {
        if (!IsCalibrated) return;
        if (Time.time - lastDebugLogTime < DebugLogInterval) return;
        lastDebugLogTime = Time.time;

        if (transformedQuaternions == null || transformedQuaternions.Length <= LeftCalfIndex) return;
        if (thigh == null || calf == null) return;

        Quaternion thighNow = NormalizeSafe(transformedQuaternions[LeftThighIndex]);
        Quaternion calfNow = NormalizeSafe(transformedQuaternions[LeftCalfIndex]);

        Quaternion kneeRelativeNow = NormalizeSafe(Quaternion.Inverse(thighNow) * calfNow);
        float relAngle = Quaternion.Angle(kneeSensorRelativeReference, kneeRelativeNow);

        Debug.Log(
            "[LeftLegPoseDriver][Debug]\n" +
            $"  传感器[5] 左大腿 Euler = {thighNow.eulerAngles}\n" +
            $"  传感器[6] 左小腿 Euler = {calfNow.eulerAngles}\n" +
            $"  左大腿参考 Euler = {ThighSensorReference.eulerAngles}\n" +
            $"  左小腿参考 Euler = {CalfSensorReference.eulerAngles}\n" +
            $"  当前左大腿 refToNowAngle = {LastRefToNowAngleDeg:F2}°\n" +
            $"  当前左大腿 rawDeltaAngle = {LastRawThighDeltaAngleDeg:F2}°\n" +
            $"  膝关节参考相对 Euler = {kneeSensorRelativeReference.eulerAngles}\n" +
            $"  当前膝关节相对 Euler = {kneeRelativeNow.eulerAngles}\n" +
            $"  当前膝关节整体相对角度 = {relAngle:F2}°，hinge弯曲角 = {CurrentKneeRelativeAngleDeg:F2}°\n" +
            $"  rawKneeSignedAngle = {LastRawKneeSignedAngleDeg:F2}°，clampedKneeAngle = {LastClampedKneeAngleDeg:F2}°\n" +
            $"  rawKneeLateralAngle = {LastRawKneeLateralAngleDeg:F2}°，clampedLateralAngle = {LastClampedKneeLateralAngleDeg:F2}°\n" +
            $"  LateralEulerAxis = {AxisIndexToName(calfLateralEulerAxis)}，CalfBoneLateralAxis = {calfBoneLateralAxis}\n" +
            $"  EnableCalfLateralSwing = {enableCalfLateralSwing}，InvertCalfLateralAngle = {invertCalfLateralAngle}\n" +
            $"  左大腿骨骼轴补偿 Euler = {thighBoneAxisOffsetEuler}\n" +
            $"  左大腿单轴反转模式 = {thighAxisInvertMode}\n" +
            $"  LimitThighTwist = {limitThighTwist}，ThighTwistAxis = {thighTwistAxisMode}，MaxThighTwistDeg = {maxThighTwistDeg:F1}°\n" +
            $"  ThighApplyOrder = {thighApplyOrder}\n" +
            $"  左小腿骨骼轴补偿 Euler = {calfBoneAxisOffsetEuler}\n" +
            $"  小腿传感器弯曲轴 = {calfSensorBendAxis}\n" +
            $"  小腿骨骼 hinge 轴 = {calfBoneHingeAxis}\n" +
            $"  InvertKneeAngle = {invertKneeAngle}\n" +
            $"  DebugForcedKneeAngleDeg = {debugForcedKneeAngleDeg:F1}，-1 表示关闭\n" +
            $"  DriveCalf = {DriveCalf}\n" +
            $"  ForceThighRestForDebug = {ForceThighRestForDebug}\n" +
            $"  ThighTarget localEuler = {LastThighTargetLocal.eulerAngles}\n" +
            $"  CalfTarget localEuler = {LastCalfTargetLocal.eulerAngles}\n" +
            $"  平滑缓存左大腿 localEuler = {smoothedThighLocal.eulerAngles}\n" +
            $"  平滑缓存左小腿 localEuler = {smoothedCalfLocal.eulerAngles}\n" +
            $"  应用后左大腿 localEuler = {thigh.localRotation.eulerAngles}\n" +
            $"  应用后左小腿 localEuler = {calf.localRotation.eulerAngles}");
    }

    private void LogStaticCheck(Quaternion thighNow, Quaternion rawThighDelta, Quaternion thighTarget)
    {
        if (!StaticCheckLogEnabled) return;
        if (Time.time - lastStaticCheckLogTime < DebugLogInterval) return;
        lastStaticCheckLogTime = Time.time;

        Debug.Log(
            "[LeftThighStaticCheck] " +
            $"refToNowAngle={LastRefToNowAngleDeg:F2}, " +
            $"rawDeltaAngle={LastRawThighDeltaAngleDeg:F2}, " +
            $"ThighRefEuler={ThighSensorReference.eulerAngles}, " +
            $"ThighNowEuler={thighNow.eulerAngles}, " +
            $"RestEuler={ThighBoneRestLocal.eulerAngles}, " +
            $"TargetEuler={thighTarget.eulerAngles}, " +
            $"AxisOffset={thighBoneAxisOffsetEuler}, " +
            $"AxisInvert={thighAxisInvertMode}, " +
            $"LimitTwist={limitThighTwist}, " +
            $"TwistAxis={thighTwistAxisMode}, " +
            $"MaxTwist={maxThighTwistDeg:F1}, " +
            $"ApplyOrder={thighApplyOrder}, " +
            $"ForceRest={ForceThighRestForDebug}");
    }

    private void LogKneeCheck(Quaternion rawKneeDelta, float rawKneeSignedAngle, float kneeAngle, float rawKneeLateralAngle, float kneeLateralAngle, Quaternion calfTarget)
    {
        if (!KneeDebugLogEnabled) return;
        if (Time.time - lastKneeDebugLogTime < DebugLogInterval) return;
        lastKneeDebugLogTime = Time.time;

        Debug.Log(
            "[LeftKneeHingeCheck] " +
            $"rawKneeDeltaEuler={rawKneeDelta.eulerAngles}, " +
            $"normEuler=({NormalizeAngle(rawKneeDelta.eulerAngles.x):F2}, {NormalizeAngle(rawKneeDelta.eulerAngles.y):F2}, {NormalizeAngle(rawKneeDelta.eulerAngles.z):F2}), " +
            $"bendSource=EulerZ, " +
            $"rawBend={rawKneeSignedAngle:F2}, " +
            $"clampedBend={kneeAngle:F2}, " +
            $"lateralSource=Euler{AxisIndexToName(calfLateralEulerAxis)}, " +
            $"rawLateral={rawKneeLateralAngle:F2}, " +
            $"clampedLateral={kneeLateralAngle:F2}, " +
            $"sensorAxis={calfSensorBendAxis}, " +
            $"boneHingeAxis={calfBoneHingeAxis}, " +
            $"boneLateralAxis={calfBoneLateralAxis}, " +
            $"invertBend={invertKneeAngle}, " +
            $"invertLateral={invertCalfLateralAngle}, " +
            $"enableLateral={enableCalfLateralSwing}, " +
            $"debugForcedBend={debugForcedKneeAngleDeg:F1}, " +
            $"debugForcedLateral={debugForcedKneeLateralAngleDeg:F1}, " +
            $"calfTargetEuler={calfTarget.eulerAngles}, " +
            $"DriveCalf={DriveCalf}");
    }

    private void ApplyThighRotation(Transform thigh, Quaternion target)
    {
        if (thigh == null) return;

        target = NormalizeSafe(target);

        if (!hasSmoothedThighLocal)
        {
            smoothedThighLocal = target;
            hasSmoothedThighLocal = true;
        }

        smoothedThighLocal = SmoothCachedLocalRotation(
            smoothedThighLocal,
            target,
            minBoneAngleThresholdDeg);

        thigh.localRotation = smoothedThighLocal;
    }

    private void ApplyCalfRotation(Transform calf, Quaternion target)
    {
        if (calf == null) return;

        target = NormalizeSafe(target);

        if (!hasSmoothedCalfLocal)
        {
            smoothedCalfLocal = target;
            hasSmoothedCalfLocal = true;
        }

        smoothedCalfLocal = SmoothCachedLocalRotation(
            smoothedCalfLocal,
            target,
            minBoneAngleThresholdDeg);

        calf.localRotation = smoothedCalfLocal;
    }

    private Quaternion SmoothCachedLocalRotation(
        Quaternion currentCached,
        Quaternion target,
        float minAngleDeg)
    {
        currentCached = NormalizeSafe(currentCached);
        target = ShortestArcTarget(currentCached, target);

        float angleDelta = Quaternion.Angle(currentCached, target);
        if (angleDelta < minAngleDeg)
            return currentCached;

        if (!SmoothingEnabled)
            return NormalizeSafe(target);

        float t = 1f - Mathf.Exp(-Mathf.Max(0.01f, SmoothingSpeed) * Time.deltaTime);
        return NormalizeSafe(Quaternion.Slerp(currentCached, target, t));
    }

    private static Quaternion ShortestArcTarget(Quaternion current, Quaternion target)
    {
        current = NormalizeSafe(current);
        target = NormalizeSafe(target);

        if (Quaternion.Dot(current, target) < 0f)
            return new Quaternion(-target.x, -target.y, -target.z, -target.w);

        return target;
    }

    private static Quaternion FilterAnomalousQuaternion(
        Quaternion current,
        Queue<Quaternion> history,
        int historySize,
        float thresholdDeg)
    {
        current = NormalizeSafe(current);

        if (history == null || history.Count == 0)
            return current;

        Quaternion avg = AverageQuaternions(history);
        avg = Hemispherize(current, avg);

        float angle = Quaternion.Angle(avg, current);
        if (angle <= thresholdDeg)
            return current;

        Quaternion[] arr = history.ToArray();
        Quaternion last = arr[arr.Length - 1];

        if (arr.Length >= 2)
        {
            Quaternion prev = arr[arr.Length - 2];
            prev = Hemispherize(last, prev);
            Quaternion extrapolated = Quaternion.Slerp(prev, last, 1.5f);
            return NormalizeSafe(Hemispherize(last, extrapolated));
        }

        return last;
    }

    private static Quaternion Hemispherize(Quaternion reference, Quaternion q)
    {
        reference = NormalizeSafe(reference);
        q = NormalizeSafe(q);

        if (Quaternion.Dot(reference, q) < 0f)
            return new Quaternion(-q.x, -q.y, -q.z, -q.w);

        return q;
    }

    private static void EnqueueHistory(Queue<Quaternion> history, Quaternion q, int maxCount)
    {
        if (history == null) return;

        history.Enqueue(NormalizeSafe(q));
        while (history.Count > maxCount)
            history.Dequeue();
    }

    private static Quaternion GetLast(Queue<Quaternion> history)
    {
        Quaternion last = Quaternion.identity;
        if (history == null) return last;

        foreach (var q in history)
            last = q;

        return NormalizeSafe(last);
    }

    private static Quaternion ApplyThighAxisInvert(Quaternion delta, LeftThighAxisInvertMode mode)
    {
        delta = NormalizeSafe(delta);

        if (mode == LeftThighAxisInvertMode.None)
            return delta;

        Vector3 e = delta.eulerAngles;
        e.x = NormalizeAngle(e.x);
        e.y = NormalizeAngle(e.y);
        e.z = NormalizeAngle(e.z);

        switch (mode)
        {
            case LeftThighAxisInvertMode.InvertX:
                e.x = -e.x;
                break;

            case LeftThighAxisInvertMode.InvertY:
                e.y = -e.y;
                break;

            case LeftThighAxisInvertMode.InvertZ:
                e.z = -e.z;
                break;
        }

        return NormalizeSafe(Quaternion.Euler(e.x, e.y, e.z));
    }

    private static Quaternion ApplyDeadZoneAndGain(Quaternion delta, float deadZoneDeg, float gain)
    {
        delta = NormalizeSafe(delta);
        delta.ToAngleAxis(out float angleDeg, out Vector3 axis);

        if (float.IsNaN(axis.x) || float.IsNaN(axis.y) || float.IsNaN(axis.z) || axis.sqrMagnitude < 0.0001f)
        {
            axis = Vector3.right;
            angleDeg = 0f;
        }

        if (angleDeg > 180f)
            angleDeg -= 360f;

        if (Mathf.Abs(angleDeg) < deadZoneDeg)
            return Quaternion.identity;

        float finalAngle = angleDeg * gain;
        return NormalizeSafe(Quaternion.AngleAxis(finalAngle, axis.normalized));
    }

    private static Vector3 ResolveSegmentDirectionWorld(
        Transform start,
        Transform end,
        Vector3 fallback)
    {
        if (start != null && end != null)
        {
            Vector3 direction = end.position - start.position;
            if (IsVectorFinite(direction) && direction.sqrMagnitude > 0.000001f)
                return direction.normalized;
        }
        return SafeAxis(fallback, Vector3.down);
    }

    private static Vector3 ResolveCalfDirectionWorld(Transform calf, Vector3 fallback)
    {
        if (calf != null)
        {
            for (int i = 0; i < calf.childCount; i++)
            {
                Transform child = calf.GetChild(i);
                if (child == null) continue;
                Vector3 direction = child.position - calf.position;
                if (IsVectorFinite(direction) && direction.sqrMagnitude > 0.000001f)
                    return direction.normalized;
            }
        }
        return SafeAxis(fallback, Vector3.down);
    }

    private static Vector3 ResolveCalibratedThighLongAxisLocal(
        Transform thigh,
        Transform calf,
        Quaternion thighRestWorld)
    {
        Vector3 longAxisWorld = Vector3.zero;

        if (thigh != null && calf != null)
            longAxisWorld = calf.position - thigh.position;

        if (!IsVectorFinite(longAxisWorld) || longAxisWorld.sqrMagnitude < 0.000001f)
            longAxisWorld = thighRestWorld * Vector3.down;

        longAxisWorld.Normalize();
        Vector3 longAxisLocal = Quaternion.Inverse(thighRestWorld) * longAxisWorld;
        if (!IsVectorFinite(longAxisLocal) || longAxisLocal.sqrMagnitude < 0.000001f)
            return Vector3.up;

        return longAxisLocal.normalized;
    }

    private static Vector3 GetTwistAxis(LeftThighTwistAxisMode mode)
    {
        switch (mode)
        {
            case LeftThighTwistAxisMode.LocalX:
                return Vector3.right;
            case LeftThighTwistAxisMode.LocalZ:
                return Vector3.forward;
            default:
                return Vector3.up;
        }
    }

    private static Quaternion LimitTwistAroundAxis(Quaternion q, Vector3 twistAxis, float maxAbsTwistDeg)
    {
        q = NormalizeSafe(q);
        twistAxis = SafeAxis(twistAxis, Vector3.up);

        Vector3 r = new Vector3(q.x, q.y, q.z);
        Vector3 projected = Vector3.Project(r, twistAxis);

        Quaternion twist = NormalizeSafe(new Quaternion(projected.x, projected.y, projected.z, q.w));
        if (Quaternion.Dot(Quaternion.identity, twist) < 0f)
            twist = new Quaternion(-twist.x, -twist.y, -twist.z, -twist.w);

        Quaternion swing = NormalizeSafe(q * Quaternion.Inverse(twist));

        twist.ToAngleAxis(out float twistAngleDeg, out Vector3 actualTwistAxis);
        if (float.IsNaN(actualTwistAxis.x) || float.IsNaN(actualTwistAxis.y) || float.IsNaN(actualTwistAxis.z) || actualTwistAxis.sqrMagnitude < 0.0001f)
            return swing;

        if (twistAngleDeg > 180f)
            twistAngleDeg -= 360f;

        actualTwistAxis.Normalize();
        float sign = Vector3.Dot(actualTwistAxis, twistAxis) >= 0f ? 1f : -1f;
        float signedTwist = twistAngleDeg * sign;
        float limitedTwist = Mathf.Clamp(signedTwist, -maxAbsTwistDeg, maxAbsTwistDeg);

        Quaternion limitedTwistQ = Quaternion.AngleAxis(limitedTwist, twistAxis);
        return NormalizeSafe(swing * limitedTwistQ);
    }

    /// <summary>
    /// 从一个 Quaternion 中提取围绕指定轴的有符号旋转角。
    /// 这里用于把完整的 kneeRelative delta 压成单一膝盖弯曲角。
    /// </summary>
    private static float ExtractSignedAngleAroundAxis(Quaternion q, Vector3 axis)
    {
        q = NormalizeSafe(q);
        axis = SafeAxis(axis, Vector3.right);

        q.ToAngleAxis(out float angleDeg, out Vector3 rotAxis);

        if (float.IsNaN(rotAxis.x) || float.IsNaN(rotAxis.y) || float.IsNaN(rotAxis.z) || rotAxis.sqrMagnitude < 0.0001f)
            return 0f;

        if (angleDeg > 180f)
            angleDeg -= 360f;

        rotAxis.Normalize();

        float dot = Vector3.Dot(rotAxis, axis);
        if (Mathf.Abs(dot) < 0.0001f)
            return 0f;

        return angleDeg * dot;
    }

    private static float GetEulerByAxis(Vector3 normalizedEuler, int axisIndex)
    {
        switch (Mathf.Clamp(axisIndex, 0, 2))
        {
            case 0: return normalizedEuler.x;
            case 1: return normalizedEuler.y;
            default: return normalizedEuler.z;
        }
    }

    private static string AxisIndexToName(int axisIndex)
    {
        switch (Mathf.Clamp(axisIndex, 0, 2))
        {
            case 0: return "X";
            case 1: return "Y";
            default: return "Z";
        }
    }

    private static Quaternion AverageQuaternions(IEnumerable<Quaternion> samples)
    {
        bool hasAny = false;
        Quaternion avg = Quaternion.identity;

        foreach (Quaternion qRaw in samples)
        {
            Quaternion q = NormalizeSafe(qRaw);

            if (!hasAny)
            {
                avg = q;
                hasAny = true;
            }
            else
            {
                q = Hemispherize(avg, q);
                avg = Quaternion.Slerp(avg, q, 0.5f);
            }
        }

        return hasAny ? NormalizeSafe(avg) : Quaternion.identity;
    }

    private static Quaternion NormalizeSafe(Quaternion q)
    {
        float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (mag < 0.000001f) return Quaternion.identity;
        return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
    }

    private static Vector3 SafeAxis(Vector3 axis, Vector3 fallback)
    {
        if (axis.sqrMagnitude < 0.0001f)
            axis = fallback;

        axis.Normalize();
        return axis;
    }

    private static bool IsQuaternionFinite(Quaternion q)
    {
        return IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w);
    }

    private static bool IsFinite(float v)
    {
        return !float.IsNaN(v) && !float.IsInfinity(v);
    }

    private static bool IsVectorFinite(Vector3 v)
    {
        return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
    }
}

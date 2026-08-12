using UnityEngine;

/// <summary>
/// V77.24 连续大臂骨段方向驱动器。
///
/// 与 V77.12 的区别：
/// V77.12 把传感器完整世界四元数直接映射到大臂骨骼，传感器自身滚转、安装坐标框架
/// 和左右镜像差异会一起进入肩关节，因此出现前伸串到侧方、自然下垂交叉等问题。
///
/// V77.13 不识别任何动作，也不吸附预设姿态。标定时根据“肩→肘”真实骨段方向，
/// 为左右大臂分别求出各自传感器中的精确骨段长轴；运行时只读取该长轴的连续空间方向，
/// 再用 Swing 旋转驱动 Unity 大臂。这样任意方向、任意幅度和复合轨迹都由实时传感器数据产生，
/// 同时过滤不应改变大臂指向的传感器轴向滚转。
///
/// V8.6保留左臂连续骨段方向主路径；03号恢复为标定自动求真实“肩→肘”传感器局部轴，
/// 运行时只驱动该轴的空间方向并过滤绕大臂长轴的twist；02/04都不参与骨骼驱动，
/// 左右小臂保持标定局部姿态，并通过骨骼父子层级随各自大臂整体运动。
/// V77.24不复制左臂数据，也不使用动作锚点。右上臂继续读取0x03实时数据，
/// 但撤销V77.23的固定+Y骨段轴和离线世界方向基，恢复为与左臂相同原理的
/// “标定时自动求传感器局部肩→肘长轴，运行时连续跟踪该长轴”。
/// 同时在输入层修复手臂传感器被历史平均误判为异常、导致0x03姿态冻结的问题。
/// </summary>
public sealed class ArmPoseDriver
{
    public const string BuildVersion = "V8.14-RUNTIME-GATE-TELEMETRY-20260812-A";

    // 以下枚举和公开属性保留，确保旧场景及 MotionCaptureController 可以无缝编译。
    public enum RightArmCorrectionMode
    {
        None = 0,
        MirrorAvatarX = 1,
        MirrorAvatarY = 2,
        MirrorAvatarZ = 3,
        FlipX = 4,
        FlipY = 5,
        FlipZ = 6
    }

    public enum RightForeArmCorrectionMode
    {
        None = 0,
        MirrorAvatarX = 1,
        MirrorAvatarY = 2,
        MirrorAvatarZ = 3,
        FlipX = 4,
        FlipY = 5,
        FlipZ = 6
    }

    public enum RightForeArmDriveMode
    {
        AbsoluteWorld = 0,
        RelativeQuaternion = 1,
        ElbowHinge = 2,
        RelativeDeltaPreRest = 3,
        RelativeDeltaPostRest = 4
    }

    public enum RightForeArmAvatarAxisSpace
    {
        ForeArmLocalPostRest = 0,
        UpperArmLocalPreRest = 1
    }

    public enum AxisMode
    {
        PositiveX = 0,
        NegativeX = 1,
        PositiveY = 2,
        NegativeY = 3,
        PositiveZ = 4,
        NegativeZ = 5
    }

    public enum SegmentAxisMode
    {
        AutoFromCalibration = 0,
        PositiveX = 1,
        NegativeX = 2,
        PositiveY = 3,
        NegativeY = 4,
        PositiveZ = 5,
        NegativeZ = 6
    }

    public const int LeftArmIndex = (int)BoneIndex.LeftArm;
    public const int LeftForeArmIndex = (int)BoneIndex.LeftForeArm;
    public const int RightArmIndex = (int)BoneIndex.RightArm;
    public const int RightForeArmIndex = (int)BoneIndex.RightForeArm;

    public bool DriveLeftArm { get; set; } = true;
    public bool DriveLeftForeArm { get; set; } = true;
    public bool DriveRightArm { get; set; } = true;
    public bool DriveRightForeArm { get; set; } = true;

    public bool SmoothingEnabled { get; set; } = true;
    public float SmoothingSpeed { get; set; } = 20f;
    public float MinAngleThresholdDeg { get; set; } = 0.2f;

    public Vector3 LeftArmBoneAxisOffsetEuler { get; set; } = Vector3.zero;
    public Vector3 LeftForeArmBoneAxisOffsetEuler { get; set; } = Vector3.zero;
    public Vector3 RightArmBoneAxisOffsetEuler { get; set; } = Vector3.zero;
    public Vector3 RightForeArmBoneAxisOffsetEuler { get; set; } = Vector3.zero;

    /// <summary>
    /// 左大臂前伸时向身体内侧串入的连续坐标耦合补偿角。
    /// 只按实时方向中的“向前分量”线性增加少量向身体外侧分量；
    /// 不是动作识别，也不会把姿态吸附到前伸/平举/上举。
    /// </summary>
    public float LeftArmForwardOutwardCompensationDeg { get; set; } = 0f;

    /// <summary>
    /// 旧场景兼容字段。V77.24右大臂不使用单角度delta校正。
    /// </summary>
    public float RightArmDeltaFrameCorrectionDeg { get; set; } = 0f;

    /// <summary>
    /// 旧版本右上臂固定局部骨段轴兼容字段。
    /// V8.6主路径使用RightArmSensorAxisMode=AutoFromCalibration，标定时自动反算真实肩→肘局部轴，
    /// 因此不读取该固定Vector3.back；字段仅保留给旧场景与回归调试。
    /// </summary>
    public Vector3 RightArmFixedSegmentAxisLocal { get; set; } = Vector3.back;

    /// <summary>
    /// V8.10主路径。先在03传感器自身局部坐标中计算 inverse(calibration) * current，
    /// 再通过连续三轴耦合矩阵转换为Avatar肩关节局部Swing增量。
    /// 全程不做动作分类、最近姿态判断或标准动作吸附。
    /// </summary>
    public bool UseRightArmCalibratedDeltaSwing { get; set; } = true;

    /// <summary>
    /// V8.5兼容开关。V8.6默认关闭：完整四元数会把绕大臂自身长轴的twist写入肩关节，
    /// 导致保持同一前伸姿态时Avatar仍继续转向脸前。关闭后使用标定自动求得的肩→肘轴Swing路径。
    /// </summary>
    public bool UseRightArmFullQuaternionDelta { get; set; } = false;

    /// <summary>
    /// V8.8旧兼容字段。V8.10主路径明确禁用固定参考姿态，避免姿态吸附。
    /// 字段仅保留旧场景序列化兼容，运行时默认false且不参与V8.10主路径。
    /// </summary>
    public bool UseRightArmFixedReferenceProfile { get; set; } = false;
    public float RightArmProfileInterpolationPower { get; set; } = 4f;
    public float RightArmProfileExactMatchAngleDeg { get; set; } = 5f;
    public float RightArmProfileFallbackAngleDeg { get; set; } = 95f;

    /// <summary>
    /// V8.7旧兼容路径。V8.8默认关闭，不再显示动作学习提示。
    /// 用前伸/平举两个独立旋转轴求完整的传感器→Avatar旋转坐标框架；仅为旧版本回归保留。
    /// </summary>
    public bool EnableRightArmFourPoseAxisLearning { get; set; } = false;
    public float RightArmPoseInitialPrepareSeconds { get; set; } = 2f;
    public float RightArmPoseTransitionSeconds { get; set; } = 1f;
    public float RightArmPoseCaptureSeconds { get; set; } = 2f;
    public bool IsRightArmPoseLearningActive => rightArmPoseLearningActive;
    public bool IsRightArmPoseLearningReady => rightArmPoseLearningReady;
    public string RightArmPoseLearningStatus => rightArmPoseLearningStatus;

    public SegmentAxisMode LeftArmSensorAxisMode { get; set; } = SegmentAxisMode.AutoFromCalibration;
    public SegmentAxisMode LeftForeArmSensorAxisMode { get; set; } = SegmentAxisMode.AutoFromCalibration;
    public SegmentAxisMode RightArmSensorAxisMode { get; set; } = SegmentAxisMode.AutoFromCalibration;
    public SegmentAxisMode RightForeArmSensorAxisMode { get; set; } = SegmentAxisMode.AutoFromCalibration;

    public Vector3 RightForeArmDeltaAxisOffsetEuler { get; set; } = Vector3.zero;
    public RightArmCorrectionMode RightArmCorrection { get; set; } = RightArmCorrectionMode.None;
    public RightForeArmCorrectionMode RightForeArmCorrection { get; set; } = RightForeArmCorrectionMode.None;
    public bool UseRightForeArmRelativeToRightArm { get; set; } = false;
    public RightForeArmDriveMode RightForeArmMode { get; set; } = RightForeArmDriveMode.AbsoluteWorld;
    public AxisMode RightForeArmSensorBendAxis { get; set; } = AxisMode.PositiveX;
    public AxisMode RightForeArmAvatarBendAxis { get; set; } = AxisMode.PositiveZ;
    public RightForeArmAvatarAxisSpace RightForeArmAvatarAxisSpaceMode { get; set; } = RightForeArmAvatarAxisSpace.ForeArmLocalPostRest;
    public float RightForeArmBendSign { get; set; } = 1f;
    public float RightForeArmBendScale { get; set; } = 1f;
    public float RightForeArmBendOffsetDeg { get; set; } = 0f;
    public bool ClampRightForeArmBend { get; set; } = true;
    public float RightForeArmMinBendDeg { get; set; } = -10f;
    public float RightForeArmMaxBendDeg { get; set; } = 150f;
    public bool RightForeArmDebugLog { get; set; } = false;

    public bool UseElbowStraightBlend { get; set; } = false;
    public float ElbowStraightFullIncludedAngleDeg { get; set; } = 165f;
    public float ElbowStraightReleaseIncludedAngleDeg { get; set; } = 150f;
    public bool UseForeArmStraightLock { get; set; } = false;
    public float ForeArmStraightLockAngleDeg { get; set; } = 28f;
    public float ForeArmStraightReleaseAngleDeg { get; set; } = 65f;
    public bool UseIndependentHierarchy { get; set; } = false;
    public bool SuppressForeArmAxialTwist { get; set; } = false;

    /// <summary>旧V3兼容字段；V5固定为false，02不参与Avatar左小臂驱动。</summary>
    public bool DriveLeftForeArmRelativeToUpperArm { get; set; } = false;
    /// <summary>旧V3兼容状态；V5双小臂锁定时固定为false且不影响左右大臂。</summary>
    public bool LeftForeArmInputAvailable { get; set; } = false;
    /// <summary>过滤左小臂沿自身长轴的滚转，只保留肘部相对摆动。</summary>
    public bool SuppressLeftForeArmAxialTwist { get; set; } = true;

    /// <summary>
    /// 旧场景兼容字段。V3为true时，右小臂仍锁定；左小臂可由上面的相对路径单独解锁。
    /// </summary>
    public bool LockForeArmsToCalibrationRest { get; set; } = true;

    public bool IsCalibrated { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public float CurrentLeftElbowFlexionAngleDeg { get; private set; }
    public float CurrentRightElbowFlexionAngleDeg { get; private set; }
    public float CurrentLeftElbowIncludedAngleDeg { get; private set; } = 180f;
    public float CurrentRightElbowIncludedAngleDeg { get; private set; } = 180f;
    public bool CurrentLeftElbowNearStraight => UseElbowStraightBlend &&
        CurrentLeftElbowIncludedAngleDeg >= ElbowStraightReleaseIncludedAngleDeg;
    public bool CurrentRightElbowNearStraight => UseElbowStraightBlend &&
        CurrentRightElbowIncludedAngleDeg >= ElbowStraightReleaseIncludedAngleDeg;

    // 0 左大臂、1 左小臂、2 右大臂、3 右小臂。
    private readonly Quaternion[] sensorReferences = new Quaternion[4];
    private readonly Quaternion[] restWorlds = new Quaternion[4];
    private readonly Quaternion[] sensorToBoneWorldOffsets = new Quaternion[4];
    private readonly Vector3[] restSegmentDirectionsWorld = new Vector3[4];
    private readonly Vector3[] sensorSegmentDirectionsLocal = new Vector3[4];
    private readonly Quaternion[] calibratedForeArmLocals = new Quaternion[2];
    private readonly Quaternion[] lastAppliedLocals = new Quaternion[4];
    private readonly bool[] hasLastApplied = new bool[4];

    // 旧V3左小臂相对映射缓存。V5主路径锁定双小臂，不调用该路径。
    private Quaternion leftForeArmRelativeReference = Quaternion.identity;
    private Quaternion leftUpperSensorToBoneBasis = Quaternion.identity;
    private Vector3 leftForeArmRestAxisUpperLocal = Vector3.down;

    // V77.23：由V77.22四组同步对称动作（前举/上举/平举/下垂）的0x03实时姿态
    // 离线求得的右上臂世界方向坐标基。它只是一套固定连续坐标变换，不识别动作。
    // 四元数与其相反数等价；这里使用w为正的形式。
    private static readonly Quaternion RightArmDirectionBasisLocal =
        new Quaternion(0.12755108f, -0.73933533f, -0.61476962f, 0.24325357f).normalized;

    // 每次标定只负责把固定骨段轴的当前世界方向对齐到模型肩→肘方向。
    // 因此标定零位严格保持，不会因改变右侧轴符号而在标定完成后跳变。
    // V8.3：01使用局部+Z；03使用局部-Z；两侧分别计算会话对齐。
    private Quaternion leftArmSessionAlignmentWorld = Quaternion.identity;
    private Quaternion rightArmSessionAlignmentWorld = Quaternion.identity;

    // V8.7旧四姿态轴学习缓存，仅为回归兼容保留；V8.8默认不进入。
    private readonly Quaternion[] rightArmPoseReferences = new Quaternion[4];
    private readonly Vector4[] rightArmPoseSums = new Vector4[4];
    private readonly int[] rightArmPoseSampleCounts = new int[4];
    private bool rightArmPoseLearningActive;
    private bool rightArmPoseLearningReady;
    private int rightArmPoseLearningStage;
    private float rightArmPoseLearningStageStartTime = -1f;
    private Quaternion rightArmDeltaBasisLocal = Quaternion.identity;
    private string rightArmPoseLearningStatus = string.Empty;

    private static readonly string[] RightArmPoseNames =
    {
        "右臂前伸", "右臂平举", "右臂上举", "右臂自然下垂"
    };

    // V8.10：由本轮03单传感器实测数据离线求得的四个“传感器局部相对旋转向量”。
    // 它们只用于建立连续坐标基，不参与运行时动作识别、阈值分类或姿态吸附。
    // 顺序：前伸、平举、上举、自然下垂；单位为弧度。
    private static readonly Vector3[] RightArmMeasuredLocalDeltaRotationVectors =
    {
        new Vector3( 0.10046171f, -2.77895579f, -0.35341774f),
        new Vector3( 0.18244164f, -0.81359229f, -0.89765835f),
        new Vector3(-0.54906902f, -2.16191416f, -1.13177081f),
        new Vector3( 0.34807837f, -0.06117195f,  0.24115003f)
    };

    private Matrix4x4 rightArmLocalDeltaToAvatarSwing = Matrix4x4.identity;
    private bool rightArmLocalDeltaMapReady;

    public ArmPoseDriver()
    {
        Debug.LogWarning(
            "[V8.14 ACTIVE][ArmPoseDriver.Constructor] Build=" + BuildVersion +
            "；01保持原连续方向驱动；03使用传感器局部Delta与连续三轴矩阵驱动；无动作识别、无参考姿态吸附、无顶部提示；V8.11中02/04解锁并参与全身驱动");
        Reset();
    }

    public void Reset()
    {
        IsCalibrated = false;
        LastError = string.Empty;
        CurrentLeftElbowFlexionAngleDeg = 0f;
        CurrentRightElbowFlexionAngleDeg = 0f;
        CurrentLeftElbowIncludedAngleDeg = 180f;
        CurrentRightElbowIncludedAngleDeg = 180f;

        for (int i = 0; i < 4; i++)
        {
            sensorReferences[i] = Quaternion.identity;
            restWorlds[i] = Quaternion.identity;
            sensorToBoneWorldOffsets[i] = Quaternion.identity;
            restSegmentDirectionsWorld[i] = Vector3.down;
            sensorSegmentDirectionsLocal[i] = Vector3.down;
            lastAppliedLocals[i] = Quaternion.identity;
            hasLastApplied[i] = false;
        }

        calibratedForeArmLocals[0] = Quaternion.identity;
        calibratedForeArmLocals[1] = Quaternion.identity;
        leftForeArmRelativeReference = Quaternion.identity;
        leftUpperSensorToBoneBasis = Quaternion.identity;
        leftForeArmRestAxisUpperLocal = Vector3.down;
        LeftForeArmInputAvailable = false;
        leftArmSessionAlignmentWorld = Quaternion.identity;
        rightArmSessionAlignmentWorld = Quaternion.identity;
        rightArmPoseLearningActive = false;
        rightArmPoseLearningReady = false;
        rightArmPoseLearningStage = 0;
        rightArmPoseLearningStageStartTime = -1f;
        rightArmDeltaBasisLocal = Quaternion.identity;
        rightArmPoseLearningStatus = string.Empty;
        rightArmLocalDeltaToAvatarSwing = Matrix4x4.identity;
        rightArmLocalDeltaMapReady = false;
        for (int i = 0; i < rightArmPoseReferences.Length; i++)
        {
            rightArmPoseReferences[i] = Quaternion.identity;
            rightArmPoseSums[i] = Vector4.zero;
            rightArmPoseSampleCounts[i] = 0;
        }
    }

    /// <summary>
    /// 仅清除人物输出平滑历史，不清除传感器参考、骨骼偏移或本次标定。
    /// 用于通信暂停后从人物当前安全姿势重新衔接，避免恢复时继续插值旧姿态。
    /// </summary>
    public void ResetSmoothingState()
    {
        for (int i = 0; i < lastAppliedLocals.Length; i++)
        {
            lastAppliedLocals[i] = Quaternion.identity;
            hasLastApplied[i] = false;
        }
    }

    public bool TryCalibrate(
        Quaternion[] sensorQuats,
        Transform leftArm,
        Transform leftForeArm,
        Transform rightArm,
        Transform rightForeArm,
        Transform avatarRoot,
        Quaternion leftArmRestLocal,
        Quaternion leftForeArmRestLocal,
        Quaternion rightArmRestLocal,
        Quaternion rightForeArmRestLocal,
        out string reason)
    {
        reason = string.Empty;
        LastError = string.Empty;

        int requiredSensorIndex = DriveRightArm ? RightArmIndex :
                                  (DriveLeftArm ? LeftArmIndex : -1);
        if (requiredSensorIndex < 0)
        {
            reason = "本轮没有启用任何大臂传感器";
            LastError = reason;
            return false;
        }

        if (sensorQuats == null || sensorQuats.Length <= requiredSensorIndex)
        {
            reason = "本轮启用的大臂传感器索引不完整";
            LastError = reason;
            return false;
        }

        if ((DriveLeftArm && (leftArm == null || leftForeArm == null)) ||
            (DriveRightArm && (rightArm == null || rightForeArm == null)))
        {
            reason = "本轮启用侧的上臂/前臂骨骼 Transform 未找到";
            LastError = reason;
            return false;
        }

        Quaternion leftArmSensor = DriveLeftArm
            ? NormalizeSafe(sensorQuats[LeftArmIndex])
            : Quaternion.identity;
        Quaternion rightArmSensor = DriveRightArm
            ? NormalizeSafe(ApplyRightArmCorrection(sensorQuats[RightArmIndex], RightArmCorrection))
            : Quaternion.identity;

        if ((DriveLeftArm && !IsQuaternionFinite(leftArmSensor)) ||
            (DriveRightArm && !IsQuaternionFinite(rightArmSensor)))
        {
            reason = "本轮启用的大臂标定四元数包含 NaN/Infinity 或零长度四元数";
            LastError = reason;
            return false;
        }

        Quaternion leftForeSensor = DriveLeftArm && sensorQuats.Length > LeftForeArmIndex &&
                                    IsQuaternionFinite(sensorQuats[LeftForeArmIndex])
            ? NormalizeSafe(sensorQuats[LeftForeArmIndex])
            : leftArmSensor;
        Quaternion rightForeSensor = DriveRightArm && sensorQuats.Length > RightForeArmIndex &&
                                     IsQuaternionFinite(sensorQuats[RightForeArmIndex])
            ? NormalizeSafe(ApplyRightForeArmCorrection(sensorQuats[RightForeArmIndex], RightForeArmCorrection))
            : rightArmSensor;

        sensorReferences[0] = leftArmSensor;
        sensorReferences[1] = leftForeSensor;
        sensorReferences[2] = rightArmSensor;
        sensorReferences[3] = rightForeSensor;

        restWorlds[0] = leftArm != null ? leftArm.rotation.normalized : Quaternion.identity;
        restWorlds[1] = leftForeArm != null ? leftForeArm.rotation.normalized : Quaternion.identity;
        restWorlds[2] = rightArm != null ? rightArm.rotation.normalized : Quaternion.identity;
        restWorlds[3] = rightForeArm != null ? rightForeArm.rotation.normalized : Quaternion.identity;

        // 保留完整四元数偏移，仅供未来解锁小臂时的兼容路径使用。
        for (int i = 0; i < 4; i++)
        {
            sensorToBoneWorldOffsets[i] =
                (Quaternion.Inverse(sensorReferences[i]) * restWorlds[i]).normalized;
        }

        // 01继续保持V8.2已验证的固定+Z。
        // V8.6：03不再假设固定-Z，也不再直接使用完整四元数。标定时利用当前Avatar肩→肘
        // 世界方向反算传感器局部真实骨段轴：localAxis = inverse(sensorReference) * restDirection。
        // 运行时只跟踪该轴的空间方向，因此安装偏差由本次标定自动吸收，绕大臂自身的twist被过滤。
        if (DriveLeftArm)
        {
            restSegmentDirectionsWorld[0] = GetDirectionBetween(
                leftArm, leftForeArm, restWorlds[0] * Vector3.down);
            sensorSegmentDirectionsLocal[0] = Vector3.forward;
            Vector3 predictedRest = SafeDirection(
                sensorReferences[0] * sensorSegmentDirectionsLocal[0], restSegmentDirectionsWorld[0]);
            leftArmSessionAlignmentWorld = StableFromToRotation(
                predictedRest, restSegmentDirectionsWorld[0], restWorlds[0]);
        }

        if (DriveRightArm)
        {
            restSegmentDirectionsWorld[2] = GetDirectionBetween(
                rightArm, rightForeArm, restWorlds[2] * Vector3.down);
            sensorSegmentDirectionsLocal[2] = ResolveSensorDirectionLocal(
                sensorReferences[2], restSegmentDirectionsWorld[2], RightArmSensorAxisMode);
            Vector3 predictedRest = SafeDirection(
                sensorReferences[2] * sensorSegmentDirectionsLocal[2], restSegmentDirectionsWorld[2]);
            rightArmSessionAlignmentWorld = StableFromToRotation(
                predictedRest, restSegmentDirectionsWorld[2], restWorlds[2]);

            if (UseRightArmCalibratedDeltaSwing &&
                !TryBuildRightArmLocalDeltaMap(avatarRoot, out string mapReason))
            {
                reason = "03连续坐标矩阵建立失败：" + mapReason;
                LastError = reason;
                return false;
            }
        }

        calibratedForeArmLocals[0] = leftForeArm != null
            ? leftForeArm.localRotation.normalized : leftForeArmRestLocal.normalized;
        calibratedForeArmLocals[1] = rightForeArm != null
            ? rightForeArm.localRotation.normalized : rightForeArmRestLocal.normalized;

        // 同侧相对参考：共同的肩部/身体转动会在Inverse(01)*02中抵消，
        // 因此不会把左大臂动作再次叠加到左小臂。
        leftForeArmRelativeReference = DriveLeftArm
            ? NormalizeSafe(Quaternion.Inverse(leftArmSensor) * leftForeSensor)
            : Quaternion.identity;

        // 将“01传感器局部坐标中的相对增量”转换到Avatar左大臂局部坐标。
        // C = BoneWorld^-1 * SensorWorld；运行时使用 C * delta * C^-1。
        leftUpperSensorToBoneBasis = DriveLeftArm
            ? NormalizeSafe(Quaternion.Inverse(restWorlds[0]) * leftArmSensor)
            : Quaternion.identity;

        if (DriveLeftArm && leftForeArm != null)
        {
            Vector3 leftForeArmAxisWorld = GetDirectionToPrimaryChild(
                leftForeArm, restWorlds[1] * Vector3.down);
            leftForeArmRestAxisUpperLocal = SafeDirection(
                Quaternion.Inverse(restWorlds[0]) * leftForeArmAxisWorld,
                Vector3.down);
        }

        lastAppliedLocals[0] = leftArm != null ? leftArm.localRotation.normalized : leftArmRestLocal.normalized;
        lastAppliedLocals[1] = leftForeArm != null ? leftForeArm.localRotation.normalized : leftForeArmRestLocal.normalized;
        lastAppliedLocals[2] = rightArm != null ? rightArm.localRotation.normalized : rightArmRestLocal.normalized;
        lastAppliedLocals[3] = rightForeArm != null ? rightForeArm.localRotation.normalized : rightForeArmRestLocal.normalized;
        for (int i = 0; i < 4; i++) hasLastApplied[i] = true;

        IsCalibrated = true;
        // V8.11继续关闭所有V8.7/V8.8姿态学习与固定参考吸附路径。
        rightArmPoseLearningActive = false;
        rightArmPoseLearningReady = false;
        rightArmPoseLearningStatus = string.Empty;

        Debug.LogWarning(
            "[V8.14 ACTIVE][ArmPoseDriver.Calibration] Build=" + BuildVersion +
            "；01使用局部+Z；03使用inverse(reference)*current传感器局部Delta，经连续三轴矩阵转换为肩关节Swing；无动作学习、无固定姿态吸附、无顶部提示；02/04使用各自标定数据驱动。\n" +
            $"  左大臂标定骨段方向(world)={restSegmentDirectionsWorld[0]}\n" +
            $"  左大臂传感器精确长轴(local)={sensorSegmentDirectionsLocal[0]}\n" +
            $"  右大臂标定骨段方向(world)={restSegmentDirectionsWorld[2]}\n" +
            $"  右大臂传感器精确长轴(local)={sensorSegmentDirectionsLocal[2]}\n" +
            $"  双小臂锁定localEuler：左={calibratedForeArmLocals[0].eulerAngles}，右={calibratedForeArmLocals[1].eulerAngles}");

        return true;
    }

    public bool Apply(
        Quaternion[] sensorQuats,
        Transform leftArm,
        Transform leftForeArm,
        Transform rightArm,
        Transform rightForeArm,
        Transform avatarRoot)
    {
        LastError = string.Empty;

        if (!IsCalibrated)
        {
            LastError = "ArmPoseDriver 尚未标定";
            return false;
        }

        int requiredSensorIndex = DriveRightArm ? RightArmIndex :
                                  (DriveLeftArm ? LeftArmIndex : -1);
        if (requiredSensorIndex < 0)
        {
            LastError = "没有启用任何大臂骨骼";
            return false;
        }
        if (sensorQuats == null || sensorQuats.Length <= requiredSensorIndex)
        {
            LastError = "本轮启用的大臂传感器索引不完整";
            return false;
        }

        if ((DriveLeftArm && (leftArm == null || leftForeArm == null)) ||
            (DriveRightArm && (rightArm == null || rightForeArm == null)))
        {
            LastError = "本轮启用侧的手臂骨骼引用不完整";
            return false;
        }

        Quaternion leftArmSensor = DriveLeftArm
            ? NormalizeSafe(sensorQuats[LeftArmIndex]) : Quaternion.identity;
        Quaternion rightArmSensor = DriveRightArm
            ? NormalizeSafe(ApplyRightArmCorrection(sensorQuats[RightArmIndex], RightArmCorrection))
            : Quaternion.identity;

        if ((DriveLeftArm && !IsQuaternionFinite(leftArmSensor)) ||
            (DriveRightArm && !IsQuaternionFinite(rightArmSensor)))
        {
            LastError = "本轮启用的大臂当前四元数非法";
            return false;
        }

        bool appliedAny = false;

        // 连续数据主路径：传感器当前姿态 * 标定得到的传感器局部骨段长轴
        // = 当前真实肩→肘空间方向。没有任何动作名称、阈值分类或固定目标姿态。
        if (DriveLeftArm)
        {
            Vector3 desiredDirection = GetCurrentSegmentDirection(leftArmSensor, 0);
            desiredDirection = ApplyForwardOutwardCrossAxisCompensation(
                desiredDirection, avatarRoot, true, LeftArmForwardOutwardCompensationDeg);
            Quaternion targetLocal = BuildDirectionMappedLocal(
                leftArm, 0, desiredDirection, LeftArmBoneAxisOffsetEuler);
            appliedAny |= ApplyLocalToBone(leftArm, 0, targetLocal);
        }

        if (DriveRightArm)
        {
            Quaternion targetLocal;
            if (UseRightArmCalibratedDeltaSwing)
            {
                // V8.10主路径：完全连续，无动作分类、无固定姿态锚点、无最近姿态吸附。
                // 在传感器局部空间计算Delta，再经三轴矩阵转换为Avatar局部Swing，修复世界坐标平面串轴。
                Vector3 desiredDirection = GetRightArmDirectionFromCalibratedDeltaSwing(rightArmSensor, avatarRoot);
                targetLocal = BuildDirectionMappedLocal(
                    rightArm, 2, desiredDirection, RightArmBoneAxisOffsetEuler);
            }
            else if (UseRightArmFullQuaternionDelta)
            {
                // 仅保留V8.5完整姿态回归开关。该路径会保留长轴twist，不作为默认方案。
                targetLocal = BuildContinuousWorldMappedLocal(
                    rightArm, rightArmSensor, sensorToBoneWorldOffsets[2], RightArmBoneAxisOffsetEuler);
            }
            else
            {
                // 最后回退：V8.6单骨段轴方向路径。仍然连续，但依赖标定时反算的传感器局部轴。
                Vector3 desiredDirection = GetCurrentSegmentDirection(rightArmSensor, 2);
                targetLocal = BuildDirectionMappedLocal(
                    rightArm, 2, desiredDirection, RightArmBoneAxisOffsetEuler);
            }
            appliedAny |= ApplyLocalToBone(rightArm, 2, targetLocal);
        }

        if (LockForeArmsToCalibrationRest)
        {
            if (DriveLeftForeArm && DriveLeftArm)
            {
                Quaternion leftForeTarget = calibratedForeArmLocals[0];
                if (DriveLeftForeArmRelativeToUpperArm && LeftForeArmInputAvailable)
                {
                    Quaternion leftForeSensor = NormalizeSafe(sensorQuats[LeftForeArmIndex]);
                    if (IsQuaternionFinite(leftForeSensor))
                    {
                        leftForeTarget = BuildLeftForeArmRelativeLocal(
                            leftArmSensor, leftForeSensor);
                    }
                }

                appliedAny |= ApplyLocalToBone(leftForeArm, 1, leftForeTarget);
            }

            if (DriveRightForeArm && DriveRightArm)
                appliedAny |= ApplyLocalToBone(rightForeArm, 3, calibratedForeArmLocals[1]);

            // V5左右小臂都保持标定localRotation，只随各自大臂父骨骼整体运动。
            UpdateElbowAngles(leftArm, leftForeArm, rightArm, rightForeArm);
            CurrentLeftElbowFlexionAngleDeg = 0f;
            CurrentLeftElbowIncludedAngleDeg = 180f;
            CurrentRightElbowFlexionAngleDeg = 0f;
            CurrentRightElbowIncludedAngleDeg = 180f;
        }
        else
        {
            // 兼容路径：若手动关闭小臂锁定，仍沿用 V77.12 的完整世界姿态映射。
            // 本轮测试不使用该路径。
            Quaternion leftForeSensor = NormalizeSafe(sensorQuats[LeftForeArmIndex]);
            Quaternion rightForeSensor = NormalizeSafe(
                ApplyRightForeArmCorrection(sensorQuats[RightForeArmIndex], RightForeArmCorrection));

            if (!IsQuaternionFinite(leftForeSensor) || !IsQuaternionFinite(rightForeSensor))
            {
                LastError = "左右小臂当前四元数包含 NaN/Infinity 或零长度四元数";
                return appliedAny;
            }

            if (DriveLeftForeArm)
            {
                Quaternion targetLocal = BuildContinuousWorldMappedLocal(
                    leftForeArm, leftForeSensor, sensorToBoneWorldOffsets[1], LeftForeArmBoneAxisOffsetEuler);
                appliedAny |= ApplyLocalToBone(leftForeArm, 1, targetLocal);
            }

            if (DriveRightForeArm)
            {
                Quaternion targetLocal = BuildContinuousWorldMappedLocal(
                    rightForeArm, rightForeSensor, sensorToBoneWorldOffsets[3], RightForeArmBoneAxisOffsetEuler);
                appliedAny |= ApplyLocalToBone(rightForeArm, 3, targetLocal);
            }

            UpdateElbowAngles(leftArm, leftForeArm, rightArm, rightForeArm);
        }

        if (!appliedAny)
            LastError = "没有任何手臂骨骼被应用，请检查 Drive 开关";

        return appliedAny;
    }

    /// <summary>
    /// 旧V3左小臂兼容路径：先在传感器空间计算02相对01的姿态，再减去A-Pose参考。
    /// 得到的增量通过标定基变换到Avatar左大臂局部空间，并作用到左小臂标定localRotation。
    /// 这样左大臂共同运动只由01负责，不会在02路径中被重复施加。
    /// </summary>
    private Quaternion BuildLeftForeArmRelativeLocal(
        Quaternion currentLeftUpperSensor,
        Quaternion currentLeftForeSensor)
    {
        Quaternion currentRelative = NormalizeSafe(
            Quaternion.Inverse(currentLeftUpperSensor) * currentLeftForeSensor);
        Quaternion relativeDeltaSensor = NormalizeSafe(
            currentRelative * Quaternion.Inverse(leftForeArmRelativeReference));

        Quaternion basis = NormalizeSafe(leftUpperSensorToBoneBasis);
        Quaternion relativeDeltaUpperBone = NormalizeSafe(
            basis * relativeDeltaSensor * Quaternion.Inverse(basis));

        if (SuppressLeftForeArmAxialTwist)
        {
            relativeDeltaUpperBone = RemoveTwistAroundAxis(
                relativeDeltaUpperBone,
                leftForeArmRestAxisUpperLocal);
        }

        Quaternion targetLocal = NormalizeSafe(
            relativeDeltaUpperBone * calibratedForeArmLocals[0]);
        if (LeftForeArmBoneAxisOffsetEuler.sqrMagnitude > 0.000001f)
        {
            targetLocal = NormalizeSafe(
                targetLocal * Quaternion.Euler(LeftForeArmBoneAxisOffsetEuler));
        }
        return targetLocal;
    }

    /// <summary>移除绕指定骨段长轴的twist，保留与肘部摆动相关的swing。</summary>
    private static Quaternion RemoveTwistAroundAxis(Quaternion rotation, Vector3 axis)
    {
        Quaternion q = NormalizeSafe(rotation);
        Vector3 n = SafeDirection(axis, Vector3.down);
        Vector3 vectorPart = new Vector3(q.x, q.y, q.z);
        Vector3 projected = n * Vector3.Dot(vectorPart, n);
        Quaternion twist = NormalizeSafe(
            new Quaternion(projected.x, projected.y, projected.z, q.w));
        Quaternion swing = NormalizeSafe(q * Quaternion.Inverse(twist));
        return swing;
    }

    private void BeginRightArmPoseLearningIfNeeded()
    {
        rightArmPoseLearningReady = false;
        rightArmDeltaBasisLocal = Quaternion.identity;
        rightArmPoseLearningStage = 0;
        rightArmPoseLearningStageStartTime = Time.time;
        rightArmPoseLearningActive = DriveRightArm && EnableRightArmFourPoseAxisLearning;
        rightArmPoseLearningStatus = rightArmPoseLearningActive
            ? "03四姿态轴学习即将开始：请先准备右臂前伸"
            : string.Empty;

        for (int i = 0; i < rightArmPoseReferences.Length; i++)
        {
            rightArmPoseReferences[i] = Quaternion.identity;
            rightArmPoseSums[i] = Vector4.zero;
            rightArmPoseSampleCounts[i] = 0;
        }
    }

    private void UpdateRightArmFourPoseLearning(Quaternion currentSensorWorld, Transform avatarRoot)
    {
        if (!rightArmPoseLearningActive || rightArmPoseLearningStage < 0 ||
            rightArmPoseLearningStage >= RightArmPoseNames.Length)
            return;
        if (!IsQuaternionFinite(currentSensorWorld)) return;

        float initial = Mathf.Max(0f, RightArmPoseInitialPrepareSeconds);
        float transition = rightArmPoseLearningStage == 0
            ? initial
            : Mathf.Max(0f, RightArmPoseTransitionSeconds);
        float capture = Mathf.Max(0.5f, RightArmPoseCaptureSeconds);
        float elapsed = Time.time - rightArmPoseLearningStageStartTime;
        string poseName = RightArmPoseNames[rightArmPoseLearningStage];

        if (elapsed < transition)
        {
            rightArmPoseLearningStatus =
                $"03四姿态轴学习 {rightArmPoseLearningStage + 1}/4：请切换到{poseName}，{transition - elapsed:F1}s后开始采样";
            return;
        }

        AccumulateRightArmPoseSample(rightArmPoseLearningStage, currentSensorWorld);
        float captureElapsed = elapsed - transition;
        rightArmPoseLearningStatus =
            $"03四姿态轴学习 {rightArmPoseLearningStage + 1}/4：保持{poseName}，采样剩余{Mathf.Max(0f, capture - captureElapsed):F1}s";

        if (captureElapsed < capture) return;

        if (!FinalizeRightArmPoseSample(rightArmPoseLearningStage))
        {
            rightArmPoseLearningActive = false;
            rightArmPoseLearningStatus = $"03四姿态轴学习失败：{poseName}有效样本不足，已回退V8.6路径";
            Debug.LogWarning("[V8.7 03四姿态轴学习] " + rightArmPoseLearningStatus);
            return;
        }

        rightArmPoseLearningStage++;
        rightArmPoseLearningStageStartTime = Time.time;
        if (rightArmPoseLearningStage < RightArmPoseNames.Length)
            return;

        rightArmPoseLearningActive = false;
        if (TryBuildRightArmLearnedBasis(avatarRoot, out string reason))
        {
            rightArmPoseLearningReady = true;
            rightArmPoseLearningStatus = "03四姿态轴学习完成，右大臂已进入连续驱动";
            Debug.LogWarning(
                $"[V8.7 03四姿态轴学习完成] basisLocal={rightArmDeltaBasisLocal.eulerAngles}；" +
                "前伸/平举用于建立双轴，上举/下垂用于自动选符号与验算");
        }
        else
        {
            rightArmPoseLearningReady = false;
            rightArmPoseLearningStatus = $"03四姿态轴学习失败：{reason}，已回退V8.6路径";
            Debug.LogWarning("[V8.7 03四姿态轴学习] " + rightArmPoseLearningStatus);
        }
    }

    private void AccumulateRightArmPoseSample(int stage, Quaternion sample)
    {
        Quaternion q = NormalizeSafe(sample);
        if (!IsQuaternionFinite(q) || stage < 0 || stage >= rightArmPoseSums.Length) return;
        Vector4 sum = rightArmPoseSums[stage];
        if (rightArmPoseSampleCounts[stage] > 0)
        {
            Quaternion reference = NormalizeSafe(new Quaternion(sum.x, sum.y, sum.z, sum.w));
            if (Quaternion.Dot(reference, q) < 0f)
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        }
        rightArmPoseSums[stage] = sum + new Vector4(q.x, q.y, q.z, q.w);
        rightArmPoseSampleCounts[stage]++;
    }

    private bool FinalizeRightArmPoseSample(int stage)
    {
        if (stage < 0 || stage >= rightArmPoseSums.Length || rightArmPoseSampleCounts[stage] < 3)
            return false;
        Vector4 sum = rightArmPoseSums[stage];
        Quaternion average = NormalizeSafe(new Quaternion(sum.x, sum.y, sum.z, sum.w));
        if (!IsQuaternionFinite(average)) return false;
        rightArmPoseReferences[stage] = average;
        return true;
    }

    private bool TryBuildRightArmLearnedBasis(Transform avatarRoot, out string reason)
    {
        reason = string.Empty;
        Quaternion rootWorld = avatarRoot != null && IsQuaternionFinite(avatarRoot.rotation)
            ? avatarRoot.rotation.normalized
            : Quaternion.identity;
        Quaternion reference = NormalizeSafe(sensorReferences[2]);
        if (!IsQuaternionFinite(reference))
        {
            reason = "03基础标定参考四元数非法";
            return false;
        }

        Quaternion[] deltasLocal = new Quaternion[4];
        for (int i = 0; i < 4; i++)
        {
            Quaternion deltaWorld = NormalizeSafe(
                rightArmPoseReferences[i] * Quaternion.Inverse(reference));
            deltasLocal[i] = NormalizeSafe(
                Quaternion.Inverse(rootWorld) * deltaWorld * rootWorld);
        }

        if (!TryGetShortestRotationAxis(deltasLocal[0], out Vector3 sourceForwardAxis) ||
            !TryGetShortestRotationAxis(deltasLocal[1], out Vector3 sourceSideAxis))
        {
            reason = "前伸或平举相对旋转过小，无法建立两个独立旋转轴";
            return false;
        }

        Vector3 restLocal = SafeDirection(
            Quaternion.Inverse(rootWorld) * restSegmentDirectionsWorld[2], Vector3.down);
        Vector3 targetForward = Vector3.forward;
        Vector3 targetSide = Vector3.right;
        Vector3 targetUp = Vector3.up;
        Vector3 targetDown = Vector3.down;
        Vector3 targetForwardAxis = SafeDirection(
            Vector3.Cross(restLocal, targetForward), Vector3.left);
        Vector3 targetSideAxis = SafeDirection(
            Vector3.Cross(restLocal, targetSide), Vector3.forward);

        float bestScore = float.PositiveInfinity;
        Quaternion bestBasis = Quaternion.identity;
        bool found = false;
        for (int forwardSign = -1; forwardSign <= 1; forwardSign += 2)
        {
            for (int sideSign = -1; sideSign <= 1; sideSign += 2)
            {
                if (!TryBuildFrameFromTwoAxes(
                        sourceForwardAxis * forwardSign,
                        sourceSideAxis * sideSign,
                        out Quaternion sourceFrame) ||
                    !TryBuildFrameFromTwoAxes(
                        targetForwardAxis,
                        targetSideAxis,
                        out Quaternion targetFrame))
                    continue;

                Quaternion candidate = NormalizeSafe(targetFrame * Quaternion.Inverse(sourceFrame));
                float score = ScoreRightArmBasis(
                    candidate, deltasLocal, restLocal,
                    targetForward, targetSide, targetUp, targetDown);
                if (!float.IsNaN(score) && !float.IsInfinity(score) && score < bestScore)
                {
                    bestScore = score;
                    bestBasis = candidate;
                    found = true;
                }
            }
        }

        if (!found)
        {
            reason = "无法从前伸与平举建立稳定正交坐标框架";
            return false;
        }

        // 四个参考姿态的平均角误差过大时不启用，避免错误样本破坏运行。
        if (bestScore > 70f)
        {
            reason = $"四姿态一致性不足（平均误差{bestScore:F1}°），请按提示重新保持每个动作";
            return false;
        }

        rightArmDeltaBasisLocal = bestBasis;
        return true;
    }

    private static float ScoreRightArmBasis(
        Quaternion basis,
        Quaternion[] deltasLocal,
        Vector3 restLocal,
        Vector3 targetForward,
        Vector3 targetSide,
        Vector3 targetUp,
        Vector3 targetDown)
    {
        Vector3[] targets = { targetForward, targetSide, targetUp, targetDown };
        float sum = 0f;
        for (int i = 0; i < deltasLocal.Length; i++)
        {
            Quaternion corrected = NormalizeSafe(
                basis * deltasLocal[i] * Quaternion.Inverse(basis));
            Vector3 predicted = SafeDirection(corrected * restLocal, restLocal);
            sum += Vector3.Angle(predicted, targets[i]);
        }
        return sum / deltasLocal.Length;
    }

    private static bool TryBuildFrameFromTwoAxes(
        Vector3 xAxis,
        Vector3 zAxisHint,
        out Quaternion frame)
    {
        frame = Quaternion.identity;
        Vector3 x = SafeDirection(xAxis, Vector3.right);
        Vector3 z = Vector3.ProjectOnPlane(zAxisHint, x);
        if (!IsVectorFinite(z) || z.sqrMagnitude < 0.0001f) return false;
        z.Normalize();
        Vector3 y = Vector3.Cross(z, x);
        if (!IsVectorFinite(y) || y.sqrMagnitude < 0.0001f) return false;
        y.Normalize();
        z = Vector3.Cross(x, y).normalized;
        frame = Quaternion.LookRotation(z, y).normalized;
        return IsQuaternionFinite(frame);
    }

    private static bool TryGetShortestRotationAxis(Quaternion rotation, out Vector3 axis)
    {
        axis = Vector3.zero;
        Quaternion q = NormalizeSafe(rotation);
        if (!IsQuaternionFinite(q)) return false;
        if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        q.ToAngleAxis(out float angle, out Vector3 rawAxis);
        if (angle > 180f)
        {
            angle = 360f - angle;
            rawAxis = -rawAxis;
        }
        if (angle < 8f || !IsVectorFinite(rawAxis) || rawAxis.sqrMagnitude < 0.0001f)
            return false;
        axis = rawAxis.normalized;
        return true;
    }

    /// <summary>
    /// V8.10右大臂连续主路径。
    /// 先在03传感器局部坐标中计算 deltaLocal = inverse(reference) * current，
    /// 将其最短旋转向量通过连续3×3矩阵转换为Avatar根节点局部肩关节Swing旋转向量，
    /// 再作用到标定时肩→肘方向。矩阵只做坐标轴耦合校正，不识别动作、不吸附姿态。
    /// </summary>
    private Vector3 GetRightArmDirectionFromCalibratedDeltaSwing(
        Quaternion currentSensorWorld,
        Transform avatarRoot)
    {
        Quaternion current = NormalizeSafe(currentSensorWorld);
        Quaternion reference = NormalizeSafe(sensorReferences[2]);
        Vector3 restWorld = SafeDirection(restSegmentDirectionsWorld[2], Vector3.down);
        if (!rightArmLocalDeltaMapReady ||
            !IsQuaternionFinite(current) || !IsQuaternionFinite(reference))
            return restWorld;

        // q与-q表示同一姿态。先统一半球，避免串口四元数换号造成旋转向量瞬间跳变。
        if (Quaternion.Dot(reference, current) < 0f)
            current = new Quaternion(-current.x, -current.y, -current.z, -current.w);

        Quaternion deltaLocal = NormalizeSafe(Quaternion.Inverse(reference) * current);
        if (!IsQuaternionFinite(deltaLocal))
            return restWorld;

        Vector3 sensorRotationVector = QuaternionToRotationVectorRadians(deltaLocal);
        if (!IsVectorFinite(sensorRotationVector))
            return restWorld;

        Vector3 avatarSwingVector = rightArmLocalDeltaToAvatarSwing.MultiplyVector(sensorRotationVector);
        if (!IsVectorFinite(avatarSwingVector))
            return restWorld;

        // 肩关节Swing采用最短弧，避免数值外推产生超过180°的翻转。
        float maxSwingRadians = 175f * Mathf.Deg2Rad;
        float swingMagnitude = avatarSwingVector.magnitude;
        if (swingMagnitude > maxSwingRadians && swingMagnitude > 0.000001f)
            avatarSwingVector *= maxSwingRadians / swingMagnitude;

        Quaternion avatarSwingLocal = RotationVectorToQuaternion(avatarSwingVector);
        if (!IsQuaternionFinite(avatarSwingLocal))
            return restWorld;

        Quaternion rootWorld = avatarRoot != null && IsQuaternionFinite(avatarRoot.rotation)
            ? avatarRoot.rotation.normalized
            : Quaternion.identity;
        Vector3 restLocal = SafeDirection(Quaternion.Inverse(rootWorld) * restWorld, Vector3.down);
        Vector3 desiredLocal = SafeDirection(avatarSwingLocal * restLocal, restLocal);
        return SafeDirection(rootWorld * desiredLocal, restWorld);
    }

    /// <summary>
    /// 使用本轮实测的03局部Delta旋转向量建立连续最小二乘坐标映射。
    /// 目标向量由当前Avatar的实际A-Pose肩→肘方向动态生成，因此不同模型骨骼初始角度仍可保持零位。
    /// </summary>
    private bool TryBuildRightArmLocalDeltaMap(Transform avatarRoot, out string reason)
    {
        reason = string.Empty;
        rightArmLocalDeltaToAvatarSwing = Matrix4x4.identity;
        rightArmLocalDeltaMapReady = false;

        Quaternion rootWorld = avatarRoot != null && IsQuaternionFinite(avatarRoot.rotation)
            ? avatarRoot.rotation.normalized
            : Quaternion.identity;
        Vector3 restLocal = SafeDirection(
            Quaternion.Inverse(rootWorld) * restSegmentDirectionsWorld[2], Vector3.down);

        Vector3[] targetDirectionsLocal =
        {
            Vector3.forward,
            Vector3.right,
            Vector3.up,
            Vector3.down
        };

        Matrix4x4 sensorNormal = new Matrix4x4();
        Matrix4x4 avatarSensorCross = new Matrix4x4();
        for (int i = 0; i < RightArmMeasuredLocalDeltaRotationVectors.Length; i++)
        {
            Vector3 sensorVector = RightArmMeasuredLocalDeltaRotationVectors[i];
            Vector3 avatarVector = RotationVectorBetweenDirections(
                restLocal, targetDirectionsLocal[i]);
            if (!IsVectorFinite(sensorVector) || !IsVectorFinite(avatarVector))
            {
                reason = $"第{i + 1}组连续轴样本非法";
                return false;
            }

            AddOuterProduct3x3(ref sensorNormal, sensorVector, sensorVector, 1f);
            AddOuterProduct3x3(ref avatarSensorCross, avatarVector, sensorVector, 1f);
        }

        // 小幅岭正则避免实测样本接近共面时矩阵病态；不引入姿态目标或运行时吸附。
        const float ridge = 0.005f;
        sensorNormal.m00 += ridge;
        sensorNormal.m11 += ridge;
        sensorNormal.m22 += ridge;
        sensorNormal.m33 = 1f;
        avatarSensorCross.m33 = 1f;

        float determinant = sensorNormal.determinant;
        if (!IsFinite(determinant) || Mathf.Abs(determinant) < 0.0000001f)
        {
            reason = "03实测局部Delta样本矩阵不可逆";
            return false;
        }

        Matrix4x4 inverseNormal = sensorNormal.inverse;
        Matrix4x4 map = avatarSensorCross * inverseNormal;
        if (!IsMatrixFinite3x3(map))
        {
            reason = "03连续坐标映射矩阵包含NaN或Infinity";
            return false;
        }

        rightArmLocalDeltaToAvatarSwing = map;
        rightArmLocalDeltaMapReady = true;
        Debug.LogWarning(
            "[V8.10 03连续局部Delta矩阵] 已建立；" +
            $"restLocal={restLocal}，det={determinant:F6}；" +
            "运行时无动作分类、无标准姿态吸附、无顶部提示");
        return true;
    }

    private static void AddOuterProduct3x3(
        ref Matrix4x4 matrix,
        Vector3 left,
        Vector3 right,
        float weight)
    {
        matrix.m00 += weight * left.x * right.x;
        matrix.m01 += weight * left.x * right.y;
        matrix.m02 += weight * left.x * right.z;
        matrix.m10 += weight * left.y * right.x;
        matrix.m11 += weight * left.y * right.y;
        matrix.m12 += weight * left.y * right.z;
        matrix.m20 += weight * left.z * right.x;
        matrix.m21 += weight * left.z * right.y;
        matrix.m22 += weight * left.z * right.z;
    }

    private static Vector3 RotationVectorBetweenDirections(Vector3 from, Vector3 to)
    {
        Vector3 safeFrom = SafeDirection(from, Vector3.down);
        Vector3 safeTo = SafeDirection(to, safeFrom);
        Quaternion rotation = NormalizeSafe(Quaternion.FromToRotation(safeFrom, safeTo));
        return QuaternionToRotationVectorRadians(rotation);
    }

    private static Vector3 QuaternionToRotationVectorRadians(Quaternion rotation)
    {
        Quaternion q = NormalizeSafe(rotation);
        if (!IsQuaternionFinite(q))
            return new Vector3(float.NaN, float.NaN, float.NaN);
        if (q.w < 0f)
            q = new Quaternion(-q.x, -q.y, -q.z, -q.w);

        float vectorMagnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
        if (vectorMagnitude < 0.000001f)
            return new Vector3(q.x, q.y, q.z) * 2f;

        float angle = 2f * Mathf.Atan2(vectorMagnitude, Mathf.Clamp(q.w, -1f, 1f));
        Vector3 axis = new Vector3(q.x, q.y, q.z) / vectorMagnitude;
        return axis * angle;
    }

    private static Quaternion RotationVectorToQuaternion(Vector3 rotationVector)
    {
        if (!IsVectorFinite(rotationVector))
            return new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);
        float angle = rotationVector.magnitude;
        if (angle < 0.000001f)
            return Quaternion.identity;
        return NormalizeSafe(Quaternion.AngleAxis(
            angle * Mathf.Rad2Deg, rotationVector / angle));
    }

    private static bool IsMatrixFinite3x3(Matrix4x4 matrix)
    {
        return IsFinite(matrix.m00) && IsFinite(matrix.m01) && IsFinite(matrix.m02) &&
               IsFinite(matrix.m10) && IsFinite(matrix.m11) && IsFinite(matrix.m12) &&
               IsFinite(matrix.m20) && IsFinite(matrix.m21) && IsFinite(matrix.m22);
    }

    private Vector3 GetRightArmDirectionFromLearnedFrame(
        Quaternion currentSensorWorld,
        Transform avatarRoot)
    {
        Quaternion rootWorld = avatarRoot != null && IsQuaternionFinite(avatarRoot.rotation)
            ? avatarRoot.rotation.normalized
            : Quaternion.identity;
        Quaternion deltaWorld = NormalizeSafe(
            currentSensorWorld * Quaternion.Inverse(sensorReferences[2]));
        Quaternion deltaLocal = NormalizeSafe(
            Quaternion.Inverse(rootWorld) * deltaWorld * rootWorld);
        Quaternion basis = NormalizeSafe(rightArmDeltaBasisLocal);
        Quaternion correctedLocal = NormalizeSafe(
            basis * deltaLocal * Quaternion.Inverse(basis));
        Vector3 restLocal = SafeDirection(
            Quaternion.Inverse(rootWorld) * restSegmentDirectionsWorld[2], Vector3.down);
        Vector3 desiredLocal = SafeDirection(correctedLocal * restLocal, restLocal);
        return SafeDirection(rootWorld * desiredLocal, restSegmentDirectionsWorld[2]);
    }

    private Vector3 GetCurrentSegmentDirection(Quaternion sensorWorld, int index)
    {
        Vector3 direction = sensorWorld.normalized * sensorSegmentDirectionsLocal[index];
        if (index == 0)
            direction = leftArmSessionAlignmentWorld * direction;
        else if (index == 2)
            direction = rightArmSessionAlignmentWorld * direction;
        return SafeDirection(direction, restSegmentDirectionsWorld[index]);
    }

    /// <summary>
    /// 右大臂校正主路径：
    /// 1. 先计算当前右上臂相对标定姿态的世界delta；
    /// 2. 对delta做坐标框架共轭变换 C * delta * C^-1；
    /// 3. 再把校正后的delta作用到标定肩→肘方向。
    /// delta在标定零位恒为identity，因此校正不会改变标定姿态。
    /// </summary>
    private Vector3 GetRightArmDirectionFromCorrectedDelta(
        Quaternion currentSensorWorld,
        Transform avatarRoot,
        float correctionDeg)
    {
        Quaternion current = NormalizeSafe(currentSensorWorld);
        Quaternion reference = NormalizeSafe(sensorReferences[2]);
        Quaternion deltaWorld = NormalizeSafe(current * Quaternion.Inverse(reference));

        float angle = Mathf.Clamp(correctionDeg, -180f, 180f);
        if (Mathf.Abs(angle) >= 0.001f)
        {
            Vector3 axis = avatarRoot != null
                ? SafeDirection(avatarRoot.right, Vector3.right)
                : Vector3.right;
            Quaternion frame = Quaternion.AngleAxis(angle, axis);
            deltaWorld = NormalizeSafe(frame * deltaWorld * Quaternion.Inverse(frame));
        }

        Vector3 direction = deltaWorld * restSegmentDirectionsWorld[2];
        return SafeDirection(direction, restSegmentDirectionsWorld[2]);
    }

    /// <summary>
    /// 连续的传感器坐标交叉轴校准：当大臂方向包含向前分量时，
    /// 按该分量的大小加入少量解剖学外侧分量，修正“前伸略向胸前内收”。
    /// 侧平举、上举和自然下垂的向前分量接近零，因此基本不受影响。
    /// </summary>
    private static Vector3 ApplyForwardOutwardCrossAxisCompensation(
        Vector3 desiredDirectionWorld,
        Transform avatarRoot,
        bool isLeftSide,
        float compensationDeg)
    {
        Vector3 direction = SafeDirection(desiredDirectionWorld, Vector3.down);
        float angle = Mathf.Clamp(compensationDeg, -25f, 25f);
        if (Mathf.Abs(angle) < 0.001f)
            return direction;

        Vector3 forward = avatarRoot != null
            ? SafeDirection(avatarRoot.forward, Vector3.forward)
            : Vector3.forward;
        Vector3 right = avatarRoot != null
            ? SafeDirection(avatarRoot.right, Vector3.right)
            : Vector3.right;
        Vector3 outward = isLeftSide ? -right : right;

        // 仅使用正向前伸分量；斜向康复动作仍保持连续。
        float forwardAmount = Mathf.Max(0f, Vector3.Dot(direction, forward));
        float lateralGain = Mathf.Tan(angle * Mathf.Deg2Rad) * forwardAmount;
        return SafeDirection(direction + outward * lateralGain, direction);
    }

    /// <summary>
    /// 把一个世界方向转到Avatar局部，应用固定连续坐标基，再转回世界。
    /// 这里只处理向量，不会写骨骼，也不会识别动作。
    /// </summary>
    private static Vector3 ApplyAvatarLocalDirectionBasis(
        Vector3 directionWorld,
        Transform avatarRoot,
        Quaternion basisLocal)
    {
        Vector3 direction = SafeDirection(directionWorld, Vector3.down);
        Quaternion rootWorld = avatarRoot != null
            ? NormalizeSafe(avatarRoot.rotation)
            : Quaternion.identity;
        Quaternion basis = NormalizeSafe(basisLocal);

        if (!IsQuaternionFinite(rootWorld)) rootWorld = Quaternion.identity;
        if (!IsQuaternionFinite(basis)) basis = Quaternion.identity;

        Vector3 localDirection = Quaternion.Inverse(rootWorld) * direction;
        Vector3 correctedLocal = basis * localDirection;
        Vector3 correctedWorld = rootWorld * correctedLocal;
        return SafeDirection(correctedWorld, direction);
    }

    private Quaternion BuildDirectionMappedLocal(
        Transform bone,
        int index,
        Vector3 desiredDirectionWorld,
        Vector3 boneAxisOffsetEuler)
    {
        Vector3 restDirection = SafeDirection(restSegmentDirectionsWorld[index], Vector3.down);
        Vector3 desiredDirection = SafeDirection(desiredDirectionWorld, restDirection);

        Quaternion swingWorld = StableFromToRotation(restDirection, desiredDirection, restWorlds[index]);
        Quaternion targetWorld = (swingWorld * restWorlds[index]).normalized;

        Quaternion parentWorld = bone.parent != null
            ? bone.parent.rotation.normalized
            : Quaternion.identity;

        Quaternion targetLocal =
            (Quaternion.Inverse(parentWorld) * targetWorld).normalized;

        if (boneAxisOffsetEuler != Vector3.zero)
            targetLocal = (targetLocal * Quaternion.Euler(boneAxisOffsetEuler)).normalized;

        return targetLocal;
    }

    /// <summary>
    /// 稳定的 FromTo：一般区域使用 Unity FromToRotation；接近 180° 时用标定骨骼的
    /// 次轴确定唯一旋转平面，避免大臂越过头顶时突然翻面。
    /// </summary>
    private static Quaternion StableFromToRotation(
        Vector3 fromDirection,
        Vector3 toDirection,
        Quaternion restWorld)
    {
        Vector3 from = SafeDirection(fromDirection, Vector3.down);
        Vector3 to = SafeDirection(toDirection, from);
        float dot = Mathf.Clamp(Vector3.Dot(from, to), -1f, 1f);

        if (dot > -0.9995f)
            return Quaternion.FromToRotation(from, to).normalized;

        Vector3 axis = Vector3.ProjectOnPlane(restWorld * Vector3.forward, from);
        if (!IsVectorFinite(axis) || axis.sqrMagnitude < 0.000001f)
            axis = Vector3.ProjectOnPlane(restWorld * Vector3.up, from);
        if (!IsVectorFinite(axis) || axis.sqrMagnitude < 0.000001f)
            axis = Vector3.Cross(from, Vector3.right);
        if (!IsVectorFinite(axis) || axis.sqrMagnitude < 0.000001f)
            axis = Vector3.Cross(from, Vector3.up);

        axis.Normalize();
        return Quaternion.AngleAxis(180f, axis).normalized;
    }

    private static Quaternion BuildContinuousWorldMappedLocal(
        Transform bone,
        Quaternion currentSensorWorld,
        Quaternion sensorToBoneWorldOffset,
        Vector3 boneAxisOffsetEuler)
    {
        Quaternion targetWorld =
            (currentSensorWorld.normalized * sensorToBoneWorldOffset.normalized).normalized;

        Quaternion parentWorld = bone.parent != null
            ? bone.parent.rotation.normalized
            : Quaternion.identity;

        Quaternion targetLocal =
            (Quaternion.Inverse(parentWorld) * targetWorld).normalized;

        if (boneAxisOffsetEuler != Vector3.zero)
            targetLocal = (targetLocal * Quaternion.Euler(boneAxisOffsetEuler)).normalized;

        return targetLocal;
    }

    private void UpdateElbowAngles(
        Transform leftArm,
        Transform leftForeArm,
        Transform rightArm,
        Transform rightForeArm)
    {
        Vector3 leftUpper = ResolveBoneDirection(leftArm, leftForeArm, leftArm.rotation * Vector3.down);
        Vector3 rightUpper = ResolveBoneDirection(rightArm, rightForeArm, rightArm.rotation * Vector3.down);

        Vector3 leftFore = leftForeArm.childCount > 0
            ? ResolveBoneDirection(leftForeArm, leftForeArm.GetChild(0), leftForeArm.rotation * Vector3.down)
            : leftForeArm.rotation * Vector3.down;
        Vector3 rightFore = rightForeArm.childCount > 0
            ? ResolveBoneDirection(rightForeArm, rightForeArm.GetChild(0), rightForeArm.rotation * Vector3.down)
            : rightForeArm.rotation * Vector3.down;

        CurrentLeftElbowFlexionAngleDeg = Mathf.Clamp(Vector3.Angle(leftUpper, leftFore), 0f, 180f);
        CurrentRightElbowFlexionAngleDeg = Mathf.Clamp(Vector3.Angle(rightUpper, rightFore), 0f, 180f);
        CurrentLeftElbowIncludedAngleDeg = 180f - CurrentLeftElbowFlexionAngleDeg;
        CurrentRightElbowIncludedAngleDeg = 180f - CurrentRightElbowFlexionAngleDeg;
    }

    private bool ApplyLocalToBone(Transform bone, int index, Quaternion targetLocal)
    {
        if (bone == null || !IsQuaternionFinite(targetLocal))
            return false;

        Quaternion current = hasLastApplied[index]
            ? lastAppliedLocals[index]
            : bone.localRotation;
        current = NormalizeSafe(current);
        targetLocal = NormalizeSafe(targetLocal);
        targetLocal = ShortestArcTarget(current, targetLocal);

        float angle = Quaternion.Angle(current, targetLocal);
        Quaternion applied;

        if (angle <= Mathf.Max(0f, MinAngleThresholdDeg))
        {
            applied = current;
        }
        else if (SmoothingEnabled)
        {
            float t = 1f - Mathf.Exp(-Mathf.Max(0.01f, SmoothingSpeed) * Time.deltaTime);
            applied = Quaternion.Slerp(current, targetLocal, t).normalized;
        }
        else
        {
            applied = targetLocal;
        }

        bone.localRotation = applied;
        lastAppliedLocals[index] = applied;
        hasLastApplied[index] = true;
        return true;
    }

    private static Vector3 ResolveSensorDirectionLocal(
        Quaternion sensorReference,
        Vector3 restDirectionWorld,
        SegmentAxisMode mode)
    {
        if (mode == SegmentAxisMode.AutoFromCalibration)
        {
            Vector3 exactLocal = Quaternion.Inverse(sensorReference.normalized) *
                                 SafeDirection(restDirectionWorld, Vector3.down);
            return SafeDirection(exactLocal, Vector3.down);
        }

        switch (mode)
        {
            case SegmentAxisMode.PositiveX: return Vector3.right;
            case SegmentAxisMode.NegativeX: return Vector3.left;
            case SegmentAxisMode.PositiveY: return Vector3.up;
            case SegmentAxisMode.NegativeY: return Vector3.down;
            case SegmentAxisMode.PositiveZ: return Vector3.forward;
            case SegmentAxisMode.NegativeZ: return Vector3.back;
            default: return Vector3.down;
        }
    }

    private static Vector3 GetDirectionBetween(Transform from, Transform to, Vector3 fallback)
    {
        if (from != null && to != null)
        {
            Vector3 direction = to.position - from.position;
            if (IsVectorFinite(direction) && direction.sqrMagnitude > 0.000001f)
                return direction.normalized;
        }

        return SafeDirection(fallback, Vector3.down);
    }

    private static Vector3 GetDirectionToPrimaryChild(Transform segment, Vector3 fallback)
    {
        if (segment == null || segment.childCount <= 0)
            return SafeDirection(fallback, Vector3.down);

        Transform bestChild = null;
        float bestDistanceSqr = 0f;
        for (int i = 0; i < segment.childCount; i++)
        {
            Transform child = segment.GetChild(i);
            if (child == null) continue;
            float distanceSqr = (child.position - segment.position).sqrMagnitude;
            if (distanceSqr > bestDistanceSqr)
            {
                bestDistanceSqr = distanceSqr;
                bestChild = child;
            }
        }

        return bestChild != null
            ? GetDirectionBetween(segment, bestChild, fallback)
            : SafeDirection(fallback, Vector3.down);
    }

    private static Vector3 ResolveBoneDirection(Transform start, Transform end, Vector3 fallback)
    {
        return GetDirectionBetween(start, end, fallback);
    }

    private static Quaternion ApplyRightArmCorrection(Quaternion q, RightArmCorrectionMode mode)
    {
        q = NormalizeSafe(q);
        switch (mode)
        {
            case RightArmCorrectionMode.MirrorAvatarX: return new Quaternion(q.x, -q.y, -q.z, q.w).normalized;
            case RightArmCorrectionMode.MirrorAvatarY: return new Quaternion(-q.x, q.y, -q.z, q.w).normalized;
            case RightArmCorrectionMode.MirrorAvatarZ: return new Quaternion(-q.x, -q.y, q.z, q.w).normalized;
            case RightArmCorrectionMode.FlipX: return new Quaternion(-q.x, q.y, q.z, q.w).normalized;
            case RightArmCorrectionMode.FlipY: return new Quaternion(q.x, -q.y, q.z, q.w).normalized;
            case RightArmCorrectionMode.FlipZ: return new Quaternion(q.x, q.y, -q.z, q.w).normalized;
            default: return q;
        }
    }

    private static Quaternion ApplyRightForeArmCorrection(Quaternion q, RightForeArmCorrectionMode mode)
    {
        q = NormalizeSafe(q);
        switch (mode)
        {
            case RightForeArmCorrectionMode.MirrorAvatarX: return new Quaternion(q.x, -q.y, -q.z, q.w).normalized;
            case RightForeArmCorrectionMode.MirrorAvatarY: return new Quaternion(-q.x, q.y, -q.z, q.w).normalized;
            case RightForeArmCorrectionMode.MirrorAvatarZ: return new Quaternion(-q.x, -q.y, q.z, q.w).normalized;
            case RightForeArmCorrectionMode.FlipX: return new Quaternion(-q.x, q.y, q.z, q.w).normalized;
            case RightForeArmCorrectionMode.FlipY: return new Quaternion(q.x, -q.y, q.z, q.w).normalized;
            case RightForeArmCorrectionMode.FlipZ: return new Quaternion(q.x, q.y, -q.z, q.w).normalized;
            default: return q;
        }
    }

    private static Quaternion NormalizeSafe(Quaternion q)
    {
        float magSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (!IsQuaternionFinite(q) || magSq < 0.00000001f)
            return new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);

        float invMag = 1f / Mathf.Sqrt(magSq);
        return new Quaternion(q.x * invMag, q.y * invMag, q.z * invMag, q.w * invMag);
    }

    private static Quaternion ShortestArcTarget(Quaternion current, Quaternion target)
    {
        if (Quaternion.Dot(current, target) < 0f)
            return new Quaternion(-target.x, -target.y, -target.z, -target.w);
        return target;
    }

    private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
    {
        if (!IsVectorFinite(value) || value.sqrMagnitude < 0.000001f)
        {
            if (!IsVectorFinite(fallback) || fallback.sqrMagnitude < 0.000001f)
                return Vector3.down;
            return fallback.normalized;
        }

        return value.normalized;
    }

    private static bool IsQuaternionFinite(Quaternion q)
    {
        return IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w);
    }

    private static bool IsVectorFinite(Vector3 v)
    {
        return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

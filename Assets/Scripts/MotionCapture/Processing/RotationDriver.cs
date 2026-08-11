using UnityEngine;

/// <summary>
/// 传感器四元数坐标转换与旧版通用骨骼旋转工具。
/// V69 恢复按手臂、躯干、左大腿、右大腿分别进行传感器坐标映射；
/// 固定佩戴角由标定抵消；V7根据V6-2实测撤销未通过的03号(+z,+x,-y,w)试验，
/// 回退V5较优的(+y,-z,+x,w)；腿部保持F_ZXY基础轴。
/// </summary>
public class RotationDriver
{
    // =========================
    // 左大腿基础轴映射测试模式
    // =========================
    // 现在不要继续靠 Thigh Bone Axis Offset Euler 盲调。
    // 测试左大腿基础映射时：
    // 1) MotionCaptureController 里 Left Thigh Bone Axis Offset Euler 设为 (0,0,0)
    // 2) Drive Left Calf = false
    // 3) Thigh Rotation Gain = 1
    // 4) 每次只改这里的 LeftThighMapMode，然后重新 Play、重新标定。
    public enum LeftThighMapMode
    {
        A_Current = 0,       // 当前旧映射：(-x,-y,-z,w)
        B_HandLike = 1,      // 与手部接近：(-y,-z,x,w)
        C_YXZ = 2,           // Y/X/Z 交换候选
        D_ZYX = 3,           // Z/Y/X 交换候选
        E_XZY = 4,           // X/Z/Y 交换候选
        F_ZXY = 5,           // Z/X/Y 交换候选
        G_HandLikeFlipW = 6, // B 的整体等价符号备选
        H_CurrentFlipW = 7   // A 的整体等价符号备选
    }

    // V77.19-1 左大腿继续使用 B_HandLike；撤销 V77.16 的额外 -8° 屈伸平面校准。
    // 建议测试顺序：B_HandLike -> C_YXZ -> D_ZYX -> E_XZY -> F_ZXY -> A_Current
    public static LeftThighMapMode CurrentLeftThighMapMode = LeftThighMapMode.F_ZXY;

    private readonly int count;
    private Quaternion[] offsets;
    private Quaternion[] targets;
    private Quaternion[] worldOffsets;
    private bool calibrated;
    private bool useSmoothing;
    private float smoothSpeed;
    private float minAngleThreshold;
    private bool useTwistSwing;
    private bool limitsEnabled = false;
    private int[] parentIndices;
    private Vector3[] cachedMinAngles;
    private Vector3[] cachedMaxAngles;
    private Quaternion[] cachedRestLocals;
    private bool constraintsReady = false;
    private Vector3[] twistAxes;
	private static readonly Vector3 LThighPostEuler = new Vector3(0f, -45f, 0f);
	private static readonly Vector3 RThighPostEuler = new Vector3(0f,  45f, 0f);

    public RotationDriver(
        int count,
        bool useSmoothing = true,
        float smoothSpeed = 10f,
        float minAngleThreshold = 0f,
        bool useTwistSwing = true,
        int[] parentIndices = null)
    {
        this.count = count;
        this.useSmoothing = useSmoothing;
        this.smoothSpeed = smoothSpeed;
        this.minAngleThreshold = minAngleThreshold;
        this.useTwistSwing = useTwistSwing;

        offsets = new Quaternion[count];
        targets = new Quaternion[count];
        worldOffsets = new Quaternion[count];
        twistAxes = new Vector3[count];

        for (int i = 0; i < count; i++)
        {
            offsets[i] = Quaternion.identity;
            targets[i] = Quaternion.identity;
            worldOffsets[i] = Quaternion.identity;
            twistAxes[i] = Vector3.right;
        }

        this.parentIndices = InitParentIndices(count, parentIndices);

        // 前臂 twist 轴保留原先约定
        if (count > (int)BoneIndex.LeftForeArm) twistAxes[(int)BoneIndex.LeftForeArm] = Vector3.left;
        if (count > (int)BoneIndex.RightForeArm) twistAxes[(int)BoneIndex.RightForeArm] = Vector3.right;

        calibrated = false;
    }

    public bool IsCalibrated => calibrated;
    public Quaternion[] Targets => targets;

    public void SetSmoothing(bool enabled, float speed)
    {
        useSmoothing = enabled;
        smoothSpeed = speed;
    }

    public void SetTwistSwing(bool enabled) => useTwistSwing = enabled;
    public void SetMinAngleThreshold(float degrees) => minAngleThreshold = degrees;
    public void SetLimitsEnabled(bool enabled) => limitsEnabled = enabled;

    public void SetConstraints(Vector3[] minAngles, Vector3[] maxAngles, Quaternion[] restLocals)
    {
        if (minAngles == null || maxAngles == null || restLocals == null)
        {
            constraintsReady = false;
            return;
        }

        if (minAngles.Length != count || maxAngles.Length != count || restLocals.Length != count)
        {
            constraintsReady = false;
            return;
        }

        cachedMinAngles = (Vector3[])minAngles.Clone();
        cachedMaxAngles = (Vector3[])maxAngles.Clone();
        cachedRestLocals = (Quaternion[])restLocals.Clone();
        constraintsReady = true;
    }

    /// <summary>
    /// 传感器四元数 -> Unity 空间映射。
    /// 所有 IMU 必须先进入同一个世界坐标系，才能进行可靠的父子相对旋转计算。
    /// </summary>
    public static Quaternion MapSensorToUnity(int index, Quaternion sensorQ)
    {
        // V69：恢复 V57/上传包中已经用于大腿测试的“按部位独立映射”。
        // 手臂保持已验证的 LHand 映射；左、右大腿分别使用各自的安装轴映射。
        switch (index)
        {
            case (int)BoneIndex.LeftArm:
            case (int)BoneIndex.LeftForeArm:
                // 左臂已经通过实测：继续保留原 LHand 硬件坐标转换。
                return ConvertSensorToUnreal_LHand(
                    sensorQ.x, sensorQ.y, sensorQ.z, sensorQ.w).normalized;

            case (int)BoneIndex.RightArm:
                // V8.2：V8.1的(+y,-z,+x,w)包含反射，四组动作中表现为能动但平面不标准。
                // 修正为合法正交旋转基(+y,-z,-x,w)。
                return ConvertSensorToUnity_RightUpperArmV7(
                    sensorQ.x, sensorQ.y, sensorQ.z, sensorQ.w).normalized;

            case (int)BoneIndex.RightForeArm:
                // 本轮右小臂仍锁定；保留旧输入转换，避免提前改变下一阶段肘关节测试基线。
                return ConvertSensorToUnreal_LHand(
                    sensorQ.x, sensorQ.y, sensorQ.z, sensorQ.w).normalized;

            case (int)BoneIndex.Spine:
                return ConvertSensorToUnreal_Rib(
                    sensorQ.x, sensorQ.y, sensorQ.z, sensorQ.w).normalized;

            case (int)BoneIndex.LeftUpLeg:
                return ConvertSensorToUnreal_LThigh(
                    sensorQ.x, sensorQ.y, sensorQ.z, sensorQ.w).normalized;

            case (int)BoneIndex.LeftLeg:
                return ConvertSensorToUnreal_LCalf(
                    sensorQ.x, sensorQ.y, sensorQ.z, sensorQ.w).normalized;

            case (int)BoneIndex.RightUpLeg:
                // V77.23：左右大腿传感器正面、接口和指示灯方向一致。
                // 恢复V77.3/V77.5已验证基线：右大腿与左大腿使用同一F_ZXY硬件轴，
                // 左右解剖差异由各自骨骼Rest、独立标定及右侧横向镜像处理。
                return ConvertSensorToUnreal_LThigh(
                    sensorQ.x, sensorQ.y, sensorQ.z, sensorQ.w).normalized;

            case (int)BoneIndex.RightLeg:
                return ConvertSensorToUnreal_RCalf(
                    sensorQ.x, sensorQ.y, sensorQ.z, sensorQ.w).normalized;

            default:
                return ConvertSensorToUnreal_LHand(
                    sensorQ.x, sensorQ.y, sensorQ.z, sensorQ.w).normalized;
        }
    }



    /// <summary>
    /// V8.2右上臂硬件正交坐标基：(+y,-z,-x,w)。
    /// V8.1使用(+y,-z,+x,w)，其三轴矩阵行列式为-1，属于反射而不是合法旋转基，
    /// 会导致前伸/平举/上举平面互相串扰。根据V8.1四组同步动作实测，将第三轴改为-x后
    /// 恢复为行列式+1的正交旋转基，并与01号左大臂的骨段长轴方向保持一致。
    /// </summary>
    private static Quaternion ConvertSensorToUnity_RightUpperArmV7(float x, float y, float z, float w)
    {
        Quaternion output;
        output.x =  y;
        output.y = -z;
        output.z = -x;
        output.w =  w;
        return output;
    }

    /// <summary>
    /// V77.18/V77.23旧右上臂独立硬件坐标转换：(+x,-z,+y,w)，V77.23已停用。
    /// V77.17 的 (+y,-z,+x,w) 已让平举接近正确，但把上举映射成前伸、
    /// 把自然下垂映射到高举区域；本版交换第一/第三输出轴，保留第二轴。
    /// 该转换只修正传感器坐标基，之后仍由右大臂实时四元数连续驱动。
    /// </summary>
    private static Quaternion ConvertSensorToUnity_RightUpperArmV7718(float x, float y, float z, float w)
    {
        Quaternion output;
        output.x =  x;
        output.y = -z;
        output.z =  y;
        output.w =  w;
        return output;
    }

    // =========================
    // V77.15 右上臂专用硬件轴转换（V77.16已停用，仅保留源码对照）
    // =========================
    // 原始左臂转换为 (-y,-z,+x,w)。V77.14 右臂在该转换后又叠加 MirrorAvatarX，
    // 实测导致肩关节的前后轴、上下轴与侧向轴串扰。本转换把右上臂固定解释为
    // (-z,+y,+x,w)，属于合法的正交坐标基变换（不是动作预设），并由标定自动抵消
    // 传感器在手臂上的固定佩戴角。
    private static Quaternion ConvertSensorToUnity_RightUpperArmV7715_Legacy(float x, float y, float z, float w)
    {
        Quaternion output;
        output.x = -z;
        output.y =  y;
        output.z =  x;
        output.w =  w;
        return output;
    }

    private static Quaternion ConvertSensorToUnreal_LHand(float x, float y, float z, float w)
    {
        Quaternion output;
        output.x = -y;
        output.y = -z;
        output.z = x;
        output.w = w;
        return output;
    }

    private static Quaternion ConvertSensorToUnreal_RHand(float x, float y, float z, float w)
    {
        Quaternion output;
        output.x = -y;
        output.y = -z;
        output.z = x;
        output.w = w;
        return output;
    }

    private static Quaternion ConvertSensorToUnreal_Rib(float x, float y, float z, float w)
    {
        Quaternion output;
        output.x = -y;
        output.y = -z;
        output.z = -x;
        output.w = -w;
        return output;
    }

    // =========================
    // 腿部映射（先拆开入口；当前先给出“保守稳定版”）
    // 后续若还需细调，只改这四个函数即可，不会再影响手臂/脊柱
    // =========================

    private static Quaternion ConvertSensorToUnreal_LThigh(float x, float y, float z, float w)
    {
        Quaternion output = Quaternion.identity;

        switch (CurrentLeftThighMapMode)
        {
            case LeftThighMapMode.A_Current:
                // 旧左大腿映射。保留用于回退对比。
                output.x = -x;
                output.y = -y;
                output.z = -z;
                output.w =  w;
                break;

            case LeftThighMapMode.B_HandLike:
                // 与当前手部映射一致的候选。
                // 如果旧腿部映射出现“前踢/外展串轴”，这一组通常最值得先测。
                output.x = -y;
                output.y = -z;
                output.z =  x;
                output.w =  w;
                break;

            case LeftThighMapMode.C_YXZ:
                // 交换 X/Y，同时保留 Z。用于测试前后轴与内外展轴互换的情况。
                output.x = -y;
                output.y = -x;
                output.z =  z;
                output.w =  w;
                break;

            case LeftThighMapMode.D_ZYX:
                // Z/Y/X 交换。用于测试前踢被映射到侧踢或后侧踢的情况。
                output.x = -z;
                output.y = -y;
                output.z =  x;
                output.w =  w;
                break;

            case LeftThighMapMode.E_XZY:
                // X/Z/Y 交换。用于测试外展接近正确但前后方向反的情况。
                output.x = -x;
                output.y = -z;
                output.z =  y;
                output.w =  w;
                break;

            case LeftThighMapMode.F_ZXY:
                // Z/X/Y 交换。用于测试下蹲/前踢无反应但侧向动作明显的情况。
                output.x = -z;
                output.y = -x;
                output.z =  y;
                output.w =  w;
                break;

            case LeftThighMapMode.G_HandLikeFlipW:
                // B 的符号备选。q 与 -q 等价，但在后续半球连续性处理前，有时这个版本更稳。
                output.x =  y;
                output.y =  z;
                output.z = -x;
                output.w = -w;
                break;

            case LeftThighMapMode.H_CurrentFlipW:
                // A 的符号备选。
                output.x =  x;
                output.y =  y;
                output.z =  z;
                output.w = -w;
                break;
        }

        return output.normalized;
    }

    private static Quaternion ConvertSensorToUnreal_LCalf(float x, float y, float z, float w)
    {
        // 小腿入口独立出来，当前先保守跟旧左腿一致
        Quaternion output;
        output.x = -x;
        output.y = -y;
        output.z = -z;
        output.w = w;
        return output;
    }

    private static Quaternion ConvertSensorToUnreal_RThigh(float x, float y, float z, float w)
    {
        Quaternion output;
        output.x = x;
        output.y = -y;
        output.z = z;
        output.w = w;
        return output;
    }

    private static Quaternion ConvertSensorToUnreal_RCalf(float x, float y, float z, float w)
    {
        Quaternion output;
        output.x = x;
        output.y = -y;
        output.z = z;
        output.w = w;
        return output;
    }

    public void Calibrate(GameObject[] bones, Quaternion[] sensorQuats)
    {
        if (bones == null || sensorQuats == null) return;
        if (bones.Length < count || sensorQuats.Length < count) return;

        for (int i = 0; i < count; i++)
        {
            var bone = bones[i];
            if (bone == null) continue;

            int parentIdx = parentIndices[i];

            if (parentIdx >= 0 && parentIdx < count)
            {
                Quaternion parentSensor = sensorQuats[parentIdx];
                Quaternion childSensor = sensorQuats[i];
                Quaternion relativeSensor = Quaternion.Inverse(parentSensor) * childSensor;

                offsets[i] = (Quaternion.Inverse(relativeSensor) * bone.transform.localRotation).normalized;
            }
            else
            {
                offsets[i] = (Quaternion.Inverse(sensorQuats[i]) * bone.transform.localRotation).normalized;
            }

            worldOffsets[i] = Quaternion.identity;
        }

        calibrated = true;
    }

    public void UpdateTargets(Quaternion[] sensorQuats)
    {
        if (!calibrated || sensorQuats == null) return;
        if (sensorQuats.Length < count) return;

        for (int i = 0; i < count; i++)
        {
            int parentIdx = parentIndices[i];
            Quaternion target;

            if (parentIdx >= 0 && parentIdx < count)
            {
                Quaternion parentSensor = sensorQuats[parentIdx];
                Quaternion childSensor = sensorQuats[i];
                Quaternion relativeSensor = Quaternion.Inverse(parentSensor) * childSensor;

                target = (relativeSensor * offsets[i]).normalized;
            }
            else
            {
                target = (sensorQuats[i] * offsets[i]).normalized;
            }

            // 小腿在目标阶段先压成单轴铰链，避免一上来就 3D 乱扭
            if (constraintsReady && IsCalfBone(i))
            {
                target = ProjectCalfToHingeX(
                    target,
                    cachedRestLocals[i],
                    cachedMinAngles[i],
                    cachedMaxAngles[i]);
            }

            targets[i] = target;
        }
    }

    public void Apply(GameObject[] bones)
    {
        if (!calibrated || bones == null) return;
        if (bones.Length < count) return;

        for (int i = 0; i < count; i++)
        {
            var bone = bones[i];
            if (bone == null) continue;

            Quaternion current = bone.transform.localRotation;
            Quaternion target = targets[i].normalized;

            target = ShortestArcTarget(current, target);

            if (limitsEnabled && constraintsReady)
            {
                if (IsCalfBone(i))
                {
                    // 小腿固定按单轴铰链限幅
                    target = ClampQuaternionByCalfHinge(
                        target,
                        cachedRestLocals[i],
                        cachedMinAngles[i],
                        cachedMaxAngles[i]);
                }
                else if (useTwistSwing && (i == (int)BoneIndex.LeftForeArm || i == (int)BoneIndex.RightForeArm))
                {
                    target = ClampQuaternionByTwistSwing(
                        target,
                        cachedRestLocals[i],
                        cachedMinAngles[i],
                        cachedMaxAngles[i],
                        twistAxes[i]);
                }
                else
                {
                    target = ClampQuaternionByEuler(
                        target,
                        cachedRestLocals[i],
                        cachedMinAngles[i],
                        cachedMaxAngles[i]);
                }

                target = ShortestArcTarget(current, target);
            }

            float angle = Quaternion.Angle(current, target);

            if (angle <= minAngleThreshold)
            {
                bone.transform.localRotation = target;
                continue;
            }

            if (useSmoothing)
            {
                float t = 1f - Mathf.Exp(-smoothSpeed * Time.deltaTime);
                Quaternion newRot = Quaternion.Slerp(current, target, t).normalized;
                bone.transform.localRotation = newRot;
            }
            else
            {
                bone.transform.localRotation = target;
            }
        }
    }

    public void ResetToRestPose(GameObject[] bones, Quaternion[] restLocalRotations)
    {
        if (bones == null || restLocalRotations == null) return;
        if (bones.Length < count || restLocalRotations.Length < count) return;

        for (int i = 0; i < count; i++)
        {
            if (bones[i] != null)
                bones[i].transform.localRotation = restLocalRotations[i];

            offsets[i] = Quaternion.identity;
            worldOffsets[i] = Quaternion.identity;
            targets[i] = Quaternion.identity;
        }

        calibrated = false;
    }

    private bool IsCalfBone(int i)
    {
        return i == (int)BoneIndex.LeftLeg || i == (int)BoneIndex.RightLeg;
    }

    /// <summary>
    /// 小腿单轴化：只保留相对 rest 的 X 轴屈伸，Y/Z 清零。
    /// </summary>
    private Quaternion ProjectCalfToHingeX(
        Quaternion q,
        Quaternion rest,
        Vector3 minAngles,
        Vector3 maxAngles)
    {
        Quaternion relative = Quaternion.Inverse(rest) * q;
        Vector3 e = relative.eulerAngles;

        e.x = NormalizeAngle(e.x);
        e.y = NormalizeAngle(e.y);
        e.z = NormalizeAngle(e.z);

        // 只保留膝盖弯曲主轴
        float hingeX = Mathf.Clamp(e.x, minAngles.x, maxAngles.x);
        Quaternion clampedRelative = Quaternion.Euler(hingeX, 0f, 0f);
        return (rest * clampedRelative).normalized;
    }

    private Quaternion ClampQuaternionByCalfHinge(
        Quaternion q,
        Quaternion rest,
        Vector3 minAngles,
        Vector3 maxAngles)
    {
        Quaternion relative = Quaternion.Inverse(rest) * q;
        Vector3 e = relative.eulerAngles;

        e.x = NormalizeAngle(e.x);

        float hingeX = Mathf.Clamp(e.x, minAngles.x, maxAngles.x);
        Quaternion clampedRelative = Quaternion.Euler(hingeX, 0f, 0f);
        return (rest * clampedRelative).normalized;
    }

    private Quaternion ClampQuaternionByEuler(
        Quaternion q,
        Quaternion rest,
        Vector3 minAngles,
        Vector3 maxAngles)
    {
        Quaternion relative = Quaternion.Inverse(rest) * q;
        Vector3 e = relative.eulerAngles;

        e.x = NormalizeAngle(e.x);
        e.y = NormalizeAngle(e.y);
        e.z = NormalizeAngle(e.z);

        Vector3 clamped = new Vector3(
            Mathf.Clamp(e.x, minAngles.x, maxAngles.x),
            Mathf.Clamp(e.y, minAngles.y, maxAngles.y),
            Mathf.Clamp(e.z, minAngles.z, maxAngles.z)
        );

        Quaternion clampedRelative = Quaternion.Euler(clamped);
        return (rest * clampedRelative).normalized;
    }

    private Quaternion ClampQuaternionByTwistSwing(
        Quaternion q,
        Quaternion rest,
        Vector3 minAngles,
        Vector3 maxAngles,
        Vector3 twistAxis)
    {
        Quaternion relative = Quaternion.Inverse(rest) * q;
        Quaternion twist = ExtractTwist(relative, twistAxis);
        Quaternion swing = relative * Quaternion.Inverse(twist);

        twist.ToAngleAxis(out float twistAngle, out Vector3 axisOut);
        if (axisOut.sqrMagnitude > 1e-8f && Vector3.Dot(axisOut, twistAxis) < 0f) twistAngle = -twistAngle;
        twistAngle = NormalizeAngle(twistAngle);

        float clampedTwistAngle = Mathf.Clamp(twistAngle, minAngles.x, maxAngles.x);
        Quaternion clampedTwist = Quaternion.AngleAxis(clampedTwistAngle, twistAxis);

        float swingAngle = Quaternion.Angle(Quaternion.identity, swing);
        float maxSwing = Mathf.Max(Mathf.Abs(maxAngles.y), Mathf.Abs(maxAngles.z));

        if (swingAngle > maxSwing && swingAngle > 0.0001f)
        {
            swing.ToAngleAxis(out _, out Vector3 rawAxis);
            if (rawAxis.sqrMagnitude > 1e-8f)
                swing = Quaternion.AngleAxis(maxSwing, rawAxis);
        }

        Quaternion clampedRelative = (swing * clampedTwist).normalized;
        return (rest * clampedRelative).normalized;
    }

    private Quaternion ExtractTwist(Quaternion q, Vector3 axis)
    {
        axis = axis.normalized;
        Vector3 v = new Vector3(q.x, q.y, q.z);
        Vector3 proj = Vector3.Project(v, axis);
        Quaternion twist = new Quaternion(proj.x, proj.y, proj.z, q.w);
        return NormalizeQuaternion(twist);
    }

    private Quaternion NormalizeQuaternion(Quaternion q)
    {
        float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (mag < 1e-8f) return Quaternion.identity;
        float inv = 1f / mag;
        return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
    }

    private static int[] InitParentIndices(int count, int[] provided)
    {
        if (provided != null && provided.Length == count)
            return (int[])provided.Clone();

        int[] defaults = new int[count];
        for (int i = 0; i < count; i++) defaults[i] = -1;

        if (count > (int)BoneIndex.Spine) defaults[(int)BoneIndex.Spine] = -1;

        if (count > (int)BoneIndex.LeftArm) defaults[(int)BoneIndex.LeftArm] = (int)BoneIndex.Spine;
        if (count > (int)BoneIndex.LeftForeArm) defaults[(int)BoneIndex.LeftForeArm] = (int)BoneIndex.LeftArm;

        if (count > (int)BoneIndex.RightArm) defaults[(int)BoneIndex.RightArm] = (int)BoneIndex.Spine;
        if (count > (int)BoneIndex.RightForeArm) defaults[(int)BoneIndex.RightForeArm] = (int)BoneIndex.RightArm;

        // 没有 pelvis 传感器时，先让大腿相对脊柱，稳定性比 world 驱动更好
        if (count > (int)BoneIndex.LeftUpLeg) defaults[(int)BoneIndex.LeftUpLeg] = (int)BoneIndex.Spine;
        if (count > (int)BoneIndex.LeftLeg) defaults[(int)BoneIndex.LeftLeg] = (int)BoneIndex.LeftUpLeg;

        if (count > (int)BoneIndex.RightUpLeg) defaults[(int)BoneIndex.RightUpLeg] = (int)BoneIndex.Spine;
        if (count > (int)BoneIndex.RightLeg) defaults[(int)BoneIndex.RightLeg] = (int)BoneIndex.RightUpLeg; // 修复原来的自指错误

        return defaults;
    }

    private float NormalizeAngle(float a)
    {
        return Mathf.Repeat(a + 180f, 360f) - 180f;
    }

    private static Quaternion ShortestArcTarget(Quaternion current, Quaternion target)
    {
        if (Quaternion.Dot(current, target) < 0f)
            return new Quaternion(-target.x, -target.y, -target.z, -target.w);
        return target;
    }
}

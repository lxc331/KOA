using System;
using UnityEngine;

/// <summary>
/// 只修正右腿最终驱动目标：
/// 1) 右大腿保留前后摆动，只镜像横向增量并限制绕大腿长轴的扭转；
/// 2) 右小腿使用四元数 swing-twist 分解，只保留膝关节铰链轴旋转。
///
/// RotationDriver 对四肢的 Targets 使用世界旋转，本类在骨骼局部空间完成约束后，
/// 必须再转换回世界旋转写入 Targets。
/// </summary>
public sealed class RightLegPoseConstraint
{
    public const int RightThighIndex = 7;
    public const int RightCalfIndex = 8;

    private readonly MotionCaptureConfig config;

    private bool thighCalibrated;
    private bool calfCalibrated;
    private Quaternion thighRestLocal = Quaternion.identity;
    private Quaternion calfRestLocal = Quaternion.identity;
    private Quaternion thighSensorToWorldOffset = Quaternion.identity;
    private Quaternion calfSensorToBoneOffset = Quaternion.identity;

    // RotationDriver 的四肢目标是世界旋转；保存父节点用于 local <-> world 转换。
    private Transform thighTransform;
    private Transform thighParent;
    private Transform calfParent;

    // 大腿骨骼长轴（大腿局部空间）、站姿方向和角色横向轴（大腿父节点空间）。
    private Vector3 thighLongAxisLocal = Vector3.up;
    private Vector3 thighRestDirectionParent = Vector3.down;
    private Vector3 thighLateralAxisParent = Vector3.right;

    private Quaternion lastCalfTarget = Quaternion.identity;

    public bool IsCalibrated => thighCalibrated && calfCalibrated;
    public bool IsThighCalibrated => thighCalibrated;
    public bool IsCalfCalibrated => calfCalibrated;
    public float CurrentKneeFlexionDeg { get; private set; }
    public float CurrentKneeOffAxisDeg { get; private set; }
    public float CurrentThighRawTwistDeg { get; private set; }
    public float CurrentThighLimitedTwistDeg { get; private set; }
    public bool IsRightLegInputFresh { get; private set; }

    public RightLegPoseConstraint(MotionCaptureConfig config)
    {
        this.config = config;
    }

    /// <summary>
    /// 分阶段记录站立零位：08 在线即可先标定并保护右大腿；
    /// 09 稍后上线时再追加右小腿相对 08 的零位，不能让 09 缺失禁用整条右腿保护。
    /// </summary>
    public bool TryCalibrate(
        Quaternion[] sensorRotations,
        GameObject[] bones,
        Quaternion[] restLocalRotations,
        Quaternion avatarRootRotation,
        bool rightThighHasData,
        bool rightCalfHasData)
    {
        if (!rightThighHasData ||
            sensorRotations == null || sensorRotations.Length <= RightCalfIndex ||
            bones == null || bones.Length <= RightCalfIndex ||
            restLocalRotations == null || restLocalRotations.Length <= RightCalfIndex)
            return false;

        Transform thigh = bones[RightThighIndex] != null
            ? bones[RightThighIndex].transform
            : null;
        Transform calf = bones[RightCalfIndex] != null
            ? bones[RightCalfIndex].transform
            : null;
        if (thigh == null || calf == null)
            return false;

        Quaternion thighSensor = NormalizeSafe(sensorRotations[RightThighIndex]);
        if (!IsFinite(thighSensor))
            return false;

        if (!thighCalibrated)
        {
            thighRestLocal = NormalizeSafe(restLocalRotations[RightThighIndex]);
            calfRestLocal = NormalizeSafe(restLocalRotations[RightCalfIndex]);
            thighTransform = thigh;
            thighParent = thigh.parent;
            calfParent = calf.parent;

            // 08 可能晚于全局预校准上线，因此不能依赖 RotationDriver 当时记录的偏移。
            // 用当前 08 姿态单独对齐到绑定姿态的世界旋转，保证首次接入不会跳转。
            Quaternion thighRestWorld = NormalizeSafe(
                (thighParent != null ? thighParent.rotation : Quaternion.identity) * thighRestLocal);
            thighSensorToWorldOffset = NormalizeSafe(
                Quaternion.Inverse(thighSensor) * thighRestWorld);

            Vector3 thighToCalfWorld = calf.position - thigh.position;
            if (!IsFinite(thighToCalfWorld) || thighToCalfWorld.sqrMagnitude < 0.000001f)
                thighToCalfWorld = thigh.rotation * Vector3.down;

            Vector3 directionParent = thigh.parent != null
                ? thigh.parent.InverseTransformDirection(thighToCalfWorld)
                : thighToCalfWorld;
            thighRestDirectionParent = SafeDirection(directionParent, Vector3.down);

            thighLongAxisLocal = SafeDirection(
                Quaternion.Inverse(thighRestLocal) * thighRestDirectionParent,
                Vector3.up);

            Vector3 avatarRightWorld = avatarRootRotation * Vector3.right;
            Vector3 lateralParent = thigh.parent != null
                ? thigh.parent.InverseTransformDirection(avatarRightWorld)
                : avatarRightWorld;

            // 横向轴必须与站姿大腿方向正交，避免镜像横移时改变腿长或站姿零位。
            lateralParent = Vector3.ProjectOnPlane(lateralParent, thighRestDirectionParent);
            if (!IsFinite(lateralParent) || lateralParent.sqrMagnitude < 0.000001f)
                lateralParent = Vector3.ProjectOnPlane(Vector3.right, thighRestDirectionParent);
            thighLateralAxisParent = SafeDirection(lateralParent, Vector3.right);

            lastCalfTarget = calfRestLocal;
            thighCalibrated = true;
        }

        if (rightCalfHasData && !calfCalibrated)
        {
            Quaternion calfSensor = NormalizeSafe(sensorRotations[RightCalfIndex]);
            if (IsFinite(calfSensor))
            {
                // 与 RotationDriver 的小腿标定公式保持一致：
                // target = currentRelativeSensor * (inverse(referenceRelativeSensor) * restLocal)。
                Quaternion referenceRelativeSensor = NormalizeSafe(
                    Quaternion.Inverse(thighSensor) * calfSensor);
                calfSensorToBoneOffset = NormalizeSafe(
                    Quaternion.Inverse(referenceRelativeSensor) * calfRestLocal);
                calfCalibrated = true;
            }
        }

        IsRightLegInputFresh = thighCalibrated;
        return thighCalibrated;
    }

    /// <summary>
    /// 在 RotationDriver.UpdateTargets 之后调用，直接约束其08/09最终目标。
    /// </summary>
    public void ConstrainTargets(
        Quaternion[] sensorRotations,
        Quaternion[] driverTargets,
        DateTime rightThighTimestamp,
        DateTime rightCalfTimestamp,
        float deltaTime)
    {
        if (!thighCalibrated || sensorRotations == null || driverTargets == null ||
            sensorRotations.Length <= RightCalfIndex || driverTargets.Length <= RightCalfIndex)
            return;

        // RotationDriver.Targets[四肢] 是世界旋转。先转到大腿局部空间做约束，
        // 再转回世界空间，否则把 localRotation 当 worldRotation 写入会让整条腿扭转。
        Quaternion thighSensor = NormalizeSafe(sensorRotations[RightThighIndex]);
        Quaternion thighParentWorld = thighParent != null
            ? thighParent.rotation
            : Quaternion.identity;
        Quaternion unconstrainedThighWorldTarget = NormalizeSafe(
            thighSensor * thighSensorToWorldOffset);
        Quaternion originalThighLocalTarget = NormalizeSafe(
            Quaternion.Inverse(thighParentWorld) * unconstrainedThighWorldTarget);
        Quaternion constrainedThighLocalTarget = ConstrainRightThighLocal(
            originalThighLocalTarget);
        Quaternion constrainedThighWorldTarget = NormalizeSafe(
            thighParentWorld * constrainedThighLocalTarget);
        driverTargets[RightThighIndex] = constrainedThighWorldTarget;

        DateTime pairTimestamp = rightThighTimestamp <= rightCalfTimestamp
            ? rightThighTimestamp
            : rightCalfTimestamp;
        double ageSeconds = pairTimestamp == DateTime.MinValue
            ? double.PositiveInfinity
            : Math.Max(0d, (DateTime.Now - pairTimestamp).TotalSeconds);

        Quaternion calfSensor = NormalizeSafe(sensorRotations[RightCalfIndex]);
        bool inputValid = calfCalibrated && IsFinite(thighSensor) && IsFinite(calfSensor) &&
            ageSeconds <= Math.Max(0.1f, config.rightLegInputFreshnessSeconds);

        if (inputValid)
        {
            lastCalfTarget = BuildStrictHingeTarget(thighSensor, calfSensor);
            IsRightLegInputFresh = true;
        }
        else
        {
            // 09 从未上线时立即保持绑定姿态；曾上线但数据变陈旧时平滑回正。
            float t = calfCalibrated
                ? 1f - Mathf.Exp(
                    -Mathf.Max(0.01f, config.rightLegReturnToNeutralSpeed) *
                    Mathf.Max(0f, deltaTime))
                : 1f;
            lastCalfTarget = NormalizeSafe(Quaternion.Slerp(lastCalfTarget, calfRestLocal, t));
            CurrentKneeFlexionDeg = Mathf.Lerp(CurrentKneeFlexionDeg, 0f, t);
            CurrentKneeOffAxisDeg = Mathf.Lerp(CurrentKneeOffAxisDeg, 0f, t);
            IsRightLegInputFresh = false;
        }

        // 小腿约束结果同样是 localRotation。若小腿直接挂在大腿下，必须使用本帧
        // 已约束的大腿世界目标，而不是尚未 Apply 的上一帧 thigh.rotation。
        Quaternion calfParentWorld = calfParent == thighTransform
            ? constrainedThighWorldTarget
            : (calfParent != null ? calfParent.rotation : Quaternion.identity);
        driverTargets[RightCalfIndex] = NormalizeSafe(
            calfParentWorld * lastCalfTarget);
    }

    public void Reset()
    {
        thighCalibrated = false;
        calfCalibrated = false;
        thighRestLocal = Quaternion.identity;
        calfRestLocal = Quaternion.identity;
        thighSensorToWorldOffset = Quaternion.identity;
        calfSensorToBoneOffset = Quaternion.identity;
        thighTransform = null;
        thighParent = null;
        calfParent = null;
        thighLongAxisLocal = Vector3.up;
        thighRestDirectionParent = Vector3.down;
        thighLateralAxisParent = Vector3.right;
        lastCalfTarget = Quaternion.identity;
        CurrentKneeFlexionDeg = 0f;
        CurrentKneeOffAxisDeg = 0f;
        CurrentThighRawTwistDeg = 0f;
        CurrentThighLimitedTwistDeg = 0f;
        IsRightLegInputFresh = false;
    }

    private Quaternion ConstrainRightThighLocal(Quaternion originalTarget)
    {
        originalTarget = NormalizeSafe(originalTarget);

        Vector3 originalDirectionParent = SafeDirection(
            originalTarget * thighLongAxisLocal,
            thighRestDirectionParent);

        // 先从原始目标中分离出“改变腿方向的 swing”和“绕腿长轴的 twist”。
        Quaternion originalSwingParent = Quaternion.FromToRotation(
            thighRestDirectionParent,
            originalDirectionParent);
        Quaternion swingOnlyTarget = NormalizeSafe(originalSwingParent * thighRestLocal);
        Quaternion residualLocal = NormalizeSafe(
            Quaternion.Inverse(swingOnlyTarget) * originalTarget);
        float rawTwistDeg = ExtractSignedTwistAngle(
            residualLocal,
            thighLongAxisLocal,
            out _);
        float limitedTwistDeg = Mathf.Clamp(
            rawTwistDeg,
            -Mathf.Abs(config.rightThighMaxTwistDeg),
            Mathf.Abs(config.rightThighMaxTwistDeg));

        CurrentThighRawTwistDeg = rawTwistDeg;
        CurrentThighLimitedTwistDeg = limitedTwistDeg;

        Vector3 correctedDirectionParent = originalDirectionParent;
        if (config.rightThighInvertLateral)
        {
            // 只反转相对站姿的横向分量。前后屈伸和平面内角度保持不变。
            float lateral = Vector3.Dot(
                correctedDirectionParent,
                thighLateralAxisParent);
            correctedDirectionParent = SafeDirection(
                correctedDirectionParent - 2f * lateral * thighLateralAxisParent,
                thighRestDirectionParent);
        }

        // 不允许大腿方向接近或越过 180°。接近反向时 FromToRotation 的旋转轴不唯一，
        // 会表现为大腿拧成麻花后继续转一圈；限制摆角可从根源上避免该奇点。
        float swingAngleDeg = Vector3.Angle(
            thighRestDirectionParent,
            correctedDirectionParent);
        float maxSwingDeg = Mathf.Clamp(config.rightThighMaxSwingDeg, 1f, 179f);
        if (swingAngleDeg > maxSwingDeg)
        {
            correctedDirectionParent = SafeDirection(
                Vector3.Slerp(
                    thighRestDirectionParent,
                    correctedDirectionParent,
                    maxSwingDeg / swingAngleDeg),
                thighRestDirectionParent);
        }

        Quaternion correctedSwingParent = Quaternion.FromToRotation(
            thighRestDirectionParent,
            correctedDirectionParent);
        Quaternion limitedTwistLocal = Quaternion.AngleAxis(
            limitedTwistDeg,
            thighLongAxisLocal);

        return NormalizeSafe(
            correctedSwingParent * thighRestLocal * limitedTwistLocal);
    }

    private Quaternion BuildStrictHingeTarget(
        Quaternion thighSensor,
        Quaternion calfSensor)
    {
        Quaternion relativeSensor = NormalizeSafe(
            Quaternion.Inverse(thighSensor) * calfSensor);
        Quaternion unrestrictedCalfTarget = NormalizeSafe(
            relativeSensor * calfSensorToBoneOffset);
        Quaternion deltaFromRest = NormalizeSafe(
            Quaternion.Inverse(calfRestLocal) * unrestrictedCalfTarget);

        Vector3 hingeAxis = SafeDirection(
            config.rightKneeHingeAxisLocal,
            Vector3.right);
        float hingeAngleDeg = ExtractSignedTwistAngle(
            deltaFromRest,
            hingeAxis,
            out Quaternion twist);

        Quaternion swing = NormalizeSafe(
            deltaFromRest * Quaternion.Inverse(twist));
        CurrentKneeOffAxisDeg = Quaternion.Angle(Quaternion.identity, swing);

        if (Mathf.Abs(hingeAngleDeg) < Mathf.Max(0f, config.rightKneeNeutralDeadZoneDeg))
            hingeAngleDeg = 0f;

        float minimum = -40f;
        float maximum = 40f;
        if (config.minLocalAngles != null && config.minLocalAngles.Length > RightCalfIndex)
            minimum = config.minLocalAngles[RightCalfIndex].x;
        if (config.maxLocalAngles != null && config.maxLocalAngles.Length > RightCalfIndex)
            maximum = config.maxLocalAngles[RightCalfIndex].x;
        if (minimum > maximum)
        {
            float swap = minimum;
            minimum = maximum;
            maximum = swap;
        }

        hingeAngleDeg = Mathf.Clamp(hingeAngleDeg, minimum, maximum);
        CurrentKneeFlexionDeg = hingeAngleDeg;

        // 严格单轴输出：所有 swing/外翻/横甩/轴向串扰均不进入小腿目标。
        return NormalizeSafe(
            calfRestLocal * Quaternion.AngleAxis(hingeAngleDeg, hingeAxis));
    }

    /// <summary>
    /// 四元数 swing-twist 分解中的 twist 提取；全程不转换为欧拉角。
    /// </summary>
    private static float ExtractSignedTwistAngle(
        Quaternion rotation,
        Vector3 axis,
        out Quaternion twist)
    {
        rotation = NormalizeSafe(rotation);
        axis = SafeDirection(axis, Vector3.right);

        Vector3 vector = new Vector3(rotation.x, rotation.y, rotation.z);
        Vector3 projected = Vector3.Project(vector, axis);
        twist = NormalizeSafe(new Quaternion(
            projected.x,
            projected.y,
            projected.z,
            rotation.w));

        // q 与 -q 表示同一旋转；统一到 w>=0 可避免角度在±180°附近跳支。
        if (twist.w < 0f)
            twist = new Quaternion(-twist.x, -twist.y, -twist.z, -twist.w);

        float signedSinHalf = Vector3.Dot(
            new Vector3(twist.x, twist.y, twist.z),
            axis);
        float angle = 2f * Mathf.Atan2(signedSinHalf, twist.w) * Mathf.Rad2Deg;
        return Mathf.Repeat(angle + 180f, 360f) - 180f;
    }

    private static Quaternion NormalizeSafe(Quaternion q)
    {
        float magnitude = Mathf.Sqrt(
            q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (!IsFinite(magnitude) || magnitude < 0.000001f)
            return Quaternion.identity;
        float inverse = 1f / magnitude;
        return new Quaternion(
            q.x * inverse,
            q.y * inverse,
            q.z * inverse,
            q.w * inverse);
    }

    private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
    {
        if (!IsFinite(value) || value.sqrMagnitude < 0.000001f)
            value = fallback;
        return value.normalized;
    }

    private static bool IsFinite(Quaternion q)
    {
        return IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w);
    }

    private static bool IsFinite(Vector3 v)
    {
        return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

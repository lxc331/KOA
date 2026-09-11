using UnityEngine;

/// <summary>
/// 将 06-09 惯性传感器映射为双腿的解剖运动。
///
/// V1.6 的三维膝角算法对右腿有效，但让左腿在伸直时仍残留约 70°，
/// 并直接导致左小腿人物模型无法完全抬起。本版恢复 d967ffa 时左腿使用的
/// 矢状面算法，只保留右腿的三维夹角算法。
/// </summary>
public sealed class LowerBodyPoseDriver
{
    public const int LeftThighIndex = 5;
    public const int LeftCalfIndex = 6;
    public const int RightThighIndex = 7;
    public const int RightCalfIndex = 8;

    private const int FirstIndex = LeftThighIndex;
    private const int SegmentCount = 4;

    private readonly MotionCaptureConfig config;
    private readonly bool[] calibrated = new bool[SegmentCount];
    private readonly Quaternion[] sensorSegmentAxis = new Quaternion[SegmentCount];
    private readonly Quaternion[] restLocal = new Quaternion[SegmentCount];
    private readonly Vector3[] restDirectionParent = new Vector3[SegmentCount];
    private readonly Transform[] bones = new Transform[SegmentCount];
    private readonly Transform[] parents = new Transform[SegmentCount];
    private readonly float[] thighFlexionDeg = new float[2];
    private readonly float[] kneeFlexionDeg = new float[2];

    private readonly bool[] hasSeatedCalibration = new bool[2];
    private readonly float[] kneeRawReadyDeg = new float[2];
    private readonly float[] kneeRawStraightDeg = new float[2];
    private readonly float[] thighRawSeatedDeg = new float[2];
    private readonly float[] calibratedReadyKneeDeg = new float[2];
    private readonly float[] calibratedSeatedThighDeg = new float[2];

    private bool seatedTrainingVisualEnabled;
    private bool seatedTrainingVisualSwitching;
    private int selectedTrainingLeg;
    private float sharedSeatedThighDeg;
    private float sharedReadyKneeDeg;
    private float seatedBlend;

    private Quaternion avatarFacing = Quaternion.identity;
    private bool geometryReady;

    public LowerBodyPoseDriver(MotionCaptureConfig config)
    {
        this.config = config;
        Reset();
    }

    public void Reset()
    {
        geometryReady = false;
        avatarFacing = Quaternion.identity;
        for (int i = 0; i < SegmentCount; i++)
        {
            calibrated[i] = false;
            sensorSegmentAxis[i] = Quaternion.identity;
            restLocal[i] = Quaternion.identity;
            restDirectionParent[i] = Vector3.down;
            bones[i] = null;
            parents[i] = null;
        }
        thighFlexionDeg[0] = 0f;
        thighFlexionDeg[1] = 0f;
        kneeFlexionDeg[0] = 0f;
        kneeFlexionDeg[1] = 0f;
        for (int leg = 0; leg < 2; leg++)
        {
            hasSeatedCalibration[leg] = false;
            kneeRawReadyDeg[leg] = 0f;
            kneeRawStraightDeg[leg] = 0f;
            thighRawSeatedDeg[leg] = 0f;
            calibratedReadyKneeDeg[leg] = 90f;
            calibratedSeatedThighDeg[leg] = 90f;
        }
        seatedTrainingVisualEnabled = false;
        seatedTrainingVisualSwitching = false;
        selectedTrainingLeg = 0;
        sharedSeatedThighDeg = 90f;
        sharedReadyKneeDeg = 90f;
        seatedBlend = 0f;
    }

    public void ConfigureRightLegCalibration(
        float rawReadyKneeDeg,
        float rawStraightKneeDeg,
        float rawSeatedThighDeg)
    {
        ConfigureSeatedLegCalibration(
            1, rawReadyKneeDeg, rawStraightKneeDeg, rawSeatedThighDeg, 90f, 90f);
    }

    public void ClearRightLegCalibration()
    {
        ClearSeatedLegCalibration(1);
    }

    public void ConfigureSeatedLegCalibration(
        int leg,
        float rawReadyKneeDeg,
        float rawStraightKneeDeg,
        float rawSeatedThighDeg,
        float targetReadyKneeDeg,
        float targetSeatedThighDeg)
    {
        if (leg < 0 || leg > 1) return;
        float kneeSpan = rawReadyKneeDeg - rawStraightKneeDeg;
        if (float.IsNaN(kneeSpan) || float.IsInfinity(kneeSpan) || kneeSpan < 20f ||
            float.IsNaN(rawSeatedThighDeg) || float.IsInfinity(rawSeatedThighDeg) ||
            float.IsNaN(targetReadyKneeDeg) || float.IsInfinity(targetReadyKneeDeg) ||
            float.IsNaN(targetSeatedThighDeg) || float.IsInfinity(targetSeatedThighDeg))
            return;

        kneeRawReadyDeg[leg] = rawReadyKneeDeg;
        kneeRawStraightDeg[leg] = rawStraightKneeDeg;
        thighRawSeatedDeg[leg] = rawSeatedThighDeg;
        calibratedReadyKneeDeg[leg] = targetReadyKneeDeg;
        calibratedSeatedThighDeg[leg] = targetSeatedThighDeg;
        hasSeatedCalibration[leg] = true;
    }

    public void ClearSeatedLegCalibration(int leg)
    {
        if (leg < 0 || leg > 1) return;
        hasSeatedCalibration[leg] = false;
    }

    public void SetSeatedTrainingVisual(
        bool enabled,
        int selectedLeg,
        bool switching,
        float sharedThighDeg,
        float sharedKneeDeg)
    {
        seatedTrainingVisualEnabled = enabled;
        selectedTrainingLeg = Mathf.Clamp(selectedLeg, 0, 1);
        seatedTrainingVisualSwitching = switching;
        sharedSeatedThighDeg = sharedThighDeg;
        sharedReadyKneeDeg = sharedKneeDeg;
    }

    public void TryCalibrate(
        Quaternion[] sensorRotations,
        GameObject[] sourceBones,
        Quaternion[] restRotations,
        Quaternion rootFacing,
        MotionCaptureState state)
    {
        if (sensorRotations == null || sourceBones == null || restRotations == null)
            return;

        if (!geometryReady)
            PrepareGeometry(sourceBones, restRotations, rootFacing);

        for (int deviceIndex = FirstIndex; deviceIndex <= RightCalfIndex; deviceIndex++)
        {
            int slot = deviceIndex - FirstIndex;
            if (calibrated[slot] || state == null || !state.GetDeviceHasData(deviceIndex))
                continue;
            if (bones[slot] == null)
                continue;

            bool rightCalfArrivedDuringBentPose =
                deviceIndex == RightCalfIndex &&
                calibrated[LeftCalfIndex - FirstIndex] &&
                calibrated[RightThighIndex - FirstIndex] &&
                Mathf.Abs(GetSagittalFlexion(
                    RightThighIndex,
                    RightThighIndex - FirstIndex,
                    sensorRotations)) > 20f;

            if (rightCalfArrivedDuringBentPose)
            {
                sensorSegmentAxis[slot] = sensorSegmentAxis[LeftCalfIndex - FirstIndex];
            }
            else
            {
                Vector3 restWorldDirection = GetRestWorldDirection(slot);
                sensorSegmentAxis[slot] = Quaternion.FromToRotation(
                    Vector3.down,
                    Quaternion.Inverse(sensorRotations[deviceIndex]) * restWorldDirection);
            }
            calibrated[slot] = true;
        }
    }

    public void ConstrainTargets(
        Quaternion[] sensorRotations,
        Quaternion[] targets,
        bool[] inputFresh)
    {
        if (!geometryReady || sensorRotations == null || targets == null)
            return;

        // A seated visual constraint must fade before standing, not wait for a
        // fully upright pose that the constrained avatar cannot reach.
        int activeIndex = selectedTrainingLeg == 0 ? LeftThighIndex : RightThighIndex;
        float activeThigh = CorrectThighFlexion(selectedTrainingLeg,
            GetSagittalFlexion(activeIndex, activeIndex - FirstIndex, sensorRotations));
        seatedBlend = seatedTrainingVisualEnabled
            ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(35f, 75f, activeThigh))
            : 0f;

        DriveThigh(LeftThighIndex, 0, sensorRotations, targets);
        DriveThigh(RightThighIndex, 1, sensorRotations, targets);

        // 两条小腿必须彼此独立。掉线腿只保持自己的最后可信膝角，
        // 因而这里不再根据另一条腿的新鲜度改变计算顺序。
        DriveCalf(LeftCalfIndex, 0, sensorRotations, targets, inputFresh);
        DriveCalf(RightCalfIndex, 1, sensorRotations, targets, inputFresh);
    }

    /// <summary>
    /// 左腿恢复 d967ffa 的矢状面算法；右腿保留 V1.6 三维骨段夹角。
    /// 这样分别保留两侧目前实测表现更好的算法，不再强行用同一公式。
    /// </summary>
    public bool TryMeasureLeg(int leg, Quaternion[] sensorRotations,
        out float thighAngle, out float kneeAngle)
    {
        thighAngle = kneeAngle = 0f;
        if (!geometryReady || leg < 0 || leg > 1 || sensorRotations == null ||
            sensorRotations.Length <= RightCalfIndex)
            return false;

        int thighIndex = leg == 0 ? LeftThighIndex : RightThighIndex;
        int calfIndex = thighIndex + 1;
        if (!calibrated[thighIndex - FirstIndex] || !calibrated[calfIndex - FirstIndex])
            return false;

        Vector3 thighDirection = Quaternion.Inverse(avatarFacing) *
            GetMeasuredSegmentDirection(thighIndex, thighIndex - FirstIndex, sensorRotations);
        Vector3 calfDirection = Quaternion.Inverse(avatarFacing) *
            GetMeasuredSegmentDirection(calfIndex, calfIndex - FirstIndex, sensorRotations);
        if (thighDirection.y * thighDirection.y + thighDirection.z * thighDirection.z < 0.01f ||
            calfDirection.y * calfDirection.y + calfDirection.z * calfDirection.z < 0.01f)
            return false;

        float rawThighAngle = GetSagittalFlexion(
            thighIndex, thighIndex - FirstIndex, sensorRotations);

        if (leg == 0)
        {
            // 左腿恢复上一个稳定版本：伸直时可正确回到接近 0°。
            float calfAngle = GetSagittalFlexion(calfIndex, calfIndex - FirstIndex, sensorRotations);
            kneeAngle = Mathf.Abs(Mathf.DeltaAngle(rawThighAngle, calfAngle));
        }
        else
        {
            if (!TryGetRightKneeFlexion3D(sensorRotations, out kneeAngle))
                return false;
        }

        thighAngle = CorrectThighFlexion(leg, rawThighAngle);
        kneeAngle = CorrectKneeFlexion(leg, kneeAngle);

        return !float.IsNaN(thighAngle) && !float.IsInfinity(thighAngle) &&
            !float.IsNaN(kneeAngle) && !float.IsInfinity(kneeAngle);
    }

    private void PrepareGeometry(
        GameObject[] sourceBones,
        Quaternion[] restRotations,
        Quaternion rootFacing)
    {
        avatarFacing = rootFacing;

        for (int deviceIndex = FirstIndex; deviceIndex <= RightCalfIndex; deviceIndex++)
        {
            int slot = deviceIndex - FirstIndex;
            if (deviceIndex < sourceBones.Length && sourceBones[deviceIndex] != null)
            {
                bones[slot] = sourceBones[deviceIndex].transform;
                parents[slot] = bones[slot].parent;
            }
            if (deviceIndex < restRotations.Length)
                restLocal[slot] = restRotations[deviceIndex];
        }

        for (int slot = 0; slot < SegmentCount; slot++)
        {
            Vector3 worldDirection = GetRestWorldDirection(slot);
            restDirectionParent[slot] = parents[slot] != null
                ? parents[slot].InverseTransformDirection(worldDirection).normalized
                : worldDirection;
        }
        geometryReady = true;
    }

    private void DriveThigh(
        int deviceIndex,
        int leg,
        Quaternion[] sensorRotations,
        Quaternion[] targets)
    {
        int slot = deviceIndex - FirstIndex;
        if (!calibrated[slot] || deviceIndex >= targets.Length)
        {
            thighFlexionDeg[leg] = 0f;
            return;
        }

        float flexion = GetSagittalFlexion(deviceIndex, slot, sensorRotations);
        flexion = CorrectThighFlexion(leg, flexion);
        flexion = Mathf.Lerp(flexion, sharedSeatedThighDeg, seatedBlend);
        flexion = Mathf.Clamp(
            flexion,
            -Mathf.Abs(config.lowerBodyThighMaxExtensionDeg),
            Mathf.Abs(config.lowerBodyThighMaxFlexionDeg));
        thighFlexionDeg[leg] = flexion;

        float radians = flexion * Mathf.Deg2Rad;
        Vector3 desiredAvatarDirection = new Vector3(
            0f, -Mathf.Cos(radians), Mathf.Sin(radians));
        Vector3 desiredWorldDirection = avatarFacing * desiredAvatarDirection;
        Vector3 desiredParentDirection = parents[slot] != null
            ? parents[slot].InverseTransformDirection(desiredWorldDirection).normalized
            : desiredWorldDirection.normalized;

        Quaternion swing = Quaternion.FromToRotation(
            restDirectionParent[slot], desiredParentDirection);
        Quaternion targetLocal = NormalizeSafe(swing * restLocal[slot]);
        targets[deviceIndex] = parents[slot] != null
            ? NormalizeSafe(parents[slot].rotation * targetLocal)
            : targetLocal;
    }

    private void DriveCalf(
        int deviceIndex,
        int leg,
        Quaternion[] sensorRotations,
        Quaternion[] targets,
        bool[] inputFresh)
    {
        int slot = deviceIndex - FirstIndex;
        if (deviceIndex >= targets.Length || bones[slot] == null)
            return;

        bool calfIsFresh = IsFresh(deviceIndex, inputFresh);
        float kneeFlexion;
        if (calfIsFresh)
        {
            int measuredThighIndex = leg == 0 ? LeftThighIndex : RightThighIndex;
            int thighSlot = measuredThighIndex - FirstIndex;

            if (calibrated[thighSlot])
            {
                if (leg == 0)
                {
                    float thighSagittal = GetSagittalFlexion(
                        measuredThighIndex, thighSlot, sensorRotations);
                    float calfSagittal = GetSagittalFlexion(
                        deviceIndex, slot, sensorRotations);
                    kneeFlexion = Mathf.Abs(
                        Mathf.DeltaAngle(thighSagittal, calfSagittal));
                }
                else if (TryGetRightKneeFlexion3D(sensorRotations, out float rightKnee))
                {
                    kneeFlexion = rightKnee;
                }
                else
                {
                    kneeFlexion = kneeFlexionDeg[leg];
                }
            }
            else
            {
                kneeFlexion = kneeFlexionDeg[leg];
            }
        }
        else
        {
            // 传感器失效时只能保持本腿最后一次可信膝角。
            // 禁止读取另一条腿，否则单腿前踢会被复制成双腿同时伸直。
            kneeFlexion = calibrated[slot] ? kneeFlexionDeg[leg] : 0f;
        }

        // Cached values have already been calibrated. Reapplying the mapping
        // during a missing frame would accumulate angle drift.
        if (calfIsFresh)
            kneeFlexion = CorrectKneeFlexion(leg, kneeFlexion);
        if (ShouldHoldSharedSeatedPose(leg))
            kneeFlexion = Mathf.Lerp(kneeFlexion, sharedReadyKneeDeg, seatedBlend);
        kneeFlexion = Mathf.Clamp(
            kneeFlexion,
            0f,
            Mathf.Abs(config.lowerBodyKneeMaxFlexionDeg));
        if (kneeFlexion < Mathf.Max(0f, config.lowerBodyKneeNeutralDeadZoneDeg))
            kneeFlexion = 0f;
        kneeFlexionDeg[leg] = kneeFlexion;

        int thighIndex = leg == 0 ? LeftThighIndex : RightThighIndex;
        Quaternion parentTargetWorld = thighIndex < targets.Length
            ? targets[thighIndex]
            : (parents[slot] != null ? parents[slot].rotation : Quaternion.identity);
        // Solve the actual calf segment direction. The imported bind pose has
        // a knee bend; adding an angle to it leaves a permanent left/right bias.
        float calfRadians = (thighFlexionDeg[leg] - kneeFlexion) * Mathf.Deg2Rad;
        Vector3 desiredWorld = avatarFacing * new Vector3(
            0f, -Mathf.Cos(calfRadians), Mathf.Sin(calfRadians));
        Vector3 desiredParent = Quaternion.Inverse(parentTargetWorld) * desiredWorld;
        Quaternion targetLocal = NormalizeSafe(Quaternion.FromToRotation(
            restDirectionParent[slot], desiredParent) * restLocal[slot]);
        targets[deviceIndex] = NormalizeSafe(parentTargetWorld * targetLocal);
    }

    private bool ShouldHoldSharedSeatedPose(int leg)
    {
        return seatedTrainingVisualEnabled &&
            (seatedTrainingVisualSwitching || leg != selectedTrainingLeg);
    }

    private float CorrectThighFlexion(int leg, float rawThighDeg)
    {
        if (leg < 0 || leg > 1 || !hasSeatedCalibration[leg])
            return rawThighDeg;

        // Preserve the standing zero while mapping the seated reference.
        return Mathf.Abs(thighRawSeatedDeg[leg]) > 1f
            ? rawThighDeg * calibratedSeatedThighDeg[leg] / thighRawSeatedDeg[leg]
            : rawThighDeg;
    }

    private float CorrectKneeFlexion(int leg, float rawKneeDeg)
    {
        if (leg < 0 || leg > 1 || !hasSeatedCalibration[leg])
            return rawKneeDeg;

        float span = kneeRawReadyDeg[leg] - kneeRawStraightDeg[leg];
        if (span < 20f)
            return rawKneeDeg;

        float corrected = (rawKneeDeg - kneeRawStraightDeg[leg]) *
            calibratedReadyKneeDeg[leg] / span;
        float deadZone = Mathf.Max(2f, config.lowerBodyKneeNeutralDeadZoneDeg);
        if (corrected <= deadZone)
            return 0f;
        return Mathf.Clamp(corrected, 0f,
            Mathf.Abs(config.lowerBodyKneeMaxFlexionDeg));
    }

    private bool TryGetRightKneeFlexion3D(
        Quaternion[] sensorRotations,
        out float kneeFlexion)
    {
        kneeFlexion = 0f;
        int thighSlot = RightThighIndex - FirstIndex;
        int calfSlot = RightCalfIndex - FirstIndex;
        if (!calibrated[thighSlot] || !calibrated[calfSlot])
            return false;

        Vector3 thigh = Quaternion.Inverse(avatarFacing) *
            GetMeasuredSegmentDirection(RightThighIndex, thighSlot, sensorRotations);
        Vector3 calf = Quaternion.Inverse(avatarFacing) *
            GetMeasuredSegmentDirection(RightCalfIndex, calfSlot, sensorRotations);
        calf.z = -calf.z;

        if (thigh.sqrMagnitude < 0.000001f || calf.sqrMagnitude < 0.000001f)
            return false;

        kneeFlexion = Vector3.Angle(thigh.normalized, calf.normalized);
        return !float.IsNaN(kneeFlexion) && !float.IsInfinity(kneeFlexion);
    }

    private bool IsFresh(int deviceIndex, bool[] inputFresh)
    {
        int slot = deviceIndex - FirstIndex;
        return slot >= 0 && slot < calibrated.Length && calibrated[slot] &&
            inputFresh != null && deviceIndex >= 0 && deviceIndex < inputFresh.Length &&
            inputFresh[deviceIndex];
    }

    private float GetSagittalFlexion(
        int deviceIndex,
        int slot,
        Quaternion[] sensorRotations)
    {
        Vector3 measuredWorld = GetMeasuredSegmentDirection(
            deviceIndex, slot, sensorRotations);
        Vector3 measuredAvatar = Quaternion.Inverse(avatarFacing) * measuredWorld;

        float sagittalMagnitude = Mathf.Sqrt(
            measuredAvatar.y * measuredAvatar.y +
            measuredAvatar.z * measuredAvatar.z);
        if (sagittalMagnitude < 0.0001f)
            return 0f;

        return Mathf.Atan2(
            measuredAvatar.z / sagittalMagnitude,
            -measuredAvatar.y / sagittalMagnitude) * Mathf.Rad2Deg;
    }

    private Vector3 GetMeasuredSegmentDirection(
        int deviceIndex,
        int slot,
        Quaternion[] sensorRotations)
    {
        if (sensorRotations == null || deviceIndex < 0 ||
            deviceIndex >= sensorRotations.Length || slot < 0 ||
            slot >= sensorSegmentAxis.Length)
        {
            return avatarFacing * Vector3.down;
        }

        Vector3 direction = sensorRotations[deviceIndex] *
            (sensorSegmentAxis[slot] * Vector3.down);
        return direction.sqrMagnitude > 0.000001f
            ? direction.normalized
            : avatarFacing * Vector3.down;
    }

    private Vector3 GetRestWorldDirection(int slot)
    {
        Transform bone = bones[slot];
        if (bone == null)
            return avatarFacing * Vector3.down;

        Transform end = null;
        if (slot == LeftThighIndex - FirstIndex)
            end = bones[LeftCalfIndex - FirstIndex];
        else if (slot == RightThighIndex - FirstIndex)
            end = bones[RightCalfIndex - FirstIndex];
        else if (bone.childCount > 0)
            end = bone.GetChild(0);

        if (end != null)
        {
            Vector3 direction = end.position - bone.position;
            if (direction.sqrMagnitude > 0.000001f)
                return direction.normalized;
        }
        return avatarFacing * Vector3.down;
    }

    private static Quaternion NormalizeSafe(Quaternion q)
    {
        float magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (float.IsNaN(magnitude) || float.IsInfinity(magnitude) || magnitude < 0.000001f)
            return Quaternion.identity;
        float inverse = 1f / magnitude;
        return new Quaternion(q.x * inverse, q.y * inverse, q.z * inverse, q.w * inverse);
    }
}

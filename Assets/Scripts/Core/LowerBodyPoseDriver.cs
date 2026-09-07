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

    private bool hasRightLegCalibration;
    private float rightKneeRawReadyDeg;
    private float rightKneeRawStraightDeg;
    private float rightThighRawSeatedDeg;

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
        hasRightLegCalibration = false;
        rightKneeRawReadyDeg = 0f;
        rightKneeRawStraightDeg = 0f;
        rightThighRawSeatedDeg = 0f;
    }

    public void ConfigureRightLegCalibration(
        float rawReadyKneeDeg,
        float rawStraightKneeDeg,
        float rawSeatedThighDeg)
    {
        float kneeSpan = rawReadyKneeDeg - rawStraightKneeDeg;
        if (float.IsNaN(kneeSpan) || float.IsInfinity(kneeSpan) || kneeSpan < 35f ||
            float.IsNaN(rawSeatedThighDeg) || float.IsInfinity(rawSeatedThighDeg))
            return;
        rightKneeRawReadyDeg = rawReadyKneeDeg;
        rightKneeRawStraightDeg = rawStraightKneeDeg;
        rightThighRawSeatedDeg = rawSeatedThighDeg;
        hasRightLegCalibration = true;
    }

    public void ClearRightLegCalibration()
    {
        hasRightLegCalibration = false;
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

        DriveThigh(LeftThighIndex, 0, sensorRotations, targets);
        DriveThigh(RightThighIndex, 1, sensorRotations, targets);

        bool leftCalfFresh = IsFresh(LeftCalfIndex, inputFresh);
        bool rightCalfFresh = IsFresh(RightCalfIndex, inputFresh);
        if (!leftCalfFresh && rightCalfFresh)
        {
            DriveCalf(RightCalfIndex, 1, sensorRotations, targets, inputFresh);
            DriveCalf(LeftCalfIndex, 0, sensorRotations, targets, inputFresh);
        }
        else
        {
            DriveCalf(LeftCalfIndex, 0, sensorRotations, targets, inputFresh);
            DriveCalf(RightCalfIndex, 1, sensorRotations, targets, inputFresh);
        }
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

        thighAngle = GetSagittalFlexion(thighIndex, thighIndex - FirstIndex, sensorRotations);
        if (leg == 1)
            thighAngle = CorrectRightThighFlexion(thighAngle);

        if (leg == 0)
        {
            // 左腿恢复上一个稳定版本：伸直时可正确回到接近 0°。
            float calfAngle = GetSagittalFlexion(calfIndex, calfIndex - FirstIndex, sensorRotations);
            kneeAngle = Mathf.Abs(Mathf.DeltaAngle(thighAngle, calfAngle));
        }
        else
        {
            if (!TryGetRightKneeFlexion3D(sensorRotations, out kneeAngle))
                return false;
            kneeAngle = CorrectRightKneeFlexion(kneeAngle);
        }

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
        if (leg == 1)
            flexion = CorrectRightThighFlexion(flexion);
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
                    kneeFlexion = CorrectRightKneeFlexion(rightKnee);
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
            int otherLeg = 1 - leg;
            int otherDeviceIndex = otherLeg == 0 ? LeftCalfIndex : RightCalfIndex;

            if (deviceIndex == RightCalfIndex && calibrated[slot])
            {
                kneeFlexion = kneeFlexionDeg[leg];
            }
            else if (IsFresh(otherDeviceIndex, inputFresh))
            {
                kneeFlexion = kneeFlexionDeg[otherLeg];
            }
            else if (calibrated[slot])
            {
                kneeFlexion = kneeFlexionDeg[leg];
            }
            else
            {
                kneeFlexion = Mathf.Max(0f, thighFlexionDeg[leg]);
            }
        }

        kneeFlexion = Mathf.Clamp(
            kneeFlexion,
            0f,
            Mathf.Abs(config.lowerBodyKneeMaxFlexionDeg));
        if (kneeFlexion < Mathf.Max(0f, config.lowerBodyKneeNeutralDeadZoneDeg))
            kneeFlexion = 0f;
        kneeFlexionDeg[leg] = kneeFlexion;

        Vector3 hingeAxis = config.lowerBodyKneeHingeAxisLocal.sqrMagnitude > 0.000001f
            ? config.lowerBodyKneeHingeAxisLocal.normalized
            : Vector3.right;
        Quaternion targetLocal = NormalizeSafe(
            restLocal[slot] * Quaternion.AngleAxis(
                kneeFlexion * config.lowerBodyKneeFlexionSign,
                hingeAxis));

        int thighIndex = leg == 0 ? LeftThighIndex : RightThighIndex;
        Quaternion parentTargetWorld = thighIndex < targets.Length
            ? targets[thighIndex]
            : (parents[slot] != null ? parents[slot].rotation : Quaternion.identity);
        targets[deviceIndex] = NormalizeSafe(parentTargetWorld * targetLocal);
    }

    private float CorrectRightThighFlexion(float rawThighDeg)
    {
        if (!hasRightLegCalibration)
            return rawThighDeg;

        return rawThighDeg + (90f - rightThighRawSeatedDeg);
    }

    private float CorrectRightKneeFlexion(float rawKneeDeg)
    {
        if (!hasRightLegCalibration)
            return rawKneeDeg;

        float span = rightKneeRawReadyDeg - rightKneeRawStraightDeg;
        if (span < 35f)
            return rawKneeDeg;

        float corrected = (rawKneeDeg - rightKneeRawStraightDeg) * 90f / span;
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

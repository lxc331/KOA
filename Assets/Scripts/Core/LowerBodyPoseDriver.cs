using UnityEngine;

/// <summary>
/// 将 06-09 惯性传感器映射为双腿的解剖运动。
///
/// IMU 的航向会漂移，而大腿/小腿的长轴方向仍然可靠。因此这里不把完整
/// 四元数直接复制给骨骼，只读取骨段长轴方向，并将运动约束到角色矢状面：
/// 髋关节负责前后屈伸，膝关节负责单轴弯曲。这样可保留坐下、起立和坐姿
/// 小腿前踢，同时阻止静止航向漂移造成整条腿绕轴旋转或交叉。
/// </summary>
public sealed class LowerBodyPoseDriver
{
    public const int LeftThighIndex = 5;
    public const int LeftCalfIndex = 6;
    public const int RightThighIndex = 7;
    public const int RightCalfIndex = 8;

    private const int FirstIndex = LeftThighIndex;
    private const int SegmentCount = 4;
    private const float LeftKneeSeatedReferenceDeg = 90f;
    private const float LeftKneeExtensionGain = 1.5f;

    private readonly MotionCaptureConfig config;
    private readonly bool[] calibrated = new bool[SegmentCount];
    private readonly Quaternion[] sensorSegmentAxis = new Quaternion[SegmentCount];
    private readonly Quaternion[] restLocal = new Quaternion[SegmentCount];
    private readonly Vector3[] restDirectionParent = new Vector3[SegmentCount];
    private readonly Transform[] bones = new Transform[SegmentCount];
    private readonly Transform[] parents = new Transform[SegmentCount];
    private readonly float[] thighFlexionDeg = new float[2];
    private readonly float[] kneeFlexionDeg = new float[2];

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
    }

    /// <summary>
    /// 各传感器独立上线、独立标定。09 晚到时优先复用 07 已识别出的
    /// “传感器局部骨段轴”，因此即使 09 在坐姿中上线也不会把坐姿当零位。
    /// </summary>
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
                if (deviceIndex == RightCalfIndex)
                {
                    float thighSagittal = GetSagittalFlexion(
                        measuredThighIndex, thighSlot, sensorRotations);
                    float calfSagittal = -GetSagittalFlexion(
                        deviceIndex, slot, sensorRotations);
                    kneeFlexion = Mathf.Abs(
                        Mathf.DeltaAngle(thighSagittal, calfSagittal));
                }
                else
                {
                    float thighSagittal = GetSagittalFlexion(
                        measuredThighIndex, thighSlot, sensorRotations);
                    float calfSagittal = GetSagittalFlexion(
                        deviceIndex, slot, sensorRotations);
                    kneeFlexion = Mathf.Abs(
                        Mathf.DeltaAngle(thighSagittal, calfSagittal));
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

        // 07 左小腿专项：只增强“从约 90°坐姿向完全伸直”的区间。
        // 90°本身不变，后勾（>90°）不变，避免影响已经可用的坐姿与后勾。
        // 当前诊断中最高前踢仍残留约 30°屈曲；1.5 倍伸展增益可把它压到接近 0°。
        if (deviceIndex == LeftCalfIndex &&
            calfIsFresh &&
            kneeFlexion < LeftKneeSeatedReferenceDeg)
        {
            float extensionFromSeated =
                LeftKneeSeatedReferenceDeg - kneeFlexion;
            kneeFlexion = Mathf.Max(
                0f,
                LeftKneeSeatedReferenceDeg -
                extensionFromSeated * LeftKneeExtensionGain);
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
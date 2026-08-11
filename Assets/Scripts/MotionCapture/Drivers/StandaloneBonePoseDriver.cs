using UnityEngine;

/// <summary>
/// 单传感器骨骼诊断驱动器。
/// 当某个子骨段传感器单独在线、但其父骨段传感器未连接时，使用标定时的
/// “传感器世界姿态 -> 骨骼世界姿态”偏移独立驱动目标骨骼。
/// 该路径主要用于单传感器排查；同侧大小腿同时在线时仍优先使用腿部相对膝关节驱动器。
/// </summary>
public sealed class StandaloneBonePoseDriver
{
    public bool IsCalibrated { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public bool SmoothingEnabled { get; set; } = true;
    public float SmoothingSpeed { get; set; } = 30f;

    private Quaternion sensorReference = Quaternion.identity;
    private Quaternion sensorToBoneWorldOffset = Quaternion.identity;
    private Quaternion restLocal = Quaternion.identity;
    private Quaternion smoothedLocal = Quaternion.identity;
    private bool hasSmoothedLocal;

    public void Reset()
    {
        IsCalibrated = false;
        LastError = string.Empty;
        sensorReference = Quaternion.identity;
        sensorToBoneWorldOffset = Quaternion.identity;
        restLocal = Quaternion.identity;
        smoothedLocal = Quaternion.identity;
        hasSmoothedLocal = false;
    }

    public bool TryCalibrate(
        Quaternion sensorQuaternion,
        Transform bone,
        Quaternion boneRestLocal,
        out string reason)
    {
        reason = string.Empty;
        LastError = string.Empty;

        if (bone == null)
        {
            reason = "单传感器目标骨骼为空";
            LastError = reason;
            return false;
        }

        sensorQuaternion = NormalizeSafe(sensorQuaternion);
        if (!IsQuaternionFinite(sensorQuaternion))
        {
            reason = "单传感器标定四元数非法";
            LastError = reason;
            return false;
        }

        sensorReference = sensorQuaternion;
        restLocal = NormalizeSafe(boneRestLocal);
        Quaternion parentWorld = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
        Quaternion boneRestWorld = NormalizeSafe(parentWorld * restLocal);
        sensorToBoneWorldOffset = NormalizeSafe(
            Quaternion.Inverse(sensorReference) * boneRestWorld);

        smoothedLocal = restLocal;
        hasSmoothedLocal = true;
        IsCalibrated = true;
        return true;
    }

    public bool Apply(Quaternion sensorQuaternion, Transform bone)
    {
        LastError = string.Empty;
        if (!IsCalibrated)
        {
            LastError = "单传感器骨骼驱动器尚未标定";
            return false;
        }
        if (bone == null)
        {
            LastError = "单传感器目标骨骼为空";
            return false;
        }

        sensorQuaternion = NormalizeSafe(sensorQuaternion);
        if (!IsQuaternionFinite(sensorQuaternion))
        {
            LastError = "单传感器当前四元数非法";
            return false;
        }

        Quaternion targetWorld = NormalizeSafe(sensorQuaternion * sensorToBoneWorldOffset);
        Quaternion parentWorld = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
        Quaternion targetLocal = NormalizeSafe(Quaternion.Inverse(parentWorld) * targetWorld);

        if (!hasSmoothedLocal)
        {
            smoothedLocal = bone.localRotation;
            hasSmoothedLocal = true;
        }

        if (SmoothingEnabled)
        {
            float t = 1f - Mathf.Exp(-Mathf.Max(0.01f, SmoothingSpeed) * Time.deltaTime);
            smoothedLocal = Quaternion.Slerp(smoothedLocal, targetLocal, t);
        }
        else
        {
            smoothedLocal = targetLocal;
        }

        bone.localRotation = smoothedLocal;
        return true;
    }

    private static Quaternion NormalizeSafe(Quaternion q)
    {
        float sqr = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (sqr < 0.0000001f || float.IsNaN(sqr) || float.IsInfinity(sqr))
            return Quaternion.identity;
        float inv = 1f / Mathf.Sqrt(sqr);
        return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
    }

    private static bool IsQuaternionFinite(Quaternion q)
    {
        return !float.IsNaN(q.x) && !float.IsInfinity(q.x) &&
               !float.IsNaN(q.y) && !float.IsInfinity(q.y) &&
               !float.IsNaN(q.z) && !float.IsInfinity(q.z) &&
               !float.IsNaN(q.w) && !float.IsInfinity(q.w) &&
               (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w) > 0.0000001f;
    }
}

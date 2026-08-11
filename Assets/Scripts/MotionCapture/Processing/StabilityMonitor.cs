using System;
using UnityEngine;

/// <summary>
/// V4 稳定性监控器。
/// 只消费真实传感器帧，并使用“角度 / 采样时间”计算角速度；稳定与失稳采用
/// 两个不同阈值，避免临界噪声让 UI 在绿色和等待状态之间反复闪烁。
/// </summary>
public class StabilityMonitor
{
    private readonly int deviceCount;
    private readonly bool[] hasPreviousSample;
    private readonly DateTime[] previousTimestampUtc;
    private readonly float[] stableDurationsSeconds;
    private readonly bool[] stableStates;
    private readonly int[] consecutiveUnstableSamples;

    /// <summary>
    /// 兼容旧 UI 的进度值。它不再表示真实帧数，而是把稳定持续时间换算成
    /// 0..requiredStableFrames 的显示进度。
    /// </summary>
    public int[] StableCounts { get; private set; }
    public Quaternion[] LastTransformed { get; private set; }

    public StabilityMonitor(int deviceCount)
    {
        this.deviceCount = Mathf.Max(1, deviceCount);
        StableCounts = new int[this.deviceCount];
        LastTransformed = new Quaternion[this.deviceCount];
        hasPreviousSample = new bool[this.deviceCount];
        previousTimestampUtc = new DateTime[this.deviceCount];
        stableDurationsSeconds = new float[this.deviceCount];
        stableStates = new bool[this.deviceCount];
        consecutiveUnstableSamples = new int[this.deviceCount];

        for (int i = 0; i < this.deviceCount; i++)
            LastTransformed[i] = Quaternion.identity;
    }

    /// <summary>
    /// 每次调用必须对应一帧真实、通过协议校验的数据。
    /// stableThresholdDegPerSec 以下累计稳定时间；unstableThresholdDegPerSec 以上
    /// 连续两次才确认失稳；两阈值之间保持当前状态，形成滞回区。
    /// </summary>
    public void UpdateDevice(
        int index,
        Quaternion current,
        bool hasData,
        DateTime timestampUtc,
        float stableThresholdDegPerSec,
        float unstableThresholdDegPerSec,
        float requiredStableDurationSeconds,
        int displayProgressMaximum,
        float maximumSampleGapSeconds = 4f)
    {
        if (index < 0 || index >= deviceCount) return;

        if (!hasData)
        {
            MarkUnavailable(index);
            return;
        }

        Quaternion c = NormalizeSafe(current);
        DateTime timestamp = timestampUtc == DateTime.MinValue ? DateTime.UtcNow : timestampUtc;
        float requiredDuration = Mathf.Max(0.05f, requiredStableDurationSeconds);
        int displayMaximum = Mathf.Max(1, displayProgressMaximum);

        if (!hasPreviousSample[index])
        {
            LastTransformed[index] = c;
            previousTimestampUtc[index] = timestamp;
            hasPreviousSample[index] = true;
            stableDurationsSeconds[index] = 0f;
            stableStates[index] = false;
            consecutiveUnstableSamples[index] = 0;
            StableCounts[index] = 0;
            return;
        }

        Quaternion prev = LastTransformed[index];
        if (Quaternion.Dot(prev, c) < 0f)
            c = new Quaternion(-c.x, -c.y, -c.z, -c.w);

        double dtSecondsRaw = (timestamp - previousTimestampUtc[index]).TotalSeconds;
        LastTransformed[index] = c;
        previousTimestampUtc[index] = timestamp;

        // 同一批出队帧仍保留各自接收时间。时间戳重复/倒退时只更新参考，
        // 不清空已经建立的稳定状态。
        if (dtSecondsRaw <= 0.0001)
            return;

        // V4不再使用固定2秒上限，而是与该路自适应在线门限保持一致。
        // 真正超过在线宽限的长空洞仍会清空参考；正常低频轮询不会反复归零。
        if (dtSecondsRaw > Mathf.Max(0.50f, maximumSampleGapSeconds))
        {
            stableDurationsSeconds[index] = 0f;
            stableStates[index] = false;
            consecutiveUnstableSamples[index] = 0;
            StableCounts[index] = 0;
            return;
        }

        float dtSeconds = (float)dtSecondsRaw;
        float angleDeg = Quaternion.Angle(prev, c);
        float angularSpeedDegPerSec = angleDeg / dtSeconds;
        float stableThreshold = Mathf.Max(0f, stableThresholdDegPerSec);
        float unstableThreshold = Mathf.Max(stableThreshold + 0.1f, unstableThresholdDegPerSec);

        if (angularSpeedDegPerSec <= stableThreshold)
        {
            consecutiveUnstableSamples[index] = 0;
            stableDurationsSeconds[index] = Mathf.Min(
                requiredDuration,
                stableDurationsSeconds[index] + dtSeconds);

            if (stableDurationsSeconds[index] >= requiredDuration)
                stableStates[index] = true;
        }
        else if (angularSpeedDegPerSec >= unstableThreshold)
        {
            // 单个IMU尖峰不立即让整套标定退回等待；连续两次高速变化才确认失稳。
            consecutiveUnstableSamples[index]++;
            if (consecutiveUnstableSamples[index] >= 2)
            {
                stableDurationsSeconds[index] = 0f;
                stableStates[index] = false;
            }
        }
        else
        {
            // 滞回区内保持当前稳定进度，并取消尚未被确认的单次尖峰。
            consecutiveUnstableSamples[index] = 0;
        }

        StableCounts[index] = stableStates[index]
            ? displayMaximum
            : Mathf.Clamp(
                Mathf.FloorToInt(stableDurationsSeconds[index] / requiredDuration * displayMaximum),
                0,
                displayMaximum - 1);
    }

    /// <summary>
    /// 兼容仍保留在旧场景中的 main.cs 四参数调用。
    /// 新版 SensorDataProcessor 继续使用上方带时间戳的入口；旧入口沿用连续稳定帧
    /// 计数，避免替换 MotionCapture 文件夹后因方法签名变化而无法编译。
    /// </summary>
    public void UpdateDevice(int index, Quaternion current, bool hasData, float maxFrameAngleDeg)
    {
        if (index < 0 || index >= deviceCount) return;

        if (!hasData)
        {
            MarkUnavailable(index);
            return;
        }

        Quaternion c = NormalizeSafe(current);
        if (!hasPreviousSample[index])
        {
            LastTransformed[index] = c;
            hasPreviousSample[index] = true;
            previousTimestampUtc[index] = DateTime.MinValue;
            stableDurationsSeconds[index] = 0f;
            stableStates[index] = false;
            consecutiveUnstableSamples[index] = 0;
            StableCounts[index] = 1;
            return;
        }

        Quaternion prev = LastTransformed[index];
        if (Quaternion.Dot(prev, c) < 0f)
            c = new Quaternion(-c.x, -c.y, -c.z, -c.w);

        float angleDeg = Quaternion.Angle(prev, c);
        StableCounts[index] = angleDeg <= Mathf.Max(0f, maxFrameAngleDeg)
            ? StableCounts[index] + 1
            : 0;

        LastTransformed[index] = c;
        previousTimestampUtc[index] = DateTime.MinValue;
        stableDurationsSeconds[index] = 0f;
        stableStates[index] = false;
        consecutiveUnstableSamples[index] = 0;
    }

    public void MarkUnavailable(int index)
    {
        if (index < 0 || index >= deviceCount) return;
        StableCounts[index] = 0;
        stableDurationsSeconds[index] = 0f;
        stableStates[index] = false;
        consecutiveUnstableSamples[index] = 0;
        hasPreviousSample[index] = false;
        previousTimestampUtc[index] = DateTime.MinValue;
        LastTransformed[index] = Quaternion.identity;
    }

    public int GetStableFrameCount(int index)
    {
        return index >= 0 && index < deviceCount ? StableCounts[index] : 0;
    }

    public bool IsDeviceStable(int index, int requiredStableFrames)
    {
        return index >= 0 && index < deviceCount &&
               (stableStates[index] || StableCounts[index] >= Mathf.Max(1, requiredStableFrames));
    }

    public bool IsSystemStable(GameObject[] bones, bool ignoreBonesWithoutObject,
        bool[] deviceHasData, int requiredStableFrames, bool requireAllDevices, int minStableDevices)
    {
        int totalConsidered = 0;
        int stableCount = 0;
        for (int i = 0; i < deviceCount; i++)
        {
            if (ignoreBonesWithoutObject && bones != null && i < bones.Length && bones[i] == null) continue;
            if (deviceHasData == null || i >= deviceHasData.Length || !deviceHasData[i]) continue;
            totalConsidered++;
            if (IsDeviceStable(i, requiredStableFrames)) stableCount++;
        }

        if (requireAllDevices)
            return totalConsidered > 0 && stableCount == totalConsidered;
        return stableCount >= Mathf.Max(1, minStableDevices);
    }

    private static Quaternion NormalizeSafe(Quaternion q)
    {
        float sqr = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (sqr < 0.0000001f || float.IsNaN(sqr) || float.IsInfinity(sqr))
            return Quaternion.identity;
        float inv = 1f / Mathf.Sqrt(sqr);
        return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
    }
}

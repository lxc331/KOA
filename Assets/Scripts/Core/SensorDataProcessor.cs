using System;
using UnityEngine;

/// <summary>
/// 传感器数据处理流水线：出队、坐标转换、稳定性判断、标定、帧同步和骨骼驱动。
/// 当前下肢测试阶段要求 06-09（左大腿、左小腿、右大腿、右小腿）
/// 四个传感器都持续收到数据且稳定后，才允许建立零位并开始驱动。
/// </summary>
public class SensorDataProcessor
{
    private const int LowerBodyFirstIndex = LowerBodyPoseDriver.LeftThighIndex;
    private const int LowerBodyLastIndex = LowerBodyPoseDriver.RightCalfIndex;
    private const int MinimumCalibrationFramesPerDevice = 3;
    private const float CalibrationFreshnessSeconds = 2.5f;

    private readonly int deviceCount;
    private readonly MotionCaptureConfig config;

    private readonly Quaternion[] rawQuaternions;
    private readonly Quaternion[] transformedQuaternions;
    private readonly float[] yaw, pitch, roll;

    private readonly SerialParser.SensorFrame[] latestFrames;
    private readonly bool[] hasLatest;
    private readonly bool[] inputFresh;

    // 用 Unity 实时时钟记录每个设备最近一次真正出队的时间。
    // StabilityMonitor 是按 Unity 帧更新的，仅看 StableCount 会把“只有一帧旧数据”
    // 误判为稳定，因此标定前还必须检查真实接收帧数和新鲜度。
    private readonly float[] lastFrameRealtimeSeconds;
    private readonly int[] receivedFrameCounts;

    private StabilityMonitor stabilityMonitor;

    public RotationDriver Driver { get; private set; }
    private readonly LowerBodyPoseDriver lowerBodyPoseDriver;

    private Quaternion[] restLocalRotations;
    private Quaternion rootFacingOffset = Quaternion.identity;

    public Quaternion[] TransformedQuaternions => transformedQuaternions;
    public Quaternion[] RawQuaternions => rawQuaternions;
    public int[] StableCounts => stabilityMonitor != null
        ? stabilityMonitor.StableCounts
        : Array.Empty<int>();

    public int LastSyncLatestCount { get; private set; }
    public int LastSyncDevicesApplied { get; private set; }
    public float LastSyncSkewMilliseconds { get; private set; } = -1f;
    public string LastSyncStatus { get; private set; } = "not_started";
    public int CalibrationVersion { get; private set; }

    /// <summary>只读采样：游戏独立检查真实收帧时间，不改变现有骨骼驱动行为。</summary>
    public RehabPhotoGame.LowerBodyMeasurement ReadLowerBodyMeasurement(
        float now, float timeoutSeconds, float maxSkewSeconds)
    {
        var sample = new RehabPhotoGame.LowerBodyMeasurement
        {
            CalibrationVersion = CalibrationVersion,
            SampleTimeSeconds = -1f,
            FailureReason = "等待 06～09 数据"
        };
        if (!HasCompleteLowerBodyLayout()) return sample;

        float oldest = float.PositiveInfinity;
        float newest = float.NegativeInfinity;
        bool quaternionsValid = true;
        for (int i = LowerBodyFirstIndex; i <= LowerBodyLastIndex; i++)
        {
            float receivedAt = lastFrameRealtimeSeconds[i];
            float age = now - receivedAt;
            if (receivedFrameCounts[i] > 0 && age >= 0f && age <= timeoutSeconds)
                sample.FreshMask |= 1 << (i - LowerBodyFirstIndex);
            oldest = Mathf.Min(oldest, receivedAt);
            newest = Mathf.Max(newest, receivedAt);
            Quaternion q = rawQuaternions[i];
            float normSquared = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            quaternionsValid &= !float.IsNaN(normSquared) && !float.IsInfinity(normSquared) &&
                normSquared >= 0.25f && normSquared <= 2.25f;
        }
        // 只有四个设备的最旧接收时刻前进，保持计时才允许前进。
        sample.SampleTimeSeconds = float.IsInfinity(oldest) ? -1f : oldest;
        if (sample.FreshMask != 15)
        {
            sample.FailureReason = "传感器缺失或超时；当前动作已中止";
            return sample;
        }
        if (newest - oldest > maxSkewSeconds)
        {
            sample.FailureReason = "四传感器时间差过大";
            return sample;
        }
        if (!quaternionsValid)
        {
            sample.FailureReason = "四元数无效";
            return sample;
        }
        if (!Driver.IsCalibrated ||
            !lowerBodyPoseDriver.TryMeasureLeg(0, transformedQuaternions,
                out sample.LeftThighDeg, out sample.LeftKneeDeg) ||
            !lowerBodyPoseDriver.TryMeasureLeg(1, transformedQuaternions,
                out sample.RightThighDeg, out sample.RightKneeDeg))
        {
            sample.LeftThighDeg = sample.LeftKneeDeg = sample.RightThighDeg = sample.RightKneeDeg = 0f;
            sample.FailureReason = "请先完成站姿标定并开始动捕驱动";
            return sample;
        }
        sample.IsValid = true;
        sample.FailureReason = "";
        return sample;
    }

    public SensorDataProcessor(MotionCaptureConfig config)
    {
        this.config = config;
        deviceCount = config.deviceCount;

        rawQuaternions = new Quaternion[deviceCount];
        transformedQuaternions = new Quaternion[deviceCount];
        yaw = new float[deviceCount];
        pitch = new float[deviceCount];
        roll = new float[deviceCount];
        latestFrames = new SerialParser.SensorFrame[deviceCount];
        hasLatest = new bool[deviceCount];
        inputFresh = new bool[deviceCount];
        lastFrameRealtimeSeconds = new float[deviceCount];
        receivedFrameCounts = new int[deviceCount];

        for (int i = 0; i < deviceCount; i++)
        {
            rawQuaternions[i] = Quaternion.identity;
            transformedQuaternions[i] = Quaternion.identity;
            lastFrameRealtimeSeconds[i] = float.NegativeInfinity;
            receivedFrameCounts[i] = 0;
        }

        stabilityMonitor = new StabilityMonitor(deviceCount);
        Driver = new RotationDriver(
            deviceCount, true, config.smoothSpeed, config.debounceThresholdDeg, false);
        lowerBodyPoseDriver = new LowerBodyPoseDriver(config);
    }

    public void SetRootFacingOffset(Quaternion offset) => rootFacingOffset = offset;

    public void InitConstraints(Quaternion[] restLocalRotations)
    {
        this.restLocalRotations = restLocalRotations != null
            ? (Quaternion[])restLocalRotations.Clone()
            : null;
        Driver.SetConstraints(
            config.minLocalAngles, config.maxLocalAngles, restLocalRotations);
    }

    public int DequeueAll(
        SerialParser parser,
        MotionCaptureState state,
        Action<int, Quaternion, Vector3> onFrame)
    {
        int count = 0;
        int deviceId;
        Quaternion q;

        while (parser.TryDequeue(out deviceId, out q))
        {
            if (deviceId < 0 || deviceId >= deviceCount)
                continue;

            rawQuaternions[deviceId] = q;

            var e = q.eulerAngles;
            yaw[deviceId] = e.z;
            pitch[deviceId] = e.y;
            roll[deviceId] = e.x;

            state.SetDeviceHasData(deviceId, true);
            state.NotifyEulerUpdated(
                deviceId,
                new Vector3(roll[deviceId], pitch[deviceId], yaw[deviceId]));

            lastFrameRealtimeSeconds[deviceId] = Time.realtimeSinceStartup;
            receivedFrameCounts[deviceId]++;

            onFrame?.Invoke(deviceId, q, e);
            count++;
        }

        return count;
    }

    public void TransformAll(MotionCaptureState state)
    {
        for (int i = 0; i < deviceCount; i++)
        {
            if (!state.GetDeviceHasData(i))
            {
                transformedQuaternions[i] = Quaternion.identity;
                continue;
            }

            transformedQuaternions[i] =
                MapSensorToAvatarSpace(i, rawQuaternions[i]);
            state.SetDeviceQuaternion(i, transformedQuaternions[i]);
        }
    }

    public void UpdateStability(MotionCaptureState state)
    {
        for (int i = 0; i < deviceCount; i++)
        {
            stabilityMonitor.UpdateDevice(
                i,
                transformedQuaternions[i],
                state.GetDeviceHasData(i),
                config.maxAngularSpeedDeg);
        }
    }

    /// <summary>
    /// 下肢测试阶段把“稳定”定义为 06-09 四个设备同时满足：
    /// 1) 已收到数据；2) 至少收到 3 个真实数据帧；
    /// 3) 最近 2.5 秒内仍有新帧；4) StabilityMonitor 达到稳定帧阈值。
    /// 这样不会再出现 06 刚上线就提前预标定、07/09 后到再各自补标定的情况。
    /// </summary>
    public bool CheckStability(GameObject[] bones, bool[] deviceHasData)
    {
        if (HasCompleteLowerBodyLayout())
            return AreLowerBodySensorsReady(deviceHasData);

        return stabilityMonitor.IsSystemStable(
            bones,
            config.ignoreBonesWithoutObject,
            deviceHasData,
            config.requiredStableFrames,
            config.requireAllDevices,
            config.minStableDevices);
    }

    public void TryPreCalibrate(GameObject[] bones, MotionCaptureState state)
    {
        if (Driver.IsCalibrated)
            return;

        Calibrate(bones, state);
    }

    /// <summary>
    /// 建立零位。06-09 未全部稳定、未连续收到真实数据时直接拒绝标定。
    /// </summary>
    public void Calibrate(GameObject[] bones, MotionCaptureState state)
    {
        if (HasCompleteLowerBodyLayout() &&
            !AreLowerBodySensorsReady(state?.GetDeviceHasDataArray()))
        {
            LastSyncStatus = "waiting_lower_body_calibration";
            return;
        }

        Driver.Calibrate(bones, transformedQuaternions);

        lowerBodyPoseDriver.Reset();
        lowerBodyPoseDriver.TryCalibrate(
            transformedQuaternions,
            bones,
            restLocalRotations,
            rootFacingOffset,
            state);

        LastSyncStatus = "calibrated";
        CalibrationVersion++;
    }

    public void SyncAndUpdateTargets(
        SerialParser parser,
        MotionCaptureState state,
        GameObject[] bones,
        bool requireAllDevices)
    {
        LastSyncLatestCount = 0;
        LastSyncDevicesApplied = 0;
        LastSyncSkewMilliseconds = -1f;
        LastSyncStatus = "collecting";

        DateTime? newestTime = null;
        DateTime? oldestTime = null;
        int latestCount = 0;

        for (int i = 0; i < deviceCount; i++)
            hasLatest[i] = false;

        for (int i = 0; i < deviceCount; i++)
        {
            if (parser.TryGetLatestFrame(i, out latestFrames[i]))
            {
                hasLatest[i] = true;
                latestCount++;

                if (!newestTime.HasValue ||
                    latestFrames[i].Timestamp > newestTime.Value)
                    newestTime = latestFrames[i].Timestamp;

                if (!oldestTime.HasValue ||
                    latestFrames[i].Timestamp < oldestTime.Value)
                    oldestTime = latestFrames[i].Timestamp;
            }
        }

        LastSyncLatestCount = latestCount;
        if (newestTime.HasValue && oldestTime.HasValue)
        {
            LastSyncSkewMilliseconds =
                (float)(newestTime.Value - oldestTime.Value).TotalMilliseconds;
        }

        if (latestCount == 0 || !newestTime.HasValue)
        {
            LastSyncStatus = "no_latest_frames";
            return;
        }

        DateTime targetTime = newestTime.Value;
        int devicesApplied = 0;

        // 之前使用 5 秒会让已经停更的 09 姿态保留过久。
        // 这里收紧到至少 2.5 秒，仍明显大于当前约 0.5 秒的典型接收间隔。
        float freshnessSeconds =
            Mathf.Max(CalibrationFreshnessSeconds, config.rightLegInputFreshnessSeconds);

        for (int i = 0; i < deviceCount; i++)
        {
            inputFresh[i] = hasLatest[i] &&
                (targetTime - latestFrames[i].Timestamp).TotalSeconds <=
                freshnessSeconds;
        }

        for (int i = 0; i < deviceCount; i++)
        {
            if (!hasLatest[i])
            {
                if (requireAllDevices)
                {
                    LastSyncStatus =
                        $"missing_required_device_{i + 1:00}";
                    return;
                }

                if (state.GetDeviceHasData(i))
                {
                    transformedQuaternions[i] =
                        MapSensorToAvatarSpace(i, rawQuaternions[i]);
                    devicesApplied++;
                }

                continue;
            }

            SerialParser.SensorFrame frame = latestFrames[i];

            if (frame.Timestamp != targetTime)
            {
                if (!parser.TryGetInterpolatedFrame(i, targetTime, out frame))
                    frame = latestFrames[i];
            }

            transformedQuaternions[i] =
                MapSensorToAvatarSpace(i, frame.Q);
            devicesApplied++;
        }

        LastSyncDevicesApplied = devicesApplied;

        if (devicesApplied == 0)
        {
            LastSyncStatus = "no_devices_applied";
            return;
        }

        // 用户可以提前按“开始”，但真正的姿态驱动必须等 06-09
        // 全部在线、连续收到真实帧且稳定后再建立一次统一零位。
        if (!Driver.IsCalibrated)
        {
            if (HasCompleteLowerBodyLayout() &&
                !AreLowerBodySensorsReady(state.GetDeviceHasDataArray()))
            {
                LastSyncStatus = "waiting_lower_body_calibration";
                return;
            }

            Calibrate(bones, state);

            if (!Driver.IsCalibrated)
            {
                LastSyncStatus = "waiting_lower_body_calibration";
                return;
            }
        }

        Driver.UpdateTargets(transformedQuaternions);

        lowerBodyPoseDriver.TryCalibrate(
            transformedQuaternions,
            bones,
            restLocalRotations,
            rootFacingOffset,
            state);

        lowerBodyPoseDriver.ConstrainTargets(
            transformedQuaternions,
            Driver.Targets,
            inputFresh);

        LastSyncStatus = "targets_updated";
    }

    /// <summary>
    /// 未完成统一标定时保持绑定姿态，不把半套传感器数据写入人物骨骼。
    /// </summary>
    public void ApplyToBones(GameObject[] bones)
    {
        if (!Driver.IsCalibrated)
            return;

        Driver.Apply(bones);
    }

    public void Reset(GameObject[] bones, Quaternion[] restLocalRotations)
    {
        CalibrationVersion++;
        for (int i = 0; i < deviceCount; i++)
        {
            rawQuaternions[i] = Quaternion.identity;
            transformedQuaternions[i] = Quaternion.identity;
            inputFresh[i] = false;
            hasLatest[i] = false;
            lastFrameRealtimeSeconds[i] = float.NegativeInfinity;
            receivedFrameCounts[i] = 0;
        }

        Driver.ResetToRestPose(bones, restLocalRotations);
        lowerBodyPoseDriver.Reset();
        stabilityMonitor = new StabilityMonitor(deviceCount);

        LastSyncLatestCount = 0;
        LastSyncDevicesApplied = 0;
        LastSyncSkewMilliseconds = -1f;
        LastSyncStatus = "reset";
    }

    public void ResetStabilityMonitor()
    {
        stabilityMonitor = new StabilityMonitor(deviceCount);
    }

    private bool HasCompleteLowerBodyLayout()
    {
        return deviceCount > LowerBodyLastIndex;
    }

    private bool AreLowerBodySensorsReady(bool[] deviceHasData)
    {
        if (!HasCompleteLowerBodyLayout() ||
            deviceHasData == null ||
            deviceHasData.Length <= LowerBodyLastIndex ||
            stabilityMonitor == null)
            return false;

        int[] stableCounts = stabilityMonitor.StableCounts;
        if (stableCounts == null ||
            stableCounts.Length <= LowerBodyLastIndex)
            return false;

        float now = Time.realtimeSinceStartup;
        float oldestReceiveTime = float.PositiveInfinity;
        float newestReceiveTime = float.NegativeInfinity;

        for (int i = LowerBodyFirstIndex; i <= LowerBodyLastIndex; i++)
        {
            if (!deviceHasData[i])
                return false;

            if (receivedFrameCounts[i] < MinimumCalibrationFramesPerDevice)
                return false;

            float age = now - lastFrameRealtimeSeconds[i];
            if (float.IsNaN(age) || float.IsInfinity(age) ||
                age < 0f || age > CalibrationFreshnessSeconds)
                return false;

            if (stableCounts[i] < config.requiredStableFrames)
                return false;

            oldestReceiveTime = Mathf.Min(
                oldestReceiveTime, lastFrameRealtimeSeconds[i]);
            newestReceiveTime = Mathf.Max(
                newestReceiveTime, lastFrameRealtimeSeconds[i]);
        }

        // 四个设备最近一次真实帧不能相差太久，避免某一只设备停更后
        // 仍拿旧姿态参与零位标定。
        return newestReceiveTime - oldestReceiveTime <=
            CalibrationFreshnessSeconds;
    }

    /// <summary>
    /// 将传感器原始四元数映射到 Unity Avatar 空间。
    /// 2026-09-03 实测日志确认：09 与 06/07/08 应使用同一通用坐标映射。
    /// 上一版对 09 单独交换 X/Y 并反转 Z，会把正常坐姿约 90°膝角压到约 40°，
    /// 并使右小腿前踢时膝角反而增大、站起后仍保持大角度弯曲。
    /// </summary>
    private Quaternion MapSensorToAvatarSpace(
        int index,
        Quaternion rawSensorQ)
    {
        Quaternion unityQ = RotationDriver.MapSensorToUnity(index, rawSensorQ);
        return rootFacingOffset * unityQ;
    }
}

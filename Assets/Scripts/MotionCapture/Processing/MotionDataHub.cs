using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// V6 集中式传感器数据中心。
///
/// 串口解析器只负责协议校验并输出原始帧；所有设备数据在这里统一完成：
/// 1. 合法性检查；
/// 2. 异常帧过滤；
/// 3. 按设备缓存；
/// 4. 超时/断流判定；
/// 5. 统一时间点插值；
/// 6. 生成一份供整个人体驱动共享的只读语义快照。
///
/// 注意：当前硬件协议没有上游帧序号，因此这里的 Sequence 是 Unity 接收端的
/// 单设备递增序号，可用于诊断 Unity 队列丢弃，但无法判断无线链路在进入串口前的丢包。
/// </summary>
public sealed class MotionDataHub
{
    public enum SensorRole
    {
        LeftUpperArm = 0,
        LeftForeArm = 1,
        RightUpperArm = 2,
        RightForeArm = 3,
        Torso = 4,
        LeftThigh = 5,
        LeftCalf = 6,
        RightThigh = 7,
        RightCalf = 8
    }

    private struct Sample
    {
        public Quaternion Rotation;
        public DateTime TimestampUtc;
        public long Sequence;
    }

    private readonly int deviceCount;
    private readonly Queue<Sample>[] histories;
    private readonly Quaternion[] synchronizedRotations;
    // 每个设备最近一次通过校验与异常过滤的姿态。标定使用该数组，避免同步插值快照在低频轮询下反复跳点。
    private readonly Quaternion[] latestAcceptedRotations;
    private readonly bool[] valid;
    private readonly bool[] updatedThisTick;
    private readonly DateTime[] lastReceiveUtc;
    private readonly long[] acceptedFrameCount;
    private readonly long[] anomalyFrameCount;
    private readonly long[] invalidFrameCount;
    private readonly long[] staleSourceFrameCount;
    private readonly float[] lastSourceBacklogAgeMs;
    private readonly long[] lastSequence;
    private readonly float[] smoothedFrameRateHz;
    private readonly float[] lastAcceptedStepAngleDeg;
    // 主线程长时间暂停后，只保留每路最后一帧，避免恢复时逐条回放历史积压。
    private readonly SerialParser.RawSensorFrame[] backlogLatestFrames;
    private readonly bool[] backlogHasLatestFrame;
    private readonly AnomalyDetector anomalyDetector;

    private const int MaxHistoryFrames = 96;
    // 在线判定允许低频轮询保留数秒；历史窗口必须覆盖同一时间范围，
    // 否则状态仍在线时历史队列却会先被清空，造成下一帧再次误离线。
    private const double HistoryRetentionSeconds = 8.0;
    private const float AdaptiveOfflineIntervalMultiplier = 4f;
    private const float AdaptiveOfflineMaximumSeconds = 4.0f;
    private const int MinimumBacklogCoalesceThreshold = 12;

    /// <summary>统一插值目标相对最新到包时间向后回退的缓冲。V77.30实时默认0ms。</summary>
    public float SynchronizationDelaySeconds { get; set; } = 0f;

    /// <summary>
    /// 标定/等待阶段在线判定的最小宽限时间。根据每路实测Hz放宽到约4个采样周期，
    /// 最多4秒。运行驱动使用Controller独立的1秒严格门限，不消费这里保留的旧姿态。
    /// </summary>
    public float OfflineTimeoutSeconds { get; set; } = 0.500f;

    /// <summary>插值前后两帧间隔超过该值时，不跨越该空洞插值。默认120ms。</summary>
    public float MaxInterpolationGapSeconds { get; set; } = 0.120f;

    /// <summary>
    /// 对01/03/06/08低频驱动通道启用严格限幅的短时预测。
    /// 只延续最近两帧已经形成的旋转趋势；超过预测时间后保持限幅结果，
    /// 不会跨数秒断流持续外推。
    /// </summary>
    public bool LowFrequencyPredictionEnabled { get; set; } = true;
    public float MaxPredictionHorizonSeconds { get; set; } = 0.200f;
    public float MaxPredictionAngleDeg { get; set; } = 7f;
    public float MaxPredictionAngularSpeedDegPerSec { get; set; } = 35f;
    public float MaxPredictionSourceIntervalSeconds { get; set; } = 2f;

    /// <summary>
    /// Reliable source-clock frames that arrive this far behind the fastest
    /// observed path are diagnostic history, not live pose input.
    /// </summary>
    public float MaximumSourceBacklogAgeSeconds { get; set; } = 0.750f;

    public DateTime SnapshotTimestampUtc { get; private set; }
    public long SnapshotIndex { get; private set; }
    public long BacklogDiscardedFrameCount { get; private set; }
    public int LastBacklogDiscardedFrameCount { get; private set; }
    public int LastInputQueueDepth { get; private set; }

    public MotionDataHub(int deviceCount, bool anomalyEnabled, int anomalyHistorySize, float anomalyThresholdDeg)
    {
        this.deviceCount = Mathf.Max(1, deviceCount);
        histories = new Queue<Sample>[this.deviceCount];
        synchronizedRotations = new Quaternion[this.deviceCount];
        latestAcceptedRotations = new Quaternion[this.deviceCount];
        valid = new bool[this.deviceCount];
        updatedThisTick = new bool[this.deviceCount];
        lastReceiveUtc = new DateTime[this.deviceCount];
        acceptedFrameCount = new long[this.deviceCount];
        anomalyFrameCount = new long[this.deviceCount];
        invalidFrameCount = new long[this.deviceCount];
        staleSourceFrameCount = new long[this.deviceCount];
        lastSourceBacklogAgeMs = new float[this.deviceCount];
        lastSequence = new long[this.deviceCount];
        smoothedFrameRateHz = new float[this.deviceCount];
        lastAcceptedStepAngleDeg = new float[this.deviceCount];
        backlogLatestFrames = new SerialParser.RawSensorFrame[this.deviceCount];
        backlogHasLatestFrame = new bool[this.deviceCount];

        for (int i = 0; i < this.deviceCount; i++)
        {
            histories[i] = new Queue<Sample>(MaxHistoryFrames);
            synchronizedRotations[i] = Quaternion.identity;
            latestAcceptedRotations[i] = Quaternion.identity;
            lastReceiveUtc[i] = DateTime.MinValue;
            lastSequence[i] = -1;
        }

        if (anomalyEnabled)
        {
            anomalyDetector = new AnomalyDetector
            {
                HistorySize = Mathf.Max(3, anomalyHistorySize),
                AngleThresholdDeg = Mathf.Clamp(anomalyThresholdDeg, 1f, 179f)
            };
        }
    }

    public bool IsDeviceValid(int deviceId)
    {
        return deviceId >= 0 && deviceId < deviceCount && valid[deviceId];
    }

    public bool WasDeviceUpdatedThisTick(int deviceId)
    {
        return deviceId >= 0 && deviceId < deviceCount && updatedThisTick[deviceId];
    }

    public Quaternion GetSynchronizedRotation(int deviceId)
    {
        return deviceId >= 0 && deviceId < deviceCount
            ? synchronizedRotations[deviceId]
            : Quaternion.identity;
    }

    /// <summary>返回该设备最近一次通过集中校验和异常过滤的姿态，不经过统一时间点插值。</summary>
    public Quaternion GetLatestAcceptedRotation(int deviceId)
    {
        return deviceId >= 0 && deviceId < deviceCount
            ? latestAcceptedRotations[deviceId]
            : Quaternion.identity;
    }

    /// <summary>该设备自本次连接/重置后是否至少有一帧通过集中校验。</summary>
    public bool HasAcceptedSample(int deviceId)
    {
        return deviceId >= 0 && deviceId < deviceCount &&
               acceptedFrameCount[deviceId] > 0 &&
               lastReceiveUtc[deviceId] != DateTime.MinValue;
    }

    /// <summary>最近一次有效帧距当前时刻的年龄。仅用于诊断与标定提示，不影响实时驱动超时门控。</summary>
    public double GetLatestAcceptedDataAgeSeconds(int deviceId, DateTime nowUtc)
    {
        if (!HasAcceptedSample(deviceId))
            return double.PositiveInfinity;
        return Math.Max(0.0, (nowUtc - lastReceiveUtc[deviceId]).TotalSeconds);
    }

    public double GetDataAgeSeconds(int deviceId, DateTime nowUtc)
    {
        if (deviceId < 0 || deviceId >= deviceCount || lastReceiveUtc[deviceId] == DateTime.MinValue)
            return double.PositiveInfinity;
        return Math.Max(0.0, (nowUtc - lastReceiveUtc[deviceId]).TotalSeconds);
    }

    public long GetAcceptedFrameCount(int deviceId) => GetCounter(acceptedFrameCount, deviceId);
    public long GetAnomalyFrameCount(int deviceId) => GetCounter(anomalyFrameCount, deviceId);
    public long GetInvalidFrameCount(int deviceId) => GetCounter(invalidFrameCount, deviceId);
    public long GetStaleSourceFrameCount(int deviceId) => GetCounter(staleSourceFrameCount, deviceId);
    public float GetLastSourceBacklogAgeMs(int deviceId) =>
        deviceId >= 0 && deviceId < deviceCount ? lastSourceBacklogAgeMs[deviceId] : 0f;
    public long GetLastSequence(int deviceId) => GetCounter(lastSequence, deviceId);
    public float GetSmoothedFrameRateHz(int deviceId) =>
        deviceId >= 0 && deviceId < deviceCount ? smoothedFrameRateHz[deviceId] : 0f;
    public float GetLastAcceptedStepAngleDeg(int deviceId) =>
        deviceId >= 0 && deviceId < deviceCount ? lastAcceptedStepAngleDeg[deviceId] : 0f;

    public float GetEffectiveOfflineTimeoutSeconds(int deviceId)
    {
        float minimumTimeout = Mathf.Max(0.50f, OfflineTimeoutSeconds);
        if (deviceId < 0 || deviceId >= deviceCount)
            return minimumTimeout;

        return CalculateAdaptiveOfflineTimeoutSeconds(minimumTimeout, smoothedFrameRateHz[deviceId]);
    }

    /// <summary>
    /// 根据单路实测频率计算标定/等待阶段的新鲜度门限：允许约四个采样周期，最多4秒。
    /// 暴露成纯函数，便于在没有真实串口的情况下做回归测试。
    /// </summary>
    public static float CalculateAdaptiveOfflineTimeoutSeconds(float minimumTimeoutSeconds, float measuredHz)
    {
        float minimumTimeout = Mathf.Max(0.50f, minimumTimeoutSeconds);
        // 只收到首帧时还无法估算Hz。先给足最大启动宽限，避免低频设备在
        // 第二帧到来前被反复判离线，导致永远无法建立自己的频率估计。
        if (measuredHz <= 0.05f)
            return Mathf.Max(minimumTimeout, AdaptiveOfflineMaximumSeconds);

        float cadenceTimeout = AdaptiveOfflineIntervalMultiplier / measuredHz;
        return Mathf.Clamp(
            Mathf.Max(minimumTimeout, cadenceTimeout),
            minimumTimeout,
            Mathf.Max(minimumTimeout, AdaptiveOfflineMaximumSeconds));
    }
    public long GetSequenceGapCount(int deviceId)
    {
        if (deviceId < 0 || deviceId >= deviceCount) return 0;
        return Math.Max(0L, lastSequence[deviceId] - acceptedFrameCount[deviceId]);
    }

    /// <summary>
    /// 从两个设备的集中历史中寻找最新、时间差足够小的一对有效姿态。
    /// 膝角测量使用该接口，避免直接把不同时间点的大腿/小腿“最新值”相减。
    /// </summary>
    public bool TryGetLatestTimePairedRotations(
        int firstDeviceId,
        int secondDeviceId,
        double maxSkewSeconds,
        out Quaternion firstRotation,
        out Quaternion secondRotation,
        out DateTime pairTimestampUtc,
        out double pairSkewSeconds)
    {
        firstRotation = Quaternion.identity;
        secondRotation = Quaternion.identity;
        pairTimestampUtc = DateTime.MinValue;
        pairSkewSeconds = double.PositiveInfinity;

        if (firstDeviceId < 0 || firstDeviceId >= deviceCount ||
            secondDeviceId < 0 || secondDeviceId >= deviceCount ||
            firstDeviceId == secondDeviceId)
            return false;

        Queue<Sample> firstHistory = histories[firstDeviceId];
        Queue<Sample> secondHistory = histories[secondDeviceId];
        if (firstHistory.Count == 0 || secondHistory.Count == 0)
            return false;

        double allowedSkew = Math.Max(0.001, maxSkewSeconds);
        bool found = false;
        DateTime bestCommonTime = DateTime.MinValue;

        foreach (Sample first in firstHistory)
        {
            foreach (Sample second in secondHistory)
            {
                double skew = Math.Abs((first.TimestampUtc - second.TimestampUtc).TotalSeconds);
                if (skew > allowedSkew)
                    continue;

                // 两路都已到达的共同时间；优先最新数据，同一时间再选更小错位。
                DateTime commonTime = first.TimestampUtc <= second.TimestampUtc
                    ? first.TimestampUtc
                    : second.TimestampUtc;
                if (found && commonTime < bestCommonTime)
                    continue;
                if (found && commonTime == bestCommonTime && skew >= pairSkewSeconds)
                    continue;

                found = true;
                bestCommonTime = commonTime;
                pairSkewSeconds = skew;
                pairTimestampUtc = commonTime;
                firstRotation = first.Rotation;
                secondRotation = second.Rotation;
            }
        }

        return found;
    }

    /// <summary>
    /// 从解析器集中取出所有原始帧，处理后建立同一时间点的人体输入快照。
    /// onAcceptedFrame 回调接收到“异常过滤后、坐标转换前”的四元数及该帧接收时间。
    /// </summary>
    public int UpdateFromParser(
        SerialParser parser,
        DateTime nowUtc,
        Action<int, Quaternion, Vector3, DateTime> onAcceptedFrame)
    {
        if (parser == null) return 0;

        for (int i = 0; i < deviceCount; i++)
            updatedThisTick[i] = false;

        int drainedCount = 0;
        LastInputQueueDepth = parser.QueueCount;
        LastBacklogDiscardedFrameCount = 0;

        // 正常实时阶段逐帧处理，保留完整记录；只有队列明显积压（至少12帧，
        // 且超过每设备2帧）时才进入恢复合并。九路约8.95fps的实测链路在
        // 正常Unity帧率下不会触发，窗口失焦/主线程暂停后的几十到256帧会触发。
        int coalesceThreshold = Math.Max(
            MinimumBacklogCoalesceThreshold,
            deviceCount * 2);
        bool coalesceBacklog = LastInputQueueDepth > coalesceThreshold;

        if (coalesceBacklog)
        {
            Array.Clear(backlogHasLatestFrame, 0, backlogHasLatestFrame.Length);
            SerialParser.RawSensorFrame queuedFrame;
            while (parser.TryDequeueFrame(out queuedFrame))
            {
                drainedCount++;
                int deviceId = queuedFrame.DeviceId;
                if (deviceId < 0 || deviceId >= deviceCount)
                    continue;
                backlogLatestFrames[deviceId] = queuedFrame;
                backlogHasLatestFrame[deviceId] = true;
            }

            int retainedFrameCount = 0;
            for (int deviceId = 0; deviceId < deviceCount; deviceId++)
            {
                if (!backlogHasLatestFrame[deviceId]) continue;
                retainedFrameCount++;
                ProcessRawFrame(backlogLatestFrames[deviceId], nowUtc, onAcceptedFrame);
            }

            LastBacklogDiscardedFrameCount = Math.Max(0, drainedCount - retainedFrameCount);
            BacklogDiscardedFrameCount += LastBacklogDiscardedFrameCount;
        }
        else
        {
            SerialParser.RawSensorFrame frame;
            while (parser.TryDequeueFrame(out frame))
            {
                drainedCount++;
                ProcessRawFrame(frame, nowUtc, onAcceptedFrame);
            }
        }

        BuildSynchronizedSnapshot(nowUtc);
        return drainedCount;
    }

    public void Reset()
    {
        for (int i = 0; i < deviceCount; i++)
        {
            histories[i].Clear();
            synchronizedRotations[i] = Quaternion.identity;
            latestAcceptedRotations[i] = Quaternion.identity;
            valid[i] = false;
            updatedThisTick[i] = false;
            lastReceiveUtc[i] = DateTime.MinValue;
            acceptedFrameCount[i] = 0;
            anomalyFrameCount[i] = 0;
            invalidFrameCount[i] = 0;
            staleSourceFrameCount[i] = 0;
            lastSourceBacklogAgeMs[i] = 0f;
            lastSequence[i] = -1;
            smoothedFrameRateHz[i] = 0f;
            lastAcceptedStepAngleDeg[i] = 0f;
        }
        SnapshotTimestampUtc = DateTime.MinValue;
        SnapshotIndex = 0;
        BacklogDiscardedFrameCount = 0;
        LastBacklogDiscardedFrameCount = 0;
        LastInputQueueDepth = 0;
        Array.Clear(backlogHasLatestFrame, 0, backlogHasLatestFrame.Length);
        anomalyDetector?.Reset();
    }

    private void ProcessRawFrame(
        SerialParser.RawSensorFrame frame,
        DateTime nowUtc,
        Action<int, Quaternion, Vector3, DateTime> onAcceptedFrame)
    {
        int deviceId = frame.DeviceId;
        if (deviceId < 0 || deviceId >= deviceCount)
            return;

        lastSourceBacklogAgeMs[deviceId] = Mathf.Max(0f, frame.SourceBacklogAgeMs);
        if (frame.SourceClockReliable &&
            frame.SourceBacklogAgeMs > Mathf.Max(50f, MaximumSourceBacklogAgeSeconds * 1000f))
        {
            staleSourceFrameCount[deviceId]++;
            return;
        }

        Quaternion raw = frame.Q;
        if (!IsFiniteNormalizedCandidate(raw))
        {
            invalidFrameCount[deviceId]++;
            return;
        }
        raw = NormalizeSafe(raw);

        bool wasAnomaly = false;
        Quaternion processed = anomalyDetector != null
            ? anomalyDetector.Process(deviceId, raw, out wasAnomaly)
            : raw;

        if (!IsFiniteNormalizedCandidate(processed))
        {
            invalidFrameCount[deviceId]++;
            return;
        }
        processed = NormalizeSafe(processed);

        if (wasAnomaly)
            anomalyFrameCount[deviceId]++;

        DateTime timestampUtc = frame.TimestampUtc == DateTime.MinValue
            ? nowUtc
            : frame.TimestampUtc;

        if (acceptedFrameCount[deviceId] > 0 &&
            lastReceiveUtc[deviceId] != DateTime.MinValue)
        {
            double intervalSeconds = (timestampUtc - lastReceiveUtc[deviceId]).TotalSeconds;
            if (intervalSeconds > 0.0001 && intervalSeconds < 5.0)
            {
                float instantHz = Mathf.Clamp((float)(1.0 / intervalSeconds), 0f, 1000f);
                smoothedFrameRateHz[deviceId] = smoothedFrameRateHz[deviceId] <= 0f
                    ? instantHz
                    : Mathf.Lerp(smoothedFrameRateHz[deviceId], instantHz, 0.18f);
            }

            lastAcceptedStepAngleDeg[deviceId] = Quaternion.Angle(
                latestAcceptedRotations[deviceId], processed);
        }
        else
        {
            smoothedFrameRateHz[deviceId] = 0f;
            lastAcceptedStepAngleDeg[deviceId] = 0f;
        }

        EnqueueSample(deviceId, new Sample
        {
            Rotation = processed,
            TimestampUtc = timestampUtc,
            Sequence = frame.Sequence
        });

        latestAcceptedRotations[deviceId] = processed;
        lastReceiveUtc[deviceId] = timestampUtc;
        lastSequence[deviceId] = frame.Sequence;
        acceptedFrameCount[deviceId]++;
        updatedThisTick[deviceId] = true;

        Vector3 e = processed.eulerAngles;
        onAcceptedFrame?.Invoke(deviceId, processed, e, timestampUtc);
    }

    private void EnqueueSample(int deviceId, Sample sample)
    {
        Queue<Sample> queue = histories[deviceId];

        // 保持同一半球，避免 q 与 -q 等价表示在插值时产生不必要的长路径。
        if (queue.Count > 0)
        {
            Sample previous = GetLast(queue);
            if (Quaternion.Dot(previous.Rotation, sample.Rotation) < 0f)
            {
                Quaternion q = sample.Rotation;
                sample.Rotation = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            }
        }

        queue.Enqueue(sample);
        while (queue.Count > MaxHistoryFrames)
            queue.Dequeue();
    }

    private void BuildSynchronizedSnapshot(DateTime nowUtc)
    {
        DateTime newestFreshTimestamp = DateTime.MinValue;
        for (int i = 0; i < deviceCount; i++)
        {
            double age = GetDataAgeSeconds(i, nowUtc);
            float timeout = GetEffectiveOfflineTimeoutSeconds(i);
            valid[i] = histories[i].Count > 0 && age <= timeout;
            if (valid[i] && lastReceiveUtc[i] > newestFreshTimestamp)
                newestFreshTimestamp = lastReceiveUtc[i];
        }

        if (newestFreshTimestamp == DateTime.MinValue)
        {
            SnapshotTimestampUtc = nowUtc;
            SnapshotIndex++;
            return;
        }

        DateTime targetTime = newestFreshTimestamp.AddSeconds(-Mathf.Max(0f, SynchronizationDelaySeconds));

        for (int i = 0; i < deviceCount; i++)
        {
            TrimOldSamples(histories[i], nowUtc.AddSeconds(-HistoryRetentionSeconds));
            if (!valid[i])
                continue; // 保留上一份旋转，但 valid=false，驱动器不得消费旧值。

            Quaternion sampled;
            if (TrySampleAt(histories[i], targetTime, i, out sampled))
                synchronizedRotations[i] = sampled;
        }

        SnapshotTimestampUtc = targetTime;
        SnapshotIndex++;
    }

    private bool TrySampleAt(
        Queue<Sample> queue,
        DateTime targetTime,
        int deviceId,
        out Quaternion result)
    {
        result = Quaternion.identity;
        if (queue == null || queue.Count == 0) return false;

        Sample before = default(Sample);
        Sample after = default(Sample);
        bool hasBefore = false;
        bool hasAfter = false;

        foreach (Sample sample in queue)
        {
            if (sample.TimestampUtc <= targetTime)
            {
                before = sample;
                hasBefore = true;
                continue;
            }

            after = sample;
            hasAfter = true;
            break;
        }

        if (hasBefore && hasAfter)
        {
            double gapSeconds = (after.TimestampUtc - before.TimestampUtc).TotalSeconds;
            if (gapSeconds > 0.000001 && gapSeconds <= Mathf.Max(0.01f, MaxInterpolationGapSeconds))
            {
                double numerator = (targetTime - before.TimestampUtc).TotalSeconds;
                float t = Mathf.Clamp01((float)(numerator / gapSeconds));
                result = Quaternion.Slerp(before.Rotation, after.Rotation, t).normalized;
                return true;
            }

            // 空洞过大时选择时间上更接近的一侧，禁止跨大间隔插值。
            double beforeDistance = Math.Abs((targetTime - before.TimestampUtc).TotalSeconds);
            double afterDistance = Math.Abs((after.TimestampUtc - targetTime).TotalSeconds);
            result = beforeDistance <= afterDistance ? before.Rotation : after.Rotation;
            return true;
        }

        if (hasBefore)
        {
            if (TryPredictAfterLatest(queue, targetTime, deviceId, out Quaternion predicted))
                result = predicted;
            else
                result = before.Rotation;
            return true;
        }

        if (hasAfter)
        {
            result = after.Rotation;
            return true;
        }

        result = GetLast(queue).Rotation;
        return true;
    }

    private bool TryPredictAfterLatest(
        Queue<Sample> queue,
        DateTime targetTime,
        int deviceId,
        out Quaternion predicted)
    {
        predicted = Quaternion.identity;
        if (!LowFrequencyPredictionEnabled || !IsPredictionDrivenDevice(deviceId) ||
            queue == null || queue.Count < 2)
            return false;

        Sample previous = default(Sample);
        Sample latest = default(Sample);
        int seen = 0;
        foreach (Sample sample in queue)
        {
            previous = latest;
            latest = sample;
            seen++;
        }
        if (seen < 2)
            return false;

        double sourceInterval = (latest.TimestampUtc - previous.TimestampUtc).TotalSeconds;
        double age = (targetTime - latest.TimestampUtc).TotalSeconds;
        if (sourceInterval < 0.05 ||
            sourceInterval > Math.Max(0.10, MaxPredictionSourceIntervalSeconds) ||
            age <= 0.0001)
            return false;

        float horizon = Mathf.Max(0.01f, MaxPredictionHorizonSeconds);
        float usedAge = Mathf.Min((float)age, horizon);
        float sourceStepDeg = Quaternion.Angle(previous.Rotation, latest.Rotation);
        if (sourceStepDeg < 0.35f || sourceStepDeg > 90f)
            return false;

        float angularSpeed = sourceStepDeg / Mathf.Max(0.001f, (float)sourceInterval);
        angularSpeed = Mathf.Min(
            angularSpeed,
            Mathf.Max(1f, MaxPredictionAngularSpeedDegPerSec));
        float extensionDeg = Mathf.Min(
            angularSpeed * usedAge,
            Mathf.Max(0f, MaxPredictionAngleDeg));
        if (extensionDeg <= 0.001f)
            return false;

        float extrapolationT = 1f + extensionDeg / sourceStepDeg;
        predicted = Quaternion.SlerpUnclamped(
            previous.Rotation,
            latest.Rotation,
            extrapolationT).normalized;
        return IsFiniteNormalizedCandidate(predicted);
    }

    private static bool IsPredictionDrivenDevice(int deviceId)
    {
        return deviceId == (int)SensorRole.LeftUpperArm ||
               deviceId == (int)SensorRole.RightUpperArm ||
               deviceId == (int)SensorRole.LeftThigh ||
               deviceId == (int)SensorRole.RightThigh;
    }

    private static void TrimOldSamples(Queue<Sample> queue, DateTime cutoffUtc)
    {
        while (queue.Count > 2 && queue.Peek().TimestampUtc < cutoffUtc)
            queue.Dequeue();
    }

    private static Sample GetLast(Queue<Sample> queue)
    {
        Sample last = default(Sample);
        foreach (Sample sample in queue)
            last = sample;
        return last;
    }

    private static long GetCounter(long[] values, int deviceId)
    {
        return deviceId >= 0 && deviceId < values.Length ? values[deviceId] : 0;
    }

    private static bool IsFiniteNormalizedCandidate(Quaternion q)
    {
        if (float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)) return false;
        if (float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z) || float.IsInfinity(q.w)) return false;
        float sqr = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        return sqr > 0.0000001f && !float.IsNaN(sqr) && !float.IsInfinity(sqr);
    }

    private static Quaternion NormalizeSafe(Quaternion q)
    {
        float sqr = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (sqr <= 0.0000001f || float.IsNaN(sqr) || float.IsInfinity(sqr))
            return Quaternion.identity;
        float inv = 1f / Mathf.Sqrt(sqr);
        return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
    }
}

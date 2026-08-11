
using System;
using UnityEngine;

/// <summary>
/// 传感器数据处理流水线（纯逻辑类，不继承 MonoBehaviour）。
/// 
/// 整体流程（由 Controller 在 Update/LateUpdate 中按顺序调用）：
///   阶段 1 — DequeueAll:          从 SerialParser 的线程安全队列中取出所有新帧
///   阶段 2 — TransformAll:        将传感器坐标系四元数转换为 Unity Avatar 空间
///   阶段 3 — UpdateStability:     统计每个设备的角速度，更新稳定性计数器
///   阶段 4 — TryPreCalibrate:     首次收到数据后自动执行一次校准（记录初始偏差）
///   阶段 5 — SyncAndUpdateTargets: 帧同步 + 时间插值 + 设置 RotationDriver 目标
///   阶段 6 — ApplyToBones:        (LateUpdate) 将目标旋转写入骨骼 localRotation
/// 
/// 外部依赖（需在项目中已存在）：
///   - SerialParser:       协议解析 + 帧队列
///   - RotationDriver:     旋转校准、平滑插值、角度限制、姿态应用
///   - StabilityMonitor:   设备角速度跟踪与系统稳定性判定
/// </summary>
public class SensorDataProcessor
{
    // ═══════════════════════════════════════════════════════════════
    //  字段
    // ═══════════════════════════════════════════════════════════════

    private readonly int deviceCount;                    // 传感器设备总数
    private readonly MotionCaptureConfig config;         // 全局配置引用

    // 每个设备的原始四元数（直接从串口解析得到）
    private readonly Quaternion[] rawQuaternions;

    // 经统一时间同步后的四元数（传感器空间 → Unity Avatar 空间），供实时骨骼驱动使用。
    private readonly Quaternion[] transformedQuaternions;

    // 每个设备最近一次有效原始帧对应的转换结果，不经过时间插值，仅用于标定。
    // 这样标定不会被同步目标时间在相邻采样点之间切换而反复重置。
    private readonly Quaternion[] calibrationQuaternions;

    // 每个设备的欧拉角分量（从原始四元数直接计算，用于日志和 UI 显示）
    private readonly float[] yaw, pitch, roll;

    // 帧同步阶段的复用缓存数组（避免每帧 new，减少 GC 压力）
    private readonly SerialParser.SensorFrame[] latestFrames;
    private readonly bool[] hasLatest;

    // V77.25 集中式数据中心：统一过滤、缓存、超时检测与时间同步。
    private readonly MotionDataHub dataHub;

    // 稳定性监控器：跟踪每个设备的角速度是否连续低于阈值
    private StabilityMonitor stabilityMonitor;

    // 旋转驱动器：负责校准、插值、限幅、最终姿态写入
    public RotationDriver Driver { get; private set; }

    // 角色根节点在 T-Pose 时的世界旋转，用于将传感器坐标补偿到角色朝向
    private Quaternion rootFacingOffset = Quaternion.identity;

    /// <summary>供外部读取的转换后四元数数组（UI 遥测表格使用）</summary>
    public Quaternion[] TransformedQuaternions => transformedQuaternions;
    public Quaternion[] CalibrationQuaternions => calibrationQuaternions;
    public MotionDataHub DataHub => dataHub;
    public long BacklogDiscardedFrameCount => dataHub != null
        ? dataHub.BacklogDiscardedFrameCount
        : 0;
    public int LastBacklogDiscardedFrameCount => dataHub != null
        ? dataHub.LastBacklogDiscardedFrameCount
        : 0;
    public int LastInputQueueDepth => dataHub != null
        ? dataHub.LastInputQueueDepth
        : 0;

    public bool IsDeviceOnline(int deviceId) => dataHub != null && dataHub.IsDeviceValid(deviceId);
    public bool WasDeviceUpdatedThisTick(int deviceId) => dataHub != null && dataHub.WasDeviceUpdatedThisTick(deviceId);
    public bool HasCalibrationSample(int deviceId) => dataHub != null && dataHub.HasAcceptedSample(deviceId);
    public double GetCalibrationSampleAgeSeconds(int deviceId) =>
        dataHub != null ? dataHub.GetLatestAcceptedDataAgeSeconds(deviceId, DateTime.UtcNow) : double.PositiveInfinity;
    public double GetDeviceDataAgeSeconds(int deviceId) => dataHub != null
        ? dataHub.GetDataAgeSeconds(deviceId, DateTime.UtcNow)
        : double.PositiveInfinity;
    public long GetCalibrationSampleSequence(int deviceId) => dataHub != null
        ? dataHub.GetLastSequence(deviceId)
        : -1;
    public long GetAcceptedFrameCount(int deviceId) => dataHub != null
        ? dataHub.GetAcceptedFrameCount(deviceId)
        : 0;
    public long GetInputSequenceGapCount(int deviceId) => dataHub != null
        ? dataHub.GetSequenceGapCount(deviceId)
        : 0;
    public float GetDeviceFrameRateHz(int deviceId) => dataHub != null
        ? dataHub.GetSmoothedFrameRateHz(deviceId)
        : 0f;
    public float GetDeviceEffectiveOfflineTimeoutSeconds(int deviceId) => dataHub != null
        ? dataHub.GetEffectiveOfflineTimeoutSeconds(deviceId)
        : 0f;
    public float GetLastAcceptedStepAngleDeg(int deviceId) => dataHub != null
        ? dataHub.GetLastAcceptedStepAngleDeg(deviceId)
        : 0f;
    public int GetStableFrameCount(int deviceId) => stabilityMonitor != null
        ? stabilityMonitor.GetStableFrameCount(deviceId)
        : 0;
    public bool IsDeviceStable(int deviceId) => stabilityMonitor != null &&
        stabilityMonitor.IsDeviceStable(deviceId, config.requiredStableFrames);

    /// <summary>
    /// 获取集中历史中时间配对后的两路姿态，并使用与骨骼驱动完全相同的
    /// 传感器坐标转换。用于06/07、08/09膝角测量，不改变实时快照。
    /// </summary>
    public bool TryGetTimePairedAvatarRotations(
        int firstDeviceId,
        int secondDeviceId,
        double maxSkewSeconds,
        out Quaternion firstAvatarRotation,
        out Quaternion secondAvatarRotation,
        out DateTime pairTimestampUtc,
        out double pairSkewSeconds)
    {
        firstAvatarRotation = Quaternion.identity;
        secondAvatarRotation = Quaternion.identity;
        pairTimestampUtc = DateTime.MinValue;
        pairSkewSeconds = double.PositiveInfinity;

        if (dataHub == null ||
            !dataHub.TryGetLatestTimePairedRotations(
                firstDeviceId,
                secondDeviceId,
                maxSkewSeconds,
                out Quaternion firstRaw,
                out Quaternion secondRaw,
                out pairTimestampUtc,
                out pairSkewSeconds))
            return false;

        firstAvatarRotation = MapSensorToAvatarSpace(firstDeviceId, firstRaw);
        secondAvatarRotation = MapSensorToAvatarSpace(secondDeviceId, secondRaw);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 初始化数据处理器。
    /// 分配所有内部数组，创建 StabilityMonitor 和 RotationDriver。
    /// </summary>
    /// <param name="config">全局配置资产</param>
    public SensorDataProcessor(MotionCaptureConfig config)
    {
        this.config = config;
        deviceCount = config.deviceCount;

        // 分配所有 per-device 数组
        rawQuaternions = new Quaternion[deviceCount];
        transformedQuaternions = new Quaternion[deviceCount];
        calibrationQuaternions = new Quaternion[deviceCount];
        yaw = new float[deviceCount];
        pitch = new float[deviceCount];
        roll = new float[deviceCount];
        latestFrames = new SerialParser.SensorFrame[deviceCount];
        hasLatest = new bool[deviceCount];

        // 初始化为 identity，避免初始值 (0,0,0,0) 导致除零或异常
        for (int i = 0; i < deviceCount; i++)
        {
            rawQuaternions[i] = Quaternion.identity;
            transformedQuaternions[i] = Quaternion.identity;
            calibrationQuaternions[i] = Quaternion.identity;
        }

        // 创建集中数据中心。异常过滤只在这里执行一次，串口解析器不再修改姿态数据。
        dataHub = new MotionDataHub(
            deviceCount,
            config.anomalyEnable,
            config.anomalyBufferSize,
            config.anomalyThreshold);

        // 创建稳定性监控器和旋转驱动器
        stabilityMonitor = new StabilityMonitor(deviceCount);
        // 参数：设备数、开启平滑、平滑速度、去抖阈值、初始关闭 Twist+Swing（使用 Euler 模式）
        Driver = new RotationDriver(deviceCount, true, config.smoothSpeed, config.debounceThresholdDeg, false);
    }

    // ═══════════════════════════════════════════════════════════════
    //  初始化 API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 设置角色根节点的初始朝向补偿。
    /// 传感器坐标转换到 Unity 空间后，还需要乘以角色在场景中的初始朝向，
    /// 否则角色面朝 Z+ 而传感器面朝其它方向时姿态会偏。
    /// </summary>
    public void SetRootFacingOffset(Quaternion offset) => rootFacingOffset = offset;

    /// <summary>
    /// 将角度限制和绑定姿态（T-Pose localRotation）传入 RotationDriver。
    /// 这些数据在运行期不变，仅初始化时设置一次。
    /// </summary>
    public void InitConstraints(Quaternion[] restLocalRotations)
    {
        int n = config.deviceCount;
        var min = (Vector3[])config.minLocalAngles.Clone();
        var max = (Vector3[])config.maxLocalAngles.Clone();
        Vector3 noLimit = new Vector3(999, 999, 999);
        min[0] = -noLimit;  // 左上臂：禁用欧拉钳制
        max[0] =  noLimit;
        min[2] = -noLimit;  // 右上臂：禁用欧拉钳制
        max[2] =  noLimit;
        Driver.SetConstraints(min, max, restLocalRotations);
    }


    // ═══════════════════════════════════════════════════════════════
    //  V77.25 集中处理入口
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 唯一的实时输入入口：一次性完成出队、异常过滤、超时判定、时间同步、
    /// 坐标转换和状态发布。Controller 每帧只能调用此方法一次，之后所有手臂、
    /// 躯干和腿部驱动器共享同一份 transformedQuaternions 快照。
    /// </summary>
    public int UpdateFromParser(
        SerialParser parser,
        MotionCaptureState state,
        Action<int, Quaternion, Vector3> onFrame)
    {
        if (parser == null || state == null || dataHub == null) return 0;

        int count = dataHub.UpdateFromParser(
            parser,
            DateTime.UtcNow,
            (deviceId, processedQ, euler, timestampUtc) =>
            {
                if (deviceId < 0 || deviceId >= deviceCount) return;

                // Unity eulerAngles=(x,y,z)，沿用原UI约定发布(roll=x,pitch=y,yaw=z)。
                roll[deviceId] = euler.x;
                pitch[deviceId] = euler.y;
                yaw[deviceId] = euler.z;
                state.NotifyEulerUpdated(deviceId,
                    new Vector3(roll[deviceId], pitch[deviceId], yaw[deviceId]));

                // 标定与稳定性都使用该设备刚收到的真实新帧。
                // 这样稳定计数不再随 Unity 渲染帧率增长，也不会重复累计同一个旧姿态。
                Quaternion mappedLatest = MapSensorToAvatarSpace(deviceId, processedQ);
                calibrationQuaternions[deviceId] = mappedLatest;
                stabilityMonitor.UpdateDevice(
                    deviceId,
                    mappedLatest,
                    true,
                    timestampUtc,
                    config.stableAngularSpeedDegPerSec,
                    config.unstableAngularSpeedDegPerSec,
                    config.requiredStableDurationSeconds,
                    config.requiredStableFrames,
                    dataHub.GetEffectiveOfflineTimeoutSeconds(deviceId));

                onFrame?.Invoke(deviceId, processedQ, euler);
            });

        // 实时驱动继续使用统一同步快照和断流超时门控。
        // 标定锁存值由上面的真实新帧回调更新，不受同步插值切点影响。
        for (int i = 0; i < deviceCount; i++)
        {
            bool isValid = dataHub.IsDeviceValid(i);
            state.SetDeviceHasData(i, isValid);
            if (!isValid)
            {
                stabilityMonitor.MarkUnavailable(i);
                continue;
            }

            rawQuaternions[i] = dataHub.GetSynchronizedRotation(i);
            transformedQuaternions[i] = MapSensorToAvatarSpace(i, rawQuaternions[i]);
            state.SetDeviceQuaternion(i, transformedQuaternions[i]);
        }

        return count;
    }

    // ═══════════════════════════════════════════════════════════════
    //  阶段 1：出队
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 SerialParser 的线程安全队列中一次性取出所有可用帧。
    /// 
    /// SerialParser 在后台线程中持续将串口字节流解析为 (deviceId, Quaternion)，
    /// 并推入队列。此方法在 Unity 主线程的 Update() 开头调用，
    /// 确保后续所有操作都在主线程执行（Transform 操作要求）。
    /// 
    /// 对于每个出队的帧：
    ///   1. 缓存原始四元数
    ///   2. 提取欧拉角分量（用于日志和 UI）
    ///   3. 标记该设备已有数据
    ///   4. 通过回调通知外部（Controller 用它写日志）
    /// </summary>
    /// <param name="parser">串口协议解析器</param>
    /// <param name="state">全局状态容器</param>
    /// <param name="onFrame">每个帧的回调：(设备ID, 四元数, 欧拉角)，可为 null</param>
    /// <returns>本次出队的帧数</returns>
    public int DequeueAll(SerialParser parser, MotionCaptureState state, Action<int, Quaternion, Vector3> onFrame)
    {
        int count = 0;
        int deviceId;
        Quaternion q;

        // 循环出队直到队列为空
        while (parser.TryDequeue(out deviceId, out q))
        {
            if (deviceId < 0 || deviceId >= deviceCount) continue;  // 丢弃非法 ID

            // 缓存原始四元数
            rawQuaternions[deviceId] = q;

            // 从四元数提取欧拉角分量
            // Unity 的 eulerAngles 返回 (x=pitch, y=yaw, z=roll)
            // 这里按硬件约定重新映射为 (yaw=z, pitch=y, roll=x)
            var e = q.eulerAngles;
            yaw[deviceId] = e.z;
            pitch[deviceId] = e.y;
            roll[deviceId] = e.x;

            // 标记该设备已收到数据 & 通知 UI 层欧拉角更新
            state.SetDeviceHasData(deviceId, true);
            state.NotifyEulerUpdated(deviceId, new Vector3(roll[deviceId], pitch[deviceId], yaw[deviceId]));

            // 回调：Controller 用这个写遥测日志
            onFrame?.Invoke(deviceId, q, e);
            count++;
        }
        return count;
    }

    // ═══════════════════════════════════════════════════════════════
    //  阶段 2：坐标系转换
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 将所有已收到数据的设备的原始四元数从传感器坐标系转换到 Unity Avatar 空间。
    /// 
    /// 转换过程：
    ///   1. RotationDriver.MapSensorToUnity(index, raw) — 按关节类型执行轴重映射
    ///   2. rootFacingOffset * unityQ — 叠加角色初始朝向补偿
    /// 
    /// 未收到数据的设备保持 identity（不影响骨骼）。
    /// </summary>
    public void TransformAll(MotionCaptureState state)
    {
        for (int i = 0; i < deviceCount; i++)
        {
            if (!state.GetDeviceHasData(i))
            {
                // 该设备尚未收到任何数据，保持 identity
                transformedQuaternions[i] = Quaternion.identity;
                continue;
            }
            // 坐标系转换：传感器 → Unity → 角色朝向补偿
            transformedQuaternions[i] = MapSensorToAvatarSpace(i, rawQuaternions[i]);
            // 同步到状态容器，供外部查询
            state.SetDeviceQuaternion(i, transformedQuaternions[i]);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  阶段 3：稳定性监控
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 更新每个设备的稳定性计数器。
    /// StabilityMonitor 内部跟踪每个设备帧间的角度变化量，
    /// 如果连续 N 帧都低于阈值，则该设备被标记为稳定。
    /// </summary>
    public void UpdateStability(MotionCaptureState state)
    {
        // 稳定计数已在 UpdateFromParser 的“真实新帧”回调中更新。
        // 此处只负责离线复位，禁止每个 Unity 渲染帧重复累计同一四元数。
        if (state == null || stabilityMonitor == null) return;
        for (int i = 0; i < deviceCount; i++)
        {
            if (!state.GetDeviceHasData(i))
                stabilityMonitor.MarkUnavailable(i);
        }
    }

    /// <summary>
    /// 综合判断系统整体稳定性（是否满足开始驱动的条件）。
    /// 根据配置可以要求全部设备稳定或仅部分设备稳定。
    /// </summary>
    public bool CheckStability(GameObject[] bones, bool[] deviceHasData)
    {
        return stabilityMonitor.IsSystemStable(
            bones, config.ignoreBonesWithoutObject, deviceHasData,
            config.requiredStableFrames, config.requireAllDevices, config.minStableDevices);
    }

    // ═══════════════════════════════════════════════════════════════
    //  阶段 4：预校准
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 在首次收到数据后自动执行一次校准。
    /// 校准过程：记录每个传感器当前旋转与对应骨骼初始旋转之间的偏差，
    /// 后续驱动时用这个偏差做"零点"补偿。
    /// 仅在尚未校准时执行，调用后 Driver.IsCalibrated 变为 true。
    /// </summary>
    public void TryPreCalibrate(GameObject[] bones)
    {
        if (Driver.IsCalibrated) return;   // 已校准则跳过
        Driver.Calibrate(bones, transformedQuaternions);
    }

    // ═══════════════════════════════════════════════════════════════
    //  阶段 5：帧同步 + 插值 + 更新目标
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 获取所有设备的最新帧，对齐到统一时间戳，并将目标旋转推送给 RotationDriver。
    /// 
    /// 为什么需要帧同步？
    ///   9 个传感器通过同一串口依次发送数据，到达时间有微小差异。
    ///   直接使用各自的最新帧会导致不同关节处于不同时刻的姿态，
    ///   引起轻微但可见的"时间撕裂"。
    /// 
    /// 同步策略：
    ///   1. 取所有设备最新帧中最晚的时间戳作为目标时间 (targetTime)
    ///   2. 对于时间戳不等于 targetTime 的设备，尝试在其帧历史中进行球面插值
    ///   3. 插值失败则回退使用该设备的最新帧
    ///   4. 若某设备完全没有最新帧，根据 requireAllDevices 决定是跳过还是用旧数据
    /// </summary>
    /// <param name="parser">解析器（用于获取帧历史和插值）</param>
    /// <param name="state">状态容器</param>
    /// <param name="bones">骨骼 GameObject 数组</param>
    /// <param name="requireAllDevices">是否要求所有设备都有数据才进行驱动</param>
    public void SyncAndUpdateTargets(SerialParser parser, MotionCaptureState state,
        GameObject[] bones, bool requireAllDevices)
    {
        // ── 第 1 步：收集所有设备的最新帧 ──
        DateTime? newestTime = null;
        DateTime? oldestTime = null;
        int latestCount = 0;

        for (int i = 0; i < deviceCount; i++) hasLatest[i] = false;

        for (int i = 0; i < deviceCount; i++)
        {
            if (parser.TryGetLatestFrame(i, out latestFrames[i]))
            {
                hasLatest[i] = true;
                latestCount++;
                // 追踪最新和最旧时间戳，用于确定同步目标时间
                if (!newestTime.HasValue || latestFrames[i].Timestamp > newestTime.Value)
                    newestTime = latestFrames[i].Timestamp;
                if (!oldestTime.HasValue || latestFrames[i].Timestamp < oldestTime.Value)
                    oldestTime = latestFrames[i].Timestamp;
            }
        }

        // 没有任何设备有最新帧，无法驱动
        if (latestCount == 0 || !newestTime.HasValue) return;

        // ── 第 2 步：以最晚时间戳为目标，逐设备对齐 ──
        DateTime targetTime = newestTime.Value;
        int devicesApplied = 0;

        for (int i = 0; i < deviceCount; i++)
        {
            if (!hasLatest[i])
            {
                // 该设备没有最新帧
                if (requireAllDevices) return;   // 严格模式：缺一不可，放弃本次驱动

                // 宽松模式：该设备之前收到过数据则沿用旧值
                if (state.GetDeviceHasData(i))
                {
                    transformedQuaternions[i] = MapSensorToAvatarSpace(i, rawQuaternions[i]);
                    devicesApplied++;
                }
                continue;
            }

            SerialParser.SensorFrame frame = latestFrames[i];
            if (frame.Timestamp != targetTime)
            {
                // 该设备的最新帧时间与目标不一致，尝试插值到 targetTime
                if (!parser.TryGetInterpolatedFrame(i, targetTime, out frame))
                    frame = latestFrames[i];   // 插值失败，回退使用最新帧
            }
            // 坐标系转换并更新
            transformedQuaternions[i] = MapSensorToAvatarSpace(i, frame.Q);
            devicesApplied++;
        }

        // 没有任何设备成功对齐，跳过
        if (devicesApplied == 0) return;

        // ── 第 3 步：确保已校准，然后推送目标给 RotationDriver ──
        if (!Driver.IsCalibrated)
            Driver.Calibrate(bones, transformedQuaternions);

        // 将 9 个设备的目标四元数一次性推送给驱动器
        // 驱动器内部会在 Apply() 时进行平滑插值和限幅
        Driver.UpdateTargets(transformedQuaternions);
    }

    // ═══════════════════════════════════════════════════════════════
    //  阶段 6：应用到骨骼
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 在 LateUpdate 中调用（确保在动画系统之后执行）。
    /// RotationDriver.Apply() 会将经过平滑插值和限幅处理的旋转
    /// 写入每个骨骼 GameObject 的 transform.localRotation。
    /// </summary>
    public void ApplyToBones(GameObject[] bones) => Driver.Apply(bones);

    // ═══════════════════════════════════════════════════════════════
    //  重置
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 全量重置：清空所有缓存数据，恢复骨骼到绑定姿态（T-Pose），
    /// 重建稳定性监控器。在用户点击"重置"时由 Controller 调用。
    /// </summary>
    public void Reset(GameObject[] bones, Quaternion[] restLocalRotations)
    {
        for (int i = 0; i < deviceCount; i++)
        {
            rawQuaternions[i] = Quaternion.identity;
            transformedQuaternions[i] = Quaternion.identity;
            calibrationQuaternions[i] = Quaternion.identity;
        }
        // 恢复所有骨骼到初始 localRotation
        Driver.ResetToRestPose(bones, restLocalRotations);
        dataHub?.Reset();
        // 重建稳定性监控器（清空历史角速度数据）
        stabilityMonitor = new StabilityMonitor(deviceCount);
    }

    /// <summary>仅重建稳定性监控器（不影响骨骼姿态）</summary>
    public void ResetStabilityMonitor()
    {
        stabilityMonitor = new StabilityMonitor(deviceCount);
    }

    // ═══════════════════════════════════════════════════════════════
    //  内部工具
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 将传感器原始四元数映射到 Unity Avatar 空间。
    /// 两步转换：
    ///   1. MapSensorToUnity: 传感器坐标系 → Unity 世界坐标系（轴重映射、手性变换）
    ///   2. rootFacingOffset *: 叠加角色在场景中的初始朝向
    /// </summary>
    private Quaternion MapSensorToAvatarSpace(int index, Quaternion rawSensorQ)
    {
        // 传感器安装角度补偿：将物理安装偏差从原始数据中消除
        // 在 MapSensorToUnity 之前应用，因为这是传感器硬件层面的偏差
        Vector3 offset = config.sensorMountingOffsets != null
                      && index < config.sensorMountingOffsets.Length
            ? config.sensorMountingOffsets[index]
            : Vector3.zero;
        if (offset != Vector3.zero)
            rawSensorQ = rawSensorQ * Quaternion.Euler(offset);

        var unityQ = RotationDriver.MapSensorToUnity(index, rawSensorQ);
        return rootFacingOffset * unityQ;
    }
}

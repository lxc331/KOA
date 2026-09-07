using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// 动捕诊断日志记录器。
/// 每行都是一条独立 JSON，既便于人工排查，也便于脚本逐行分析。
/// </summary>
public sealed class TelemetryLogger : IDisposable
{
    private const float RuntimeSnapshotIntervalSeconds = 0.5f;
    private const float FlushIntervalSeconds = 0.5f;

    public bool IsLogging => writer != null;
    public string CurrentLogPath { get; private set; } = "";
    public bool SaveEnabled { get; set; } = true;

    private string exportDirectory;
    private readonly string[] deviceNames;
    private StreamWriter writer;
    private float nextRuntimeSnapshotAt;
    private float nextFlushAt;
    private readonly long[] receivedFrameCounts;
    private readonly float[] lastReceiveAt;
    private readonly Quaternion[] lastRawQuaternions;
    private readonly bool[] hasLastRawQuaternion;

    public TelemetryLogger(string exportDirectory, string[] deviceNames = null)
    {
        this.exportDirectory = string.IsNullOrWhiteSpace(exportDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(exportDirectory);
        this.deviceNames = deviceNames ?? Array.Empty<string>();
        receivedFrameCounts = new long[this.deviceNames.Length];
        lastReceiveAt = new float[this.deviceNames.Length];
        lastRawQuaternions = new Quaternion[this.deviceNames.Length];
        hasLastRawQuaternion = new bool[this.deviceNames.Length];
    }

    public void SetExportDirectory(string directory)
    {
        if (IsLogging || string.IsNullOrWhiteSpace(directory)) return;
        exportDirectory = Path.GetFullPath(directory.Trim());
    }

    public string GetExportDirectory() => exportDirectory;

    public bool Open()
    {
        if (writer != null) return true;
        if (!SaveEnabled) return false;

        try
        {
            Directory.CreateDirectory(exportDirectory);
            string fileName = "motion_diagnostic_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".jsonl";
            CurrentLogPath = Path.Combine(exportDirectory, fileName);

            var stream = new FileStream(CurrentLogPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
            nextRuntimeSnapshotAt = 0f;
            nextFlushAt = Time.realtimeSinceStartup + FlushIntervalSeconds;
            Array.Clear(receivedFrameCounts, 0, receivedFrameCounts.Length);
            Array.Clear(hasLastRawQuaternion, 0, hasLastRawQuaternion.Length);
            Application.logMessageReceived += HandleUnityLog;

            WriteRecord(new SessionRecord
            {
                type = "session_start",
                utc = UtcNow(),
                realtimeSeconds = Time.realtimeSinceStartup,
                unityFrame = Time.frameCount,
                unityVersion = Application.unityVersion,
                productName = Application.productName,
                applicationVersion = Application.version,
                platform = Application.platform.ToString(),
                outputPath = CurrentLogPath
            }, true);

            Debug.Log($"[TelemetryLogger] 诊断日志开始记录: {CurrentLogPath}");
            return true;
        }
        catch (Exception ex)
        {
            Application.logMessageReceived -= HandleUnityLog;
            writer?.Dispose();
            writer = null;
            CurrentLogPath = "";
            Debug.LogError($"[TelemetryLogger] 无法创建诊断日志: {ex.Message}");
            return false;
        }
    }

    public void LogSessionConfiguration(MotionCaptureConfig config, GameObject[] bones,
        Quaternion[] restLocalRotations, string port, int baud)
    {
        if (!CanWrite() || config == null) return;

        int count = config.deviceCount;
        var bindings = new BindingRecord[count];
        for (int i = 0; i < count; i++)
        {
            GameObject bone = bones != null && i < bones.Length ? bones[i] : null;
            bindings[i] = new BindingRecord
            {
                index = i,
                deviceId = DeviceId(i),
                configuredBoneName = config.boneNames != null && i < config.boneNames.Length
                    ? config.boneNames[i] : "",
                boneFound = bone != null,
                resolvedBoneName = bone != null ? bone.name : "",
                restLocalQuaternion = restLocalRotations != null && i < restLocalRotations.Length
                    ? restLocalRotations[i] : Quaternion.identity
            };
        }

        WriteRecord(new ConfigurationRecord
        {
            type = "session_config",
            utc = UtcNow(),
            realtimeSeconds = Time.realtimeSinceStartup,
            unityFrame = Time.frameCount,
            port = port ?? "",
            baud = baud,
            deviceCount = count,
            requireAllDevices = config.requireAllDevices,
            minStableDevices = config.minStableDevices,
            requiredStableFrames = config.requiredStableFrames,
            maxAngularSpeedDeg = config.maxAngularSpeedDeg,
            smoothSpeed = config.smoothSpeed,
            debounceThresholdDeg = config.debounceThresholdDeg,
            anomalyEnabled = config.anomalyEnable,
            anomalyBufferSize = config.anomalyBufferSize,
            anomalyThresholdDeg = config.anomalyThreshold,
            lowerBodyJumpRejectDeg = config.lowerBodyJumpRejectDeg,
            lowerBodyJumpRecoveryFrames = config.lowerBodyJumpRecoveryFrames,
            lowerBodyJumpRecoveryToleranceDeg = config.lowerBodyJumpRecoveryToleranceDeg,
            rootMotionEnabled = config.rootMotionEnabled,
            rootMotionHorizontalEnabled = config.rootMotionHorizontalEnabled,
            rootMotionMaxDrop = config.rootMotionMaxDrop,
            rootMotionMaxHorizontalOffset = config.rootMotionMaxHorizontalOffset,
            minLocalAngles = config.minLocalAngles,
            maxLocalAngles = config.maxLocalAngles,
            bindings = bindings
        }, true);
    }

    /// <summary>记录解析器已经接受并出队的一帧原始传感器数据。</summary>
    public void LogFrame(int deviceIndex, Quaternion rawQuaternion, Vector3 rawEuler, SerialParser parser)
    {
        if (!CanWrite()) return;

        float now = Time.realtimeSinceStartup;
        long frameCount = -1;
        float intervalMs = -1f;
        float receiveHz = -1f;
        float angularDeltaDeg = -1f;
        float angularSpeedDegPerSecond = -1f;

        if (deviceIndex >= 0 && deviceIndex < receivedFrameCounts.Length)
        {
            frameCount = ++receivedFrameCounts[deviceIndex];
            if (hasLastRawQuaternion[deviceIndex])
            {
                float interval = now - lastReceiveAt[deviceIndex];
                intervalMs = interval * 1000f;
                if (interval > 0.000001f)
                {
                    receiveHz = 1f / interval;
                    angularDeltaDeg = Quaternion.Angle(lastRawQuaternions[deviceIndex], rawQuaternion);
                    angularSpeedDegPerSecond = angularDeltaDeg / interval;
                }
            }
            lastReceiveAt[deviceIndex] = now;
            lastRawQuaternions[deviceIndex] = rawQuaternion;
            hasLastRawQuaternion[deviceIndex] = true;
        }

        WriteRecord(new SensorFrameRecord
        {
            type = "sensor_frame",
            utc = UtcNow(),
            realtimeSeconds = now,
            unityFrame = Time.frameCount,
            deviceIndex = deviceIndex,
            deviceId = DeviceId(deviceIndex),
            boneName = DeviceName(deviceIndex),
            acceptedFrameCount = frameCount,
            rawQuaternion = rawQuaternion,
            rawQuaternionNorm = QuaternionNorm(rawQuaternion),
            rawEuler = rawEuler,
            receiveIntervalMs = intervalMs,
            receiveHz = receiveHz,
            angularDeltaDeg = angularDeltaDeg,
            angularSpeedDegPerSecond = angularSpeedDegPerSecond,
            parserQueueCount = ReadLong(parser, "QueueCount"),
            checksumFailCount = ReadLong(parser, "ChecksumFailCount"),
            parityErrorCount = ReadLong(parser, "ParityErrorCount"),
            frameErrorCount = ReadLong(parser, "FrameErrorCount"),
            overrunErrorCount = ReadLong(parser, "OverrunErrorCount"),
            droppedFrameCount = ReadDroppedFrameCount(parser, deviceIndex)
        });
    }

    /// <summary>以 2 Hz 记录整个运动链路，放在 LateUpdate 后可看到骨骼最终姿态。</summary>
    public void LogRuntimeSnapshot(MotionCaptureState state, SerialManager serial,
        SensorDataProcessor processor, GameObject[] bones, Quaternion[] restLocalRotations,
        Transform avatarRoot, RootMotionSolver rootSolver, int baud, bool requireAllDevices)
    {
        if (!CanWrite() || state == null || processor == null) return;

        float now = Time.realtimeSinceStartup;
        if (now < nextRuntimeSnapshotAt)
        {
            FlushIfDue(now);
            return;
        }
        nextRuntimeSnapshotAt = now + RuntimeSnapshotIntervalSeconds;

        SerialParser parser = serial?.Parser;
        Quaternion[] raw = processor.RawQuaternions;
        Quaternion[] transformed = processor.TransformedQuaternions;
        int[] stableCounts = processor.StableCounts;
        Quaternion[] targets = ReadQuaternionArray(processor.Driver, "Targets");
        int count = Math.Max(deviceNames.Length, transformed != null ? transformed.Length : 0);
        var devices = new DeviceSnapshotRecord[count];

        for (int i = 0; i < count; i++)
        {
            Quaternion rawQ = GetQuaternion(raw, i);
            Quaternion transformedQ = GetQuaternion(transformed, i);
            Quaternion targetQ = GetQuaternion(targets, i);
            bool hasTarget = targets != null && i < targets.Length;
            GameObject bone = bones != null && i < bones.Length ? bones[i] : null;
            Quaternion restQ = GetQuaternion(restLocalRotations, i);

            bool hasLatest = false;
            Quaternion latestQ = Quaternion.identity;
            string latestTimestamp = "";
            float latestAgeMs = -1f;
            if (parser != null && parser.TryGetLatestFrame(i, out SerialParser.SensorFrame latest))
            {
                hasLatest = true;
                latestQ = latest.Q;
                latestTimestamp = latest.Timestamp.ToString("O");
                latestAgeMs = (float)(DateTime.Now - latest.Timestamp).TotalMilliseconds;
            }

            devices[i] = new DeviceSnapshotRecord
            {
                index = i,
                deviceId = DeviceId(i),
                boneName = DeviceName(i),
                hasData = state.GetDeviceHasData(i),
                acceptedFrameCount = i < receivedFrameCounts.Length ? receivedFrameCounts[i] : 0,
                stableCount = stableCounts != null && i < stableCounts.Length ? stableCounts[i] : -1,
                rawQuaternion = rawQ,
                rawEuler = rawQ.eulerAngles,
                transformedQuaternion = transformedQ,
                transformedEuler = transformedQ.eulerAngles,
                hasLatestFrame = hasLatest,
                latestFrameQuaternion = latestQ,
                latestFrameEuler = latestQ.eulerAngles,
                latestFrameTimestamp = latestTimestamp,
                latestFrameAgeMs = latestAgeMs,
                hasDriverTarget = hasTarget,
                driverTargetQuaternion = targetQ,
                driverTargetEuler = targetQ.eulerAngles,
                droppedFrameCount = ReadDroppedFrameCount(parser, i),
                lowerBodyGuardRejectedFrameCount =
                    processor.LowerBodyGuardRejectedFrameCounts != null &&
                    i < processor.LowerBodyGuardRejectedFrameCounts.Length
                        ? processor.LowerBodyGuardRejectedFrameCounts[i] : 0,
                boneFound = bone != null,
                boneLocalQuaternion = bone != null ? bone.transform.localRotation : Quaternion.identity,
                boneLocalEuler = bone != null ? bone.transform.localEulerAngles : Vector3.zero,
                boneWorldQuaternion = bone != null ? bone.transform.rotation : Quaternion.identity,
                boneWorldPosition = bone != null ? bone.transform.position : Vector3.zero,
                restLocalQuaternion = restQ,
                boneFromRestAngleDeg = bone != null ? Quaternion.Angle(restQ, bone.transform.localRotation) : -1f,
                boneToDriverTargetAngleDeg = bone != null && hasTarget
                    ? Quaternion.Angle(bone.transform.localRotation, targetQ) : -1f,
                transformedToDriverTargetAngleDeg = hasTarget
                    ? Quaternion.Angle(transformedQ, targetQ) : -1f
            };
        }

        WriteRecord(new RuntimeSnapshotRecord
        {
            type = "runtime_snapshot",
            utc = UtcNow(),
            realtimeSeconds = now,
            unityFrame = Time.frameCount,
            deltaTimeMs = Time.unscaledDeltaTime * 1000f,
            approximateFps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f,
            port = serial?.CurrentPort ?? "",
            baud = baud,
            serialConnected = serial != null && serial.IsConnected,
            parserPortOpen = ReadBool(parser, "IsPortOpen"),
            parserQueueCount = ReadLong(parser, "QueueCount"),
            checksumFailCount = ReadLong(parser, "ChecksumFailCount"),
            parityErrorCount = ReadLong(parser, "ParityErrorCount"),
            frameErrorCount = ReadLong(parser, "FrameErrorCount"),
            overrunErrorCount = ReadLong(parser, "OverrunErrorCount"),
            hasAnyData = state.HasAnyData,
            calibrated = state.IsCalibrated,
            driving = state.IsDriving,
            stable = state.IsStable,
            requireAllDevices = requireAllDevices,
            syncStatus = processor.LastSyncStatus,
            syncLatestDeviceCount = processor.LastSyncLatestCount,
            syncAppliedDeviceCount = processor.LastSyncDevicesApplied,
            syncSkewMs = processor.LastSyncSkewMilliseconds,
            avatarRootPosition = avatarRoot != null ? avatarRoot.position : Vector3.zero,
            avatarRootRotation = avatarRoot != null ? avatarRoot.rotation : Quaternion.identity,
            rootMotionEnabled = rootSolver != null && rootSolver.Enabled,
            rootMotionInitialized = rootSolver != null && rootSolver.IsInitialized,
            rootVerticalOffset = rootSolver != null ? rootSolver.CurrentVerticalOffset : 0f,
            rootHorizontalOffset = rootSolver != null ? rootSolver.CurrentHorizontalOffset : Vector2.zero,
            groundY = rootSolver != null ? rootSolver.GroundY : 0f,
            devices = devices
        });
        FlushIfDue(now);
    }

    public void LogEvent(string name, string detail = "")
    {
        if (!CanWrite()) return;
        WriteRecord(new EventRecord
        {
            type = "event",
            utc = UtcNow(),
            realtimeSeconds = Time.realtimeSinceStartup,
            unityFrame = Time.frameCount,
            name = name ?? "",
            detail = detail ?? ""
        }, true);
    }

    public void LogState(MotionCaptureState state)
    {
        if (!CanWrite() || state == null) return;
        WriteRecord(new StateRecord
        {
            type = "state_change",
            utc = UtcNow(),
            realtimeSeconds = Time.realtimeSinceStartup,
            unityFrame = Time.frameCount,
            connected = state.IsConnected,
            hasAnyData = state.HasAnyData,
            calibrated = state.IsCalibrated,
            driving = state.IsDriving,
            stable = state.IsStable
        }, true);
    }

    public void SyncState(bool isConnected)
    {
        if (!SaveEnabled && writer != null)
            Close("save_disabled");
        else if (SaveEnabled && writer == null && isConnected)
            Open();
    }

    public void Close(string reason = "closed")
    {
        if (writer == null) return;
        Application.logMessageReceived -= HandleUnityLog;
        try
        {
            WriteRecord(new EventRecord
            {
                type = "session_end",
                utc = UtcNow(),
                realtimeSeconds = Time.realtimeSinceStartup,
                unityFrame = Time.frameCount,
                name = reason ?? "closed",
                detail = ""
            }, true);
            writer.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TelemetryLogger] 关闭日志时发生异常: {ex.Message}");
        }
        finally
        {
            writer = null;
        }
    }

    public void Dispose() => Close("disposed");

    private bool CanWrite() => SaveEnabled && writer != null;

    private void WriteRecord(object record, bool flush = false)
    {
        if (writer == null) return;
        try
        {
            writer.WriteLine(JsonUtility.ToJson(record));
            if (flush) writer.Flush();
        }
        catch (Exception ex)
        {
            Application.logMessageReceived -= HandleUnityLog;
            try { writer.Dispose(); } catch { }
            writer = null;
            Debug.LogError($"[TelemetryLogger] 写入日志失败，已停止记录: {ex.Message}");
        }
    }

    private void FlushIfDue(float now)
    {
        if (writer == null || now < nextFlushAt) return;
        try { writer.Flush(); }
        catch { }
        nextFlushAt = now + FlushIntervalSeconds;
    }

    private void HandleUnityLog(string condition, string stackTrace, LogType type)
    {
        if (writer == null || type == LogType.Log) return;
        if (!string.IsNullOrEmpty(condition) && condition.StartsWith("[TelemetryLogger]")) return;
        WriteRecord(new UnityLogRecord
        {
            type = "unity_log",
            utc = UtcNow(),
            realtimeSeconds = Time.realtimeSinceStartup,
            unityFrame = Time.frameCount,
            severity = type.ToString(),
            message = condition ?? "",
            stackTrace = stackTrace ?? ""
        }, true);
    }

    private string DeviceName(int index)
    {
        return index >= 0 && index < deviceNames.Length ? deviceNames[index] ?? "" : "";
    }

    private static string DeviceId(int index) => index >= 0 ? (index + 1).ToString("00") : "invalid";

    private static string UtcNow() => DateTime.UtcNow.ToString("O");

    private static float QuaternionNorm(Quaternion q)
    {
        return Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
    }

    private static Quaternion GetQuaternion(Quaternion[] values, int index)
    {
        return values != null && index >= 0 && index < values.Length ? values[index] : Quaternion.identity;
    }

    private static object ReadMember(object instance, string name)
    {
        if (instance == null) return null;
        try
        {
            Type type = instance.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null) return property.GetValue(instance, null);
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field?.GetValue(instance);
        }
        catch { return null; }
    }

    private static long ReadLong(object instance, string name)
    {
        try
        {
            object value = ReadMember(instance, name);
            return value == null ? -1 : Convert.ToInt64(value);
        }
        catch { return -1; }
    }

    private static bool ReadBool(object instance, string name)
    {
        try
        {
            object value = ReadMember(instance, name);
            return value != null && Convert.ToBoolean(value);
        }
        catch { return false; }
    }

    private static Quaternion[] ReadQuaternionArray(object instance, string name)
    {
        return ReadMember(instance, name) as Quaternion[];
    }

    private static long ReadDroppedFrameCount(object parser, int deviceIndex)
    {
        if (parser == null || deviceIndex < 0) return -1;
        try
        {
            MethodInfo method = parser.GetType().GetMethod("GetDroppedFrameCount",
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int) }, null);
            object value = method?.Invoke(parser, new object[] { deviceIndex });
            return value == null ? -1 : Convert.ToInt64(value);
        }
        catch { return -1; }
    }

    [Serializable]
    private class SessionRecord
    {
        public string type, utc;
        public float realtimeSeconds;
        public int unityFrame;
        public string unityVersion, productName, applicationVersion, platform, outputPath;
    }

    [Serializable]
    private class ConfigurationRecord
    {
        public string type, utc, port;
        public float realtimeSeconds;
        public int unityFrame, baud, deviceCount, minStableDevices, requiredStableFrames, anomalyBufferSize;
        public int lowerBodyJumpRecoveryFrames;
        public bool requireAllDevices, anomalyEnabled, rootMotionEnabled, rootMotionHorizontalEnabled;
        public float maxAngularSpeedDeg, smoothSpeed, debounceThresholdDeg, anomalyThresholdDeg;
        public float lowerBodyJumpRejectDeg, lowerBodyJumpRecoveryToleranceDeg;
        public float rootMotionMaxDrop, rootMotionMaxHorizontalOffset;
        public Vector3[] minLocalAngles, maxLocalAngles;
        public BindingRecord[] bindings;
    }

    [Serializable]
    private class BindingRecord
    {
        public int index;
        public string deviceId, configuredBoneName, resolvedBoneName;
        public bool boneFound;
        public Quaternion restLocalQuaternion;
    }

    [Serializable]
    private class SensorFrameRecord
    {
        public string type, utc, deviceId, boneName;
        public float realtimeSeconds;
        public int unityFrame, deviceIndex;
        public long acceptedFrameCount;
        public Quaternion rawQuaternion;
        public float rawQuaternionNorm;
        public Vector3 rawEuler;
        public float receiveIntervalMs, receiveHz, angularDeltaDeg, angularSpeedDegPerSecond;
        public long parserQueueCount, checksumFailCount, parityErrorCount, frameErrorCount, overrunErrorCount;
        public long droppedFrameCount;
    }

    [Serializable]
    private class RuntimeSnapshotRecord
    {
        public string type, utc, port, syncStatus;
        public float realtimeSeconds, deltaTimeMs, approximateFps, syncSkewMs;
        public int unityFrame, baud, syncLatestDeviceCount, syncAppliedDeviceCount;
        public bool serialConnected, parserPortOpen, hasAnyData, calibrated, driving, stable, requireAllDevices;
        public long parserQueueCount, checksumFailCount, parityErrorCount, frameErrorCount, overrunErrorCount;
        public Vector3 avatarRootPosition;
        public Quaternion avatarRootRotation;
        public bool rootMotionEnabled, rootMotionInitialized;
        public float rootVerticalOffset, groundY;
        public Vector2 rootHorizontalOffset;
        public DeviceSnapshotRecord[] devices;
    }

    [Serializable]
    private class DeviceSnapshotRecord
    {
        public int index, stableCount;
        public string deviceId, boneName, latestFrameTimestamp;
        public bool hasData, hasLatestFrame, hasDriverTarget, boneFound;
        public long acceptedFrameCount, droppedFrameCount, lowerBodyGuardRejectedFrameCount;
        public Quaternion rawQuaternion, transformedQuaternion, latestFrameQuaternion, driverTargetQuaternion;
        public Quaternion boneLocalQuaternion, boneWorldQuaternion, restLocalQuaternion;
        public Vector3 rawEuler, transformedEuler, latestFrameEuler, driverTargetEuler;
        public Vector3 boneLocalEuler, boneWorldPosition;
        public float latestFrameAgeMs, boneFromRestAngleDeg, boneToDriverTargetAngleDeg;
        public float transformedToDriverTargetAngleDeg;
    }

    [Serializable]
    private class EventRecord
    {
        public string type, utc, name, detail;
        public float realtimeSeconds;
        public int unityFrame;
    }

    [Serializable]
    private class StateRecord
    {
        public string type, utc;
        public float realtimeSeconds;
        public int unityFrame;
        public bool connected, hasAnyData, calibrated, driving, stable;
    }

    [Serializable]
    private class UnityLogRecord
    {
        public string type, utc, severity, message, stackTrace;
        public float realtimeSeconds;
        public int unityFrame;
    }
}

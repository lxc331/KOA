using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 面向远程排障和AI分析的增量会话日志。
/// 每次连接前立即创建UTF-8 JSONL文件；每一行都是独立JSON对象，程序异常退出时
/// 已经写入的内容仍然可读。这里只记录状态/链路摘要，不替代逐帧Excel姿态数据。
/// </summary>
public sealed class AiDiagnosticLogger : IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly object sync = new object();
    private StreamWriter writer;
    private DateTime sessionStartUtc;
    private bool unityLogSubscribed;
    private readonly Dictionary<string, UnityLogRepeatState> unityLogRepeats =
        new Dictionary<string, UnityLogRepeatState>(StringComparer.Ordinal);
    private static readonly TimeSpan UnityLogRepeatInterval = TimeSpan.FromSeconds(2d);
    private const int MaxTrackedUnityLogSignatures = 512;

    private sealed class UnityLogRepeatState
    {
        public DateTime LastWrittenUtc;
        public int Suppressed;
    }

    public bool IsLogging
    {
        get { lock (sync) return writer != null; }
    }

    public string CurrentPath { get; private set; } = string.Empty;
    public string LastError { get; private set; } = string.Empty;

    public struct SensorSnapshot
    {
        public int Id;
        public string Role;
        public bool Required;
        public bool Online;
        public bool RuntimeReady;
        public bool Stable;
        public string Calibration;
        public float ReceiveHz;
        public float SourceHz;
        public double AgeMs;
        public float DeliveryPercent;
        public long SourceLost;
        public long SourceDuplicate;
        public long SourceOutOfOrder;
        public long SourceRestart;
        public long DuplicateLogicalId;
        public uint HardwareId;
        public uint SourceSequence;
        public uint SenderTickMs;
        public int SourceFlags;
        public bool SourceClockReliable;
        public bool SourceMainClockHealthy;
        public bool SourceSlottedTransmit;
        public bool SourceLinkSynchronized;
        public float SourceBacklogAgeMs;
        public float SourceMaximumBacklogAgeMs;
        public long SourceStaleRejected;
        public long InputSequenceGap;
        public int CalibrationAccepted;
        public int CalibrationRequired;
        public int CalibrationRejected;
        public int CalibrationRestarts;
        public int RuntimeFaults;
        public bool LegPairRequired;
        public bool LegPairFresh;
        public double LegPairSkewMs;
        public double LegPairAgeMs;
        public long LegPairHoldCount;
        public Quaternion Q;
    }

    public struct ParserSnapshot
    {
        public bool Connected;
        public string Port;
        public int Baud;
        public int PayloadLength;
        public int XorFailures;
        public int CrcFailures;
        public int InvalidPayloadLengths;
        public int InvalidQuaternions;
        public int InvalidDeviceIds;
        public int ParityErrors;
        public int FrameErrors;
        public int OverrunErrors;
        public int DuplicateIdConflicts;
        public int QueueDepth;
        public int QueueCapacity;
        public int QueueDrops;
        public long BacklogDiscarded;
    }

    public bool Open(
        string directory,
        string buildVersion,
        string projectName,
        string unityVersion,
        string port,
        int baud,
        int deviceCount)
    {
        Close("new_session");
        try
        {
            string outputDirectory = string.IsNullOrWhiteSpace(directory)
                ? Directory.GetCurrentDirectory()
                : directory;
            Directory.CreateDirectory(outputDirectory);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", Invariant);
            string path = Path.Combine(outputDirectory, $"AI诊断_{stamp}.jsonl");
            FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            StreamWriter newWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            lock (sync)
            {
                writer = newWriter;
                CurrentPath = path;
                LastError = string.Empty;
                sessionStartUtc = DateTime.UtcNow;
                unityLogRepeats.Clear();
            }

            WriteJsonLine(
                "{\"kind\":\"session_start\",\"schema\":1," + CommonFields() +
                ",\"build\":" + Quote(buildVersion) +
                ",\"project\":" + Quote(projectName) +
                ",\"unity\":" + Quote(unityVersion) +
                ",\"platform\":" + Quote(Application.platform.ToString()) +
                ",\"os\":" + Quote(SystemInfo.operatingSystem) +
                ",\"port\":" + Quote(port) +
                ",\"baud\":" + baud.ToString(Invariant) +
                ",\"device_count\":" + deviceCount.ToString(Invariant) +
                ",\"note\":\"请将本jsonl与同次测试Excel、视频一起发送；每行均可独立分析\"}");

            Application.logMessageReceivedThreaded += OnUnityLog;
            unityLogSubscribed = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            lock (sync)
            {
                writer?.Dispose();
                writer = null;
                CurrentPath = string.Empty;
            }
            return false;
        }
    }

    public void LogEvent(string eventName, string state, string message, string details = "")
    {
        WriteJsonLine(
            "{\"kind\":\"event\"," + CommonFields() +
            ",\"event\":" + Quote(eventName) +
            ",\"state\":" + Quote(state) +
            ",\"message\":" + Quote(message) +
            ",\"details\":" + Quote(details) + "}");
    }

    public void LogSnapshot(
        string state,
        string calibrationStatus,
        string lastRuntimeFault,
        ParserSnapshot parser,
        SensorSnapshot[] sensors)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\"kind\":\"snapshot\",").Append(CommonFields());
        sb.Append(",\"state\":").Append(Quote(state));
        sb.Append(",\"calibration_status\":").Append(Quote(calibrationStatus));
        sb.Append(",\"last_runtime_fault\":").Append(Quote(lastRuntimeFault));
        sb.Append(",\"serial\":{");
        AppendBoolean(sb, "connected", parser.Connected); sb.Append(',');
        AppendString(sb, "port", parser.Port); sb.Append(',');
        AppendNumber(sb, "baud", parser.Baud); sb.Append(',');
        AppendNumber(sb, "payload_len", parser.PayloadLength); sb.Append(',');
        AppendNumber(sb, "xor_fail", parser.XorFailures); sb.Append(',');
        AppendNumber(sb, "crc_fail", parser.CrcFailures); sb.Append(',');
        AppendNumber(sb, "invalid_len", parser.InvalidPayloadLengths); sb.Append(',');
        AppendNumber(sb, "invalid_quaternion", parser.InvalidQuaternions); sb.Append(',');
        AppendNumber(sb, "invalid_device_id", parser.InvalidDeviceIds); sb.Append(',');
        AppendNumber(sb, "parity_error", parser.ParityErrors); sb.Append(',');
        AppendNumber(sb, "frame_error", parser.FrameErrors); sb.Append(',');
        AppendNumber(sb, "overrun_error", parser.OverrunErrors); sb.Append(',');
        AppendNumber(sb, "duplicate_id_conflict", parser.DuplicateIdConflicts); sb.Append(',');
        AppendNumber(sb, "queue_depth", parser.QueueDepth); sb.Append(',');
        AppendNumber(sb, "queue_capacity", parser.QueueCapacity); sb.Append(',');
        AppendNumber(sb, "queue_drop", parser.QueueDrops); sb.Append(',');
        AppendNumber(sb, "backlog_discarded", parser.BacklogDiscarded);
        sb.Append("},\"sensors\":[");

        if (sensors != null)
        {
            for (int i = 0; i < sensors.Length; i++)
            {
                if (i > 0) sb.Append(',');
                SensorSnapshot s = sensors[i];
                sb.Append('{');
                AppendNumber(sb, "id", s.Id); sb.Append(',');
                AppendString(sb, "role", s.Role); sb.Append(',');
                AppendBoolean(sb, "required", s.Required); sb.Append(',');
                AppendBoolean(sb, "online", s.Online); sb.Append(',');
                AppendBoolean(sb, "runtime_ready", s.RuntimeReady); sb.Append(',');
                AppendBoolean(sb, "stable", s.Stable); sb.Append(',');
                AppendString(sb, "calibration", s.Calibration); sb.Append(',');
                AppendFloat(sb, "receive_hz", s.ReceiveHz); sb.Append(',');
                AppendFloat(sb, "source_hz", s.SourceHz); sb.Append(',');
                AppendDoubleOrNull(sb, "age_ms", s.AgeMs); sb.Append(',');
                AppendFloat(sb, "delivery_percent", s.DeliveryPercent); sb.Append(',');
                AppendNumber(sb, "source_lost", s.SourceLost); sb.Append(',');
                AppendNumber(sb, "source_duplicate", s.SourceDuplicate); sb.Append(',');
                AppendNumber(sb, "source_out_of_order", s.SourceOutOfOrder); sb.Append(',');
                AppendNumber(sb, "source_restart", s.SourceRestart); sb.Append(',');
                AppendNumber(sb, "duplicate_logical_id", s.DuplicateLogicalId); sb.Append(',');
                AppendString(sb, "hardware_id", s.HardwareId == 0u ? "" : s.HardwareId.ToString("X8", Invariant)); sb.Append(',');
                AppendNumber(sb, "source_sequence", s.SourceSequence); sb.Append(',');
                AppendNumber(sb, "sender_tick_ms", s.SenderTickMs); sb.Append(',');
                AppendNumber(sb, "source_flags", s.SourceFlags); sb.Append(',');
                AppendBoolean(sb, "source_clock_reliable", s.SourceClockReliable); sb.Append(',');
                AppendBoolean(sb, "source_main_clock_healthy", s.SourceMainClockHealthy); sb.Append(',');
                AppendBoolean(sb, "source_slotted_transmit", s.SourceSlottedTransmit); sb.Append(',');
                AppendBoolean(sb, "source_link_synchronized", s.SourceLinkSynchronized); sb.Append(',');
                AppendFloat(sb, "source_backlog_age_ms", s.SourceBacklogAgeMs); sb.Append(',');
                AppendFloat(sb, "source_max_backlog_age_ms", s.SourceMaximumBacklogAgeMs); sb.Append(',');
                AppendNumber(sb, "source_stale_rejected", s.SourceStaleRejected); sb.Append(',');
                AppendNumber(sb, "input_sequence_gap", s.InputSequenceGap); sb.Append(',');
                AppendNumber(sb, "calibration_accepted", s.CalibrationAccepted); sb.Append(',');
                AppendNumber(sb, "calibration_required", s.CalibrationRequired); sb.Append(',');
                AppendNumber(sb, "calibration_rejected", s.CalibrationRejected); sb.Append(',');
                AppendNumber(sb, "calibration_restarts", s.CalibrationRestarts); sb.Append(',');
                AppendNumber(sb, "runtime_faults", s.RuntimeFaults); sb.Append(',');
                AppendBoolean(sb, "leg_pair_required", s.LegPairRequired); sb.Append(',');
                AppendBoolean(sb, "leg_pair_fresh", s.LegPairFresh); sb.Append(',');
                AppendDoubleOrNull(sb, "leg_pair_skew_ms", s.LegPairSkewMs); sb.Append(',');
                AppendDoubleOrNull(sb, "leg_pair_age_ms", s.LegPairAgeMs); sb.Append(',');
                AppendNumber(sb, "leg_pair_hold_count", s.LegPairHoldCount); sb.Append(',');
                sb.Append("\"q\":[")
                    .Append(Float(s.Q.x)).Append(',')
                    .Append(Float(s.Q.y)).Append(',')
                    .Append(Float(s.Q.z)).Append(',')
                    .Append(Float(s.Q.w)).Append(']');
                sb.Append('}');
            }
        }
        sb.Append("]}");
        WriteJsonLine(sb.ToString());
    }

    public void Close(string reason)
    {
        if (unityLogSubscribed)
        {
            Application.logMessageReceivedThreaded -= OnUnityLog;
            unityLogSubscribed = false;
        }

        lock (sync)
        {
            if (writer == null) return;
            try
            {
                // 高频相同报错在运行中限流，关闭时补一条汇总，既保留次数又避免磁盘IO反过来干扰串口。
                foreach (KeyValuePair<string, UnityLogRepeatState> pair in unityLogRepeats)
                {
                    if (pair.Value.Suppressed <= 0) continue;
                    writer.WriteLine("{\"kind\":\"unity_log_repeat_summary\"," + CommonFieldsUnsafe() +
                                     ",\"signature\":" + Quote(pair.Key) +
                                     ",\"suppressed\":" + pair.Value.Suppressed.ToString(Invariant) + "}");
                }
                writer.WriteLine("{\"kind\":\"session_end\"," + CommonFieldsUnsafe() +
                                 ",\"reason\":" + Quote(reason) + "}");
                writer.Flush();
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
            }
            finally
            {
                writer.Dispose();
                writer = null;
            }
        }
    }

    public void Dispose() => Close("dispose");

    private void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        if (type != LogType.Warning && type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
            return;

        string signature = type + ":" + (condition ?? string.Empty);
        int suppressed;
        lock (sync)
        {
            if (writer == null) return;
            DateTime now = DateTime.UtcNow;
            if (!unityLogRepeats.ContainsKey(signature) &&
                unityLogRepeats.Count >= MaxTrackedUnityLogSignatures)
            {
                signature = type + ":<additional_unique_messages>";
            }
            if (!unityLogRepeats.TryGetValue(signature, out UnityLogRepeatState repeat))
            {
                repeat = new UnityLogRepeatState();
                unityLogRepeats[signature] = repeat;
            }
            else if (now - repeat.LastWrittenUtc < UnityLogRepeatInterval)
            {
                repeat.Suppressed++;
                return;
            }

            suppressed = repeat.Suppressed;
            repeat.Suppressed = 0;
            repeat.LastWrittenUtc = now;
        }

        string stack = type == LogType.Warning ? string.Empty : stackTrace;
        WriteJsonLine(
            "{\"kind\":\"unity_log\"," + CommonFields() +
            ",\"level\":" + Quote(type.ToString()) +
            ",\"message\":" + Quote(condition) +
            ",\"stack\":" + Quote(stack) +
            ",\"repeat_suppressed_since_previous\":" + suppressed.ToString(Invariant) + "}");
    }

    private void WriteJsonLine(string json)
    {
        lock (sync)
        {
            if (writer == null) return;
            try
            {
                writer.WriteLine(json);
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                writer.Dispose();
                writer = null;
            }
        }
    }

    private string CommonFields()
    {
        lock (sync) return CommonFieldsUnsafe();
    }

    private string CommonFieldsUnsafe()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = sessionStartUtc == default(DateTime) ? 0d : (now - sessionStartUtc).TotalSeconds;
        return "\"ts_utc\":" + Quote(now.ToString("O", Invariant)) +
               ",\"session_s\":" + elapsed.ToString("F3", Invariant);
    }

    private static string Quote(string value)
    {
        if (value == null) return "null";
        var sb = new StringBuilder(value.Length + 8).Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32) sb.Append("\\u").Append(((int)c).ToString("X4", Invariant));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string Float(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) ? "null" : value.ToString("F5", Invariant);

    private static void AppendString(StringBuilder sb, string name, string value) =>
        sb.Append('"').Append(name).Append("\":").Append(Quote(value));
    private static void AppendBoolean(StringBuilder sb, string name, bool value) =>
        sb.Append('"').Append(name).Append("\":").Append(value ? "true" : "false");
    private static void AppendNumber(StringBuilder sb, string name, long value) =>
        sb.Append('"').Append(name).Append("\":").Append(value.ToString(Invariant));
    private static void AppendNumber(StringBuilder sb, string name, uint value) =>
        sb.Append('"').Append(name).Append("\":").Append(value.ToString(Invariant));
    private static void AppendFloat(StringBuilder sb, string name, float value) =>
        sb.Append('"').Append(name).Append("\":").Append(Float(value));
    private static void AppendDoubleOrNull(StringBuilder sb, string name, double value) =>
        sb.Append('"').Append(name).Append("\":")
            .Append(double.IsNaN(value) || double.IsInfinity(value) ? "null" : value.ToString("F1", Invariant));
}

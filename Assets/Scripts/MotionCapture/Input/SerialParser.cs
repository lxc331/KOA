using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Ports;
using UnityEngine;

public class SerialParser
{
    SerialPort serialPort;
    private const byte HEADER0 = 0xAA;
    private const byte HEADER1 = 0x44;
    private const int MIN_FRAME_LEN = 5;
    private const int MAX_REASONABLE_LEN = 64;
    private const int MAX_DEVICE_COUNT = 9;
    private const int MAX_READ_CHUNK = 1024;
    private const int MAX_GLOBAL_QUEUED_FRAMES = 256;
    private const int V2_PAYLOAD_LENGTH = 30;
    private const int V2_VERSION_PAYLOAD_OFFSET = 16;
    private const byte V2_PROTOCOL_VERSION = 2;

    Parity parity;
    int dataBits;
    StopBits stopBits;
    int readTimeout;
    int baudRate;

    // V77.25：异常过滤已迁移到 MotionDataHub。保留属性仅兼容旧调用，不在解析线程使用。
    public AnomalyDetector Detector { get; set; }

    int checksumFailCount = 0;
    int crc16FailCount = 0;
    int invalidPayloadLengthCount = 0;
    int extendedPayloadFrameCount = 0;
    int lastPayloadLength = 0;
    bool extendedPayloadNoticeLogged = false;
    int invalidQuaternionCount = 0;
    int invalidDeviceIdCount = 0;
    int globalQueueDroppedFrameCount = 0;
    int parityErrorCount = 0;
    int frameErrorCount = 0;
    int overrunErrorCount = 0;
    int duplicateLogicalIdConflictCount = 0;
    private readonly uint[] activeHardwareIds = new uint[MAX_DEVICE_COUNT];
    private readonly bool[] hasActiveHardwareId = new bool[MAX_DEVICE_COUNT];
    private readonly uint[] lastSourceSequence = new uint[MAX_DEVICE_COUNT];
    private readonly uint[] lastSenderTickMs = new uint[MAX_DEVICE_COUNT];
    private readonly bool[] hasLastSourceSequence = new bool[MAX_DEVICE_COUNT];
    private readonly long[] sourceLostFrameCount = new long[MAX_DEVICE_COUNT];
    private readonly long[] sourceDuplicateFrameCount = new long[MAX_DEVICE_COUNT];
    private readonly long[] sourceOutOfOrderFrameCount = new long[MAX_DEVICE_COUNT];
    private readonly long[] sourceRestartCount = new long[MAX_DEVICE_COUNT];
    private readonly long[] duplicateLogicalIdCount = new long[MAX_DEVICE_COUNT];

    public int ParityErrorCount { get { return parityErrorCount; } }
    public int FrameErrorCount { get { return frameErrorCount; } }
    public int OverrunErrorCount { get { return overrunErrorCount; } }
    public bool IsPortOpen { get { return serialPort != null && serialPort.IsOpen; } }
    public SerialPort Port { get { return serialPort; } }

    public struct SensorFrame { public Quaternion Q; public DateTime Timestamp; }

    /// <summary>协议层输出的原始传感器帧。业务过滤与同步由 MotionDataHub 统一处理。</summary>
    public struct RawSensorFrame
    {
        public int DeviceId;
        public Quaternion Q;
        public DateTime TimestampUtc;
        public long Sequence;
        public bool HasSourceMetadata;
        public byte ProtocolVersion;
        public uint HardwareId;
        public uint SourceSequence;
        public uint SenderTickMs;
    }
    private readonly Queue<SensorFrame>[] deviceQueues = new Queue<SensorFrame>[MAX_DEVICE_COUNT];
    private readonly object[] deviceQueueLocks = new object[MAX_DEVICE_COUNT];
    private int perDeviceQueueSize = 32;
    public int PerDeviceQueueSize { get => perDeviceQueueSize; set => perDeviceQueueSize = Mathf.Max(2, value); }
    private int[] droppedFrameCount = new int[MAX_DEVICE_COUNT];
    public int GetDroppedFrameCount(int deviceId) => (deviceId >= 0 && deviceId < MAX_DEVICE_COUNT) ? droppedFrameCount[deviceId] : 0;

    private struct QueuedFrame
    {
        public int DeviceId;
        public Quaternion Q;
        public DateTime TimestampUtc;
        public long Sequence;
        public bool HasSourceMetadata;
        public byte ProtocolVersion;
        public uint HardwareId;
        public uint SourceSequence;
        public uint SenderTickMs;
    }
    private readonly Queue<QueuedFrame> queuedFrames = new Queue<QueuedFrame>();
    private readonly object queuedFramesLock = new object();
    private readonly List<byte> parseBuffer = new List<byte>();
    private readonly object parseBufferLock = new object();
    private readonly long[] receivedSequence = new long[MAX_DEVICE_COUNT];

    public SerialParser()
    {
        for (int i = 0; i < MAX_DEVICE_COUNT; i++)
        {
            deviceQueues[i] = new Queue<SensorFrame>();
            deviceQueueLocks[i] = new object();
            droppedFrameCount[i] = 0;
            receivedSequence[i] = 0;
            activeHardwareIds[i] = 0;
            hasActiveHardwareId[i] = false;
            lastSourceSequence[i] = 0;
            lastSenderTickMs[i] = 0;
            hasLastSourceSequence[i] = false;
            sourceLostFrameCount[i] = 0;
            sourceDuplicateFrameCount[i] = 0;
            sourceOutOfOrderFrameCount[i] = 0;
            sourceRestartCount[i] = 0;
            duplicateLogicalIdCount[i] = 0;
        }
    }

    public void Reset()
    {
        lock (queuedFramesLock) { queuedFrames.Clear(); }
        for (int i = 0; i < MAX_DEVICE_COUNT; i++)
        {
            lock (deviceQueueLocks[i]) { deviceQueues[i].Clear(); }
            droppedFrameCount[i] = 0;
            receivedSequence[i] = 0;
            activeHardwareIds[i] = 0;
            hasActiveHardwareId[i] = false;
            lastSourceSequence[i] = 0;
            lastSenderTickMs[i] = 0;
            hasLastSourceSequence[i] = false;
            sourceLostFrameCount[i] = 0;
            sourceDuplicateFrameCount[i] = 0;
            sourceOutOfOrderFrameCount[i] = 0;
            sourceRestartCount[i] = 0;
            duplicateLogicalIdCount[i] = 0;
        }
        lock (parseBufferLock) { parseBuffer.Clear(); }
        checksumFailCount = 0;
        crc16FailCount = 0;
        invalidPayloadLengthCount = 0;
        extendedPayloadFrameCount = 0;
        lastPayloadLength = 0;
        extendedPayloadNoticeLogged = false;
        invalidQuaternionCount = 0;
        invalidDeviceIdCount = 0;
        globalQueueDroppedFrameCount = 0;
        parityErrorCount = 0;
        frameErrorCount = 0;
        overrunErrorCount = 0;
        duplicateLogicalIdConflictCount = 0;
        Detector?.Reset();
    }

    public bool TryGetLatestFrame(int deviceId, out SensorFrame frame)
    {
        frame = new SensorFrame();
        if (deviceId < 0 || deviceId >= MAX_DEVICE_COUNT) return false;
        lock (deviceQueueLocks[deviceId])
        {
            var queue = deviceQueues[deviceId];
            if (queue.Count > 0)
            {
                frame = queue.Peek();
                foreach (var f in queue) frame = f;
                return true;
            }
        }
        return false;
    }

    public bool TryGetInterpolatedFrame(int deviceId, DateTime targetTime, out SensorFrame frame)
    {
        frame = new SensorFrame();
        if (deviceId < 0 || deviceId >= MAX_DEVICE_COUNT) return false;
        lock (deviceQueueLocks[deviceId])
        {
            var queue = deviceQueues[deviceId];
            if (queue.Count == 0) return false;
            SensorFrame before = new SensorFrame(), after = new SensorFrame();
            bool foundBefore = false, foundAfter = false;
            foreach (var f in queue)
            {
                if (f.Timestamp <= targetTime) { before = f; foundBefore = true; }
                if (f.Timestamp > targetTime) { after = f; foundAfter = true; break; }
            }
            if (foundBefore && foundAfter)
            {
                double t = (targetTime - before.Timestamp).TotalSeconds / (after.Timestamp - before.Timestamp).TotalSeconds;
                // 兼容 .NET Framework：Math.Clamp 不可用，手动限幅
                t = System.Math.Max(0.0, System.Math.Min(1.0, t));
                Quaternion qInterp = Quaternion.Slerp(before.Q, after.Q, (float)t);
                frame = new SensorFrame { Q = qInterp, Timestamp = targetTime };
                return true;
            }
            if (foundBefore) { frame = before; return true; }
            if (foundAfter) { frame = after; return true; }
        }
        return false;
    }

    public bool TryDequeueFrame(out RawSensorFrame frame)
    {
        lock (queuedFramesLock)
        {
            if (queuedFrames.Count > 0)
            {
                QueuedFrame f = queuedFrames.Dequeue();
                frame = new RawSensorFrame
                {
                    DeviceId = f.DeviceId,
                    Q = f.Q,
                    TimestampUtc = f.TimestampUtc,
                    Sequence = f.Sequence,
                    HasSourceMetadata = f.HasSourceMetadata,
                    ProtocolVersion = f.ProtocolVersion,
                    HardwareId = f.HardwareId,
                    SourceSequence = f.SourceSequence,
                    SenderTickMs = f.SenderTickMs
                };
                return true;
            }
        }
        frame = default(RawSensorFrame);
        return false;
    }

    // 兼容旧代码。新主流程应使用 TryDequeueFrame，以保留时间戳和接收序号。
    public bool TryDequeue(out int deviceId, out Quaternion q)
    {
        RawSensorFrame frame;
        if (TryDequeueFrame(out frame))
        {
            deviceId = frame.DeviceId;
            q = frame.Q;
            return true;
        }
        deviceId = -1;
        q = Quaternion.identity;
        return false;
    }

    private bool EnqueueFrame(QueuedFrame frame)
    {
        // V51：限制全局实时队列长度，避免磁盘写入或编辑器卡顿时积压数千帧，
        // 造成角色“处理旧数据”式的明显卡顿/延迟。每个设备最新姿态仍由下方队列保留。
        lock (queuedFramesLock)
        {
            while (queuedFrames.Count >= MAX_GLOBAL_QUEUED_FRAMES)
            {
                queuedFrames.Dequeue();
                globalQueueDroppedFrameCount++;
            }
            queuedFrames.Enqueue(frame);
        }
        int deviceId = frame.DeviceId;
        if (deviceId >= 0 && deviceId < MAX_DEVICE_COUNT)
        {
            lock (deviceQueueLocks[deviceId])
            {
                var queue = deviceQueues[deviceId];
                if (queue.Count < perDeviceQueueSize)
                {
                    queue.Enqueue(new SensorFrame { Q = frame.Q, Timestamp = frame.TimestampUtc });
                    return true;
                }
                queue.Dequeue(); droppedFrameCount[deviceId]++;
                queue.Enqueue(new SensorFrame { Q = frame.Q, Timestamp = frame.TimestampUtc });
                return true;
            }
        }
        return false;
    }

    public int QueueCount { get { lock (queuedFramesLock) { return queuedFrames.Count; } } }
    public int GlobalQueueCapacity => MAX_GLOBAL_QUEUED_FRAMES;

    public bool OpenPort(string portName, int baudRate = 115200, Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One, int readTimeoutMs = 50)
    {
        this.baudRate = baudRate; this.parity = parity; this.dataBits = dataBits; this.stopBits = stopBits; this.readTimeout = readTimeoutMs;
        try
        {
            if (serialPort != null && serialPort.IsOpen) ClosePort();
            serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
            serialPort.ReadTimeout = readTimeout;
            serialPort.ErrorReceived += SerialPort_ErrorReceived;
            serialPort.Open();
            Debug.Log("SerialManager: Opened port " + portName + " @ " + baudRate);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("SerialManager: OpenPort failed: " + ex.Message);
            serialPort = null; return false;
        }
    }

    public void ClosePort()
    {
        try
        {
            if (serialPort != null)
            {
                if (serialPort.IsOpen) serialPort.Close();
                serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                serialPort.Dispose(); serialPort = null;
            }
            Debug.Log("SerialManager: Port closed");
        }
        catch (Exception ex)
        {
            Debug.LogError("SerialManager: ClosePort failed: " + ex.Message);
        }
    }

    public IEnumerator DataReceiveCoroutine()
    {
        while (true)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                try
                {
                    int n = serialPort.BytesToRead;
                    if (n > 0)
                    {
                        int toRead = Math.Min(Math.Max(n, 1), MAX_READ_CHUNK);
                        byte[] buf = new byte[toRead];
                        int read = serialPort.Read(buf, 0, toRead);
                        if (read > 0)
                        {
                            AppendBytes(buf, read);
                        }
                    }
                }
                catch (TimeoutException) { }
                catch (Exception ex)
                {
                    Debug.LogError("SerialManager: DataReceiveCoroutine exception: " + ex.Message);
                }
            }
            yield return null;
        }
    }

    public void AppendBytes(byte[] buf, int count)
    {
        if (buf == null || count <= 0) return;
        lock (parseBufferLock)
        {
            for (int i = 0; i < count; i++) parseBuffer.Add(buf[i]);
            ParseBuffer();
        }
    }

    private void ParseBuffer()
    {
        var bufferList = parseBuffer;
        while (bufferList.Count >= MIN_FRAME_LEN)
        {
            int headerIndex = -1;
            for (int i = 0; i <= bufferList.Count - 2; i++)
            {
                if (bufferList[i] == HEADER0 && bufferList[i + 1] == HEADER1)
                { headerIndex = i; break; }
            }
            if (headerIndex == -1)
            {
                int keep = Math.Min(1, bufferList.Count);
                bufferList.RemoveRange(0, bufferList.Count - keep);
                break;
            }
            if (headerIndex > 0)
            {
                bufferList.RemoveRange(0, headerIndex);
                continue;
            }
            int len = bufferList[3];
            if (len <= 0 || len > MAX_REASONABLE_LEN)
            {
                bool jumped = DropToNextHeaderOrKeepOne(bufferList);
                if (jumped) continue; else break;
            }
            if (len == V2_PAYLOAD_LENGTH &&
                bufferList.Count <= 4 + V2_VERSION_PAYLOAD_OFFSET)
                break;

            bool isV2 = len == V2_PAYLOAD_LENGTH &&
                        bufferList[4 + V2_VERSION_PAYLOAD_OFFSET] == V2_PROTOCOL_VERSION;
            int frameSize = len + (isV2 ? 6 : 5);
            if (bufferList.Count < frameSize) break;

            if (isV2)
            {
                ushort expectedCrc = (ushort)(bufferList[len + 4] |
                                              (bufferList[len + 5] << 8));
                ushort actualCrc = Crc16Ccitt(bufferList, len + 4);
                if (actualCrc != expectedCrc)
                {
                    crc16FailCount++;
                    bool jumped = DropToNextHeaderOrKeepOne(bufferList);
                    if (jumped) continue; else break;
                }
            }
            else
            {
                byte checksum = 0;
                for (int i = 0; i < len + 4; i++) checksum ^= bufferList[i];
                if (checksum != bufferList[len + 4])
                {
                    checksumFailCount++;
                    bool jumped = DropToNextHeaderOrKeepOne(bufferList);
                    if (jumped) continue; else break;
                }
            }

            // V77.28 协议兼容：旧版硬件实际可能在4个float四元数后追加状态字节。
            // 因此只要求负载“至少16字节”，四元数固定读取前16字节；校验和仍覆盖完整负载。
            // V77.25 的 len == 16 强校验会把这类有效扩展帧全部丢弃，表现为串口已连接但UI始终等待数据。
            lastPayloadLength = len;
            if (len < 16)
            {
                invalidPayloadLengthCount++;
                bufferList.RemoveRange(0, frameSize);
                continue;
            }
            if (len > 16)
            {
                extendedPayloadFrameCount++;
                if (!extendedPayloadNoticeLogged)
                {
                    extendedPayloadNoticeLogged = true;
                    Debug.LogWarning($"[V77.28协议兼容] 检测到负载长度={len}字节；已按前16字节解析四元数，其余字节保留在校验范围内并忽略。");
                }
            }
            int deviceId = bufferList[2] - 1;
            if (deviceId < 0 || deviceId >= MAX_DEVICE_COUNT)
            {
                invalidDeviceIdCount++;
                bufferList.RemoveRange(0, frameSize);
                continue;
            }
            byte[] payload = new byte[len];
            bufferList.CopyTo(4, payload, 0, len);
            Quaternion q;
            bool ok = QuaterFromBytes(payload, out q, true, true);
            if (!ok || !IsFiniteQuaternion(q))
            {
                invalidQuaternionCount++;
                bufferList.RemoveRange(0, frameSize);
                continue;
            }

            bool hasSourceMetadata = false;
            byte protocolVersion = 1;
            uint hardwareId = 0;
            uint sourceSequence = 0;
            uint senderTickMs = 0;
            if (isV2)
            {
                protocolVersion = payload[16];
                sourceSequence = ReadUInt32LittleEndian(payload, 18);
                senderTickMs = ReadUInt32LittleEndian(payload, 22);
                hardwareId = ReadUInt32LittleEndian(payload, 26);
                hasSourceMetadata = (payload[17] & 0x01) != 0 && hardwareId != 0;
                if (!hasSourceMetadata ||
                    !AcceptV2SourceFrame(deviceId, hardwareId, sourceSequence, senderTickMs))
                {
                    bufferList.RemoveRange(0, frameSize);
                    continue;
                }
            }

            long sequence = ++receivedSequence[deviceId];
            EnqueueFrame(new QueuedFrame
            {
                DeviceId = deviceId,
                Q = q,
                TimestampUtc = DateTime.UtcNow,
                Sequence = sequence,
                HasSourceMetadata = hasSourceMetadata,
                ProtocolVersion = protocolVersion,
                HardwareId = hardwareId,
                SourceSequence = sourceSequence,
                SenderTickMs = senderTickMs
            });
            bufferList.RemoveRange(0, frameSize);
        }
    }

    private bool AcceptV2SourceFrame(
        int deviceId,
        uint hardwareId,
        uint sourceSequence,
        uint senderTickMs)
    {
        if (!hasActiveHardwareId[deviceId])
        {
            hasActiveHardwareId[deviceId] = true;
            activeHardwareIds[deviceId] = hardwareId;
        }
        else if (activeHardwareIds[deviceId] != hardwareId)
        {
            duplicateLogicalIdConflictCount++;
            duplicateLogicalIdCount[deviceId]++;
            return false;
        }

        if (!hasLastSourceSequence[deviceId])
        {
            hasLastSourceSequence[deviceId] = true;
            lastSourceSequence[deviceId] = sourceSequence;
            lastSenderTickMs[deviceId] = senderTickMs;
            return true;
        }

        bool senderRestarted = senderTickMs < lastSenderTickMs[deviceId] &&
                               lastSenderTickMs[deviceId] - senderTickMs > 1000u;
        if (senderRestarted)
        {
            sourceRestartCount[deviceId]++;
            lastSourceSequence[deviceId] = sourceSequence;
            lastSenderTickMs[deviceId] = senderTickMs;
            return true;
        }

        uint delta = unchecked(sourceSequence - lastSourceSequence[deviceId]);
        if (delta == 0u)
        {
            sourceDuplicateFrameCount[deviceId]++;
            return false;
        }
        if (delta >= 0x80000000u)
        {
            sourceOutOfOrderFrameCount[deviceId]++;
            return false;
        }
        if (delta > 1u)
            sourceLostFrameCount[deviceId] += delta - 1u;

        lastSourceSequence[deviceId] = sourceSequence;
        lastSenderTickMs[deviceId] = senderTickMs;
        return true;
    }

    private static uint ReadUInt32LittleEndian(byte[] bytes, int offset)
    {
        return (uint)(bytes[offset] |
                      (bytes[offset + 1] << 8) |
                      (bytes[offset + 2] << 16) |
                      (bytes[offset + 3] << 24));
    }

    private static ushort Crc16Ccitt(List<byte> bytes, int count)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < count; i++)
        {
            crc ^= (ushort)(bytes[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0
                    ? (crc << 1) ^ 0x1021
                    : crc << 1);
        }
        return crc;
    }

    private static bool QuaterFromBytes(byte[] bytes, out Quaternion result, bool normalize = false, bool sensorLittleEndian = true)
    {
        result = Quaternion.identity;
        if (bytes == null || bytes.Length < 16) return false;
        try
        {
            float q0 = ReadSingleFromBytes(bytes, 0, sensorLittleEndian);
            float q1 = ReadSingleFromBytes(bytes, 4, sensorLittleEndian);
            float q2 = ReadSingleFromBytes(bytes, 8, sensorLittleEndian);
            float q3 = ReadSingleFromBytes(bytes, 12, sensorLittleEndian);
            if (normalize)
            {
                float magnitude = (float)Math.Sqrt(q0 * q0 + q1 * q1 + q2 * q2 + q3 * q3);
                const float epsilon = 1e-6f;
                if (magnitude > epsilon)
                { q0 /= magnitude; q1 /= magnitude; q2 /= magnitude; q3 /= magnitude; }
            }
            result = new Quaternion(q1, q2, q3, q0);
            return true;
        }
        catch { return false; }
    }

    private static bool IsFiniteQuaternion(Quaternion q)
    {
        if (float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)) return false;
        if (float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z) || float.IsInfinity(q.w)) return false;
        float sqr = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        return sqr > 0.0000001f && !float.IsNaN(sqr) && !float.IsInfinity(sqr);
    }

    private static float ReadSingleFromBytes(byte[] bytes, int offset, bool sensorLittleEndian)
    {
        if (sensorLittleEndian == BitConverter.IsLittleEndian) return BitConverter.ToSingle(bytes, offset);
        byte[] tmp = new byte[4]; Array.Copy(bytes, offset, tmp, 0, 4); Array.Reverse(tmp); return BitConverter.ToSingle(tmp, 0);
    }

    private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        NotifySerialError(e.EventType);
    }

    public void NotifySerialError(SerialError error)
    {
        switch (error)
        {
            case SerialError.RXParity: parityErrorCount++; break;
            case SerialError.Frame: frameErrorCount++; break;
            case SerialError.Overrun: overrunErrorCount++; break;
            default: break;
        }
    }

    private bool DropToNextHeaderOrKeepOne(List<byte> bufferList)
    {
        int nextHeader = -1;
        for (int j = 1; j <= bufferList.Count - 2; j++)
        {
            if (bufferList[j] == HEADER0 && bufferList[j + 1] == HEADER1)
            { nextHeader = j; break; }
        }
        if (nextHeader > 0)
        { bufferList.RemoveRange(0, nextHeader); return true; }
        else
        { int keep = Math.Min(1, bufferList.Count); bufferList.RemoveRange(0, bufferList.Count - keep); return false; }
    }

    public int ChecksumFailCount => checksumFailCount;
    public int Crc16FailCount => crc16FailCount;
    public int InvalidPayloadLengthCount => invalidPayloadLengthCount;
    public int ExtendedPayloadFrameCount => extendedPayloadFrameCount;
    public int LastPayloadLength => lastPayloadLength;
    public int InvalidQuaternionCount => invalidQuaternionCount;
    public int InvalidDeviceIdCount => invalidDeviceIdCount;
    public int GlobalQueueDroppedFrameCount => globalQueueDroppedFrameCount;
    public int DuplicateLogicalIdConflictCount => duplicateLogicalIdConflictCount;
    public bool HasV2Source(int deviceId) =>
        deviceId >= 0 && deviceId < MAX_DEVICE_COUNT && hasActiveHardwareId[deviceId];
    public uint GetHardwareId(int deviceId) =>
        deviceId >= 0 && deviceId < MAX_DEVICE_COUNT ? activeHardwareIds[deviceId] : 0u;
    public uint GetLastSourceSequence(int deviceId) =>
        deviceId >= 0 && deviceId < MAX_DEVICE_COUNT ? lastSourceSequence[deviceId] : 0u;
    public uint GetLastSenderTickMs(int deviceId) =>
        deviceId >= 0 && deviceId < MAX_DEVICE_COUNT ? lastSenderTickMs[deviceId] : 0u;
    public long GetSourceLostFrameCount(int deviceId) =>
        deviceId >= 0 && deviceId < MAX_DEVICE_COUNT ? sourceLostFrameCount[deviceId] : 0L;
    public long GetSourceDuplicateFrameCount(int deviceId) =>
        deviceId >= 0 && deviceId < MAX_DEVICE_COUNT ? sourceDuplicateFrameCount[deviceId] : 0L;
    public long GetSourceOutOfOrderFrameCount(int deviceId) =>
        deviceId >= 0 && deviceId < MAX_DEVICE_COUNT ? sourceOutOfOrderFrameCount[deviceId] : 0L;
    public long GetSourceRestartCount(int deviceId) =>
        deviceId >= 0 && deviceId < MAX_DEVICE_COUNT ? sourceRestartCount[deviceId] : 0L;
    public long GetDuplicateLogicalIdCount(int deviceId) =>
        deviceId >= 0 && deviceId < MAX_DEVICE_COUNT ? duplicateLogicalIdCount[deviceId] : 0L;
}

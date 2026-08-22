using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 串口协议回归测试。既可从 Unity 菜单执行，也可用 -executeMethod 在命令行执行。
/// 不依赖真实串口，专门验证粘包/拆包后的协议边界、校验与九设备身份约束。
/// </summary>
public static class SerialProtocolV2SelfTest
{
    [MenuItem("Tools/Motion Capture/Run Serial Protocol V2 Self Test")]
    public static void RunFromMenu()
    {
        RunAll();
        Debug.Log("[SerialProtocolV2SelfTest] PASS：全部协议回归测试通过。");
    }

    public static void RunFromCommandLine()
    {
        try
        {
            RunAll();
            Debug.Log("[SerialProtocolV2SelfTest] PASS：全部协议回归测试通过。");
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogError("[SerialProtocolV2SelfTest] FAIL：" + ex);
            EditorApplication.Exit(1);
        }
    }

    private static void RunAll()
    {
        LegacyFrameStillParses();
        V2FrameParsesAfterFragmentedInput();
        ReliableSourceClockFlagsAreExposed();
        LinkControlFramesHaveStableLayoutAndCrc();
        CorruptedV2FrameIsRejected();
        DuplicateLogicalIdIsRejected();
        SourceSequenceDetectsLossDuplicateAndOutOfOrder();
        ResetClearsOldFramesAndIdentityState();
        AdaptiveCalibrationTimeoutUsesEachDeviceCadence();
        PairedLegCalibrationChannelsAccumulateIndependently();
        TimePairedLegCompositionPreservesCurrentThighAndPairedKnee();
        AiDiagnosticLogIsIncrementalAndComplete();
    }

    private static void AiDiagnosticLogIsIncrementalAndComplete()
    {
        var logger = new AiDiagnosticLogger();
        string path = string.Empty;
        try
        {
            Require(logger.Open(
                Path.GetTempPath(),
                "SELF-TEST",
                "KOA",
                Application.unityVersion,
                "COM-TEST",
                115200,
                9), "AI诊断日志无法创建：" + logger.LastError);
            path = logger.CurrentPath;
            logger.LogEvent("self_test", "WAITING_DATA", "incremental write");
            logger.LogSnapshot(
                "WAITING_DATA",
                "self test",
                string.Empty,
                new AiDiagnosticLogger.ParserSnapshot { Port = "COM-TEST", Baud = 115200 },
                new[] { new AiDiagnosticLogger.SensorSnapshot { Id = 1, Role = "测试", Q = Quaternion.identity } });

            // AutoFlush=true：Close之前也必须已经能从另一个读句柄看到增量内容。
            string liveText = ReadAllTextShared(path);
            Require(liveText.Contains("\"kind\":\"session_start\"") &&
                    liveText.Contains("\"kind\":\"event\"") &&
                    liveText.Contains("\"kind\":\"snapshot\""),
                "AI诊断日志未增量落盘");

            logger.Close("self_test_complete");
            string finalText = ReadAllTextShared(path);
            Require(finalText.Contains("\"kind\":\"session_end\""), "AI诊断日志缺少结束记录");
        }
        finally
        {
            logger.Dispose();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
    }

    private static string ReadAllTextShared(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
            return reader.ReadToEnd();
    }

    private static void LegacyFrameStillParses()
    {
        SerialParser parser = new SerialParser();
        byte[] frameBytes = BuildLegacyFrame(1, Quaternion.identity);
        byte[] withNoise = new byte[frameBytes.Length + 3];
        withNoise[0] = 0x10;
        withNoise[1] = 0x20;
        withNoise[2] = 0x30;
        Buffer.BlockCopy(frameBytes, 0, withNoise, 3, frameBytes.Length);
        parser.AppendBytes(withNoise, withNoise.Length);

        SerialParser.RawSensorFrame frame;
        Require(parser.TryDequeueFrame(out frame), "旧版帧未能在前导噪声后恢复解析");
        Require(frame.DeviceId == 0, "旧版帧设备 ID 映射错误");
        Require(!frame.HasSourceMetadata && frame.ProtocolVersion == 1, "旧版帧被错误标记为 V2");
    }

    private static void V2FrameParsesAfterFragmentedInput()
    {
        SerialParser parser = new SerialParser();
        byte[] bytes = BuildV2Frame(3, 0x1234ABCDu, 42u, 5000u, Quaternion.identity);
        parser.AppendBytes(bytes, 9);
        SerialParser.RawSensorFrame frame;
        Require(!parser.TryDequeueFrame(out frame), "不完整 V2 帧被提前出队");

        byte[] tail = new byte[bytes.Length - 9];
        Buffer.BlockCopy(bytes, 9, tail, 0, tail.Length);
        parser.AppendBytes(tail, tail.Length);

        Require(parser.TryDequeueFrame(out frame), "拆分输入后的 V2 帧未解析");
        Require(frame.DeviceId == 2, "V2 设备 ID 映射错误");
        Require(frame.HasSourceMetadata && frame.ProtocolVersion == 2, "V2 元数据标记错误");
        Require(frame.HardwareId == 0x1234ABCDu, "V2 硬件唯一 ID 错误");
        Require(frame.SourceSequence == 42u && frame.SenderTickMs == 5000u, "V2 序号或发送时钟错误");
    }

    private static void CorruptedV2FrameIsRejected()
    {
        SerialParser parser = new SerialParser();
        byte[] bytes = BuildV2Frame(1, 0x11111111u, 1u, 100u, Quaternion.identity);
        bytes[8] ^= 0x01;
        parser.AppendBytes(bytes, bytes.Length);

        SerialParser.RawSensorFrame frame;
        Require(!parser.TryDequeueFrame(out frame), "CRC 已损坏的 V2 帧仍被接收");
        Require(parser.Crc16FailCount == 1, "CRC 失败计数未增加");
    }

    private static void ReliableSourceClockFlagsAreExposed()
    {
        SerialParser parser = new SerialParser();
        Append(parser, BuildV2Frame(
            2,
            0x22222222u,
            1u,
            100u,
            Quaternion.identity,
            0x1F));

        SerialParser.RawSensorFrame frame;
        Require(parser.TryDequeueFrame(out frame), "可靠源时钟测试帧未解析");
        Require(frame.SourceClockReliable && frame.SourceFlags == 0x1F,
            "可靠源时钟标志未传递到业务帧");
        Require(parser.IsSourceClockReliable(1) && parser.IsSourceMainClockHealthy(1),
            "解析器未暴露硬件时钟/主时钟健康标志");
        Require(parser.IsSourceSlottedTransmit(1) && parser.IsSourceLinkSynchronized(1),
            "解析器未暴露错峰发送/链路同步标志");
    }

    private static void LinkControlFramesHaveStableLayoutAndCrc()
    {
        const uint token = 0x78563412u;
        byte[] configure = LinkControlProtocol.BuildConfigureAndSync(8, token);
        Require(configure.Length == 12, "错峰配置帧长度错误");
        Require(configure[0] == 0xA5 && configure[1] == 0x5A &&
                configure[2] == 1 && configure[3] == 0x01 && configure[4] == 5,
            "错峰配置帧头、版本、命令或载荷长度错误");
        Require(configure[5] == 8 && configure[6] == 0x12 && configure[7] == 0x34 &&
                configure[8] == 0x56 && configure[9] == 0x78,
            "错峰配置的频率或同步Token编码错误");
        RequireFrameCrc(configure, "错峰配置帧CRC错误");

        byte[] pause = LinkControlProtocol.BuildPause();
        Require(pause.Length == 7 && pause[3] == 0x02 && pause[4] == 0,
            "暂停命令布局错误");
        RequireFrameCrc(pause, "暂停命令CRC错误");
    }

    private static void RequireFrameCrc(byte[] frame, string message)
    {
        ushort expected = Crc16Ccitt(frame, frame.Length - 2);
        ushort actual = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
        Require(expected == actual &&
                expected == LinkControlProtocol.ComputeCrc16Ccitt(frame, frame.Length - 2), message);
    }

    private static void DuplicateLogicalIdIsRejected()
    {
        SerialParser parser = new SerialParser();
        Append(parser, BuildV2Frame(4, 0xAAAA0001u, 1u, 100u, Quaternion.identity));
        Append(parser, BuildV2Frame(4, 0xBBBB0002u, 2u, 200u, Quaternion.identity));

        SerialParser.RawSensorFrame frame;
        Require(parser.TryDequeueFrame(out frame), "首个硬件身份帧未接收");
        Require(!parser.TryDequeueFrame(out frame), "相同逻辑 ID 的第二个硬件身份未被拒绝");
        Require(parser.DuplicateLogicalIdConflictCount == 1, "重复逻辑 ID 冲突未计数");
        Require(parser.GetHardwareId(3) == 0xAAAA0001u, "重复 ID 覆盖了已锁定的硬件身份");
    }

    private static void SourceSequenceDetectsLossDuplicateAndOutOfOrder()
    {
        SerialParser parser = new SerialParser();
        const uint hardwareId = 0xCAFEBABEu;
        Append(parser, BuildV2Frame(7, hardwareId, 10u, 1000u, Quaternion.identity));
        Append(parser, BuildV2Frame(7, hardwareId, 12u, 1200u, Quaternion.identity));
        Append(parser, BuildV2Frame(7, hardwareId, 12u, 1210u, Quaternion.identity));
        Append(parser, BuildV2Frame(7, hardwareId, 11u, 1220u, Quaternion.identity));

        Require(parser.GetSourceLostFrameCount(6) == 1, "源端丢帧计数错误");
        Require(parser.GetSourceDuplicateFrameCount(6) == 1, "源端重复帧计数错误");
        Require(parser.GetSourceOutOfOrderFrameCount(6) == 1, "源端乱序帧计数错误");
        Require(parser.QueueCount == 2, "重复或乱序帧进入了业务队列");
        Require(Mathf.Abs(parser.GetSourceReportedFrameRateHz(6) - 10f) < 0.01f,
            "发送端序号/时钟没有正确估算控制板实际发送Hz");
        Require(Mathf.Abs(parser.GetSourceDeliveryPercent(6) - (200f / 3f)) < 0.1f,
            "接收帧/源端缺口没有正确计算链路到达率");
    }

    private static void ResetClearsOldFramesAndIdentityState()
    {
        SerialParser parser = new SerialParser();
        Append(parser, BuildV2Frame(9, 0x90000001u, 8u, 800u, Quaternion.identity));
        parser.Reset();

        Require(parser.QueueCount == 0, "Reset 后仍残留旧业务帧");
        Require(!parser.HasV2Source(8), "Reset 后仍残留旧硬件身份");
        Require(parser.GetLastSourceSequence(8) == 0u, "Reset 后仍残留旧源端序号");
        Require(parser.GetSourceReportedFrameRateHz(8) == 0f && parser.GetSourceDeliveryPercent(8) == 0f,
            "Reset 后仍残留源端Hz或链路到达率");
        Require(parser.Crc16FailCount == 0 && parser.DuplicateLogicalIdConflictCount == 0,
            "Reset 后仍残留旧错误计数");

        Append(parser, BuildV2Frame(9, 0x90000002u, 1u, 10u, Quaternion.identity));
        Require(parser.DuplicateLogicalIdConflictCount == 0,
            "Reset 后新的合法硬件身份被错误判断为冲突");
    }

    private static void AdaptiveCalibrationTimeoutUsesEachDeviceCadence()
    {
        Require(Mathf.Approximately(
                MotionDataHub.CalculateAdaptiveOfflineTimeoutSeconds(0.5f, 0f), 4f),
            "尚未估算Hz时没有给予4秒启动宽限");
        Require(Mathf.Abs(
                MotionDataHub.CalculateAdaptiveOfflineTimeoutSeconds(0.5f, 1.2f) - (4f / 1.2f)) < 0.001f,
            "1.2Hz设备没有采用约四个采样周期的新鲜度门限");
        Require(Mathf.Approximately(
                MotionDataHub.CalculateAdaptiveOfflineTimeoutSeconds(0.5f, 20f), 0.5f),
            "高频设备的新鲜度门限没有收紧到最小值");
        Require(Mathf.Approximately(
                MotionDataHub.CalculateAdaptiveOfflineTimeoutSeconds(0.5f, 0.5f), 4f),
            "极低频设备的新鲜度门限超过或低于4秒上限");
    }

    private static void PairedLegCalibrationChannelsAccumulateIndependently()
    {
        LeftLegPoseDriver left = new LeftLegPoseDriver
        {
            DriveCalf = true,
            CalibrationSampleFramesRequired = 5
        };
        RightLegPoseDriver right = new RightLegPoseDriver
        {
            DriveCalf = true,
            CalibrationSampleFramesRequired = 5
        };
        string reason;

        for (int i = 0; i < 5; i++)
        {
            Require(left.TryAccumulateCalibrationSampleForSensor(
                    LeftLegPoseDriver.LeftThighIndex, Quaternion.identity, out reason), reason);
            Require(right.TryAccumulateCalibrationSampleForSensor(
                    RightLegPoseDriver.RightThighIndex, Quaternion.identity, out reason), reason);
        }

        Require(left.ThighCalibrationSampleCount == 5 && left.CalfCalibrationSampleCount == 0,
            "左大腿新帧错误地重复累计了左小腿旧值");
        Require(right.ThighCalibrationSampleCount == 5 && right.CalfCalibrationSampleCount == 0,
            "右大腿新帧错误地重复累计了右小腿旧值");

        for (int i = 0; i < 3; i++)
        {
            Require(left.TryAccumulateCalibrationSampleForSensor(
                    LeftLegPoseDriver.LeftCalfIndex, Quaternion.identity, out reason), reason);
            Require(right.TryAccumulateCalibrationSampleForSensor(
                    RightLegPoseDriver.RightCalfIndex, Quaternion.identity, out reason), reason);
        }

        Require(left.ThighCalibrationSampleCount == 5 && left.CalfCalibrationSampleCount == 3,
            "左腿两路标定样本没有独立累计");
        Require(right.ThighCalibrationSampleCount == 5 && right.CalfCalibrationSampleCount == 3,
            "右腿两路标定样本没有独立累计");

        left.ClearCalibrationSamplesForSensor(LeftLegPoseDriver.LeftCalfIndex);
        right.ClearCalibrationSamplesForSensor(RightLegPoseDriver.RightCalfIndex);
        Require(left.ThighCalibrationSampleCount == 5 && left.CalfCalibrationSampleCount == 0,
            "清除左小腿样本时误清除了左大腿样本");
        Require(right.ThighCalibrationSampleCount == 5 && right.CalfCalibrationSampleCount == 0,
            "清除右小腿样本时误清除了右大腿样本");
    }

    private static void TimePairedLegCompositionPreservesCurrentThighAndPairedKnee()
    {
        Quaternion pairedThigh = Quaternion.Euler(12f, 28f, -7f);
        Quaternion pairedCalf = Quaternion.Euler(18f, 30f, 36f);
        Quaternion currentThigh = Quaternion.Euler(-24f, 75f, 11f);
        Quaternion syntheticCalf = MotionCaptureController.ComposeTimePairedCalfForCurrentThigh(
            currentThigh, pairedThigh, pairedCalf);

        Quaternion expectedRelative =
            (Quaternion.Inverse(pairedThigh) * pairedCalf).normalized;
        Quaternion actualRelative =
            (Quaternion.Inverse(currentThigh) * syntheticCalf).normalized;
        Require(Quaternion.Angle(expectedRelative, actualRelative) < 0.01f,
            "时间配对小腿合成没有保留同一时刻的膝关节相对旋转");
    }

    private static void Append(SerialParser parser, byte[] bytes)
    {
        parser.AppendBytes(bytes, bytes.Length);
    }

    private static byte[] BuildLegacyFrame(int logicalDeviceId, Quaternion q)
    {
        const int payloadLength = 16;
        byte[] frame = new byte[payloadLength + 5];
        frame[0] = 0xAA;
        frame[1] = 0x44;
        frame[2] = (byte)logicalDeviceId;
        frame[3] = payloadLength;
        WriteQuaternionWxyz(frame, 4, q);
        byte checksum = 0;
        for (int i = 0; i < payloadLength + 4; i++) checksum ^= frame[i];
        frame[payloadLength + 4] = checksum;
        return frame;
    }

    private static byte[] BuildV2Frame(
        int logicalDeviceId,
        uint hardwareId,
        uint sourceSequence,
        uint senderTickMs,
        Quaternion q,
        byte sourceFlags = 0x01)
    {
        const int payloadLength = 30;
        byte[] frame = new byte[payloadLength + 6];
        frame[0] = 0xAA;
        frame[1] = 0x44;
        frame[2] = (byte)logicalDeviceId;
        frame[3] = payloadLength;
        WriteQuaternionWxyz(frame, 4, q);
        frame[20] = 2;
        frame[21] = sourceFlags;
        WriteUInt32LittleEndian(frame, 22, sourceSequence);
        WriteUInt32LittleEndian(frame, 26, senderTickMs);
        WriteUInt32LittleEndian(frame, 30, hardwareId);
        ushort crc = Crc16Ccitt(frame, payloadLength + 4);
        frame[34] = (byte)(crc & 0xFF);
        frame[35] = (byte)(crc >> 8);
        return frame;
    }

    private static void WriteQuaternionWxyz(byte[] bytes, int offset, Quaternion q)
    {
        WriteSingleLittleEndian(bytes, offset, q.w);
        WriteSingleLittleEndian(bytes, offset + 4, q.x);
        WriteSingleLittleEndian(bytes, offset + 8, q.y);
        WriteSingleLittleEndian(bytes, offset + 12, q.z);
    }

    private static void WriteSingleLittleEndian(byte[] bytes, int offset, float value)
    {
        byte[] raw = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian) Array.Reverse(raw);
        Buffer.BlockCopy(raw, 0, bytes, offset, 4);
    }

    private static void WriteUInt32LittleEndian(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    private static ushort Crc16Ccitt(byte[] bytes, int count)
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

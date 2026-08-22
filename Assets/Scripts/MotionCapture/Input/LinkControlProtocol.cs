using System;

/// <summary>
/// Unity -> Zigbee nodes downlink. The coordinator transparently broadcasts
/// these short frames; nodes validate CRC before applying a command.
/// </summary>
public static class LinkControlProtocol
{
    public const byte ProtocolVersion = 1;
    public const byte ConfigureAndSyncCommand = 0x01;
    public const byte PauseCommand = 0x02;

    public static byte[] BuildConfigureAndSync(int transmitRateHz, uint syncToken)
    {
        if (transmitRateHz < 1 || transmitRateHz > 10)
            throw new ArgumentOutOfRangeException(nameof(transmitRateHz), "发送频率必须为 1~10 Hz");

        byte[] frame = new byte[12];
        frame[0] = 0xA5;
        frame[1] = 0x5A;
        frame[2] = ProtocolVersion;
        frame[3] = ConfigureAndSyncCommand;
        frame[4] = 5;
        frame[5] = (byte)transmitRateHz;
        WriteUInt32LittleEndian(frame, 6, syncToken);
        WriteCrc(frame);
        return frame;
    }

    public static byte[] BuildPause()
    {
        byte[] frame = new byte[7];
        frame[0] = 0xA5;
        frame[1] = 0x5A;
        frame[2] = ProtocolVersion;
        frame[3] = PauseCommand;
        frame[4] = 0;
        WriteCrc(frame);
        return frame;
    }

    public static ushort ComputeCrc16Ccitt(byte[] data, int length)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (length < 0 || length > data.Length) throw new ArgumentOutOfRangeException(nameof(length));

        ushort crc = 0xFFFF;
        for (int i = 0; i < length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    private static void WriteCrc(byte[] frame)
    {
        ushort crc = ComputeCrc16Ccitt(frame, frame.Length - 2);
        frame[frame.Length - 2] = (byte)(crc & 0xFF);
        frame[frame.Length - 1] = (byte)(crc >> 8);
    }

    private static void WriteUInt32LittleEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value & 0xFF);
        target[offset + 1] = (byte)((value >> 8) & 0xFF);
        target[offset + 2] = (byte)((value >> 16) & 0xFF);
        target[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}

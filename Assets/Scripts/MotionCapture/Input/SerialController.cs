using System.IO.Ports;
using System.Threading;
using UnityEngine;

public class SerialController
{
    public SerialParser Parser { get; }
    private Thread readThread;
    private volatile bool readLoopRunning;
    private SerialPort serialPort;
    private readonly object portLock = new object();

    private volatile bool isConnected;
    public bool IsConnected
    {
        get => isConnected;
        private set => isConnected = value;
    }
    public string CurrentPort { get; private set; }
    public int CurrentBaud { get; private set; }

    public SerialController(SerialParser parser)
    {
        this.Parser = parser;
    }

    public void RefreshPorts(out string[] ports)
    {
        ports = SerialPort.GetPortNames();
    }

    public bool Connect(string portName, int baud)
    {
        if (IsConnected) return true;
        if (readThread != null)
            Disconnect();
        CurrentPort = portName;
        CurrentBaud = baud;
        if (!OpenPort(portName, baud))
        {
            Debug.LogError($"SerialController: failed to open port {portName} @{baud}");
            IsConnected = false;
            return false;
        }
        readLoopRunning = true;
        IsConnected = true;
        readThread = new Thread(ReadLoop) { IsBackground = true, Name = "SerialPortReadLoop" };
        readThread.Start();
        return true;
    }

    public void Disconnect()
    {
        // 即使物理断线已把 IsConnected 置为 false，也必须继续回收线程和串口对象。
        readLoopRunning = false;
        try
        {
            if (readThread != null && Thread.CurrentThread != readThread)
            {
                if (!readThread.Join(200)) readThread.Interrupt();
                readThread = null;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError("SerialController: stop thread failed: " + ex.Message);
        }
        ClosePort();
        IsConnected = false;
    }

    /// <summary>
    /// Thread-safe binary downlink used for one-shot Zigbee schedule control.
    /// Reading stays on the background thread; writing is short and synchronous.
    /// </summary>
    public bool TryWrite(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return false;

        lock (portLock)
        {
            try
            {
                if (serialPort == null || !serialPort.IsOpen)
                    return false;
                serialPort.Write(bytes, 0, bytes.Length);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError("SerialController: Write failed: " + ex.Message);
                return false;
            }
        }
    }

    private bool OpenPort(string portName, int baudRate = 115200, Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One, int readTimeoutMs = 50)
    {
        try
        {
            ClosePort();
            serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
            serialPort.ReadTimeout = readTimeoutMs;
            serialPort.ErrorReceived += OnSerialErrorReceived;
            serialPort.Open();
            Debug.Log($"SerialController: Opened {portName} @{baudRate}");
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError("SerialController: OpenPort failed: " + ex.Message);
            serialPort = null;
            return false;
        }
    }

    private void ClosePort()
    {
        lock (portLock)
        {
            try
            {
                if (serialPort != null)
                {
                    serialPort.ErrorReceived -= OnSerialErrorReceived;
                    if (serialPort.IsOpen) serialPort.Close();
                    serialPort.Dispose();
                    serialPort = null;
                }
                Debug.Log("SerialController: Port closed");
            }
            catch (System.Exception ex)
            {
                serialPort = null;
                Debug.LogError("SerialController: ClosePort failed: " + ex.Message);
            }
        }
    }

    private void OnSerialErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        Parser?.NotifySerialError(e.EventType);
    }

    private void ReadLoop()
    {
        const int MAX_READ_CHUNK = 1024;
        try
        {
            while (readLoopRunning)
            {
                if (serialPort == null || !serialPort.IsOpen || Parser == null)
                {
                    readLoopRunning = false;
                    break;
                }

                try
                {
                    int n = serialPort.BytesToRead;
                    if (n > 0)
                    {
                        int toRead = System.Math.Min(System.Math.Max(n, 1), MAX_READ_CHUNK);
                        byte[] buf = new byte[toRead];
                        int read = serialPort.Read(buf, 0, toRead);
                        if (read > 0)
                            Parser.AppendBytes(buf, read);
                    }
                }
                catch (System.TimeoutException) { }
                catch (System.Threading.ThreadInterruptedException) { break; }
                catch (System.Exception ex)
                {
                    Debug.LogError("SerialController: ReadLoop exception: " + ex.Message);
                    readLoopRunning = false;
                    break;
                }
                Thread.Sleep(1);
            }
        }
        finally
        {
            IsConnected = false;
            ClosePort();
        }
    }
} 

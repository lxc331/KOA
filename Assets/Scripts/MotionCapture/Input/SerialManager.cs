
using System;
using System.IO.Ports;
using UnityEngine;

/// <summary>
/// 串口生命周期管理器。
/// 
/// 职责：
///   - 封装 SerialController（后台读取线程）和 SerialParser（协议解析）的创建与销毁
///   - 管理系统串口列表的枚举与选择
///   - 提供连接/断开的高层 API
///   - 不涉及数据的解读和业务处理，仅负责"通道"的建立与销毁
/// 
/// 外部依赖（需在项目中已存在）：
///   - SerialController: 后台线程读取串口原始字节流
///   - SerialParser:     将字节流解析为 (deviceId, Quaternion) 帧，存入线程安全队列
///   - AnomalyDetector:  可选的异常帧过滤器
/// 
/// 实现 IDisposable 以保证在任何退出路径下正确关闭串口。
/// </summary>
public class SerialManager : IDisposable
{
    // ═══════════════════════════════════════════════════════════════
    //  公开属性
    // ═══════════════════════════════════════════════════════════════

    /// <summary>协议解析器实例，外部通过它读取解析后的帧队列</summary>
    public SerialParser Parser { get; private set; }

    /// <summary>当前是否已成功连接串口。直接读取底层控制器，物理断线后会立即同步为 false。</summary>
    public bool IsConnected => controller != null && controller.IsConnected;

    /// <summary>系统中检测到的所有可用串口名称</summary>
    public string[] AvailablePorts { get; private set; } = Array.Empty<string>();

    /// <summary>当前选中的串口在 AvailablePorts 中的索引（-1 表示无可用端口）</summary>
    public int SelectedPortIndex { get; private set; } = -1;

    /// <summary>当前选中或手动输入的串口名称（如 "COM9"）</summary>
    public string CurrentPort { get; private set; } = "";

    // ═══════════════════════════════════════════════════════════════
    //  私有字段
    // ═══════════════════════════════════════════════════════════════

    /// <summary>底层串口控制器，负责后台线程的字节流读取</summary>
    private SerialController controller;

    // ═══════════════════════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 创建 SerialManager：同时构造 Parser 和 Controller。
    /// Controller 内部会启动后台读取线程（连接后激活）。
    /// </summary>
    public SerialManager()
    {
        Parser = new SerialParser();                     // 创建协议解析器
        controller = new SerialController(Parser);       // 创建串口控制器，绑定到解析器
    }

    // ═══════════════════════════════════════════════════════════════
    //  异常检测配置
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 配置异常帧检测器。应在 Connect() 之前调用。
    /// V77.25起异常检测由 MotionDataHub 在主线程集中执行；本方法只保留兼容入口。
    /// </summary>
    /// <param name="enable">是否启用异常检测</param>
    /// <param name="bufferSize">滑动窗口大小（保留多少帧历史用于对比）</param>
    /// <param name="thresholdDeg">角度跳变阈值（度），超过则丢弃该帧</param>
    public void ConfigureAnomalyDetection(bool enable, int bufferSize, float thresholdDeg)
    {
        // V77.25：SerialParser 只负责协议解析，不允许在后台接收线程修改姿态。
        // enable/bufferSize/thresholdDeg 由 SensorDataProcessor 构造 MotionDataHub 时读取。
        // 这里保留API以兼容旧Controller和旧场景，但明确关闭解析层过滤，防止重复过滤。
        Parser.Detector = null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  端口枚举与选择
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 通过 .NET SerialPort.GetPortNames() 刷新系统串口列表。
    /// 自动选中第一个可用端口（如果之前未选中或索引越界）。
    /// </summary>
    /// <returns>刷新后的端口名称数组</returns>
    public string[] RefreshPorts()
    {
        try
        {
            AvailablePorts = SerialPort.GetPortNames();
        }
        catch (Exception)
        {
            AvailablePorts = Array.Empty<string>();   // 枚举失败（权限/驱动问题）时置空
        }

        // 自动修正选中索引
        if (AvailablePorts.Length > 0)
        {
            if (SelectedPortIndex < 0 || SelectedPortIndex >= AvailablePorts.Length)
                SelectedPortIndex = 0;                // 索引越界时回到第一个
            CurrentPort = AvailablePorts[SelectedPortIndex];
        }
        else
        {
            SelectedPortIndex = -1;                   // 无可用端口
            CurrentPort = "";
        }
        return AvailablePorts;
    }

    /// <summary>
    /// 通过底层 controller.RefreshPorts 刷新端口列表。
    /// 某些平台下 controller 可能会进行额外的硬件探测。
    /// </summary>
    public string[] RefreshPortsViaController()
    {
        string[] ports;
        controller.RefreshPorts(out ports);            // 调用底层硬件探测
        AvailablePorts = ports ?? Array.Empty<string>();

        if (AvailablePorts.Length > 0)
        {
            if (SelectedPortIndex < 0 || SelectedPortIndex >= AvailablePorts.Length)
                SelectedPortIndex = 0;
            CurrentPort = AvailablePorts[SelectedPortIndex];
        }
        else
        {
            SelectedPortIndex = -1;
            CurrentPort = "";
        }
        return AvailablePorts;
    }

    /// <summary>
    /// 通过索引选择端口。
    /// 索引会被 Clamp 到合法范围，自动更新 CurrentPort。
    /// </summary>
    public void SelectPort(int index)
    {
        if (AvailablePorts.Length == 0) return;
        SelectedPortIndex = Mathf.Clamp(index, 0, AvailablePorts.Length - 1);
        CurrentPort = AvailablePorts[SelectedPortIndex];
    }

    /// <summary>
    /// 手动指定端口名（当系统未检测到任何端口时，允许用户手动输入）。
    /// </summary>
    public void SetPortManual(string port) => CurrentPort = port ?? "";

    /// <summary>
    /// 尝试将选中端口对齐到指定的默认端口名。
    /// 若默认端口存在于列表中则选中它，否则选中第一个。
    /// 在 Start() 初始化时调用。
    /// </summary>
    public void AlignToDefault(string defaultPort)
    {
        if (AvailablePorts.Length == 0) return;
        int idx = Array.IndexOf(AvailablePorts, defaultPort);   // 查找默认端口
        SelectPort(idx >= 0 ? idx : 0);                          // 找不到则用第一个
    }

    // ═══════════════════════════════════════════════════════════════
    //  连接与断开
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 使用当前选中端口 (CurrentPort) 和指定波特率连接串口。
    /// </summary>
    /// <returns>是否连接成功</returns>
    public bool Connect(int baud)
    {
        if (IsConnected) return true;                    // 已连接则直接返回
        if (string.IsNullOrEmpty(CurrentPort)) return false;  // 无端口可连

        return controller.Connect(CurrentPort, baud);         // 底层连接状态作为唯一事实来源
    }

    /// <summary>
    /// 指定端口名和波特率连接（便捷重载）。
    /// </summary>
    public bool Connect(string port, int baud)
    {
        CurrentPort = port;
        return Connect(baud);
    }

    /// <summary>
    /// 断开串口连接。底层会终止后台读取线程。
    /// </summary>
    public void Disconnect()
    {
        controller?.Disconnect();
    }

    /// <summary>
    /// 重置解析器内部状态（清空帧队列和缓冲区）。
    /// 在"重置"操作时调用，确保下次连接时不会读到旧数据。
    /// </summary>
    public void ResetParser()
    {
        Parser?.Reset();
    }

    /// <summary>释放资源：断开串口连接</summary>
    public void Dispose()
    {
        Disconnect();
    }
}




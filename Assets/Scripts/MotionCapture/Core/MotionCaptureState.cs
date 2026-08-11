
using System;
using UnityEngine;

/// <summary>
/// 动捕系统的集中状态容器。
/// 
/// 设计目的：
///   - 将所有运行时状态（连接、校准、驱动、稳定性等）集中管理，
///     避免状态散落在各个类中导致不一致。
///   - 提供 OnChanged 事件，仅在状态实际发生变化时触发，
///     解决原代码中每帧广播 OnStatusChanged 的性能问题。
///   - 为 UI 层提供只读查询接口，UI 不直接修改业务状态。
/// 
/// 使用方式：
///   - Controller 拥有 State 实例，通过 Refresh() 批量更新状态。
///   - UI 通过 OnChanged 事件接收状态变更通知，刷新界面显示。
///   - 设备级数据（每个传感器的四元数、欧拉角）也由此类统一管理。
/// </summary>
public class MotionCaptureState
{
    // ═══════════════════════════════════════════════════════════════
    //  核心状态属性（只读对外，仅通过方法修改以保证事件触发）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>串口是否已成功连接</summary>
    public bool IsConnected { get; private set; }

    /// <summary>是否至少收到过一个传感器的数据</summary>
    public bool HasAnyData { get; private set; }

    /// <summary>RotationDriver 是否已完成初始校准（记录传感器初始偏差）</summary>
    public bool IsCalibrated { get; private set; }

    /// <summary>是否处于驱动状态（正在将传感器数据应用到骨骼）</summary>
    public bool IsDriving { get; private set; }

    /// <summary>所有（或足够多的）传感器数据是否已稳定</summary>
    public bool IsStable { get; private set; }

    // ═══════════════════════════════════════════════════════════════
    //  事件
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 核心状态变更事件——仅在上述 5 个属性中任意一个实际改变时触发。
    /// UI 层订阅此事件以刷新界面，避免每帧轮询。
    /// 参数为 State 自身，订阅者可直接读取所有属性。
    /// </summary>
    public event Action<MotionCaptureState> OnChanged;

    /// <summary>
    /// 某个设备的欧拉角数据更新时触发。
    /// 参数：(设备ID, 欧拉角度数 Vector3(roll, pitch, yaw))
    /// 用于 UI 遥测数据表格的实时刷新。
    /// </summary>
    public event Action<int, Vector3> OnEulerUpdated;

    // ═══════════════════════════════════════════════════════════════
    //  设备级数据存储
    // ═══════════════════════════════════════════════════════════════

    private readonly int deviceCount;

    /// <summary>每个设备是否已收到过数据（索引 = 设备ID）</summary>
    private readonly bool[] deviceHasData;

    /// <summary>每个设备经坐标转换后的四元数（索引 = 设备ID）</summary>
    private readonly Quaternion[] deviceQuaternions;

    /// <summary>每个设备的欧拉角缓存，供 UI 读取（索引 = 设备ID）</summary>
    private readonly Vector3[] eulerCache;

    // ═══════════════════════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 初始化状态容器。
    /// </summary>
    /// <param name="deviceCount">传感器设备总数（通常为 9）</param>
    public MotionCaptureState(int deviceCount)
    {
        this.deviceCount = deviceCount;

        // 分配设备级数组
        deviceHasData = new bool[deviceCount];
        deviceQuaternions = new Quaternion[deviceCount];
        eulerCache = new Vector3[deviceCount];

        // 四元数初始化为 identity，避免初始值 (0,0,0,0) 导致异常
        for (int i = 0; i < deviceCount; i++)
            deviceQuaternions[i] = Quaternion.identity;
    }

    // ═══════════════════════════════════════════════════════════════
    //  核心状态修改方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 批量刷新所有核心状态，仅在至少一个字段有变化时才触发 OnChanged。
    /// 由 Controller 的 Update() 每帧调用，但事件不会每帧触发。
    /// </summary>
    /// <param name="connected">当前串口连接状态</param>
    /// <param name="hasData">是否至少有一个设备收到了数据</param>
    /// <param name="calibrated">旋转驱动器是否已完成校准</param>
    /// <param name="driving">是否正在驱动骨骼旋转</param>
    /// <param name="stable">系统稳定性判定结果</param>
    public void Refresh(bool connected, bool hasData, bool calibrated, bool driving, bool stable)
    {
        // 逐字段比对，全部相同则跳过——这是避免每帧触发事件的关键
        if (connected == IsConnected &&
            hasData == HasAnyData &&
            calibrated == IsCalibrated &&
            driving == IsDriving &&
            stable == IsStable)
            return;

        // 至少有一个字段变化，更新并广播
        IsConnected = connected;
        HasAnyData = hasData;
        IsCalibrated = calibrated;
        IsDriving = driving;
        IsStable = stable;
        OnChanged?.Invoke(this);
    }

    /// <summary>单独设置驱动状态（开始/停止驱动时由 Controller 调用）</summary>
    public void SetDriving(bool value)
    {
        if (IsDriving == value) return;   // 无变化则跳过
        IsDriving = value;
        OnChanged?.Invoke(this);
    }

    /// <summary>单独设置连接状态（断开时由 Controller 调用）</summary>
    public void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        OnChanged?.Invoke(this);
    }

    /// <summary>
    /// 全量重置到初始状态：清除所有连接/校准/驱动标志及设备数据。
    /// 在用户点击"重置"按钮时由 Controller 调用。
    /// </summary>
    public void Reset()
    {
        IsConnected = false;
        HasAnyData = false;
        IsCalibrated = false;
        IsDriving = false;
        IsStable = false;

        // 清空所有设备级数据
        for (int i = 0; i < deviceCount; i++)
        {
            deviceHasData[i] = false;
            deviceQuaternions[i] = Quaternion.identity;
            eulerCache[i] = Vector3.zero;
        }

        OnChanged?.Invoke(this);
    }

    // ═══════════════════════════════════════════════════════════════
    //  设备级数据访问（供 SensorDataProcessor 和 Controller 使用）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>查询某个设备是否已收到数据</summary>
    public bool GetDeviceHasData(int id) =>
        id >= 0 && id < deviceCount && deviceHasData[id];

    /// <summary>标记某个设备已收到数据</summary>
    public void SetDeviceHasData(int id, bool value)
    {
        if (id >= 0 && id < deviceCount) deviceHasData[id] = value;
    }

    /// <summary>返回 deviceHasData 数组引用（供 StabilityMonitor 批量读取）</summary>
    public bool[] GetDeviceHasDataArray() => deviceHasData;

    /// <summary>遍历检查是否至少有一个设备已收到数据</summary>
    public bool CheckHasAnyData()
    {
        for (int i = 0; i < deviceCount; i++)
            if (deviceHasData[i]) return true;
        return false;
    }

    /// <summary>获取指定设备经坐标转换后的四元数</summary>
    public Quaternion GetDeviceQuaternion(int id) =>
        (id >= 0 && id < deviceCount) ? deviceQuaternions[id] : Quaternion.identity;

    /// <summary>更新指定设备的四元数（由 SensorDataProcessor.TransformAll 调用）</summary>
    public void SetDeviceQuaternion(int id, Quaternion q)
    {
        if (id >= 0 && id < deviceCount) deviceQuaternions[id] = q;
    }

    /// <summary>获取指定设备的欧拉角缓存（供 UI 读取显示）</summary>
    public Vector3 GetEulerCache(int id) =>
        (id >= 0 && id < deviceCount) ? eulerCache[id] : Vector3.zero;

    /// <summary>
    /// 更新欧拉角缓存并触发 OnEulerUpdated 事件。
    /// 由 SensorDataProcessor.DequeueAll 在每次收到新帧时调用。
    /// </summary>
    public void NotifyEulerUpdated(int deviceId, Vector3 euler)
    {
        if (deviceId >= 0 && deviceId < deviceCount)
            eulerCache[deviceId] = euler;
        OnEulerUpdated?.Invoke(deviceId, euler);
    }
}




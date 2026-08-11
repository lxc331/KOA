using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// V77.24轻量异常检测器：不依赖 MonoBehaviour。
/// - 按设备维护最近若干帧的四元数队列
/// - 基于与历史平均的夹角门限检测异常
/// - 异常时用历史两帧做插值/外推估算替代值
/// 目标：在数据层初步去噪，减少驱动抖动。
/// </summary>
public class AnomalyDetector
{
    public int HistorySize { get; set; } = 10;
    public float AngleThresholdDeg { get; set; } = 45f;
    // V77.24：手臂康复动作可能在短时间内连续跨越较大角度。
    // 旧算法相对10帧历史平均超过45°就替换为旧估计值，会让0x03反复冻结。
    // 手臂0x01-0x04改为仅拦截单帧不可能的大跳变。
    public float ArmSingleFrameHardJumpDeg { get; set; } = 135f;

    private Dictionary<int, Queue<Quaternion>> history = new Dictionary<int, Queue<Quaternion>>();

    private void EnsureDevice(int deviceId)
    {
        if (!history.ContainsKey(deviceId)) history[deviceId] = new Queue<Quaternion>(HistorySize);
    }

    public Quaternion Process(int deviceId, Quaternion raw, out bool wasAnomaly)
    {
        wasAnomaly = false;
        EnsureDevice(deviceId);
        var h = history[deviceId];
        raw = raw.normalized;
        Quaternion last = Quaternion.identity;
        if (h.Count > 0)
        {
            var arr = h.ToArray();
            last = arr[arr.Length - 1];
            if (Quaternion.Dot(last, raw) < 0f)
                raw = new Quaternion(-raw.x, -raw.y, -raw.z, -raw.w);
        }
        // V77.24：索引0-3为四个手臂传感器。
        // 不能再与10帧历史平均比较，否则正常的前举→上举→下垂会被误判并返回旧姿态。
        // 只在相邻两帧真的出现超大不连续跳变时保留上一帧；正常连续运动全部放行。
        if (deviceId >= 0 && deviceId <= 3)
        {
            if (h.Count > 0)
            {
                float frameAngle = Quaternion.Angle(last, raw);
                if (frameAngle > Mathf.Clamp(ArmSingleFrameHardJumpDeg, 90f, 179f))
                {
                    wasAnomaly = true;
                    return last.normalized;
                }
            }

            AddToHistory(deviceId, raw);
            return raw;
        }

        // 腿部和躯干保持原来的保守历史平均过滤，避免本轮改动影响腿部基线。
        if (h.Count < 3)
        {
            AddToHistory(deviceId, raw);
            return raw;
        }
        Quaternion avg = CalculateAverageQuaternion(new List<Quaternion>(h));
        float angle = Quaternion.Angle(avg, raw);
        if (angle > AngleThresholdDeg)
        {
            wasAnomaly = true;
            Quaternion est = EstimateQuaternionWithInterpolation(deviceId).normalized;
            // V77.25：异常原始帧不能写入历史，否则会污染后续历史平均，造成连续冻结/回弹。
            // 历史中只保存本次真正采用的估计输出。
            AddToHistory(deviceId, est);
            return est;
        }
        AddToHistory(deviceId, raw);
        return raw;
    }

    public void Reset()
    {
        history.Clear();
    }

    private void AddToHistory(int deviceId, Quaternion q)
    {
        EnsureDevice(deviceId);
        var qh = history[deviceId];
        qh.Enqueue(q);
        while (qh.Count > HistorySize) qh.Dequeue();
    }

    private Quaternion CalculateAverageQuaternion(List<Quaternion> quaternions)
    {
        if (quaternions == null || quaternions.Count == 0) return Quaternion.identity;
        Quaternion avg = quaternions[0];
        for (int i = 1; i < quaternions.Count; i++)
        {
            float w = 1.0f / (i + 1);
            avg = Quaternion.Slerp(avg, quaternions[i], w);
        }
        return avg;
    }

    private Quaternion EstimateQuaternionWithInterpolation(int deviceId, float alpha = 0.7f)
    {
        EnsureDevice(deviceId);
        var qh = history[deviceId];
        if (qh.Count == 0) return Quaternion.identity;
        if (qh.Count == 1) return qh.Peek();

        Quaternion[] arr = qh.ToArray();
        Quaternion prev = arr[arr.Length - 2];
        Quaternion last = arr[arr.Length - 1];

        Quaternion interp = Quaternion.Slerp(prev, last, alpha);
        float extrapolateFactor = 1.1f;
        Quaternion est = Quaternion.Slerp(last, interp, extrapolateFactor - 1.0f);
        return est.normalized;
    }
} 

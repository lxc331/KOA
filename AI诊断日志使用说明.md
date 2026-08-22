# AI诊断日志使用说明（V8.20）

## 已有功能与本次补充

- 原有 Excel 遥测：记录姿态与通信明细，通常在标定锁定或人物开始驱动后启动，正常停止时生成 `.xlsx`。
- 原有 Unity Console：能看到警告和错误，但客户若未保存 Console，远程排障信息容易丢失。
- AI 诊断日志：点击“连接”时立即创建 `AI诊断_日期_时间.jsonl`，不依赖标定成功；每行写入后立即刷新到磁盘，Unity 异常退出时已写入部分仍可读取。
- V8.20 新增腿部时配对、局部保持、输入大跳变和 Zigbee 自动重同步记录，用于区分“无线丢包”“两块腿板时间不一致”和“传感器输入本身突变”。

## 客户如何测试

1. 不需要填写路径。每次点击连接，程序都会在项目 `Logs` 下自动创建一个日期时间文件夹，例如 `Logs/20260812_201530_126/`。
2. AI 日志与本次测试的 Excel 都写入该文件夹；界面出现“AI诊断自动记录: AI诊断_xxx.jsonl”即已开始记录。
3. 正常操作并复现问题。问题刚发生时点击一次“标记异常”，日志会保存精确时刻和当时九路快照；建议视频里同时拍到遥测表与问题发生时刻。
4. 测试结束后点击断开或重置。即使 Unity 崩溃，也不要删除已经生成的 `.jsonl`。
5. 将同次测试的 `.jsonl`、`.xlsx`、视频以及九块控制板烧录 ID 对照一起发送。

## 日志记录内容

- 会话环境：软件版本、Unity 版本、操作系统、串口、波特率。
- 状态机事件：连接结果、标定锁定、等待运行数据、进入驱动、故障暂停、恢复驱动、断开和重置。
- 每秒九路快照：设备 ID/部位、在线、运行就绪、稳定、标定状态、四元数、Unity 接收 Hz、控制板源端 Hz、最后一帧年龄、链路到达率、源端丢帧/重复/乱序/重启、硬件 ID 和源帧序号。
- 腿部配对快照：`leg_pair_required`、`leg_pair_fresh`、`leg_pair_skew_ms`、`leg_pair_age_ms` 和累计保持次数 `leg_pair_hold_count`。
- 关键事件：腿部配对保持/恢复 `leg_pair_drive_held/resumed`、输入大跳变 `leg_input_large_step`、Zigbee 同步状态变化与自动重发 `zigbee_sync_status_changed`、`zigbee_schedule_resync_sent/failed`。
- 全局通信错误：XOR/CRC、非法包长、非法四元数、非法设备 ID、奇偶/帧/溢出错误、重复 ID、队列积压和丢弃。
- Unity 的 Warning、Error、Exception 与错误堆栈。
- 相同高频 Unity 报错会按 2 秒限流并记录被抑制次数，避免日志磁盘写入反过来影响实时串口。

## 快速判断思路

- `source_hz` 接近 10、`receive_hz` 很低、`source_lost` 快速增加：数据在控制板发送后、进入 Unity 前大量丢失，重点检查九设备 Zigbee 轮询/带宽/接收端。
- `source_hz` 与 `receive_hz` 都低：重点检查控制板传感器读取周期、发送定时与单板运行。
- `crc_fail`、`invalid_len`、`parity_error`、`frame_error` 增加：重点检查串口参数、字节串扰、供电和接线。
- `queue_drop` 或 `backlog_discarded` 增加：Unity 主线程来不及消费或窗口失焦/卡顿。
- `duplicate_id_conflict` 或 `duplicate_logical_id` 增加：至少两块板烧录了相同 `DEVICE_LOGICAL_ID`。
- `age_ms` 突然增大且只有固定设备反复触发 `runtime_link_fault`：优先检查该设备、该设备供电及对应轮询槽位。
- `leg_pair_fresh=false`、`leg_pair_hold_count` 增加：同一条腿的大腿板和小腿板没有在允许时间窗内形成可靠配对；V8.20 会保持小腿最后姿态，不再用错时刻数据计算膝关节。
- `leg_input_large_step` 持续集中在同一 ID：重点检查该板安装方向、绑带松动、传感器复位或源端姿态跳变；它不是 Unity 串轴的直接证据。
- `source_link_synchronized=false`：Unity 会每 3 秒自动重发时隙同步，全部同步后每 30 秒维护一次；若 10 秒后仍未恢复，应检查协调器是否把下行命令广播到了全部节点。
- 单路短暂掉线时，V8.20 只暂停该骨骼更新并保持最后姿态；重复 ID、串口故障等全局不安全事件仍会停止整个人物驱动。

JSONL 是“一行一个 JSON 对象”的文本格式，可以直接发送给 AI 分析，不需要客户手工整理。

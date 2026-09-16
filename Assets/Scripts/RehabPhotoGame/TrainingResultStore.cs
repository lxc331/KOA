using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace RehabPhotoGame
{
    /// <summary>独立于动捕日志开关的本地游戏记录。结果以 session_id 幂等落盘。</summary>
    public sealed class TrainingResultStore
    {
        private readonly string directory;
        public TrainingResultStore(string directory) { this.directory = Path.GetFullPath(directory); }
        public string SavePhoto(string sessionId, string photoId, byte[] jpg)
        {
            ValidateId(sessionId);
            ValidateId(photoId);
            if (jpg == null || jpg.Length < 2 || jpg[0] != 0xff || jpg[1] != 0xd8)
                throw new ArgumentException("组合照片必须为有效 JPG。");
            Directory.CreateDirectory(directory);
            string name = sessionId + "_" + photoId + ".jpg";
            string path = Path.Combine(directory, name);
            // 唯一照片 ID，已获得的作品永远不覆盖或删除。
            if (File.Exists(path)) return name;
            string temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(jpg, 0, jpg.Length);
                stream.Flush(true);
            }
            File.Move(temp, path);
            return name;
        }
        public void Append(string sessionId, string json)
        {
            ValidateId(sessionId);
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(Path.Combine(directory, sessionId + ".events.jsonl"),
                FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        public string Save(TrainingSessionResult result)
        {
            ValidateId(result.session_id);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, result.session_id + ".summary.json");
            string json = JsonUtility.ToJson(result, true);
            if (File.Exists(path))
            {
                if (File.ReadAllText(path) != json) throw new IOException("同一训练 ID 的结果已存在且内容不同。");
                return path;
            }
            string temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            File.Move(temp, path);
            return path;
        }
        private static void ValidateId(string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("无效训练 ID");
        }
    }
}

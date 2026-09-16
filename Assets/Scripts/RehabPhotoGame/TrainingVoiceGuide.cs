using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace RehabPhotoGame
{
    public enum TrainingVoiceCue
    {
        PhotoCompletedReturn,
        SignalRecovering,
        SignalRecovered,
        ReadyForNext,
        SignalError
    }

    /// <summary>正式语音文案与预录音资源名。只描述游戏流程，不评价动作或疗效。</summary>
    public static class TrainingVoicePrompts
    {
        public static string Text(TrainingVoiceCue cue, PhotoTrainingMode mode)
        {
            switch (cue)
            {
                case TrainingVoiceCue.PhotoCompletedReturn:
                    return mode == PhotoTrainingMode.ShallowSquat
                        ? "拍照完成，请缓慢站直并站稳。"
                        : mode == PhotoTrainingMode.SitToStand
                            ? "拍照完成，请缓慢坐回并坐稳。"
                            : "拍照完成，请缓慢放下小腿并坐稳。";
                case TrainingVoiceCue.SignalRecovering:
                    return "信号波动，请保持稳定，正在恢复。";
                case TrainingVoiceCue.SignalRecovered:
                    return "信号已恢复，请按屏幕提示继续。";
                case TrainingVoiceCue.ReadyForNext:
                    return mode == PhotoTrainingMode.ShallowSquat
                        ? "准备完成，请开始下一次浅蹲。"
                        : mode == PhotoTrainingMode.SitToStand
                            ? "准备完成，请开始下一次坐站。"
                            : "准备完成，请开始下一次伸膝。";
                default:
                    return "信号持续中断，请检查传感器连接和佩戴。";
            }
        }

        public static string ResourcePath(TrainingVoiceCue cue, PhotoTrainingMode mode)
        {
            const string root = "RehabPhotoGame/Voice/";
            switch (cue)
            {
                case TrainingVoiceCue.PhotoCompletedReturn:
                    return root + "photo_complete_" + ModeKey(mode);
                case TrainingVoiceCue.SignalRecovering:
                    return root + "signal_recovering";
                case TrainingVoiceCue.SignalRecovered:
                    return root + "signal_recovered";
                case TrainingVoiceCue.ReadyForNext:
                    return root + "ready_" + ModeKey(mode);
                default:
                    return root + "signal_error";
            }
        }

        private static string ModeKey(PhotoTrainingMode mode)
        {
            return mode == PhotoTrainingMode.ShallowSquat ? "squat" :
                mode == PhotoTrainingMode.SitToStand ? "sit_to_stand" : "knee_extension";
        }
    }

    /// <summary>
    /// 游戏流程语音层。优先播放 Resources 中的预录 AudioClip；Windows 上没有录音时使用系统中文 TTS 后备。
    /// 不读取或修改传感器、骨骼、动作判定与根节点。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TrainingVoiceGuide : MonoBehaviour
    {
        private const float DuplicateCueGapSeconds = 5f;
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private AudioSource voiceSource;
        private Process speechProcess;
        private string lastCueKey = "";
        private float lastCueAt = float.NegativeInfinity;

        public string LastText { get; private set; } = "";
        public string LastResourcePath { get; private set; } = "";

        private void Awake()
        {
            voiceSource = gameObject.AddComponent<AudioSource>();
            voiceSource.playOnAwake = false;
            voiceSource.loop = false;
            voiceSource.spatialBlend = 0f;
            voiceSource.volume = 0.9f;
        }

        public bool Speak(TrainingVoiceCue cue, PhotoTrainingMode mode)
        {
            string resourcePath = TrainingVoicePrompts.ResourcePath(cue, mode);
            string text = TrainingVoicePrompts.Text(cue, mode);
            string cueKey = cue + ":" + mode;
            return PlayPrompt(resourcePath, text, cueKey);
        }

        public bool SpeakText(string resourceKey, string text) =>
            PlayPrompt("RehabPhotoGame/Voice/" + resourceKey, text, resourceKey);

        private bool PlayPrompt(string resourcePath, string text, string cueKey)
        {
            float now = Time.realtimeSinceStartup;
            if (cueKey == lastCueKey && now - lastCueAt < DuplicateCueGapSeconds)
                return false;

            lastCueKey = cueKey;
            lastCueAt = now;
            LastText = text;
            LastResourcePath = resourcePath;
            StopPlayback();

            AudioClip clip = LoadClip(resourcePath);
            if (clip != null)
            {
                voiceSource.PlayOneShot(clip);
                return true;
            }

            SpeakWithSystemVoice(text);
            return true;
        }

        public void StopPlayback()
        {
            if (voiceSource != null) voiceSource.Stop();
            if (speechProcess == null) return;
            try { if (!speechProcess.HasExited) speechProcess.Kill(); } catch { }
            try { speechProcess.Dispose(); } catch { }
            speechProcess = null;
        }

        private AudioClip LoadClip(string path)
        {
            if (clips.TryGetValue(path, out AudioClip cached)) return cached;
            AudioClip clip = Resources.Load<AudioClip>(path);
            clips[path] = clip;
            return clip;
        }

        private void SpeakWithSystemVoice(string text)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (Application.isBatchMode) return;
            try
            {
                string escaped = (text ?? "").Replace("'", "''");
                string command =
                    "Add-Type -AssemblyName System.Speech; " +
                    "$s=New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                    "$voices=$s.GetInstalledVoices() | Where-Object {$_.Enabled}; " +
                    "$v=$voices | Where-Object {$_.VoiceInfo.Culture.Name -like 'zh-*'} | Select-Object -First 1; " +
                    "if($v){$s.SelectVoice($v.VoiceInfo.Name)}; " +
                    "$s.Rate=-3; $s.Volume=85; $s.Speak('" + escaped + "');";
                speechProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -WindowStyle Hidden -Command \"" + command + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[TrainingVoiceGuide] " + exception.Message);
            }
#endif
        }

        private void OnDisable() => StopPlayback();
        private void OnDestroy() => StopPlayback();
    }
}

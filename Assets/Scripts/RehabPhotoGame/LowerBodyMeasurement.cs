using System;

namespace RehabPhotoGame
{
    /// <summary>角度约定：膝伸直为 0°，坐姿通常约 90°；来自标定后四元数。</summary>
    [Serializable]
    public struct LowerBodyMeasurement
    {
        public bool IsValid;
        public string FailureReason;
        public int FreshMask;
        public int CalibrationVersion;
        public float SampleTimeSeconds;
        public float LeftThighDeg, RightThighDeg;
        public float LeftKneeDeg, RightKneeDeg;
        public float Sensor06AgeSeconds, Sensor07AgeSeconds;
        public float Sensor08AgeSeconds, Sensor09AgeSeconds;

        public float GetSensorAgeSeconds(int bit)
        {
            switch (bit)
            {
                case 0: return Sensor06AgeSeconds;
                case 1: return Sensor07AgeSeconds;
                case 2: return Sensor08AgeSeconds;
                case 3: return Sensor09AgeSeconds;
                default: return -1f;
            }
        }

        public void SetSensorAgeSeconds(int bit, float value)
        {
            switch (bit)
            {
                case 0: Sensor06AgeSeconds = value; break;
                case 1: Sensor07AgeSeconds = value; break;
                case 2: Sensor08AgeSeconds = value; break;
                case 3: Sensor09AgeSeconds = value; break;
            }
        }
    }
}

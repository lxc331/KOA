using UnityEngine;

namespace RehabPhotoGame
{
    /// <summary>可保存的游戏联调配置。运行时复制一次，避免播放期间误写共享资产。</summary>
    [CreateAssetMenu(menuName = "Rehab Photo Game/浅蹲游戏联调参数",
        fileName = "ShallowSquatGameTuning")]
    public sealed class ShallowSquatGameTuning : ScriptableObject
    {
        public const string ResourcePath = "RehabPhotoGame/ShallowSquatGameTuning";

        [Tooltip("停止播放后修改并保存；下次播放自动加载。仅影响游戏判定，不改变人物驱动。")]
        public ShallowSquatSettings settings = new ShallowSquatSettings();

        public ShallowSquatSettings CreateRuntimeSettings()
        {
            return settings == null ? null :
                JsonUtility.FromJson<ShallowSquatSettings>(JsonUtility.ToJson(settings));
        }
    }
}

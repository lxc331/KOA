using UnityEngine;

namespace RehabPhotoGame
{
    [CreateAssetMenu(menuName = "Rehab Photo Game/训练流程参数", fileName = "TrainingFlowConfig")]
    public sealed class TrainingFlowConfig : ScriptableObject
    {
        [Tooltip("游戏流程参数；每次开始训练复制配置，正在进行的训练不随资产变更。")]
        public TrainingFlowSettings settings = new TrainingFlowSettings();
    }
}

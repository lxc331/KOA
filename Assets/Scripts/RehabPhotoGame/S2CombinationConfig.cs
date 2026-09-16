using UnityEngine;

namespace RehabPhotoGame
{
    [CreateAssetMenu(menuName = "Rehab Photo Game/S2 Combination Config")]
    public sealed class S2CombinationConfig : ScriptableObject
    {
        public S2CombinationSettings settings = new S2CombinationSettings();
    }
}

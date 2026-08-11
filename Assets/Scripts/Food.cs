using Sirenix.OdinInspector;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Food : BaseComponent
{

    public Vector3 location;
    public Vector3 localrotaion;
   public enum FoodType
    {
        water,
        food
    }
    public enum BattleType
    {
        none,
        Æ¿×Ó,
        ¹Þ×Ó
    }

    public FoodType type = FoodType.water;
    public BattleType battleType = BattleType.none;

    [Button]
    private void ReadLocation()
    {
        location = transform.localPosition;
        localrotaion = transform.localEulerAngles; ;
    }
    [Button]
    public void SetLocation()
    {
        transform.localPosition = location;
        transform.localEulerAngles = localrotaion;
    }
}

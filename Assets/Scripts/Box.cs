using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Box : MonoBehaviour
{
    public Food.BattleType battleType = Food.BattleType.ф©вс;
    public void OnTriggerEnter(Collider other)
    {
        var food = other.GetComponent<Food>();
        if (food != null)
        {
            if(food.battleType == battleType)
            {
                Destroy(food.gameObject);
                UIManager.Instance.AddScore();
                Generate.Instance.Get();
            }
        }
    }
}

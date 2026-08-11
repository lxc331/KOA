using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Hand : MonoBehaviour
{
    public enum HandType {
    left,
    right
    }
    public HandType type = HandType.left;

    private void OnTriggerEnter(Collider other)
    {
        if (UIManager.Instance.IsEnd) return;
        var food = other.GetComponent<Food>();
        if (food != null)
        {
            if ((food.type == Food.FoodType.food && type == HandType.right) || (type == HandType.left && food.type == Food.FoodType.water))
            {
                food.transform.SetParent(transform);
                food.transform.localPosition = food.location;
                food.transform.localEulerAngles = food.localrotaion;
            }
        }
        else
        {
            var hide = other.GetComponent<HideComponent>();
            if (hide != null)
            {
                hide.gameObject.SetActive(false);
                UIManager.Instance.AddScore();
            }
        }
    }
}

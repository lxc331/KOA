using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HideManager : MonoBehaviour
{
    private void Start()
    {
        UIManager.Instance.OnReset.AddListener(() =>
        {
            foreach (Transform child in transform)
            {
                child.gameObject.SetActive(true);
            }
        });
    }
}

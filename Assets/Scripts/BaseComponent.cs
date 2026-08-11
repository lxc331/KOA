using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BaseComponent : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        var mouse = other.GetComponent<Mouse>();
        if(mouse != null )
        {
            Destroy(gameObject);
        }
    }
}

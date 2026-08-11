using Sirenix.OdinInspector;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Generate : MonoBehaviour
{
    public static Generate Instance;
    public List<GameObject> prefabs = new List<GameObject>();

    private List<GameObject> currents = new List<GameObject>();
    private void Awake()
    {
        Instance = this;
        Get();
       
    }
    private void Start()
    {
        UIManager.Instance.OnReset.AddListener(() =>
        {
            foreach (var go in currents)
            {
                Destroy(go);
            }
            Get();
        });
    }
    [Button]
    public void Get()
    {
        var index = Random.Range(0,prefabs.Count); ;
        var go = Instantiate(prefabs[index]); ;
        go.transform.position = GetRandomPos();
        currents.Add(go);
    }

    public Vector3 GetRandomPos()
    {
        var x = transform.localScale.x * 0.5f;
        var z = transform.localScale.z * 0.5f; ;

        var offset = new Vector3(Random.Range(-x, x), -transform.localScale.y * 0.5f, Random.Range(-z, z));
        return transform.position + offset;
    }
}

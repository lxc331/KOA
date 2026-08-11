using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Mouse : MonoBehaviour
{
    public Vector3 waterPos;
    public Vector3 foodPos;
    public List<GameObject> foodList = new List<GameObject>();
    public GameObject waterPrefab;

    private List<GameObject> gameObjects = new List<GameObject>();
    
    private void Awake()
    {
        UIManager.Instance.OnReset.AddListener(() =>
        {
            foreach (var item in gameObjects)
            {
                Destroy(item);
            }
            InitGame();
        });
        InitGame();
    }
    void InitGame()
    {
        var GO = Instantiate(foodList[Random.Range(0, foodList.Count)]);
        GO.transform.position = foodPos;
        gameObjects.Add(GO);
        GO = Instantiate(waterPrefab);
        GO.transform.position = waterPos;
        gameObjects.Add(GO);
    }
    private void OnTriggerEnter(Collider other)
    {
        if (UIManager.Instance.IsEnd) return;
        var food = other.GetComponent<Food>();
        if (food != null)
        {
            Destroy(food.gameObject);
            UIManager.Instance.AddScore();
            if(food.type == Food.FoodType.food)
            {
                var GO = Instantiate(foodList[Random.Range(0, foodList.Count)]);
                GO.transform.position = foodPos;
                gameObjects.Add(GO);
            }
            else
            {
                var GO = Instantiate(waterPrefab);
                GO.transform.position = waterPos;
                gameObjects.Add(GO);
            }
        }

    }
}

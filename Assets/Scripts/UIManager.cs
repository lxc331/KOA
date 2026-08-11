using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

public class UIManager : MonoBehaviour
{
    public UnityEvent OnReset = new UnityEvent();
    public static UIManager Instance;
    public TextMeshProUGUI scoreText;
    public TextMeshProUGUI timerText;

    public float allTime = 180;

    private void Awake()
    {
        Instance = this;
        UpdateTimerText();
    }
    public int score = 0;

    public void AddScore()
    {
        score++;
        scoreText.text = $"score: {score}";
    }

    public bool IsEnd { get => allTime <= 0; }

    private void Update()
    {
       if(allTime > 0)
        {
            allTime -= Time.deltaTime;
            UpdateTimerText();
        }
    }

    void UpdateTimerText()
    {
        var min = Mathf.FloorToInt(allTime / 60);
        var second = Mathf.FloorToInt(allTime % 60);
        var str = string.Format("{0:00}:{1:00}", min, second);
        timerText.text = str;
    }

    public void ResetGame()
    {
        allTime = 180;
        score = 0;
        UpdateTimerText();
        scoreText.text = $"score: {score}";
        OnReset?.Invoke();
    }

    public void ChangeScene(int index)
    {
        SceneManager.LoadScene(index);
    }
}

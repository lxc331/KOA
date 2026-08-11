using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GyroCube : MonoBehaviour {

    private Quaternion q1 = Quaternion.identity;
    private Quaternion a1 = Quaternion.identity;

    void Start () {
        Input.gyro.enabled = true;

        q1 = GyroToUnity(Input.gyro.attitude);
        a1 = transform.rotation;
    }

    void Update () {
        Quaternion q = Quaternion.Inverse(q1) * a1 * GyroToUnity(Input.gyro.attitude);
        transform.rotation = new Quaternion(q.x, -q.z, q.y, q.w);
    }

    public void OnClickButton()
    {
        q1 = GyroToUnity(Input.gyro.attitude);

        transform.rotation = Quaternion.identity;
        a1 = transform.rotation;
    }

    private static Quaternion GyroToUnity(Quaternion q)
    {
        return new Quaternion(q.x, q.y, -q.z, -q.w);
    }

    protected void OnGUI()
    {
        GUI.skin.label.fontSize = Screen.width / 60;
        GUILayout.Label("Orientation: " + Screen.orientation);

        GUILayout.Label("q1: " + q1);
        GUILayout.Label("a1: " + a1);
        GUILayout.Label("Quaternion.Inverse(q1) * a1: " + Quaternion.Inverse(q1) * a1);

        GUILayout.Label("GyroToUnity(Input.gyro.attitude): " + GyroToUnity(Input.gyro.attitude));
        GUILayout.Label("Quaternion.Inverse(q1) * a1 * GyroToUnity(Input.gyro.attitude): " + Quaternion.Inverse(q1) * a1 * GyroToUnity(Input.gyro.attitude));

        GUILayout.Label("transform.rotation: " + transform.rotation);
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Ports;
using System.IO;
using UnityEngine;
public class move_2 : MonoBehaviour
{

    #region 串口参数,主要修改串口名与波特率
    /// <summary>
    /// 串口名
    /// </summary>
    //是否使用远程位移
    string getPortName;
    Quaternion q;
    private GameObject obj, obj_1, obj_3, obj_4, obj_5, obj_6, obj_7, obj_8, obj_9;
    String txtPath_1 = "D:\\unity\\Project location\\text_1.txt";
    String txtPath_2 = "D:\\unity\\Project location\\text_2.txt";
    /// <summary>
    /// 波特率
    /// </summary>
    int flag = 0, work_state = 0, quation_flag = 0;
    string bote = "115200", state_button = "连接";
    int baudRate = 115200, set;
    float yaw, pitch, roll, yaw_1, pitch_1, roll_1, yaw_3, pitch_3, roll_3, yaw_4, pitch_4, roll_4;
    float yaw_5, pitch_5, roll_5, yaw_6, pitch_6, roll_6, yaw_7, pitch_7, roll_7, yaw_8, pitch_8, roll_8, yaw_9, pitch_9, roll_9;
    float yaw_cha, pitch_cha, roll_cha;
    float yaw_cha_2, pitch_cha_2, roll_cha_2;
    float q0, q1, q2, q3;
    float q0_1, q1_1, q2_1, q3_1, q0_2, q1_2, q2_2, q3_2;
    float yaw_now, pitch_now, roll_now, yaw_last, pitch_last, roll_last;
    float yaw_now_2, pitch_now_2, roll_now_2, yaw_last_2, pitch_last_2, roll_last_2;
    int state = 0;
    private Parity parity = Parity.None;
    private int dataBits = 8;
    private StopBits stopBits = StopBits.One;
    SerialPort sp = null;
    Quaternion device_1, device_2, device_3, device_4, device_5, device_6, device_7, device_8, device_9, now_q, last_q, cha, cha_2, cha_3, cha_4, cha_5, cha_6, cha_7, cha_8, cha_9;
    Vector3 eulerAngle_1, eulerAngle_2, eulerAngle_3, eulerAngle_4, eulerAngle_5, eulerAngle_6, eulerAngle_7, eulerAngle_8, eulerAngle_9;
    int count = 0;
    Quaternion device_1_now, device_1_last, device_cha, device_2_now, device_3_now, device_4_now, device_5_now, device_6_now, device_7_now, device_8_now, device_9_now;
    Quaternion result, result_2, result_3, result_4, result_5, result_6, result_7, result_8, result_9;
    #endregion

    #region 消息处理相关
    /// <summary>
    /// 缓存消息列表
    /// </summary>
    List<byte> bufferList = new List<byte>();
    /// <summary>
    /// 一条消息的长度
    /// </summary>
    int messageLen = 24;
    #endregion

    void OnGUI()
    {
        GUI.Window(0, new Rect(0, 5, 500, 250), windfunc, "Data Interfaces");
        //GUI.Window(1, new Rect(150, 10, 130, 230), windfunc, "设备二");
        //GUI.Window(2, new Rect(300, 10, 130, 230), windfunc, "设备三");
        GUI.Window(10, new Rect(0, 750, 170, 120), windfunc, "Control Interface");


    }
    void windfunc(int windowID)
    {
        if (windowID == 0)
        {

            GUI.Label(new Rect(15, 20, 100, 40), "number");
            GUI.Label(new Rect(90, 20, 60, 40), "Q0");
            GUI.Label(new Rect(150, 20, 60, 40), "Q1");
            GUI.Label(new Rect(220, 20, 60, 40), "Q2");
            GUI.Label(new Rect(270, 20, 60, 40), "Q3");
            GUI.Label(new Rect(330, 20, 60, 40), "yaw");
            GUI.Label(new Rect(390, 20, 60, 40), "Pitch");
            GUI.Label(new Rect(450, 20, 60, 40), "Roll");

            GUI.Label(new Rect(20, 40, 30, 60), "0x01");
            GUI.TextField(new Rect(70, 40, 60, 20), device_1.w.ToString());
            GUI.TextField(new Rect(130, 40, 60, 20), device_1.x.ToString());
            GUI.TextField(new Rect(190, 40, 60, 20), device_1.y.ToString());
            GUI.TextField(new Rect(250, 40, 60, 20), device_1.z.ToString());
            GUI.TextField(new Rect(310, 40, 60, 20), yaw.ToString());
            GUI.TextField(new Rect(370, 40, 60, 20), pitch.ToString());
            GUI.TextField(new Rect(430, 40, 60, 20), roll.ToString());

            GUI.Label(new Rect(20, 60, 30, 60), "0x02");
            GUI.TextField(new Rect(70, 60, 60, 20), device_2.w.ToString());
            GUI.TextField(new Rect(130, 60, 60, 20), device_2.x.ToString());
            GUI.TextField(new Rect(190, 60, 60, 20), device_2.y.ToString());
            GUI.TextField(new Rect(250, 60, 60, 20), device_2.z.ToString());
            GUI.TextField(new Rect(310, 60, 60, 20), yaw_1.ToString());
            GUI.TextField(new Rect(370, 60, 60, 20), pitch_1.ToString());
            GUI.TextField(new Rect(430, 60, 60, 20), roll_1.ToString());

            GUI.Label(new Rect(20, 80, 30, 60), "0x03");
            GUI.TextField(new Rect(70, 80, 60, 20), device_3.w.ToString());
            GUI.TextField(new Rect(130, 80, 60, 20), device_3.x.ToString());
            GUI.TextField(new Rect(190, 80, 60, 20), device_3.y.ToString());
            GUI.TextField(new Rect(250, 80, 60, 20), device_3.z.ToString());
            GUI.TextField(new Rect(310, 80, 60, 20), yaw_3.ToString());
            GUI.TextField(new Rect(370, 80, 60, 20), pitch_3.ToString());
            GUI.TextField(new Rect(430, 80, 60, 20), roll_3.ToString());

            GUI.Label(new Rect(20, 100, 30, 60), "0x04");
            GUI.TextField(new Rect(70, 100, 60, 20), device_4.w.ToString());
            GUI.TextField(new Rect(130, 100, 60, 20), device_4.x.ToString());
            GUI.TextField(new Rect(190, 100, 60, 20), device_4.y.ToString());
            GUI.TextField(new Rect(250, 100, 60, 20), device_4.z.ToString());
            GUI.TextField(new Rect(310, 100, 60, 20), yaw_4.ToString());
            GUI.TextField(new Rect(370, 100, 60, 20), pitch_4.ToString());
            GUI.TextField(new Rect(430, 100, 60, 20), roll_4.ToString());

            GUI.Label(new Rect(20, 120, 30, 60), "0x05");
            GUI.TextField(new Rect(70, 120, 60, 20), device_5.w.ToString());
            GUI.TextField(new Rect(130, 120, 60, 20), device_5.x.ToString());
            GUI.TextField(new Rect(190, 120, 60, 20), device_5.y.ToString());
            GUI.TextField(new Rect(250, 120, 60, 20), device_5.z.ToString());
            GUI.TextField(new Rect(310, 120, 60, 20), yaw_5.ToString());
            GUI.TextField(new Rect(370, 120, 60, 20), pitch_5.ToString());
            GUI.TextField(new Rect(430, 120, 60, 20), roll_5.ToString());

            GUI.Label(new Rect(20, 140, 30, 60), "0x06");
            GUI.TextField(new Rect(70, 140, 60, 20), device_6.w.ToString());
            GUI.TextField(new Rect(130, 140, 60, 20), device_6.x.ToString());
            GUI.TextField(new Rect(190, 140, 60, 20), device_6.y.ToString());
            GUI.TextField(new Rect(250, 140, 60, 20), device_6.z.ToString());
            GUI.TextField(new Rect(310, 140, 60, 20), yaw_6.ToString());
            GUI.TextField(new Rect(370, 140, 60, 20), pitch_6.ToString());
            GUI.TextField(new Rect(430, 140, 60, 20), roll_6.ToString());

            GUI.Label(new Rect(20, 160, 30, 60), "0x07");
            GUI.TextField(new Rect(70, 160, 60, 20), device_7.w.ToString());
            GUI.TextField(new Rect(130, 160, 60, 20), device_7.x.ToString());
            GUI.TextField(new Rect(190, 160, 60, 20), device_7.y.ToString());
            GUI.TextField(new Rect(250, 160, 60, 20), device_7.z.ToString());
            GUI.TextField(new Rect(310, 160, 60, 20), yaw_7.ToString());
            GUI.TextField(new Rect(370, 160, 60, 20), pitch_7.ToString());
            GUI.TextField(new Rect(430, 160, 60, 20), roll_7.ToString());

            GUI.Label(new Rect(20, 180, 30, 60), "0x08");
            GUI.TextField(new Rect(70, 180, 60, 20), device_8.w.ToString());
            GUI.TextField(new Rect(130, 180, 60, 20), device_8.x.ToString());
            GUI.TextField(new Rect(190, 180, 60, 20), device_8.y.ToString());
            GUI.TextField(new Rect(250, 180, 60, 20), device_8.z.ToString());
            GUI.TextField(new Rect(310, 180, 60, 20), yaw_8.ToString());
            GUI.TextField(new Rect(370, 180, 60, 20), pitch_8.ToString());
            GUI.TextField(new Rect(430, 180, 60, 20), roll_8.ToString());

            GUI.Label(new Rect(20, 200, 30, 60), "0x09");
            GUI.TextField(new Rect(70, 200, 60, 20), device_9.w.ToString());
            GUI.TextField(new Rect(130, 200, 60, 20), device_9.x.ToString());
            GUI.TextField(new Rect(190, 200, 60, 20), device_9.y.ToString());
            GUI.TextField(new Rect(250, 200, 60, 20), device_9.z.ToString());
            GUI.TextField(new Rect(310, 200, 60, 20), yaw_9.ToString());
            GUI.TextField(new Rect(370, 200, 60, 20), pitch_9.ToString());
            GUI.TextField(new Rect(430, 200, 60, 20), roll_9.ToString());


        }

        if (windowID == 10)
        {
            if (GUI.Button(new Rect(30, 70, 80, 40), state_button))
            {
                flag++;
                if (flag % 2 == 1)
                {
                    state_button = "turn on";
                }
                else
                {
                    state_button = "turn off";
                }
                getPortName = "COM5";
                baudRate = set;

                OpenPort(getPortName, baudRate);
                StartCoroutine(DataReceiveFunction());


            }

            GUI.Label(new Rect(20, 20, 100, 20), "serial");
            GUI.TextField(new Rect(60, 20, 100, 20), "COM5");
            GUI.Label(new Rect(20, 40, 100, 20), "baud");
            bote = GUI.TextField(new Rect(60, 40, 100, 20), bote, 10);
            if (bote == "115200")
            {
                set = 115200;
            }
            else if (bote == "9600")
            {
                set = 9600;
            }
            else if (bote == "4800")
            {
                set = 4800;
            }

        }




    }
    // Start is called before the first frame update
    void Start()
    {
        Screen.fullScreen = false;  //退出全屏     
        obj = GameObject.Find("Bip01 L UpperArm");
        obj_1 = GameObject.Find("Bip01 L Forearm");
        obj_3 = GameObject.Find("Bip01 R UpperArm");
        obj_4 = GameObject.Find("Bip01 R Forearm");
        obj_5 = GameObject.Find("Bip01 Spine2");
        obj_6 = GameObject.Find("Bip01 L Thigh");
        obj_7 = GameObject.Find("Bip01 L Calf");
        obj_8 = GameObject.Find("Bip01 R Thigh");
        obj_9 = GameObject.Find("Bip01 R Calf");
        File.WriteAllText(txtPath_1, "");
        File.WriteAllText(txtPath_2, "");





    }


    IEnumerator DataReceiveFunction()
    {

        while (true)
        {
            if (sp != null && sp.IsOpen)
            {
                try
                {
                    RecAndProcessingFunction();
                }
                catch (Exception ex)
                {
                    Debug.LogError(ex);
                }
            }
            yield return new WaitForSeconds(Time.deltaTime);
        }
    }
    public Quaternion siyuan(float q0, float q1, float q2, float q3)
    {
        Quaternion q;
        q.w = q0;
        q.x = q1;
        q.y = q2;
        q.z = q3;

        return q;
    }
    public Quaternion quater_cha(Quaternion q0, Quaternion q1)
    {
        Quaternion q;
        q.w = q0.w - q1.w;
        q.x = q0.x - q1.x;
        q.y = q0.y - q1.y;
        q.z = q0.z - q1.z;
        return q;
    }
    public Quaternion quater_add(Quaternion q0, Quaternion q1)
    {
        Quaternion q;
        q.w = q0.w + q1.w;
        q.x = q0.x + q1.x;
        q.y = q0.y + q1.y;
        q.z = q0.z + q1.z;
        return q;
    }
    Quaternion quat_inv(Quaternion q)
    {
        float a = 1.0f / (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return new Quaternion(a * q.w, a * -q.x, a * -q.y, a * -q.z);

    }
    Quaternion ConvertSensorToUnreal_LHand(float x, float y, float z, float w)  //左小臂和左大臂
    {

        Quaternion output;
        output.x = y;    //上下
        output.y = -z;  //前后
        output.z = -x;   // 旋转角
        output.w = w;

        return output;
    }
    Quaternion ConvertSensorToUnreal_RHand(float x, float y, float z, float w)  //右大臂和右小臂
    {

        Quaternion output;
        output.x = x;    //上下
        output.y = -z;    //前后
        output.z = y;    // 旋转角
        output.w = w;
        //output.x = -input.x;
        //output.y = input.z;
        //output.z = -input.y;
        //output.w = input.w;

        return output;
    }

    Quaternion ConvertSensorToUnreal_Rib(float x, float y, float z, float w)  //腰部
    {
        Quaternion output;
        output.x = -y;
        output.y = -z;
        output.z = -x;
        output.w = -w;
        //output.x = -x;
        //output.y = z;
        //output.z = -y;
        //output.w = w;

        return output;

    }

    Quaternion ConvertSensorToUnreal_LL(float x, float y, float z, float w) //左腿
    {
        Quaternion output;
        output.x = y;
        output.y = x;
        output.z = -z;
        output.w = w;

        return output;

    }
    Quaternion ConvertSensorToUnreal_RL(float x, float y, float z, float w) //右腿
    {
        Quaternion output;
        output.x = x;
        output.y = -y;
        output.z = z;
        output.w = w;

        return output;

    }
    void Update()
    {

        device_1_now = ConvertSensorToUnreal_LHand(device_1.x, device_1.y, device_1.z, device_1.w);
        device_2_now = ConvertSensorToUnreal_LHand(device_2.x, device_2.y, device_2.z, device_2.w);
        device_3_now = ConvertSensorToUnreal_RHand(device_3.x, device_3.y, device_3.z, device_3.w);
        device_4_now = ConvertSensorToUnreal_RHand(device_4.x, device_4.y, device_4.z, device_4.w);
        device_5_now = ConvertSensorToUnreal_Rib(device_5.x, device_5.y, device_5.z, device_5.w);
        device_6_now = ConvertSensorToUnreal_LL(device_6.x, device_6.y, device_6.z, device_6.w);
        device_7_now = ConvertSensorToUnreal_LL(device_7.x, device_7.y, device_7.z, device_7.w);
        device_8_now = ConvertSensorToUnreal_RL(device_8.x, device_8.y, device_8.z, device_8.w);
        device_9_now = ConvertSensorToUnreal_RL(device_9.x, device_9.y, device_9.z, device_9.w);
        if (count > 100)
        {
            if (quation_flag == 0)
            {

                cha = Quaternion.Inverse(device_1_now) * obj.transform.localRotation;
                result = device_1_now * cha;

                cha_2 = Quaternion.Inverse(device_2_now) * obj_1.transform.localRotation;
                result_2 = device_2_now * cha_2;

                cha_3 = Quaternion.Inverse(device_3_now) * obj_3.transform.localRotation;
                result_3 = device_3_now * cha_3;
                Debug.Log(" cha_3 " + cha_3);
                Debug.Log("Quaternion.Inverse(device_3_now)" + Quaternion.Inverse(device_3_now));
                Debug.Log("初始状态" + obj_3.transform.localRotation);
                Debug.Log(" 结果 " + result_3);



                cha_4 = Quaternion.Inverse(device_4_now) * obj_4.transform.localRotation;
                result_4 = device_4_now * cha_4;



                //初步公式偏差关系
                //obj_5.transform.localRotation=device_5_now*cha_5
                //cha_5=(device_5_now^-1)*obj_5.transform.localRotation
                cha_5 = Quaternion.Inverse(device_5_now) * obj_5.transform.localRotation;
                result_5 = device_5_now * cha_5;

                cha_6 = Quaternion.Inverse(device_6_now) * obj_6.transform.localRotation;
                result_6 = device_6_now * cha_6;


                cha_7 = Quaternion.Inverse(device_7_now) * obj_7.transform.localRotation;
                result_7 = device_7_now * cha_7;

                cha_8 = Quaternion.Inverse(device_8_now) * obj_8.transform.localRotation;
                result_8 = device_8_now * cha_8;

                cha_9 = Quaternion.Inverse(device_9_now) * obj_9.transform.localRotation;
                result_9 = device_9_now * cha_9;
                //result_5 = cha_5 * device_5_now; //结果值=拟合差*当前传感器四元数
                //result_5 = Quaternion.Inverse(result_5);//结果差求逆，从这里应该得到和obj_5.transform.localRotation相同的四元数




                quation_flag = 1;



            }
            else
            {
                result = device_1_now * cha;
                result_2 = device_2_now * cha_2;
                result_3 = device_3_now * cha_3;
                result_4 = device_4_now * cha_4;
                result_5 = device_5_now * cha_5;
                result_6 = device_6_now * cha_6;
                result_7 = device_7_now * cha_7;

                result_8 = device_8_now * cha_8;
                result_9 = device_9_now * cha_9;

                //result_3 = Quaternion.Inverse(result_3);
                //result_4 = Quaternion.Inverse(result_4);
                result_5 = Quaternion.Inverse(result_5);

                result_6 = Quaternion.Inverse(result_6);
                result_7 = Quaternion.Inverse(result_7);

                result_8 = Quaternion.Inverse(result_8);
                result_9 = Quaternion.Inverse(result_9);







                Vector3 eulerAngle = result_5.eulerAngles;

                Debug.Log("当前" + result_3);
                Debug.Log("组件2" + obj_3.transform.localRotation);


            }
            //obj_1.transform.Rotate(0, 0, roll_cha);   //z y x
            //obj.transform.Rotate(0,0, roll_cha_2);

            obj.transform.rotation = result;
            obj_1.transform.rotation = result_2;
            obj_3.transform.localRotation = result_3;
            obj_4.transform.localRotation = result_4;
            obj_5.transform.localRotation = result_5;
            obj_6.transform.localRotation = result_6;
            obj_7.transform.localRotation = result_7;
            obj_8.transform.localRotation = result_8;
            obj_9.transform.localRotation = result_9;



            //obj_3.transform.localRotation = Quaternion.Slerp(transform.rotation, device_3, 2f);
            //obj_4.transform.localRotation = Quaternion.Slerp(transform.rotation, device_4, 2f);
            //obj_5.transform.localRotation = Quaternion.Slerp(transform.rotation, device_5, 2f);
            //obj_6.transform.localRotation = Quaternion.Slerp(transform.rotation, device_6, 2f);
            //obj_7.transform.localRotation = Quaternion.Slerp(transform.rotation, device_7, 2f);
            //obj_8.transform.localRotation = Quaternion.Slerp(transform.rotation, device_8, 2f);
            //obj_9.transform.localRotation = Quaternion.Slerp(transform.rotation, device_9, 2f);



        }

        //  obj_1.transform.localRotation = Quaternion.Slerp(transform.rotation=Quaternion.identity, Quaternion.Euler(40, 0, 0), 2f);
        //obj_1.transform.eulerAngles = new Vector3(30,0,0);
        //yaw_last = yaw_now;
        //pitch_last = pitch_now;
        //roll_last = roll_now;
        //yaw_last_2 = yaw_now_2;
        //pitch_last_2 = pitch_now_2;
        //roll_last_2 = roll_now_2;
        device_1_last = device_1_now;

        //obj.transform.localRotation = Quaternion.Lerp(transform.rotation, newQuaternion.Set(0.2f,0.3f,0.4f,0.5f), 2f);
        //obj.transform.localRotation = Quaternion.Lerp(transform.rotation, Quaternion.Euler(30, 40, 50), 2f);




    }

    /// <summary>
    /// 读取并处理消息
    /// </summary>
    private void RecAndProcessingFunction()
    {
        //待读字节个数
        int n = sp.BytesToRead;
        //创建n个字节的缓存
        byte[] buf = new byte[n];
        //读到在数据存储到buf
        sp.Read(buf, 0, n);
        //1.缓存数据 不断地将接收到的数据加入到buffer链表中
        bufferList.AddRange(buf);
        //2.完整性判断 至少包含帧头（1字节）、类型（1字节）、功能位（22字节） 根据设计不同而不同
        while (bufferList.Count >= 2)
        {

            //2.1 查找数据头 根据帧头和类型
            if (bufferList[0] == 0xAA && bufferList[1] == 0x44)
            {
                if (bufferList[2] == 0x01)
                {
                    state = 1;
                }
                if (bufferList[2] == 0x02)
                {
                    state = 2;
                }
                if (bufferList[2] == 0x03)
                {
                    state = 3;
                }
                if (bufferList[2] == 0x04)
                {
                    state = 4;
                }
                if (bufferList[2] == 0x05)
                {
                    state = 5;
                }
                if (bufferList[2] == 0x06)
                {
                    state = 6;
                }
                if (bufferList[2] == 0x07)
                {
                    state = 7;
                }
                if (bufferList[2] == 0x08)
                {
                    state = 8;
                }
                if (bufferList[2] == 0x09)
                {
                    state = 9;
                }

                int len = bufferList[3];//数据长度
                //如果小于则说明数据区尚未接收完整，
                if (bufferList.Count < len + 5)
                {
                    //跳出接收函数后之后继续接收数据
                    break;
                }
                byte checksum = 0;
                for (int i = 0; i < len + 4; i++)//len+3表示校验之前的位置
                {
                    checksum ^= bufferList[i];
                }
                if (checksum != bufferList[len + 4]) //如果数据校验失败，丢弃这一包数据
                {
                    bufferList.RemoveRange(0, len + 5);//从缓存中删除错误数据
                    continue;//继续下一次循环
                }
                //得到一帧完整的数据，进行处理，在此之前可以使用校验位保证此帧数据完整性
                byte[] processingByteArray = new byte[len + 5];
                //从缓存池中拷贝到处理数组
                bufferList.CopyTo(0, processingByteArray, 0, len + 5);
                //处理一帧数据
                DataProcessingFunction(processingByteArray);
                //从缓存池移除处理完的这帧
                bufferList.RemoveRange(0, len + 5);
            }
            else
            {
                //帧头不正确时，清除第一个字节，继续检测下一个。
                bufferList.RemoveAt(0);
                state = 0;
            }
        }
    }
    /// <summary>
    /// 数据处理
    /// </summary>
    private void DataProcessingFunction(byte[] dataBytes)
    {
        count++;
        //对拆分后的4个字节进行重组，模拟接收到hex后的数据还原过程
        byte[] byteTemp0 = new byte[4];
        byte[] byteTemp1 = new byte[4];
        byte[] byteTemp2 = new byte[4];
        byte[] byteTemp3 = new byte[4];
        if (dataBytes != null)
        {

            byteTemp0[0] = dataBytes[4];
            byteTemp0[1] = dataBytes[5];
            byteTemp0[2] = dataBytes[6];
            byteTemp0[3] = dataBytes[7];
            //
            byteTemp1[0] = dataBytes[8];
            byteTemp1[1] = dataBytes[9];
            byteTemp1[2] = dataBytes[10];
            byteTemp1[3] = dataBytes[11];
            //
            byteTemp2[0] = dataBytes[12];
            byteTemp2[1] = dataBytes[13];
            byteTemp2[2] = dataBytes[14];
            byteTemp2[3] = dataBytes[15];
            //
            byteTemp3[0] = dataBytes[16];
            byteTemp3[1] = dataBytes[17];
            byteTemp3[2] = dataBytes[18];
            byteTemp3[3] = dataBytes[19];
            q0 = BitConverter.ToSingle(byteTemp0, 0);
            q1 = BitConverter.ToSingle(byteTemp1, 0);
            q2 = BitConverter.ToSingle(byteTemp2, 0);
            q3 = BitConverter.ToSingle(byteTemp3, 0);

            if (state == 1)
            {
                device_1 = siyuan(q0, q1, q2, q3);
                eulerAngle_1 = device_1.eulerAngles;
                yaw = eulerAngle_1.z;
                pitch = eulerAngle_1.y;
                roll = eulerAngle_1.x;
                File.AppendAllText(txtPath_1, yaw.ToString() + " " + pitch.ToString() + " " + roll.ToString() + "\n");
            }

            if (state == 2)
            {
                device_2 = siyuan(q0, q1, q2, q3);
                eulerAngle_2 = device_2.eulerAngles;
                yaw_1 = eulerAngle_2.z;
                pitch_1 = eulerAngle_2.y;
                roll_1 = eulerAngle_2.x;
                File.AppendAllText(txtPath_2, yaw_1.ToString() + " " + pitch_1.ToString() + " " + roll_1.ToString() + "\n");
            }
            if (state == 3)
            {
                device_3 = siyuan(q0, q1, q2, q3);
                eulerAngle_3 = device_3.eulerAngles;
                yaw_3 = eulerAngle_3.z;
                pitch_3 = eulerAngle_3.y;
                roll_3 = eulerAngle_3.x;
                //File.AppendAllText(txtPath_2, yaw_3.ToString() + " " + pitch_3.ToString() + " " + roll_3.ToString() + "\n");
            }
            if (state == 4)
            {
                device_4 = siyuan(q0, q1, q2, q3);
                eulerAngle_4 = device_4.eulerAngles;
                yaw_4 = eulerAngle_4.z;
                pitch_4 = eulerAngle_4.y;
                roll_4 = eulerAngle_4.x;
                //File.AppendAllText(txtPath_2, yaw_4.ToString() + " " + pitch_4.ToString() + " " + roll_4.ToString() + "\n");
            }
            if (state == 5)
            {
                device_5 = siyuan(q0, q1, q2, q3);
                eulerAngle_5 = device_5.eulerAngles;
                yaw_5 = eulerAngle_5.z;
                pitch_5 = eulerAngle_5.y;
                roll_5 = eulerAngle_5.x;
                //File.AppendAllText(txtPath_2, yaw_55.ToString() + " " + pitch_4.ToString() + " " + roll_4.ToString() + "\n");
            }
            if (state == 6)
            {
                device_6 = siyuan(q0, q1, q2, q3);
                eulerAngle_6 = device_6.eulerAngles;
                yaw_6 = eulerAngle_6.z;
                pitch_6 = eulerAngle_6.y;
                roll_6 = eulerAngle_6.x;
                //File.AppendAllText(txtPath_2, yaw_4.ToString() + " " + pitch_4.ToString() + " " + roll_4.ToString() + "\n");
            }
            if (state == 7)
            {
                device_7 = siyuan(q0, q1, q2, q3);
                eulerAngle_7 = device_7.eulerAngles;
                yaw_7 = eulerAngle_7.z;
                pitch_7 = eulerAngle_7.y;
                roll_7 = eulerAngle_7.x;
                //File.AppendAllText(txtPath_2, yaw_4.ToString() + " " + pitch_4.ToString() + " " + roll_4.ToString() + "\n");
            }
            if (state == 8)
            {
                device_8 = siyuan(q0, q1, q2, q3);
                eulerAngle_8 = device_8.eulerAngles;
                yaw_8 = eulerAngle_8.z;
                pitch_8 = eulerAngle_8.y;
                roll_8 = eulerAngle_8.x;
                //File.AppendAllText(txtPath_2, yaw_4.ToString() + " " + pitch_4.ToString() + " " + roll_4.ToString() + "\n");
            }
            if (state == 9)
            {
                device_9 = siyuan(q0, q1, q2, q3);
                eulerAngle_9 = device_9.eulerAngles;
                yaw_9 = eulerAngle_9.z;
                pitch_9 = eulerAngle_9.y;
                roll_9 = eulerAngle_9.x;
                //File.AppendAllText(txtPath_2, yaw_4.ToString() + " " + pitch_4.ToString() + " " + roll_4.ToString() + "\n");
            }




        }


    }

    /// <summary>
    /// 字节数组转16进制字符串
    /// </summary>
    /// <param name="bytes"></param>
    /// <returns></returns>
    public static string byteToHexStr(byte[] bytes)
    {
        string returnStr = "";
        if (bytes != null)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                returnStr += bytes[i].ToString("X2");
                returnStr += "-";
            }
        }
        return returnStr;
    }

    #region 串口开启关闭相关
    //打开串口
    public void OpenPort(string DefaultPortName, int DefaultBaudRate)
    {
        sp = new SerialPort(DefaultPortName, DefaultBaudRate, parity, dataBits, stopBits);
        sp.ReadTimeout = 10;
        try
        {
            if (!sp.IsOpen)
            {
                sp.Open();
            }
        }

        catch (Exception ex)
        {
            Debug.Log(ex.Message);
        }
    }

    //关闭串口
    public void ClosePort()
    {
        try
        {
            sp.Close();
        }
        catch (Exception ex)
        {
            Debug.Log(ex.Message);
        }
    }
    #endregion

    #region Unity
    private void OnApplicationQuit()
    {
        ClosePort();
    }
    private void OnDisable()
    {
        ClosePort();
    }
    #endregion
}


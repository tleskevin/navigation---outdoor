using UnityEngine;
using System.Collections;

public class GpsTracker : MonoBehaviour // 更改類別名稱，更符合職責
{
    public float CurrentLatitude { get; private set; }
    public float CurrentLongitude { get; private set; }

    // 【新增】儲存首次定位的原點
    public double ReferenceLatitude { get; private set; }
    public double ReferenceLongitude { get; private set; }

    // 追蹤是否已設定參考點
    private bool isReferenceSet = false;

    IEnumerator Start()
    {
        Debug.Log("正在啟動定位服務...");

      

        // 1. 啟動定位服務
        if (!Input.location.isEnabledByUser) { Debug.LogError("Location services not enabled."); yield break; }

        // 設置定位精度
        Input.location.Start(1f, 1f); // 啟動服務 (精度1米，最小移動距離1米)

        int maxWait = 20;
        while (Input.location.status == LocationServiceStatus.Initializing && maxWait > 0)
        {
            yield return new WaitForSeconds(1);
            maxWait--;
        }

        if (maxWait < 1 || Input.location.status == LocationServiceStatus.Failed)
        {
            Debug.LogError("定位服務初始化失敗或超時。");
            yield break;
        }

        Debug.Log("定位服務啟動成功。");
    }

    void Update()
    {
        if (Input.location.status == LocationServiceStatus.Running)
        {
            LocationInfo data = Input.location.lastData;
            CurrentLatitude = data.latitude;
            CurrentLongitude = data.longitude;

            // 【新增】在第一次成功定位時，設定參考原點
            if (!isReferenceSet)
            {
                ReferenceLatitude = CurrentLatitude;
                ReferenceLongitude = CurrentLongitude;
                isReferenceSet = true;
                Debug.Log($"GPS 參考原點已設定: ({ReferenceLatitude}, {ReferenceLongitude})");
            }

            
        }
    }
}
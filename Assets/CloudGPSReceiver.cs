using UnityEngine;
using UnityEngine.Networking;
using System.Collections;

public class CloudGPSReceiver : MonoBehaviour
{
    [Header("Firebase 設定")]
    public string firebaseURL = "https://unitygpstracker-default-rtdb.asia-southeast1.firebasedatabase.app/gps.json";
    public float pollInterval = 0.5f;

    [Header("關聯腳本")]
    public MapDataLoader mapDataLoader;

    [System.Serializable]
    public class GpsData
    {
        public double lat;
        public double lng;
    }

    void Start()
    {
        if (mapDataLoader == null) mapDataLoader = GetComponent<MapDataLoader>();
        StartCoroutine(ContinuousGetCloudData());
    }

    IEnumerator ContinuousGetCloudData()
    {
        while (true)
        {
            using (UnityWebRequest webRequest = UnityWebRequest.Get(firebaseURL))
            {
                yield return webRequest.SendWebRequest();
                if (webRequest.result == UnityWebRequest.Result.Success)
                {
                    string json = webRequest.downloadHandler.text;
                    if (json != "null" && json != "{}")
                    {
                        try
                        {
                            GpsData data = JsonUtility.FromJson<GpsData>(json);
                            // 修正 CS1501 報錯：直接傳入 double 數值
                            mapDataLoader.OnReceiveGpsData(data.lat, data.lng);
                            Debug.Log($"<color=white>【雲端同步】</color> Lat: {data.lat}, Lng: {data.lng}");
                        }
                        catch { }
                    }
                }
            }
            yield return new WaitForSeconds(pollInterval);
        }
    }
}
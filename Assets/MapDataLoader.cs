using UnityEngine;
using System.Collections;
using UnityEngine.Networking;
using TMPro;
using System;

public class MapDataLoader : MonoBehaviour
{
    [Header("References")]
    public VoiceNavigationHandler voiceHandler;
    public TextMeshProUGUI debugOutputText;
    public AudioSource audioSource;
    public AudioClip arrivalSfx;

    [Header("API Keys")]
    public string GoogleApiKey;
    [HideInInspector] public string DestinationAddress;

    private double currentLat, currentLng;
    private double snappedLat, snappedLng;
    private double targetLat, targetLng;

    private double lastApiLat, lastApiLng;
    private double walkingDistanceValue = 0;
    private string durationText = "計算中...";
    private string distanceText = "計算中...";

    private bool isNavigating = false;
    private bool isFinalSprint = false;
    private string systemStatus = "等待指令";

    public void StartNavigationTo(string newAddress)
    {
        DestinationAddress = newAddress;
        isNavigating = false;
        isFinalSprint = false;
        walkingDistanceValue = 0;
        durationText = "重新計算...";
        distanceText = "重新計算...";
        StopAllCoroutines();
        StartCoroutine(StartNavigationFlow());
    }

    IEnumerator Start()
    {
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        yield return StartCoroutine(InitializeGPS());
    }

    private IEnumerator InitializeGPS()
    {
        systemStatus = "📍 初始化 GPS...";
        if (!Input.location.isEnabledByUser) { systemStatus = "❌ GPS 未授權"; yield break; }

        Input.location.Start(1f, 1f);
        int maxWait = 20;
        while (Input.location.status == LocationServiceStatus.Initializing && maxWait-- > 0) yield return new WaitForSeconds(1);

        if (Input.location.status != LocationServiceStatus.Running) systemStatus = "❌ GPS 啟動失敗";
        else systemStatus = "✅ GPS 已就緒";
    }

    private IEnumerator StartNavigationFlow()
    {
        currentLat = Input.location.lastData.latitude;
        currentLng = Input.location.lastData.longitude;
        if (currentLat == 0) { LogToUI("❌ 錯誤：尚未取得有效 GPS"); yield break; }

        yield return StartCoroutine(SnapLocationToRoad(currentLat, currentLng));
        yield return StartCoroutine(UpdateShortestWalkingRoute());

        if (isNavigating) StartCoroutine(SmartTrackingLoop());
    }

    private IEnumerator SnapLocationToRoad(double lat, double lng)
    {
        systemStatus = "🛣️ 道路貼合中...";
        string url = $"https://roads.googleapis.com/v1/snapToRoads?path={lat},{lng}&key={GoogleApiKey}";
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success && req.downloadHandler.text.Contains("latitude"))
            {
                string json = req.downloadHandler.text;
                snappedLat = ParseJsonDouble(json, "latitude");
                snappedLng = ParseJsonDouble(json, "longitude");
                systemStatus = "✅ 道路貼合完成";
            }
            else
            {
                snappedLat = lat; snappedLng = lng;
                systemStatus = "⚠️ 使用原始 GPS";
            }
        }
    }

    private IEnumerator UpdateShortestWalkingRoute()
    {
        systemStatus = "📡 同步 Google 路線...";
        string url = $"https://maps.googleapis.com/maps/api/directions/json" +
                     $"?origin={snappedLat},{snappedLng}" +
                     $"&destination={UnityWebRequest.EscapeURL(DestinationAddress)}" +
                     $"&mode=walking&alternatives=true&key={GoogleApiKey}";

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) { systemStatus = "❌ 網路失敗"; yield break; }

            string json = req.downloadHandler.text;
            if (!json.Contains("\"status\" : \"OK\"")) { systemStatus = "❌ 查無路徑"; yield break; }

            FindShortestPathInfo(json);

            lastApiLat = Input.location.lastData.latitude;
            lastApiLng = Input.location.lastData.longitude;

            isNavigating = (walkingDistanceValue > 0);
            systemStatus = isNavigating ? "🚀 導航中" : "❌ 解析失敗";

            UpdateUI(walkingDistanceValue);
        }
    }

    private void FindShortestPathInfo(string json)
    {
        int legsIdx = json.IndexOf("\"legs\"");
        if (legsIdx == -1) return;

        int totalDistIdx = json.IndexOf("\"distance\"", legsIdx);
        if (totalDistIdx != -1)
        {
            walkingDistanceValue = ParseJsonDouble(json.Substring(totalDistIdx), "value");
            distanceText = ExtractValue(json.Substring(totalDistIdx), "text");

            int durationIdx = json.IndexOf("\"duration\"", legsIdx);
            durationText = ExtractValue(json.Substring(durationIdx), "text");

            int endLocIdx = json.IndexOf("\"end_location\"", legsIdx);
            targetLat = ParseJsonDouble(json.Substring(endLocIdx), "lat");
            targetLng = ParseJsonDouble(json.Substring(endLocIdx), "lng");

            isNavigating = true;
        }
    }

    private IEnumerator SmartTrackingLoop()
    {
        while (isNavigating)
        {
            double nowLat = Input.location.lastData.latitude;
            double nowLng = Input.location.lastData.longitude;

            double movedSinceLastApi = CalculateDistance(nowLat, nowLng, lastApiLat, lastApiLng);
            double straightToTarget = CalculateDistance(nowLat, nowLng, targetLat, targetLng);

            if (straightToTarget <= 15.0)
            {
                isFinalSprint = true;
                if (straightToTarget <= 8.0)
                {
                    isNavigating = false;
                    TriggerArrival();
                    yield break;
                }
            }
            else if (movedSinceLastApi >= 20.0 && !isFinalSprint)
            {
                yield return StartCoroutine(SnapLocationToRoad(nowLat, nowLng));
                yield return StartCoroutine(UpdateShortestWalkingRoute());
            }

            double currentDisplay = isFinalSprint ? straightToTarget : walkingDistanceValue - movedSinceLastApi;
            if (currentDisplay < 0) currentDisplay = 0;

            UpdateUI(currentDisplay);
            HandleHaptics(currentDisplay);

            yield return new WaitForSeconds(1.0f);
        }
    }

    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        double R = 6371000;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) +
                   Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                   Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
        return R * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
    }

    // --- 修改後的 UI 顯示：同步目標後方的距離 ---
    private void UpdateUI(double currentRemaining)
    {
        if (debugOutputText == null) return;

        string color = isFinalSprint ? "red" : "green";
        debugOutputText.text =
            $"🏁 目標：<b>{DestinationAddress} ({distanceText})</b>\n\n" +
            $"[ <color={color}>{systemStatus}</color> ]\n" +
            $"路徑總長：{distanceText} ({Math.Round(walkingDistanceValue)}m)\n" +
            $"預計時間：{durationText}\n" +
            "--------------------\n" +
            $"<size=120%>🔔 剩餘：<color=yellow>{Math.Round(currentRemaining)}</color> 公尺</size>";
    }

    private double ParseJsonDouble(string json, string key)
    {
        int idx = json.IndexOf($"\"{key}\"");
        if (idx < 0) return 0;
        int colon = json.IndexOf(":", idx);
        int start = colon + 1;
        while (start < json.Length && (json[start] == ' ' || json[start] == '"')) start++;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-')) end++;
        if (double.TryParse(json.Substring(start, end - start), out double r)) return r;
        return 0;
    }

    private string ExtractValue(string json, string key)
    {
        int idx = json.IndexOf($"\"{key}\"");
        if (idx < 0) return "--";
        int colon = json.IndexOf(":", idx);
        int q1 = json.IndexOf("\"", colon + 1);
        int q2 = json.IndexOf("\"", q1 + 1);
        if (q1 < 0 || q2 < 0) return "--";
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    private void HandleHaptics(double d)
    {
        if (d <= 30 && d > 8) OVRInput.SetControllerVibration(0.3f, 0.4f, OVRInput.Controller.RTouch);
        else OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
    }

    private void TriggerArrival()
    {
        if (arrivalSfx != null) audioSource.PlayOneShot(arrivalSfx);
        voiceHandler.Speak("抵達目的地附近。");
    }

    private void LogToUI(string msg) { if (debugOutputText != null) debugOutputText.text = msg; }
}
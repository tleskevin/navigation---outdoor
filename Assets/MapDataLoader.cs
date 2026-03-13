using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using TMPro;
using System;
using System.Text.RegularExpressions;

[Serializable]
public class NavStep
{
    public string instruction;
    public double startLat, startLng;
    public bool isReported = false;
    public bool isCompleted = false;
}

public class MapDataLoader : MonoBehaviour
{
    [Header("UI 顯示元件")]
    public TextMeshProUGUI statusOutputText;      // 導航主面板 (使用您要求的格式)
    public TextMeshProUGUI coordinatesOutputText; // 定位數據面板
    public TextMeshProUGUI debugOutputText;       // 除錯面板

    [Header("References")]
    public VoiceNavigationHandler voiceHandler;
    public AudioSource audioSource;
    public AudioClip arrivalSfx;

    [Header("API Keys")]
    public string GoogleApiKey = "YOUR_GOOGLE_KEY";

    [Header("空間對齊設定")]
    public Transform navContainer;

    [HideInInspector] public double currentLat, currentLng;
    private double snappedLat, snappedLng, targetLat, targetLng;
    private double routeOriginLat, routeOriginLng;

    private string destinationName = "無", destinationId, durationText = "--", systemStatus = "等待指令";
    private List<NavStep> routeSteps = new List<NavStep>();
    private float currentMovingBearing = 0f;
    private bool isNavigating = false, isFinalSprint = false;
    private double totalWalkingDistance = 0, currentRemainingDistance = 0;
    private float nextReportTime = 0f;
    private const float REPORT_INTERVAL = 15f;

    private string internalDebugInfo = "";

    public void OnReceiveGpsData(double lat, double lng)
    {
        if (currentLat != 0 && (currentLat != lat || currentLng != lng))
        {
            currentMovingBearing = (float)CalculateBearing(currentLat, currentLng, lat, lng);
            AlignVirtualNorth(currentMovingBearing);
        }
        currentLat = lat; currentLng = lng;

        if (isNavigating) UpdateNavigationLogic();
        else
        {
            if (lat != 0) systemStatus = "✅ 定位已連接";
            UpdateNavigationUI(0);
        }
        UpdateCoordinatesUI();
    }

    private void UpdateNavigationLogic()
    {
        double movedSinceStart = CalculateDistance(currentLat, currentLng, routeOriginLat, routeOriginLng);
        currentRemainingDistance = Math.Max(0, totalWalkingDistance - movedSinceStart);
        double straightToFinal = CalculateDistance(currentLat, currentLng, targetLat, targetLng);

        CheckStepGuidance();

        if (straightToFinal <= 15.0)
        {
            isFinalSprint = true;
            if (straightToFinal <= 6.0) { TriggerArrival(); return; }
        }

        if (Time.time >= nextReportTime) { DoVoiceReport(); nextReportTime = Time.time + REPORT_INTERVAL; }
        UpdateNavigationUI(currentRemainingDistance);
    }

    private void CheckStepGuidance()
    {
        for (int i = 0; i < routeSteps.Count; i++)
        {
            var step = routeSteps[i];
            if (!step.isCompleted)
            {
                double distToStep = CalculateDistance(currentLat, currentLng, step.startLat, step.startLng);
                if (!step.isReported && distToStep <= 25.0)
                {
                    voiceHandler.Speak($"前方 20 公尺，{step.instruction}");
                    step.isReported = true;
                }
                if (distToStep < 8.0) step.isCompleted = true;
                break;
            }
        }
    }

    private void UpdateNavigationUI(double remaining)
    {
        if (statusOutputText == null) return;

        string color = isFinalSprint ? "#FF4444" : "#00FF00";

        // 若尚未導航，顯示簡約格式
        if (!isNavigating)
        {
            statusOutputText.text =
                $"<b>系統狀態：</b> [ <color={color}>{systemStatus}</color> ]\n" +
                $"🏁 目標：<b>{destinationName}</b>\n" +
                $"🔔 總剩餘：<color=yellow>{Math.Round(remaining)}</color> 公尺 ({durationText})";
            return;
        }

        // 導航中，計算「接下來路線」內容
        string nextRouteDisplay = "";
        bool foundActive = false;

        foreach (var step in routeSteps)
        {
            if (!step.isCompleted)
            {
                double dist = CalculateDistance(currentLat, currentLng, step.startLat, step.startLng);
                nextRouteDisplay = $"直走 <color=#00FFFF>{Math.Round(dist)}</color> 公尺\n{step.instruction}";
                foundActive = true;
                break;
            }
        }

        if (!foundActive)
        {
            double distToFinal = CalculateDistance(currentLat, currentLng, targetLat, targetLng);
            nextRouteDisplay = $"再向前 <color=#00FFFF>{Math.Round(distToFinal)}</color> 公尺抵達目的地";
        }

        // --- 使用您指定的合併格式 ---
        statusOutputText.text =
            $"<b>系統狀態：</b> [ <color={color}>{systemStatus}</color> ]\n" +
            "--------------------\n" +
            $"🏁 目標：<b>{destinationName}</b>\n" +
            $"<b>接下來路線：</b>\n{nextRouteDisplay}\n" +
            "--------------------\n" +
            $"🔔 剩餘：<color=yellow>{Math.Round(remaining)}</color> 公尺 ({durationText})";

        // 更新除錯面板
        if (debugOutputText != null)
        {
            debugOutputText.text = $"<color=orange>[DEBUG]</color>\n{internalDebugInfo}\n當前路口: {GetActiveStepIndex() + 1}/{routeSteps.Count}";
        }
    }

    private int GetActiveStepIndex()
    {
        for (int i = 0; i < routeSteps.Count; i++) if (!routeSteps[i].isCompleted) return i;
        return routeSteps.Count - 1;
    }

    private void ParseDirections(string json)
    {
        internalDebugInfo = "解析指令中...";
        int legsIdx = json.IndexOf("\"legs\"");
        totalWalkingDistance = ParseJsonDouble(json.Substring(json.IndexOf("\"distance\"", legsIdx)), "value");
        durationText = ExtractValue(json.Substring(json.IndexOf("\"duration\"", legsIdx)), "text");
        int endIdx = json.IndexOf("\"end_location\"", legsIdx);
        targetLat = ParseJsonDouble(json.Substring(endIdx), "lat");
        targetLng = ParseJsonDouble(json.Substring(endIdx), "lng");

        routeOriginLat = currentLat;
        routeOriginLng = currentLng;

        routeSteps.Clear();
        // 正則暴力提取：針對轉彎步驟
        MatchCollection instructions = Regex.Matches(json, "\"html_instructions\"\\s*:\\s*\"(.*?)\"");
        MatchCollection lats = Regex.Matches(json, "\"start_location\"\\s*:\\s*\\{\\s*\"lat\"\\s*:\\s*([-+]?[0-9]*\\.?[0-9]+)");
        MatchCollection lngs = Regex.Matches(json, "\"lng\"\\s*:\\s*([-+]?[0-9]*\\.?[0-9]+)");

        for (int i = 0; i < instructions.Count; i++)
        {
            NavStep s = new NavStep();
            string raw = instructions[i].Groups[1].Value;
            s.instruction = Regex.Replace(raw, "<.*?>", string.Empty);
            // 處理 Unicode 與 HTML 殘留
            s.instruction = s.instruction.Replace("\\u003cb\\u003e", "").Replace("\\u003c/b\\u003e", "").Replace("\"", "");

            if (i < lats.Count)
            {
                double.TryParse(lats[i].Groups[1].Value, out s.startLat);
                double.TryParse(lngs[i].Groups[1].Value, out s.startLng);
            }
            routeSteps.Add(s);
        }
        internalDebugInfo = $"✅ 已成功提取 {routeSteps.Count} 個路口資訊";
    }

    // --- 固定工具函式 (數學與 API) ---

    private double CalculateBearing(double lat1, double lon1, double lat2, double lon2)
    {
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double rLat1 = lat1 * Math.PI / 180.0; double rLat2 = lat2 * Math.PI / 180.0;
        double y = Math.Sin(dLon) * Math.Cos(rLat2);
        double x = Math.Cos(rLat1) * Math.Sin(rLat2) - Math.Sin(rLat1) * Math.Cos(rLat2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180.0 / Math.PI + 360.0) % 360.0;
    }

    private void AlignVirtualNorth(float bearing) { if (navContainer != null) navContainer.rotation = Quaternion.Euler(0, -bearing, 0); }

    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        double R = 6371000; double dLat = (lat2 - lat1) * Math.PI / 180.0; double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
        return R * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
    }

    public void StartNavigationTo(string placeId, string displayName)
    {
        destinationId = placeId; destinationName = displayName;
        isNavigating = true; isFinalSprint = false;
        StopAllCoroutines(); StartCoroutine(StartNavigationFlow());
    }

    private IEnumerator StartNavigationFlow()
    {
        yield return StartCoroutine(SnapLocationToRoad(currentLat, currentLng));
        yield return StartCoroutine(UpdateShortestWalkingRoute());
        if (isNavigating) voiceHandler.Speak($"開始導航。");
    }

    private void DoVoiceReport() { voiceHandler.Speak($"剩餘 {Math.Round(currentRemainingDistance)} 公尺。"); }
    private void TriggerArrival() { isNavigating = false; systemStatus = "🚩 已抵達"; UpdateNavigationUI(0); if (arrivalSfx != null) audioSource.PlayOneShot(arrivalSfx); voiceHandler.Speak($"導航結束。"); }
    private void UpdateCoordinatesUI() { if (coordinatesOutputText != null) coordinatesOutputText.text = $"座標: {currentLat:F5}, {currentLng:F5}"; }

    private IEnumerator SnapLocationToRoad(double lat, double lng)
    {
        string url = $"https://roads.googleapis.com/v1/snapToRoads?path={lat},{lng}&key={GoogleApiKey}";
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
            {
                snappedLat = ParseJsonDouble(req.downloadHandler.text, "latitude");
                snappedLng = ParseJsonDouble(req.downloadHandler.text, "longitude");
            }
            else { snappedLat = lat; snappedLng = lng; }
        }
    }

    private IEnumerator UpdateShortestWalkingRoute()
    {
        string url = $"https://maps.googleapis.com/maps/api/directions/json?origin={snappedLat},{snappedLng}&destination=place_id:{destinationId}&mode=walking&key={GoogleApiKey}";
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success && req.downloadHandler.text.Contains("OK"))
            {
                ParseDirections(req.downloadHandler.text);
                isNavigating = true; systemStatus = "🚀 導航中";
            }
        }
    }

    private double ParseJsonDouble(string json, string key)
    {
        int idx = json.IndexOf($"\"{key}\""); if (idx < 0) return 0;
        int cl = json.IndexOf(":", idx); int st = cl + 1;
        while (json[st] == ' ' || json[st] == '"') st++;
        int ed = st; while (ed < json.Length && (char.IsDigit(json[ed]) || json[ed] == '.' || json[ed] == '-')) ed++;
        double.TryParse(json.Substring(st, ed - st), out double r); return r;
    }

    private string ExtractValue(string json, string key)
    {
        int idx = json.IndexOf($"\"{key}\""); if (idx < 0) return "--";
        int cl = json.IndexOf(":", idx);
        int q1 = json.IndexOf("\"", cl + 1);
        int q2 = json.IndexOf("\"", q1 + 1);
        return (q1 < 0 || q2 < 0) ? "--" : json.Substring(q1 + 1, q2 - q1 - 1);
    }
}
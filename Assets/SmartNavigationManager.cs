using UnityEngine;
using TMPro;
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;

public class SmartNavigationManager : MonoBehaviour
{
    [Header("API Keys")]
    public string openAIKey = "YOUR_OPENAI_KEY";
    public string googleApiKey = "YOUR_GOOGLE_KEY";

    [Header("UI & Audio References")]
    public TextMeshProUGUI statusText;      // 顯示錄音/分析狀態
    public TextMeshProUGUI navPanelText;    // 顯示導航資訊面板 (TMP)
    public AudioSource audioSource;
    public AudioClip arrivalSfx;

    [Header("Input Settings")]
    private readonly OVRInput.Button voiceTrigger = OVRInput.Button.One;             // 右手 A 鍵
    private readonly OVRInput.Button manualUpdateTrigger = OVRInput.Button.PrimaryThumbstick; // 搖桿按下

    // 導航內部狀態
    private string destinationName, destinationId;
    private double currentLat, currentLng, snappedLat, snappedLng, targetLat, targetLng, lastApiLat, lastApiLng;
    private double walkingDistanceTotal = 0, currentRemainingDist = 0;
    private string durationText = "--", distanceText = "--", systemStatus = "等待指令";

    private bool isNavigating = false, isFinalSprint = false, isRecording = false;
    private int sampleRate = 16000;
    private AudioClip recordingClip;

    // 定時器
    private float nextReportTime = 0f;
    private const float REPORT_INTERVAL = 10f;

    void Start()
    {
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        StartCoroutine(InitializeGPS());
    }

    void Update()
    {
        // 1. 偵測語音辨識按鍵 (A 鍵)
        if (OVRInput.GetDown(voiceTrigger, OVRInput.Controller.RTouch)) StartRecording();
        if (OVRInput.GetUp(voiceTrigger, OVRInput.Controller.RTouch)) _ = StopRecordingAndProcess();

        if (isNavigating)
        {
            // 2. 搖桿手動強制更新定位
            if (OVRInput.GetDown(manualUpdateTrigger, OVRInput.Controller.RTouch)) StartCoroutine(ManualForceUpdateFlow());

            // 3. 每 10 秒自動語音播報
            if (Time.time >= nextReportTime) { TriggerVoiceReport(); nextReportTime = Time.time + REPORT_INTERVAL; }

            // 4. 每幀更新 UI (確保 TMP 顯示不會消失)
            UpdateNavigationUI();
        }
    }

    private IEnumerator InitializeGPS()
    {
        Input.location.Start(1f, 1f);
        int maxWait = 20;
        while (Input.location.status == LocationServiceStatus.Initializing && maxWait-- > 0) yield return new WaitForSeconds(1);
        systemStatus = (Input.location.status == LocationServiceStatus.Running) ? "✅ 定位就緒" : "❌ GPS 失敗";
    }

    // --- 1. 語音辨識與分析 ---

    private void StartRecording()
    {
        if (isRecording || Microphone.devices.Length == 0) return;
        isRecording = true;
        statusText.text = "🎤 正在聆聽中...";
        OVRInput.SetControllerVibration(0.3f, 0.3f, OVRInput.Controller.RTouch);
        recordingClip = Microphone.Start("", false, 15, sampleRate);
    }

    private async Task StopRecordingAndProcess()
    {
        int lastPos = Microphone.GetPosition(null);
        Microphone.End("");
        isRecording = false;
        OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);

        statusText.text = "⌛ 辨識中...";
        byte[] wav = AudioClipToWav(recordingClip, lastPos);

        string transcript = await SendToWhisperAsync(wav);
        if (string.IsNullOrEmpty(transcript)) { Speak("沒聽清楚。"); return; }

        statusText.text = $"🗣️: {transcript}";
        string keyword = await ExtractKeywordWithGPT(transcript);
        if (keyword.Contains("INVALID")) { Speak("這似乎不是導航指令。"); return; }

        // 呼叫搜尋功能
        await SearchNearestPlace(keyword);
    }

    // --- 2. 地點搜尋 (整合您提供的程式碼) ---

    private async Task SearchNearestPlace(string keyword)
    {
        // 檢查定位狀態
        if (Input.location.status != LocationServiceStatus.Running) { Speak("GPS 尚未就緒。"); return; }

        float lat = Input.location.lastData.latitude;
        float lng = Input.location.lastData.longitude;

        // 使用 Google Places Nearby Search
        string url = $"https://maps.googleapis.com/maps/api/place/nearbysearch/json?location={lat},{lng}&keyword={UnityWebRequest.EscapeURL(keyword)}&rankby=distance&key={googleApiKey}";

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (req.result != UnityWebRequest.Result.Success) { Speak("網路查詢失敗。"); return; }

            string json = req.downloadHandler.text;
            string pName = SimpleJsonExtract(json, "name");
            string pId = SimpleJsonExtract(json, "place_id");

            if (!string.IsNullOrEmpty(pId))
            {
                // 找到地點，開始啟動導航流程
                destinationId = pId;
                destinationName = pName;
                isNavigating = false;
                StopAllCoroutines();
                StartCoroutine(NavigationFlow());

                // 使用 OpenAI 語音反饋
                Speak($"已找到最近的 {pName}。正在規劃精確路線。");
            }
            else
            {
                Speak($"在附近找不到關於 {keyword} 的地點。");
            }
        }
    }

    // --- 3. 導航流程 ---

    private IEnumerator NavigationFlow()
    {
        systemStatus = "📡 規劃路線中...";
        yield return StartCoroutine(SnapToRoad());
        yield return StartCoroutine(GetDirections());

        if (isNavigating)
        {
            // 啟動即播報目的地與剩餘距離
            Speak($"導航開始。前往 {destinationName}，目前距離約 {distanceText}。");
            nextReportTime = Time.time + REPORT_INTERVAL;
            StartCoroutine(TrackingLoop());
        }
    }

    private IEnumerator TrackingLoop()
    {
        while (isNavigating)
        {
            double nowLat = Input.location.lastData.latitude;
            double nowLng = Input.location.lastData.longitude;
            double moved = CalculateDistance(nowLat, nowLng, lastApiLat, lastApiLng);
            double straight = CalculateDistance(nowLat, nowLng, targetLat, targetLng);
            currentRemainingDist = isFinalSprint ? straight : walkingDistanceTotal - moved;

            if (straight <= 15.0) isFinalSprint = true;
            if (straight <= 8.0) { isNavigating = false; TriggerArrival(); yield break; }

            // 定位更新邏輯：移動 20 公尺觸發 API 更新
            if (moved >= 20.0)
            {
                yield return StartCoroutine(SnapToRoad());
                yield return StartCoroutine(GetDirections());
                TriggerVoiceReport(); // 更新座標時自動語音告知
                nextReportTime = Time.time + REPORT_INTERVAL;
            }
            yield return new WaitForSeconds(1.0f);
        }
    }

    private void TriggerVoiceReport()
    {
        int dist = (int)Math.Round(currentRemainingDist);
        // 定期播報內容
        Speak($"定位已重新更新。剩餘距離大約 {dist} 公尺，預計時間 {durationText}。");
    }

    private IEnumerator ManualForceUpdateFlow()
    {
        systemStatus = "🔍 定位更新中...";
        yield return StartCoroutine(SnapToRoad());
        yield return StartCoroutine(GetDirections());
        systemStatus = "✅ 更新完成";
        TriggerVoiceReport();
        yield return new WaitForSeconds(3f);
        if (isNavigating) systemStatus = "🚀 導航中";
    }

    // --- 4. 語音核心 (OpenAI TTS) ---

    public async void Speak(string message)
    {
        string json = "{\"model\":\"tts-1\",\"input\":\"" + JsonEscape(message) + "\",\"voice\":\"alloy\"}";
        byte[] body = Encoding.UTF8.GetBytes(json);
        using (UnityWebRequest www = new UnityWebRequest("https://api.openai.com/v1/audio/speech", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(body);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAIKey);
            var op = www.SendWebRequest(); while (!op.isDone) await Task.Yield();
            if (www.result == UnityWebRequest.Result.Success)
            {
                UnityMainThreadDispatcher.Instance().Enqueue(() => { StartCoroutine(PlayAudioCoroutine(www.downloadHandler.data)); });
            }
        }
    }

    private IEnumerator PlayAudioCoroutine(byte[] bytes)
    {
        string path = Path.Combine(Application.temporaryCachePath, "tts_nav.mp3");
        File.WriteAllBytes(path, bytes);
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG))
        {
            yield return www.SendWebRequest();
            if (www.result == UnityWebRequest.Result.Success)
            {
                audioSource.clip = DownloadHandlerAudioClip.GetContent(www);
                audioSource.Play();
            }
        }
    }

    // --- 5. 工具方法 (數學運算與 API 輔助) ---

    private byte[] AudioClipToWav(AudioClip clip, int lengthSamples)
    {
        float[] samples = new float[lengthSamples]; clip.GetData(samples, 0);
        MemoryStream ms = new MemoryStream(); BinaryWriter bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + samples.Length * 2);
        bw.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sampleRate); bw.Write(sampleRate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(samples.Length * 2);
        foreach (float f in samples) bw.Write((short)Mathf.Clamp(f * 32767f, short.MinValue, short.MaxValue));
        return ms.ToArray();
    }

    private async Task<string> SendToWhisperAsync(byte[] wav)
    {
        WWWForm form = new WWWForm(); form.AddBinaryData("file", wav, "audio.wav", "audio/wav"); form.AddField("model", "whisper-1");
        using (UnityWebRequest req = UnityWebRequest.Post("https://api.openai.com/v1/audio/transcriptions", form))
        {
            req.SetRequestHeader("Authorization", "Bearer " + openAIKey);
            var op = req.SendWebRequest(); while (!op.isDone) await Task.Yield();
            return (req.result == UnityWebRequest.Result.Success) ? SimpleJsonExtract(req.downloadHandler.text, "text") : "";
        }
    }

    private async Task<string> ExtractKeywordWithGPT(string text)
    {
        string payload = "{\"model\":\"gpt-4o-mini\",\"messages\":[{\"role\":\"system\",\"content\":\"提取目的地。只回覆名稱。\"},{\"role\":\"user\",\"content\":\"" + JsonEscape(text) + "\"}]}";
        using (UnityWebRequest www = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAIKey);
            var op = www.SendWebRequest(); while (!op.isDone) await Task.Yield();
            return (www.result == UnityWebRequest.Result.Success) ? SimpleJsonExtract(www.downloadHandler.text, "content").Trim() : "INVALID";
        }
    }

    private IEnumerator SnapToRoad()
    {
        double lat = Input.location.lastData.latitude; double lng = Input.location.lastData.longitude;
        string url = $"https://roads.googleapis.com/v1/snapToRoads?path={lat},{lng}&key={googleApiKey}";
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success && req.downloadHandler.text.Contains("latitude"))
            {
                string json = req.downloadHandler.text;
                snappedLat = ParseJsonDouble(json, "latitude"); snappedLng = ParseJsonDouble(json, "longitude");
            }
            else { snappedLat = lat; snappedLng = lng; }
        }
    }

    private IEnumerator GetDirections()
    {
        string url = $"https://maps.googleapis.com/maps/api/directions/json?origin={snappedLat},{snappedLng}&destination=place_id:{destinationId}&mode=walking&key={googleApiKey}";
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest(); string json = req.downloadHandler.text;
            if (json.Contains("OK"))
            {
                int legsIdx = json.IndexOf("\"legs\""); int distIdx = json.IndexOf("\"distance\"", legsIdx);
                walkingDistanceTotal = ParseJsonDouble(json.Substring(distIdx), "value");
                distanceText = SimpleJsonExtract(json.Substring(distIdx), "text");
                durationText = SimpleJsonExtract(json.Substring(json.IndexOf("\"duration\"", legsIdx)), "text");
                int endIdx = json.IndexOf("\"end_location\"", legsIdx);
                targetLat = ParseJsonDouble(json.Substring(endIdx), "lat"); targetLng = ParseJsonDouble(json.Substring(endIdx), "lng");
                lastApiLat = Input.location.lastData.latitude; lastApiLng = Input.location.lastData.longitude;
                isNavigating = true;
            }
        }
    }

    private void UpdateNavigationUI()
    {
        if (navPanelText == null) return;
        string color = isFinalSprint ? "red" : "green";
        navPanelText.text = $"🏁 目標：<b>{destinationName} ({distanceText})</b>\n\n[ <color={color}>{systemStatus}</color> ]\n預計時間：{durationText}\n--------------------\n<size=120%>🔔 剩餘：<color=yellow>{Math.Round(currentRemainingDist)}</color> 公尺</size>";
    }

    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        double R = 6371000; double dLat = (lat2 - lat1) * Math.PI / 180.0; double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
        return R * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
    }

    private double ParseJsonDouble(string json, string key)
    {
        int idx = json.IndexOf($"\"{key}\""); if (idx < 0) return 0;
        int cl = json.IndexOf(":", idx); int st = cl + 1;
        while (json[st] == ' ' || json[st] == '"') st++;
        int ed = st; while (ed < json.Length && (char.IsDigit(json[ed]) || json[ed] == '.' || json[ed] == '-')) ed++;
        double.TryParse(json.Substring(st, ed - st), out double r); return r;
    }

    private string SimpleJsonExtract(string json, string key)
    {
        int idx = json.IndexOf($"\"{key}\""); if (idx < 0) return "";
        int f = json.IndexOf('"', idx + key.Length + 2); int s = json.IndexOf('"', f + 1);
        return (f < 0 || s < 0) ? "" : json.Substring(f + 1, s - f - 1);
    }

    private string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    private void TriggerArrival() { if (arrivalSfx != null) audioSource.PlayOneShot(arrivalSfx); Speak("導航結束。"); }
}
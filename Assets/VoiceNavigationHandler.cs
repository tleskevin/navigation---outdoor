using UnityEngine;
using TMPro;
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;

public class VoiceNavigationHandler : MonoBehaviour
{
    [Header("References")]
    public MapDataLoader mapDataLoader;
    public AudioSource audioSource;
    public TextMeshProUGUI statusText;

    [Header("API Keys")]
    public string openAIKey = "YOUR_OPENAI_KEY";
    public string googleApiKey = "YOUR_GOOGLE_KEY";

    [Header("Settings")]
    private readonly OVRInput.Button navTriggerButton = OVRInput.Button.One; // 右手 A 鍵
    private bool isRecording = false;
    private AudioClip recordingClip;
    private int sampleRate = 16000;

    [Header("Haptic Settings")]
    [Range(0, 1)] public float vibrationAmplitude = 0.3f;
    [Range(0, 1)] public float vibrationFrequency = 0.3f;

    void Start()
    {
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        if (statusText != null) statusText.text = "按住右手 A 鍵開始語音導航";
    }

    void Update()
    {
        // 偵測按下 A 鍵
        if (OVRInput.GetDown(navTriggerButton, OVRInput.Controller.RTouch))
        {
            StartRecording();
        }

        // 偵測放開 A 鍵
        if (OVRInput.GetUp(navTriggerButton, OVRInput.Controller.RTouch))
        {
            StopRecordingAndProcessAsync().ConfigureAwait(false);
        }
    }

    private void StartRecording()
    {
        if (isRecording || Microphone.devices.Length == 0) return;
        isRecording = true;
        if (statusText != null) statusText.text = "🎤 正在聆聽中...";

        // 震動回饋：開始錄音
        OVRInput.SetControllerVibration(vibrationFrequency, vibrationAmplitude, OVRInput.Controller.RTouch);

        recordingClip = Microphone.Start("", false, 15, sampleRate);
    }

    private async Task StopRecordingAndProcessAsync()
    {
        Microphone.End("");
        isRecording = false;

        // 停止震動
        OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);

        if (statusText != null) statusText.text = "⌛ 正在分析語音...";
        if (recordingClip == null) return;

        byte[] wav = AudioClipToWav(recordingClip);

        // 1. Whisper STT
        string transcript = await SendToWhisperAsync(wav);
        if (string.IsNullOrEmpty(transcript)) { Speak("沒聽清楚，請重試一次。"); return; }
        Debug.Log($"[Whisper] 辨識: {transcript}");

        // 2. GPT 提取關鍵字
        string keyword = await ExtractKeywordWithGPT(transcript);
        Debug.Log($"[GPT] 關鍵字: {keyword}");

        if (keyword.Contains("INVALID")) { Speak("這似乎不是一個導航指令。"); return; }

        // 3. Google Places Nearby Search (搜尋最近的地點)
        await SearchNearestPlace(keyword);
    }

    private async Task SearchNearestPlace(string keyword)
    {
        if (Input.location.status != LocationServiceStatus.Running)
        {
            Speak("GPS 定位尚未就緒，請檢查權限。");
            return;
        }

        float lat = Input.location.lastData.latitude;
        float lng = Input.location.lastData.longitude;
        if (statusText != null) statusText.text = $"🔍 搜尋最近的 {keyword}...";

        // 改用 Nearby Search 並加上 rankby=distance，確保回傳的是最近的一間
        string url = $"https://maps.googleapis.com/maps/api/place/nearbysearch/json" +
                     $"?location={lat},{lng}" +
                     $"&keyword={UnityWebRequest.EscapeURL(keyword)}" +
                     $"&rankby=distance" +
                     $"&key={googleApiKey}";

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (req.result != UnityWebRequest.Result.Success) { Speak("網路查詢失敗。"); return; }

            string json = req.downloadHandler.text;

            // 解析結果 (Nearby Search 回傳 results 陣列)
            string placeName = SimpleJsonExtract(json, "name");
            string address = SimpleJsonExtract(json, "vicinity"); // Nearby Search 使用 vicinity
            float tLat = ParseJsonFloat(json, "location", "lat");
            float tLng = ParseJsonFloat(json, "location", "lng");

            if (string.IsNullOrEmpty(placeName))
            {
                Speak($"在您附近找不到關於 {keyword} 的地點。");
                return;
            }

            // 4. 直線距離驗證 (Haversine)
            float dist = CalculateDistance(lat, lng, tLat, tLng);

            if (dist > 10000f) // 超過 10 公里
            {
                Speak($"最近的 {placeName} 距離超過十公里，不符合短程導航。");
                return;
            }

            // 成功啟動導航
            if (statusText != null) statusText.text = $"🚀 目標：{placeName} ({Mathf.RoundToInt(dist)}m)";
            mapDataLoader.StartNavigationTo(address);
            Speak($"已找到最近的{placeName}，距離大約 {Mathf.RoundToInt(dist)} 公尺，正在規劃路線。");
        }
    }

    // --- 功能性方法 ---

    public async void Speak(string message)
    {
        byte[] ttsBytes = await TextToSpeechAsync(message);
        if (ttsBytes != null)
        {
            UnityMainThreadDispatcher.Instance().Enqueue(() => {
                StartCoroutine(PlayAudioBytesCoroutine(ttsBytes, audioSource));
            });
        }
    }

    private float CalculateDistance(float lat1, float lon1, float lat2, float lon2)
    {
        float R = 6371000f;
        float dLat = (lat2 - lat1) * Mathf.Deg2Rad;
        float dLon = (lon2 - lon1) * Mathf.Deg2Rad;
        float a = Mathf.Sin(dLat / 2) * Mathf.Sin(dLat / 2) + Mathf.Cos(lat1 * Mathf.Deg2Rad) * Mathf.Cos(lat2 * Mathf.Deg2Rad) * Mathf.Sin(dLon / 2) * Mathf.Sin(dLon / 2);
        return R * 2 * Mathf.Atan2(Mathf.Sqrt(a), Mathf.Sqrt(1 - a));
    }

    // --- API 呼叫 ---

    private async Task<string> ExtractKeywordWithGPT(string userText)
    {
        string systemPrompt = "你是一個導航助手。請從句子中提取『目的地名稱』。如果是『附近的全家』回覆『全家』。只回覆名稱，不要標點。若不相關回覆 INVALID。";
        string payload = "{\"model\":\"gpt-4o-mini\",\"messages\":[{\"role\":\"system\",\"content\":\"" + systemPrompt + "\"},{\"role\":\"user\",\"content\":\"" + JsonEscape(userText) + "\"}]}";
        byte[] body = Encoding.UTF8.GetBytes(payload);
        using (UnityWebRequest www = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(body);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAIKey);
            var op = www.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            return (www.result == UnityWebRequest.Result.Success) ? SimpleJsonExtract(Encoding.UTF8.GetString(www.downloadHandler.data), "content").Trim() : "INVALID";
        }
    }

    private async Task<string> SendToWhisperAsync(byte[] wavBytes)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("file", wavBytes, "audio.wav", "audio/wav");
        form.AddField("model", "whisper-1");
        using (UnityWebRequest req = UnityWebRequest.Post("https://api.openai.com/v1/audio/transcriptions", form))
        {
            req.SetRequestHeader("Authorization", "Bearer " + openAIKey);
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            return (req.result == UnityWebRequest.Result.Success) ? SimpleJsonExtract(Encoding.UTF8.GetString(req.downloadHandler.data), "text") : "";
        }
    }

    private async Task<byte[]> TextToSpeechAsync(string text)
    {
        string json = "{\"model\":\"tts-1\",\"input\":\"" + JsonEscape(text) + "\",\"voice\":\"alloy\"}";
        byte[] body = Encoding.UTF8.GetBytes(json);
        using (UnityWebRequest www = new UnityWebRequest("https://api.openai.com/v1/audio/speech", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(body);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAIKey);
            var op = www.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            return (www.result == UnityWebRequest.Result.Success) ? www.downloadHandler.data : null;
        }
    }

    // --- 工具 ---
    private string SimpleJsonExtract(string json, string key)
    {
        int idx = json.IndexOf($"\"{key}\"");
        if (idx < 0) return "";
        int firstQuote = json.IndexOf('"', idx + key.Length + 2);
        int secondQuote = json.IndexOf('"', firstQuote + 1);
        if (firstQuote < 0 || secondQuote < 0) return "";
        return JsonUnescape(json.Substring(firstQuote + 1, secondQuote - firstQuote - 1));
    }

    private float ParseJsonFloat(string json, string parentKey, string childKey)
    {
        int pIdx = json.IndexOf($"\"{parentKey}\"");
        if (pIdx < 0) return 0;
        int cIdx = json.IndexOf($"\"{childKey}\"", pIdx);
        int colon = json.IndexOf(":", cIdx);
        int start = colon + 1;
        while (json[start] == ' ' || json[start] == '"') start++;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-')) end++;
        float.TryParse(json.Substring(start, end - start), out float r);
        return r;
    }

    private byte[] AudioClipToWav(AudioClip clip)
    {
        float[] samples = new float[clip.samples];
        clip.GetData(samples, 0);
        MemoryStream ms = new MemoryStream();
        BinaryWriter bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + samples.Length * 2);
        bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sampleRate); bw.Write(sampleRate * 2);
        bw.Write((short)2); bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(samples.Length * 2);
        foreach (float f in samples) bw.Write((short)Mathf.Clamp(f * 32767f, short.MinValue, short.MaxValue));
        return ms.ToArray();
    }

    private IEnumerator PlayAudioBytesCoroutine(byte[] audioBytes, AudioSource src)
    {
        string path = Path.Combine(Application.temporaryCachePath, "tts_temp.mp3");
        File.WriteAllBytes(path, audioBytes);
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG))
        {
            yield return www.SendWebRequest();
            if (www.result == UnityWebRequest.Result.Success) { src.clip = DownloadHandlerAudioClip.GetContent(www); src.Play(); }
        }
    }

    private string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    private string JsonUnescape(string s) => s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
}
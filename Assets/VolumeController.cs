using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;

public class VolumeManager : MonoBehaviour
{
    [Header("音訊元件")]
    public AudioSource audioSource;
    public AudioClip introAudioClip;
    public AudioClip backgroundMusic;
    public AudioClip startListeningSound;

    [Header("UI 元件")]
    public Slider volumeSlider;

    [Header("OpenAI 設定")]
    public string openAIKey = "您的API_KEY";

    private bool isRecording = false;
    private AudioClip recordingClip;
    private int sampleRate = 16000;
    private bool hasStartedPlaying = false;
    private const string VolumeKey = "GlobalVolume";
    //按鍵A=OVRInput.Button.One ， 按鍵B=OVRInput.Button.Two，板機鍵=OVRInput.Button.PrimaryIndexTrigger
    private readonly OVRInput.Button recordButton = OVRInput.Button.One;

    void Start()
    {
        // 自動嘗試抓取同物件上的 AudioSource
        if (audioSource == null) audioSource = GetComponent<AudioSource>();

        // 初始化音量設定 (從 PlayerPrefs 讀取)
        float savedVolume = PlayerPrefs.GetFloat(VolumeKey, 0.5f);
        if (audioSource != null) audioSource.volume = savedVolume;

        // 設定 Slider 初始值
        if (volumeSlider != null)
        {
            volumeSlider.minValue = 0f;
            volumeSlider.maxValue = 1f;
            volumeSlider.value = savedVolume;
            volumeSlider.onValueChanged.RemoveAllListeners();
            volumeSlider.onValueChanged.AddListener(OnVolumeChanged);
        }

        // --- 核心修改：啟動播放序列 (先 A 再 背景音樂) ---
        StartCoroutine(PlayAudioSequence());
    }

    // 新增：處理先播 A 再循環播背景音樂的協程
    private IEnumerator PlayAudioSequence()
    {
        if (audioSource == null) yield break;

        // 1. 準備並播放開場音檔 A
        if (introAudioClip != null)
        {
            audioSource.clip = introAudioClip;
            audioSource.loop = false; // A 不需要循環
            audioSource.Play();

            // 等待 A 播放完畢
            // 使用 while 檢查 isPlaying 是最保險的做法
            while (audioSource.isPlaying)
            {
                yield return null;
            }
        }

        // 2. A 播完後，切換到背景音樂並開啟循環播放
        if (backgroundMusic != null)
        {
            audioSource.clip = backgroundMusic;
            audioSource.loop = true; // 開啟重複播放
            audioSource.Play();
            hasStartedPlaying = true;
        }
    }

    void Update()
    {
        if (OVRInput.GetDown(recordButton))
        {
            if (!isRecording)
            {
                Debug.Log("<color=green>[A Button]</color> 開始錄音...");
                StartVoiceRecording();
            }
            else
            {
                Debug.Log("<color=yellow>[A Button]</color> 停止錄音，分析中...");
                _ = StopAndProcessVoiceAsync();
            }
        }
    }

    private void StartVoiceRecording()
    {
        if (startListeningSound != null) audioSource.PlayOneShot(startListeningSound);
        recordingClip = Microphone.Start(null, false, 10, sampleRate);
        isRecording = true;
    }

    private async Task StopAndProcessVoiceAsync()
    {
        Microphone.End(null);
        isRecording = false;

        if (recordingClip == null) return;

        byte[] wavData = AudioClipToWav(recordingClip);

        string transcript = await SendToWhisperAsync(wavData);
        Debug.Log($"<color=cyan>[Whisper 原始結果]:</color> {transcript}");

        if (string.IsNullOrEmpty(transcript)) return;

        // 核心修正：交給 GPT 糾錯聯想
        string correctedText = await SendToChatForCommandAsync(transcript);
        Debug.Log($"<color=magenta>[GPT 修正後結果]:</color> {correctedText}");

        MainThreadDispatcher.RunOnMainThread(() => {
            ProcessVolumeCommandLogic(correctedText);
        });
    }

    // --- 修改後的處理邏輯：統一由這個 Function 處理中文字串 ---
    private void ProcessVolumeCommandLogic(string text)
    {
        float targetVal = volumeSlider.value;
        bool changed = false;

        if (text.Contains("大聲") || text.Contains("增加") || text.Contains("提高") || text.Contains("大聲一點") || text.Contains("音量增加"))
        {
            targetVal = Mathf.Clamp01(targetVal + 0.2f);
            changed = true;
        }
        else if (text.Contains("小聲") || text.Contains("降低") || text.Contains("減少") || text.Contains("小聲一點") || text.Contains("音量減少") || text.Contains("降低音量"))
        {
            targetVal = Mathf.Clamp01(targetVal - 0.2f);
            changed = true;
        }
        else if (text.Contains("最大聲") || text.Contains("最大") || text.Contains("滿格") || text.Contains("最大聲"))
        {
            targetVal = 1.0f;
            changed = true;
        }
        else if (text.Contains("靜音") || text.Contains("最小") || text.Contains("關閉"))
        {
            targetVal = 0.0f;
            changed = true;
        }
        else if (text.Contains("一半") || text.Contains("百分之五十"))
        {
            targetVal = 0.5f;
            changed = true;
        }

        if (changed && targetVal != volumeSlider.value)
        {
            Debug.Log($"<color=orange>[Control]</color> 執行音量調整: {targetVal:F2}");
            volumeSlider.value = targetVal;
        }
    }

    // --- 修改後的 GPT 糾錯服務：要求其回傳特定的中文關鍵字 ---
    private async Task<string> SendToChatForCommandAsync(string userText)
    {
        // 修正 Prompt：讓 GPT 直接修正並聯想成我們程式碼支援的中文字串
        string systemPrompt = "你是一個音量指令修正器。使用者語音可能會有口誤或辨識錯誤（如：『大叔』『大書』聯想到『大聲』；『小生』『小山』聯想到『小聲』）。" +
                              "請將輸入聯想並轉換為以下唯一關鍵字之一：大聲、增加、提高、增加音量、大聲一點、小聲、降地、減少、小聲一點、降低音量、最大聲、最大、滿格、靜音、最小、一半。" +
                              "若無法判斷則回傳 NONE。只需回傳關鍵字，不要有解釋。";

        string payload = "{\"model\":\"gpt-4o-mini\",\"messages\":[" +
                         "{\"role\":\"system\",\"content\":\"" + systemPrompt + "\"}," +
                         "{\"role\":\"user\",\"content\":\"" + JsonEscape(userText) + "\"}]}";

        byte[] bodyRaw = Encoding.UTF8.GetBytes(payload);

        using (UnityWebRequest www = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAIKey);

            var op = www.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (www.result != UnityWebRequest.Result.Success) return userText; // 失敗則回傳原文字

            string json = Encoding.UTF8.GetString(www.downloadHandler.data);
            return ExtractChatContent(json).Trim();
        }
    }

    public void OnVolumeChanged(float value)
    {
        if (audioSource != null)
        {
            audioSource.volume = value;
            if (!hasStartedPlaying && value > 0.05f)
            {
                audioSource.Play();
                hasStartedPlaying = true;
            }
            PlayerPrefs.SetFloat(VolumeKey, value);
            PlayerPrefs.Save();
        }
    }

    // --- 工具函式 ---
    private async Task<string> SendToWhisperAsync(byte[] bytes)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("file", bytes, "audio.wav", "audio/wav");
        form.AddField("model", "whisper-1");
        form.AddField("language", "zh");

        using (UnityWebRequest req = UnityWebRequest.Post("https://api.openai.com/v1/audio/transcriptions", form))
        {
            req.SetRequestHeader("Authorization", "Bearer " + openAIKey);
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success) return "";
            string json = Encoding.UTF8.GetString(req.downloadHandler.data);
            return ExtractJsonField(json, "text");
        }
    }

    private string ExtractJsonField(string json, string field)
    {
        int idx = json.IndexOf("\"" + field + "\"");
        int q1 = json.IndexOf('"', idx + field.Length + 3);
        int q2 = json.IndexOf('"', q1 + 1);
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    private string ExtractChatContent(string json)
    {
        int idx = json.IndexOf("\"content\"");
        int q1 = json.IndexOf('"', idx + 10);
        int q2 = json.IndexOf('"', q1 + 1);
        return json.Substring(q1 + 1, q2 - q1 - 1);
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
        foreach (float f in samples) bw.Write((short)(f * 32767));
        return ms.ToArray();
    }

    private string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private void OnDestroy() { if (volumeSlider != null) volumeSlider.onValueChanged.RemoveListener(OnVolumeChanged); }
}

public class MainThreadDispatcher : MonoBehaviour
{
    private static readonly Queue<Action> _queue = new Queue<Action>();
    public static void RunOnMainThread(Action action) { lock (_queue) { _queue.Enqueue(action); } }
    void Update() { lock (_queue) { while (_queue.Count > 0) _queue.Dequeue().Invoke(); } }
    private static MainThreadDispatcher _instance;
    [RuntimeInitializeOnLoadMethod]
    static void Init()
    {
        var go = new GameObject("MainThreadDispatcher");
        _instance = go.AddComponent<MainThreadDispatcher>();
        DontDestroyOnLoad(go);
    }
}
using UnityEngine;
using TMPro;
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;

public class ABC_Play_Record_STT : MonoBehaviour
{
    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip audioClipABC; // 開場播放的語音

    [Header("UI")]
    public TextMeshProUGUI resultText;

    [Header("OpenAI API")]
    public string openAIKey = "YOUR_API_KEY_HERE";

    // 使用右手板機鍵 (PrimaryIndexTrigger)
    private readonly OVRInput.Button toggleButton = OVRInput.Button.PrimaryIndexTrigger;

    private bool isPlayingABC = false;
    private bool isRecording = false;
    private bool isReady = true; // 修改：初始設為 true，讓第一次按下板機鍵能觸發

    private AudioClip recordingClip;
    private int sampleRate = 16000;

    void Start()
    {
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.loop = false;

        // 修改：移除啟動即播放的邏輯，僅更新 UI 提示
        if (resultText != null) resultText.text = "Press Trigger to Start Guidance";
    }

    void Update()
    {
        // 偵測右手板機鍵按下
        if (OVRInput.GetDown(toggleButton))
        {
            // 1. 如果正在播放開場語音，按下板機鍵會重播
            if (isPlayingABC)
            {
                StopAllCoroutines();
                audioSource.Stop();
                PlayABC();
                return;
            }

            // 2. 如果功能已準備好（不在錄音中也不在播放中），按下板機開始播放與後續錄音
            if (isReady && !isRecording)
            {
                PlayABC();
            }
            // 3. 如果正在錄音，按下板機鍵停止錄音並上傳
            else if (isRecording)
            {
                StopRecordingAndProcessAsync().ConfigureAwait(false);
            }
        }
    }

    // --------------------------------------
    // Step 1：播放開場語音
    // --------------------------------------
    private void PlayABC()
    {
        if (audioClipABC == null)
        {
            Debug.LogError("音檔 ABC 尚未指定！");
            // 若沒指定音檔，直接進入錄音模式避免程式卡住
            StartRecording();
            return;
        }

        isReady = false; // 播放期間鎖定，直到播完或再次按下重播
        audioSource.clip = audioClipABC;
        audioSource.Play();
        isPlayingABC = true;

        if (resultText != null) resultText.text = "Playing Guidance...";

        StartCoroutine(CheckABCFinished());
    }

    private IEnumerator CheckABCFinished()
    {
        // 等待音效播放結束
        while (audioSource.isPlaying)
            yield return null;

        isPlayingABC = false;

        // 播完後自動進入錄音狀態
        StartRecording();
    }

    // --------------------------------------
    // Step 2：錄音開始
    // --------------------------------------
    private void StartRecording()
    {
        if (resultText != null) resultText.text = "Recording... Press Trigger to stop.";

        recordingClip = Microphone.Start("", false, 20, sampleRate);
        isRecording = true;
    }

    // --------------------------------------
    // Step 3：停止錄音 → Whisper STT → ChatGPT → TTS
    // --------------------------------------
    private async Task StopRecordingAndProcessAsync()
    {
        Microphone.End("");
        isRecording = false;

        if (resultText != null) resultText.text = "Transcribing...";

        if (recordingClip == null)
        {
            Debug.LogError("Recording Clip is NULL");
            isReady = true;
            return;
        }

        byte[] wav = AudioClipToWav(recordingClip);

        // 1) Whisper STT
        string transcript = await SendToWhisperAsync(wav);
        if (string.IsNullOrEmpty(transcript))
        {
            if (resultText != null) resultText.text = "STT Error";
            isReady = true;
            return;
        }

        if (resultText != null) resultText.text = "Transcript:\n" + transcript + "\n\nGPT Thinking...";

        // 2) Send to Chat
        string gptReply = await SendToChatAsync(transcript);

        // 3) TTS
        byte[] ttsBytes = await TextToSpeechAsync(gptReply);
        if (ttsBytes != null && ttsBytes.Length > 0)
        {
            StartCoroutine(PlayAudioBytesCoroutine(ttsBytes, audioSource));
        }

        if (resultText != null) resultText.text = "GPT:\n" + gptReply;

        // 完成所有對話後，解鎖功能以便進行下一次觸發
        isReady = true;
    }

    // --- API 傳輸與工具函式 (保持不變) ---
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
            if (req.result != UnityWebRequest.Result.Success) return "";
            string json = Encoding.UTF8.GetString(req.downloadHandler.data);
            int idx = json.IndexOf("\"text\"");
            if (idx < 0) return "";
            int q1 = json.IndexOf('"', idx + 6);
            int q2 = json.IndexOf('"', q1 + 1);
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }
    }

    private async Task<string> SendToChatAsync(string userMessage)
    {
        string payload = "{\"model\":\"gpt-4o-mini\",\"messages\":[{\"role\":\"user\",\"content\":\"" + JsonEscape(userMessage) + "\"}]}";
        byte[] bodyRaw = Encoding.UTF8.GetBytes(payload);
        using (UnityWebRequest www = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAIKey);
            var op = www.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            return ExtractAssistantContent(Encoding.UTF8.GetString(www.downloadHandler.data));
        }
    }

    private string ExtractAssistantContent(string json)
    {
        int txt = json.IndexOf("\"content\"");
        if (txt < 0) return "";
        int q1 = json.IndexOf('"', txt + 10);
        int q2 = json.IndexOf('"', q1 + 1);
        return JsonUnescape(json.Substring(q1 + 1, q2 - q1 - 1));
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
            return www.downloadHandler.data;
        }
    }

    private IEnumerator PlayAudioBytesCoroutine(byte[] audioBytes, AudioSource src)
    {
        string path = Path.Combine(Application.temporaryCachePath, "tts_temp.mp3");
        File.WriteAllBytes(path, audioBytes);
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG))
        {
            yield return www.SendWebRequest();
            src.clip = DownloadHandlerAudioClip.GetContent(www);
            src.Play();
        }
    }

    private byte[] AudioClipToWav(AudioClip clip)
    {
        float[] samples = new float[clip.samples];
        clip.GetData(samples, 0);
        MemoryStream ms = new MemoryStream();
        BinaryWriter bw = new BinaryWriter(ms);
        int byteCount = samples.Length * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + byteCount);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sampleRate); bw.Write(sampleRate * 2);
        bw.Write((short)2); bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(byteCount);
        foreach (float f in samples)
        {
            short s = (short)Mathf.Clamp(f * 32767f, short.MinValue, short.MaxValue);
            bw.Write(s);
        }
        return ms.ToArray();
    }

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    private static string JsonUnescape(string s) => s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
}
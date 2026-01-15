using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;

public class VoiceButtonTrigger : MonoBehaviour
{
    [Header("Audio 設定")]
    public AudioSource audioSource;
    public AudioClip startListeningSound; // 啟動提示音
    public AudioClip openingSound;        // 一開始要播放的開場語音

    [Header("OpenAI API 設定")]
    public string openAIKey = "您的API_KEY";

    private bool isRecording = false;
    private bool isReady = false;         // 標記是否已導覽完畢並解鎖功能
    private AudioClip recordingClip;
    private int sampleRate = 16000;

    // 按鍵定義
    private readonly OVRInput.Button toggleButton = OVRInput.Button.Two; // 右手 B 鍵 (錄音)
    private readonly OVRInput.Button triggerButton = OVRInput.Button.PrimaryIndexTrigger; // 板機鍵 (重播)

    void Start()
    {
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.loop = false;

        // 一啟動就播放語音
        StartCoroutine(PlayOpeningSequence());
    }

    // 播放開場語音的協程
    private IEnumerator PlayOpeningSequence()
    {
        isReady = false; // 播放時鎖定功能
        if (openingSound != null)
        {
            audioSource.clip = openingSound;
            audioSource.Play();
            Debug.Log("正在播放開場語音...");

            // 等待音效播放結束
            while (audioSource.isPlaying)
            {
                yield return null;
            }
        }

        isReady = true; // 播放完畢，解鎖錄音功能
        Debug.Log("語音播放結束，功能已解鎖");
    }

    void Update()
    {
        // 1. 偵測板機鍵 (隨時可以重播，但重播時會重新鎖定功能直到播完)
        if (OVRInput.GetDown(triggerButton))
        {
            if (!isRecording) // 錄音中不允許重播語音
            {
                StopAllCoroutines(); // 停止目前的播放協程
                StartCoroutine(PlayOpeningSequence());
                return; // 觸發重播後跳出 Update
            }
        }

        // 2. 只有在語音播完 (isReady) 且不在錄音狀態下才允許啟動錄音
        if (isReady)
        {
            if (OVRInput.GetDown(toggleButton))
            {
                if (!isRecording)
                {
                    StartRecordingProcess();
                }
                else
                {
                    StopAndProcessVoiceAsync().ConfigureAwait(false);
                }
            }
        }
    }

    // 1. 開始錄音流程
    private void StartRecordingProcess()
    {
        if (startListeningSound != null)
        {
            audioSource.PlayOneShot(startListeningSound);
        }

        Debug.Log("錄音中... 再次按下 B 鍵停止");
        recordingClip = Microphone.Start("", false, 10, sampleRate);
        isRecording = true;
    }

    // 2. 停止錄音並傳送
    private async Task StopAndProcessVoiceAsync()
    {
        Microphone.End("");
        isRecording = false;
        Debug.Log("正在辨識語音...");

        if (recordingClip == null) return;

        byte[] wav = AudioClipToWav(recordingClip);
        string transcript = await SendToWhisperAsync(wav);
        Debug.Log("Whisper 辨識結果: " + transcript);

        ProcessCommand(transcript);
    }

    // 3. 核心邏輯
    private void ProcessCommand(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (text.Contains("執行程式") || text.Contains("開始導航") || text.Contains("去導航") || text.Contains("開啟相機") || text.Contains("導航"))
        {
            OnAButtonClick();
        }
        else if (text.Contains("介紹說明") || text.Contains("怎麼使用") || text.Contains("教學") || text.Contains("說明文件") || text.Contains("介紹一下") || text.Contains("介紹") || text.Contains("說明") || text.Contains("導覽介面"))
        {
            OnBButtonClick();
        }
        else if (text.Contains("返回") || text.Contains("回到首頁") || text.Contains("返回首頁") || text.Contains("離開"))
        {
            OnReturnButtonClick();
        }
        else
        {
            Debug.Log("辨識到無關文字: " + text);
        }
    }

    public void OnAButtonClick() => SceneManager.LoadScene("Camera");
    public void OnBButtonClick() => SceneManager.LoadScene("userUI");
    public void OnReturnButtonClick() => SceneManager.LoadScene("menu");

    // --- 工具函式 (Whisper API & Wav 轉換) ---

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

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Whisper Error: " + req.error);
                return "";
            }

            string json = Encoding.UTF8.GetString(req.downloadHandler.data);
            int idx = json.IndexOf("\"text\"");
            if (idx < 0) return "";
            int firstQuote = json.IndexOf('"', idx + 6);
            int secondQuote = json.IndexOf('"', firstQuote + 1);
            if (firstQuote < 0 || secondQuote < 0) return "";
            return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
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
        bw.Write(16);
        bw.Write((short)1); bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
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
}
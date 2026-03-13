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
    private AudioClip recordingClip;
    private int sampleRate = 16000;

    // 按鍵定義
    private readonly OVRInput.Button toggleButton = OVRInput.Button.Two; // 右手 B 鍵 (錄音)
    private readonly OVRInput.Button triggerButton = OVRInput.Button.PrimaryIndexTrigger; // 板機鍵 (重播)

    void Start()
    {
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        // --- 新增音量讀取邏輯 ---
        // 讀取 setting 場景存下來的音量 (預設值 0.5f)
        float savedVol = PlayerPrefs.GetFloat("GlobalVolume", 0.5f);

        // 套用讀取到的音量到 AudioSource
        audioSource.volume = savedVol;
        // -----------------------

        audioSource.loop = false;

        // 一啟動就播放語音
        if (openingSound != null)
        {
            audioSource.clip = openingSound;
            audioSource.Play();
        }
    }

    void Update()
    {
        // 1. 偵測板機鍵：重複播放開場語音
        if (OVRInput.GetDown(triggerButton))
        {
            if (!isRecording)
            {
                audioSource.Stop();
                if (openingSound != null)
                {
                    audioSource.clip = openingSound;
                    audioSource.Play();
                    Debug.Log("重播開場語音...");
                }
            }
        }

        // 2. 偵測錄音按鍵 (B 鍵)
        if (OVRInput.GetDown(toggleButton))
        {
            // 按下錄音鍵時，立即停止當前正在播放的任何音效
            if (audioSource.isPlaying)
            {
                audioSource.Stop();
                Debug.Log("偵測到錄音指令，停止當前播放音效");
            }

            if (!isRecording)
            {
                StartRecordingProcess();
            }
            else
            {
                _ = StopAndProcessVoiceAsync();
            }
        }
    }

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

    // --- 核心邏輯修改區塊 ---
    private void ProcessCommand(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        string t = text.ToLower();

        // 1. 導航功能
        if (t.Contains("執行程式") || t.Contains("開始導航") || t.Contains("執行導航") || t.Contains("開啟程式") || t.Contains("導航") || t.Contains("啟動導航"))
        {
            OnAButtonClick();
        }
        // 2. 介紹與說明功能
        else if (t.Contains("介紹說明") || t.Contains("怎麼使用") || t.Contains("教學") || t.Contains("說明文件") || t.Contains("介紹一下") || t.Contains("介紹") || t.Contains("說明") || t.Contains("導覽介面") || t.Contains("功能導覽"))
        {
            OnBButtonClick();
        }
        // 3. 【新增】進入 Setting 場景
        else if (t.Contains("設定") || t.Contains("調整") || t.Contains("配置") || t.Contains("偏好") || t.Contains("系統設定") || t.Contains("打開設定"))
        {
            OnSettingButtonClick();
        }
        // 4. 返回功能
        else if (t.Contains("返回") || t.Contains("回到首頁") || t.Contains("返回首頁") || t.Contains("離開") || t.Contains("結束程式"))
        {
            OnReturnButtonClick();
        }
        else if (t.Contains("返回") || t.Contains("回到首頁") || t.Contains("返回首頁") || t.Contains("離開") || t.Contains("結束程式"))
        {
            OnReturnBButtonClick();
        }
        else
        {
            Debug.Log("辨識到無關文字: " + text);
        }
    }

    public void OnAButtonClick() => SceneManager.LoadScene("Camera");
    public void OnBButtonClick() => SceneManager.LoadScene("userUI");
    public void OnSettingButtonClick() => SceneManager.LoadScene("setting");
    public void OnReturnButtonClick() => SceneManager.LoadScene("menu");
    public void OnReturnBButtonClick() => SceneManager.LoadScene("menu");


    // --- 工具函式 (Whisper API & Wav 轉換) 保持不變 ---

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
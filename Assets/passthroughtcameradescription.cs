using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using TMPro;
using Meta.XR;
using UnityEngine.Rendering;

public class PassthroughBlindGuide : MonoBehaviour
{
    [Header("References")]
    public PassthroughCameraAccess cameraAccess;
    public OpenAIConfiguration configuration;
    public TextMeshProUGUI resultText;

    [Header("Debug UI")]
    public TextMeshProUGUI yoloStatusText;

    [Header("OpenAI 設定")]
    public string openAIKey = "您的API_KEY";
    public int targetAiWidth = 512;

    private readonly OVRInput.Button captureButton = OVRInput.Button.PrimaryIndexTrigger;
    private bool isCapturing = false;

    void Start()
    {
        if (resultText != null) resultText.text = "扣下板機開始環境掃描";
    }

    void Update()
    {
        if (OVRInput.GetDown(captureButton, OVRInput.Controller.RTouch) ||
            OVRInput.GetDown(captureButton, OVRInput.Controller.LTouch))
        {
            StartCoroutine(CaptureAndProcessRoutine());
        }
    }

    #region YOLO 語音處理 (含 GPT 翻譯)

    /// <summary>
    /// 由 DetectionManager 呼叫，傳入 YOLO 偵測到的標籤字串
    /// </summary>
    public void Speak(string englishLabels)
    {
        if (string.IsNullOrEmpty(englishLabels)) return;

        Debug.Log("<color=cyan>[YOLO 觸發]</color>: " + englishLabels);
        _ = ProcessYoloSpeechAsync(englishLabels);
    }

    private async Task ProcessYoloSpeechAsync(string labels)
    {
        // 修改後的優先預警 Prompt
        string prompt = $@"你是一位極其專業的視障者安全導引助理。
當前偵測標籤：{labels}

請根據以下優先順序產出一句自然的中文警示：
1. 【最優先 - 移動威脅】：若包含汽機車 (car, motorbike, bus, truck) 或行人 (person)，必須用『警告』語氣，提醒注意來車或避讓。
2. 【次優先 - 行走障礙】：若包含家具 (chair, diningtable)、設施 (bench, pottedplant) 或號誌 (stop sign)，提醒注意前方障礙。
3. 【低優先 - 隨身物品】：若只有小物件 (cup, phone, book)，則用『發現』語氣簡單描述即可。

規則：
- 根據照片，以照片的中心點，分出左右兩側，以我畫面中的樣子作為辨識物體的位置並左右相反，我給你的照片視角可能跟我視角是相反的，物體的位置方向都要說明清楚。
- 總字數控制在 20 字內。
- 若同時存在多類物體，『警告』類必須排在最前面。
- 範例：『警告，右前方有車子接近，請小心來車』或『注意右前方有長椅，請向左偏一點在直行』。";

        string finalWarning = await GetChatResponseAsync(prompt);

        if (string.IsNullOrEmpty(finalWarning)) return;

        // 顯示與 TTS 流程 (保持不變)
        MainThreadDispatcher.RunOnMainThread(() => {
            if (yoloStatusText != null)
                yoloStatusText.text = $"<color=orange>安全引導:</color>\n{finalWarning}";
        });

        byte[] ttsBytes = await TextToSpeechAsync(finalWarning);

        if (ttsBytes != null)
        {
            MainThreadDispatcher.RunOnMainThread(() =>
            {
                StartCoroutine(PlayAudioExclusiveCoroutine(ttsBytes, "yolo_label.mp3"));
            });
        }
    }

    private async Task<string> GetChatResponseAsync(string prompt)
    {
        try
        {
            var client = new OpenAIClient(new OpenAIAuthentication(openAIKey));
            var messages = new List<Message> { new Message(Role.User, prompt) };
            var chatReq = new ChatRequest(messages, model: "gpt-4o");
            var result = await client.ChatEndpoint.GetCompletionAsync(chatReq);
            return result?.FirstChoice?.Message?.Content?.ToString();
        }
        catch { return null; }
    }

    #endregion

    #region 環境分析 (GPT Vision)

    private IEnumerator CaptureAndProcessRoutine()
    {
        if (isCapturing) yield break;
        isCapturing = true;

        if (resultText != null) resultText.text = "正在掃描路況...";

        if (cameraAccess == null || !cameraAccess.IsPlaying)
        {
            isCapturing = false;
            yield break;
        }

        Texture gpuTexture = cameraAccess.GetTexture();
        if (gpuTexture == null)
        {
            isCapturing = false;
            yield break;
        }

        Texture2D readableText = ConvertToReadableTexture(gpuTexture, targetAiWidth);
        byte[] imageBytes = readableText.EncodeToJPG(75);
        Destroy(readableText);

        _ = ProcessBlindGuideAsync(imageBytes);
    }

    private async Task ProcessBlindGuideAsync(byte[] imageBytes)
    {
        try
        {
            string base64Image = Convert.ToBase64String(imageBytes);
            string dataUrl = $"data:image/jpeg;base64,{base64Image}";

            var client = new OpenAIClient(new OpenAIAuthentication(openAIKey));
            var messages = new List<Message>
            {
                new Message(Role.System, @"你是一位極其專業的視障者導引助理。
你的目標是：根據照片，以照片的中心點，分出左右兩側，以我畫面中的樣子作為辨識物體的位置並左右相反，我給你的照片視角可能跟我視角是相反的，再為正在行走的盲人提供關鍵、簡短、致命相關的安全建議。
重點關注：
1. 正前方的障礙物（電線桿、違停、階梯、行人、汽機車、柵欄）。
2. 地面的變化（導盲磚中斷、路面不平、積水）。
3. 交通信號（紅綠燈狀態、斑馬線位置）。
4. 其他可能影響行走安全的因素。

回覆規則：
- 必須以第一人稱提供建議。
- 極簡化：限制在 20 字內。
- 優先順序：如何行走躲避障礙物>危險障礙物 > 導航建議 > 環境描述。
- 範例:『前方3公尺人行道有機車，請向右偏移再直行』或『注意右前方有行人，請向左偏移一點再直行』。
- 語言：繁體中文。"),
                new Message(Role.User, new List<Content>
                {
                    new Content("掃描路況並給我一句話的指示。"),
                    new Content(new ImageUrl(dataUrl))
                })
            };

            var chatReq = new ChatRequest(messages, model: Model.GPT4o);
            var result = await client.ChatEndpoint.GetCompletionAsync(chatReq);
            string reply = result?.FirstChoice?.Message?.Content?.ToString() ?? "無法分析";

            MainThreadDispatcher.RunOnMainThread(async () => {
                if (resultText != null) resultText.text = reply;
                byte[] ttsBytes = await TextToSpeechAsync(reply);
                if (ttsBytes != null)
                {
                    StartCoroutine(PlayAudioExclusiveCoroutine(ttsBytes, "gpt_analysis.mp3"));
                }
            });
        }
        catch (Exception ex)
        {
            Debug.LogError("AI 分析失敗: " + ex.Message);
        }
        finally
        {
            isCapturing = false;
        }
    }

    #endregion

    #region TTS 與 播放核心

    private async Task<byte[]> TextToSpeechAsync(string text)
    {
        string escapedText = JsonEscape(text);
        string json = $"{{\"model\":\"tts-1\",\"input\":\"{escapedText}\",\"voice\":\"alloy\"}}";
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest www = new UnityWebRequest("https://api.openai.com/v1/audio/speech", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(body);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAIKey);

            var op = www.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (www.result == UnityWebRequest.Result.Success) return www.downloadHandler.data;

            // 錯誤反饋
            string errorDetail = $"HTTP {www.responseCode}: {www.error}";
            MainThreadDispatcher.RunOnMainThread(() => {
                if (yoloStatusText != null) yoloStatusText.text = $"<color=red>TTS 失敗\n{errorDetail}</color>";
            });
            return null;
        }
    }

    private IEnumerator PlayAudioExclusiveCoroutine(byte[] audioBytes, string fileName)
    {
        // 加上時間戳記防止併發檔案讀寫衝突
        string uniqueName = Time.realtimeSinceStartup.ToString("F2").Replace(".", "_") + "_" + fileName;
        string path = Path.Combine(Application.temporaryCachePath, uniqueName);

        File.WriteAllBytes(path, audioBytes);

        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                if (GlobalVoiceManager.Instance != null)
                {
                    GlobalVoiceManager.Instance.EnqueueAudio(clip);
                }
            }
        }
    }

    #endregion

    private Texture2D ConvertToReadableTexture(Texture source, int targetWidth)
    {
        float aspect = (float)source.height / source.width;
        int targetHeight = (int)(targetWidth * aspect);
        RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, rt);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
        result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
        result.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        return result;
    }

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
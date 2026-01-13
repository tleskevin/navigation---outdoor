using System;
using System.Collections;
using System.Collections.Generic;
using System.IO; // 用於存檔
using System.Threading.Tasks;
using Meta.XR;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using UnityEngine;
using UnityEngine.Rendering;
using TMPro;

public class PassthroughCaptureAndUpload : MonoBehaviour
{
    [Header("References")]
    public PassthroughCameraAccess cameraAccess;
    public OpenAIConfiguration configuration;
    public TextMeshProUGUI resultText;
    public Texture2D fallbackTexture;
    public bool useWebcamInEditor = true;

    [Header("Settings")]
    [Tooltip("指定 Editor 下的存檔路徑")]
    public string editorSavePath = @"D:\unity\source\photo";

    [Tooltip("傳送給 AI 前是否要縮圖 (建議開啟以加速)")]
    public bool resizeForAI = true;
    [Tooltip("縮圖後的目標寬度")]
    public int targetAiWidth = 512;

    // internal
    private Texture2D capturedTexture;
#if UNITY_EDITOR
    private WebCamTexture webcam;
#endif
    private bool isCapturing = false;
    private readonly object captureLock = new object();

    void Start()
    {
#if UNITY_EDITOR
        // 確保指定的存檔資料夾存在
        if (!Directory.Exists(editorSavePath))
        {
            try { Directory.CreateDirectory(editorSavePath); }
            catch (Exception e) { Debug.LogError($"無法建立資料夾: {e.Message}"); }
        }

        if (useWebcamInEditor && WebCamTexture.devices.Length > 0)
        {
            webcam = new WebCamTexture();
            webcam.Play();
        }
#endif
    }

    void OnDisable()
    {
#if UNITY_EDITOR
        if (webcam != null && webcam.isPlaying) webcam.Stop();
#endif
    }

    void Update()
    {
        // 按 A 鍵拍照
        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            TryTakePicture();
        }
    }

    public void TryTakePicture()
    {
        lock (captureLock)
        {
            if (isCapturing) return;
            isCapturing = true;
        }

        if (resultText != null) resultText.text = "1. Capturing...";

        // 1. Quest Passthrough (真機模式)
        if (cameraAccess != null)
        {
            TakeFromPassthrough();
            return;
        }

#if UNITY_EDITOR
        // 2. Editor Webcam (電腦測試模式)
        if (webcam != null && webcam.isPlaying)
        {
            TakeFromWebcam();
            return;
        }
#endif
        // 3. Fallback (若無相機)
        if (fallbackTexture != null)
        {
            capturedTexture = new Texture2D(fallbackTexture.width, fallbackTexture.height, fallbackTexture.format, false);
            Graphics.CopyTexture(fallbackTexture, capturedTexture);
            _ = ProcessAndSubmitImageAsync();
            lock (captureLock) { isCapturing = false; }
            return;
        }

        lock (captureLock) { isCapturing = false; }
        if (resultText != null) resultText.text = "Error: No Camera";
    }

    // --- 拍照邏輯 ---
    private void TakeFromPassthrough()
    {
        try
        {
            Texture src = cameraAccess.GetTexture();
            if (src == null) { ResetCapture(); return; }
            StartCoroutine(AsyncCaptureRenderTextureFlow(src, cameraAccess.CurrentResolution.x, cameraAccess.CurrentResolution.y));
        }
        catch { ResetCapture(); }
    }

#if UNITY_EDITOR
    private void TakeFromWebcam()
    {
        try
        {
            int w = webcam.width; int h = webcam.height;
            if (capturedTexture == null || capturedTexture.width != w || capturedTexture.height != h)
                capturedTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);

            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0);
            Graphics.Blit(webcam, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            capturedTexture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            capturedTexture.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            _ = ProcessAndSubmitImageAsync().ContinueWith(_ => ResetCapture());
        }
        catch { ResetCapture(); }
    }
#endif

    private IEnumerator AsyncCaptureRenderTextureFlow(Texture src, int w, int h)
    {
        RenderTexture tempRT = RenderTexture.GetTemporary(w, h, 0);
        Graphics.Blit(src, tempRT);
        yield return null;

        AsyncGPUReadback.Request(tempRT, 0, TextureFormat.RGBA32, req =>
        {
            if (req.hasError) { ResetCapture(); return; }

            var data = req.GetData<Color32>();
            if (capturedTexture == null || capturedTexture.width != w || capturedTexture.height != h)
                capturedTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);

            capturedTexture.SetPixels32(data.ToArray());
            capturedTexture.Apply();

            _ = ProcessAndSubmitImageAsync();
            RenderTexture.ReleaseTemporary(tempRT);
            ResetCapture();
        });
    }

    private void ResetCapture() { lock (captureLock) { isCapturing = false; } }

    // --- 關鍵修改：處理圖片並發送給 OpenAI ---
    public async Task ProcessAndSubmitImageAsync()
    {
        if (capturedTexture == null) return;

        // 1. 存檔 (存原始大圖)
        byte[] originalPng = capturedTexture.EncodeToPNG();
        SaveImageToFixedPath(originalPng);

        if (resultText != null) resultText.text = "2. Processing...";

        // 2. 縮圖 & 轉 JPG (為了讓 OpenAI 讀取順利)
        Texture2D resizedTex = ResizeTexture(capturedTexture, targetAiWidth);
        byte[] finalBytesForAI = resizedTex.EncodeToJPG(70); // 壓縮品質 70
        Destroy(resizedTex); // 釋放記憶體

        if (configuration == null) return;

        try
        {
            if (resultText != null) resultText.text = "3. Sending to AI...";

            // 轉成 Base64
            string base64Image = Convert.ToBase64String(finalBytesForAI);
            string dataUrl = $"data:image/jpeg;base64,{base64Image}";

            var client = new OpenAIClient(configuration);

            // 【重點修正】：這裡明確告訴 OpenAI 哪部分是文字，哪部分是圖片
            var messages = new List<Message>
            {
                new Message(Role.System, "You are a helpful assistant."),
                new Message(Role.User, new List<Content>
                {
                    // 文字指令
                    new Content("Describe what is in this image briefly."),
                    
                    // 圖片數據 (正確建立 image_url 內容)
                    new Content(new ImageUrl(dataUrl))
                })
            };

            var chatReq = new ChatRequest(messages, model: Model.GPT4o);

            // 加入簡單的 Timeout 機制 (15秒)
            var task = client.ChatEndpoint.GetCompletionAsync(chatReq);
            if (await Task.WhenAny(task, Task.Delay(15000)) == task)
            {
                var result = await task;
                string reply = result?.FirstChoice?.Message?.Content?.ToString() ?? "No response";
                Debug.Log("AI Reply: " + reply);

                // 回到 Unity UI 顯示結果
                if (resultText != null) resultText.text = reply;
            }
            else
            {
                if (resultText != null) resultText.text = "Error: Timeout";
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("OpenAI Error: " + ex.Message);
            if (resultText != null) resultText.text = "Error: " + ex.Message;
        }
    }

    // 存檔功能
    private void SaveImageToFixedPath(byte[] bytes)
    {
        string folder = editorSavePath;
#if !UNITY_EDITOR
        folder = Application.persistentDataPath; // Quest 上改用內部儲存
#endif
        try
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            string filename = $"Photo_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            File.WriteAllBytes(Path.Combine(folder, filename), bytes);
            Debug.Log($"<color=green>[Saved]</color> {filename}");
        }
        catch (Exception e) { Debug.LogError($"Save Failed: {e.Message}"); }
    }

    // 縮圖功能
    private Texture2D ResizeTexture(Texture2D source, int targetWidth)
    {
        float aspect = (float)source.height / source.width;
        int targetHeight = (int)(targetWidth * aspect);
        RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0);
        Graphics.Blit(source, rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
        result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
        result.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return result;
    }
}
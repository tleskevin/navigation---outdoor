using UnityEngine;
using TMPro;
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using System.Text;
using System.Threading.Tasks;

public class VoiceNavigationHandler : MonoBehaviour
{
    public MapDataLoader mapDataLoader;
    public TextMeshProUGUI statusText;

    [Header("API Keys")]
    public string openAIKey = "YOUR_OPENAI_KEY";
    public string googleApiKey = "YOUR_GOOGLE_KEY";

    private readonly OVRInput.Button navTrigger = OVRInput.Button.One;
    private bool isRecording = false;
    private AudioClip recordingClip;
    private int sampleRate = 16000;

    void Update()
    {
        if (OVRInput.GetDown(navTrigger, OVRInput.Controller.RTouch)) StartRecording();
        if (OVRInput.GetUp(navTrigger, OVRInput.Controller.RTouch)) StopAndProcess();
    }

    private void StartRecording()
    {
        if (isRecording || Microphone.devices.Length == 0) return;
        isRecording = true;
        OVRInput.SetControllerVibration(0.3f, 0.3f, OVRInput.Controller.RTouch);
        statusText.text = "🎤 正在聆聽指令...";
        recordingClip = Microphone.Start("", false, 10, sampleRate);
    }

    private async void StopAndProcess()
    {
        Microphone.End("");
        isRecording = false;
        OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
        statusText.text = "🧠 正在分析...";
        byte[] wav = AudioClipToWav(recordingClip);
        string transcript = await SendToWhisperAsync(wav);
        if (string.IsNullOrEmpty(transcript)) { Speak("沒聽清楚。"); return; }
        string keyword = await ExtractKeywordWithGPT(transcript);
        if (keyword.Contains("INVALID")) { Speak("請問您要去哪裡？"); return; }
        await SearchNearestPlace(keyword);
    }

    public async void Speak(string message)
    {
        byte[] ttsBytes = await TextToSpeechAsync(message);
        if (ttsBytes != null)
        {
            UnityMainThreadDispatcher.Instance().Enqueue(() => {
                StartCoroutine(PlayAudioThroughManager(ttsBytes, "nav_tts.mp3"));
            });
        }
    }

    private async Task<byte[]> TextToSpeechAsync(string text)
    {
        string json = "{\"model\":\"tts-1\",\"input\":\"" + text + "\",\"voice\":\"alloy\"}";
        using (UnityWebRequest www = new UnityWebRequest("https://api.openai.com/v1/audio/speech", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAIKey);
            var op = www.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            return www.downloadHandler.data;
        }
    }

    private IEnumerator PlayAudioThroughManager(byte[] bytes, string fileName)
    {
        string path = Path.Combine(Application.temporaryCachePath, fileName);
        File.WriteAllBytes(path, bytes);
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG))
        {
            yield return www.SendWebRequest();
            if (www.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                if (GlobalVoiceManager.Instance != null)
                    GlobalVoiceManager.Instance.PlayPriorityAudio(clip);
            }
        }
    }

    // --- Whisper/GPT 工具維持不變 ---
    private async Task<string> SendToWhisperAsync(byte[] wav)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("file", wav, "audio.wav", "audio/wav");
        form.AddField("model", "whisper-1");
        using (UnityWebRequest req = UnityWebRequest.Post("https://api.openai.com/v1/audio/transcriptions", form))
        {
            req.SetRequestHeader("Authorization", "Bearer " + openAIKey);
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            return SimpleJsonExtract(req.downloadHandler.text, "text");
        }
    }

    private async Task<string> ExtractKeywordWithGPT(string text)
    {
        string payload = "{\"model\":\"gpt-4o-mini\",\"messages\":[{\"role\":\"system\",\"content\":\"提取目的地。\"},{\"role\":\"user\",\"content\":\"" + text + "\"}]}";
        using (UnityWebRequest www = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAIKey);
            var op = www.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            return SimpleJsonExtract(Encoding.UTF8.GetString(www.downloadHandler.data), "content");
        }
    }

    private string SimpleJsonExtract(string json, string key)
    {
        int idx = json.IndexOf($"\"{key}\"");
        if (idx < 0) return "";
        int f = json.IndexOf('"', idx + key.Length + 2);
        int s = json.IndexOf('"', f + 1);
        return (f < 0 || s < 0) ? "" : json.Substring(f + 1, s - f - 1);
    }

    private async Task SearchNearestPlace(string keyword)
    {
        string url = $"https://maps.googleapis.com/maps/api/place/nearbysearch/json?location={mapDataLoader.currentLat},{mapDataLoader.currentLng}&keyword={UnityWebRequest.EscapeURL(keyword)}&rankby=distance&key={googleApiKey}";
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (req.result == UnityWebRequest.Result.Success)
            {
                string json = req.downloadHandler.text;
                string id = SimpleJsonExtract(json, "place_id");
                string nm = SimpleJsonExtract(json, "name");
                if (!string.IsNullOrEmpty(id)) mapDataLoader.StartNavigationTo(id, nm);
                else Speak($"找不到附近的 {keyword}。");
            }
        }
    }

    private byte[] AudioClipToWav(AudioClip clip)
    {
        float[] samples = new float[clip.samples]; clip.GetData(samples, 0);
        MemoryStream ms = new MemoryStream(); BinaryWriter bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + samples.Length * 2);
        bw.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sampleRate); bw.Write(sampleRate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(samples.Length * 2);
        foreach (float f in samples) bw.Write((short)Mathf.Clamp(f * 32767f, short.MinValue, short.MaxValue));
        return ms.ToArray();
    }
}
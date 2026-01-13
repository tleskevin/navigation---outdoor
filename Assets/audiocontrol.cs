using UnityEngine;
using TMPro;
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;

// ABC_Play_Record_STT_WithTTS
// - 按 B -> 播 ABC -> 播完自動錄音 -> 再次按 B 停止並上傳 Whisper -> 取得 transcript
// - 把 transcript 送 Chat -> 顯示文字 (Transcript + GPT) 並用 TTS 產生音訊播放 GPT 回覆
public class ABC_Play_Record_STT : MonoBehaviour
{
    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip audioClipABC;

    [Header("UI")]
    public TextMeshProUGUI resultText;

    [Header("OpenAI API")]
    [Tooltip("測試用放這裡即可，但生產環境請改用 server proxy")]
    public string openAIKey = "YOUR_API_KEY_HERE";

    private readonly OVRInput.Button toggleButton = OVRInput.Button.Two;

    private bool isPlayingABC = false;
    private bool isRecording = false;

    private AudioClip recordingClip;
    private int sampleRate = 16000;

    void Start()
    {
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.loop = false;
        if (resultText != null) resultText.text = "Press B to ask a question";
    }

    void Update()
    {
        if (OVRInput.GetDown(toggleButton))
        {
            if (!isPlayingABC && !isRecording)
            {
                PlayABC();
            }
            else if (isRecording)
            {
                StopRecordingAndProcessAsync().ConfigureAwait(false);
            }
        }
    }

    // --------------------------------------
    // Step 1：播放 ABC
    // --------------------------------------
    private void PlayABC()
    {
        if (audioClipABC == null)
        {
            Debug.LogError("音檔 ABC 尚未指定！");
            return;
        }

        audioSource.clip = audioClipABC;
        audioSource.Play();
        isPlayingABC = true;

        if (resultText != null) resultText.text = "Playing ABC...";

        StartCoroutine(CheckABCFinished());
    }

    private IEnumerator CheckABCFinished()
    {
        while (audioSource.isPlaying)
            yield return null;

        isPlayingABC = false;
        StartRecording();
    }

    // --------------------------------------
    // Step 2：錄音開始
    // --------------------------------------
    private void StartRecording()
    {
        if (resultText != null) resultText.text = "Recording... press B to stop.";

        recordingClip = Microphone.Start("", false, 20, sampleRate);
        isRecording = true;
    }

    // --------------------------------------
    // Step 3：停止錄音 → STT → Chat → 顯示 + TTS 播放
    // --------------------------------------
    private async Task StopRecordingAndProcessAsync()
    {
        Microphone.End("");
        isRecording = false;

        if (resultText != null) resultText.text = "Transcribing...";

        if (recordingClip == null)
        {
            Debug.LogError("Recording Clip is NULL");
            return;
        }

        byte[] wav = AudioClipToWav(recordingClip);

        // 1) Whisper STT
        string transcript = await SendToWhisperAsync(wav);
        if (string.IsNullOrEmpty(transcript))
        {
            resultText.text = "STT Error";
            return;
        }

        resultText.text = "Transcript:\n" + transcript + "\n\nGPT Thinking...";

        // 2) Send to Chat
        string gptReply = await SendToChatAsync(transcript);
        if (string.IsNullOrEmpty(gptReply))
        {
            resultText.text = "Transcript:\n" + transcript + "\n\nGPT: [No reply]";
            return;
        }

        // Display text
        resultText.text = "Transcript:\n" + transcript + "\n\nGPT:\n" + gptReply;

        // 3) TTS: request audio from OpenAI and play it
        byte[] ttsBytes = await TextToSpeechAsync(gptReply);
        if (ttsBytes != null && ttsBytes.Length > 0)
        {
            // Play via coroutine (handles WAV/MP3/OGG)
            StartCoroutine(PlayAudioBytesCoroutine(ttsBytes, audioSource));
        }
        else
        {
            Debug.LogWarning("TTS returned no audio bytes.");
        }
    }

    // --------------------------------------
    // Whisper STT (OpenAI)
    // --------------------------------------
    private async Task<string> SendToWhisperAsync(byte[] wavBytes)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("file", wavBytes, "audio.wav", "audio/wav");
        form.AddField("model", "whisper-1");

        using (UnityWebRequest req = UnityWebRequest.Post("https://api.openai.com/v1/audio/transcriptions", form))
        {
            req.SetRequestHeader("Authorization", "Bearer " + openAIKey);

            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string body = req.downloadHandler != null ? Encoding.UTF8.GetString(req.downloadHandler.data) : "";
                Debug.LogError("Whisper Error: " + req.error + " body: " + body);
                return "";
            }

            string json = Encoding.UTF8.GetString(req.downloadHandler.data);
            Debug.Log("[Whisper JSON] " + json);

            // Extract "text" field safely
            int idx = json.IndexOf("\"text\"");
            if (idx < 0) return "";
            int firstQuote = json.IndexOf('"', idx + 6);
            int secondQuote = json.IndexOf('"', firstQuote + 1);
            if (firstQuote < 0 || secondQuote < 0) return "";
            return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }
    }

    // --------------------------------------
    // ChatGPT (text) - returns assistant text
    // --------------------------------------
    private async Task<string> SendToChatAsync(string userMessage)
    {
        if (string.IsNullOrEmpty(openAIKey))
        {
            Debug.LogError("OpenAI key empty.");
            return "";
        }

        // Minimal chat payload
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

            if (www.result != UnityWebRequest.Result.Success)
            {
                string body = www.downloadHandler != null ? Encoding.UTF8.GetString(www.downloadHandler.data) : "";
                Debug.LogError("Chat API error: " + www.error + " body: " + body);
                return "";
            }

            string resp = Encoding.UTF8.GetString(www.downloadHandler.data);
            Debug.Log("[Chat JSON] " + resp);

            // try robust extraction (reuse ExtractAssistantContent pattern)
            string extracted = ExtractAssistantContent(resp);
            if (!string.IsNullOrEmpty(extracted)) return extracted.Trim();

            Debug.LogWarning("Failed to extract assistant content; returning raw.");
            return resp;
        }
    }

    // Robust extractor: try common patterns to find textual assistant content
    private string ExtractAssistantContent(string json)
    {
        if (string.IsNullOrEmpty(json)) return "";

        // Try typical "choices[0].message.content" (string) or "content":[{"type":"text","text":"..."}]
        int choicesIdx = json.IndexOf("\"choices\"");
        if (choicesIdx >= 0)
        {
            int messageIdx = json.IndexOf("\"message\"", choicesIdx);
            if (messageIdx >= 0)
            {
                int contentIdx = json.IndexOf("\"content\"", messageIdx);
                if (contentIdx >= 0)
                {
                    // if content is a string
                    int colon = json.IndexOf(':', contentIdx);
                    if (colon >= 0)
                    {
                        int pos = colon + 1;
                        while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
                        if (pos < json.Length && json[pos] == '"')
                        {
                            int start = pos + 1;
                            int end = json.IndexOf('"', start);
                            if (end > start) return JsonUnescape(json.Substring(start, end - start));
                        }
                        // otherwise try find "text": inside content
                        int textIdx = json.IndexOf("\"text\"", contentIdx);
                        if (textIdx >= 0)
                        {
                            int q1 = json.IndexOf('"', textIdx + 6);
                            int q2 = json.IndexOf('"', q1 + 1);
                            if (q1 >= 0 && q2 > q1) return JsonUnescape(json.Substring(q1 + 1, q2 - q1 - 1));
                        }
                    }
                }
            }
        }

        // fallback: search any "text":"..." occurrence likely to be assistant text
        int txt = json.IndexOf("\"text\"");
        if (txt >= 0)
        {
            int q1 = json.IndexOf('"', txt + 6);
            int q2 = json.IndexOf('"', q1 + 1);
            if (q1 >= 0 && q2 > q1) return JsonUnescape(json.Substring(q1 + 1, q2 - q1 - 1));
        }

        return "";
    }

    // --------------------------------------
    // Text-to-Speech (OpenAI): returns raw audio bytes
    // API: POST https://api.openai.com/v1/audio/speech  (model: gpt-4o-mini-tts)
    // Docs: OpenAI Text-to-Speech (Audio API). :contentReference[oaicite:1]{index=1}
    // --------------------------------------
    private async Task<byte[]> TextToSpeechAsync(string text)
    {
        if (string.IsNullOrEmpty(openAIKey)) return null;

        // Minimal request body; you can add voice, format params per OpenAI docs.
        string json = "{\"model\":\"gpt-4o-mini-tts\",\"input\":\"" + JsonEscape(text) + "\"}";
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest www = new UnityWebRequest("https://api.openai.com/v1/audio/speech", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(body);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + openAIKey);

            var op = www.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (www.result != UnityWebRequest.Result.Success)
            {
                string bodyText = www.downloadHandler != null ? Encoding.UTF8.GetString(www.downloadHandler.data) : "";
                Debug.LogError("TTS error: " + www.error + " body: " + bodyText);
                return null;
            }

            // return raw audio bytes (may be WAV/MP3/OGG depending on endpoint/settings)
            return www.downloadHandler.data;
        }
    }

    // --------------------------------------
    // Play audio bytes robustly (supports WAV/MP3/OGG fallback)
    // --------------------------------------
    private IEnumerator PlayAudioBytesCoroutine(byte[] audioBytes, AudioSource src)
    {
        if (audioBytes == null || audioBytes.Length == 0)
        {
            Debug.LogWarning("PlayAudioBytes: empty bytes.");
            yield break;
        }

        // Debug head
        Debug.Log($"PlayAudioBytes: length={audioBytes.Length}, head={audioBytes[0]:X2} {audioBytes[1]:X2} {audioBytes[2]:X2} {audioBytes[3]:X2}");

        bool isWav = audioBytes.Length >= 4 &&
                     audioBytes[0] == (byte)'R' && audioBytes[1] == (byte)'I' &&
                     audioBytes[2] == (byte)'F' && audioBytes[3] == (byte)'F';

        bool isOgg = audioBytes.Length >= 4 &&
                     audioBytes[0] == (byte)'O' && audioBytes[1] == (byte)'g' &&
                     audioBytes[2] == (byte)'g' && audioBytes[3] == (byte)'S';

        bool looksLikeMp3 = audioBytes.Length >= 3 &&
                            (audioBytes[0] == 0xFF && (audioBytes[1] & 0xE0) == 0xE0
                             || (audioBytes[0] == (byte)'I' && audioBytes[1] == (byte)'D' && audioBytes[2] == (byte)'3'));

        if (isWav)
        {
            // try parse in-memory WAV first
            AudioClip clip = WavFromBytes(audioBytes);
            if (clip != null)
            {
                src.clip = clip;
                src.Play();
                yield break;
            }
            else
            {
                Debug.LogWarning("In-memory WAV parse failed; falling back to file-based decode.");
            }
        }

        // fallback: write temp file and let Unity decode
        string ext = isOgg ? ".ogg" : (looksLikeMp3 ? ".mp3" : ".wav");
        string path = Path.Combine(Application.temporaryCachePath, "tts_temp" + ext);
        try
        {
            File.WriteAllBytes(path, audioBytes);
        }
        catch (Exception e)
        {
            Debug.LogError("Failed write tts temp file: " + e);
            yield break;
        }

        string url = "file://" + path;
        AudioType audioType = AudioType.WAV;
        if (ext == ".mp3") audioType = AudioType.MPEG;
        if (ext == ".ogg") audioType = AudioType.OGGVORBIS;

        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, audioType))
        {
            ((DownloadHandlerAudioClip)www.downloadHandler).streamAudio = false;
            var op = www.SendWebRequest();
            while (!op.isDone) yield return null;

#if UNITY_2020_1_OR_NEWER
            if (www.result != UnityWebRequest.Result.Success)
#else
            if (www.isNetworkError || www.isHttpError)
#endif
            {
                Debug.LogError("UnityWebRequestMultimedia failed: " + www.error + " ; text: " + (www.downloadHandler != null ? www.downloadHandler.text : ""));
            }
            else
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                if (clip == null)
                {
                    Debug.LogError("Decoded clip is null");
                }
                else
                {
                    src.clip = clip;
                    src.Play();
                }
            }
        }

        // optional: clean up temp file later if desired
        // File.Delete(path);
    }

    // --------------------------------------
    // Helper: Try to parse WAV (PCM16 little-endian) to AudioClip
    // Returns null on parse failure
    // --------------------------------------
    private AudioClip WavFromBytes(byte[] wavBytes)
    {
        try
        {
            using (MemoryStream ms = new MemoryStream(wavBytes))
            using (BinaryReader br = new BinaryReader(ms))
            {
                string riff = new string(br.ReadChars(4));
                if (riff != "RIFF") { Debug.LogWarning("Not RIFF"); return null; }
                br.ReadInt32(); // file size
                string wave = new string(br.ReadChars(4));
                if (wave != "WAVE") { Debug.LogWarning("Not WAVE"); return null; }

                int channels = 1;
                int sampleRateLocal = 16000;
                int bitDepth = 16;
                int dataSize = 0;

                while (br.BaseStream.Position < br.BaseStream.Length)
                {
                    string chunkId = new string(br.ReadChars(4));
                    int chunkSize = br.ReadInt32();
                    if (chunkId == "fmt ")
                    {
                        short audioFormat = br.ReadInt16();
                        channels = br.ReadInt16();
                        sampleRateLocal = br.ReadInt32();
                        br.ReadInt32(); // byte rate
                        br.ReadInt16(); // block align
                        bitDepth = br.ReadInt16();
                        if (chunkSize > 16) br.ReadBytes(chunkSize - 16);
                    }
                    else if (chunkId == "data")
                    {
                        dataSize = chunkSize;
                        break;
                    }
                    else
                    {
                        br.ReadBytes(chunkSize);
                    }
                }

                if (dataSize == 0) { Debug.LogWarning("No data chunk"); return null; }

                int sampleCount = dataSize / (bitDepth / 8);
                float[] samples = new float[sampleCount];
                if (bitDepth == 16)
                {
                    for (int i = 0; i < sampleCount; i++)
                    {
                        short val = br.ReadInt16();
                        samples[i] = val / 32768f;
                    }
                }
                else
                {
                    Debug.LogWarning("Unsupported bit depth: " + bitDepth);
                    return null;
                }

                int channelsOut = channels;
                int samplesPerChannel = sampleCount / channelsOut;
                AudioClip audioClip = AudioClip.Create("tts", samplesPerChannel, channelsOut, sampleRateLocal, false);

                // If interleaved, set data directly
                audioClip.SetData(samples, 0);
                return audioClip;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("WavFromBytes error: " + e);
            return null;
        }
    }

    // --------------------------------------
    // Utility helpers
    // --------------------------------------
    private static string JsonEscape(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    private static string JsonUnescape(string s)
    {
        if (s == null) return "";
        return s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    // --------------------------------------
    // WAV 編碼 (16-bit PCM)
    // --------------------------------------
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
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);

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
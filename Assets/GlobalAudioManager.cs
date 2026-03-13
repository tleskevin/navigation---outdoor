using UnityEngine;
using System.Collections.Generic;

public class GlobalVoiceManager : MonoBehaviour
{
    public static GlobalVoiceManager Instance;
    private AudioSource _audioSource;

    // 語音隊列：用來存放排隊中的音訊
    private Queue<AudioClip> _audioQueue = new Queue<AudioClip>();

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject); // 確保切換場景也不會消失
        }
        else { Destroy(gameObject); return; }

        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null) _audioSource = gameObject.AddComponent<AudioSource>();

        _audioSource.playOnAwake = false;
        _audioSource.spatialBlend = 0f;
    }

    void Update()
    {
        // 自動播放邏輯：如果沒在播，且隊列裡有東西，就播下一個
        if (!_audioSource.isPlaying && _audioQueue.Count > 0)
        {
            PlayNext();
        }
    }

    /// <summary>
    /// 排隊播放：誰先傳進來就先排隊，不會打斷正在播放的聲音
    /// </summary>
    public void EnqueueAudio(AudioClip clip)
    {
        if (clip == null) return;
        _audioQueue.Enqueue(clip);
    }

    private void PlayNext()
    {
        if (_audioQueue.Count > 0)
        {
            _audioSource.clip = _audioQueue.Dequeue();
            _audioSource.Play();
        }
    }

    /// <summary>
    /// 強制優先播放 (保留給導航使用)：這會清空隊列並立刻播放
    /// </summary>
    public void PlayPriorityAudio(AudioClip clip)
    {
        _audioQueue.Clear(); // 清空排隊
        if (_audioSource.isPlaying) _audioSource.Stop();

        _audioSource.clip = clip;
        _audioSource.Play();
    }
}
using UnityEngine;
using TMPro; // 使用 TextMeshPro 必備

public class VoiceInteractionHandler : MonoBehaviour
{
    [Header("UI 文字設定")]
    public TextMeshProUGUI textTMP;     // 對應 Text (TMP)
    public TextMeshProUGUI textTMP1;    // 對應 Text (TMP) (1)

    [Header("語音檔設定")]
    public AudioSource audioSource;     // 播放器
    public AudioClip voiceA;            // 第一段語音 (場景開始)
    public AudioClip voiceB;            // 按下 A 鍵語音
    public AudioClip voiceC;            // 按下板機鍵語音

    void Start()
    {
        // 1. 一開始隱藏所有文字
        if (textTMP != null) textTMP.gameObject.SetActive(false);
        if (textTMP1 != null) textTMP1.gameObject.SetActive(false);

        // 2. 進入場景播放語音 A
        PlayVoice(voiceA);
    }

    void Update()
    {
        // 偵測右邊手把的 A 鍵
        // OVRInput.Button.One 通常對應右手的 A 鍵
        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            Debug.Log("偵測到手把 A 鍵按下");
            HandleActionA();
        }

        // 偵測右邊手把的 板機鍵 (Trigger)
        // OVRInput.Button.PrimaryIndexTrigger 對應食指位置的板機
        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger))
        {
            Debug.Log("偵測到手把板機鍵按下");
            HandleActionB();
        }
    }

    // 處理按下 A 鍵的邏輯
    public void HandleActionA()
    {
        PlayVoice(voiceB);

        // 顯示文字 Text(TMP)，隱藏 Text(TMP)1
        if (textTMP != null) textTMP.gameObject.SetActive(true);
        if (textTMP1 != null) textTMP1.gameObject.SetActive(false);
    }

    // 處理按下板機鍵的邏輯
    public void HandleActionB()
    {
        PlayVoice(voiceC);

        // 隱藏文字 Text(TMP)，顯示 Text(TMP)1
        if (textTMP != null) textTMP.gameObject.SetActive(false);
        if (textTMP1 != null) textTMP1.gameObject.SetActive(true);
    }

    // 核心播放方法：確保新語音會中斷舊語音
    private void PlayVoice(AudioClip clip)
    {
        if (audioSource != null && clip != null)
        {
            if (audioSource.isPlaying)
            {
                audioSource.Stop(); // 中斷目前語音
            }
            audioSource.clip = clip;
            audioSource.Play();
        }
    }
}
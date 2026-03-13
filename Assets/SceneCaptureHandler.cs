using UnityEngine;

public class SceneCaptureHandler : MonoBehaviour
{
    // 按下 A 鍵時呼叫，強迫喚起系統介面
    public void RequestSceneCapture()
    {
        // 修正：您的 SDK 版本要求引數為 out ulong
        ulong jobId;
        if (OVRPlugin.RequestSceneCapture(out jobId))
        {
            Debug.Log($"[系統掃描] 已發起請求，JobID: {jobId}");
        }
        else
        {
            Debug.LogWarning("[系統掃描] 無法發起掃描請求。");
        }
    }
}
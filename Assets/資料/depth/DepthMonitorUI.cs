using UnityEngine;
using TMPro;

public class DepthMonitorUI : MonoBehaviour
{
    public TextMeshProUGUI statusText;
    private int currentPointCount = 0;

    public void UpdatePointCount(int count) { currentPointCount = count; }

    void Update()
    {
        Texture depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
        if (depthTex != null)
        {
            statusText.text = $"<color=green>深度圖狀態：已連線</color>\n" +
                              $"尺寸: {depthTex.width}x{depthTex.height}\n" +
                              $"類型: {depthTex.dimension}\n" +
                              $"<b>點雲數量: {currentPointCount:N0}</b>\n" +
                              $"時間: {Time.time:F2}";
        }
    }
}
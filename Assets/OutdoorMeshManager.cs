using UnityEngine;
using Meta.XR.MRUtilityKit;
using TMPro;

public class OutdoorSmartScanner : MonoBehaviour
{
    [Header("掃描設定")]
    public float CullDistance = 4.0f;           // 網格顯示半徑 (4公尺外自動隱藏)
    public float MoveRefreshThreshold = 2.0f;  // 每走 2 公尺自動全量同步一次
    public Material MeshMaterial;               // 拖入您的 RoomBox1 材質

    [Header("測試者 UI")]
    public TextMeshProUGUI DebugText;

    private Vector3 _lastRefreshPos;
    private float _updateTimer = 0f;

    void Start()
    {
        _lastRefreshPos = Camera.main.transform.position;
        // 啟動即執行一次載入
        RefreshEnvironment();
    }

    void Update()
    {
        // 1. 保留手動 A 鍵：徹底強制刷新
        if (OVRInput.GetDown(OVRInput.RawButton.A))
        {
            var handler = Object.FindFirstObjectByType<SceneCaptureHandler>();
            if (handler != null) handler.RequestSceneCapture();
            RefreshEnvironment();
        }

        // 2. 自動偵測位移：實現「走到哪長到哪」，無需盲人操作
        if (Vector3.Distance(Camera.main.transform.position, _lastRefreshPos) > MoveRefreshThreshold)
        {
            RefreshEnvironment();
            _lastRefreshPos = Camera.main.transform.position;
        }

        // 3. 背景持續更新 (每 0.5 秒靜默載入一次數據)
        _updateTimer += Time.deltaTime;
        if (_updateTimer >= 0.5f)
        {
            if (MRUK.Instance != null) MRUK.Instance.LoadSceneFromDevice();
            _updateTimer = 0f;
        }

        ProcessMeshManagement();
        UpdateStatsDisplay();
    }

    void RefreshEnvironment()
    {
        if (MRUK.Instance == null) return;
        // 關鍵：直接從設備深度感應器載入，不經過使用者點選
        MRUK.Instance.LoadSceneFromDevice();
    }

    void ProcessMeshManagement()
    {
        if (MRUK.Instance == null) return;
        var room = MRUK.Instance.GetCurrentRoom();
        if (room == null) return;

        Vector3 cameraPos = Camera.main.transform.position;

        foreach (var anchor in room.Anchors)
        {
            float distance = Vector3.Distance(cameraPos, anchor.transform.position);

            // 實施距離捨棄：這就是「丟掉舊網格」的邏輯
            if (distance > CullDistance)
            {
                if (anchor.gameObject.activeSelf) anchor.gameObject.SetActive(false);
            }
            else
            {
                if (!anchor.gameObject.activeSelf) anchor.gameObject.SetActive(true);

                // 動態優化材質：解決「一片綠」並強化深度感
                ApplyDepthEnhancement(anchor);
            }
        }
    }

    void ApplyDepthEnhancement(MRUKAnchor anchor)
    {
        var renderer = anchor.GetComponentInChildren<MeshRenderer>();
        if (renderer == null) return;

        // 針對不同標籤動態調整 Effect Angle，產生立體輪廓線
        if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR))
        {
            // 地板：較實心的綠色，較低的效果角
            renderer.material.color = new Color(0, 1, 0, 0.3f);
            renderer.material.SetFloat("_EffectAngle", 0.4f);
        }
        else if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
        {
            // 牆壁：半透明藍色，強化轉角
            renderer.material.color = new Color(0, 0, 1, 0.4f);
            renderer.material.SetFloat("_EffectAngle", 0.75f);
        }
        else
        {
            // GLOBAL_MESH (室外路面、雜物)：極淡綠色，極高效果角 (只留線條)
            renderer.material.color = new Color(0, 1, 0, 0.15f);
            renderer.material.SetFloat("_EffectAngle", 0.92f);
        }
    }

    void UpdateStatsDisplay()
    {
        if (DebugText == null) return;
        var room = MRUK.Instance.GetCurrentRoom();
        int count = (room != null) ? room.Anchors.Count : 0;

        DebugText.text = $"<color=green>[全自動盲人導航掃描]</color>\n" +
                         $"作用物件數: {count}\n" +
                         $"位移刷新: {Vector3.Distance(Camera.main.transform.position, _lastRefreshPos):F1}m\n" +
                         $"<color=cyan>藍:牆</color> | <color=green>綠:地</color>";
    }
}
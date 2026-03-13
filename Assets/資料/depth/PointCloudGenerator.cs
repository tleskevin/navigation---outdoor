using UnityEngine;
using UnityEngine.Rendering;

public class PointCloudGenerator : MonoBehaviour
{
    public ComputeShader depthShader;
    public Material pointMaterial;
    public Mesh pointMesh;
    public DepthMonitorUI uiMonitor;

    [Range(0.1f, 10f)] public float maxDistance = 5.0f;
    private const int textureSize = 320;

    private ComputeBuffer _pointBuffer;
    private ComputeBuffer _argsBuffer;
    private uint[] _args = new uint[5] { 0, 0, 0, 0, 0 };

    void Start()
    {
        _pointBuffer = new ComputeBuffer(textureSize * textureSize, sizeof(float) * 3);
        _argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        Debug.Log("<color=cyan>【初始化】Buffer 建立。解析度: 320x320</color>");
    }

    void Update()
    {
        Texture depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
        if (depthTex == null) return;

        int kernel = depthShader.FindKernel("CSMain");
        depthShader.SetTexture(kernel, "_EnvironmentDepthTexture", depthTex);
        depthShader.SetBuffer(kernel, "_PointCloudBuffer", _pointBuffer);
        depthShader.SetFloat("_MaxDistance", maxDistance);

        // 1. 傳入相機的世界轉換矩陣
        depthShader.SetMatrix("_LocalToWorldMatrix", Camera.main.cameraToWorldMatrix);

        // 2. 【關鍵】取得與目前眼部對齊的逆投影矩陣
        Matrix4x4 proj = GL.GetGPUProjectionMatrix(Camera.main.projectionMatrix, false);
        depthShader.SetMatrix("_InvProjectionMatrix", proj.inverse);

        depthShader.Dispatch(kernel, textureSize / 8, textureSize / 8, 1);

        // --- Console 診斷區域 ---
        if (Time.frameCount % 60 == 0)
        {
            float[] data = new float[3];
            _pointBuffer.GetData(data, 0, 0, 3);
            Vector3 pos = new Vector3(data[0], data[1], data[2]);
            Debug.Log($"<color=yellow>【座標診斷】點雲位置: {pos} | 距離你的頭: {Vector3.Distance(Camera.main.transform.position, pos):F2} 米</color>");
        }

        if (uiMonitor != null) uiMonitor.UpdatePointCount(textureSize * textureSize);

        if (pointMesh != null && pointMaterial != null)
        {
            _args[0] = pointMesh.GetIndexCount(0);
            _args[1] = (uint)(textureSize * textureSize);
            _argsBuffer.SetData(_args);
            pointMaterial.SetBuffer("_PointCloudBuffer", _pointBuffer);
            // 在世界空間渲染，Bounds 設大一點確保角落可見
            Graphics.DrawMeshInstancedIndirect(pointMesh, 0, pointMaterial,
                new Bounds(Vector3.zero, Vector3.one * 100f), _argsBuffer);
        }
    }

    void OnDestroy() { _pointBuffer?.Release(); _argsBuffer?.Release(); }
}
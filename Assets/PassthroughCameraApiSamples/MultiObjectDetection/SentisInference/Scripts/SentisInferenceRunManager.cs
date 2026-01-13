// Copyright (c) Meta Platforms, Inc. and affiliates.
using System;
using System.Collections;
using Meta.XR;
using Meta.XR.Samples;
using Unity.InferenceEngine;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class SentisInferenceRunManager : MonoBehaviour
    {
        [Header("Sentis Model config")]
        [SerializeField] private Vector2Int m_inputSize = new(640, 640);
        [SerializeField] private BackendType m_backend = BackendType.CPU;
        [SerializeField] private ModelAsset m_sentisModel;
        [SerializeField] private int m_layersPerFrame = 25;
        [SerializeField] private TextAsset m_labelsAsset;
        public bool IsModelLoaded { get; private set; } = false;

        [Header("UI display references")]
        [SerializeField] private SentisInferenceUiManager m_uiInference;

        [Header("[Editor Only] Convert to Sentis")]
        public ModelAsset OnnxModel;
        [SerializeField, Range(0, 1)] private float m_iouThreshold = 0.6f;
        [SerializeField, Range(0, 1)] private float m_scoreThreshold = 0.23f;
        [Space(40)]

        private Worker m_engine;
        private IEnumerator m_schedule;
        private bool m_started = false;
        private Tensor<float> m_input;
        private Model m_model;
        private int m_download_state = 0;
        private Tensor<float> m_output;
        private Tensor<int> m_labelIDs;
        private Tensor<float> m_pullOutput;
        private Tensor<int> m_pullLabelIDs;
        private bool m_isWaiting = false;
        private Pose m_imageCameraPose;

        #region Unity Functions
        private IEnumerator Start()
        {
            // Wait for the UI to be ready because when Sentis load the model it will block the main thread.
            yield return new WaitForSeconds(0.05f);

            m_uiInference.SetLabels(m_labelsAsset);
            LoadModel();
        }

        private void Update()
        {
            InferenceUpdate();
        }

        private void OnDestroy()
        {
            if (m_schedule != null)
            {
                StopCoroutine(m_schedule);
            }
            m_input?.Dispose();
            m_engine?.Dispose();
        }
        #endregion

        #region Public Functions
        public void RunInference(PassthroughCameraAccess cameraAccess)
        {
            // 如果推論尚未執行，準備輸入數據
            if (!m_started)
            {
                m_imageCameraPose = cameraAccess.GetCameraPose();

                // 清理上一次的輸入
                m_input?.Dispose();

                // 獲取當前 Passthrough 貼圖
                Texture targetTexture = cameraAccess.GetTexture();
                m_uiInference.SetDetectionCapture(targetTexture);

                // --- 修正部分：移除過時的 SetDimensions ---
                // 建立預設的轉換設定即可，Sentis 會自動對齊 m_input 的尺寸
                var textureTransform = new TextureTransform();

                // 定義目標 Tensor 形狀：(Batch, Channels, Height, Width)
                // 注意：通常 AI 模型輸入為 NCHW 格式 (1, 3, 高, 寬)
                m_input = new Tensor<float>(new TensorShape(1, 3, (int)m_inputSize.y, (int)m_inputSize.x));

                // 轉換貼圖為 Tensor，這會自動根據 m_input 的形狀進行 Resize
                TextureConverter.ToTensor(targetTexture, m_input, textureTransform);

                // 排程執行推論
                m_schedule = m_engine.ScheduleIterable(m_input);
                m_download_state = 0;
                m_started = true;
            }
        }

        public bool IsRunning()
        {
            return m_started;
        }
        #endregion

        #region Inference Functions
        private void LoadModel()
        {
            // 1. 加載模型資產
            var model = ModelLoader.Load(m_sentisModel);
            Debug.Log($"Sentis model loaded correctly with iouThreshold: {m_iouThreshold} and scoreThreshold: {m_scoreThreshold}");

            // 2. 建立推論引擎 (Worker)
            m_engine = new Worker(model, m_backend);

            // 3. 準備一張空白貼圖進行預熱 (Warm-up)
            // 建立一個空的 Texture2D
            Texture2D m_loadingTexture = new Texture2D(m_inputSize.x, m_inputSize.y, TextureFormat.RGBA32, false);

            // --- 修正部分：移除 SetDimensions ---
            // 直接建立預設的 TextureTransform 即可
            var textureTransform = new TextureTransform();

            // 定義 Tensor 形狀 (Batch, Channels, Height, Width)
            // 注意：如果模型是 NCHW 格式，通常順序是 (1, 3, y, x)
            m_input = new Tensor<float>(new TensorShape(1, 3, m_inputSize.y, m_inputSize.x));

            // 將空白貼圖轉換為 Tensor (Sentis 會自動對齊 m_input 的尺寸)
            TextureConverter.ToTensor(m_loadingTexture, m_input, textureTransform);

            // 4. 執行一次空推論，讓模型加載到內存/顯存中
            m_engine.Schedule(m_input);

            // 標記加載完成
            IsModelLoaded = true;

            // 釋放預熱用的臨時貼圖資源
            Destroy(m_loadingTexture);
        }

        private void InferenceUpdate()
        {
            // Run the inference layer by layer to not block the main thread.
            if (m_started)
            {
                try
                {
                    if (m_download_state == 0)
                    {
                        var it = 0;
                        while (m_schedule.MoveNext())
                        {
                            if (++it % m_layersPerFrame == 0)
                                return;
                        }
                        m_download_state = 1;
                    }
                    else
                    {
                        // Get the result once all layers are processed
                        GetInferencesResults();
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Sentis error: {e.Message}");
                }
            }
        }

        private void PollRequestOuput()
        {
            // Get the output 0 (coordinates data) from the model output using Sentis pull request.
            m_pullOutput = m_engine.PeekOutput(0) as Tensor<float>;
            if (m_pullOutput.dataOnBackend != null)
            {
                m_pullOutput.ReadbackRequest();
                m_isWaiting = true;
            }
            else
            {
                Debug.LogError("Sentis: No data output m_output");
                m_download_state = 4;
            }
        }

        private void PollRequestLabelIDs()
        {
            // Get the output 1 (labels ID data) from the model output using Sentis pull request.
            m_pullLabelIDs = m_engine.PeekOutput(1) as Tensor<int>;
            if (m_pullLabelIDs.dataOnBackend != null)
            {
                m_pullLabelIDs.ReadbackRequest();
                m_isWaiting = true;
            }
            else
            {
                Debug.LogError("Sentis: No data output m_labelIDs");
                m_download_state = 4;
            }
        }

        private void GetInferencesResults()
        {
            // Get the different outputs in diferent frames to not block the main thread.
            switch (m_download_state)
            {
                case 1:
                    if (!m_isWaiting)
                    {
                        PollRequestOuput();
                    }
                    else
                    {
                        if (m_pullOutput.IsReadbackRequestDone())
                        {
                            m_output = m_pullOutput.ReadbackAndClone();
                            m_isWaiting = false;

                            if (m_output.shape[0] > 0)
                            {
                                Debug.Log("Sentis: m_output ready");
                                m_download_state = 2;
                            }
                            else
                            {
                                Debug.Log("Sentis: m_output empty");
                                m_download_state = 4;
                            }
                        }
                    }
                    break;
                case 2:
                    if (!m_isWaiting)
                    {
                        PollRequestLabelIDs();
                    }
                    else
                    {
                        if (m_pullLabelIDs.IsReadbackRequestDone())
                        {
                            m_labelIDs = m_pullLabelIDs.ReadbackAndClone();
                            m_isWaiting = false;

                            if (m_labelIDs.shape[0] > 0)
                            {
                                Debug.Log("Sentis: m_labelIDs ready");
                                m_download_state = 3;
                            }
                            else
                            {
                                Debug.LogError("Sentis: m_labelIDs empty");
                                m_download_state = 4;
                            }
                        }
                    }
                    break;
                case 3:
                    m_uiInference.DrawUIBoxes(m_output, m_labelIDs, m_inputSize.x, m_inputSize.y, m_imageCameraPose);
                    m_download_state = 5;
                    break;
                case 4:
                    m_uiInference.OnObjectDetectionError();
                    m_download_state = 5;
                    break;
                case 5:
                    m_download_state++;
                    m_started = false;
                    m_output?.Dispose();
                    m_labelIDs?.Dispose();
                    break;
            }
        }
        #endregion
    }
}

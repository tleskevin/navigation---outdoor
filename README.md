# 🕶️ Smart Navigation Glasses (瞳行者)

這是一款基於 Unity 開發的虛擬實境/混合實境 (VR/MR) 導航系統。使用者可以透過語音輸入目的地，系統將自動分析語意、搜尋地點，並提供即時的步行導航資訊與語音進度報告。

## 🌟 核心功能

- **🎙️ 語音直覺操控**：按住手把按鍵即可說出目的地（整合 OpenAI Whisper）。
- **🧠 語意目的地提取**：自動辨識語音中的地點關鍵字（整合 OpenAI GPT-4o-mini）。
- **📍 精準導航系統**：
  - 使用 **Google Roads API** 修正 GPS 座標至道路上（Snap to Road）。
  - 使用 **Google Directions API** 規劃最佳步行路徑。
  - 使用 **Google Places API** 搜尋鄰近目標。
- **🔊 智慧語音導引**：
  - 出發時自動播報預計時間與距離。
  - 導航過程中每 15 秒自動報告剩餘進度。
  - 抵達目的地時播放音效並語音提醒。
- **📊 即時 UI 面板**：在視野中顯示經緯度、剩餘距離、目標名稱與系統同步狀態。

🚀 AI 技術貢獻說明 (AI Technical Contribution)
本專案「瞳行者」核心開發目標在於整合多項先進 人工智慧 (Artificial Intelligence) 技術，解決傳統導航系統在穿戴式裝置上的交互痛點。以下為本專案之 AI 技術貢獻要點：

1. 多模態語音交互系統 (Speech AI)
Whisper 語音辨識：捨棄傳統關鍵字觸發，整合 OpenAI Whisper (Large-v3) 模型，實現高準確度的語音轉文字 (STT)，即使在戶外噪音環境下也能精準捕捉使用者指令。

TTS 語音反饋：透過 OpenAI TTS (Text-to-Speech) 提供自然的人聲導引，強化使用者在步行過程中的沉浸感與安全感。

2. 自然語言處理與意圖分析 (NLP & LLM)
GPT-4 語意理解：利用 GPT-4o-mini 模型作為系統中樞，將使用者的自然語言輸入轉化為結構化數據（如：提取目的地、判斷緊急程度）。

動態提示詞工程 (Prompt Engineering)：開發特定的 System Prompt，確保模型能精確從長句中提取 Place ID 關鍵字，並過濾無效指令。

3. 計算機視覺與物體偵測 (Computer Vision)
YOLO 即時邊緣運算：系統預留 YOLO (You Only Look Once) 偵測接口，用於在導航過程中進行即時障礙物辨識與路口紅綠燈狀態偵測，將 AI 視覺從單純的影像記錄提升至主動避障層次。

4. AI 驅動的決策邏輯
智慧路徑優化：結合 Google API 數據與 AI 邏輯，系統能自動判斷導航狀態，並在 15 秒的時間間隔內自動進行進度彙報，實現真正的「免動手 (Hands-free)」導航體驗。

## 🛠️ 技術層

- **引擎**: Unity 2022.3+
- **硬體支援**: Meta Quest 系列 (使用 OVRInput)
- **API 整合**:
  - OpenAI API (Whisper & GPT)
  - Google Maps Platform (Directions, Roads, Places)
- **UI 系統**: TextMeshPro

## 🚀 快速開始

### 1. 環境設定
確保您的 Unity 專案已安裝以下 Package：
- `Meta-All-in-one`
- `Oculus Integration` (Meta Interaction SDK)
- `TextMeshPro`

### 2. API 金鑰配置
在場景中找到 `MapDataLoader`、 `VoiceBottonTrigger` 與 `VoiceNavigationHandler` 物件，並在 Inspector 視窗填入您的金鑰：
- **Google API Key**: 需開啟 Directions, Roads, Places 權限。
- **OpenAI API Key**: 用於語音轉文字與語意分析。

### 3. 操作方式
1. **啟動**：戴上頭盔並執行場景。
2. **語音指令**：
   - 按住 **右側手把 A 鍵 (Button.One)**。
   - 說出：「我要去台北車站」或「帶我去最近的便利商店」。
   - 放開按鍵，系統開始規劃。
3. **導航**：跟著 UI 上的距離提示前進，系統會自動更新路徑。

## 📂 程式碼結構

- `MapDataLoader.cs`: 負責處理導航邏輯、路徑計算與 UI 更新。
- `VoiceNavigationHandler.cs`: 處理麥克風錄音、串接 OpenAI 服務以及搜尋地點。
- `GlobalAudioManager.cs`: 彙整音訊內容，並依照優先順序波放出來。
- `CloudGPSReceiver.cs`:使用firebase獲取手機定位資訊，並擷取firebase即時資料庫資訊的定位資訊。

---

## ⚠️ 注意事項

- **API **：本專案使用多項API，請自行申請(openAI 、Google API)。
- **安全性**：請勿將包含真實 API Key 的 `README.md` 或腳本上傳至公開倉庫。
- **權限**：在 Android (Quest) 平台上執行時，請確保已開啟麥克風權限。

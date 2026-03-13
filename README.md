# 🕶️ Smart Navigation Glasses (智慧導航眼鏡)

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

## 🛠️ 技術棧

- **引擎**: Unity 2022.3+
- **硬體支援**: Meta Quest 系列 (使用 OVRInput)
- **API 整合**:
  - OpenAI API (Whisper & GPT)
  - Google Maps Platform (Directions, Roads, Places)
- **UI 系統**: TextMeshPro

## 🚀 快速開始

### 1. 環境設定
確保您的 Unity 專案已安裝以下 Package：
- `Oculus Integration` (Meta Interaction SDK)
- `TextMeshPro`

### 2. API 金鑰配置
在場景中找到 `MapDataLoader` 與 `VoiceNavigationHandler` 物件，並在 Inspector 視窗填入您的金鑰：
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

---

## ⚠️ 注意事項

- **API 費用**：本專案使用多項付費 API，請監控您的使用量。
- **安全性**：請勿將包含真實 API Key 的 `README.md` 或腳本上傳至公開倉庫。
- **權限**：在 Android (Quest) 平台上執行時，請確保已開啟麥克風權限。

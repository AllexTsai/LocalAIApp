# LocalAIApp

一個專為 AI PC 邊緣運算（Edge AI）設計的高效能 Windows 桌面端軟體原型。本專案專注於克服地端硬體算力（Compute Budget）與系統資源限制，透過底層網路管道優化與防禦性編程（Defensive Programming），實現流暢、不耗盡系統資源的地端 AI 色彩管理助手應用。

本專案使用最新 **.NET 10 (WPF)** 開發，並原生對接 **Ollama** 地端常駐伺服器與微軟 **Phi-3 (3.8B)** 輕量級大模型（SLM）。

---

## 🚀 核心架構與技術亮點 (Technical Highlights)

作為資深軟體架構師，本專案在實作過程中針對 Edge AI 的常見痛點（如風扇暴轉、首字延遲高、中途斷流與孤兒程序）進行了深度的系統級優化：

### 1. 首字延遲優化 (TTFT Tuning via `num_ctx`)
*   **痛點**：地端輕量模型在有限的筆電硬體上運行時，預設的上下文視窗（Context Window）過大會導致首字生成時間（Time-to-First-Token, TTFT）過長，使用者體驗極差。
*   **解法**：手動注入 Ollama 核心參數設定 `num_ctx = 1024`。在確保顯示器色彩管理專業對話所需長度的前提下，大幅縮減記憶體與算力開銷，讓 **TTFT 縮短數倍**，提供即時的互動反饋。

### 2. 精準輸出防禦與截斷控制 (`num_predict`)
*   **痛點**：地端 SLM 模型在缺乏算力加速或上下文過載時，容易產生中文編碼幻覺（亂碼）並無限吐字，進而觸發後端超時中斷。
*   **解法**：嚴格控制 `num_predict` 的輸出範圍，並調低隨機性參數 `temperature = 0.3`。強迫地端模型以最高邏輯嚴謹度、最精簡的 Token 消耗，完整呈現長文繁體中文解答。

### 3. 高效能非同步零緩衝串流 (`HttpClient` + .NET 10 Async Stream)
*   **解法**：繞過仍處於 Alpha 階段的第三方高階框架（避開 API 協議不對齊的 Bug），直接使用原生 `HttpClient` 的 `HttpCompletionOption.ResponseHeadersRead` 建立網路管道。搭配符合 .NET 10 最新高效能規範的非同步行讀取器（`ReadLineAsync` 迴圈），完全杜絕執行緒飢餓（Thread Starvation），實現**流暢、不中斷的地端打字機效果**。

### 4. 完整的生命週期守護與算力防禦 (`CancellationToken`)
*   **痛點**：當使用者在 AI 串流途中直接關閉軟體時，後端推論引擎（`llama-server.exe`）容易變成孤兒程序，在背景狂奔空轉，導致筆電持續發熱、耗電。
*   **解法**：在 WPF 的 `Closed` 生命週期中深度綁定 `CancellationTokenSource`。當視窗關閉時主動發出取消權杖強切 TCP 連線，**確保 Ollama 後端能在 1 秒內釋放 CPU/GPU 算力，完美收放自如**。

### 5. 流暢的 UI 狀態機切換 (WPF IsIndeterminate Loading)
*   **解法**：利用 WPF 的非同步數據流控制，在發送 Request 到地端模型解算 Header 的空檔，動態激活 `ProgressBar` 的 `IsIndeterminate="True"` 循環動畫，在 AI 思考期間提供平滑的視覺反饋，並在串流吐字的第一時間優化隱藏，建立極致的 UX 體驗。

---

## 🛠️ 系統組件與環境 (Environment)

*   **開發框架**：.NET 10.0-windows (WPF / C#)
*   **地端 AI 引擎**：Ollama Windows Client (RESTful Server 監聽 `http://localhost:11434`)
*   **部署模型**：Microsoft Phi-3 (3.8B Lightweight Model)
*   **System Prompt 設定**：
    > *"你是一位精通顯示器（Monitor）色彩校正、ICC Profile 與硬體控制的資深軟體架構師。請用繁體中文回答使用者的問題。"*

---

## 📂 本地運行指引 (Getting Started)

### 1. 啟動地端模型
確保您的電腦已安裝 Ollama，並在命令提示字元（CMD）中跑起模型：
```bash
ollama run phi3
```

### 2. 複製並編譯專案
```bash
git clone https://github.com
cd LocalAIApp

# 清除快取並重新還原
dotnet clean
dotnet restore

# 執行 WPF 應用程式
dotnet run
```

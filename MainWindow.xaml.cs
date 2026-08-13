using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using LocalAIApp.Services;
using LocalAIApp.Tools;

namespace LocalAIApp;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const string OllamaUrl = "http://localhost:11434/api/generate";

    private CancellationTokenSource? _cts;

    private readonly IAiSecurityPipeline _securityPipeline = new AiSecurityPipeline();

    public MainWindow()
    {
        InitializeComponent();
        TxtResponse.Text = "地端 AI 原生防禦版就緒！";

        this.Closed += MainWindow_Closed;
    }

    private async void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        await SendMessageToAiAsync();
    }

    private async void TxtInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SendMessageToAiAsync();
        }
    }

    System.Diagnostics.Stopwatch totalWatch = System.Diagnostics.Stopwatch.StartNew();
    System.Diagnostics.Stopwatch tokenWatch = new System.Diagnostics.Stopwatch();

    long ttftMilliseconds = 0;
    int tokenCount = 0;
    private async System.Threading.Tasks.Task SendMessageToAiAsync()
    {
        string userInput = TxtInput.Text.Trim();
        if (string.IsNullOrEmpty(userInput)) return;

        // Clean Prompt
        string sanitizedInput = _securityPipeline.SanitizePrompt(userInput);

        // By employing strict intent separation and strong paradigms, the reflection range of the small model is confined.
        string systemSetting =
            "你是一位精通 Windows 系統與顯示器色彩校正的專業架構師。請用繁體中文回答。\n\n" +
            "[核心規則]\n" +
            "1. 當使用者是在跟你「討論技術概念」、「比較平台差異」(例如問Windows與Mac色彩差異) 或「一般聊天」時，你「禁止」輸出任何協議標籤，請直接用文字專業回覆。\n" +
            "2. 只有當使用者發出明確的「查詢指令」或希望「即時偵測/診斷/獲取當前這台電腦的實體硬體數據」時，你才可以在回覆的最開頭輸出以下標籤（其後不加其他文字）：\n" +
            "   - 明確要求查這台電腦的作業系統版本: [[CALL_WMI:OS]]\n" +
            "   - 明確要求查這台電腦的CPU資訊/負載: [[CALL_WMI:CPU]]\n" +
            "   - 明確要求查這台電腦的顯示卡/GPU: [[CALL_WMI:GPU]]\n" +
            "   - 明確要求查這台電腦的記憶體/RAM: [[CALL_WMI:Memory]]\n" +
            "   - 明確要求查這台電腦的硬碟/Disk: [[CALL_WMI:Disk]]\n\n" +
            "[範例對齊]\n" +
            "問：「Windows色彩架構跟Mac有何不同？」 -> 答：「(直接詳細解釋ICC Profile與色彩管理差異，絕對不帶有標籤)」\n" +
            "問：「幫我看一下我這台電腦的CPU是哪一顆」 -> 答：「[[CALL_WMI:CPU]]」";

        string finalPrompt = $"<|system|>\n{systemSetting}<|end|>\n<|user|>\n{sanitizedInput}<|end|>\n<|assistant|>\n";

        const int LaptopMaxCtx = 2048;

        // Token computing power circuit breaker check
        if (!_securityPipeline.ValidateTokenBudget(systemSetting, sanitizedInput, LaptopMaxCtx, out int projectedTokens))
        {
            TxtResponse.Text = $"⚠️【地端算力防禦熔斷】\n當前輸入預估消耗 {projectedTokens} Tokens（已超越硬體負載上限 {LaptopMaxCtx}）。本次推理已安全攔截。";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        TxtInput.Clear();
        BtnSend.IsEnabled = false;
        TxtResponse.Text = "";
        LoadingOverlay.Visibility = Visibility.Visible;

        bool isFirstToken = true;

        var requestPayload = new
        {
            model = "phi3",
            prompt = finalPrompt,
            stream = true,
            options = new { num_predict = 512, temperature = 0.0, num_ctx = LaptopMaxCtx }
        };

        try
        {
            var jsonPayload = JsonSerializer.Serialize(requestPayload);

            using var request = new HttpRequestMessage(HttpMethod.Post, OllamaUrl)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(_cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string fullResponseText = "";
            bool isProtocolChecked = false;

            while (await reader.ReadLineAsync(_cts.Token) is string line)
            {
                if (string.IsNullOrEmpty(line)) continue;

                using var jsonDoc = JsonDocument.Parse(line);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("response", out var responseProp))
                {
                    string token = responseProp.GetString() ?? "";
                    if (isFirstToken && !string.IsNullOrEmpty(token))
                    {
                        isFirstToken = false;
                        LoadingOverlay.Visibility = Visibility.Collapsed;

                        // Stop counting when the first character is captured.
                        totalWatch.Stop();
                        ttftMilliseconds = totalWatch.ElapsedMilliseconds;

                        // Start the word timer
                        tokenWatch.Start();
                    }

                    tokenCount++;
                    fullResponseText += token;

                    // As long as the protocol hasn't been triggered yet, and the stream text contains a complete protocol closing tag. `]]`
                    if (!isProtocolChecked && fullResponseText.Contains("[[CALL_WMI:") && fullResponseText.Contains("]]"))
                    {
                        isProtocolChecked = true; // The system is locked; subsequent normal conversations will never trigger the check again.
                        LoadingOverlay.Visibility = Visibility.Collapsed;

                        int startIdx = fullResponseText.IndexOf("[[CALL_WMI:") + 11;
                        int endIdx = fullResponseText.IndexOf("]]", startIdx);

                        if (endIdx > startIdx)
                        {
                            string selectedCategory = fullResponseText.Substring(startIdx, endIdx - startIdx).Trim();

                            TxtResponse.Text = $"🤖 [協議解碼成功] 偵測到模型發射 WMI 驅動標籤：[{selectedCategory}]。\n正在跨進程喚醒 WmiQueryTool 子系統...\n";
                            TxtResponse.ScrollToEnd();

                            var adapterType = typeof(WmiToolAdapter);
                            var method = adapterType.GetMethod("ExecuteWmiQuery");

                            if (method != null)
                            {
                                var adapterInstance = new WmiToolAdapter();
                                string wmiResult = (string)method.Invoke(adapterInstance, new object[] { selectedCategory })!;

                                TxtResponse.Text += $"\n====================================\n{wmiResult}====================================\n\n🤖 [系統優化提示] 舊有組件數據已透過 IPC 隔離管道安全回填。";
                                TxtResponse.ScrollToEnd();
                            }
                            break; // The Ollama connection should only be disconnected when the WMI protocol is actually triggered!
                        }
                    }

                    // As long as it doesn't start with a protocol tag, it's a pure technical discussion, 100% complete, and will never be interrupted midway.
                    if (!fullResponseText.StartsWith("[[CALL_WMI:"))
                    {
                        TxtResponse.Text = fullResponseText;
                        TxtResponse.ScrollToEnd();
                    }
                }
            }

            // After the streaming is completely finished (outside the loop), calculate TPOT and output the performance metrics to the UI or Console.
            tokenWatch.Stop();
            double tpotMilliseconds = tokenCount > 0 ? (double)tokenWatch.ElapsedMilliseconds / tokenCount : 0;

            Console.WriteLine($"[AI 效能報告] TTFT (首字延遲): {ttftMilliseconds} ms | TPOT (平均字延遲): {tpotMilliseconds:F2} ms/token | 總生成 Token 數: {tokenCount}");

            if (tokenCount > 0)
            {
                TxtResponse.Text += $"\n\n" +
                                    $"====================================\n" +
                                    $"📊 【地端 AI 邊緣端效能即時觀測】\n" +
                                    $"------------------------------------\n" +
                                    $" ⏱️ 首字延遲 (TTFT)  : {ttftMilliseconds} ms\n" +
                                    $" ⚡ 每個 Token 延遲   : {tpotMilliseconds:F2} ms/token\n" +
                                    $" 📈 持續吞吐效能     : {(1000 / tpotMilliseconds):F1} tokens/sec\n" +
                                    $" 📥 總輸出 Token 數量 : {tokenCount} tokens\n" +
                                    $"====================================";
                
                TxtResponse.ScrollToEnd();
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("AI 推論已被使用者或系統安全取消。");
        }
        catch (Exception ex)
        {
            TxtResponse.Text = $"地端驅動發生錯誤: {ex.Message}";
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            BtnSend.IsEnabled = true;
        }
    }


    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // 1. Send a cancellation signal to forcibly interrupt the HttpClient stream that is running in the background.
        _cts?.Cancel();
        _cts?.Dispose();

        // 2. Completely release HttpClient
        _httpClient.Dispose();

        // 3. Forcefully ensures the entire WPF process exits completely.
        Application.Current.Shutdown();
    }
}

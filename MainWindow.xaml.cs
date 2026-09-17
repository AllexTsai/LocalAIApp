using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly IToolDiscoveryService _toolDiscoveryService = new ToolDiscoveryService();
    private readonly string _toolListText;

    private static readonly Regex ProtocolTagPattern =
        new Regex(@"\[\[([A-Z_]+):([^\]]*)\]\]", RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        TxtResponse.Text = "地端 AI 原生防禦版就緒！";
        this.Closed += MainWindow_Closed;

        // 掃描所有標記 [AiPlugin] 的方法，動態組出可用工具清單，取代手寫的固定規則列表。
        var discoveredTools = _toolDiscoveryService.DiscoverTools(typeof(WmiToolAdapter));
        _toolListText = _toolDiscoveryService.BuildToolListText(discoveredTools);
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

    System.Diagnostics.Stopwatch totalWatch = new System.Diagnostics.Stopwatch();
    System.Diagnostics.Stopwatch tokenWatch = new System.Diagnostics.Stopwatch();
    long ttftMilliseconds = 0;
    int tokenCount = 0;

    private class StreamAttemptResult
    {
        public bool CompletedNormally;        // 有收到 done:true
        public bool WmiTriggered;             // 觸發了 WMI 協議分支
        public bool ContentLeakageDetected;   // 偵測到模型複誦/幻覺系統指令結構，已提早中止
        public bool UnknownProtocolDetected;  // 偵測到未定義的協議標籤（非 CALL_WMI），已提早中止
        public int TokenCount;
        public int ActualPromptEvalCount;
        public string AccumulatedText = "";
    }

    private async System.Threading.Tasks.Task SendMessageToAiAsync()
    {
        string userInput = TxtInput.Text.Trim();
        if (string.IsNullOrEmpty(userInput)) return;

        string sanitizedInput = _securityPipeline.SanitizePrompt(userInput);

        string systemSetting =
            "你是一位精通 Windows 系統與顯示器色彩校正的專業架構師。請用繁體中文回答。\n\n" +
            "[核心規則]\n" +
            "1. 當使用者是在跟你「討論技術概念」、「比較平台差異」(例如問Windows與Mac色彩差異) 或「一般聊天」時，你「禁止」輸出任何協議標籤，請直接用文字專業回覆。\n" +
            "2. 只有當使用者發出明確的「查詢指令」或希望「即時偵測/診斷/獲取當前這台電腦的實體硬體數據」時，你才可以在回覆的最開頭輸出以下標籤（其後不加其他文字）：\n" +
            _toolListText + "\n\n" +
            "[範例對齊]\n" +
            "問：「Windows色彩架構跟Mac有何不同？」 -> 答：「(直接詳細解釋ICC Profile與色彩管理差異，絕對不帶有標籤)」\n" +
            "問：「幫我看一下我這台電腦的CPU是哪一顆」 -> 答：「[[CALL_WMI:CPU]]」";

        string finalPrompt = $"<|system|>\n{systemSetting}<|end|>\n<|user|>\n{sanitizedInput}<|end|>\n<|assistant|>\n";

        const int LaptopMaxCtx = 2048;

        if (!_securityPipeline.ValidateTokenBudget(finalPrompt, LaptopMaxCtx, out int projectedTokens))
        {
            TxtResponse.Text = $"⚠️【地端算力防禦熔斷】\n當前輸入預估消耗 {projectedTokens} Tokens（已超越硬體負載上限 {LaptopMaxCtx}）。本次推理已安全攔截。";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var myToken = _cts.Token; // 鎖定這次呼叫專屬的 token，避免被下一次呼叫中途替換

        TxtInput.Clear();
        BtnSend.IsEnabled = false;
        TxtResponse.Text = "";
        LoadingOverlay.Visibility = Visibility.Visible;

        totalWatch.Restart();
        tokenWatch.Reset();
        ttftMilliseconds = 0;
        tokenCount = 0;

        try
        {
            var attempt = await StreamOnceAsync(finalPrompt, sanitizedInput, temperature: 0.0, myToken);

            // 串流異常中斷（沒收到 done:true，也沒有正常觸發 WMI 分支）才重試；
            // temperature=0.0 是貪婪解碼，原地重試同一個 prompt 只會得到一模一樣的失敗結果，
            // 所以重試時刻意調高 temperature，讓模型有機會走上不同的生成路徑。
            if (!attempt.CompletedNormally && !attempt.WmiTriggered && !attempt.ContentLeakageDetected && !attempt.UnknownProtocolDetected)
            {
                WriteStreamDebugLog($"STREAM_ABNORMAL_END | tokenCount={attempt.TokenCount} | retrying_with_temperature=0.7");
                TxtResponse.Text += "\n\n⚠️ 偵測到回應異常中斷，正在以備用取樣參數重新嘗試...\n";
                TxtResponse.ScrollToEnd();

                totalWatch.Restart();
                tokenWatch.Reset();
                ttftMilliseconds = 0;

                attempt = await StreamOnceAsync(finalPrompt, sanitizedInput, temperature: 0.7, myToken);

                if (!attempt.CompletedNormally && !attempt.WmiTriggered && !attempt.ContentLeakageDetected && !attempt.UnknownProtocolDetected)
                {
                    WriteStreamDebugLog($"GIVE_UP_AFTER_RETRY | tokenCount={attempt.TokenCount}");
                    TxtResponse.Text = attempt.AccumulatedText +
                        "\n\n⚠️ 此問題可能觸發已知的模型輸出編碼邊界情況，建議嘗試換句話問。";
                    TxtResponse.ScrollToEnd();
                }
            }

            tokenCount = attempt.TokenCount;

            if (!attempt.WmiTriggered && !attempt.ContentLeakageDetected && !attempt.UnknownProtocolDetected && tokenCount > 0)
            {
                tokenWatch.Stop();
                double tpotMilliseconds = (double)tokenWatch.ElapsedMilliseconds / tokenCount;

                Console.WriteLine($"[AI 效能報告] TTFT: {ttftMilliseconds} ms | TPOT: {tpotMilliseconds:F2} ms/token | 總生成 Token 數: {tokenCount} | 估算(校正後): {projectedTokens} | Ollama實際: {(attempt.ActualPromptEvalCount > 0 ? attempt.ActualPromptEvalCount.ToString() : "N/A")}");

                TxtResponse.Text += $"\n\n" +
                                    $"====================================\n" +
                                    $"📊 【地端 AI 邊緣端效能即時觀測】\n" +
                                    $"------------------------------------\n" +
                                    $" ⏱️ 首字延遲 (TTFT)  : {ttftMilliseconds} ms\n" +
                                    $" ⚡ 每個 Token 延遲   : {tpotMilliseconds:F2} ms/token\n" +
                                    $" 📈 持續吞吐效能     : {(1000 / tpotMilliseconds):F1} tokens/sec\n" +
                                    $" 📥 總輸出 Token 數量 : {tokenCount} tokens\n" +
                                    $" 🎯 Token 估算校正值 : {projectedTokens}（校正係數 1.22）\n" +
                                    $" ✅ Ollama 實際消耗  : {(attempt.ActualPromptEvalCount > 0 ? attempt.ActualPromptEvalCount.ToString() : "N/A")}\n" +
                                    $"====================================";

                TxtResponse.ScrollToEnd();
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("AI 推論已被使用者或系統安全取消。");
            WriteStreamDebugLog($"CANCELLED | tokenCount={tokenCount}");
        }
        catch (Exception ex)
        {
            TxtResponse.Text = $"地端驅動發生錯誤: {ex.Message}";
            WriteStreamDebugLog($"EXCEPTION: {ex.Message} | tokenCount={tokenCount}");
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            BtnSend.IsEnabled = true;
        }
    }

    private async Task<StreamAttemptResult> StreamOnceAsync(string finalPrompt, string sanitizedInput, double temperature, CancellationToken outerToken)
    {
        var result = new StreamAttemptResult();
        bool isFirstToken = true;
        bool isProtocolChecked = false;
        string fullResponseText = "";

        var requestPayload = new
        {
            model = "phi3",
            prompt = finalPrompt,
            stream = true,
            options = new { num_predict = 512, temperature = temperature, num_ctx = 2048, repeat_penalty = 1.1 }
        };

        var jsonPayload = JsonSerializer.Serialize(requestPayload);

        using var request = new HttpRequestMessage(HttpMethod.Post, OllamaUrl)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, outerToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(outerToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            // 每一行都各自套用一個 8 秒的讀取逾時，跟外層的取消 token 合併；
            // 逾時只代表「這一行等太久」，不等於使用者主動取消。
            using var readTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken, readTimeoutCts.Token);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (!outerToken.IsCancellationRequested)
            {
                WriteStreamDebugLog($"READ_TIMEOUT_NO_DONE | tokenCount={result.TokenCount}");
                break;
            }

            if (line == null) break; // 串流結束（reader 收到 EOF）
            if (string.IsNullOrEmpty(line)) continue;

            using var jsonDoc = TryParseStreamLine(line);
            if (jsonDoc == null) continue;
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("prompt_eval_count", out var promptEvalCountProp))
            {
                result.ActualPromptEvalCount = promptEvalCountProp.GetInt32();
            }

            if (root.TryGetProperty("done", out var doneProp) && doneProp.ValueKind == JsonValueKind.True)
            {
                WriteStreamDebugLog($"DONE_TRUE_LINE | raw_line=\"{line}\"");
                result.CompletedNormally = true;
            }

            if (root.TryGetProperty("response", out var responseProp))
            {
                string token = responseProp.GetString() ?? "";

                if (!string.IsNullOrEmpty(token))
                {
                    if (isFirstToken)
                    {
                        isFirstToken = false;
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                        totalWatch.Stop();
                        ttftMilliseconds = totalWatch.ElapsedMilliseconds;
                        tokenWatch.Start();
                    }

                    result.TokenCount++;
                    fullResponseText += token;

                    // 內容健檢：模型若複誦/幻覺出系統指令本身的結構（而非真的在回答問題），立刻中止本次串流。
                    if (fullResponseText.Contains("[核心規則]") || fullResponseText.Contains("[範例對齊]"))
                    {
                        string matchedMarker = fullResponseText.Contains("[核心規則]") ? "[核心規則]" : "[範例對齊]";
                        string snippet = fullResponseText.Length > 200 ? fullResponseText.Substring(0, 200) : fullResponseText;
                        WriteStreamDebugLog($"SYSTEM_PROMPT_LEAKAGE_DETECTED | matched_marker={matchedMarker} | raw_response_snippet={snippet}");

                        TxtResponse.Text = "⚠️ [內容異常攔截] 偵測到模型輸出疑似偏離主題，已中止本次回應，建議換句話問或提供更明確的指令。";
                        TxtResponse.ScrollToEnd();

                        result.ContentLeakageDetected = true;
                        result.AccumulatedText = fullResponseText;
                        return result;
                    }

                    if (!isProtocolChecked)
                    {
                        var protocolMatch = ProtocolTagPattern.Match(fullResponseText);

                        if (protocolMatch.Success)
                        {
                            isProtocolChecked = true;
                            string tagName = protocolMatch.Groups[1].Value;
                            string tagParam = protocolMatch.Groups[2].Value;

                            if (tagName != "CALL_WMI")
                            {
                                // 幻覺協議標籤：不是我們定義的 CALL_WMI，不嘗試呼叫任何工具，直接安全攔截。
                                LoadingOverlay.Visibility = Visibility.Collapsed;

                                string snippet = fullResponseText.Length > 200 ? fullResponseText.Substring(0, 200) : fullResponseText;
                                WriteStreamDebugLog($"UNKNOWN_PROTOCOL_TAG | tag_name={tagName} | tag_param={tagParam} | raw_snippet={snippet}");

                                TxtResponse.Text = $"⚠️ [未知協議攔截] 偵測到模型輸出未定義的協議格式 [{tagName}:{tagParam}]，已安全攔截。";
                                TxtResponse.ScrollToEnd();

                                result.UnknownProtocolDetected = true;
                                result.AccumulatedText = fullResponseText;
                                return result;
                            }

                            LoadingOverlay.Visibility = Visibility.Collapsed;

                            int startIdx = fullResponseText.IndexOf("[[CALL_WMI:") + 11;
                            int endIdx = fullResponseText.IndexOf("]]", startIdx);

                            if (endIdx > startIdx)
                            {
                                string selectedCategory = fullResponseText.Substring(startIdx, endIdx - startIdx).Trim();

                                try
                                {
                                    string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wmi_trigger_debug.log");
                                    string logEntry = $"時間戳: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\n" +
                                                       $"使用者原始輸入 (sanitizedInput): {sanitizedInput}\n" +
                                                       $"解析出的 selectedCategory: {selectedCategory}\n" +
                                                       $"觸發當下的完整 fullResponseText:\n{fullResponseText}\n" +
                                                       $"------------------------------------\n";
                                    File.AppendAllText(logPath, logEntry);
                                }
                                catch { }

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

                                result.WmiTriggered = true;
                                result.AccumulatedText = fullResponseText;
                                return result;
                            }
                        }
                    }

                    if (!fullResponseText.StartsWith("[[CALL_WMI:"))
                    {
                        TxtResponse.Text = fullResponseText;
                        TxtResponse.ScrollToEnd();
                    }
                }
            }
        }

        result.AccumulatedText = fullResponseText;
        WriteStreamDebugLog(result.CompletedNormally
            ? $"NORMAL_LOOP_END | tokenCount={result.TokenCount}"
            : $"NORMAL_LOOP_END_WITHOUT_DONE | tokenCount={result.TokenCount}");

        return result;
    }

    private static JsonDocument? TryParseStreamLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (Exception ex)
        {
            WriteStreamDebugLog($"JSON_PARSE_FAILED | raw_line=\"{line}\" | exception={ex.Message}");
            return null;
        }
    }

    private static void WriteStreamDebugLog(string message)
    {
        try
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "stream_parse_debug.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch
        {
            // debug log 寫入失敗不應該影響主流程
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _httpClient.Dispose();
        Application.Current.Shutdown();
    }
}
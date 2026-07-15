using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace LocalAIApp;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const string OllamaUrl = "http://localhost:11434/api/generate";
    
    private CancellationTokenSource? _cts;

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

    private async System.Threading.Tasks.Task SendMessageToAiAsync()
    {
        string userInput = TxtInput.Text.Trim();
        if (string.IsNullOrEmpty(userInput)) return;

        // 如果上一次的對話還在跑，先取消掉它
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
            prompt = $"[系統設定]你是一位精通顯示器色彩校正的架構師。請用繁體中文回答。\n[使用者提問]{userInput}",
            stream = true,
            options = new { num_predict = 1024, temperature = 0.3 , num_ctx = 1024}
        };

        try
        {
            var jsonPayload = JsonSerializer.Serialize(requestPayload);
            
            using var request = new HttpRequestMessage(HttpMethod.Post, OllamaUrl)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            // 💡 將 CancellationToken 注入到網路請求中
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(_cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            
            string fullResponseText = "";

            // 💡 在讀取串流的每一行時，同樣傳入 Token。一旦取消，這裡會立刻噴出 OperationCanceledException 並終止
            while (await reader.ReadLineAsync(_cts.Token) is string line)
            {
                if (string.IsNullOrEmpty(line)) continue;

                using var jsonDoc = JsonDocument.Parse(line);
                if (jsonDoc.RootElement.TryGetProperty("response", out var responseProp))
                {
                    string token = responseProp.GetString() ?? "";
                    if (isFirstToken && !string.IsNullOrEmpty(token))
                    {
                        isFirstToken = false;
                        // 關閉思考中動畫，讓使用者看到畫面開始動了
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                    }

                    fullResponseText += token;

                    TxtResponse.Text = fullResponseText;
                    TxtResponse.ScrollToEnd();
                }
                if (jsonDoc.RootElement.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean())
                {
                    // 檢查結束時的狀態統計
                    if (jsonDoc.RootElement.TryGetProperty("done_reason", out var reason))
                    {
                        string endReason = reason.GetString()?? "";
                        // 如果 endReason 是 "length"，代表就是 num_predict 不夠大，被強制切斷了！
                        // 如果 endReason 是 "stop"，代表模型自己覺得講完了（可能是 prompt 引導不夠好）
                        Console.WriteLine($"Stream 結束原因: {endReason}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 使用者取消或視窗關閉，優雅結束，不噴錯誤 UI
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

    // 💡 當使用者點擊視窗「X」關閉時觸發
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // 1. 發出取消訊號，強行中斷正在背景狂奔的 HttpClient 串流
        _cts?.Cancel();
        _cts?.Dispose();

        // 2. 徹底釋放 HttpClient
        _httpClient.Dispose();

        // 3. 強制確保整個 WPF 進程完全退出，不留孤兒程序
        Application.Current.Shutdown();
    }
}

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using LocalAIApp.Services; 

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

    private async System.Threading.Tasks.Task SendMessageToAiAsync()
    {
        string userInput = TxtInput.Text.Trim();
        if (string.IsNullOrEmpty(userInput)) return;

        string sanitizedInput = _securityPipeline.SanitizePrompt(userInput);

        // Define your System settings and final assembly Prompt template
        string systemSetting = "[系統設定]你是一位精通顯示器色彩校正的架構師。請用繁體中文回答。";
        string finalPrompt = $"{systemSetting}\n[使用者提問]{sanitizedInput}";

        // My laptop's tested security context boundaries
        const int LaptopMaxCtx = 1024; 

        // Pre-estimation is performed using systemSetting and the cleaned sanitizedInput.
        if (!_securityPipeline.ValidateTokenBudget(systemSetting, sanitizedInput, LaptopMaxCtx, out int projectedTokens))
        {
            // Triggering security defense: Instead of uploading to the local Ollama server, a warning is displayed directly in the UI to protect the laptop's CPU from overheating.
            TxtResponse.Text = $"⚠️【地端算力防禦熔斷】\n" +
                               $"當前輸入預估消耗 {projectedTokens} Tokens（已逼近或超越硬體安全負載上限 {LaptopMaxCtx}）。\n" +
                               $"為防止作業系統 Thread Pool 過載與核心風扇暴走，本次推理已自動攔截。\n" +
                               $"[建議] 請縮短您的提問內容，或精簡前置文字後重新發送。";
            
            // Keep the input box content intact so users can easily edit and reduce the number of characters.
            return; 
        }

        // If the previous conversation is still running, cancel it first.
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
            options = new { num_predict = 1024, temperature = 0.3 , num_ctx = LaptopMaxCtx}
        };

        try
        {
            var jsonPayload = JsonSerializer.Serialize(requestPayload);
            
            using var request = new HttpRequestMessage(HttpMethod.Post, OllamaUrl)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            // Inject the CancellationToken into the network request
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(_cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            
            string fullResponseText = "";

            // The same Token is passed in when reading each line of the stream.
            // If cancelled, an OperationCanceledException will be thrown immediately, terminating the process.
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
                        // Turn off the animation so that the user can see the screen start moving.
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                    }

                    fullResponseText += token;

                    TxtResponse.Text = fullResponseText;
                    TxtResponse.ScrollToEnd();
                }
                if (jsonDoc.RootElement.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean())
                {
                    // Status statistics at the end of the inspection
                    if (jsonDoc.RootElement.TryGetProperty("done_reason", out var reason))
                    {
                        string endReason = reason.GetString()?? "";
                        // If endReason is "length", it means that num_predict is not large enough and has been forcibly truncated!
                        // If endReason is "stop", it means the model itself feels it has finished explaining (possibly due to insufficient prompt guidance).
                        Console.WriteLine($"Stream 結束原因: {endReason}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The user cancels or closes the window, ending without the UI errors.
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

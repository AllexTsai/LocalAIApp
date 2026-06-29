using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace LocalAIApp;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const string OllamaUrl = "http://localhost:11434/api/generate";

    public MainWindow()
    {
        InitializeComponent();
        TxtResponse.Text = "地端 AI 原生驅動就緒！請輸入任何色彩校正或顯示器控制的問題。";
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

    // 使用原生 HttpClient 進行極致穩定的 JSON 串流讀取
    private async System.Threading.Tasks.Task SendMessageToAiAsync()
    {
        string userInput = TxtInput.Text.Trim();
        if (string.IsNullOrEmpty(userInput)) return;

        TxtInput.Clear();
        BtnSend.IsEnabled = false;
        TxtResponse.Text = "";

        var requestPayload = new
        {
            model = "phi3",
            prompt = $"[系統設定]你是一位精通顯示器色彩校正、ICC Profile 與硬體控制的資深軟體架構師。請用繁體中文回答。\n[使用者提問]{userInput}",
            stream = true
        };

        try
        {
            var jsonPayload = JsonSerializer.Serialize(requestPayload);
            
            // 修正 CS1503：改用 HttpRequestMessage 搭配 SendAsync，這才是支援 HttpCompletionOption 的標準架構
            using var request = new HttpRequestMessage(HttpMethod.Post, OllamaUrl)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            // 確保一拿到 Header 就開始讀取，實現真正的零緩衝串流
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            // 優化：指定編碼，保持乾淨的非同步管道
            using var reader = new StreamReader(stream, Encoding.UTF8);
            
            string fullResponseText = "";

            // 修正 CA2024：完全放棄阻塞的 EndOfStream，改用現代 .NET 推薦的 ReadLineAsync() 迴圈
            // 這能確保在等待地端 NPU/GPU 算力的空檔，完全不佔用 UI 或執行緒池的資源
            while (await reader.ReadLineAsync() is string line)
            {
                if (string.IsNullOrEmpty(line)) continue;

                using var jsonDoc = JsonDocument.Parse(line);
                if (jsonDoc.RootElement.TryGetProperty("response", out var responseProp))
                {
                    string token = responseProp.GetString() ?? "";
                    fullResponseText += token;

                    TxtResponse.Text = fullResponseText;
                    TxtResponse.ScrollToEnd();
                }
            }
        }
        catch (Exception ex)
        {
            TxtResponse.Text = $"地端驅動發生錯誤: {ex.Message}";
        }
        finally
        {
            BtnSend.IsEnabled = true;
        }
    }
}

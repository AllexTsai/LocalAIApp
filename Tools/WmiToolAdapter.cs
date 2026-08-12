using System;
using System.Diagnostics;
using System.IO;
using LocalAIApp.Attributes;

namespace LocalAIApp.Tools;

public class WmiToolAdapter
{
    // The instruction manual tells the AI ​​that you can pass in five parameters: "OS", "CPU", "GPU", "Memory", and "Disk".
    [AiPlugin("ExecuteWmiQuery", "當使用者需要查詢特定的硬體資訊時呼叫此工具。必須傳入 wmiCategory 參數：'OS'、'CPU'、'GPU'、'Memory' 或 'Disk'。")]
    public string ExecuteWmiQuery(string wmiCategory)
    {
        // Define the path to the pre-compiled WmiQueryTool.exe.
        string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WmiQueryTool.exe");

        // If the file cannot be found, send back the Mock data to ensure uninterrupted display.
        try
        {
            // Using Windows Process Redirection
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = wmiCategory, // Pass the category of the AI ​​decision as a parameter.
                RedirectStandardOutput = true, // Intercept its Console.WriteLine
                UseShellExecute = false,
                CreateNoWindow = true // Hide window, run quietly in the background
            };

            using Process? process = Process.Start(startInfo);
            if (process == null) return "進程啟動失敗。";

            // Read the WMI data output by WmiQueryTool
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return $"[WmiQueryTool 進程外呼叫成功]\n{output}";
        }
        catch (Exception ex)
        {
            return $"進程級跨界驅動失敗: {ex.Message}";
        }
    }
}

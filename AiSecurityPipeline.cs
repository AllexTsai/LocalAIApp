using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpToken;

namespace LocalAIApp.Services
{
    public interface IAiSecurityPipeline
    {
        string SanitizePrompt(string rawInput);
        bool ValidateTokenBudget(string systemPrompt, string sanitizedInput, int maxContextWindow, out int totalTokens);
    }

    public class AiSecurityPipeline : IAiSecurityPipeline
    {
        private readonly GptEncoding _encoder;

        public AiSecurityPipeline()
        {
            // cl100k_base is the underlying BPE segmentation code used by gpt-4, gpt-3.5, and most modern open-source small models (such as Phi-3, Llama-3) 
            // SharpToken has this encoding built-in, requiring no additional loading of any local files, resulting in minimal memory usage
            _encoder = GptEncoding.GetEncoding("cl100k_base"); 
        }

        /// <summary>
        /// 1. Prompt Cleaning: Defending against Special Character Contamination and Injection
        /// </summary>
        public string SanitizePrompt(string rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput)) return string.Empty;

            // Remove ASCII control characters (0-31), preserve newlines and tabs to prevent Ollama's JSON parsing from crashing.
            string sanitized = Regex.Replace(rawInput, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");

            // Prevent users from entering malicious Chat Template tags (such as <|end|> for Llama/Phi).
            sanitized = sanitized.Replace("<|", "&lt;|").Replace("|>", "|&gt;");

            return sanitized.Trim();
        }

        /// <summary>
        /// 2. Token circuit breaker mechanism: providing computational power defense before the token is sent from the ground.
        /// </summary>
        public bool ValidateTokenBudget(string systemPrompt, string sanitizedInput, int maxContextWindow, out int totalTokens)
        {
            // Use SharpToken's Encode for precise counting
            var systemTokensCount = _encoder.Encode(systemPrompt).Count;
            var userTokensCount = _encoder.Encode(sanitizedInput).Count;

            // Buffer reserved for model response (512 tokens reserved for model output)
            int outputBuffer = 512;
            
            totalTokens = systemTokensCount + userTokensCount;

            // If the total tokens + buffer exceed the laptop's set limit, sending back false will trigger a circuit breaker.
            return (totalTokens + outputBuffer) <= maxContextWindow;
        }
    }
}
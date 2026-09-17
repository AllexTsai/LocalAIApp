using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using LocalAIApp.Attributes;

namespace LocalAIApp.Services
{
    public class ToolInfo
    {
        public string MethodName { get; init; } = "";
        public string Description { get; init; } = "";
        public string? TagTemplate { get; init; }
        public string[]? ValidValues { get; init; }
    }

    public interface IToolDiscoveryService
    {
        IReadOnlyList<ToolInfo> DiscoverTools(Assembly assembly);
        IReadOnlyList<ToolInfo> DiscoverTools(params Type[] targetTypes);
        string BuildToolListText(IEnumerable<ToolInfo> tools);
    }

    public class ToolDiscoveryService : IToolDiscoveryService
    {
        private const BindingFlags ScanFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Scan every type in the given assembly for methods carrying [AiPlugin].
        /// </summary>
        public IReadOnlyList<ToolInfo> DiscoverTools(Assembly assembly)
        {
            return DiscoverTools(assembly.GetTypes());
        }

        /// <summary>
        /// Scan a specific set of types for methods carrying [AiPlugin].
        /// </summary>
        public IReadOnlyList<ToolInfo> DiscoverTools(params Type[] targetTypes)
        {
            var tools = new List<ToolInfo>();

            foreach (var type in targetTypes)
            {
                foreach (var method in type.GetMethods(ScanFlags))
                {
                    var attr = method.GetCustomAttribute<AiPluginAttribute>();
                    if (attr == null) continue;

                    tools.Add(new ToolInfo
                    {
                        MethodName = attr.MethodName,
                        Description = attr.Description,
                        TagTemplate = attr.TagTemplate,
                        ValidValues = attr.ValidValues
                    });
                }
            }

            return tools;
        }

        /// <summary>
        /// Render discovered tools as the bullet-list block that gets embedded into the system prompt.
        /// </summary>
        public string BuildToolListText(IEnumerable<ToolInfo> tools)
        {
            var toolList = tools.ToList();
            if (toolList.Count == 0) return "   - (目前沒有可用的工具)";

            var lines = new List<string>();

            foreach (var tool in toolList)
            {
                if (!string.IsNullOrEmpty(tool.TagTemplate) && tool.ValidValues is { Length: > 0 })
                {
                    // 有明確的子類別時，逐一展開成「說明 + 該子類別對應的確切標籤」，比照原本手寫的五行風格。
                    foreach (var value in tool.ValidValues)
                    {
                        string tag = string.Format(tool.TagTemplate, value);
                        lines.Add($"   - 明確要求查詢 {value} 相關資訊時輸出: {tag}");
                    }
                }
                else
                {
                    // 沒有子類別（例如只需要一個通用觸發的工具），就只印出一般性說明。
                    lines.Add($"   - {tool.Description}");
                }
            }

            return string.Join("\n", lines);
        }
    }
}

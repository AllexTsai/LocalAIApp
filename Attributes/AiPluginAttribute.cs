using System;

namespace LocalAIApp.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class AiPluginAttribute : Attribute
{
    public string MethodName { get; }
    public string Description { get; }
    public string? TagTemplate { get; set; }   // 例如 "[[CALL_WMI:{0}]]"
    public string[]? ValidValues { get; set; } // 例如 ["OS","CPU","GPU","Memory","Disk"]

    public AiPluginAttribute(string methodName, string description)
    {
        MethodName = methodName;
        Description = description;
    }
}

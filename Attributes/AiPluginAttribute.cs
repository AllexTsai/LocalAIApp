using System;

namespace LocalAIApp.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class AiPluginAttribute : Attribute
{
    public string MethodName { get; }
    public string Description { get; }

    public AiPluginAttribute(string methodName, string description)
    {
        MethodName = methodName;
        Description = description;
    }
}

namespace DesignerAssistant.Tools;

public static class OperationsMcpPath
{
    public static string Resolve(string contentRoot)
    {
        var configured = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_OPS_MCP_DLL");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var project = Path.GetFullPath(Path.Combine(contentRoot, "..", "AssistantOps.Mcp"));
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var path = Path.Combine(project, "bin", configuration, "net8.0", "AssistantOps.Mcp.dll");
            if (File.Exists(path)) return path;
        }
        return Path.Combine(project, "bin", "Debug", "net8.0", "AssistantOps.Mcp.dll");
    }
}

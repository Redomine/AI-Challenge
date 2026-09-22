namespace DesignerAssistant.Tools;

public static class WorkspaceRootLocator
{
    public static string Find(string startPath)
    {
        var configured = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_WORKSPACE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Не удалось найти корень Git workspace. Задайте DESIGN_ASSISTANT_WORKSPACE_ROOT.");
    }
}

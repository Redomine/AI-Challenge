namespace DesignerAssistant.Tools;

public sealed class WorkspacePathPolicy
{
    private static readonly HashSet<string> DeniedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "designer-assistant.db"
    };
    private static readonly HashSet<string> DeniedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pfx", ".p12", ".key", ".pem", ".db", ".sqlite", ".sqlite3"
    };

    public WorkspacePathPolicy(string root)
    {
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? throw new ArgumentException("Корень workspace не задан.", nameof(root)) : root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException($"Workspace не найден: {Root}");
    }

    public string Root { get; }

    public string Resolve(string relativePath, bool mustExist = false)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return Root;
        if (Path.IsPathRooted(relativePath)) throw new UnauthorizedAccessException("Разрешены только относительные пути workspace.");
        var fullPath = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!IsInside(fullPath)) throw new UnauthorizedAccessException("Путь находится вне рабочей области.");
        RejectReparseTraversal(fullPath);
        if (mustExist && !File.Exists(fullPath) && !Directory.Exists(fullPath))
            throw new FileNotFoundException("Путь не найден в workspace.", relativePath);
        return fullPath;
    }

    public string ResolveReadableFile(string relativePath)
    {
        var path = Resolve(relativePath, mustExist: true);
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден в workspace.", relativePath);
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (DeniedFileNames.Contains(name) || DeniedExtensions.Contains(extension) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("token", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Чтение потенциально секретного файла запрещено.");
        return path;
    }

    public string ToRelative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    private bool IsInside(string path) =>
        path.Equals(Root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void RejectReparseTraversal(string fullPath)
    {
        var current = Root;
        var relative = Path.GetRelativePath(Root, fullPath);
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Переход через symbolic link или reparse point запрещён.");
        }
    }
}

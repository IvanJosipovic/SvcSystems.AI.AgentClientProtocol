namespace SvcSystems.AI.AgentClientProtocol;

internal static class WorkspacePathPolicy
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static IReadOnlyList<string> CreateRoots(string workingDirectory, IReadOnlyList<string> additionalDirectories)
    {
        var roots = new List<string>(additionalDirectories.Count + 1)
        {
            ResolvePath(workingDirectory)
        };
        roots.AddRange(additionalDirectories.Select(ResolvePath));
        return roots.Distinct(PathComparer).ToArray();
    }

    public static bool AllowsPath(string path, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        var resolvedPath = ResolvePath(path);
        return roots.Any(root => IsWithinRoot(resolvedPath, root));
    }

    /// <summary>
    /// Resolves a path and confirms that it stays inside one of the workspace roots.
    /// </summary>
    /// <param name="path">The absolute path to resolve.</param>
    /// <param name="roots">The workspace roots the path must stay inside.</param>
    /// <returns>The fully resolved path, with every symbolic link and <c>..</c> segment applied.</returns>
    /// <exception cref="ArgumentException">The path is not absolute.</exception>
    /// <exception cref="IOException">A link in the path could not be resolved.</exception>
    /// <remarks>
    /// The resolved path is what the host should open. Opening the unresolved path instead leaves
    /// a window in which a link in the path can be swapped for one that points outside the
    /// workspace after the check has passed.
    /// </remarks>
    public static string ResolveWithinRoots(string path, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Workspace paths must be absolute.", nameof(path));
        }

        var resolvedPath = ResolvePath(path);
        if (!roots.Any(root => IsWithinRoot(resolvedPath, root)))
        {
            throw new UnauthorizedAccessException("The path is outside the session's authorized workspace.");
        }

        return resolvedPath;
    }

    private static string ResolvePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Workspace paths must be absolute.", nameof(path));
        }

        var pathRoot = Path.GetPathRoot(path)
            ?? throw new ArgumentException("Workspace path has no filesystem root.", nameof(path));
        var currentPath = pathRoot;
        var segments = path[pathRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                currentPath = Directory.GetParent(currentPath)?.FullName ?? pathRoot;
                continue;
            }

            currentPath = Path.Combine(currentPath, segment);
            currentPath = ResolveExistingLink(currentPath);
        }

        return Path.GetFullPath(currentPath);
    }

    private static string ResolveExistingLink(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            ThrowIfDanglingLink(path);
            return path;
        }
        catch (DirectoryNotFoundException)
        {
            ThrowIfDanglingLink(path);
            return path;
        }

        if ((attributes & FileAttributes.ReparsePoint) == 0) return path;

        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        var target = info.ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new IOException($"Could not resolve filesystem link '{path}'.");
        return Path.GetFullPath(target.FullName);
    }

    private static void ThrowIfDanglingLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
        {
            throw new IOException($"Could not resolve filesystem link '{path}'.");
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        if (string.Equals(path, root, PathComparison)) return true;
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootPrefix, PathComparison);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

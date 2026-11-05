using System.IO;
using System;

namespace Skua.Manager.Avalonia.Services;

internal static class SkuaExecutableLocator
{
    public static string ExecutablePath { get; } = Resolve();

    public static string WorkingDirectory =>
        Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;

    private static string Resolve()
    {
        string vibePath = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Bonxie.exe" : "Bonxie");
        if (File.Exists(vibePath))
            return vibePath;

        string deployedPath = Path.Combine(AppContext.BaseDirectory, "Skua.exe");
        if (File.Exists(deployedPath))
            return deployedPath;

        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string[] candidates =
        [
            Path.Combine(repositoryRoot, "Build", "Bonxie.exe"),
            Path.Combine(repositoryRoot, "Build", "Skua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Release", "net10.0-windows", "Bonxie.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Release", "net10.0-windows", "Skua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Release", "net10.0-windows", "win-x64", "Bonxie.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Release", "net10.0-windows", "win-x64", "Skua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Debug", "net10.0-windows", "Bonxie.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Debug", "net10.0-windows", "Skua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Debug", "net10.0-windows", "win-x64", "Bonxie.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Debug", "net10.0-windows", "win-x64", "Skua.exe")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return File.Exists(vibePath) ? vibePath : deployedPath;
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Skua.Shared.Avalonia.Utils;

/// <summary>
/// Makes the app use the native libraries it ships (Skia, HarfBuzz, ANGLE) instead of same-named copies found elsewhere.
/// </summary>
/// <remarks>
/// The packaged build keeps them in Assemblies\runtimes\{rid}\native, a folder .NET does not probe, so they used to be
/// found by an ordinary search by name. That search includes PATH, and other apps put their own copies there: UniGetUI
/// adds a newer libSkiaSharp.dll, which SkiaSharp refuses, and Avalonia then fails before any window opens. Windows hands
/// a library that is already loaded to every later load by the same name, so loading ours by full path first decides
/// which copy the process uses.
/// </remarks>
public static class BundledNativeLibraries
{
    /// <summary>Loads every bundled native library. Call before Avalonia starts; does nothing outside the packaged Windows build.</summary>
    public static void Load()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string rid = Environment.Is64BitProcess ? "win-x64" : "win-x86";
        string nativeDir = Path.Combine(AppContext.BaseDirectory, "Assemblies", "runtimes", rid, "native");
        if (!Directory.Exists(nativeDir))
            return; // Development builds keep runtimes\ next to the exe, where .NET already looks first.

        // Best effort: a library that won't load here, or a folder that can't be read, is left to the usual search by
        // name, exactly as before this existed. Failing would stop the app opening in cases that work today; Avalonia,
        // for one, falls back to software rendering when ANGLE won't load.
        try
        {
            foreach (string library in Directory.EnumerateFiles(nativeDir, "*.dll"))
                NativeLibrary.TryLoad(library, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

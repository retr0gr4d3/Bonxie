using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Skua.Core.Models;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Westwind.Scripting;

namespace Skua.Core;

/// <summary>
/// "Slightly" modified compiler based on Westwind.Scripting (https://github.com/RickStrahl/Westwind.Scripting)
/// </summary>
public class Compiler : CSharpScriptExecution
{
    private const int _maxCachedAssemblies = 1024;
    private static readonly string _cacheDirectory = Path.Combine(ClientFileSources.SkuaScriptsDIR, "Cached-Scripts");
    private static readonly TimeSpan _cacheExpiration = TimeSpan.FromDays(7);
    private static readonly TimeSpan _cleanupThrottle = TimeSpan.FromMinutes(5);
    private static DateTime _lastCleanupTime = DateTime.MinValue;
    private static readonly object _cleanupLock = new();
    private static readonly CSharpCompilationOptions _compilationOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        concurrentBuild: true,
        deterministic: true,
        reportSuppressedDiagnostics: false);
    private static readonly CSharpParseOptions _parseOptions = new(
        LanguageVersion.Latest,
        DocumentationMode.None);
    private readonly object _namespaceCacheLock = new();
    private string? _cachedNamespacePrefix = null;
    private int _lastNamespaceHash = 0;

    private static readonly ConcurrentDictionary<int, string> _compiledRegistry = new();
    private static readonly ConcurrentDictionary<string, Assembly> _loadedAssemblies = new(StringComparer.OrdinalIgnoreCase);

    public static bool FastVerifyAssembly(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;

        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsCompiled(int hash) => _compiledRegistry.ContainsKey(hash) || TryLoadFromDiskCache(hash, null) != null;

    public static bool IsLoaded(string assemblyPath) => _loadedAssemblies.ContainsKey(assemblyPath);

    public static void RegisterCompiled(int hash, string cachePath)
    {
        _compiledRegistry[hash] = cachePath;
    }

    public static void RegisterLoaded(string assemblyPath, Assembly assembly)
    {
        _loadedAssemblies[assemblyPath] = assembly;
    }

    public static void ClearSessionRegistries()
    {
        _loadedAssemblies.Clear();
    }

    /// <summary>
    /// This method compiles a class and hands back a
    /// dynamic reference to that class that you can
    /// call members on.
    ///
    /// Must have include parameterless ctor()
    /// </summary>
    /// <param name="code">Fully self-contained C# class</param>
    /// <param name="cacheHash">Optional hash for disk cache lookup</param>
    /// <param name="loadContext">Optional AssemblyLoadContext for loading the assembly</param>
    /// <param name="scriptName">Optional script name for cache file naming</param>
    /// <returns>Instance of that class or null</returns>
    [RequiresUnreferencedCode("This method may require code that cannot be statically analyzed for trimming. Use with caution.")]
    public new dynamic? CompileClass(string code, int? cacheHash = null, ScriptLoadContext? loadContext = null, string? scriptName = null)
    {
        Type? type = CompileClassToType(code, cacheHash, loadContext, scriptName);
        if (type == null)
            return null;

        GeneratedClassName = type.Name;
        GeneratedNamespace = type.Namespace;

        return CreateInstance();
    }

    /// <summary>
    /// This method compiles a class and hands back a
    /// dynamic reference to that class that you can
    /// call members on.
    /// </summary>
    /// <param name="code">Fully self-contained C# class</param>
    /// <param name="cacheHash">Optional hash for disk cache lookup</param>
    /// <param name="loadContext">Optional AssemblyLoadContext for loading the assembly</param>
    /// <param name="scriptName">Optional script name for cache file naming</param>
    /// <returns>Instance of that class or null</returns>
    [RequiresUnreferencedCode("This method may require code that cannot be statically analyzed for trimming. Use with caution.")]
    public new Type? CompileClassToType(string code, int? cacheHash = null, ScriptLoadContext? loadContext = null, string? scriptName = null)
    {
        int hash = cacheHash ?? code.GetHashCode();

        if (loadContext != null || !CachedAssemblies.ContainsKey(hash))
        {
            string? cachedAssemblyPath = TryLoadFromDiskCache(hash, scriptName);

            if (cachedAssemblyPath != null)
            {
                try
                {
                    Assembly = LoadCompiled(cachedAssemblyPath, loadContext);
                    RegisterCompiled(hash, cachedAssemblyPath);
                    RegisterLoaded(cachedAssemblyPath, Assembly);
                }
                catch
                {
                    try
                    {
                        File.Delete(cachedAssemblyPath);
                    }
                    catch
                    {
                    }
                    cachedAssemblyPath = null;
                }
            }

            if (cachedAssemblyPath == null)
            {
                code = PrependNamespaces(code);
                GeneratedClassCode = code;
                string diskCachePath = GetDiskCachePath(hash, scriptName);

                if (!CompileOrWaitForAssembly(code, diskCachePath, loadContext))
                    return null;

                RegisterCompiled(hash, diskCachePath);
                RegisterLoaded(diskCachePath, Assembly);
            }

            if (loadContext == null)
            {
                lock (CachedAssemblies)
                {
                    if (CachedAssemblies.Count >= _maxCachedAssemblies)
                    {
                        int oldestKey = CachedAssemblies.Keys.Min();
                        CachedAssemblies.TryRemove(oldestKey, out _);
                    }

                    CachedAssemblies[hash] = Assembly;
                }
            }
        }
        else
        {
            Assembly = CachedAssemblies[hash];
        }

        Type? firstType = Assembly.ExportedTypes.FirstOrDefault();
        if (firstType == null)
            throw new InvalidOperationException($"Assembly '{Assembly.FullName}' contains no exported types.");
        return firstType;
    }

    /// <summary>
    /// <para>
    /// Compiles a class and creates an assembly from the compiled class.</para>
    /// <para>
    /// Assembly is stored on the `.Assembly` property. Use `noLoad()`
    /// to bypass loading of the assembly
    /// </para>
    /// <para>Must include parameterless ctor()</para>
    /// </summary>
    /// <param name="source">Source code</param>
    /// <param name="noLoad">if set doesn't load the assembly (useful only when OutputAssembly is set)</param>
    /// <returns></returns>
    [RequiresUnreferencedCode("This method may require code that cannot be statically analyzed for trimming. Use with caution.")]
    public new bool CompileAssembly(string source, bool noLoad = false)
    {
        ClearErrors();
        string sourceWithNamespaces = PrependNamespaces(source);

        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceWithNamespaces.Trim(), _parseOptions);

        CSharpCompilation compilation = CSharpCompilation.Create(GeneratedClassName + ".cs")
            .WithOptions(_compilationOptions)
            .WithReferences(References)
            .AddSyntaxTrees(tree);

        if (SaveGeneratedCode)
            GeneratedClassCode = sourceWithNamespaces;

        bool isFileAssembly = false;
        Stream? codeStream = null;
        if (string.IsNullOrEmpty(OutputAssembly))
        {
            codeStream = new MemoryStream();
        }
        else
        {
            codeStream = new FileStream(OutputAssembly, FileMode.Create, FileAccess.Write);
            isFileAssembly = true;
        }

        using (codeStream)
        {
            if (!Emit(compilation, codeStream))
                return false;

            if (!noLoad)
            {
                Assembly = !isFileAssembly ? Assembly.Load(((MemoryStream)codeStream).ToArray()) : Assembly.LoadFrom(OutputAssembly);
            }
        }

        return true;
    }

    private void ClearErrors()
    {
        LastException = null;
        Error = false;
        ErrorMessage = null;
        ErrorType = ExecutionErrorTypes.None;
    }

    private void SetErrors(Exception ex)
    {
        Error = true;
        LastException = ex.GetBaseException();
        ErrorMessage = LastException.Message;

        if (ThrowExceptions)
            throw LastException;
    }

    private string PrependNamespaces(string source)
    {
        if (Namespaces == null || Namespaces.Count == 0)
            return source;

        int currentHash = Namespaces.Count > 0 ? string.Join(";", Namespaces).GetHashCode() : 0;

        lock (_namespaceCacheLock)
        {
            if (_cachedNamespacePrefix == null || _lastNamespaceHash != currentHash)
            {
                StringBuilder sb = new(Namespaces.Count * 40);
                foreach (string ns in Namespaces)
                {
                    sb.Append("using ");
                    sb.Append(ns);
                    sb.AppendLine(";");
                }
                sb.AppendLine();
                _cachedNamespacePrefix = sb.ToString();
                _lastNamespaceHash = currentHash;
            }

            return _cachedNamespacePrefix + source;
        }
    }

    /// <summary>
    /// Clears the cached assemblies to free memory
    /// </summary>
    public static void ClearAssemblyCache()
    {
        CachedAssemblies?.Clear();
    }

    private static string? TryLoadFromDiskCache(int hash, string? scriptName = null)
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
                return null;

            TryRunCleanup();

            string fileName = string.IsNullOrEmpty(scriptName) ? $"{hash}.dll" : $"{hash}-{scriptName}.dll";
            string cachedPath = Path.Combine(_cacheDirectory, fileName);

            if (File.Exists(cachedPath))
            {
                if (FastVerifyAssembly(cachedPath))
                {
                    return cachedPath;
                }
                else
                {
                    try
                    {
                        File.Delete(cachedPath);
                    }
                    catch
                    {
                    }
                    return null;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetDiskCachePath(int hash, string? scriptName = null)
    {
        if (!Directory.Exists(_cacheDirectory))
            Directory.CreateDirectory(_cacheDirectory);

        if (!string.IsNullOrEmpty(scriptName))
        {
            DeleteOldVersions(scriptName, hash);
        }

        string fileName = string.IsNullOrEmpty(scriptName) ? $"{hash}.dll" : $"{hash}-{scriptName}.dll";
        return Path.Combine(_cacheDirectory, fileName);
    }

    private static Assembly LoadCompiled(string path, ScriptLoadContext? loadContext)
    {
        if (loadContext is null)
            return Assembly.LoadFrom(path);

        using FileStream stream = File.OpenRead(path);
        return loadContext.LoadFromStream(stream);
    }

    /// <summary>
    /// Compiles <paramref name="source"/> to <paramref name="outputPath"/> and loads it, unless another client got
    /// there first, in which case its assembly is loaded. A named mutex keeps clients from compiling the same script
    /// into the same file at once.
    /// </summary>
    private bool CompileOrWaitForAssembly(string source, string outputPath, ScriptLoadContext? loadContext)
    {
        string mutexName = $"Global\\Skua_Compile_{Path.GetFileName(outputPath).Replace(".dll", "").Replace("-", "_")}";
        using Mutex compilationMutex = new(false, mutexName);

        try
        {
            if (!compilationMutex.WaitOne(TimeSpan.FromMinutes(2)))
                return Fail("Timeout waiting for script compilation to complete.");
        }
        catch (AbandonedMutexException)
        {
            // The client that was compiling died. The mutex is ours now; whatever it left is checked below.
        }

        try
        {
            if (File.Exists(outputPath) && !FastVerifyAssembly(outputPath))
            {
                try
                {
                    File.Delete(outputPath);
                }
                catch
                {
                }
            }

            if (!File.Exists(outputPath) && !CompileAssemblyToDisk(source, outputPath))
                return false;

            Assembly = LoadCompiled(outputPath, loadContext);
            return true;
        }
        finally
        {
            compilationMutex.ReleaseMutex();
        }
    }

    private bool Fail(string message)
    {
        ErrorType = ExecutionErrorTypes.Compilation;
        ErrorMessage = message;
        SetErrors(new ApplicationException(message));
        return false;
    }

    private bool CompileAssemblyToDisk(string source, string outputPath)
    {
        ClearErrors();

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source.Trim(), _parseOptions);

        string fileName = Path.GetFileNameWithoutExtension(outputPath);
        int lastDash = fileName.LastIndexOf('-');
        string assemblyName = lastDash > 0 ? fileName[(lastDash + 1)..] : fileName;

        CSharpCompilation compilation = CSharpCompilation.Create(assemblyName)
            .WithOptions(_compilationOptions)
            .WithReferences(References)
            .AddSyntaxTrees(tree);

        if (SaveGeneratedCode)
            GeneratedClassCode = source;

        using FileStream codeStream = new(outputPath, FileMode.Create, FileAccess.Write);
        return Emit(compilation, codeStream);
    }

    /// <summary>Writes the compiled assembly to <paramref name="codeStream"/>; on failure the errors are recorded.</summary>
    private bool Emit(CSharpCompilation compilation, Stream codeStream)
    {
        EmitResult compilationResult = CompileWithDebug
            ? compilation.Emit(codeStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded))
            : compilation.Emit(codeStream);

        if (compilationResult.Success)
            return true;

        StringBuilder sb = new();
        foreach (Diagnostic diag in
            compilationResult.Diagnostics
                .Where(diagnostic => diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error))
        {
            sb.AppendLine(diag.ToString());
        }

        return Fail(sb.ToString());
    }

    private static void TryRunCleanup()
    {
        lock (_cleanupLock)
        {
            if ((DateTime.Now - _lastCleanupTime) < _cleanupThrottle)
                return;

            Task.Run(() => CleanupOldCachedAssemblies());
            _lastCleanupTime = DateTime.Now;
        }
    }

    private static void CleanupOldCachedAssemblies()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
                return;

            string[] filePaths = Directory.GetFiles(_cacheDirectory, "*.dll");
            FileInfo[] files = new FileInfo[filePaths.Length];

            for (int i = 0; i < filePaths.Length; i++)
            {
                files[i] = new FileInfo(filePaths[i]);
            }

            DateTime now = DateTime.Now;
            HashSet<FileInfo> filesToDelete = [];

            foreach (FileInfo file in files)
            {
                string scriptName = GetScriptNameFromCacheFile(file.Name);
                if (scriptName.Contains("Core", StringComparison.OrdinalIgnoreCase))
                    continue;

                TimeSpan age = now - file.LastWriteTimeUtc;
                if (age > _cacheExpiration)
                {
                    filesToDelete.Add(file);
                }
            }

            Dictionary<string, List<FileInfo>> scriptGroups = new();
            foreach (FileInfo file in files)
            {
                if (filesToDelete.Contains(file))
                    continue;

                string scriptName = GetScriptNameFromCacheFile(file.Name);
                if (!scriptGroups.ContainsKey(scriptName))
                {
                    scriptGroups[scriptName] = [];
                }
                scriptGroups[scriptName].Add(file);
            }

            foreach (KeyValuePair<string, List<FileInfo>> kvp in scriptGroups)
            {
                if (kvp.Value.Count > 1)
                {
                    FileInfo newest = kvp.Value.MaxBy(f => f.LastWriteTimeUtc)!;
                    foreach (FileInfo file in kvp.Value)
                    {
                        if (file != newest)
                            filesToDelete.Add(file);
                    }
                }
            }

            if (files.Length - filesToDelete.Count >= _maxCachedAssemblies)
            {
                List<FileInfo> remaining = [.. files.Except(filesToDelete)
                    .Where(f => !GetScriptNameFromCacheFile(f.Name).Contains("Core", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.LastWriteTimeUtc)];
                int toRemove = remaining.Count - _maxCachedAssemblies + 1;

                for (int i = 0; i < toRemove && i < remaining.Count; i++)
                {
                    filesToDelete.Add(remaining[i]);
                }
            }

            foreach (FileInfo file in filesToDelete)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    /* ignored */
                }
            }
        }
        catch
        {
        }
    }

    private static string GetScriptNameFromCacheFile(string fileName)
    {
        ReadOnlySpan<char> nameWithoutExt = Path.GetFileNameWithoutExtension(fileName.AsSpan());
        int lastDashIndex = nameWithoutExt.LastIndexOf('-');

        return lastDashIndex <= 0 ? string.Empty : new string(nameWithoutExt[(lastDashIndex + 1)..]);
    }

    private static void DeleteOldVersions(string scriptName, int currentHash)
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
                return;

            foreach (string file in Directory.GetFiles(_cacheDirectory, $"*-{scriptName}.dll"))
            {
                string fileName = Path.GetFileName(file);
                string cachedScriptName = GetScriptNameFromCacheFile(fileName);

                if (cachedScriptName != scriptName)
                    continue;

                ReadOnlySpan<char> fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName.AsSpan());
                int dashIndex = fileNameWithoutExt.LastIndexOf('-');

                if (dashIndex > 0 && int.TryParse(fileNameWithoutExt[..dashIndex], out int fileHash) && fileHash != currentHash)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Locked files will be cleaned up by background throttle cleanup
                    }
                }
            }
        }
        catch
        {
        }
    }
}

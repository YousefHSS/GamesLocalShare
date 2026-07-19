using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace GamesLocalShare.Services;

/// <summary>
/// Loads the bundled <c>LibXboxOne</c> assembly (and its DiscUtils / BouncyCastle deps) <b>in-process</b> for
/// the streaming skeletonizer path. The batch capture path still shells out to
/// <c>tools\xvdtool\XVDTool.exe</c> as a separate process (see <see cref="SkeletonService"/>); streaming needs
/// the skeletonizer running in the app so the proxy tee can feed it page-by-page with direct backpressure — no
/// IPC.
///
/// <para>The bundled tool folder (<c>tools\xvdtool\</c>) holds <c>LibXboxOne.dll</c> and its runtime deps, but
/// that folder is NOT on the app's default probe path (the DLLs deliberately don't sit next to the exe, so the
/// subprocess model kept them out of the app's load context). <see cref="EnsureResolver"/> installs an
/// <see cref="AssemblyLoadContext"/> resolver that satisfies any managed load the default context can't — but
/// only from files physically present in that one folder — so the deps load lazily the first time a
/// <c>LibXboxOne</c> type is actually touched (i.e. only when streaming capture is used). Registering the
/// resolver is a no-op until then; it never force-loads anything.</para>
/// </summary>
public static class XvdInProcess
{
    private static readonly object Gate = new();
    private static bool _registered;
    private static string? _toolsDir;

    /// <summary>The bundled <c>tools\xvdtool</c> directory, or null if it can't be located.</summary>
    public static string? ToolsDir
    {
        get { lock (Gate) { return _toolsDir ??= ResolveToolsDir(); } }
    }

    /// <summary>Installs the in-process assembly resolver once. Safe to call from app startup; it does not load
    /// any xvdtool assembly by itself — resolution happens lazily on first use of a <c>LibXboxOne</c> type.</summary>
    public static void EnsureResolver()
    {
        lock (Gate)
        {
            if (_registered) return;
            _toolsDir ??= ResolveToolsDir();
            if (_toolsDir != null)
                AssemblyLoadContext.Default.Resolving += Resolve;
            _registered = true;
        }
    }

    private static Assembly? Resolve(AssemblyLoadContext ctx, AssemblyName name)
    {
        var dir = _toolsDir;
        if (dir == null || string.IsNullOrEmpty(name.Name)) return null;
        // Only satisfy loads from a DLL that actually sits in the bundled tool folder. This fires solely for
        // assemblies the default context couldn't resolve (LibXboxOne + its DiscUtils/BouncyCastle deps),
        // never for framework or app assemblies (those resolve normally before this hook runs).
        var path = Path.Combine(dir, name.Name + ".dll");
        if (!File.Exists(path)) return null;
        try { return ctx.LoadFromAssemblyPath(path); }
        catch { return null; }
    }

    /// <summary>Same walk-up resolution <see cref="SkeletonService"/> uses for the exe, but returns the folder.</summary>
    private static string? ResolveToolsDir()
    {
        var beside = Path.Combine(System.AppContext.BaseDirectory, "tools", "xvdtool");
        if (File.Exists(Path.Combine(beside, "LibXboxOne.dll"))) return beside;
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "tools", "xvdtool");
            if (File.Exists(Path.Combine(cand, "LibXboxOne.dll"))) return cand;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Validates the in-process load end to end: constructs a <c>LibXboxOne</c> type that has no
    /// external deps (the pure reorder bridge) and force-loads each bundled dependency assembly by name through
    /// the resolver. Returns a human-readable multi-line status; <paramref name="ok"/> is true only when every
    /// step succeeds. Used by the <c>--selftest-xvd-inprocess</c> headless mode.</summary>
    public static string SelfTest(out bool ok)
    {
        ok = false;
        EnsureResolver();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"tools dir: {(_toolsDir ?? "<not found>")}");
        if (_toolsDir == null) { sb.AppendLine("FAIL: tools\\xvdtool not located"); return sb.ToString(); }

        try
        {
            // 1) A LibXboxOne type with NO external deps must load + construct in-process.
            var probe = new LibXboxOne.StreamingPackageDecryptor(0, 1 << 20,
                (off, cnt) => new byte[cnt], (off, data, len) => { });
            probe.Push(0, 4);
            if (!probe.Flush(4)) { sb.AppendLine("FAIL: StreamingPackageDecryptor smoke did not drain"); return sb.ToString(); }
            var lib = typeof(LibXboxOne.StreamingSkeletonizer).Assembly;
            sb.AppendLine($"loaded: {lib.GetName().Name} {lib.GetName().Version} from {lib.Location}");

            // 2) Each bundled runtime dep must resolve through the resolver (by assembly name).
            string[] deps = { "DiscUtils.Core", "DiscUtils.Ntfs", "DiscUtils.Streams", "BouncyCastle.Crypto" };
            foreach (var d in deps)
            {
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(d));
                sb.AppendLine($"loaded dep: {asm.GetName().Name} {asm.GetName().Version}");
            }
            ok = true;
            sb.AppendLine("PASS: LibXboxOne + deps load in-process from tools\\xvdtool");
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
        }
        return sb.ToString();
    }
}

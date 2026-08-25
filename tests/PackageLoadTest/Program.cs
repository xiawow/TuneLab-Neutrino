using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using TuneLab.Foundation;
using TuneLab.SDK;

string packageDirectory = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "package"));
string manifestPath = Path.Combine(packageDirectory, "manifest.json");
using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
string assemblyName = manifest.RootElement.GetProperty("assembly").GetString()
    ?? throw new Exception("The package manifest has no assembly.");
string className = manifest.RootElement.GetProperty("class").GetString()
    ?? throw new Exception("The package manifest has no class.");
string assemblyPath = Path.Combine(packageDirectory, assemblyName);

var loadContext = new HostCompatiblePluginLoadContext(packageDirectory, assemblyPath);
Assembly pluginAssembly = loadContext.LoadFromAssemblyPath(assemblyPath);
Type engineType = pluginAssembly.GetType(className, throwOnError: true)!;
if (Activator.CreateInstance(engineType) is not IVoiceSynthesisEngine engine)
    throw new Exception("The manifest class does not implement TuneLab's voice engine contract.");
if (engine is not IExtensionSettings extensionSettings)
    throw new Exception("The manifest class does not expose extension settings.");

ObjectConfig settingsConfig = extensionSettings.GetSettingsConfig(new SettingsContext(PropertyObject.Empty));
var voicebankPathsEntry = settingsConfig.Properties
    .FirstOrDefault(pair => pair.Key.Id == "voicebank_paths");
ListConfig? voicebankPaths = voicebankPathsEntry?.Value as ListConfig;
if (voicebankPaths is null || voicebankPaths.Elements.Count != 1 || voicebankPaths.AddableElements.Count != 1)
    throw new Exception("Voicebank directories must start with one empty row and support adding more rows.");

engine.Init();
try
{
    if (engine.VoiceSourceInfos.Count == 0)
        throw new Exception("The loaded plugin did not find any NEUTRINO v3 voicebanks.");
    var automations = engine.GetAutomationConfigs(null!);
    AutomationConfig? styleShift = null;
    foreach (var pair in automations)
    {
        if (pair.Key.Id == "shfc")
        {
            styleShift = pair.Value;
            break;
        }
    }
    if (styleShift is null ||
        styleShift.MinValue != -1200 ||
        styleShift.MaxValue != 1200 ||
        styleShift.DefaultValue != 0)
    {
        throw new Exception("The packaged plugin did not expose the expected SHFC automation.");
    }
    string? nativeRuntime = loadContext.ProbeUnmanagedDll("onnxruntime");
    if (nativeRuntime is null || !File.Exists(nativeRuntime))
        throw new Exception("The packaged ONNX Runtime native library cannot be resolved.");

    Console.WriteLine($"Loaded {engineType.FullName} through TuneLab-compatible isolation.");
    Console.WriteLine($"Voicebanks: {string.Join(", ", engine.VoiceSourceInfos.Keys)}");
    Console.WriteLine("Automation: SHFC -1200..1200 cent, default 0");
    Console.WriteLine($"ONNX Runtime: {nativeRuntime}");
}
finally
{
    engine.Destroy();
}

sealed class HostCompatiblePluginLoadContext : AssemblyLoadContext
{
    public HostCompatiblePluginLoadContext(string pluginDirectory, string mainAssemblyPath)
        : base("package-load-test", isCollectible: false)
    {
        mPluginDirectory = pluginDirectory;
        mResolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    public string? ProbeUnmanagedDll(string name) => mResolver.ResolveUnmanagedDllToPath(name);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? name = assemblyName.Name;
        if (name is not null && IsSharedContract(name))
            return null;

        string? resolved = mResolver.ResolveAssemblyToPath(assemblyName);
        if (resolved is not null)
            return LoadFromAssemblyPath(resolved);

        if (name is not null)
        {
            string candidate = Path.Combine(mPluginDirectory, name + ".dll");
            if (File.Exists(candidate))
                return LoadFromAssemblyPath(candidate);
        }
        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = mResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }

    static bool IsSharedContract(string assemblyName) =>
        assemblyName == "TuneLab.Foundation" ||
        assemblyName == "TuneLab.SDK" ||
        assemblyName.StartsWith("TuneLab.SDK.", StringComparison.Ordinal);

    readonly string mPluginDirectory;
    readonly AssemblyDependencyResolver mResolver;
}

sealed class SettingsContext(PropertyObject settings) : IExtensionSettingsContext
{
    public PropertyObject Settings => settings;
}

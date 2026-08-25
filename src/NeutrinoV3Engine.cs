using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.NeutrinoV3;

public sealed class NeutrinoV3Engine : IVoiceSynthesisEngine, IExtensionSettings
{
    const string VoicebankPathsSetting = "voicebank_paths";
    const string LegacyRootSetting = "neutrino_root";
    internal const string StyleShiftAutomationId = "shfc";

    public IReadOnlyOrderedMap<string, VoiceSourceInfo> VoiceSourceInfos => mVoiceInfos;

    public ObjectConfig GetSettingsConfig(IExtensionSettingsContext context)
    {
        IReadOnlyList<IControllerConfig> pathElements;
        if (context.Settings.Map.ContainsKey(VoicebankPathsSetting))
        {
            int count = context.Settings.GetValue(VoicebankPathsSetting, PropertyArray.Empty).Count;
            pathElements = Enumerable.Range(0, count)
                .Select(_ => (IControllerConfig)TextBoxConfig.Create())
                .ToArray();
        }
        else
        {
            string legacyPath = NormalizeConfiguredPath(
                context.Settings.GetString(LegacyRootSetting, ""));
            pathElements = [TextBoxConfig.Create(legacyPath)];
        }

        var properties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            {
                (VoicebankPathsSetting, IsChinese ? "声库目录" : "Voicebank directories"),
                ListConfig.Create(
                    pathElements,
                    [new AddableElement(TextBoxConfig.Create())])
            },
        };
        return ObjectConfig.Create(properties);
    }

    public void ApplySettings(PropertyObject settings)
    {
        string[] configuredPaths = ReadConfiguredPaths(settings);
        lock (mGate)
        {
            if (mConfiguredPaths.SequenceEqual(configuredPaths, StringComparer.OrdinalIgnoreCase))
                return;

            if (mInitialized)
                ReloadCatalog(configuredPaths);
            mConfiguredPaths = configuredPaths;
        }
    }

    public void Init()
    {
        lock (mGate)
        {
            if (mInitialized)
                return;
            ReloadCatalog(mConfiguredPaths);
            mInitialized = true;
        }
    }

    public void Destroy()
    {
        lock (mGate)
        {
            foreach (var voicebank in mVoicebanks.Values)
                voicebank.Retire();
            mVoicebanks.Clear();
            mVoiceInfos.Clear();
            mInitialized = false;
        }
    }

    public IVoiceSynthesisSession CreateSession(IVoiceSynthesisContext context)
    {
        NeutrinoVoicebank voicebank;
        lock (mGate)
        {
            if (!mVoicebanks.TryGetValue(context.VoiceId, out voicebank!))
                throw new InvalidOperationException($"NEUTRINO v3 voicebank is unavailable: {context.VoiceId}");
            voicebank.Acquire();
        }
        try
        {
            return new NeutrinoV3Session(context, voicebank);
        }
        catch
        {
            voicebank.Release();
            throw;
        }
    }

    public IReadOnlyOrderedMap<PropertyKey, AutomationConfig> GetAutomationConfigs(
        IVoiceSynthesisPartPropertyContext context) => sAutomationConfigs;

    public IReadOnlyOrderedMap<PropertyKey, AutomationConfig> GetSynthesizedParameterConfigs(
        IVoiceSynthesisPartPropertyContext context) => [];

    public ObjectConfig GetPartPropertyConfig(IVoiceSynthesisPartPropertyContext context) =>
        ObjectConfig.Create([]);

    public ObjectConfig GetNotePropertyConfig(IVoiceSynthesisNotePropertyContext context) =>
        ObjectConfig.Create([]);

    public IReadOnlyMap<int, ObjectConfig> GetPhonemePropertyConfigs(
        IVoiceSynthesisNotePropertyContext context) => new Map<int, ObjectConfig>();

    void ReloadCatalog(IReadOnlyList<string> configuredPaths)
    {
        IReadOnlyList<string> searchPaths = NeutrinoVoicebankLocator.ResolveSearchPaths(configuredPaths);
        var found = NeutrinoVoicebankLocator.Scan(searchPaths);
        if (found.Count == 0)
        {
            throw new DirectoryNotFoundException(
                "No NEUTRINO v3 voicebanks were found. A configured directory may be a " +
                "NEUTRINO folder, its model folder, or one voicebank folder containing " +
                "t.bin, p.bin, s.bin and v.bin.");
        }

        string packageDictionary = Path.Combine(PackageDirectory, "Resources", "japanese.utf_8.table");
        if (!File.Exists(packageDictionary))
            throw new FileNotFoundException("The bundled NEUTRINO phoneme dictionary is missing.", packageDictionary);
        NeutrinoPhonemes.LoadDictionary(packageDictionary);

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var voicebank in found)
        {
            string preferred = voicebank.Id;
            string id = preferred;
            int suffix = 2;
            while (!usedIds.Add(id))
                id = $"{preferred}-{suffix++}";
            voicebank.Id = id;
        }

        foreach (var voicebank in mVoicebanks.Values)
            voicebank.Retire();
        mVoicebanks.Clear();
        mVoiceInfos.Clear();

        foreach (var voicebank in found)
        {
            mVoicebanks.Add(voicebank.Id, voicebank);
            mVoiceInfos.Add(voicebank.Id, new VoiceSourceInfo
            {
                Name = voicebank.Name,
                Description = voicebank.Description,
            });
        }
    }

    static bool IsChinese => TuneLabContext.Global.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    static string[] ReadConfiguredPaths(PropertyObject settings)
    {
        IEnumerable<string> values;
        if (settings.Map.ContainsKey(VoicebankPathsSetting))
        {
            values = settings.GetValue(VoicebankPathsSetting, PropertyArray.Empty)
                .Select(value => value.ToString(out string? path) ? path : "");
        }
        else
        {
            values = [settings.GetString(LegacyRootSetting, "")];
        }

        return values
            .Select(NormalizeConfiguredPath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static string NormalizeConfiguredPath(string path) => path.Trim().Trim('"');

    internal static string PackageDirectory =>
        Path.GetDirectoryName(typeof(NeutrinoV3Engine).Assembly.Location)!;

    readonly object mGate = new();
    readonly OrderedMap<string, VoiceSourceInfo> mVoiceInfos = new();
    readonly Dictionary<string, NeutrinoVoicebank> mVoicebanks = new(StringComparer.OrdinalIgnoreCase);
    string[] mConfiguredPaths = [];
    bool mInitialized;

    static readonly OrderedMap<PropertyKey, AutomationConfig> sAutomationConfigs = new()
    {
        {
            (StyleShiftAutomationId, "SHFC (cent)"),
            AutomationConfig.Create(-1200, 1200)
                .WithDefault(0)
                .WithColor("#58A6A6")
                .WithFormat(NumberFormat.Decimals(0))
                .WithMinLabel("-12 st")
                .WithMaxLabel("+12 st")
        },
    };
}

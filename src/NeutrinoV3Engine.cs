using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.NeutrinoV3;

public sealed class NeutrinoV3Engine : IVoiceSynthesisEngine, IExtensionSettings
{
    const string RootSetting = "neutrino_root";
    internal const string StyleShiftAutomationId = "shfc";

    public IReadOnlyOrderedMap<string, VoiceSourceInfo> VoiceSourceInfos => mVoiceInfos;

    public ObjectConfig GetSettingsConfig(IExtensionSettingsContext context)
    {
        var properties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            {
                (RootSetting, IsChinese ? "NEUTRINO 目录" : "NEUTRINO directory"),
                TextBoxConfig.Create("")
            },
        };
        return ObjectConfig.Create(properties);
    }

    public void ApplySettings(PropertyObject settings)
    {
        string configured = settings.GetString(RootSetting, "").Trim().Trim('"');
        lock (mGate)
        {
            if (string.Equals(configured, mConfiguredRoot, StringComparison.OrdinalIgnoreCase))
                return;

            mConfiguredRoot = configured;
            if (mInitialized)
                ReloadCatalog();
        }
    }

    public void Init()
    {
        lock (mGate)
        {
            if (mInitialized)
                return;
            ReloadCatalog();
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

    void ReloadCatalog()
    {
        string root = NeutrinoVoicebankLocator.ResolveRoot(mConfiguredRoot);
        var found = NeutrinoVoicebankLocator.Scan(root);
        if (found.Count == 0)
        {
            throw new DirectoryNotFoundException(
                $"No NEUTRINO v3 voicebanks were found under '{root}'. " +
                "The directory must contain model/<voice>/t.bin, p.bin, s.bin and v.bin.");
        }

        string packageDictionary = Path.Combine(PackageDirectory, "Resources", "japanese.utf_8.table");
        string rootDictionary = Path.Combine(root, "settings", "dic", "japanese.utf_8.table");
        NeutrinoPhonemes.LoadDictionary(File.Exists(rootDictionary) ? rootDictionary : packageDictionary);

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

    internal static string PackageDirectory =>
        Path.GetDirectoryName(typeof(NeutrinoV3Engine).Assembly.Location)!;

    readonly object mGate = new();
    readonly OrderedMap<string, VoiceSourceInfo> mVoiceInfos = new();
    readonly Dictionary<string, NeutrinoVoicebank> mVoicebanks = new(StringComparer.OrdinalIgnoreCase);
    string mConfiguredRoot = string.Empty;
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

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TuneLab.NeutrinoV3;

internal sealed class NeutrinoVoicebank : IDisposable
{
    public required string Id { get; set; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string ModelDirectory { get; init; }
    public required int BottomKey { get; init; }
    public required int TopKey { get; init; }

    public float[] RunTiming(IReadOnlyCollection<NamedOnnxValue> inputs) =>
        GetModels().RunTiming(inputs);

    public float[] RunPitch(IReadOnlyCollection<NamedOnnxValue> inputs) =>
        GetModels().RunPitch(inputs);

    public float[] RunMelspec(IReadOnlyCollection<NamedOnnxValue> inputs) =>
        GetModels().RunMelspec(inputs);

    public float[] RunVocoder(IReadOnlyCollection<NamedOnnxValue> inputs) =>
        GetModels().RunVocoder(inputs);

    public void Acquire()
    {
        lock (mGate)
        {
            if (mRetired || mDisposed)
                throw new ObjectDisposedException(Name);
            mLeaseCount++;
        }
    }

    public void Release()
    {
        lock (mGate)
        {
            if (mLeaseCount > 0)
                mLeaseCount--;
            if (mRetired && mLeaseCount == 0)
                DisposeCore();
        }
    }

    public void Retire()
    {
        lock (mGate)
        {
            mRetired = true;
            if (mLeaseCount == 0)
                DisposeCore();
        }
    }

    NeutrinoOnnxModels GetModels()
    {
        lock (mGate)
        {
            ObjectDisposedException.ThrowIf(mDisposed, this);
            return mModels ??= new NeutrinoOnnxModels(ModelDirectory);
        }
    }

    public void Dispose()
    {
        lock (mGate)
        {
            mRetired = true;
            DisposeCore();
        }
    }

    void DisposeCore()
    {
        if (mDisposed)
            return;
        mDisposed = true;
        mModels?.Dispose();
        mModels = null;
    }

    readonly object mGate = new();
    NeutrinoOnnxModels? mModels;
    int mLeaseCount;
    bool mRetired;
    bool mDisposed;
}

internal sealed class NeutrinoOnnxModels : IDisposable
{
    public NeutrinoOnnxModels(string modelDirectory)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableCpuMemArena = true,
            EnableMemoryPattern = true,
        };
        try
        {
            mTiming = new InferenceSession(Path.Combine(modelDirectory, "t.bin"), options);
            mPitch = new InferenceSession(Path.Combine(modelDirectory, "p.bin"), options);
            mMelspec = new InferenceSession(Path.Combine(modelDirectory, "s.bin"), options);
            mVocoder = new InferenceSession(Path.Combine(modelDirectory, "v.bin"), options);
        }
        catch
        {
            mTiming?.Dispose();
            mPitch?.Dispose();
            mMelspec?.Dispose();
            mVocoder?.Dispose();
            throw;
        }
        finally
        {
            options.Dispose();
        }
    }

    public float[] RunTiming(IReadOnlyCollection<NamedOnnxValue> inputs) => Run(mTiming!, mTimingGate, inputs);
    public float[] RunPitch(IReadOnlyCollection<NamedOnnxValue> inputs) => Run(mPitch!, mPitchGate, inputs);
    public float[] RunMelspec(IReadOnlyCollection<NamedOnnxValue> inputs) => Run(mMelspec!, mMelspecGate, inputs);
    public float[] RunVocoder(IReadOnlyCollection<NamedOnnxValue> inputs) => Run(mVocoder!, mVocoderGate, inputs);

    static float[] Run(
        InferenceSession session,
        object gate,
        IReadOnlyCollection<NamedOnnxValue> inputs)
    {
        lock (gate)
        {
            using var outputs = session.Run(inputs);
            return outputs.First().AsTensor<float>().ToArray();
        }
    }

    public void Dispose()
    {
        mTiming?.Dispose();
        mPitch?.Dispose();
        mMelspec?.Dispose();
        mVocoder?.Dispose();
        mTiming = null;
        mPitch = null;
        mMelspec = null;
        mVocoder = null;
    }

    readonly object mTimingGate = new();
    readonly object mPitchGate = new();
    readonly object mMelspecGate = new();
    readonly object mVocoderGate = new();
    InferenceSession? mTiming;
    InferenceSession? mPitch;
    InferenceSession? mMelspec;
    InferenceSession? mVocoder;
}

internal static class NeutrinoVoicebankLocator
{
    public static string ResolveRoot(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string? explicitRoot = NormalizePath(configured);
            if (explicitRoot is not null &&
                Directory.Exists(explicitRoot) &&
                ScanModelDirectories(explicitRoot).Any())
            {
                return explicitRoot;
            }
            throw new DirectoryNotFoundException($"The configured NEUTRINO directory is invalid: {configured}");
        }

        var candidates = new List<string>();
        Add(Environment.GetEnvironmentVariable("NEUTRINO_HOME"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NEUTRINO"));
        Add(@"C:\NEUTRINO");
        Add(@"D:\NEUTRINO");

        var package = new DirectoryInfo(NeutrinoV3Engine.PackageDirectory);
        for (int i = 0; i < 5 && package != null; i++, package = package.Parent)
            Add(package.FullName);

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate) && ScanModelDirectories(candidate).Any())
                return candidate;
        }

        throw new DirectoryNotFoundException(
            "NEUTRINO directory was not found automatically. Set it in Settings > Extensions.");

        void Add(string? path)
        {
            string? expanded = NormalizePath(path);
            if (expanded is null)
                return;
            if (!candidates.Contains(expanded, StringComparer.OrdinalIgnoreCase))
                candidates.Add(expanded);
        }
    }

    static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            return Path.GetFullPath(expanded);
        }
        catch
        {
            return null;
        }
    }

    public static List<NeutrinoVoicebank> Scan(string root)
    {
        var result = new List<NeutrinoVoicebank>();
        foreach (string modelDirectory in ScanModelDirectories(root)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var metadata = ReadMetadata(Path.Combine(modelDirectory, "info.toml"));
            string folderName = new DirectoryInfo(modelDirectory).Name;
            string name = string.IsNullOrWhiteSpace(metadata.Name) ? folderName : metadata.Name;
            string description = $"NEUTRINO {metadata.Version} / {metadata.Language}".TrimEnd(' ', '/');
            result.Add(new NeutrinoVoicebank
            {
                Id = folderName,
                Name = name,
                Description = description,
                ModelDirectory = modelDirectory,
                BottomKey = metadata.BottomKey,
                TopKey = metadata.TopKey,
            });
        }
        return result;
    }

    static IEnumerable<string> ScanModelDirectories(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        if (IsV3ModelDirectory(root))
            yield return root;

        string modelRoot = Path.Combine(root, "model");
        if (Directory.Exists(modelRoot))
        {
            if (IsV3ModelDirectory(modelRoot))
                yield return modelRoot;
            foreach (string directory in Directory.EnumerateDirectories(modelRoot))
            {
                if (IsV3ModelDirectory(directory))
                    yield return directory;
                string nested = Path.Combine(directory, "model");
                if (IsV3ModelDirectory(nested))
                    yield return nested;
            }
        }

        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            if (IsV3ModelDirectory(directory))
                yield return directory;
            string nested = Path.Combine(directory, "model");
            if (IsV3ModelDirectory(nested))
                yield return nested;
        }
    }

    static bool IsV3ModelDirectory(string directory) =>
        File.Exists(Path.Combine(directory, "t.bin")) &&
        File.Exists(Path.Combine(directory, "p.bin")) &&
        File.Exists(Path.Combine(directory, "s.bin")) &&
        File.Exists(Path.Combine(directory, "v.bin"));

    static Metadata ReadMetadata(string path)
    {
        var result = new Metadata();
        if (!File.Exists(path))
            return result;

        string section = string.Empty;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals < 0)
                continue;
            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim().Trim('"');
            if (section.Length == 0)
            {
                if (key == "version") result.Version = value;
                else if (key == "top_key" && int.TryParse(value, out int top)) result.TopKey = top;
                else if (key == "bottom_key" && int.TryParse(value, out int bottom)) result.BottomKey = bottom;
            }
            else if (section == "speaker")
            {
                if (key == "name") result.Name = value;
                else if (key == "language") result.Language = value;
            }
        }
        return result;
    }

    sealed class Metadata
    {
        public string Name = string.Empty;
        public string Version = "v3";
        public string Language = "Japanese";
        public int BottomKey = 41;
        public int TopKey = 86;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Autocaster.Core;

namespace Autocaster.Infrastructure;

public sealed class ProfileService(string folder)
{
    public const long MaxFileBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _writes = new(1);
    private readonly Dictionary<Guid, byte[]> _loaded = [];
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        MaxDepth = 48,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public async Task<(IReadOnlyList<ScenarioProfile> Profiles, IReadOnlyList<string> Errors)> LoadAll()
    {
        Directory.CreateDirectory(folder);
        List<ScenarioProfile> profiles = [];
        List<string> errors = [];
        foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                var bytes = await ReadBytes(path).ConfigureAwait(false);
                var profile = Parse(bytes);
                if (!string.Equals(Path.GetFileName(path), FileName(profile.Id), StringComparison.OrdinalIgnoreCase) ||
                    profiles.Any(item => item.Id == profile.Id))
                    throw new InvalidDataException("The filename does not match the config ID.");
                _loaded[profile.Id] = bytes;
                profiles.Add(profile);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ScenarioException)
            {
                errors.Add($"{Path.GetFileName(path)}: {error.Message}");
            }
        }
        return (profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), errors);
    }

    public static async Task<ScenarioProfile> Import(string path) => Parse(await ReadBytes(path).ConfigureAwait(false));

    public static Task Export(string path, ScenarioProfile profile)
    {
        ScenarioCompiler.ValidateProfile(profile);
        return AtomicWrite(path, Serialize(profile));
    }

    public async Task Save(ScenarioProfile profile)
    {
        ScenarioCompiler.ValidateProfile(profile);
        var bytes = Serialize(profile);
        string path = Path.Combine(folder, FileName(profile.Id));
        await _writes.WaitAsync().ConfigureAwait(false);
        try
        {
            await CheckExternalChange(profile.Id, path).ConfigureAwait(false);
            Directory.CreateDirectory(folder);
            await AtomicWrite(path, bytes).ConfigureAwait(false);
            _loaded[profile.Id] = bytes;
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task Delete(Guid id)
    {
        string path = Path.Combine(folder, FileName(id));
        await _writes.WaitAsync().ConfigureAwait(false);
        try
        {
            await CheckExternalChange(id, path).ConfigureAwait(false);
            File.Delete(path);
            _loaded.Remove(id);
        }
        finally
        {
            _writes.Release();
        }
    }

    private async Task CheckExternalChange(Guid id, string path)
    {
        bool exists = File.Exists(path);
        bool known = _loaded.TryGetValue(id, out var previous);
        if (exists != known || (exists && !(await ReadBytes(path).ConfigureAwait(false)).AsSpan().SequenceEqual(previous)))
            throw new IOException("The config changed outside the application. Restart to load the updated file.");
    }

    private static string FileName(Guid id) => $"{id:N}.json";

    private static byte[] Serialize(ScenarioProfile profile)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profile, Json);
        if (bytes.Length > MaxFileBytes)
            throw new InvalidDataException("The config exceeds 8 MB.");
        return bytes;
    }

    private static ScenarioProfile Parse(byte[] bytes)
    {
        var profile = JsonSerializer.Deserialize<ScenarioProfile>(bytes, Json) ?? throw new InvalidDataException("Empty config.");
        return FlatScenario.Convert(profile);
    }

    private static async Task<byte[]> ReadBytes(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxFileBytes)
            throw new InvalidDataException("The config exceeds 8 MB.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        return bytes;
    }

    public static async Task AtomicWrite(string path, byte[] bytes)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}

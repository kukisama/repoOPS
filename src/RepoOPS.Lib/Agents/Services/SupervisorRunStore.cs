using System.Text.Json;
using System.Text.Json.Serialization;
using RepoOPS.Agents.Models;

namespace RepoOPS.Agents.Services;

public sealed class SupervisorRunStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Lock _syncRoot = new();
    private readonly string _runsPath = Path.Combine(GetBaseDir(), "agent-runs.json");
    private List<SupervisorRun>? _cache;

    public IReadOnlyList<SupervisorRun> GetAll()
    {
        lock (_syncRoot)
        {
            _cache ??= LoadFromDisk();
            return _cache
                .OrderByDescending(r => r.UpdatedAt)
                .Select(Clone)
                .ToList();
        }
    }

    public SupervisorRun? Get(string runId)
    {
        lock (_syncRoot)
        {
            _cache ??= LoadFromDisk();
            var run = _cache.FirstOrDefault(r => string.Equals(r.RunId, runId, StringComparison.OrdinalIgnoreCase));
            return run is null ? null : Clone(run);
        }
    }

    public SupervisorRun Upsert(SupervisorRun run)
    {
        lock (_syncRoot)
        {
            _cache ??= LoadFromDisk();
            run.UpdatedAt = DateTime.UtcNow;

            var index = _cache.FindIndex(r => string.Equals(r.RunId, run.RunId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _cache[index] = Clone(run);
            }
            else
            {
                _cache.Add(Clone(run));
            }

            PersistUnsafe();
            return Clone(run);
        }
    }

    private List<SupervisorRun> LoadFromDisk()
    {
        if (!File.Exists(_runsPath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_runsPath);
            var runs = JsonSerializer.Deserialize<List<SupervisorRun>>(json, s_jsonOptions);
            return runs ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void PersistUnsafe()
    {
        var json = JsonSerializer.Serialize(_cache ?? [], s_jsonOptions);
        File.WriteAllText(_runsPath, json);
    }

    private static string GetBaseDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var dir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                baseDir = dir;
            }
        }

        if (!File.Exists(Path.Combine(baseDir, "tasks.json")))
        {
            baseDir = Directory.GetCurrentDirectory();
        }

        return baseDir;
    }

    private static SupervisorRun Clone(SupervisorRun run)
    {
        var json = JsonSerializer.Serialize(run, s_jsonOptions);
        return JsonSerializer.Deserialize<SupervisorRun>(json, s_jsonOptions) ?? new SupervisorRun();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using RepoOPS.Agents.Models;

namespace RepoOPS.Agents.Services;

public sealed class AssistantPlanStore
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
    private readonly string _plansPath = Path.Combine(GetBaseDir(), "assistant-plans.json");
    private List<AssistantPlan>? _cache;

    public IReadOnlyList<AssistantPlan> GetAll()
    {
        lock (_syncRoot)
        {
            _cache ??= LoadFromDisk();
            return _cache
                .OrderByDescending(item => item.UpdatedAt)
                .Select(Clone)
                .ToList();
        }
    }

    public AssistantPlan? Get(string planId)
    {
        lock (_syncRoot)
        {
            _cache ??= LoadFromDisk();
            var plan = _cache.FirstOrDefault(item => string.Equals(item.PlanId, planId, StringComparison.OrdinalIgnoreCase));
            return plan is null ? null : Clone(plan);
        }
    }

    public AssistantPlan Upsert(AssistantPlan plan)
    {
        lock (_syncRoot)
        {
            _cache ??= LoadFromDisk();
            plan.UpdatedAt = DateTime.UtcNow;

            var index = _cache.FindIndex(item => string.Equals(item.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _cache[index] = Clone(plan);
            }
            else
            {
                _cache.Add(Clone(plan));
            }

            PersistUnsafe();
            return Clone(plan);
        }
    }

    private List<AssistantPlan> LoadFromDisk()
    {
        if (!File.Exists(_plansPath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_plansPath);
            return JsonSerializer.Deserialize<List<AssistantPlan>>(json, s_jsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void PersistUnsafe()
    {
        File.WriteAllText(_plansPath, JsonSerializer.Serialize(_cache ?? [], s_jsonOptions));
    }

    private static AssistantPlan Clone(AssistantPlan plan)
    {
        var json = JsonSerializer.Serialize(plan, s_jsonOptions);
        return JsonSerializer.Deserialize<AssistantPlan>(json, s_jsonOptions) ?? new AssistantPlan();
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
}

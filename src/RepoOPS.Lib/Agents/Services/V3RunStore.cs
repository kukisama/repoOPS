using System.Text.Json;
using System.Text.Json.Serialization;
using RepoOPS.Agents.Models;

namespace RepoOPS.Agents.Services;

public sealed class V3RunStore
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
	private readonly string _registryPath = Path.Combine(GetBaseDir(), "v3-runs-registry.json");
	private List<V3RunRegistryEntry>? _cache;

	public IReadOnlyList<V3PairRun> GetAll()
	{
		lock (_syncRoot)
		{
			_cache ??= LoadRegistryFromDisk();
			var runs = LoadRunsAndPruneUnsafe();
			return runs
				.OrderByDescending(item => item.UpdatedAt)
				.Select(Clone)
				.ToList();
		}
	}

	public V3PairRun? Get(string runId)
	{
		lock (_syncRoot)
		{
			_cache ??= LoadRegistryFromDisk();
			var runs = LoadRunsAndPruneUnsafe();
			var run = runs.FirstOrDefault(item => string.Equals(item.RunId, runId, StringComparison.OrdinalIgnoreCase));
			return run is null ? null : Clone(run);
		}
	}

	public void Upsert(V3PairRun run)
	{
		lock (_syncRoot)
		{
			_cache ??= LoadRegistryFromDisk();
			var runJsonPath = GetRunJsonPath(run);
			var artifactRoot = Path.GetDirectoryName(runJsonPath) ?? string.Empty;
			var entry = new V3RunRegistryEntry
			{
				RunId = run.RunId,
				WorkspaceRoot = run.WorkspaceRoot,
				RunJsonPath = runJsonPath,
				ArtifactRoot = artifactRoot,
				UpdatedAt = run.UpdatedAt
			};

			var index = _cache.FindIndex(item => string.Equals(item.RunId, run.RunId, StringComparison.OrdinalIgnoreCase));
			if (index >= 0)
			{
				_cache[index] = entry;
			}
			else
			{
				_cache.Add(entry);
			}

			PersistRegistryUnsafe();
		}
	}

	public void Delete(string runId)
	{
		lock (_syncRoot)
		{
			_cache ??= LoadRegistryFromDisk();
			_cache.RemoveAll(item => string.Equals(item.RunId, runId, StringComparison.OrdinalIgnoreCase));
			PersistRegistryUnsafe();
		}
	}

	public static string GetRunJsonPath(V3PairRun run)
	{
		var workspaceRoot = string.IsNullOrWhiteSpace(run.WorkspaceRoot) ? GetBaseDir() : run.WorkspaceRoot!;
		return Path.Combine(workspaceRoot, ".repoops", "v3", "runs", run.RunId, "run.json");
	}

	private List<V3RunRegistryEntry> LoadRegistryFromDisk()
	{
		if (!File.Exists(_registryPath))
		{
			return [];
		}

		try
		{
			var json = File.ReadAllText(_registryPath);
			return JsonSerializer.Deserialize<List<V3RunRegistryEntry>>(json, s_jsonOptions) ?? [];
		}
		catch
		{
			return [];
		}
	}

	private List<V3PairRun> LoadRunsAndPruneUnsafe()
	{
		var runs = new List<V3PairRun>();
		var mutated = false;

		foreach (var entry in _cache ?? [])
		{
			if (string.IsNullOrWhiteSpace(entry.RunJsonPath) || !File.Exists(entry.RunJsonPath))
			{
				mutated = true;
				continue;
			}

			try
			{
				var json = File.ReadAllText(entry.RunJsonPath);
				var run = JsonSerializer.Deserialize<V3PairRun>(json, s_jsonOptions);
				if (run is null)
				{
					mutated = true;
					continue;
				}

				runs.Add(SanitizeLoadedRun(run));
			}
			catch
			{
				mutated = true;
			}
		}

		if (mutated)
		{
			var surviving = runs.Select(run => new V3RunRegistryEntry
			{
				RunId = run.RunId,
				WorkspaceRoot = run.WorkspaceRoot,
				RunJsonPath = GetRunJsonPath(run),
				ArtifactRoot = Path.GetDirectoryName(GetRunJsonPath(run)) ?? string.Empty,
				UpdatedAt = run.UpdatedAt
			}).ToList();

			_cache = surviving;
			PersistRegistryUnsafe();
		}

		return runs;
	}

	private void PersistRegistryUnsafe()
	{
		File.WriteAllText(_registryPath, JsonSerializer.Serialize(_cache ?? [], s_jsonOptions));
	}

	private static V3PairRun SanitizeLoadedRun(V3PairRun run)
	{
		run.RecoveredFromStorage = true;
		run.MainThreadSessionId = null;
		run.SubThreadSessionId = null;
		run.MainThreadCommandPreview = null;
		run.SubThreadCommandPreview = null;
		run.MainThreadStatus = "idle";
		run.SubThreadStatus = "idle";

		if (run.AwaitingInitialApproval && !string.IsNullOrWhiteSpace(run.InitialPlanTaskCard))
		{
			run.Status = "awaiting-approval";
			run.InitialPlanStatus ??= "pending";
			return run;
		}

		if (!string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(run.Status, "stopped", StringComparison.OrdinalIgnoreCase))
		{
			run.Status = "stopped";
		}

		return run;
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

	private static V3PairRun Clone(V3PairRun run)
	{
		var json = JsonSerializer.Serialize(run, s_jsonOptions);
		return JsonSerializer.Deserialize<V3PairRun>(json, s_jsonOptions) ?? new V3PairRun();
	}

	private sealed class V3RunRegistryEntry
	{
		public string RunId { get; set; } = string.Empty;
		public string? WorkspaceRoot { get; set; }
		public string RunJsonPath { get; set; } = string.Empty;
		public string ArtifactRoot { get; set; } = string.Empty;
		public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
	}
}
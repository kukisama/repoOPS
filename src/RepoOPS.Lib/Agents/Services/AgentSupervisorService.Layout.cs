namespace RepoOPS.Agents.Services;

public sealed partial class AgentSupervisorService
{
    private static string GetWorkerSurfaceId(string workerId)
        => $"surface:worker:{workerId}";

    private static void EnsureRunLayout(RepoOPS.Agents.Models.SupervisorRun run, RepoOPS.Agents.Models.SupervisorSettings settings)
    {
        run.Lanes ??= [];
        run.Attention ??= [];
        run.VerificationHistory ??= [];
        run.Workers ??= [];
            var shouldShowVerificationSurface = settings.EnableVerificationSurface || run.LastVerification is not null || run.VerificationHistory.Count > 0;

        if (settings.AutoCreateDefaultLanes)
        {
            EnsureLane(run, AgentsLaneId, settings.AgentLaneName, "workers", 0);

            if (settings.EnableCoordinatorSurface)
            {
                EnsureLane(run, ControlLaneId, settings.ControlLaneName, "control", 1);
            }

            if (shouldShowVerificationSurface)
            {
                EnsureLane(run, VerificationLaneId, settings.VerificationLaneName, "verification", 2);
            }
        }

        if (string.IsNullOrWhiteSpace(run.ActiveLaneId))
        {
            run.ActiveLaneId = settings.EnableCoordinatorSurface ? ControlLaneId : AgentsLaneId;
        }

        if (string.IsNullOrWhiteSpace(run.ActiveSurfaceId))
        {
            run.ActiveSurfaceId = settings.EnableCoordinatorSurface ? CoordinatorSurfaceId : run.Workers.Select(worker => GetWorkerSurfaceId(worker.WorkerId)).FirstOrDefault();
        }
    }

    private static void EnsureLane(RepoOPS.Agents.Models.SupervisorRun run, string laneId, string name, string kind, int order)
    {
        if (run.Lanes.Any(lane => string.Equals(lane.LaneId, laneId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        run.Lanes.Add(new RepoOPS.Agents.Models.ExecutionLane
        {
            LaneId = laneId,
            RunId = run.RunId,
            Name = name,
            Kind = kind,
            Order = order
        });
    }

    private static string ResolveLaneIdForSurface(RepoOPS.Agents.Models.SupervisorRun run, string surfaceId, RepoOPS.Agents.Models.SupervisorSettings settings)
    {
        if (string.Equals(surfaceId, CoordinatorSurfaceId, StringComparison.OrdinalIgnoreCase))
        {
            return ControlLaneId;
        }

        if (string.Equals(surfaceId, VerificationSurfaceId, StringComparison.OrdinalIgnoreCase))
        {
            return VerificationLaneId;
        }

        if (run.Workers.Any(worker => string.Equals(GetWorkerSurfaceId(worker.WorkerId), surfaceId, StringComparison.OrdinalIgnoreCase)))
        {
            return AgentsLaneId;
        }

        return settings.EnableCoordinatorSurface ? ControlLaneId : AgentsLaneId;
    }

    private static void AcknowledgeSurfaceAttention(RepoOPS.Agents.Models.SupervisorRun run, string surfaceId)
    {
        foreach (var attention in run.Attention.Where(item => string.Equals(item.SurfaceId, surfaceId, StringComparison.OrdinalIgnoreCase) && !item.IsRead))
        {
            attention.IsRead = true;
            attention.AcknowledgedAt ??= DateTime.UtcNow;
        }
    }

    private static void RecalculateAttentionAggregates(RepoOPS.Agents.Models.SupervisorRun run, RepoOPS.Agents.Models.SupervisorSettings settings)
    {
        EnsureRunLayout(run, settings);

        if (run.Attention.Count > settings.MaxAttentionEvents)
        {
            run.Attention = run.Attention
                .OrderByDescending(item => item.CreatedAt)
                .Take(settings.MaxAttentionEvents)
                .OrderBy(item => item.CreatedAt)
                .ToList();
        }

        foreach (var worker in run.Workers)
        {
            var surfaceId = GetWorkerSurfaceId(worker.WorkerId);
            var workerEvents = run.Attention
                .Where(item => !item.IsResolved && string.Equals(item.SurfaceId, surfaceId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAt)
                .ToList();

            worker.NeedsAttention = workerEvents.Count > 0;
            worker.UnreadCount = workerEvents.Count(item => !item.IsRead);
            worker.AttentionLevel = workerEvents.Select(item => item.Level)
                .OrderByDescending(AttentionSeverity)
                .FirstOrDefault();
            worker.LastAttentionMessage = workerEvents.FirstOrDefault()?.Message;
        }

        foreach (var lane in run.Lanes)
        {
            var laneEvents = run.Attention
                .Where(item => !item.IsResolved && string.Equals(ResolveLaneIdForSurface(run, item.SurfaceId ?? string.Empty, settings), lane.LaneId, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Level)
                .OrderByDescending(AttentionSeverity)
                .FirstOrDefault();
            lane.LatestAttentionLevel = laneEvents;
        }

        if (settings.SuggestFocusOnAttention)
        {
            var nextSurface = run.Attention
                .Where(item => !item.IsResolved)
                .OrderByDescending(item => AttentionSeverity(item.Level))
                .ThenByDescending(item => item.CreatedAt)
                .Select(item => item.SurfaceId)
                .FirstOrDefault(surfaceId => !string.IsNullOrWhiteSpace(surfaceId));
            run.FocusSuggestionSurfaceId = nextSurface;
        }
        else
        {
            run.FocusSuggestionSurfaceId = null;
        }
    }

    private RepoOPS.Agents.Models.RunSnapshot BuildSnapshot(RepoOPS.Agents.Models.SupervisorRun run, RepoOPS.Agents.Models.SupervisorSettings settings)
    {
        EnsureRunLayout(run, settings);
        RecalculateAttentionAggregates(run, settings);

        var assistantPlan = TryGetAssistantPlanForRun(run);
        var surfaces = BuildSurfaces(run, settings);
        var lanes = run.Lanes
            .OrderBy(lane => lane.Order)
            .ThenBy(lane => lane.Name)
            .Select(lane => new RepoOPS.Agents.Models.ExecutionLane
            {
                LaneId = lane.LaneId,
                RunId = lane.RunId,
                Name = lane.Name,
                Kind = lane.Kind,
                Order = lane.Order,
                Collapsed = lane.Collapsed,
                SurfaceIds = surfaces.Where(surface => string.Equals(surface.LaneId, lane.LaneId, StringComparison.OrdinalIgnoreCase)).Select(surface => surface.SurfaceId).ToList(),
                LatestAttentionLevel = lane.LatestAttentionLevel
            })
            .ToList();

        return new RepoOPS.Agents.Models.RunSnapshot
        {
            Run = run,
            Lanes = lanes,
            Surfaces = surfaces,
            Attention = run.Attention.OrderByDescending(item => item.CreatedAt).ToList(),
            Decisions = run.Decisions.OrderByDescending(item => item.CreatedAt).ToList(),
            Verifications = run.VerificationHistory.OrderByDescending(item => item.CompletedAt).ToList(),
            AssistantPlan = assistantPlan,
            AssistantArtifacts = BuildAssistantArtifactStatuses(run, assistantPlan),
            RoundHistoryContent = TryReadRoundHistoryContent(run),
            Summary = new RepoOPS.Agents.Models.RunSnapshotSummary
            {
                RunningSurfaces = surfaces.Count(item => string.Equals(item.Status, "running", StringComparison.OrdinalIgnoreCase)),
                QueuedSurfaces = surfaces.Count(item => string.Equals(item.Status, "queued", StringComparison.OrdinalIgnoreCase)),
                CompletedSurfaces = surfaces.Count(item => string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase)),
                FailedSurfaces = surfaces.Count(item => string.Equals(item.Status, "failed", StringComparison.OrdinalIgnoreCase)),
                NeedsAttention = surfaces.Count(item => item.NeedsAttention),
                UnreadAttention = run.Attention.Count(item => !item.IsRead),
                ResolvedAttention = run.Attention.Count(item => item.IsResolved)
            }
        };
    }

    private static List<RepoOPS.Agents.Models.AssistantArtifactStatus> BuildAssistantArtifactStatuses(
        RepoOPS.Agents.Models.SupervisorRun run,
        RepoOPS.Agents.Models.AssistantPlan? plan)
    {
        if (plan is null)
        {
            return [];
        }

        var workspaceRoot = string.IsNullOrWhiteSpace(run.WorkspaceRoot)
            ? plan.ExecutionRoot ?? AgentRoleConfigService.GetBaseDir()
            : run.WorkspaceRoot!;
        var statuses = new List<RepoOPS.Agents.Models.AssistantArtifactStatus>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddArtifact(RepoOPS.Agents.Models.AssistantRoundPlan round, string? artifactName, bool isRoundDeliverable, bool isRoleOutput, string? roleId = null, string? roleName = null)
        {
            if (string.IsNullOrWhiteSpace(artifactName))
            {
                return;
            }

            var resolvedPath = Path.IsPathRooted(artifactName)
                ? artifactName
                : Path.Combine(workspaceRoot, artifactName);
            resolvedPath = Path.GetFullPath(resolvedPath);
            var uniqueKey = $"{round.RoundId}|{resolvedPath}|{roleId}|{isRoundDeliverable}|{isRoleOutput}";
            if (!seen.Add(uniqueKey))
            {
                return;
            }

            statuses.Add(new RepoOPS.Agents.Models.AssistantArtifactStatus
            {
                RoundId = round.RoundId,
                RoundNumber = round.RoundNumber,
                ArtifactName = artifactName,
                ArtifactPath = resolvedPath,
                Exists = File.Exists(resolvedPath),
                IsRoundDeliverable = isRoundDeliverable,
                IsRoleOutput = isRoleOutput,
                RoleId = roleId,
                RoleName = roleName
            });
        }

        foreach (var round in plan.Rounds.OrderBy(item => item.RoundNumber))
        {
            foreach (var deliverable in round.Deliverables)
            {
                AddArtifact(round, deliverable, isRoundDeliverable: true, isRoleOutput: false);
            }

            foreach (var role in round.Roles)
            {
                AddArtifact(round, role.OutputArtifact, isRoundDeliverable: false, isRoleOutput: true, role.RoleId, role.RoleName);
            }
        }

        return statuses
            .OrderBy(item => item.RoundNumber)
            .ThenBy(item => item.ArtifactName)
            .ToList();
    }

    private static string? TryReadRoundHistoryContent(RepoOPS.Agents.Models.SupervisorRun run)
    {
        if (string.IsNullOrWhiteSpace(run.RoundHistoryDocumentPath))
        {
            return null;
        }

        try
        {
            return File.Exists(run.RoundHistoryDocumentPath)
                ? File.ReadAllText(run.RoundHistoryDocumentPath)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private List<RepoOPS.Agents.Models.ExecutionSurface> BuildSurfaces(RepoOPS.Agents.Models.SupervisorRun run, RepoOPS.Agents.Models.SupervisorSettings settings)
    {
        var surfaces = new List<RepoOPS.Agents.Models.ExecutionSurface>();

        if (settings.EnableCoordinatorSurface)
        {
            var liveState = GetSupervisorLiveState(run.RunId);
            var coordinatorEvents = run.Attention
                .Where(item => !item.IsResolved && string.Equals(item.SurfaceId, CoordinatorSurfaceId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAt)
                .ToList();
            surfaces.Add(new RepoOPS.Agents.Models.ExecutionSurface
            {
                SurfaceId = CoordinatorSurfaceId,
                RunId = run.RunId,
                LaneId = ControlLaneId,
                Type = "coordinator",
                Status = liveState is null ? run.Status : "running",
                DisplayName = "Coordinator",
                WorkspacePath = run.WorkspaceRoot,
                CurrentTask = liveState?.Title,
                CommandPreview = liveState?.CommandPreview ?? run.LastSupervisorCommandPreview,
                LastSummary = run.LatestSummary,
                LastReportedStatus = liveState is null ? null : "streaming",
                LastOutputPreview = liveState?.GetPreview(6000),
                NeedsAttention = coordinatorEvents.Count > 0,
                UnreadCount = coordinatorEvents.Count(item => !item.IsRead),
                AttentionLevel = coordinatorEvents.OrderByDescending(item => AttentionSeverity(item.Level)).Select(item => item.Level).FirstOrDefault(),
                LastAttentionMessage = coordinatorEvents.FirstOrDefault()?.Message,
                LastActivityAt = liveState?.UpdatedAt ?? run.Decisions.OrderByDescending(item => item.CreatedAt).Select(item => (DateTime?)item.CreatedAt).FirstOrDefault(),
                StartedAt = liveState?.StartedAt ?? run.CreatedAt,
                UpdatedAt = liveState?.UpdatedAt ?? run.UpdatedAt
            });
        }

        surfaces.AddRange(run.Workers
            .Where(worker => settings.ShowCompletedSurfaces || worker.Status is not "completed" and not "stopped")
            .Select(worker => new RepoOPS.Agents.Models.ExecutionSurface
            {
                SurfaceId = GetWorkerSurfaceId(worker.WorkerId),
                RunId = run.RunId,
                LaneId = AgentsLaneId,
                Type = "agent-worker",
                Status = worker.Status,
                DisplayName = worker.RoleName,
                RoleId = worker.RoleId,
                WorkerId = worker.WorkerId,
                SessionId = worker.SessionId,
                WorkspacePath = worker.WorkspacePath,
                CurrentTask = worker.CurrentTask,
                CommandPreview = worker.EffectiveCommandPreview,
                LastSummary = worker.LastSummary,
                LastReportedStatus = worker.LastReportedStatus,
                LastNextStep = worker.LastNextStep,
                LastOutputPreview = worker.LastOutputPreview,
                NeedsAttention = worker.NeedsAttention,
                UnreadCount = worker.UnreadCount,
                AttentionLevel = worker.AttentionLevel,
                LastAttentionMessage = worker.LastAttentionMessage,
                AssistantAssignedRoundNumber = worker.AssistantAssignedRoundNumber,
                AssistantAssignedRoundTitle = worker.AssistantAssignedRoundTitle,
                AssistantRoundObjective = worker.AssistantRoundObjective,
                AssistantCanWriteCode = worker.AssistantCanWriteCode,
                AssistantOutputKind = worker.AssistantOutputKind,
                AssistantRoleMode = worker.AssistantRoleMode,
                LastActivityAt = worker.LastSurfaceActivityAt ?? worker.UpdatedAt,
                StartedAt = worker.StartedAt,
                UpdatedAt = worker.UpdatedAt,
                ExitCode = worker.ExitCode
            }));

        var latestVerification = run.VerificationHistory.OrderByDescending(item => item.CompletedAt).FirstOrDefault() ?? run.LastVerification;
        if (settings.EnableVerificationSurface || latestVerification is not null)
        {
            var verificationEvents = run.Attention
                .Where(item => !item.IsResolved && string.Equals(item.SurfaceId, VerificationSurfaceId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAt)
                .ToList();
            surfaces.Add(new RepoOPS.Agents.Models.ExecutionSurface
            {
                SurfaceId = VerificationSurfaceId,
                RunId = run.RunId,
                LaneId = VerificationLaneId,
                Type = "verification",
                Status = latestVerification?.Status ?? "idle",
                DisplayName = "Verification",
                VerificationId = latestVerification?.VerificationId,
                WorkspacePath = run.WorkspaceRoot,
                CommandPreview = latestVerification?.Command,
                LastSummary = latestVerification?.Summary,
                LastReportedStatus = latestVerification is null ? null : (latestVerification.Passed ? "passed" : "failed"),
                LastOutputPreview = latestVerification?.OutputPreview,
                NeedsAttention = verificationEvents.Count > 0,
                UnreadCount = verificationEvents.Count(item => !item.IsRead),
                AttentionLevel = verificationEvents.OrderByDescending(item => AttentionSeverity(item.Level)).Select(item => item.Level).FirstOrDefault(),
                LastAttentionMessage = verificationEvents.FirstOrDefault()?.Message,
                LastActivityAt = latestVerification?.CompletedAt,
                StartedAt = latestVerification?.StartedAt,
                UpdatedAt = latestVerification?.CompletedAt,
                ExitCode = latestVerification?.ExitCode
            });
        }

        return surfaces.OrderBy(item => item.LaneId).ThenBy(item => item.DisplayName).ToList();
    }

    private static int AttentionSeverity(string? level)
        => (level ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "error" or "critical" => 3,
            "warning" => 2,
            "info" => 1,
            _ => 0
        };
}

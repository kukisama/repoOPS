namespace RepoOPS.Agents.Services;

/// <summary>
/// Manages reusable prompt templates for V2 orchestration.
/// Templates use {{placeholder}} syntax and can be customized at runtime.
/// </summary>
public sealed class V2PromptTemplateEngine
{
    private readonly Dictionary<string, string> _templates = new(StringComparer.OrdinalIgnoreCase);
  private static readonly string TemplateRoot = Path.Combine(AppContext.BaseDirectory, "Agents", "Templates", "V2");

    public V2PromptTemplateEngine()
    {
    RegisterDefaults();
    TryLoadExternalTemplates();
    }

    public void Register(string name, string template) => _templates[name] = template;

    public string Render(string name, Dictionary<string, string> variables)
    {
        if (!_templates.TryGetValue(name, out var template))
            throw new InvalidOperationException($"Prompt template '{name}' not found.");

        var result = template;
        foreach (var (key, value) in variables)
        {
            result = result.Replace($"{{{{{key}}}}}", value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    public IReadOnlyDictionary<string, string> GetAllTemplates() => _templates;

    private void TryLoadExternalTemplates()
    {
      var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["main-plan-roles"] = "main-plan-roles.md",
        ["worker-dispatch"] = "worker-dispatch.md",
        ["main-review-round"] = "main-review-round.md",
        ["forced-review"] = "forced-review.md",
        ["main-final-verdict"] = "main-final-verdict.md"
      };

      foreach (var kvp in map)
      {
        var fullPath = Path.Combine(TemplateRoot, kvp.Value);
        if (!File.Exists(fullPath))
        {
          continue;
        }

        var text = File.ReadAllText(fullPath);
        if (!string.IsNullOrWhiteSpace(text))
        {
          Register(kvp.Key, text);
        }
      }
    }

        private void RegisterDefaults()
    {
        // --- 主线程: 规划角色分配 ---
        Register("main-plan-roles", """
你是 RepoOPS V2 自驱动调度器的主线程.

## 当前任务
- 总目标: {{goal}}
- 项目根目录: {{workspaceRoot}}
- 可用角色列表:
{{roleList}}

## 轮次递进上下文(上一轮产物)
{{roundContext}}

## 上一阶段关联记录(仅在相关时参考)
{{previousStageContext}}

## 你的职责
1. 分析目标, 决定本轮当前唯一需要激活的角色.
2. 为该角色生成一段清晰的任务描述(这段 task 会被写入该角色的任务单 md, 并作为子线程 prompt).
3. 输出必须是纯 JSON(不要 Markdown 代码块包裹).
4. 子线程将优先读取任务单 md 执行, 所以 task 必须写成可直接执行的指令(包含目标、步骤、产出、验收).
5. 一轮不必只是“最小目标”, 而应尽量形成一个对强 agent 来说可以连续推进 20-60 分钟、并能交付明显成果的“阶段包”.
6. 主线程后续每次追问都应优先依赖: 子线程已提交的文件、首轮计划、轮次记录. 不要随意发起全仓完全 review.
7. 不要因为担心 builder 可能写不完, 就机械把任务拆得过细; 是否需要缩小阶段, 应优先交给 review 根据真实产出动态调整.

## 调度规则(必须遵守)

### 单执行槽原则
- 除命名阶段外, 任意时刻只能有 1 个 active actor.
- 每轮只派发 1 个角色, 绝不允许同时派发 2 个或以上角色.

### 角色职责边界
- planner 只能产出计划文件, 不能改业务代码.
- reviewer 只能产出 review 文件, 不能改业务代码或计划文件.
- builder 只能改业务代码/业务配置 + 自己的交付说明.
- 其他角色如需表达意见, 只能写 comment / suggestion / feedback.

### 首轮默认
- 首轮(round 1)默认只安排 1 个角色: 优先 planner.
- 仅当你明确判断"可直接实施"时, 改为 builder 单角色执行.

### 阶段推进原则
- planner 应尽量把目标整理为: 初级目标 / 中级目标 / 最终目标.
- 当前轮应优先给出“阶段包”而不是碎片化小任务: 要让强 agent 能在单轮内形成较完整、可评审、可交付的成果.
- 只有在风险很高、依赖明显不清晰、修改范围可能失控时, 才主动把阶段拆小; 否则默认允许更有野心的推进.
- 当前阶段完成后, 才建议主线程结束本轮并进入下一轮.
- 如果 planner 或 reviewer 提出切换轮次建议, 你必须检查其理由是否足够强壮再采纳.
- 若本轮实际推进超出预期, review 可以建议下一轮继续维持较大阶段包; 若本轮证明范围偏大, review 再建议收缩.

## 额外执行约束(必须遵守)
- 这是"调度输出"任务, 不是代码实现任务.
- 允许先做简短会话计划(session plan), 但请以内存/Todo 方式维护, 不要通过 shell 写计划文件.
- 除非确有必要, 不要运行命令; 尤其不要为了"写计划"执行 shell.
- 若进行计划步骤, 请在 3-5 行内完成, 然后直接输出最终 JSON.

## 输出格式
{
  "summary": "本轮调度摘要",
  "assignments": [
    {
      "roleId": "角色ID",
      "task": "给该角色的具体任务描述, 尽量详细"
    }
  ]
}
""");

        // --- 主线程: 派发给子线程的任务包装模板 ---
        Register("worker-dispatch", """
你是 {{roleName}}({{roleDescription}}).

## 项目总目标
{{goal}}

## 本轮任务
{{task}}

## 计划文档(优先读取)
- 轮次索引: {{planIndexPath}}
- 你的任务单: {{taskMarkdownPath}}
- 当前轮次目录: {{currentRoundDirectory}}
- 建议先读取任务单 md, 再开始执行.

## 上一阶段关联记录(仅在相关时参考)
{{previousStageContext}}

## 执行顺序(必须)
1. 先读取"你的任务单"md.
2. 若任务单与上方"本轮任务"有差异, 以任务单 md 为准.
3. 查看你的角色写权限规则与专属工件路径.
4. 按任务单完成产出, 并在结尾给出下一轮建议(写入 NEXT).

## 工作约束
- 工作目录: {{workspaceRoot}}
- 所有产出必须在工作目录内.
- 同一时间只有你一个活动角色, 不存在并行同轮改写者.

## 你的角色写权限(严格执行)
{{roleWritePolicy}}

## 你的专属主文件
- 你本轮必须维护这个文件: {{roleOwnedOutputPath}}
- planner / reviewer / 其他建议角色的主结论必须落到这个文件.
- builder 必须维护自己的交付说明文件, 同时可以修改业务代码.
- 除 builder 外, 其他角色一律不得修改业务代码.

## 轮次与阶段意识
- 默认假设你是强执行 agent: 如果当前任务在一段连续工作时间内可以完成较完整的阶段交付, 请直接大胆推进, 不要因为担心“可能写不完”而机械只做很小一块.
- 这一轮的目标不是把整个项目一次做完, 而是尽量交付一个有明显价值、可评审、可继续衔接的阶段成果包.
- 如果执行中发现范围比预期大, 你应优先完成主路径、主交付和最关键部分; 剩余尾项可以在总结中交给 review / 主线程动态收缩下一轮.
- 如果你认为当前阶段已经完成, 可以建议主线程进入下一轮, 但必须写出明确理由和证据.
- 如果你认为本轮其实还能继续做更完整, 也可以在 NEXT 中明确说明“下一轮可继续保持较大阶段包推进”.
- 如果你只是有意见但不构成切轮依据, 请把它写成 comment / suggestion / feedback, 不要把弱建议包装成强结论.

## 结构化汇报(必须放在回答最后)

```
STATUS: completed | blocked | need-help
SUMMARY: 一句话总结你做了什么
NEXT: 建议下一步做什么(如果有的话)
FILES: 本轮新增或修改的文件列表(逗号分隔)
```

## 额外提醒
- 主线程下一次追问更依赖你的已提交文件、首轮计划和轮次记录, 所以请把核心结论写进你的专属主文件, 不要只留在终端输出里.
""");

        // --- 主线程: 审查一轮结果 ---
        Register("main-review-round", """
你是 RepoOPS V2 调度器主线程. 刚刚完成了第 {{roundNumber}} 轮执行.

## 总目标
{{goal}}

## 本轮各角色汇报
{{workerReports}}

## 历史决策
{{decisionHistory}}

## 轮次工件
- 当前轮次目录: {{roundArtifactDirectory}}
- 当前轮次计划索引: {{currentPlanIndexPath}}
- 首轮计划参考:
{{initialPlanContext}}

## 上一阶段关联记录(仅在相关时参考)
{{previousStageContext}}

## 你的任务
1. 优先基于子线程已提交文件、首轮计划、轮次记录来判断本轮是否完成了“阶段目标/阶段包交付”.
2. 不要默认重新全量审查整个仓库; 只有在工件互相冲突、证据不足或角色报告 blocked 时, 才扩大审查范围.
3. 判断项目整体进度, 并决定当前阶段是: 继续补齐 / 进入下一轮 / 完成.
4. 如果 planner 或 reviewer 给出了进入下一轮的建议, 你必须审查理由是否足够强壮再采纳.
5. 如果需要下一轮, 仍然只能分配 1 个角色.
6. 你不是“默认保守”的裁判; 如果本轮 builder 已交付了较完整且高价值的成果, 应允许下一轮继续用较大阶段包推进, 而不是机械切成过碎的小步.
7. 只有当你发现范围过大、质量失稳、依赖未收敛、尾项明显失控时, 才建议下一轮缩小范围; 否则应鼓励延续高密度推进.

## 调度规则提醒(下一轮分配时必须遵守)
- 除命名阶段外, 任意时刻只能有 1 个 active actor.
- 每轮只派 1 个角色.
- planner 只能推进计划文件; reviewer 只能推进 review 文件; builder 只能推进业务代码 + 自己的交付说明; 其他角色只能写意见文件.

输出纯 JSON:
{
  "overallStatus": "continue | needs-review | completed",
  "summary": "本轮总结",
  "issues": ["发现的问题列表"],
  "nextRoundAssignments": [
    {
      "roleId": "角色ID",
      "workerId": "如果要复用已有 worker 的 ID 则填, 新建则留空",
      "task": "下一轮任务描述"
    }
  ]
}
""");

        // --- 强制独立审查轮 ---
        Register("forced-review", """
你是一个独立的审查员(Reviewer). 之前所有参与角色都声称任务已完成, 但调度器认为需要第三方验证.

## 项目总目标
{{goal}}

## 各角色最终汇报
{{workerReports}}

## 你的职责
1. 彻底检查工作目录 {{workspaceRoot}} 中的文件.
2. 验证每个角色声称的产出是否真实存在且内容正确.
3. 重点检查: 业务交付文件是否在 `{{workspaceRoot}}/` 根或业务子目录下. 如果交付物仅存在于 `.repoops/` 或 `.github/` 内部, 视为未交付, 必须判定 failed.
4. 检查每个角色是否已写入变更日志(`<角色ID>-变更日志.md`, 应在工作目录根下).
5. 运行必要的构建/测试命令来实际验证.
6. 如果一切正常, 输出通过; 如果有问题, 详细列出.

完成后输出:
```
STATUS: passed | failed
SUMMARY: 审查结果摘要
ISSUES: 具体问题列表(如果有)
```
""");

        // --- 主线程: 最终裁决 ---
        Register("main-final-verdict", """
你是 RepoOPS V2 调度器主线程. 独立审查轮已完成.

## 总目标
{{goal}}

## 审查员报告
{{reviewReport}}

## 历史决策
{{decisionHistory}}

## 你的任务
根据审查员的报告做最终裁决:
- 如果通过: 宣布项目完成.
- 如果未通过: 决定如何修复.
- 修复阶段仍然只能分配 1 个角色, 不允许并行返工.

输出纯 JSON:
{
  "verdict": "completed | fix-needed",
  "summary": "最终结论",
  "fixAssignments": [
    {
      "roleId": "需要返工的角色ID",
      "workerId": "复用的 workerID",
      "task": "修复任务描述"
    }
  ]
}
""");
    }
}
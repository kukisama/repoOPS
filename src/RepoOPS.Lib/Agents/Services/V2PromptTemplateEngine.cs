namespace RepoOPS.Agents.Services;

/// <summary>
/// Manages reusable prompt templates for V2 orchestration.
/// Templates use {{placeholder}} syntax and can be customized at runtime.
/// </summary>
public sealed class V2PromptTemplateEngine
{
    private readonly Dictionary<string, string> _templates = new(StringComparer.OrdinalIgnoreCase);

    public V2PromptTemplateEngine()
    {
        RegisterDefaults();
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

    private void RegisterDefaults()
    {
        // ─── 主线程：规划角色分配 ───
        Register("main-plan-roles", """
你是 RepoOPS V2 自驱动调度器的主线程。

## 当前任务
总目标：{{goal}}
项目根目录：{{workspaceRoot}}
可用角色列表：
{{roleList}}

## 你的职责
1. 分析目标，决定本轮需要哪些角色参与
2. 为每个选定角色生成一段清晰的任务描述（会作为 prompt 传给对应的 coding agent）
3. 输出必须是纯 JSON（不要 Markdown 代码块包裹）

## 输出格式
{
  "summary": "本轮调度摘要",
  "assignments": [
    {
      "roleId": "角色ID",
      "task": "给该角色的具体任务描述，尽量详细"
    }
  ]
}
""");

        // ─── 主线程：派发给子线程的任务包装模板 ───
        Register("worker-dispatch", """
你是 {{roleName}}（{{roleDescription}}）。

## 项目总目标
{{goal}}

## 本轮任务
{{task}}

## 工作约束
- 工作目录：{{workspaceRoot}}
- 所有产出必须在工作目录内
- 完成后请输出结构化汇报，格式如下（放在回答的最后）：

```
STATUS: completed | blocked | need-help
SUMMARY: 一句话总结你做了什么
NEXT: 建议下一步做什么（如果有的话）
```

## 同伴信息
本轮还有以下角色在并行工作：{{peerRoles}}
请注意避免冲突、做好协调。
""");

        // ─── 主线程：审查一轮结果 ───
        Register("main-review-round", """
你是 RepoOPS V2 调度器主线程。刚刚完成了第 {{roundNumber}} 轮执行。

## 总目标
{{goal}}

## 本轮各角色汇报
{{workerReports}}

## 历史决策
{{decisionHistory}}

## 你的任务
1. 审查每个角色的产出是否合格
2. 判断项目整体进度
3. 决定是否需要下一轮（如需要，给出新的角色分配）
4. 如果所有角色都报告完成，你必须保持警惕——有人可能遗漏或误报

输出纯 JSON：
{
  "overallStatus": "continue | needs-review | completed",
  "summary": "本轮总结",
  "issues": ["发现的问题列表"],
  "nextRoundAssignments": [
    {
      "roleId": "角色ID",
      "workerId": "如果要复用已有worker的ID则填，新建则留空",
      "task": "下一轮任务描述"
    }
  ]
}
""");

        // ─── 强制 Review 轮 ───
        Register("forced-review", """
你是一个独立的代码审查员（Reviewer）。之前所有参与角色都声称任务已完成——但调度器认为需要第三方验证。

## 项目总目标
{{goal}}

## 各角色最终汇报
{{workerReports}}

## 你的职责
1. 彻底检查工作目录 {{workspaceRoot}} 中的代码/文件
2. 验证每个角色声称的产出是否真实存在且功能正确
3. 运行必要的构建/测试命令来实际验证
4. 如果一切正常，输出通过；如果有问题，详细列出

完成后输出：
```
STATUS: passed | failed
SUMMARY: 审查结果摘要
ISSUES: 具体问题列表（如果有）
```
""");

        // ─── 主线程：最终裁决 ───
        Register("main-final-verdict", """
你是 RepoOPS V2 调度器主线程。Review 轮次已完成。

## 总目标
{{goal}}

## Reviewer 报告
{{reviewReport}}

## 历史决策
{{decisionHistory}}

## 任务
根据 Reviewer 的报告，做最终裁决：
- 如果通过：宣布项目完成
- 如果未通过：决定如何修复

输出纯 JSON：
{
  "verdict": "completed | fix-needed",
  "summary": "最终结论",
  "fixAssignments": [
    {
      "roleId": "需要返工的角色ID",
      "workerId": "复用的workerID",
      "task": "修复任务描述"
    }
  ]
}
""");
    }
}

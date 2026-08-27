using C99.Models;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace C99.Services
{
    public class ToolExecutionPlan
    {
        public bool Execute { get; set; }
        public string Script { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string Raw { get; set; } = "";
    }

    public static class ToolDescriptionAnalyzer
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static async Task<ToolExecutionPlan> AnalyzeAsync(
            AIToolItem tool,
            string requestContext,
            Func<string, string?, Task<string>> callAI)
        {
            string prompt = BuildPrompt(tool, requestContext);

            string systemPrompt =
                "你是一个工具调度分析器。你的任务是根据工具的描述和当前上下文，判断是否应该执行此工具。\n" +
                "如果可以执行，请返回 JSON 格式的执行计划，包含以下字段：\n" +
                "  - execute: true\n" +
                "  - script: 要执行的脚本文件名（不带路径）\n" +
                "  - arguments: 传递给脚本的参数\n" +
                "如果不需要执行此工具，返回：\n" +
                "  {\"execute\": false}\n" +
                "只输出 JSON，不要任何其他文字。";

            string jsonResponse = "";
            try
            {
                jsonResponse = await callAI(prompt, systemPrompt);
            }
            catch
            {
                return new ToolExecutionPlan { Execute = false, Raw = "(AI 调用失败)" };
            }

            var plan = TryParsePlan(jsonResponse);
            if (plan == null)
            {
                // 部分本地模型首次输出格式不可用，重试一次并强调仅输出 JSON
                try
                {
                    jsonResponse = await callAI(prompt,
                        systemPrompt + "\n再次强调：只输出一个 JSON 对象，不要包含任何说明文字、注释或代码块标记。");
                }
                catch { }
                plan = TryParsePlan(jsonResponse ?? "");
            }

            if (plan == null)
                return new ToolExecutionPlan { Execute = false, Raw = jsonResponse ?? "" };

            plan.Raw = jsonResponse ?? "";
            return plan;
        }

        /// <summary>从模型输出中提取并解析 JSON 计划（容忍代码块、前后说明文字）。解析失败返回 null。</summary>
        private static ToolExecutionPlan? TryParsePlan(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            string t = text.Replace("```json", "").Replace("```", "").Trim();
            int start = t.IndexOf('{');
            int end = t.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            t = t.Substring(start, end - start + 1);

            try
            {
                return JsonSerializer.Deserialize<ToolExecutionPlan>(t, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static string BuildPrompt(AIToolItem tool, string requestContext)
        {
            return
                $"## 可用工具\n" +
                $"名称: {tool.Name}\n" +
                $"描述: {tool.Description}\n" +
                $"目录: {tool.DirectoryPath}\n" +
                $"\n## 当前上下文\n{requestContext}\n" +
                $"\n请根据上下文判断是否要执行此工具。如果需要，指定要运行的脚本和参数。";
        }
    }
}

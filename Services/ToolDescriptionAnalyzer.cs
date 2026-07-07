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

            string jsonResponse;
            try
            {
                jsonResponse = await callAI(prompt, systemPrompt);
            }
            catch
            {
                return new ToolExecutionPlan { Execute = false };
            }

            jsonResponse = jsonResponse.Trim();
            if (jsonResponse.StartsWith("```"))
            {
                int start = jsonResponse.IndexOf('\n') + 1;
                int end = jsonResponse.LastIndexOf("```");
                if (start > 0 && end > start)
                    jsonResponse = jsonResponse[start..end].Trim();
            }

            try
            {
                var plan = JsonSerializer.Deserialize<ToolExecutionPlan>(jsonResponse, JsonOptions);
                return plan ?? new ToolExecutionPlan { Execute = false };
            }
            catch
            {
                return new ToolExecutionPlan { Execute = false };
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

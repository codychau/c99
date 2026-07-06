using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace C99.Models
{
    /// <summary>
    /// AI梦工厂配置
    /// </summary>
    public class DreamFactoryConfig
    {
        /// <summary>HTTP 服务端口</summary>
        public int Port { get; set; } = 9527;

        /// <summary>是否自动启动 HTTP 服务</summary>
        public bool AutoStart { get; set; } = true;

        /// <summary>AI 模型来源：BuiltIn / Custom</summary>
        public string ModelSource { get; set; } = "BuiltIn";

        /// <summary>内置预设模型名</summary>
        public string BuiltInModel { get; set; } = "Local llama.cpp";

        /// <summary>内置预设选中的模型文件（.gguf 路径或模型名）</summary>
        public string BuiltInModelFile { get; set; } = "";

        /// <summary>自定义 API 地址 (OpenAI 兼容)</summary>
        public string CustomApiUrl { get; set; } = "http://localhost:8080/v1/chat/completions";

        /// <summary>自定义 API Key</summary>
        public string CustomApiKey { get; set; } = "";

        /// <summary>自定义模型名称</summary>
        public string CustomModelName { get; set; } = "";

        /// <summary>System Prompt</summary>
        public string SystemPrompt { get; set; } =
            "你是一个专业的工作报告助手。请根据以下邮件信息，用中文生成一份简洁的工作报告摘要。"
            + "格式：1) 重点关注事项（来自重要联系人的邮件）；2) 其他值得关注的信息；3) 今日工作建议。"
            + "请控制在500字以内。";

        /// <summary>逻辑管道配置（key=工作流名称）</summary>
        public Dictionary<string, LogicPipelineConfig> LogicPipelines { get; set; } = new();

        /// <summary>当前使用的工作流名称</summary>
        public string CurrentWorkflow { get; set; } = "mail_report";

        /// <summary>获取实际使用的 API URL</summary>
        public string GetEffectiveApiUrl()
        {
            if (ModelSource == "BuiltIn")
            {
                return BuiltInModel switch
                {
                    "Local llama.cpp" => "http://localhost:8080/v1/chat/completions",
                    "Local ollama" => "http://localhost:11434/v1/chat/completions",
                    "Local vllm" => "http://localhost:8000/v1/chat/completions",
                    _ => "http://localhost:8080/v1/chat/completions",
                };
            }
            return CustomApiUrl;
        }

        /// <summary>获取实际使用的模型名称</summary>
        public string GetEffectiveModelName()
        {
            if (ModelSource == "BuiltIn")
            {
                if (!string.IsNullOrEmpty(BuiltInModelFile))
                    return BuiltInModelFile;
                return BuiltInModel switch
                {
                    "Local llama.cpp" => "local-model",
                    "Local ollama" => "local-model",
                    "Local vllm" => "local-model",
                    _ => "local-model",
                };
            }
            return CustomModelName;
        }

        /// <summary>获取实际使用的 API Key</summary>
        public string GetEffectiveApiKey()
        {
            if (ModelSource == "BuiltIn") return ""; // 本地模型不需要 key
            return CustomApiKey;
        }
    }

    /// <summary>
    /// 邮件报告请求（来自油猴脚本）
    /// </summary>
    public class MailReportRequest
    {
        [JsonPropertyName("important")]
        public MailItem[] Important { get; set; } = Array.Empty<MailItem>();

        [JsonPropertyName("others")]
        public string Others { get; set; } = "";

        [JsonPropertyName("account")]
        public string Account { get; set; } = "";
    }

    public class MailItem
    {
        [JsonPropertyName("from")]
        public string From { get; set; } = "";

        [JsonPropertyName("subject")]
        public string Subject { get; set; } = "";

        [JsonPropertyName("preview")]
        public string Preview { get; set; } = "";

        [JsonPropertyName("time")]
        public string Time { get; set; } = "";
    }

    /// <summary>
    /// AI 响应
    /// </summary>
    public class AIReportResponse
    {
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = "";
    }

    /// <summary>
    /// OpenAI 兼容 API 请求
    /// </summary>
    public class OpenAIChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("messages")]
        public OpenAIMessage[] Messages { get; set; } = Array.Empty<OpenAIMessage>();

        [JsonPropertyName("temperature")]
        public float Temperature { get; set; } = 0.7f;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 1024;
    }

    public class OpenAIMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }

    public class OpenAIChatResponse
    {
        [JsonPropertyName("choices")]
        public OpenAIChoice[] Choices { get; set; } = Array.Empty<OpenAIChoice>();
    }

    public class OpenAIChoice
    {
        [JsonPropertyName("message")]
        public OpenAIMessage Message { get; set; } = new();
    }
}

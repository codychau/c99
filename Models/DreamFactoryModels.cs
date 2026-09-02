using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace C99.Models
{
    public class AIToolItem
    {
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "🛠️";
        public string Description { get; set; } = "";
        public string DirectoryPath { get; set; } = "";
        public string Category { get; set; } = "";
    }

    /// <summary>AI梦工厂工作流模式：主流程 / 知识库检索流程</summary>
    public enum DreamWorkflowMode
    {
        /// <summary>主流程（默认，原有邮件报告工作流）</summary>
        Main = 0,

        /// <summary>知识库检索流程</summary>
        KnowledgeBase = 1,
    }

    /// <summary>
    /// AI梦工厂配置
    /// </summary>
    public class DreamFactoryConfig
    {
        /// <summary>HTTP 服务端口</summary>
        public int Port { get; set; } = 9527;

        /// <summary>当前工作流模式（UI 按钮切换，HTTP 请求按接口路径自动识别）</summary>
        public DreamWorkflowMode CurrentWorkflowMode { get; set; } = DreamWorkflowMode.Main;

        /// <summary>是否自动启动 HTTP 服务</summary>
        public bool AutoStart { get; set; } = true;

        /// <summary>AI 模型来源：BuiltIn / Custom</summary>
        public string ModelSource { get; set; } = "BuiltIn";

        /// <summary>内置预设模型名</summary>
        public string BuiltInModel { get; set; } = "Local llama.cpp";

        /// <summary>内置预设选中的模型文件（.gguf 路径或模型名，已废弃：模型文件自动按 AI 启动底座所选目录判断）</summary>
        public string BuiltInModelFile { get; set; } = "";

        /// <summary>内置预设：由 AI 启动底座推导出的实际 API 地址（留空回退到内置映射）</summary>
        public string BuiltInApiUrl { get; set; } = "";

        /// <summary>内置预设：由 AI 启动底座推导出的实际模型名称（留空回退到内置映射）</summary>
        public string BuiltInModelName { get; set; } = "";

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

        /// <summary>知识库检索流程 System Prompt</summary>
        public string SystemPromptKb { get; set; } =
            "你是一个知识库检索助手。请根据提供的知识库资料，用中文准确、简洁地回答用户问题。"
            + "如果资料中找不到相关信息，请如实说明没有找到，不要编造内容。";

        /// <summary>逻辑管道配置（key=工作流名称）</summary>
        public Dictionary<string, LogicPipelineConfig> LogicPipelines { get; set; } = new();

        /// <summary>当前使用的工作流名称（主流程）</summary>
        public string CurrentWorkflow { get; set; } = "mail_report";

        /// <summary>当前使用的工作流名称（知识库检索流程）</summary>
        public string CurrentWorkflowKb { get; set; } = "kb_report";

        /// <summary>获取当前模式对应的 System Prompt</summary>
        public string GetEffectiveSystemPrompt() =>
            CurrentWorkflowMode == DreamWorkflowMode.KnowledgeBase ? SystemPromptKb : SystemPrompt;

        /// <summary>获取指定模式对应的 System Prompt</summary>
        public string GetSystemPrompt(DreamWorkflowMode mode) =>
            mode == DreamWorkflowMode.KnowledgeBase ? SystemPromptKb : SystemPrompt;

        /// <summary>获取指定模式对应的工作流名称</summary>
        public string GetWorkflowName(DreamWorkflowMode mode) =>
            mode == DreamWorkflowMode.KnowledgeBase ? CurrentWorkflowKb : CurrentWorkflow;

        /// <summary>获取实际使用的 API URL</summary>
        public string GetEffectiveApiUrl()
        {
            if (ModelSource == "BuiltIn")
            {
                // 优先使用由 AI 启动底座推导出的地址（端口等与底座配置保持一致）
                if (!string.IsNullOrEmpty(BuiltInApiUrl))
                    return BuiltInApiUrl;
                return BuiltInModel switch
                {
                    "llama.cpp" or "Local llama.cpp" => "http://127.0.0.1:8080/v1/chat/completions",
                    "ollama" or "Local ollama" => "http://localhost:11434/v1/chat/completions",
                    "vllm" or "Local vllm" => "http://localhost:8000/v1/chat/completions",
                    "lmstudio" or "Local lmstudio" => "http://127.0.0.1:1234/v1/chat/completions",
                    _ => "http://127.0.0.1:8080/v1/chat/completions",
                };
            }
            return CustomApiUrl;
        }

        /// <summary>获取实际使用的模型名称</summary>
        public string GetEffectiveModelName()
        {
            if (ModelSource == "BuiltIn")
            {
                // 优先使用由 AI 启动底座推导出的模型名（如 ollama 需发送真实模型名）
                if (!string.IsNullOrEmpty(BuiltInModelName))
                    return BuiltInModelName;
                return "local-model";
            }
            return CustomModelName;
        }

        /// <summary>获取实际使用的 API Key</summary>
        public string GetEffectiveApiKey()
        {
            if (ModelSource == "BuiltIn") return ""; // 本地模型不需要 key
            return CustomApiKey;
        }

        /// <summary>base64 解码编码格式（auto / utf-8 / gb2312 / gbk / big5）</summary>
        public string Base64Encoding { get; set; } = "auto";

        /// <summary>AI 生成最大 Token 数</summary>
        public int MaxTokens { get; set; } = 8192;

        /// <summary>外部模型输入价格（¥/百万tokens）</summary>
        public double ApiInputPricePerMillion { get; set; } = 0;

        /// <summary>外部模型输出价格（¥/百万tokens）</summary>
        public double ApiOutputPricePerMillion { get; set; } = 0;

        /// <summary>本地模型价格（¥/百万tokens）</summary>
        public double LocalPricePerMillion { get; set; } = 2;

        /// <summary>AI 杂货铺工具列表</summary>
        public List<AIToolItem> AITools { get; set; } = new()
        {
            new() { Name = "图像生成", Icon = "🎨" },
            new() { Name = "文本创作", Icon = "✍️" },
            new() { Name = "音乐创作", Icon = "🎵" },
            new() { Name = "智能对话", Icon = "💬" },
            new() { Name = "数据分析", Icon = "📊" },
            new() { Name = "视频生成", Icon = "🎬" },
            new() { Name = "代码助手", Icon = "📝" },
            new() { Name = "图片处理", Icon = "🖼️" },
            new() { Name = "知识库", Icon = "📚", Category = "知识库" },
        };
    }

    /// <summary>
    /// 知识库检索请求（POST /api/kb/query）
    /// </summary>
    public class KbQueryRequest
    {
        [JsonPropertyName("question")]
        public string Question { get; set; } = "";

        [JsonPropertyName("top_k")]
        public int TopK { get; set; } = 8;

        [JsonPropertyName("collection")]
        public string? Collection { get; set; }
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

        [JsonPropertyName("emails")]
        public string Emails { get; set; } = "";

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

        [JsonPropertyName("usage")]
        public OpenAIUsage? Usage { get; set; }
    }

    public class OpenAIUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }

    public class OpenAIChoice
    {
        [JsonPropertyName("message")]
        public OpenAIMessage Message { get; set; } = new();
    }
}

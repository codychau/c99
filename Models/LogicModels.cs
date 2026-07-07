using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace C99.Models
{
    public class LogicAction
    {
        public string ActionType { get; set; } = "log";
        public Dictionary<string, string> Params { get; set; } = new();
    }

    public class LogicPipeline
    {
        public bool Enabled { get; set; }
        public List<LogicAction> Actions { get; set; } = new();
    }

    public class LogicPipelineConfig
    {
        public LogicPipeline? PreAILogic { get; set; }
        public LogicPipeline? PostAILogic { get; set; }
        public PostActionConfig PostAction { get; set; } = new();
    }

    public class PostActionConfig
    {
        public string ActionType { get; set; } = "none";
        public string OutputDir { get; set; } = "";
        public string SiyuanApiUrl { get; set; } = "http://localhost:6806";
        public string SiyuanApiKey { get; set; } = "";
        public string SiyuanNotebookId { get; set; } = "";
    }

    public class LogicEngine
    {
        public Func<string, string, int, Task>? ShowPopupNotifyAsync { get; set; }
        public Func<string, string, Task<bool>>? ShowPopupConfirmAsync { get; set; }
        public Func<string, Task>? LogAsync { get; set; }
        public Func<string, Task<string>>? ExtractKeywordsAsync { get; set; }
        public Action<int>? OnStepExecuted { get; set; }
        public Func<string, Task<AIToolItem?>>? GetToolByNameAsync { get; set; }
        public Func<AIToolItem, string, Task<string>>? AnalyzeToolAsync { get; set; }
        public Func<string, string, string, Task<string>>? ExecuteScriptAsync { get; set; }

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        public async Task<Dictionary<string, string>> ExecuteAsync(LogicPipeline? pipeline, Dictionary<string, string> context)
        {
            if (pipeline == null || !pipeline.Enabled || pipeline.Actions.Count == 0)
                return context;

            foreach (var action in pipeline.Actions)
            {
                bool shouldContinue = await ExecuteActionAsync(action, context);
                OnStepExecuted?.Invoke(1);
                if (!shouldContinue) break;
            }

            return context;
        }

        private async Task<bool> ExecuteActionAsync(LogicAction action, Dictionary<string, string> context)
        {
            var p = action.Params;
            string type = action.ActionType;

            switch (type)
            {
                case "replace_text":
                {
                    string target = GetParam(p, "target");
                    string find = Resolve(GetParam(p, "find"), context);
                    string replace = Resolve(GetParam(p, "replace"), context);
                    if (context.TryGetValue(target, out var val))
                        context[target] = val.Replace(find, replace);
                    break;
                }

                case "regex_replace":
                {
                    string target = GetParam(p, "target");
                    string pattern = Resolve(GetParam(p, "pattern"), context);
                    string replacement = Resolve(GetParam(p, "replacement"), context);
                    if (context.TryGetValue(target, out var val))
                    {
                        try { context[target] = Regex.Replace(val, pattern, replacement); }
                        catch (Exception ex) { await Log($"正则替换失败: {ex.Message}"); }
                    }
                    break;
                }

                case "http_request":
                {
                    string url = Resolve(GetParam(p, "url"), context);
                    string method = GetParam(p, "method", "POST");
                    string body = Resolve(GetParam(p, "body"), context);
                    string resultVar = GetParam(p, "result_var");
                    try
                    {
                        var req = new HttpRequestMessage(method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Get : HttpMethod.Post, url);
                        if (!string.IsNullOrEmpty(body) && !method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        var resp = await _http.SendAsync(req);
                        string respBody = await resp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrEmpty(resultVar))
                            SetContext(context, resultVar, respBody);
                        await Log($"HTTP {method} {url} -> {resp.StatusCode}");
                    }
                    catch (Exception ex) { await Log($"HTTP请求失败: {ex.Message}"); }
                    break;
                }

                case "search_files":
                {
                    string folderPath = Resolve(GetParam(p, "folder_path"), context);
                    string recursive = GetParam(p, "recursive", "否");
                    bool isRecursive = recursive == "是" || recursive.Equals("true", StringComparison.OrdinalIgnoreCase);
                    string keywordSource = Resolve(GetParam(p, "keywords"), context);
                    if (string.IsNullOrWhiteSpace(keywordSource) && context.TryGetValue("ai_response", out var aiResp))
                        keywordSource = aiResp;

                    bool filterRequested = !string.IsNullOrWhiteSpace(keywordSource);
                    string keywordText = keywordSource;

                    string smartAnalysis = GetParam(p, "smart_analysis", "否");
                    bool useAIAnalysis = smartAnalysis == "是";
                    if (filterRequested && useAIAnalysis && ExtractKeywordsAsync != null)
                    {
                        await Log("正在通过 AI 提取检索关键词...");
                        try
                        {
                            string extracted = (await ExtractKeywordsAsync(keywordSource) ?? "").Trim();
                            if (extracted == "无" || extracted.Equals("none", StringComparison.OrdinalIgnoreCase))
                                extracted = "";
                            keywordText = extracted;
                            await Log($"AI 提取关键词: {(string.IsNullOrEmpty(keywordText) ? "(无可用关键词)" : keywordText)}");
                        }
                        catch (Exception ex)
                        {
                            await Log($"关键词提取失败，改用原文匹配: {ex.Message}");
                        }
                    }

                    await SearchTextFilesAsync(folderPath, isRecursive, keywordText, filterRequested, context);
                    break;
                }

                case "log":
                {
                    string msg = Resolve(GetParam(p, "message"), context);
                    await Log(msg);
                    break;
                }

                case "popup_notify":
                {
                    string title = Resolve(GetParam(p, "title"), context);
                    string msg = Resolve(GetParam(p, "message"), context);
                    int autoDismiss = int.TryParse(GetParam(p, "auto_dismiss"), out var d) ? d : 10;
                    if (ShowPopupNotifyAsync != null)
                        await ShowPopupNotifyAsync(title, msg, autoDismiss);
                    break;
                }

                case "popup_confirm":
                {
                    string title = Resolve(GetParam(p, "title"), context);
                    string msg = Resolve(GetParam(p, "message"), context);
                    if (ShowPopupConfirmAsync != null)
                    {
                        bool confirmed = await ShowPopupConfirmAsync(title, msg);
                        if (!confirmed) return false;
                    }
                    break;
                }

                case "call_tool":
                {
                    string toolName = GetParam(p, "tool_name");
                    string requestContext = Resolve(GetParam(p, "context", "{ai_response}"), context);

                    if (string.IsNullOrEmpty(toolName))
                    {
                        await Log("调用工具失败: 未指定工具名称");
                        break;
                    }

                    AIToolItem? tool = null;
                    if (GetToolByNameAsync != null)
                        tool = await GetToolByNameAsync(toolName);

                    if (tool == null)
                    {
                        await Log($"调用工具失败: 未找到工具 \"{toolName}\"");
                        break;
                    }

                    await Log($"正在分析工具 [{tool.Name}] 的描述...");

                    if (AnalyzeToolAsync != null)
                    {
                        string marker = await AnalyzeToolAsync(tool, requestContext);

                        try
                        {
                            var plan = JsonSerializer.Deserialize<ToolMarker>(marker);
                            if (plan != null && plan.Execute && !string.IsNullOrEmpty(plan.Script))
                            {
                                await Log($"AI 决定执行: {plan.Script} {plan.Arguments}");
                                if (ExecuteScriptAsync != null)
                                {
                                    string result = await ExecuteScriptAsync(plan.Script, plan.Arguments, tool.DirectoryPath);
                                    context["tool_result"] = result;
                                    string preview = result.Length > 200 ? result[..200] + "..." : result;
                                    await Log($"工具执行结果: {preview}");
                                }
                            }
                            else
                            {
                                await Log("AI 决定不执行此工具");
                            }
                        }
                        catch (Exception ex)
                        {
                            await Log($"解析工具执行计划失败: {ex.Message}");
                        }
                    }
                    break;
                }
            }

            return true;
        }

        private static string GetParam(Dictionary<string, string> p, string key, string defaultVal = "") =>
            p.TryGetValue(key, out var v) ? v : defaultVal;

        private static void SetContext(Dictionary<string, string> ctx, string key, string value)
        {
            if (!string.IsNullOrEmpty(key)) ctx[key] = value;
        }

        public static string Resolve(string template, Dictionary<string, string> context)
        {
            if (string.IsNullOrEmpty(template) || !template.Contains('{')) return template;
            return Regex.Replace(template, @"\{(\w+)\}", m =>
                context.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        }

        private async Task Log(string msg)
        {
            if (LogAsync != null) await LogAsync(msg);
            System.Diagnostics.Debug.WriteLine($"[LogicEngine] {msg}");
        }

        private static readonly HashSet<string> TextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".html", ".htm",
            ".css", ".js", ".ts", ".py", ".cs", ".java", ".yaml", ".yml", ".toml",
            ".ini", ".cfg", ".conf", ".log", ".sql", ".sh", ".bat", ".ps1",
            ".r", ".rb", ".php", ".swift", ".kt", ".lua", ".pl", ".rs", ".go",
            ".c", ".cpp", ".h", ".hpp"
        };

        private static readonly int MaxFiles = 50;
        private static readonly long MaxTotalSize = 500 * 1024;
        private static readonly long MaxFileSize = 100 * 1024;

        private async Task SearchTextFilesAsync(string folderPath, bool recursive, string keywords, bool filterRequested, Dictionary<string, string> context)
        {
            var foundPaths = new List<string>();
            var contentsBuilder = new StringBuilder();
            long totalSize = 0;

            try
            {
                string mode = recursive ? "递归搜索" : "搜索(非递归)";
                await Log($"开始搜索资料库: {folderPath}（{mode}）");

                var keywordList = ParseKeywords(keywords);
                bool doFilter = filterRequested || keywordList.Count > 0;
                if (doFilter)
                    await Log(keywordList.Count > 0
                        ? $"关键词过滤: {string.Join(" / ", keywordList)}"
                        : "关键词过滤: (无有效关键词，不匹配任何文件)");
                else
                    await Log("未提供有效关键词，返回全部文本文件");

                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var allFiles = Directory.GetFiles(folderPath, "*.*", searchOption);
                var textFiles = allFiles.Where(f => TextFileExtensions.Contains(Path.GetExtension(f))).ToList();
                await Log($"扫描到 {allFiles.Length} 个文件，其中 {textFiles.Count} 个文本文件");

                var scored = new List<ScoredFile>();
                foreach (var filePath in textFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        string shortName = Path.GetFileName(filePath);
                        bool tooLarge = fileInfo.Length > MaxFileSize;
                        string? content = null;
                        if (!tooLarge)
                        {
                            try { content = await Task.Run(() => File.ReadAllText(filePath, Encoding.UTF8)); }
                            catch { content = null; }
                        }

                        int score;
                        if (doFilter)
                        {
                            score = CountHits(shortName, keywordList) * FileNameWeight;
                            if (content != null) score += CountHits(content, keywordList);
                            if (score <= 0) continue;
                        }
                        else
                        {
                            score = 0;
                        }

                        scored.Add(new ScoredFile
                        {
                            Path = filePath,
                            ShortName = shortName,
                            Length = fileInfo.Length,
                            TooLarge = tooLarge,
                            Content = content,
                            Score = score
                        });
                    }
                    catch (Exception ex)
                    {
                        await Log($"读取文件失败: {Path.GetFileName(filePath)} - {ex.Message}");
                    }
                }

                IEnumerable<ScoredFile> ordered = doFilter
                    ? scored.OrderByDescending(s => s.Score).ThenBy(s => s.ShortName)
                    : scored;

                if (doFilter)
                    await Log($"按关键词匹配到 {scored.Count} 个相关文件");

                int taken = 0;
                foreach (var sf in ordered)
                {
                    if (taken >= MaxFiles)
                    {
                        await Log($"已达文件数量上限（{MaxFiles}），停止读取剩余文件");
                        break;
                    }

                    if (sf.TooLarge)
                    {
                        await Log($"跳过过大文件: {sf.ShortName}（{sf.Length / 1024}KB）");
                        foundPaths.Add(sf.Path);
                        contentsBuilder.AppendLine($"--- 文件: {sf.Path} ---");
                        contentsBuilder.AppendLine($"(文件过大，已跳过，大小: {sf.Length / 1024}KB)");
                        taken++;
                        continue;
                    }

                    if (totalSize + sf.Length > MaxTotalSize)
                    {
                        await Log($"搜索文件总大小已达上限，停止读取剩余文件");
                        break;
                    }

                    string content = sf.Content ?? "(读取失败)";
                    await Log($"读取文件: {sf.ShortName}（{sf.Length / 1024}KB）{(doFilter ? $"[相关度 {sf.Score}]" : "")}");
                    totalSize += sf.Length;
                    foundPaths.Add(sf.Path);
                    contentsBuilder.AppendLine($"--- 文件: {sf.Path} ---");
                    contentsBuilder.AppendLine(content);
                    contentsBuilder.AppendLine();
                    taken++;
                }

                context["search_file_paths"] = string.Join(Environment.NewLine, foundPaths);
                context["search_file_contents"] = contentsBuilder.ToString();
                context["search_keywords"] = string.Join(",", keywordList);
                await Log($"搜索完成: 已读取 {foundPaths.Count} 个文件，总大小 {totalSize / 1024}KB");
            }
            catch (Exception ex)
            {
                await Log($"搜索资料库失败: {ex.Message}");
                context["search_file_paths"] = "";
                context["search_file_contents"] = "";
            }
        }

        private const int FileNameWeight = 5;

        private sealed class ScoredFile
        {
            public string Path = "";
            public string ShortName = "";
            public long Length;
            public bool TooLarge;
            public string? Content;
            public int Score;
        }

        private static List<string> ParseKeywords(string keywords)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(keywords)) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Split into word segments: runs of alphanumeric/CJK characters.
            // No n-gram expansion — keywords are already clean (AI-extracted or user-typed).
            var buf = new StringBuilder();
            foreach (char c in keywords)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || (c >= 0x4E00 && c <= 0x9FFF))
                    buf.Append(c);
                else
                    FlushBuffer(buf, seen, result);
            }
            FlushBuffer(buf, seen, result);

            return result;
        }

        private static void FlushBuffer(StringBuilder buf, HashSet<string> seen, List<string> result)
        {
            if (buf.Length >= 2)
            {
                var w = buf.ToString();
                if (!w.Contains('{') && !w.Contains('}') && seen.Add(w))
                    result.Add(w);
            }
            buf.Clear();
        }

        private static int CountHits(string text, List<string> keywords)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int total = 0;
            foreach (var kw in keywords)
            {
                int idx = 0;
                while ((idx = text.IndexOf(kw, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    total++;
                    idx += kw.Length;
                }
            }
            return total;
        }

        private sealed class ToolMarker
        {
            public bool Execute { get; set; }
            public string Script { get; set; } = "";
            public string Arguments { get; set; } = "";
        }
    }
}

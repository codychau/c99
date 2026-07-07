using C99.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace C99.Services
{
    /// <summary>
    /// AI梦工厂服务：HTTP API + AI 调用 + 通知
    /// </summary>
    public class AIDreamFactoryService : IDisposable
    {
        private class ReportHistoryItem
        {
            public DateTime Time { get; set; }
            public string Account { get; set; } = "";
            public string Summary { get; set; } = "";
        }

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly DreamFactoryConfig _config;
        private readonly HttpClient _httpClient;
        private bool _isRunning;
        private readonly List<ReportHistoryItem> _reportHistory = new();
        private static readonly TimeSpan HistoryMaxAge = TimeSpan.FromDays(1);
        private readonly string _base64Encoding;

        /// <summary>指标服务（由外部注入）</summary>
        public MetricsService? Metrics { get; set; }

        /// <summary>收到新报告时触发</summary>
        public event Action<string, string>? OnReportGenerated;

        /// <summary>网页报告就绪时触发 (url, account)</summary>
        public event Action<string, string>? OnWebReportReady;

        public event Action<string>? OnLog;
        public event Func<string, string, int, Task>? OnPopupNotifyAsync;
        public event Func<string, string, Task<bool>>? OnPopupConfirmAsync;

        public bool IsRunning => _isRunning;

        public AIDreamFactoryService(DreamFactoryConfig config)
        {
            _config = config;
            _base64Encoding = config.Base64Encoding;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _cts = new CancellationTokenSource();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_config.Port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_config.Port}/");
                _listener.Start();
                _isRunning = true;

                Log($"HTTP 服务已启动: http://localhost:{_config.Port}/");

                // 后台处理请求
                _ = Task.Run(() => ListenLoop(_cts.Token));
            }
            catch (Exception ex)
            {
                Log($"启动 HTTP 服务失败: {ex.Message}");
                Log($"提示：可能需要以管理员身份运行，或端口 {_config.Port} 已被占用");
                _isRunning = false;
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            Log("HTTP 服务已停止");
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var context = await _listener.GetContextAsync().WaitAsync(ct);
                    _ = Task.Run(() => HandleRequestAsync(context), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex)
                {
                    Log($"请求处理异常: {ex.Message}");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            // CORS
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            try
            {
                string path = request.Url?.AbsolutePath ?? "/";

                switch (path)
                {
                    case "/api/report":
                        await HandleReportAsync(request, response);
                        break;
                    case "/api/health":
                        await WriteJsonAsync(response, new { status = "ok", model = _config.GetEffectiveModelName() });
                        break;
                    case "/api/config":
                        if (request.HttpMethod == "GET")
                            await WriteJsonAsync(response, _config);
                        else if (request.HttpMethod == "POST")
                            await HandleConfigUpdateAsync(request, response);
                        else
                            await WriteJsonAsync(response, new { error = "Method not allowed" }, 405);
                        break;
                    case "/api/file":
                        await HandleFileViewAsync(request, response);
                        break;
                    case "/report/latest":
                        await HandleReportPageAsync(request, response);
                        break;
                    default:
                        response.StatusCode = 404;
                        await WriteJsonAsync(response, new { error = "Not found" });
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"API 错误: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonAsync(response, new { error = ex.Message });
            }
        }

        private async Task HandleReportAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync();
            }

            Log($"收到邮件报告 ({body.Length} 字节)");

            MailReportRequest? report;
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            try
            {
                report = JsonSerializer.Deserialize<MailReportRequest>(body, jsonOptions);
            }
            catch (Exception ex)
            {
                Log($"JSON 解析失败: {ex.Message}");
                response.StatusCode = 400;
                await WriteJsonAsync(response, new { error = "Invalid JSON" });
                return;
            }

            bool hasImportant = report != null && report.Important != null && report.Important.Length > 0;
            bool hasOthers = !string.IsNullOrEmpty(report?.Others);
            bool hasEmails = !string.IsNullOrEmpty(report?.Emails);

            if (report == null || (!hasImportant && !hasOthers && !hasEmails))
            {
                Log("邮件报告数据为空，跳过处理");
                await WriteJsonAsync(response, new { summary = "没有新的邮件数据" });
                return;
            }

            LogicPipeline? preLogic = null;
            LogicPipeline? postLogic = null;
            if (_config.LogicPipelines.TryGetValue(_config.CurrentWorkflow, out var pipelineConfig))
            {
                preLogic = pipelineConfig.PreAILogic;
                postLogic = pipelineConfig.PostAILogic;
            }

            string prompt = BuildPrompt(report);

            var context = new Dictionary<string, string>
            {
                ["request_body"] = body,
                ["user_prompt"] = prompt,
                ["account"] = report.Account ?? ""
            };

            var engine = CreateLogicEngine();
            if (preLogic != null && preLogic.Enabled && preLogic.Actions.Count > 0)
            {
                try
                {
                    Log($"[前置逻辑] 开始执行 ({preLogic.Actions.Count} 个动作)");
                    await engine.ExecuteAsync(preLogic, context);
                    Log($"[前置逻辑] 执行完毕");
                }
                catch (Exception ex)
                {
                    Log($"[前置逻辑] 执行异常: {ex.Message}");
                }
            }

            string finalPrompt = context.TryGetValue("user_prompt", out var modPrompt) ? modPrompt : prompt;

            if (context.TryGetValue("search_file_contents", out var fileContents) && !string.IsNullOrEmpty(fileContents))
            {
                finalPrompt += "\n\n--- 搜索到的文件内容 ---\n" + fileContents;
                Log($"[前置逻辑] 已将搜索到的文件内容附加到 AI 提示中");
            }

            Log("正在调用 AI 生成工作报告...");
            string summary;
            try
            {
                summary = await CallAIAsync(finalPrompt);
                Log($"AI 报告已生成 ({summary.Length} 字符)");
            }
            catch (Exception ex)
            {
                Log($"AI 调用失败: {ex.Message}");
                var fallbackLines = new StringBuilder();

                if (report.Important != null && report.Important.Length > 0)
                {
                    var impText = string.Join("\n", report.Important.Select(m =>
                        $"{(string.IsNullOrEmpty(m.Time) ? "" : $"({m.Time}) ")}[{m.From}] {m.Subject}\n  {m.Preview}"));
                    AppendFallbackSection(fallbackLines, "重要联系人邮件", impText);
                }
                AppendFallbackSection(fallbackLines, "其他邮件摘要", report.Others);
                AppendFallbackSection(fallbackLines, "邮件列表", report.Emails);

                var result = fallbackLines.ToString();
                if (result.Length > 3000)
                    result = result[..3000] + "\n...(已截断)";
                summary = $"AI 调用失败: {ex.Message}\n\n以下是原始邮件摘要:\n\n" + result;
            }

            context["ai_response"] = summary;

            if (postLogic != null && postLogic.Enabled && postLogic.Actions.Count > 0)
            {
                try
                {
                    Log($"[后置逻辑] 开始执行 ({postLogic.Actions.Count} 个动作)");
                    await engine.ExecuteAsync(postLogic, context);
                    Log($"[后置逻辑] 执行完毕");
                }
                catch (Exception ex)
                {
                    Log($"[后置逻辑] 执行异常: {ex.Message}");
                }
            }

            string finalSummary = context.TryGetValue("ai_response", out var modSummary) ? modSummary : summary;

            if (context.TryGetValue("search_file_paths", out var filePaths) && !string.IsNullOrEmpty(filePaths))
            {
                string keywords = context.TryGetValue("search_keywords", out var kw) ? kw : "";
                if (!string.IsNullOrEmpty(keywords))
                    finalSummary += $"\n\n--- 搜索到的文件 ---\n关键词: {keywords}\n" + filePaths;
                else
                    finalSummary += "\n\n--- 搜索到的文件 ---\n" + filePaths;
                Log($"[后置逻辑] 已将搜索到的文件路径附加到报告中");
            }
            string account = report.Account ?? "";

            lock (_reportHistory)
            {
                _reportHistory.Insert(0, new ReportHistoryItem
                {
                    Time = DateTime.Now,
                    Account = account,
                    Summary = finalSummary
                });
                _reportHistory.RemoveAll(h => DateTime.Now - h.Time > HistoryMaxAge);
            }

            try { Metrics?.RecordReport(); } catch { }

            OnReportGenerated?.Invoke(finalSummary, account);

            // 执行输出动作
            var postAction = pipelineConfig?.PostAction;
            if (postAction != null && postAction.ActionType != "none")
            {
                try
                {
                    await ExecutePostActionAsync(postAction, finalSummary, account);
                }
                catch (Exception ex)
                {
                    Log($"[输出动作] 执行失败: {ex.Message}");
                }
            }

            await WriteJsonAsync(response, new AIReportResponse { Summary = finalSummary });
        }

        private LogicEngine CreateLogicEngine()
        {
            var engine = new LogicEngine();
            engine.LogAsync = msg => { Log(msg); return Task.CompletedTask; };
            engine.ShowPopupNotifyAsync = async (title, msg, sec) =>
            {
                if (OnPopupNotifyAsync != null)
                    await OnPopupNotifyAsync(title, msg, sec);
            };
            engine.ShowPopupConfirmAsync = async (title, msg) =>
            {
                if (OnPopupConfirmAsync != null)
                    return await OnPopupConfirmAsync(title, msg);
                return true;
            };
            engine.OnStepExecuted = count => { try { Metrics?.RecordPipelineSteps(count); } catch { } };
            engine.ExtractKeywordsAsync = async (reportText) =>
            {
                if (reportText.Length > 4000) reportText = reportText.Substring(0, 4000);
                string sys = "你是文件检索关键词提取助手，只输出关键词，不输出任何解释。";
                string prompt =
                    "从下面的工作报告中提取用于在本地文档库检索相关资料的核心关键词。\n" +
                    "要求：\n" +
                    "1) 只保留具体名词：人名、系统/项目名称、技术术语、产品名、专有缩写等；\n" +
                    "2) 忽略通用词，如\"工作报告/摘要/重点/关注/相关/操作/说明/邮件/提醒/信息/更新/情况/事项\"等；\n" +
                    "3) 最多输出 8 个关键词，用英文逗号分隔，不要编号、不要解释、不要多余文字；\n" +
                    "4) 若报告中没有可用于检索的具体关键词，只输出：无\n\n" +
                    "报告内容：\n" + reportText;
                return await CallAIAsync(prompt, sys);
            };
            engine.GetToolByNameAsync = async (name) =>
            {
                await Task.CompletedTask;
                return _config.AITools.FirstOrDefault(t => t.Name == name);
            };
            engine.AnalyzeToolAsync = async (tool, requestContext) =>
            {
                var plan = await ToolDescriptionAnalyzer.AnalyzeAsync(tool, requestContext, CallAIAsync);
                return System.Text.Json.JsonSerializer.Serialize(plan);
            };
            engine.ExecuteScriptAsync = (scriptName, args, workingDir) =>
                ExecuteScriptInternalAsync(scriptName, args, workingDir);
            return engine;
        }

        public async Task<string> DebugToolAsync(AIToolItem tool, string requestContext)
        {
            var plan = await ToolDescriptionAnalyzer.AnalyzeAsync(tool, requestContext, CallAIAsync);
            if (!plan.Execute || string.IsNullOrEmpty(plan.Script))
                return "AI 决定不执行此工具";

            return await ExecuteScriptInternalAsync(plan.Script, plan.Arguments, tool.DirectoryPath);
        }

        private async Task<string> ExecuteScriptInternalAsync(string scriptName, string args, string workingDir)
        {
            string scriptPath = System.IO.Path.Combine(workingDir, scriptName);
            if (!System.IO.File.Exists(scriptPath))
                return $"(脚本不存在: {scriptPath})";

            string ext = System.IO.Path.GetExtension(scriptPath).ToLowerInvariant();
            ProcessStartInfo psi;

            if (ext == ".ps1")
            {
                psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {args}")
                {
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else if (ext == ".bat" || ext == ".cmd")
            {
                psi = new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\" {args}")
                {
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                psi = new ProcessStartInfo(scriptPath, args)
                {
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return "(进程启动失败)";
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                return string.IsNullOrEmpty(error) ? output : $"{output}\nSTDERR:\n{error}";
            }
            catch (Exception ex)
            {
                return $"(执行失败: {ex.Message})";
            }
        }

        private async Task HandleConfigUpdateAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                body = await reader.ReadToEndAsync();

            var update = JsonSerializer.Deserialize<DreamFactoryConfig>(body);
            if (update != null)
            {
                _config.Port = update.Port;
                _config.ModelSource = update.ModelSource;
                _config.BuiltInModel = update.BuiltInModel;
                _config.CustomApiUrl = update.CustomApiUrl;
                _config.CustomApiKey = update.CustomApiKey;
                _config.CustomModelName = update.CustomModelName;
                _config.SystemPrompt = update.SystemPrompt;
            }
            await WriteJsonAsync(response, new { status = "ok" });
        }

        private string BuildPrompt(MailReportRequest report)
        {
            if (!string.IsNullOrEmpty(report.Emails)
                && (report.Important == null || report.Important.Length == 0)
                && string.IsNullOrEmpty(report.Others))
            {
                string emails = DecodeBase64Content(report.Emails);
                if (emails.Length > 32000)
                    emails = emails[..32000] + "\n...(已截断)";
                return "## 邮件列表\n\n" + emails;
            }

            var sb = new StringBuilder();
            sb.AppendLine("## 重要联系人邮件");
            if (report.Important != null)
            {
                foreach (var m in report.Important)
                    sb.AppendLine($"- [{m.From}] {m.Subject} ({m.Time})\n  {m.Preview}");
            }

            if (!string.IsNullOrEmpty(report.Others))
            {
                sb.AppendLine();
                sb.AppendLine("## 其他邮件摘要");
                string others = report.Others;
                if (others.Length > 24000)
                    others = others[..24000] + "\n...(已截断)";
                sb.AppendLine(others);
            }

            return sb.ToString();
        }

        private string DecodeBase64Content(string rawText)
        {
            var sb = new StringBuilder();
            var lines = rawText.Split('\n');
            var b64Buffer = new List<string>();
            string? lastNonB64 = null;

            void Flush()
            {
                if (b64Buffer.Count == 0) return;
                var joined = string.Join("", b64Buffer.Select(l => l.Trim()));
                b64Buffer.Clear();
                var decoded = TryDecodeBase64(joined, lastNonB64);
                sb.AppendLine(decoded ?? joined);
                lastNonB64 = null;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0) { Flush(); continue; }
                if (IsBase64Line(trimmed))
                    b64Buffer.Add(trimmed);
                else
                {
                    Flush();
                    sb.AppendLine(TryFixGarbledText(lines[i]) ?? lines[i]);
                    lastNonB64 = trimmed;
                }
                if (i == lines.Length - 1) Flush();
            }

            return sb.ToString();
        }

        private async Task<string> CallAIAsync(string prompt, string? systemPromptOverride = null)
        {
            string apiUrl = _config.GetEffectiveApiUrl();
            string model = _config.GetEffectiveModelName();
            string apiKey = _config.GetEffectiveApiKey();
            string systemPrompt = systemPromptOverride ?? _config.SystemPrompt;

            var request = new OpenAIChatRequest
            {
                Model = model,
                Temperature = 0.7f,
                MaxTokens = _config.MaxTokens,
                Messages = new[]
                {
                    new OpenAIMessage { Role = "system", Content = systemPrompt },
                    new OpenAIMessage { Role = "user", Content = prompt }
                }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = content
            };

            if (!string.IsNullOrEmpty(apiKey))
                httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var httpResponse = await _httpClient.SendAsync(httpRequest);
            sw.Stop();
            httpResponse.EnsureSuccessStatusCode();

            var responseBody = await httpResponse.Content.ReadAsStringAsync();
            var chatResponse = JsonSerializer.Deserialize<OpenAIChatResponse>(responseBody);

            var responseText = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();

            // record metrics
            try
            {
                int pt = chatResponse?.Usage?.PromptTokens ?? 0;
                int ct = chatResponse?.Usage?.CompletionTokens ?? 0;
                bool isLocal = _config.ModelSource == "BuiltIn";
                double cost = 0;
                if (!isLocal)
                {
                    double inputPrice = _config.ApiInputPricePerMillion / 1_000_000.0;
                    double outputPrice = _config.ApiOutputPricePerMillion / 1_000_000.0;
                    cost = pt * inputPrice + ct * outputPrice;
                }
                Metrics?.RecordAICall(pt, ct, sw.Elapsed.TotalMilliseconds, cost, isLocal);
            }
            catch { }

            return string.IsNullOrEmpty(responseText) ? "(AI 返回空内容)" : responseText;
        }

        private void AppendFallbackSection(StringBuilder sb, string title, string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            sb.AppendLine($"## {title}");

            var lines = content.Split('\n');
            var b64Buffer = new List<string>();
            string? lastNonB64 = null;

            void FlushBase64()
            {
                if (b64Buffer.Count == 0) return;
                var joined = string.Join("", b64Buffer.Select(l => l.Trim()));
                b64Buffer.Clear();
                var decoded = TryDecodeBase64(joined, lastNonB64);
                lastNonB64 = null;
                if (decoded != null)
                {
                    if (decoded.Length > 500) decoded = decoded[..500] + "...";
                    sb.AppendLine(decoded);
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0)
                {
                    FlushBase64();
                    continue;
                }

                if (IsBase64Line(trimmed))
                {
                    b64Buffer.Add(trimmed);
                }
                else
                {
                    FlushBase64();
                    var fixedText = TryFixGarbledText(lines[i]);
                    if (fixedText != null)
                        sb.AppendLine(fixedText);
                    else
                    {
                        if (trimmed.Length > 200) trimmed = trimmed[..200] + "...";
                        sb.AppendLine(trimmed);
                    }
                    lastNonB64 = trimmed;
                }

                if (i == lines.Length - 1) FlushBase64();
            }

            sb.AppendLine();
        }

        private static bool IsBase64Line(string text)
        {
            if (text.Length < 40) return false;
            int valid = 0, total = 0;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                total++;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=')
                    valid++;
            }
            return total >= 30 && valid * 100 / total >= 90;
        }

        private string? TryDecodeBase64(string base64Text, string? hintLine = null)
        {
            try
            {
                var cleaned = base64Text.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
                if (cleaned.Length % 4 != 0)
                    cleaned = cleaned.PadRight(cleaned.Length + (4 - cleaned.Length % 4) % 4, '=');
                var bytes = Convert.FromBase64String(cleaned);

                var hintCharset = ExtractCharsetFromText(hintLine);
                if (!string.IsNullOrEmpty(hintCharset))
                {
                    var hintEnc = ResolveEncoding(hintCharset);
                    if (hintEnc != null)
                    {
                        try
                        {
                            var result = hintEnc.GetString(bytes);
                            if (ScoreContent(result) > 0.3) return result;
                        }
                        catch { }
                    }
                }

                // 如果用户指定了编码（非 auto），直接用
                if (!string.Equals(_base64Encoding, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var enc = ResolveEncoding(_base64Encoding);
                        if (enc != null)
                        {
                            var result = enc.GetString(bytes);
                            if (ScoreContent(result) > 0.3) return result;
                        }
                    }
                    catch { }
                }

                // 自动检测：charset 头 → UTF-8 → GBK
                var detectCharset = TryDetectCharset(bytes);
                var encodings = new List<System.Text.Encoding>();
                if (!string.IsNullOrEmpty(detectCharset))
                {
                    var detectedEnc = ResolveEncoding(detectCharset);
                    if (detectedEnc != null) encodings.Add(detectedEnc);
                }
                encodings.Add(System.Text.Encoding.UTF8);
                var gbkEnc = ResolveEncoding("gbk");
                if (gbkEnc != null) encodings.Add(gbkEnc);

                string? best = null;
                double bestScore = 0;
                foreach (var enc in encodings)
                {
                    try
                    {
                        var decoded = enc.GetString(bytes);
                        var score = ScoreContent(decoded);
                        if (score > bestScore) { bestScore = score; best = decoded; }
                    }
                    catch { }
                }

                if (best != null && bestScore > 0.3) return best;
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static double ScoreContent(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int readable = 0;
            foreach (char c in text)
            {
                if ((c >= 0x4E00 && c <= 0x9FFF)
                    || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9')
                    || char.IsWhiteSpace(c) || char.IsPunctuation(c))
                    readable++;
            }
            return (double)readable / text.Length;
        }

        private static string? TryDetectCharset(byte[] bytes)
        {
            try
            {
                var ascii = System.Text.Encoding.ASCII.GetString(bytes);
                var m = System.Text.RegularExpressions.Regex.Match(ascii,
                    @"charset\s*=\s*[""']?([\w-]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    if (ResolveEncoding(m.Groups[1].Value) != null) return m.Groups[1].Value;
                }
            }
            catch { }
            return null;
        }

        private static string? ExtractCharsetFromText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(text,
                @"charset\s*=\s*[""']?([\w-]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string? TryFixGarbledText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int garbled = 0, latin1 = 0;
            foreach (char c in text)
            {
                if (c == 0xFFFD) garbled++;
                else if (c >= 0x80 && c <= 0xFF) latin1++;
            }
            if (garbled + latin1 == 0) return null;

            var charsetName = ExtractCharsetFromText(text);
            if (charsetName == null) return null;

            try
            {
                var enc = ResolveEncoding(charsetName);
                if (enc == null) return null;
                var bytes = new byte[text.Length];
                for (int i = 0; i < text.Length; i++)
                    bytes[i] = text[i] <= 0xFF ? (byte)text[i] : (byte)0x3F;
                var decoded = enc.GetString(bytes);
                if (ScoreContent(decoded) > 0.5) return decoded;
            }
            catch { }
            return null;
        }

        private static System.Text.Encoding? ResolveEncoding(string name)
        {
            try { return System.Text.Encoding.GetEncoding(name); } catch { }
            if (name.Equals("gb2312", StringComparison.OrdinalIgnoreCase))
            {
                try { return System.Text.Encoding.GetEncoding(936); } catch { }
                try { return System.Text.Encoding.GetEncoding("gbk"); } catch { }
            }
            return null;
        }

        private async Task WriteJsonAsync(HttpListenerResponse response, object data, int statusCode = 200)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            var json = JsonSerializer.Serialize(data);
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
            System.Diagnostics.Debug.WriteLine($"[AI梦工厂] {msg}");
        }

        private async Task ExecutePostActionAsync(PostActionConfig action, string summary, string account)
        {
            Log($"[输出动作] 类型: {action.ActionType}");

            switch (action.ActionType)
            {
                case "web_report":
                {
                    string url = $"http://localhost:{_config.Port}/report/latest";
                    Log($"[输出动作] 报告已就绪: {url}");
                    OnWebReportReady?.Invoke(url, account);
                    break;
                }

                case "markdown":
                    await SaveMarkdownAsync(action, summary);
                    break;

                case "word":
                    await SaveWordAsync(action, summary);
                    break;

                case "excel":
                    await SaveExcelAsync(action, summary);
                    break;

                case "siyuan":
                    await UploadToSiyuanAsync(action, summary);
                    break;
            }
        }

        private async Task HandleReportPageAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            string? indexParam = request.QueryString["i"];
            int startIdx = int.TryParse(indexParam, out var i) && i >= 0 ? i : 0;

            List<ReportHistoryItem> history;
            lock (_reportHistory) { history = _reportHistory.ToList(); }

            if (history.Count == 0)
            {
                await WriteHtmlAsync(response, "<!DOCTYPE html><html><head><meta charset=utf-8></head><body style='font-family:sans-serif;padding:40px;color:#888'>暂无报告</body></html>");
                return;
            }

            if (startIdx >= history.Count) startIdx = 0;

            string rawSummary = history[startIdx].Summary;
            const string filesMarker = "--- 搜索到的文件 ---";
            const string kwMarker = "关键词:";
            string bodyPart = rawSummary;
            string filesHtml = "";
            string searchKeywords = "";
            int mi = rawSummary.IndexOf(filesMarker, StringComparison.Ordinal);
            if (mi >= 0)
            {
                bodyPart = rawSummary.Substring(0, mi);
                var lines = rawSummary.Substring(mi + filesMarker.Length)
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();
                if (lines.Count > 0)
                {
                    if (lines[0].StartsWith(kwMarker, StringComparison.Ordinal))
                    {
                        searchKeywords = lines[0].Substring(kwMarker.Length).Trim();
                        lines.RemoveAt(0);
                    }
                    if (lines.Count > 0)
                    {
                        var sbf = new StringBuilder();
                        sbf.Append("<div class=\"files\"><div class=\"files-h\">搜索到的文件 (");
                        sbf.Append(lines.Count);
                        sbf.Append(")</div><div class=\"file-list\">");
                        string kwEncoded = System.Net.WebUtility.UrlEncode(searchKeywords);
                        foreach (var pth in lines)
                        {
                            sbf.Append("<a class=\"file-link\" href=\"/api/file?path=");
                            sbf.Append(Uri.EscapeDataString(pth));
                            sbf.Append("&keyword=");
                            sbf.Append(kwEncoded);
                            sbf.Append("\" target=\"_blank\">");
                            sbf.Append(System.Net.WebUtility.HtmlEncode(pth));
                            sbf.Append("</a>");
                        }
                        sbf.Append("</div></div>");
                        filesHtml = sbf.ToString();
                    }
                }
            }

            string summaryHtml = Markdig.Markdown.ToHtml(bodyPart);

            var sbSidebar = new StringBuilder();
            for (int j = 0; j < history.Count; j++)
            {
                string selClass = j == startIdx ? " sel" : "";
                sbSidebar.Append("<a class='item");
                sbSidebar.Append(selClass);
                sbSidebar.Append("' href='?i=");
                sbSidebar.Append(j);
                sbSidebar.Append("'><div class='ti'>");
                sbSidebar.Append(System.Net.WebUtility.HtmlEncode(history[j].Time.ToString("HH:mm:ss")));
                sbSidebar.Append("</div><div class='ac'>账号: ");
                sbSidebar.Append(System.Net.WebUtility.HtmlEncode(history[j].Account));
                sbSidebar.Append("</div></a>");
            }

            string html = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>工作报告</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','PingFang SC','Microsoft YaHei',sans-serif;display:flex;height:100vh;color:#2c3e50;background:#f5f7fa}
.side{width:260px;min-width:260px;background:#1e293b;display:flex;flex-direction:column;box-shadow:2px 0 12px rgba(0,0,0,.08);z-index:1}
.side h2{font-size:14px;font-weight:600;padding:20px 20px 16px;color:#94a3b8;text-transform:uppercase;letter-spacing:1px;border-bottom:1px solid #334155}
.list{flex:1;overflow-y:auto;padding:8px 0}
.list::-webkit-scrollbar{width:4px}
.list::-webkit-scrollbar-track{background:transparent}
.list::-webkit-scrollbar-thumb{background:#475569;border-radius:2px}
.item{display:block;padding:14px 20px;border-left:3px solid transparent;text-decoration:none;margin:1px 8px;border-radius:0 6px 6px 0;transition:all .2s ease}
.item:hover{background:rgba(255,255,255,.06);border-left-color:#64748b}
.item.sel{background:rgba(59,130,246,.15);border-left-color:#3b82f6}
.item .ti{font-size:13px;color:#e2e8f0;font-weight:500;font-variant-numeric:tabular-nums}
.item .ac{font-size:11px;color:#64748b;margin-top:3px}
.main{flex:1;overflow-y:auto;display:flex;justify-content:center;padding:40px 48px}
.main::-webkit-scrollbar{width:6px}
.main::-webkit-scrollbar-track{background:transparent}
.main::-webkit-scrollbar-thumb{background:#cbd5e1;border-radius:3px}
.main-inner{max-width:780px;width:100%}
.main h1{font-size:28px;font-weight:700;color:#1e293b;padding-bottom:12px;margin-bottom:12px;border-bottom:3px solid #3b82f6;letter-spacing:-0.5px}
.main h2{font-size:19px;color:#2563eb;margin-top:28px;margin-bottom:10px;font-weight:600}
.main h3{font-size:16px;color:#475569;margin-top:20px;margin-bottom:8px;font-weight:600}
.main p{margin:10px 0;font-size:15px}
.main ul,.main ol{padding-left:28px;margin:10px 0}
.main li{margin:6px 0;font-size:15px}
.main li::marker{color:#3b82f6}
.main strong{color:#0f172a;font-weight:600}
.main em{color:#334155}
.main code{background:#f1f5f9;color:#e11d48;padding:2px 8px;border-radius:4px;font-family:'JetBrains Mono','Cascadia Code',Consolas,monospace;font-size:13px;border:1px solid #e2e8f0}
.main pre{background:#1e293b;color:#e2e8f0;border:none;border-radius:10px;padding:20px 24px;font-size:13px;line-height:1.7;overflow-x:auto;margin:16px 0;box-shadow:0 2px 8px rgba(0,0,0,.08)}
.main pre code{background:none;color:inherit;padding:0;border:none;font-size:inherit}
.main blockquote{border-left:4px solid #3b82f6;margin:16px 0;padding:12px 20px;color:#475569;background:#f8fafc;border-radius:0 8px 8px 0;font-size:14px;box-shadow:0 1px 3px rgba(0,0,0,.04)}
.main hr{border:none;border-top:2px solid #e2e8f0;margin:28px 0}
.main table{width:100%;border-collapse:collapse;margin:16px 0;font-size:14px;border-radius:8px;overflow:hidden;box-shadow:0 1px 4px rgba(0,0,0,.06)}
.main th{background:#f1f5f9;padding:10px 14px;text-align:left;font-weight:600;color:#475569;border-bottom:2px solid #e2e8f0}
.main td{padding:10px 14px;border-bottom:1px solid #f1f5f9}
.main tr:last-child td{border-bottom:none}
.main a{color:#2563eb;text-decoration:none;border-bottom:1px solid transparent;transition:border-color .15s}
.main a:hover{border-bottom-color:#2563eb}
.time{color:#94a3b8;font-size:13px;margin-bottom:24px;display:flex;align-items:center;gap:6px}
.time::before{content:'🕐';font-size:14px}
.empty{padding:40px 0;color:#94a3b8;text-align:center;font-size:14px}
.files{margin-top:28px;border-top:2px solid #e2e8f0;padding-top:16px}
.files-h{font-size:12px;color:#64748b;font-weight:600;margin-bottom:10px;text-transform:uppercase;letter-spacing:.5px}
.file-list{display:flex;flex-direction:column;gap:4px}
.file-link{display:block;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:8px 12px;font-size:12px;line-height:1.5;color:#2563eb;word-break:break-all;text-decoration:none;transition:all .15s}
.file-link:hover{background:#eff6ff;border-color:#93c5fd}
</style>
</head>
<body>
<div class=""side"">
<h2>历史报告</h2>
<div class=""list"">" + sbSidebar.ToString() + @"</div>
</div>
<div class=""main"">
<div class=""main-inner"">
<h1>工作报告</h1>
<div class=""time"">账号: " + System.Net.WebUtility.HtmlEncode(history[startIdx].Account) + @" | " + history[startIdx].Time.ToString("yyyy-MM-dd HH:mm:ss") + @"</div>
<div>" + summaryHtml + filesHtml + @"</div>
</div>
</div>
</body>
</html>";

            byte[] buffer = Encoding.UTF8.GetBytes(html);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }

        private async Task HandleFileViewAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            string? filePath = request.QueryString["path"];
            string? keywords = request.QueryString["keyword"] ?? "";

            if (string.IsNullOrEmpty(filePath))
            {
                await WriteHtmlAsync(response, "<!DOCTYPE html><html><head><meta charset=utf-8></head><body style='font-family:sans-serif;padding:40px;color:#888'>缺少文件路径参数</body></html>");
                return;
            }

            string fullPath;
            try { fullPath = Path.GetFullPath(filePath); }
            catch
            {
                await WriteHtmlAsync(response, "<!DOCTYPE html><html><head><meta charset=utf-8></head><body style='font-family:sans-serif;padding:40px;color:#888'>无效的文件路径</body></html>");
                return;
            }

            if (!File.Exists(fullPath))
            {
                await WriteHtmlAsync(response, "<!DOCTYPE html><html><head><meta charset=utf-8></head><body style='font-family:sans-serif;padding:40px;color:#888'>文件不存在或已被删除</body></html>");
                return;
            }

            var fileInfo = new FileInfo(fullPath);
            const long maxViewSize = 1024 * 1024;
            string fileContent;
            if (fileInfo.Length > maxViewSize)
            {
                fileContent = $"(文件过大，无法预览: {fileInfo.Length / 1024}KB)";
            }
            else
            {
                try { fileContent = await Task.Run(() => File.ReadAllText(fullPath, Encoding.UTF8)); }
                catch { fileContent = "(无法读取文件内容，可能为二进制文件)"; }
            }

            string escapedContent = System.Net.WebUtility.HtmlEncode(fileContent);

            if (!string.IsNullOrEmpty(keywords))
            {
                var kwList = keywords.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Trim())
                    .Where(k => k.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(k => k.Length)
                    .ToList();
                foreach (var kw in kwList)
                {
                    string escapedKw = System.Net.WebUtility.HtmlEncode(kw);
                    escapedContent = Regex.Replace(escapedContent, Regex.Escape(escapedKw), "<mark>$&</mark>", RegexOptions.IgnoreCase);
                }
            }

            string fileName = Path.GetFileName(fullPath);
            string fileSize = fileInfo.Length >= 1024 * 1024
                ? $"{fileInfo.Length / (1024.0 * 1024.0):F1} MB"
                : $"{fileInfo.Length / 1024.0:F1} KB";
            string modTime = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");

            string encFileName = System.Net.WebUtility.HtmlEncode(fileName);
            string encFullPath = System.Net.WebUtility.HtmlEncode(fullPath);

            string head = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>" + encFileName + @" - 文件预览</title>
<style>
:root{--bg:#fff;--fg:#1e293b;--muted:#64748b;--border:#e2e8f0;--pre-bg:#f8fafc;--mark-bg:#fef08a;--mark-fg:#1e293b;--h-bg:#f8fafc;--btn-bg:#f1f5f9;--btn-hover:#e2e8f0;--path-fg:#475569}
.dark{--bg:#0f172a;--fg:#e2e8f0;--muted:#94a3b8;--border:#334155;--pre-bg:#1e293b;--mark-bg:#854d0e;--mark-fg:#fef08a;--h-bg:#1e293b;--btn-bg:#334155;--btn-hover:#475569;--path-fg:#94a3b8}
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','PingFang SC','Microsoft YaHei',monospace;background:var(--bg);color:var(--fg);padding:0;min-height:100vh}
.header{display:flex;align-items:center;justify-content:space-between;padding:16px 24px;background:var(--h-bg);border-bottom:1px solid var(--border);position:sticky;top:0;z-index:10;flex-wrap:wrap;gap:8px}
.header-left{display:flex;align-items:center;gap:12px;flex-wrap:wrap}
.file-name{font-size:16px;font-weight:600}
.file-path{font-size:12px;color:var(--path-fg);word-break:break-all}
.file-meta{font-size:12px;color:var(--muted);display:flex;gap:16px}
.file-meta span{display:inline-flex;align-items:center;gap:4px}
.toggle-btn{background:var(--btn-bg);border:1px solid var(--border);border-radius:6px;padding:6px 14px;font-size:12px;color:var(--fg);cursor:pointer;transition:all .15s;white-space:nowrap}
.toggle-btn:hover{background:var(--btn-hover)}
.content-wrap{padding:24px}
pre{background:var(--pre-bg);border:1px solid var(--border);border-radius:8px;padding:20px 24px;font-size:13px;line-height:1.7;overflow-x:auto;overflow-y:auto;max-height:calc(100vh - 180px);font-family:'JetBrains Mono','Cascadia Code',Consolas,monospace;tab-size:4}
pre mark{background:var(--mark-bg);color:var(--mark-fg);border-radius:3px;padding:0 2px}
.back-link{color:#2563eb;text-decoration:none;font-size:13px;display:inline-flex;align-items:center;gap:4px;margin-bottom:12px;display:inline-block;padding:4px 0}
.back-link:hover{text-decoration:underline}
</style>
</head>
<body>
<div class=""header"">
<div class=""header-left"">
<div><div class=""file-name"">" + encFileName + @"</div>
<div class=""file-path"">" + encFullPath + @"</div></div>
<div class=""file-meta""><span>" + fileSize + @"</span><span>" + modTime + @"</span></div>
</div>
<button class=""toggle-btn"" id=""themeBtn"" onclick=""toggleTheme()"">&#127769; 暗色</button>
</div>
<div class=""content-wrap"">
<a class=""back-link"" href=""/report/latest"">&larr; 返回报告</a>
<pre><code>" + escapedContent + @"</code></pre>
</div>
<script>
(function(){var t=localStorage.getItem('fv-theme');if(t==='dark'){document.body.classList.add('dark');document.getElementById('themeBtn').textContent='&#9728;&#65039; 浅色'}})();
function toggleTheme(){var b=document.body;b.classList.toggle('dark');var isDark=b.classList.contains('dark');document.getElementById('themeBtn').textContent=isDark?'&#9728;&#65039; 浅色':'&#127769; 暗色';localStorage.setItem('fv-theme',isDark?'dark':'light')}
</script>
</body>
</html>";

            await WriteHtmlAsync(response, head);
        }

        private async Task SaveMarkdownAsync(PostActionConfig action, string summary)
        {
            string dir = action.OutputDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Log("[输出动作] 输出目录无效，跳过 Markdown 保存");
                return;
            }
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, $"report_{timestamp}.md");
            await Task.Run(() => File.WriteAllText(path, summary, Encoding.UTF8));
            Log($"[输出动作] Markdown 已保存: {path}");
        }

        private async Task SaveWordAsync(PostActionConfig action, string summary)
        {
            string dir = action.OutputDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Log("[输出动作] 输出目录无效，跳过 Word 保存");
                return;
            }
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, $"report_{timestamp}.docx");

            await Task.Run(() =>
            {
                using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(path,
                    DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
                var mainPart = doc.AddMainDocumentPart();
                mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
                var body = new DocumentFormat.OpenXml.Wordprocessing.Body();

                // Title
                var title = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
                var titleRun = new DocumentFormat.OpenXml.Wordprocessing.Run();
                var titleText = new DocumentFormat.OpenXml.Wordprocessing.Text("工作报告");
                var titleProps = new DocumentFormat.OpenXml.Wordprocessing.RunProperties();
                titleProps.Append(new DocumentFormat.OpenXml.Wordprocessing.Bold());
                titleProps.Append(new DocumentFormat.OpenXml.Wordprocessing.FontSize { Val = "32" });
                titleRun.Append(titleProps);
                titleRun.Append(titleText);
                title.Append(titleRun);
                body.Append(title);

                // Content
                foreach (var line in summary.Split('\n'))
                {
                    var p = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
                    var r = new DocumentFormat.OpenXml.Wordprocessing.Run();
                    var t = new DocumentFormat.OpenXml.Wordprocessing.Text(line) { Space =
                        DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve };
                    r.Append(t);
                    p.Append(r);
                    body.Append(p);
                }

                mainPart.Document.Append(body);
                mainPart.Document.Save();
            });

            Log($"[输出动作] Word 已保存: {path}");
        }

        private async Task SaveExcelAsync(PostActionConfig action, string summary)
        {
            string dir = action.OutputDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Log("[输出动作] 输出目录无效，跳过 Excel 保存");
                return;
            }
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, $"report_{timestamp}.xlsx");

            await Task.Run(() =>
            {
                using var wb = new ClosedXML.Excel.XLWorkbook();
                var ws = wb.Worksheets.Add("工作报告");
                ws.Cell("A1").Value = "工作报告";
                ws.Cell("A1").Style.Font.Bold = true;
                ws.Cell("A1").Style.Font.FontSize = 16;

                var lines = summary.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    ws.Cell(i + 3, 1).Value = lines[i];

                ws.Column(1).Width = 80;
                ws.Column(1).Style.Alignment.WrapText = true;
                wb.SaveAs(path);
            });

            Log($"[输出动作] Excel 已保存: {path}");
        }

        private async Task UploadToSiyuanAsync(PostActionConfig action, string summary)
        {
            string apiUrl = action.SiyuanApiUrl.TrimEnd('/');
            string apiKey = action.SiyuanApiKey;
            string notebookId = action.SiyuanNotebookId;

            if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(notebookId))
            {
                Log("[输出动作] 思源笔记配置不完整，跳过上传");
                return;
            }

            string title = $"工作报告 {DateTime.Now:yyyy-MM-dd HH:mm}";
            var payload = new
            {
                notebook = notebookId,
                title,
                markdown = summary
            };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var req = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/notebook/createDocWithMd")
            {
                Content = content
            };
            req.Headers.Add("Authorization", $"Token {apiKey}");

            try
            {
                var resp = await _httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                    Log($"[输出动作] 已上传到思源笔记");
                else
                    Log($"[输出动作] 思源笔记上传失败: HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                Log($"[输出动作] 思源笔记上传异常: {ex.Message}");
            }
        }

        private async Task WriteHtmlAsync(HttpListenerResponse response, string html, int statusCode = 200)
        {
            response.StatusCode = statusCode;
            response.ContentType = "text/html; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }

        public void Dispose()
        {
            Stop();
            _httpClient.Dispose();
            _cts?.Dispose();
        }
    }
}

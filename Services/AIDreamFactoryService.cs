using C99.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
                var fallbackLines = new System.Text.StringBuilder();
                if (report.Important != null)
                {
                    foreach (var m in report.Important)
                        fallbackLines.AppendLine($"- [{m.From}] {m.Subject}");
                }
                if (!string.IsNullOrEmpty(report.Others))
                    fallbackLines.Append(report.Others.AsSpan(0, Math.Min(500, report.Others.Length)).ToString());
                summary = $"AI 调用失败: {ex.Message}\n\n以下是原始邮件摘要:\n\n" + fallbackLines;
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
            return engine;
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
                string emails = report.Emails;
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

        private async Task<string> CallAIAsync(string prompt)
        {
            string apiUrl = _config.GetEffectiveApiUrl();
            string model = _config.GetEffectiveModelName();
            string apiKey = _config.GetEffectiveApiKey();
            string systemPrompt = _config.SystemPrompt;

            var request = new OpenAIChatRequest
            {
                Model = model,
                Temperature = 0.7f,
                MaxTokens = 1024,
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

            var httpResponse = await _httpClient.SendAsync(httpRequest);
            httpResponse.EnsureSuccessStatusCode();

            var responseBody = await httpResponse.Content.ReadAsStringAsync();
            var chatResponse = JsonSerializer.Deserialize<OpenAIChatResponse>(responseBody);

            return chatResponse?.Choices?.FirstOrDefault()?.Message?.Content?.Trim()
                ?? "(AI 返回空内容)";
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

            string summaryHtml = Markdig.Markdown.ToHtml(history[startIdx].Summary);

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
<div>" + summaryHtml + @"</div>
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

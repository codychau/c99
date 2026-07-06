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
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly DreamFactoryConfig _config;
        private readonly HttpClient _httpClient;
        private bool _isRunning;
        private string _latestReport = "";
        private string _latestAccount = "";

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
            try
            {
                report = JsonSerializer.Deserialize<MailReportRequest>(body);
            }
            catch
            {
                response.StatusCode = 400;
                await WriteJsonAsync(response, new { error = "Invalid JSON" });
                return;
            }

            if (report == null || (report.Important.Length == 0 && string.IsNullOrEmpty(report.Others)))
            {
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
                Log($"[前置逻辑] 开始执行 ({preLogic.Actions.Count} 个动作)");
                await engine.ExecuteAsync(preLogic, context);
                Log($"[前置逻辑] 执行完毕");
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
                summary = $"AI 调用失败: {ex.Message}\n\n以下是原始邮件摘要:\n\n" +
                    string.Join("\n", report.Important.Select(m => $"- [{m.From}] {m.Subject}")) +
                    "\n\n" + report.Others.Substring(0, Math.Min(500, report.Others.Length));
            }

            context["ai_response"] = summary;

            if (postLogic != null && postLogic.Enabled && postLogic.Actions.Count > 0)
            {
                Log($"[后置逻辑] 开始执行 ({postLogic.Actions.Count} 个动作)");
                await engine.ExecuteAsync(postLogic, context);
                Log($"[后置逻辑] 执行完毕");
            }

            string finalSummary = context.TryGetValue("ai_response", out var modSummary) ? modSummary : summary;
            string account = report.Account ?? "";

            _latestReport = finalSummary;
            _latestAccount = account;

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
            var sb = new StringBuilder();
            sb.AppendLine("## 重要联系人邮件");
            foreach (var m in report.Important)
                sb.AppendLine($"- [{m.From}] {m.Subject} ({m.Time})\n  {m.Preview}");

            if (!string.IsNullOrEmpty(report.Others))
            {
                sb.AppendLine();
                sb.AppendLine("## 其他邮件摘要");
                // 截断到安全长度
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
            string summary = _latestReport;
            string html = $@"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>工作报告 - {_latestAccount}</title>
<style>
body{{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;max-width:800px;margin:40px auto;padding:0 20px;line-height:1.7;color:#333;background:#fafafa}}
h1{{font-size:24px;border-bottom:2px solid #5B9BD5;padding-bottom:8px}}
pre{{background:#fff;border:1px solid #e0e0e0;border-radius:8px;padding:16px;white-space:pre-wrap;font-size:14px;line-height:1.6}}
.time{{color:#999;font-size:13px;margin-bottom:20px}}
</style>
</head>
<body>
<h1>工作报告</h1>
<div class=""time"">账号: {System.Net.WebUtility.HtmlEncode(_latestAccount)} | {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>
<pre>{System.Net.WebUtility.HtmlEncode(summary)}</pre>
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

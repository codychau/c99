using C99.Models;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace C99.Services
{
    /// <summary>
    /// 模型网关：挂在 AI梦工厂 9527 HTTP 服务的 /gateway 前缀下，
    /// 把外部 Agent（dify/openclaw 等）的 OpenAI 兼容请求转发到上游模型，
    /// 并在转发过程中统计 Token 计入底座费用。
    /// </summary>
    public class ModelGateway
    {
        private readonly DreamFactoryConfig _config;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(300) };

        public ModelGateway(DreamFactoryConfig config)
        {
            _config = config;
        }

        /// <summary>解析上游 OpenAI 兼容基地址（自动兼容完整地址 / /v1 基地址 / 跟随当前模型配置；0.0.0.0 归一化为 127.0.0.1）</summary>
        public string ResolveUpstreamBase()
        {
            string raw = string.IsNullOrWhiteSpace(_config.GatewayConfig.UpstreamUrl)
                ? _config.GetEffectiveApiUrl()
                : _config.GatewayConfig.UpstreamUrl.Trim();

            string url = raw.TrimEnd('/');
            if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                url = url[..url.LastIndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase)].TrimEnd('/');

            // 0.0.0.0 / ::0 是不可作为连接目标的通配地址，HttpClient 会直接报错，统一改为本机回环
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                string host = uri.Host.Trim('[', ']');
                if (host == "0.0.0.0" || host == "::" || host == "::0" || host == "0:0:0:0:0:0:0:0")
                {
                    var ub = new UriBuilder(uri) { Host = "127.0.0.1" };
                    url = ub.Uri.ToString().TrimEnd('/');
                }
            }
            return url;
        }

        private HttpRequestMessage BuildForwardRequest(string method, string upstreamPath, string body, string? auth)
        {
            string upstreamBase = ResolveUpstreamBase();
            var req = new HttpRequestMessage(new HttpMethod(method), upstreamBase + upstreamPath);
            if (!string.IsNullOrEmpty(body))
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(auth))
                req.Headers.TryAddWithoutValidation("Authorization", auth);
            return req;
        }

        private static string? GetAuth(HttpListenerRequest request)
        {
            string[] keys = { "Authorization" };
            foreach (var k in keys)
            {
                var v = request.Headers[k];
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        /// <summary>POST /gateway/v1/chat/completions：转发并统计 Token。requestBody 为最终待转发的请求体（可由前置逻辑改写）</summary>
        public async Task HandleChatCompletionAsync(HttpListenerRequest request, HttpListenerResponse response,
            MetricsService? metrics, Action<string>? log, string requestBody)
        {
            string body = requestBody;
            bool stream = IsStreamRequest(body);
            int promptEstimate = EstimatePromptTokens(body);

            string? auth = GetAuth(request);
            using var upstreamReq = BuildForwardRequest("POST", "/chat/completions", body, auth);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var upstreamResp = await _http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead);
                int status = (int)upstreamResp.StatusCode;
                response.StatusCode = status;

                if (stream && upstreamResp.IsSuccessStatusCode)
                {
                    response.SendChunked = true;
                    response.ContentType = "text/event-stream";
                    response.AddHeader("Cache-Control", "no-cache");
                }
                else if (upstreamResp.Content.Headers.ContentType != null)
                {
                    response.ContentType = upstreamResp.Content.Headers.ContentType.ToString();
                }

                if (!upstreamResp.IsSuccessStatusCode)
                {
                    var errBody = await upstreamResp.Content.ReadAsStringAsync();
                    log?.Invoke($"网关转发失败: 上游返回 HTTP {status} {(errBody.Length > 200 ? errBody[..200] : errBody)}");
                    await WriteRawAsync(response, errBody, status, response.ContentType);
                    return;
                }

                int finalPrompt = promptEstimate;
                int finalCompletion = 0;

                using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync();
                if (stream)
                {
                    int streamPrompt = 0, streamCompletion = 0, charCompletion = 0;
                    using var streamReader = new StreamReader(upstreamStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                    using var outWriter = new StreamWriter(response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };

                    string? line;
                    while ((line = await streamReader.ReadLineAsync()) != null)
                    {
                        if (line.StartsWith("data:", StringComparison.Ordinal))
                        {
                            string payload = line["data:".Length..].Trim();
                            if (payload.StartsWith("{", StringComparison.Ordinal))
                            {
                                string content = ExtractContentDelta(payload);
                                charCompletion += CountTokensFromText(content);
                                if (TryGetUsage(payload, out int up, out int uc))
                                {
                                    if (up > 0) streamPrompt = up;
                                    if (uc > 0) streamCompletion = uc;
                                }
                            }
                        }
                        await outWriter.WriteLineAsync(line);
                    }
                    await outWriter.FlushAsync();

                    finalPrompt = streamPrompt > 0 ? streamPrompt : promptEstimate;
                    finalCompletion = streamCompletion > 0 ? streamCompletion : charCompletion;
                }
                else
                {
                    string respText = await new StreamReader(upstreamStream).ReadToEndAsync();
                    await WriteRawAsync(response, respText, 200, response.ContentType);
                    if (TryGetUsage(respText, out int up, out int uc))
                    {
                        if (up > 0) finalPrompt = up;
                        if (uc > 0) finalCompletion = uc;
                    }
                    if (finalCompletion <= 0)
                        finalCompletion = EstimateCompletionTokens(respText);
                }

                sw.Stop();
                metrics?.RecordAICall(finalPrompt, finalCompletion, sw.Elapsed.TotalMilliseconds, 0, true);
                log?.Invoke($"网关完成: prompt {finalPrompt} + completion {finalCompletion} tokens"
                    + (stream ? "（流式）" : "") + $"，耗时 {sw.Elapsed.TotalSeconds:F1}s");
            }
            catch (Exception ex)
            {
                sw.Stop();
                log?.Invoke($"网关请求异常: {ex.Message}");
                try
                {
                    response.StatusCode = 502;
                    response.ContentType = "application/json; charset=utf-8";
                    await WriteRawAsync(response,
                        JsonSerializer.Serialize(new { error = $"网关转发失败: {ex.Message}" }), 502, null);
                }
                catch { }
            }
        }

        /// <summary>GET /gateway/v1/models：透传上游模型列表</summary>
        public async Task HandleModelsAsync(HttpListenerRequest request, HttpListenerResponse response, Action<string>? log)
        {
            string? auth = GetAuth(request);
            using var upstreamReq = BuildForwardRequest("GET", "/models", "", auth);
            try
            {
                using var upstreamResp = await _http.SendAsync(upstreamReq);
                string body = await upstreamResp.Content.ReadAsStringAsync();
                await WriteRawAsync(response, body, (int)upstreamResp.StatusCode, "application/json");
            }
            catch (Exception ex)
            {
                log?.Invoke($"网关 /models 转发失败: {ex.Message}");
                await WriteJsonAsync(response, 502, new { error = ex.Message });
            }
        }

        /// <summary>其它 /gateway/v1/* 请求：透传但不计费</summary>
        public async Task HandleGenericAsync(HttpListenerRequest request, HttpListenerResponse response,
            string method, string upstreamPath, Action<string>? log)
        {
            string body = "";
            if (request.InputStream != null)
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                body = await reader.ReadToEndAsync();
            }
            string? auth = GetAuth(request);
            using var upstreamReq = BuildForwardRequest(method, upstreamPath, body, auth);
            try
            {
                using var upstreamResp = await _http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead);
                string raw = await upstreamResp.Content.ReadAsStringAsync();
                await WriteRawAsync(response, raw, (int)upstreamResp.StatusCode, "application/json");
            }
            catch (Exception ex)
            {
                log?.Invoke($"网关透传失败: {ex.Message}");
                await WriteJsonAsync(response, 502, new { error = ex.Message });
            }
        }

        // ==================== 前置逻辑支持 ====================

        /// <summary>从聊天请求体提取最后一条 user 消息的文本内容；无则返回空串</summary>
        internal static string ExtractUserPrompt(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
                    return "";
                JsonElement[] arr = new JsonElement[msgs.GetArrayLength()];
                int n = 0;
                foreach (var m in msgs.EnumerateArray())
                    arr[n++] = m;
                for (int i = n - 1; i >= 0; i--)
                {
                    if (arr[i].ValueKind != JsonValueKind.Object) continue;
                    if (arr[i].TryGetProperty("role", out var role)
                        && string.Equals(role.GetString(), "user", StringComparison.OrdinalIgnoreCase)
                        && arr[i].TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String)
                        return content.GetString() ?? "";
                }
            }
            catch { }
            return "";
        }

        /// <summary>用新文本替换最后一条 user 消息的 content，其余字段原样保留；解析失败或无 user 消息则返回原 body</summary>
        internal static string RebuildBodyWithPrompt(string body, string newPrompt)
        {
            if (string.IsNullOrEmpty(body)) return body;
            try
            {
                var doc = JsonNode.Parse(body);
                if (doc == null) return body;
                var msgs = doc["messages"] as JsonArray;
                if (msgs == null) return body;

                for (int i = msgs.Count - 1; i >= 0; i--)
                {
                    var m = msgs[i] as JsonObject;
                    if (m == null) continue;
                    var role = m["role"]?.GetValue<string>();
                    if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) continue;
                    if (m["content"] is JsonNode c && c.GetValueKind() == JsonValueKind.String)
                    {
                        m["content"] = newPrompt ?? "";
                        return doc.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    }
                }
            }
            catch { }
            return body;
        }

        // ==================== Token 估算 ====================

        private static bool IsStreamRequest(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
            }
            catch { return false; }
        }

        /// <summary>从聊天请求体 messages 估算 prompt tokens</summary>
        internal static int EstimatePromptTokens(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var sb = new StringBuilder();
                if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in msgs.EnumerateArray())
                    {
                        if (m.TryGetProperty("content", out var c))
                            sb.Append(c.GetString() ?? "");
                    }
                }
                int t = CountTokensFromText(sb.ToString());
                return t > 0 ? t : CountTokensFromText(body);
            }
            catch { return CountTokensFromText(body); }
        }

        private static int EstimateCompletionTokens(string respText)
        {
            try
            {
                using var doc = JsonDocument.Parse(respText);
                var root = doc.RootElement;
                var sb = new StringBuilder();
                if (root.TryGetProperty("choices", out var choices))
                {
                    foreach (var c in choices.EnumerateArray())
                    {
                        if (c.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var ct))
                        {
                            sb.Append(ct.GetString() ?? "");
                            break;
                        }
                    }
                }
                return CountTokensFromText(sb.ToString());
            }
            catch { return 0; }
        }

        /// <summary>提取流式增量中的 content 片段</summary>
        private static string ExtractContentDelta(string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
                    && ch[0].TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var ct))
                    return ct.GetString() ?? "";
            }
            catch { }
            return "";
        }

        /// <summary>尽力从响应/增量中解析 usage（真实 token 数）；失败返回 false</summary>
        private static bool TryGetUsage(string json, out int promptTokens, out int completionTokens)
        {
            promptTokens = 0;
            completionTokens = 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("usage", out var u)) return false;
                if (u.TryGetProperty("prompt_tokens", out var p)) promptTokens = p.GetInt32();
                if (u.TryGetProperty("completion_tokens", out var c)) completionTokens = c.GetInt32();
                return completionTokens > 0 || promptTokens > 0;
            }
            catch { return false; }
        }

        /// <summary>粗略 token 估算：CJK 按 1 字符 1 token，其它按 4 字符 1 token</summary>
        internal static int CountTokensFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int cjk = 0, other = 0;
            foreach (char c in text)
            {
                if (c >= 0x2E80 && c <= 0x9FFF) cjk++;
                else other++;
            }
            return cjk + (other + 3) / 4;
        }

        // ==================== 响应输出 ====================

        private static async Task WriteJsonAsync(HttpListenerResponse response, int status, object data)
        {
            await WriteRawAsync(response, JsonSerializer.Serialize(data), status, "application/json; charset=utf-8");
        }

        private static async Task WriteRawAsync(HttpListenerResponse response, string text, int status, string? contentType)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                response.StatusCode = status;
                if (!string.IsNullOrEmpty(contentType))
                    response.ContentType = contentType;
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                response.Close();
            }
            catch { }
        }
    }
}
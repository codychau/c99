using C99.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace C99.Services
{
    /// <summary>
    /// 向量模型调用服务（OpenAI 兼容 /embeddings 接口）
    /// </summary>
    public class VectorEmbeddingService : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private System.Diagnostics.Process? _localServer;
        private readonly object _localLock = new();

        /// <summary>将文本转为向量</summary>
        public async Task<float[]> EmbedAsync(string text, KnowledgeBaseConfig config)
        {
            if (config.VectorModel == "local")
                return await EmbedLocalAsync(text, config);
            return await EmbedRemoteAsync(text, config);
        }

        /// <summary>远程（自定义）API 向量化</summary>
        private async Task<float[]> EmbedRemoteAsync(string text, KnowledgeBaseConfig config)
        {
            string url = !string.IsNullOrEmpty(config.VectorModelApiUrl)
                ? config.VectorModelApiUrl
                : "http://localhost:8000/v1/embeddings";
            string key = config.VectorModelApiKey;

            var request = new { model = "text-embedding", input = text };
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrEmpty(key))
                req.Headers.Add("Authorization", $"Bearer {key}");

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync();
                throw new Exception($"向量模型调用失败: HTTP {(int)resp.StatusCode} {errBody}");
            }
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                throw new Exception("向量模型返回为空");
            var emb = data[0].GetProperty("embedding");
            var list = new List<float>();
            foreach (var item in emb.EnumerateArray())
                list.Add(item.GetSingle());
            return list.ToArray();
        }

        /// <summary>
        /// 本地启动模式：使用 llama.cpp 的 llama-server --embeddings 提供向量化服务。
        /// 首次调用时自动启动本地服务并等待就绪。
        /// </summary>
        private async Task<float[]> EmbedLocalAsync(string text, KnowledgeBaseConfig config)
        {
            EnsureLocalServerStarted(config);
            await WaitLocalServerReadyAsync(config);
            string url = $"http://127.0.0.1:{config.LocalEmbeddingPort}/v1/embeddings";
            string modelId = Path.GetFileNameWithoutExtension(config.LocalModelFile);

            var request = new { model = modelId, input = text };
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync();
                throw new Exception($"本地向量模型调用失败: HTTP {(int)resp.StatusCode} {errBody}");
            }
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                throw new Exception("本地向量模型返回为空");
            var emb = data[0].GetProperty("embedding");
            var list = new List<float>();
            foreach (var item in emb.EnumerateArray())
                list.Add(item.GetSingle());
            // 若返回维度与配置不一致，以服务实际维度为准
            config.Dimension = list.Count;
            return list.ToArray();
        }

        /// <summary>确保本地 llama.cpp embedding 服务已启动（并等待就绪）</summary>
        private void EnsureLocalServerStarted(KnowledgeBaseConfig config)
        {
            lock (_localLock)
            {
                if (_localServer != null && !_localServer.HasExited)
                    return;

                string dir = config.LlamaCppDir;
                string modelFile = config.LocalModelFile;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    throw new Exception("请先选择 llama.cpp 安装目录");
                if (string.IsNullOrEmpty(modelFile) || !File.Exists(modelFile))
                    throw new Exception("请先选择向量模型文件（.gguf）");

                // 在安装目录下查找嵌入服务可执行文件
                string mainExe = Path.Combine(dir, "llama-server.exe");
                if (!File.Exists(mainExe))
                    throw new Exception($"未找到 llama-server.exe（目录: {dir}）");

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = mainExe,
                    Arguments = $"-m \"{modelFile}\" --host 127.0.0.1 --port {config.LocalEmbeddingPort} --embeddings --pooling mean",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                try
                {
                    _localServer = new System.Diagnostics.Process { StartInfo = psi };
                    _localServer.OutputDataReceived += (s, e) => { if (e.Data != null) System.Diagnostics.Debug.WriteLine($"[llama-server] {e.Data}"); };
                    _localServer.ErrorDataReceived += (s, e) => { if (e.Data != null) System.Diagnostics.Debug.WriteLine($"[llama-server-err] {e.Data}"); };
                    _localServer.Start();
                    _localServer.BeginOutputReadLine();
                    _localServer.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    _localServer = null;
                    throw new Exception($"启动 llama-server 失败: {ex.Message}");
                }
            }
        }

        /// <summary>轮询等待本地 embedding 服务就绪（最多 60 秒）</summary>
        private async Task WaitLocalServerReadyAsync(KnowledgeBaseConfig config)
        {
            var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try
            {
                for (int i = 0; i < 60; i++)
                {
                    if (_localServer != null && _localServer.HasExited)
                        throw new Exception("llama-server 进程已退出，无法提供向量服务");
                    try
                    {
                        var resp = await probe.GetAsync($"http://127.0.0.1:{config.LocalEmbeddingPort}/health");
                        if (resp.IsSuccessStatusCode)
                            return;
                    }
                    catch { }
                    await Task.Delay(1000);
                }
                throw new Exception("等待 llama-server 就绪超时（60 秒），请检查模型文件或端口配置");
            }
            finally
            {
                probe.Dispose();
            }
        }

        /// <summary>批量文本转向量：一次请求传入多条文本，减少网络往返。</summary>
        public async Task<List<float[]>> EmbedBatchAsync(List<string> texts, KnowledgeBaseConfig config, CancellationToken ct = default)
        {
            if (texts.Count == 0) return new List<float[]>();
            if (config.VectorModel == "local")
                return await EmbedLocalBatchAsync(texts, config, ct);
            return await EmbedRemoteBatchAsync(texts, config, ct);
        }

        /// <summary>远程 API 批量向量化（`input` 传数组，按 data[i] 取回）</summary>
        private async Task<List<float[]>> EmbedRemoteBatchAsync(List<string> texts, KnowledgeBaseConfig config, CancellationToken ct)
        {
            string url = !string.IsNullOrEmpty(config.VectorModelApiUrl)
                ? config.VectorModelApiUrl
                : "http://localhost:8000/v1/embeddings";
            string key = config.VectorModelApiKey;

            var request = new { model = "text-embedding", input = texts };
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrEmpty(key))
                req.Headers.Add("Authorization", $"Bearer {key}");

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync();
                throw new Exception($"向量模型调用失败: HTTP {(int)resp.StatusCode} {errBody}");
            }
            var body = await resp.Content.ReadAsStringAsync();
            return ParseEmbeddings(body, texts.Count);
        }

        /// <summary>本地 llama-server 批量向量化（一次请求传多条）</summary>
        private async Task<List<float[]>> EmbedLocalBatchAsync(List<string> texts, KnowledgeBaseConfig config, CancellationToken ct)
        {
            EnsureLocalServerStarted(config);
            await WaitLocalServerReadyAsync(config);
            string url = $"http://127.0.0.1:{config.LocalEmbeddingPort}/v1/embeddings";
            string modelId = Path.GetFileNameWithoutExtension(config.LocalModelFile);

            var request = new { model = modelId, input = texts };
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync();
                throw new Exception($"本地向量模型调用失败: HTTP {(int)resp.StatusCode} {errBody}");
            }
            var body = await resp.Content.ReadAsStringAsync();
            var result = ParseEmbeddings(body, texts.Count);
            // 以服务实际维度为准
            if (result.Count > 0 && result[0].Length > 0)
                config.Dimension = result[0].Length;
            return result;
        }

        /// <summary>解析 /embeddings 响应（data 数组，按 index 对齐输入顺序）</summary>
        private static List<float[]> ParseEmbeddings(string body, int expectedCount)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                throw new Exception("向量模型返回为空");

            var byIndex = new Dictionary<int, float[]>();
            foreach (var item in data.EnumerateArray())
            {
                int idx = byIndex.Count;
                if (item.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out int parsedIdx))
                    idx = parsedIdx;
                var emb = item.GetProperty("embedding");
                var list = new List<float>();
                foreach (var v in emb.EnumerateArray())
                    list.Add(v.GetSingle());
                byIndex[idx] = list.ToArray();
            }

            var result = new List<float[]>(expectedCount);
            for (int i = 0; i < expectedCount; i++)
                result.Add(byIndex.TryGetValue(i, out var v) ? v : Array.Empty<float>());
            return result;
        }

        public void Dispose()
        {
            try
            {
                if (_localServer != null && !_localServer.HasExited)
                {
                    _localServer.Kill();
                    _localServer.WaitForExit(3000);
                }
            }
            catch { }
            _localServer = null;
            _http.Dispose();
        }
    }
}
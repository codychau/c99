using C99.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace C99.Services
{
    /// <summary>
    /// Milvus 外置向量库适配器（通过 Milvus HTTP API 交互）。
    /// </summary>
    public class MilvusVectorDbService : IVectorStore
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _username;
        private readonly string _password;
        private readonly string _database;

        private HttpClient? _http;
        private string? _authToken;

        public VectorDbType DbType => VectorDbType.Milvus;
        public bool IsConnected { get; private set; }

        public MilvusVectorDbService(string host, int port, string username, string password, string database)
        {
            _host = string.IsNullOrWhiteSpace(host) ? "localhost" : host;
            _port = port > 0 ? port : 19530;
            _username = username;
            _password = password;
            _database = string.IsNullOrWhiteSpace(database) ? "default" : database;
        }

        private string BaseUrl => $"http://{_host}:{_port}";

        public string GetConfigSummary()
        {
            return $"Milvus ({_host}:{_port}, db={_database})";
        }

        public async Task<bool> ConnectAsync()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                // 尝试连通性检查：获取集合列表
                var resp = await PostJsonAsync("/v2/vectordb/collections/list", new { db_name = _database, fields = new[] { "collection_name" } });
                IsConnected = true;
                return true;
            }
            catch
            {
                IsConnected = false;
                return false;
            }
        }

        public Task DisconnectAsync()
        {
            _http?.Dispose();
            _http = null;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public async Task<bool> CreateCollectionAsync(string collectionName, int dimension)
        {
            var payload = new
            {
                db_name = _database,
                collection_name = collectionName,
                dimension = dimension,
                metric_type = "COSINE",
                fields = new object[]
                {
                    new { field_name = "pk", data_type = "VarChar", is_primary = true, max_length = 128 },
                    new { field_name = "content", data_type = "VarChar", max_length = 65535 },
                    new { field_name = "metadata", data_type = "JSON" }
                },
                enable_dynamic_field = true
            };
            var resp = await PostJsonAsync("/v2/vectordb/collections/create", payload);
            return IsSuccess(resp);
        }

        public async Task<bool> DropCollectionAsync(string collectionName)
        {
            var resp = await PostJsonAsync("/v2/vectordb/collections/drop",
                new { db_name = _database, collection_name = collectionName });
            return IsSuccess(resp);
        }

        public async Task<List<string>> ListCollectionsAsync()
        {
            var resp = await PostJsonAsync("/v2/vectordb/collections/list",
                new { db_name = _database, fields = new[] { "collection_name" } });
            var data = ParseResponse(resp);
            var result = new List<string>();
            if (data.HasValue && data.Value.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("collection_name", out var name))
                        result.Add(name.GetString() ?? "");
                }
            }
            return result;
        }

        public async Task<bool> CollectionExistsAsync(string collectionName)
        {
            var list = await ListCollectionsAsync();
            return list.Any(n => string.Equals(n, collectionName, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<bool> AddAsync(string collectionName, List<KnowledgeChunk> chunks)
        {
            if (chunks == null || chunks.Count == 0) return true;

            var rows = new List<Dictionary<string, object>>();
            foreach (var c in chunks)
            {
                var row = new Dictionary<string, object>
                {
                    ["pk"] = c.Id,
                    ["content"] = c.Content ?? "",
                    ["metadata"] = c.Metadata
                };
                if (c.Embedding != null)
                    row["vector"] = c.Embedding;
                rows.Add(row);
            }

            var payload = new
            {
                db_name = _database,
                collection_name = collectionName,
                data = rows
            };
            var resp = await PostJsonAsync("/v2/vectordb/entities/insert", payload);
            return IsSuccess(resp);
        }

        public async Task<bool> DeleteAsync(string collectionName, string docId)
        {
            var payload = new
            {
                db_name = _database,
                collection_name = collectionName,
                filter = $"pk == \"{docId}\""
            };
            var resp = await PostJsonAsync("/v2/vectordb/entities/delete", payload);
            return IsSuccess(resp);
        }

        public async Task<bool> DeleteByMetadataAsync(string collectionName, string metadataKey, string metadataValue)
        {
            var payload = new
            {
                db_name = _database,
                collection_name = collectionName,
                filter = $"metadata[\"{metadataKey}\"] == \"{metadataValue}\""
            };
            var resp = await PostJsonAsync("/v2/vectordb/entities/delete", payload);
            return IsSuccess(resp);
        }

        public async Task<List<KnowledgeChunk>> SearchAsync(string collectionName, float[] queryVector, int topK)
        {
            var payload = new
            {
                db_name = _database,
                collection_name = collectionName,
                data = new[] { queryVector },
                limit = topK,
                output_fields = new[] { "content", "metadata" },
                metric_type = "COSINE",
                consistency_level = "Bounded",
                offset = 0
            };
            var resp = await PostJsonAsync("/v2/vectordb/entities/search", payload);
            var data = ParseResponse(resp);
            var results = new List<KnowledgeChunk>();
            if (data.HasValue && data.Value.TryGetProperty("data", out var arr))
            {
                if (arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Array) continue;
                        foreach (var hit in item.EnumerateArray())
                        {
                            var chunk = new KnowledgeChunk();
                            if (hit.TryGetProperty("id", out var id)) chunk.Id = id.GetString() ?? "";
                            if (hit.TryGetProperty("content", out var content)) chunk.Content = content.GetString() ?? "";
                            if (hit.TryGetProperty("distance", out var dist)) chunk.Score = 1 - dist.GetDouble();
                            if (hit.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in meta.EnumerateObject())
                                    chunk.Metadata[prop.Name] = prop.Value.ToString();
                            }
                            chunk.CollectionName = collectionName;
                            results.Add(chunk);
                        }
                    }
                }
            }
            return results;
        }

        public async Task<long> CountAsync(string collectionName)
        {
            var resp = await PostJsonAsync("/v2/vectordb/entities/get",
                new
                {
                    db_name = _database,
                    collection_name = collectionName,
                    filter = "pk != \"\"",
                    output_fields = new[] { "pk" },
                    limit = 1
                });
            // Milvus REST 无直接 count；这里使用近似方式（返回 limit 后判断）。如需精确可在集合详细里获取。
            var data = ParseResponse(resp);
            var stats = await PostJsonAsync("/v2/vectordb/collections/stats",
                new { db_name = _database, collection_name = collectionName });
            var statData = ParseResponse(stats);
            if (statData.HasValue && statData.Value.TryGetProperty("data", out var s) &&
                s.TryGetProperty("row_count", out var rc))
                return rc.GetInt64();
            return data.HasValue && data.Value.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? -1 : 0;
        }

        public async Task<List<KnowledgeChunk>> GetAllAsync(string collectionName)
        {
            var payload = new
            {
                db_name = _database,
                collection_name = collectionName,
                filter = "pk != \"\"",
                output_fields = new[] { "content", "metadata" },
                limit = 1000
            };
            var resp = await PostJsonAsync("/v2/vectordb/entities/get", payload);
            var data = ParseResponse(resp);
            var results = new List<KnowledgeChunk>();
            if (data.HasValue && data.Value.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var chunk = new KnowledgeChunk();
                    chunk.Id = item.TryGetProperty("pk", out var pk) ? (pk.GetString() ?? "") : "";
                    chunk.Content = item.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
                    if (item.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in meta.EnumerateObject())
                            chunk.Metadata[prop.Name] = prop.Value.ToString();
                    }
                    chunk.CollectionName = collectionName;
                    results.Add(chunk);
                }
            }
            return results;
        }

        public async Task<string> GetSourceFileAsync(string collectionName, string docId)
        {
            var payload = new
            {
                db_name = _database,
                collection_name = collectionName,
                filter = $"pk == \"{docId}\"",
                output_fields = new[] { "metadata" }
            };
            var resp = await PostJsonAsync("/v2/vectordb/entities/get", payload);
            var data = ParseResponse(resp);
            if (data.HasValue && data.Value.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("pk", out var pk) && pk.GetString() == docId
                        && item.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                    {
                        if (meta.TryGetProperty("source_file", out var sf) && sf.GetString() is string s)
                            return s;
                    }
                }
            }
            return "";
        }

        public async Task<string> GetContentAsync(string collectionName, string docId)
        {
            var payload = new
            {
                db_name = _database,
                collection_name = collectionName,
                filter = $"pk == \"{docId}\"",
                output_fields = new[] { "content" }
            };
            var resp = await PostJsonAsync("/v2/vectordb/entities/get", payload);
            var data = ParseResponse(resp);
            if (data.HasValue && data.Value.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("pk", out var pk) && pk.GetString() == docId)
                    {
                        if (item.TryGetProperty("content", out var c))
                            return c.GetString() ?? "";
                    }
                }
            }
            return "";
        }

        // ==================== HTTP 辅助 ====================

        private async Task<string> PostJsonAsync(string path, object payload)
        {
            if (_http == null) throw new InvalidOperationException("未连接 Milvus");
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path) { Content = content };
            req.Headers.Add("Accept", "application/json");
            if (!string.IsNullOrEmpty(_authToken))
                req.Headers.Add("Authorization", $"Bearer {_authToken}");
            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (_authToken == null)
            {
                // 尝试 token 获取（若鉴权返回 code 2000/2001）
                try
                {
                    var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("code", out var code) &&
                        (code.GetInt32() == 2000 || code.GetInt32() == 2001))
                    {
                        var token = await GetAuthTokenAsync();
                        if (!string.IsNullOrEmpty(token))
                        {
                            _authToken = token;
                            req.Headers.Remove("Authorization");
                            req.Headers.Add("Authorization", $"Bearer {_authToken}");
                            var resp2 = await _http.SendAsync(req);
                            body = await resp2.Content.ReadAsStringAsync();
                        }
                    }
                }
                catch { }
            }
            return body;
        }

        private async Task<string?> GetAuthTokenAsync()
        {
            try
            {
                var loginPayload = new { username = _username, password = _password };
                var content = new StringContent(JsonSerializer.Serialize(loginPayload), Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/vectordb/users/login") { Content = content };
                var resp = await _http!.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("token", out var token))
                    return token.GetString();
            }
            catch { }
            return null;
        }

        private static bool IsSuccess(string body)
        {
            try
            {
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("code", out var code))
                    return code.GetInt32() == 0;
            }
            catch { }
            return true;
        }

        private static JsonElement? ParseResponse(string body)
        {
            try { return JsonDocument.Parse(body).RootElement; }
            catch { return null; }
        }
    }
}
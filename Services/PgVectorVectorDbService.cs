using C99.Models;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace C99.Services
{
    /// <summary>
    /// PostgreSQL + pgvector 外置向量库适配器。
    /// </summary>
    public class PgVectorVectorDbService : IVectorStore
    {
        private string _connectionString;
        private NpgsqlConnection? _conn;

        public VectorDbType DbType => VectorDbType.PgVector;
        public bool IsConnected => _conn?.State == System.Data.ConnectionState.Open;

        public PgVectorVectorDbService(string host, int port, string database, string username, string password)
        {
            _connectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password};Timeout=10;";
        }

        public string GetConfigSummary()
        {
            return $"pgvector (PostgreSQL)";
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                _conn = new NpgsqlConnection(_connectionString);
                await _conn.OpenAsync();
                using var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", _conn);
                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch
            {
                try { _conn?.Dispose(); } catch { }
                _conn = null;
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            if (_conn != null)
            {
                try { await _conn.CloseAsync(); } catch { }
                try { _conn.Dispose(); } catch { }
                _conn = null;
            }
            await Task.CompletedTask;
        }

        private string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

        private async Task<string> EnsureTableAsync(string collectionName, int dimension)
        {
            string table = NormalizeName(collectionName);
            string dim = dimension.ToString();
            string sql = $"CREATE TABLE IF NOT EXISTS {Q(table)} (" +
                         "id VARCHAR(128) PRIMARY KEY, " +
                         $"embedding vector({dim}), " +
                         "content TEXT, " +
                         "metadata JSONB, " +
                         "created_at TIMESTAMPTZ DEFAULT now());";
            using var cmd = new NpgsqlCommand(sql, _conn);
            await cmd.ExecuteNonQueryAsync();
            return table;
        }

        private static string NormalizeName(string name)
        {
            var chars = (name ?? "kb_collection").Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            return new string(chars);
        }

        public async Task<bool> CreateCollectionAsync(string collectionName, int dimension)
        {
            if (_conn == null) return false;
            try { await EnsureTableAsync(collectionName, dimension); return true; }
            catch { return false; }
        }

        public async Task<bool> DropCollectionAsync(string collectionName)
        {
            if (_conn == null) return false;
            try
            {
                using var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS {Q(NormalizeName(collectionName))};", _conn);
                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch { return false; }
        }

        public async Task<List<string>> ListCollectionsAsync()
        {
            var result = new List<string>();
            if (_conn == null) return result;
            try
            {
                // 以表作为集合，过滤系统表
                using var cmd = new NpgsqlCommand(
                    "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_name NOT LIKE 'pg_%' ORDER BY table_name;", _conn);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    result.Add(reader.GetString(0));
            }
            catch { }
            return result;
        }

        public async Task<bool> CollectionExistsAsync(string collectionName)
        {
            var list = await ListCollectionsAsync();
            return list.Any(n => string.Equals(n, NormalizeName(collectionName), StringComparison.OrdinalIgnoreCase));
        }

        public async Task<bool> AddAsync(string collectionName, List<KnowledgeChunk> chunks)
        {
            if (_conn == null || chunks == null || chunks.Count == 0) return true;
            try
            {
                int dim = chunks.FirstOrDefault()?.Embedding?.Length ?? 1536;
                string table = await EnsureTableAsync(collectionName, dim);

                foreach (var chunk in chunks)
                {
                    var vecText = chunk.Embedding != null
                        ? "[" + string.Join(",", chunk.Embedding.Select(f => f.ToString("G9"))) + "]"
                        : null;
                    string metaJson = JsonSerializer.Serialize(chunk.Metadata);
                    using var cmd = new NpgsqlCommand(
                        $"INSERT INTO {Q(table)} (id, embedding, content, metadata, created_at) VALUES (@id, {VecLiteral(vecText)}, @content, cast(@meta AS jsonb), @ca) " +
                        "ON CONFLICT (id) DO UPDATE SET content=EXCLUDED.content, metadata=EXCLUDED.metadata, created_at=EXCLUDED.created_at;", _conn);
                    cmd.Parameters.AddWithValue("id", chunk.Id);
                    if (vecText != null) cmd.Parameters.AddWithValue("vec", vecText);
                    cmd.Parameters.AddWithValue("content", chunk.Content ?? "");
                    cmd.Parameters.AddWithValue("meta", metaJson);
                    cmd.Parameters.AddWithValue("ca", chunk.CreatedAt);
                    await cmd.ExecuteNonQueryAsync();
                }
                return true;
            }
            catch { return false; }
        }

        private static string VecLiteral(string? vecText)
        {
            return vecText == null ? "NULL" : $"@vec::vector";
        }

        public async Task<bool> DeleteAsync(string collectionName, string docId)
        {
            if (_conn == null) return false;
            try
            {
                string table = await EnsureTableAsync(collectionName, 1536);
                using var cmd = new NpgsqlCommand($"DELETE FROM {Q(table)} WHERE id=@id;", _conn);
                cmd.Parameters.AddWithValue("id", docId);
                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch { return false; }
        }

        public async Task<bool> DeleteByMetadataAsync(string collectionName, string metadataKey, string metadataValue)
        {
            if (_conn == null) return false;
            try
            {
                string table = await EnsureTableAsync(collectionName, 1536);
                using var cmd = new NpgsqlCommand(
                    $"DELETE FROM {Q(table)} WHERE metadata->>@key = @val;", _conn);
                cmd.Parameters.AddWithValue("key", metadataKey);
                cmd.Parameters.AddWithValue("val", metadataValue);
                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch { return false; }
        }

        public async Task<List<KnowledgeChunk>> SearchAsync(string collectionName, float[] queryVector, int topK)
        {
            var result = new List<KnowledgeChunk>();
            if (_conn == null || queryVector == null) return result;
            try
            {
                string table = await EnsureTableAsync(collectionName, queryVector.Length);
                string vecText = "[" + string.Join(",", queryVector.Select(f => f.ToString("G9"))) + "]";
                using var cmd = new NpgsqlCommand(
                    $"SELECT id, content, metadata, embedding <=> @q::vector AS dist " +
                    $"FROM {Q(table)} ORDER BY dist LIMIT @topk;", _conn);
                cmd.Parameters.AddWithValue("q", vecText);
                cmd.Parameters.AddWithValue("topk", Math.Max(1, topK));
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var chunk = new KnowledgeChunk
                    {
                        Id = reader.GetString(0),
                        Content = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        Score = 1 - (reader.IsDBNull(3) ? 0 : reader.GetDouble(3) < 2 ? reader.GetDouble(3) : Math.Min(1, reader.GetDouble(3))),
                        CollectionName = collectionName
                    };
                    if (!reader.IsDBNull(2))
                    {
                        try
                        {
                            var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2));
                            if (meta != null) chunk.Metadata = meta;
                        }
                        catch { }
                    }
                    result.Add(chunk);
                }
            }
            catch { }
            return result;
        }

        public async Task<long> CountAsync(string collectionName)
        {
            if (_conn == null) return 0;
            try
            {
                string table = await EnsureTableAsync(collectionName, 1536);
                using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {Q(table)};", _conn);
                object? result = await cmd.ExecuteScalarAsync();
                return result == null ? 0 : Convert.ToInt64(result);
            }
            catch { return 0; }
        }

        public async Task<List<KnowledgeChunk>> GetAllAsync(string collectionName)
        {
            var result = new List<KnowledgeChunk>();
            if (_conn == null) return result;
            try
            {
                string table = await EnsureTableAsync(collectionName, 1536);
                using var cmd = new NpgsqlCommand($"SELECT id, content, metadata, created_at FROM {Q(table)};", _conn);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var chunk = new KnowledgeChunk
                    {
                        Id = reader.GetString(0),
                        Content = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        CollectionName = collectionName
                    };
                    if (!reader.IsDBNull(2))
                    {
                        try
                        {
                            var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2));
                            if (meta != null) chunk.Metadata = meta;
                        }
                        catch { }
                    }
                    if (!reader.IsDBNull(3)) chunk.CreatedAt = reader.GetDateTime(3);
                    result.Add(chunk);
                }
            }
            catch { }
            return result;
        }
    }
}
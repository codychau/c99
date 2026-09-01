using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using C99.Models;

namespace C99.Services
{
    /// <summary>
    /// 内置向量库服务：通过 P/Invoke 调用 Rust DLL（BuiltInVectorDb.dll）实现知识库存储与检索。
    /// 若运行时 DLL 不存在，所有操作将抛出异常并提示用户。
    /// </summary>
    public class BuiltInVectorDbService : IVectorStore, IDisposable
    {
        private readonly NativeVectorDb? _native;
        private readonly string _dataDir;
        private readonly string _dllPath;

        // 原生 DLL 不是线程安全的，所有 P/Invoke 必须串行执行且不能占用 UI 线程
        private readonly SemaphoreSlim _nativeLock = new(1, 1);

        public VectorDbType DbType => VectorDbType.BuiltIn;
        public bool IsConnected => _native?.IsOpen ?? false;

        /// <summary>Rust DLL 缺失时的错误信息（空表示已加载成功）</summary>
        public string LastNativeError { get; private set; } = "";

        public BuiltInVectorDbService(string? dataDir = null)
        {
            // DLL 固定从程序目录加载；dataDir 仅用于数据文件的存储目录
            _dataDir = string.IsNullOrWhiteSpace(dataDir) ? AppContext.BaseDirectory : dataDir;
            _dllPath = Path.Combine(AppContext.BaseDirectory, "BuiltInVectorDb.dll");
            _native = TryLoadNative(_dataDir);
        }

        public string GetConfigSummary()
        {
            if (_native != null)
                return $"内置向量库（Rust DLL，数据目录：{_native.DataDir}，算法：{_native.CurrentMetric}）";
            return $"内置向量库加载失败：{LastNativeError}";
        }

        public Task<bool> ConnectAsync()
        {
            EnsureNative();
            return RunNativeAsync(() => true);
        }

        public Task DisconnectAsync()
        {
            return RunNativeAsyncVoid(() => _native?.Close());
        }

        public Task<bool> CreateCollectionAsync(string collectionName, int dimension)
        {
            EnsureNative();
            return RunNativeAsync(() =>
            {
                bool ok = _native!.CreateCollection(collectionName, dimension);
                if (ok) _native.Dimension = dimension;
                return ok;
            });
        }

        public Task<bool> DropCollectionAsync(string collectionName)
        {
            EnsureNative();
            return RunNativeAsync(() => _native!.DropCollection(collectionName));
        }

        public Task<List<string>> ListCollectionsAsync()
        {
            EnsureNative();
            return RunNativeAsync(() => _native!.ListCollections());
        }

        public async Task<bool> CollectionExistsAsync(string collectionName)
        {
            var list = await ListCollectionsAsync();
            return list.Any(n => string.Equals(n, collectionName, StringComparison.OrdinalIgnoreCase));
        }

        public Task<bool> AddAsync(string collectionName, List<KnowledgeChunk> chunks)
        {
            EnsureNative();
            return RunNativeAsync(() =>
            {
                bool ok = true;
                foreach (var c in chunks)
                {
                    if (c.Embedding == null || c.Embedding.Length == 0)
                        c.Embedding = FallbackHashEmbedding(c.Content, _native!.Dimension);
                    // 取源文件标识（从 metadata.path 的文件名），供分片存储与增量识别
                    string source = "";
                    if (c.Metadata.TryGetValue("path", out var path) &&
                        !string.IsNullOrWhiteSpace(path))
                        source = Path.GetFileName(path);
                    else if (c.Metadata.TryGetValue("source_file", out var sf))
                        source = sf;
                    ok &= _native!.Add(collectionName, c.Id, c.Embedding,
                        c.Content, JsonSerializer.Serialize(c.Metadata), source);
                }
                return ok;
            });
        }

        /// <summary>获取集合已入库的源文件列表（供增量导入跳过）</summary>
        public Task<List<string>> ListSourceFilesAsync(string collectionName)
        {
            EnsureNative();
            return RunNativeAsync(() => _native!.ListSourceFiles(collectionName));
        }

        public Task<bool> DeleteAsync(string collectionName, string docId)
        {
            EnsureNative();
            return RunNativeAsync(() => _native!.Delete(collectionName, docId));
        }

        public Task<bool> DeleteByMetadataAsync(string collectionName, string metadataKey, string metadataValue)
        {
            // Rust 端暂无按元数据删除接口
            return Task.FromResult(false);
        }

        public Task<List<KnowledgeChunk>> SearchAsync(string collectionName, float[] queryVector, int topK)
        {
            EnsureNative();
            return RunNativeAsync(() => _native!.Search(collectionName, queryVector, topK));
        }

        public Task<long> CountAsync(string collectionName)
        {
            EnsureNative();
            return RunNativeAsync(() => _native!.Count(collectionName));
        }

        public Task<List<KnowledgeChunk>> GetAllAsync(string collectionName)
        {
            EnsureNative();
            return RunNativeAsync(() => _native!.GetAll(collectionName));
        }

        /// <summary>本地哈希向量兜底（无 API 时）</summary>
        private static float[] FallbackHashEmbedding(string text, int dim)
        {
            if (dim <= 0) dim = 256;
            var vec = new float[dim];
            foreach (var ch in text.ToLowerInvariant())
            {
                int idx = Math.Abs(ch.GetHashCode()) % dim;
                vec[idx] += 1f;
            }
            double norm = 0;
            for (int i = 0; i < dim; i++) norm += vec[i] * vec[i];
            norm = Math.Sqrt(norm);
            if (norm > 1e-12)
            {
                for (int i = 0; i < dim; i++) vec[i] = (float)(vec[i] / norm);
            }
            return vec;
        }

        public void Dispose()
        {
            _native?.Close();
        }

        /// <summary>
        /// 在后台线程串行执行原生调用，避免占用 UI 线程。
        /// 原生回调耗时（尤其批量 Add / Search）时，UI 保持响应。
        /// </summary>
        private Task<T> RunNativeAsync<T>(Func<T> action)
        {
            return Task.Run(async () =>
            {
                await _nativeLock.WaitAsync();
                try { return action(); }
                finally { _nativeLock.Release(); }
            });
        }

        private Task RunNativeAsyncVoid(Action action)
        {
            return Task.Run(async () =>
            {
                await _nativeLock.WaitAsync();
                try { action(); }
                finally { _nativeLock.Release(); }
            });
        }

        /// <summary>校验 DLL 已加载，否则抛出异常提示用户。</summary>
        private void EnsureNative()
        {
            if (_native == null)
            {
                throw new InvalidOperationException(
                    $"内置向量库（BuiltInVectorDb.dll）不存在或加载失败（{LastNativeError}），" +
                    $"无法实现知识库的存储和检索。请将 BuiltInVectorDb.dll 复制到应用目录后重启程序。");
            }
        }

        /// <summary>尝试加载 Rust DLL，成功返回实例，失败返回 null 并记录错误。</summary>
        private NativeVectorDb? TryLoadNative(string dataDir)
        {
            if (!File.Exists(_dllPath))
            {
                LastNativeError = $"未找到 DLL: {_dllPath}";
                return null;
            }

            try
            {
                var context = NativeMethods.KB_Open(dataDir);
                if (context == IntPtr.Zero)
                {
                    LastNativeError = "KB_Open 返回 NULL";
                    return null;
                }
                return new NativeVectorDb(dataDir, context);
            }
            catch (Exception ex)
            {
                LastNativeError = $"加载失败：{ex.Message}";
                return null;
            }
        }

        #region Rust DLL 封装（P/Invoke）

        private sealed class NativeVectorDb
        {
            public readonly string DataDir;
            public IntPtr Context;
            public int Dimension;
            public string CurrentMetric = "cosine";

            public NativeVectorDb(string dataDir, IntPtr context)
            {
                DataDir = dataDir;
                Context = context;
            }

            public bool IsOpen => Context != IntPtr.Zero;

            public void Close()
            {
                if (Context != IntPtr.Zero)
                {
                    NativeMethods.KB_Close(Context);
                    Context = IntPtr.Zero;
                }
            }

            public bool CreateCollection(string name, int dim)
            {
                return Context != IntPtr.Zero &&
                       !string.IsNullOrEmpty(name) && dim > 0 &&
                       NativeMethods.KB_CreateCollection(Context, name, dim) == 0;
            }

            public bool DropCollection(string name)
            {
                return Context != IntPtr.Zero &&
                       NativeMethods.KB_DropCollection(Context, name) == 0;
            }

            public List<string> ListCollections()
            {
                var json = InvokeJson((IntPtr buf, int len) => NativeMethods.KB_ListCollections(Context, buf, len));
                if (string.IsNullOrEmpty(json)) return new List<string>();
                try
                {
                    return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                }
                catch { return new List<string>(); }
            }

            public bool Add(string collectionName, string id, float[] embedding, string content, string metadataJson, string sourceFile)
            {
                if (Context == IntPtr.Zero || string.IsNullOrEmpty(collectionName) ||
                    string.IsNullOrEmpty(id)) return false;
                unsafe
                {
                    fixed (float* pVec = embedding)
                    {
                        int rc = NativeMethods.KB_Add(Context, collectionName, id, pVec, embedding.Length,
                            content, metadataJson, sourceFile);
                        return rc == 0;
                    }
                }
            }

            public List<string> ListSourceFiles(string collectionName)
            {
                if (Context == IntPtr.Zero) return new List<string>();
                var json = InvokeJson((IntPtr buf, int len) =>
                    NativeMethods.KB_ListSourceFiles(Context, collectionName, buf, len));
                if (string.IsNullOrEmpty(json)) return new List<string>();
                try
                {
                    return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                }
                catch { return new List<string>(); }
            }

            public bool Delete(string collectionName, string id)
            {
                return Context != IntPtr.Zero &&
                       NativeMethods.KB_Delete(Context, collectionName, id) == 0;
            }

            public List<KnowledgeChunk> Search(string collectionName, float[] queryVector, int topK)
            {
                if (Context == IntPtr.Zero || queryVector == null || queryVector.Length == 0)
                    return new List<KnowledgeChunk>();
                var json = InvokeJson((IntPtr buf, int len) =>
                {
                    unsafe
                    {
                        fixed (float* pQuery = queryVector)
                        {
                            return NativeMethods.KB_Search(Context, collectionName, pQuery, queryVector.Length,
                                topK, buf, len);
                        }
                    }
                });
                return ParseHits(json, collectionName);
            }

            public long Count(string collectionName)
            {
                if (Context == IntPtr.Zero) return -1;
                return NativeMethods.KB_Count(Context, collectionName);
            }

            public List<KnowledgeChunk> GetAll(string collectionName)
            {
                if (Context == IntPtr.Zero) return new List<KnowledgeChunk>();
                var json = InvokeJson((IntPtr buf, int len) =>
                    NativeMethods.KB_ReadAll(Context, collectionName, buf, len));
                return ParseHits(json, collectionName);
            }

            public bool SetMetric(string name, string metric)
            {
                if (Context == IntPtr.Zero) return false;
                int rc = NativeMethods.KB_SetMetric(Context, name, metric);
                if (rc == 0) CurrentMetric = metric;
                return rc == 0;
            }

            /// <summary>调用返回可变长 JSON 的原生函数；返回码 0=成功，&gt;0=需要更大缓冲区。</summary>
            private string InvokeJson(Func<IntPtr, int, int> invoke)
            {
                if (Context == IntPtr.Zero) return "";
                int size = 8192;
                for (int attempt = 0; attempt < 32; attempt++)
                {
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        int rc = invoke(buf, size);
                        if (rc == 0)
                        {
                            return Marshal.PtrToStringUTF8(buf) ?? "";
                        }
                        if (rc < 0) return "";
                        // rc > 0：所需缓冲区长度（含 NUL）
                        size = rc + 1;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                return "";
            }

            private static List<KnowledgeChunk> ParseHits(string json, string collectionName)
            {
                if (string.IsNullOrEmpty(json)) return new List<KnowledgeChunk>();
                try
                {
                    var hits = JsonSerializer.Deserialize<List<SearchHitJson>>(json);
                    if (hits == null) return new List<KnowledgeChunk>();
                    return hits.Select(h => new KnowledgeChunk
                    {
                        Id = h.Id ?? "",
                        Content = h.Content ?? "",
                        Metadata = h.Metadata?.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()) ?? new(),
                        Score = h.Score,
                        CollectionName = collectionName
                    }).ToList();
                }
                catch { return new List<KnowledgeChunk>(); }
            }

            private sealed class SearchHitJson
            {
                public string? Id { get; set; }
                public string? Content { get; set; }
                public Dictionary<string, JsonElement>? Metadata { get; set; }
                public double Score { get; set; }
            }
        }

        /// <summary>
        /// 原生函数声明（Rust 端 `extern "C"`，UTF-8 字符串，cdecl 调用约定）。
        /// </summary>
        private static unsafe class NativeMethods
        {
            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_Open", CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr KB_Open([MarshalAs(UnmanagedType.LPUTF8Str)] string dataDir);

            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_Close", CallingConvention = CallingConvention.Cdecl)]
            public static extern void KB_Close(IntPtr ctx);

            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_CreateCollection", CallingConvention = CallingConvention.Cdecl)]
            public static extern int KB_CreateCollection(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int dim);

            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_DropCollection", CallingConvention = CallingConvention.Cdecl)]
            public static extern int KB_DropCollection(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_ListCollections", CallingConvention = CallingConvention.Cdecl)]
            public static extern int KB_ListCollections(IntPtr ctx, IntPtr outBuf, int bufLen);

            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_Add", CallingConvention = CallingConvention.Cdecl)]
            public static extern int KB_Add(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string id, float* vec, int dim,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string content,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string metadataJson,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceFile);

            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_Delete", CallingConvention = CallingConvention.Cdecl)]
            public static extern int KB_Delete(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string id);

            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_Search", CallingConvention = CallingConvention.Cdecl)]
            public static extern int KB_Search(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                float* queryVec, int dim, int topK, IntPtr outBuf, int bufLen);

            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_Count", CallingConvention = CallingConvention.Cdecl)]
            public static extern long KB_Count(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_ReadAll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int KB_ReadAll(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                IntPtr outBuf, int bufLen);

            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_ListSourceFiles", CallingConvention = CallingConvention.Cdecl)]
            public static extern int KB_ListSourceFiles(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                IntPtr outBuf, int bufLen);

            [DllImport("BuiltInVectorDb.dll", EntryPoint = "KB_SetMetric", CallingConvention = CallingConvention.Cdecl)]
            public static extern int KB_SetMetric(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string metric);
        }

        #endregion
    }
}
using C99.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace C99.Services
{
    /// <summary>
    /// 向量存储统一接口：屏蔽内置 DLL 与外置 Milvus/pgvector 的差异。
    /// </summary>
    public interface IVectorStore
    {
        /// <summary>数据库类型</summary>
        VectorDbType DbType { get; }

        /// <summary>是否已连接</summary>
        bool IsConnected { get; }

        /// <summary>建立连接 / 初始化</summary>
        Task<bool> ConnectAsync();

        /// <summary>关闭连接</summary>
        Task DisconnectAsync();

        /// <summary>创建集合（collection / table）</summary>
        Task<bool> CreateCollectionAsync(string collectionName, int dimension);

        /// <summary>删除集合</summary>
        Task<bool> DropCollectionAsync(string collectionName);

        /// <summary>列出所有集合</summary>
        Task<List<string>> ListCollectionsAsync();

        /// <summary>集合是否存在</summary>
        Task<bool> CollectionExistsAsync(string collectionName);

        /// <summary>向集合中插入文档（含向量）</summary>
        Task<bool> AddAsync(string collectionName, List<KnowledgeChunk> chunks);

        /// <summary>删除文档</summary>
        Task<bool> DeleteAsync(string collectionName, string docId);

        /// <summary>按元数据删除文档</summary>
        Task<bool> DeleteByMetadataAsync(string collectionName, string metadataKey, string metadataValue);

        /// <summary>向量召回：queryVector 为查询向量，返回 topK 条相似文档</summary>
        Task<List<KnowledgeChunk>> SearchAsync(string collectionName, float[] queryVector, int topK);

        /// <summary>获取集合内文档数</summary>
        Task<long> CountAsync(string collectionName);

        /// <summary>读取集合内全部文档（供整理/导出）</summary>
        Task<List<KnowledgeChunk>> GetAllAsync(string collectionName);

        /// <summary>获取自定义支持的配置说明文本</summary>
        string GetConfigSummary();
    }
}
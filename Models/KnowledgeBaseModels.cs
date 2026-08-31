using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace C99.Models
{
    /// <summary>
    /// 向量数据库类型
    /// </summary>
    public enum VectorDbType
    {
        BuiltIn = 0,      // 内置（通过 DLL 或内置托管实现）
        Milvus = 1,       // 外置 Milvus
        PgVector = 2      // 外置 PostgreSQL + pgvector
    }

    /// <summary>
    /// 知识库配置（保存到 AppConfig）
    /// </summary>
    public class KnowledgeBaseConfig
    {
        /// <summary>用户自定义名称</summary>
        public string Name { get; set; } = "知识库";

        /// <summary>分类标识</summary>
        public string Category { get; set; } = "知识库";

        /// <summary>向量模型来源：custom=自定义(OpenAI 兼容 API)，local=本地启动(llama.cpp)</summary>
        public string VectorModel { get; set; } = "custom";

        /// <summary>向量模型的自定义 API 地址（用于自定义模型）</summary>
        public string VectorModelApiUrl { get; set; } = "";

        /// <summary>向量模型 API Key</summary>
        public string VectorModelApiKey { get; set; } = "";

        /// <summary>向量维度</summary>
        public int Dimension { get; set; } = 1536;

        /// <summary>本地启动：llama.cpp 安装目录</summary>
        public string LlamaCppDir { get; set; } = "";

        /// <summary>本地启动：向量模型文件（.gguf）</summary>
        public string LocalModelFile { get; set; } = "";

        /// <summary>本地启动：embedding 服务端口</summary>
        public int LocalEmbeddingPort { get; set; } = 18080;

        /// <summary>向量数据库类型</summary>
        public VectorDbType DbType { get; set; } = VectorDbType.BuiltIn;

        /// <summary>默认集合名称</summary>
        public string CollectionName { get; set; } = "knowledge_base";

        /// <summary>内置：数据文件保存目录</summary>
        public string BuiltInDataDir { get; set; } = "";

        /// <summary>外置：Milvus / PgVector 地址</summary>
        public string ExternalHost { get; set; } = "localhost";

        /// <summary>外置：端口</summary>
        public int ExternalPort { get; set; } = 19530;

        /// <summary>外置：用户名</summary>
        public string ExternalUsername { get; set; } = "root";

        /// <summary>外置：密码</summary>
        public string ExternalPassword { get; set; } = "";

        /// <summary>外置：数据库名（PgVector 必填）</summary>
        public string ExternalDatabase { get; set; } = "postgres";

        /// <summary>外置：连接字符串（可选，优先于 Host/Port 组合）</summary>
        public string ExternalConnectionString { get; set; } = "";

        /// <summary>召回：Top-K 默认值</summary>
        public int TopK { get; set; } = 5;
    }

    /// <summary>
    /// 向量数据库接口统一返回/入参模型
    /// </summary>
    public class KnowledgeChunk
    {
        /// <summary>唯一 Id</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>所属集合</summary>
        public string CollectionName { get; set; } = "";

        /// <summary>文本内容</summary>
        public string Content { get; set; } = "";

        /// <summary>元数据（文件名、来源、更新时间等）</summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>向量（构建时写入，召回时可为空）</summary>
        public float[]? Embedding { get; set; }

        /// <summary>召回得分</summary>
        public double Score { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
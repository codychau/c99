using C99.Models;

namespace C99.Services
{
    /// <summary>
    /// 向量库工厂
    /// </summary>
    public static class VectorStoreFactory
    {
        public static IVectorStore Create(KnowledgeBaseConfig config)
        {
            return config.DbType switch
            {
                VectorDbType.Milvus => new MilvusVectorDbService(
                    config.ExternalHost, config.ExternalPort,
                    config.ExternalUsername, config.ExternalPassword,
                    config.ExternalDatabase),
                VectorDbType.PgVector => new PgVectorVectorDbService(
                    config.ExternalHost, config.ExternalPort,
                    config.ExternalDatabase, config.ExternalUsername, config.ExternalPassword),
                _ => new BuiltInVectorDbService(config.BuiltInDataDir),
            };
        }
    }
}
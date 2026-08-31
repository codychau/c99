# 内置向量数据库 (Rust) —— BuiltInVectorDb.dll

用 Rust 实现的轻量向量数据库，编译为 Windows 动态链接库，
由 C# 主程序通过 P/Invoke 加载调用。

## 位置与构建

- 源码目录：`NATIVE/vector-db-rust/`
- 构建命令：`build.bat`（已包含 release 构建 + 单元测试）
- 产物：`target\release\BuiltInVectorDb.dll`

## 功能

- 集合（collection）增删改查
- 向量召回（Top-K 相似检索）
- 可切换三种距离/相似度算法（按集合配置）：
  - `cosine` 余弦相似度（默认）
  - `euclidean` 欧氏距离（内部换算为得分 1/(1+d)）
  - `dot` 点积
- 数据自动持久化为 JSON 文件，保存在数据目录（启动时传入）
- 线程安全：单实例顺序调用即可（接口非线程安全，调用方需自行串行化）

## C ABI 接口契约

所有导出函数使用 C 调用约定（`cdecl`）。
字符串统一为 UTF-8 编码；`int` 返回 0 表示成功。

| 函数 | 签名 | 说明 |
|---|---|---|
| `KB_Open` | `void* KB_Open(const char* dataDir)` | 创建/打开引擎，返回句柄；失败返回 NULL |
| `KB_Close` | `void KB_Close(void* ctx)` | 释放引擎 |
| `KB_CreateCollection` | `int(... ctx, const char* name, int dim)` | 建集合（同维度，默认余弦） |
| `KB_DropCollection` | `int(... ctx, const char* name)` | 删集合 |
| `KB_ListCollections` | `int(... ctx, char* outBuf, int bufLen)` | 输出 JSON `["a","b"]` |
| `KB_SetMetric` | `int(... ctx, const char* name, const char* metric)` | 切换算法：cosine/euclidean/dot |
| `KB_Add` | `int(... ctx, name, const char* id, const float* vec, int dim, const char* content, const char* metadataJson)` | 插入/覆盖 |
| `KB_Delete` | `int(... ctx, name, const char* id)` | 删记录 |
| `KB_Search` | `int(... ctx, name, const float* queryVec, int dim, int topK, char* outBuf, int bufLen)` | 召回 |
| `KB_Count` | `long(... ctx, name)` | 记录数；失败 -1 |
| `KB_ReadAll` | `int(... ctx, name, char* outBuf, int bufLen)` | 全量读取 |

### 输出缓冲区约定（KB_ListCollections / KB_Search / KB_ReadAll）

- 写入 UTF-8 JSON，末尾补 `\0`
- 返回 `0` 表示写入成功
- 返回正数表示所需缓冲区长度（调用方扩容后重试）

### KB_Search 返回 JSON

```json
[
  { "id": "abc", "content": "...", "metadata": { "source": "file", "path": "..." }, "score": 0.9234 }
]
```

### 示例 C# P/Invoke（对齐约定）

```csharp
[LibraryImport("BuiltInVectorDb.dll", CallingConvention = CallingConvention.Cdecl)]
private static partial nint KB_Open(byte[] dataDirUtf8);

[LibraryImport("BuiltInVectorDb.dll", CallingConvention = CallingConvention.Cdecl)]
private static partial void KB_Close(nint ctx);

[LibraryImport("BuiltInVectorDb.dll", CallingConvention = CallingConvention.Cdecl)]
private static partial int KB_CreateCollection(nint ctx, byte[] nameUtf8, int dim);

[LibraryImport("BuiltInVectorDb.dll", CallingConvention = CallingConvention.Cdecl)]
private static partial int KB_Search(nint ctx, byte[] nameUtf8, float[] queryVec, int dim, int topK, byte[] outBuf, int bufLen);

[LibraryImport("BuiltInVectorDb.dll", CallingConvention = CallingConvention.Cdecl)]
private static partial int KB_SetMetric(nint ctx, byte[] nameUtf8, byte[] metricUtf8);

[LibraryImport("BuiltInVectorDb.dll", CallingConvention = CallingConvention.Cdecl)]
private static partial long KB_Count(nint ctx, byte[] nameUtf8);
```

> 注意：字符串以 UTF-8 字节数组（含 `\0`）传递；浮点向量为 `float[]` 连续内存。
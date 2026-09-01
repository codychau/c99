use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::PathBuf;

/// 距离/相似度算法
#[derive(Clone, Copy, PartialEq, Eq, Debug, Serialize, Deserialize)]
pub enum Metric {
    /// 余弦相似度（值越大越相似），默认
    Cosine,
    /// 欧氏距离（越小越近，转成得分 1/(1+d)）
    Euclidean,
    /// 点积（越大越相似）
    Dot,
}

impl Metric {
    pub fn parse(s: &str) -> Option<Metric> {
        match s.to_ascii_lowercase().as_str() {
            "cosine" | "cos" | "余弦" => Some(Metric::Cosine),
            "euclidean" | "l2" | "欧氏" | "欧几里得" => Some(Metric::Euclidean),
            "dot" => Some(Metric::Dot),
            _ => None,
        }
    }
}

/// 集合（collection）中的一条记录
#[derive(Clone, Serialize, Deserialize)]
pub struct Record {
    pub id: String,
    pub content: String,
    pub metadata: serde_json::Value,
    pub embedding: Vec<f32>,
    #[serde(default)]
    pub created_at: String,
    /// 排序用的得分（仅在检索结果中填充，持久化时为 0）
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub score: Option<f32>,
}

/// 检索结果
#[derive(Serialize)]
pub struct SearchHit {
    pub id: String,
    pub content: String,
    pub metadata: serde_json::Value,
    pub score: f32,
}

impl Record {
    pub fn score_against(&self, query: &[f32], metric: Metric) -> f32 {
        match metric {
            Metric::Cosine => cosine_similarity(query, &self.embedding),
            Metric::Euclidean => {
                let d = euclidean_distance(query, &self.embedding);
                1.0 / (1.0 + d)
            }
            Metric::Dot => dot(query, &self.embedding),
        }
    }
}

/// 磁盘上单文件分片（每个源文件对应一个分片 JSON）。
/// 旧版本的单文件格式（无 source 字段）兼容反序列化：source 缺省为空串。
#[derive(Serialize, Deserialize)]
pub struct ShardFile {
    pub name: String,   // 集合名
    #[serde(default)]
    pub source: String, // 源文件名（空串表示历史单文件格式，加载时用文件 stem 兜底）
    pub dim: usize,
    pub metric: Metric,
    pub records: Vec<Record>,
}

/// 集合（内存模型）
pub struct Collection {
    pub dim: usize,
    pub metric: Metric,
    /// source 文件名 -> 该来源的全部记录（分片）
    pub shards: HashMap<String, Vec<Record>>,
}

/// 历史单文件格式的默认 source（避免覆盖真实源文件名）
const LEGACY_SOURCE_PREFIX: &str = "__legacy__";

/// 空/空白 source 归一到历史默认值，保证文件命名稳定
fn normalize_source(source: &str) -> String {
    if source.trim().is_empty() {
        LEGACY_SOURCE_PREFIX.to_string()
    } else {
        source.to_string()
    }
}

impl Collection {
    fn new(_name: &str, dim: usize, metric: Metric) -> Self {
        Collection {
            dim,
            metric,
            shards: HashMap::new(),
        }
    }

    /// 合并一个分片文件到集合（load_all 阶段使用）
    fn merge_shard(&mut self, shard: &ShardFile, fallback_source: &str) {
        let source = if shard.source.trim().is_empty() {
            fallback_source.to_string()
        } else {
            shard.source.clone()
        };
        let records = self.shards.entry(source).or_default();
        for r in &shard.records {
            // 同 id 覆盖
            if let Some(slot) = records.iter_mut().find(|e| e.id == r.id) {
                *slot = r.clone();
            } else {
                records.push(r.clone());
            }
        }
    }

    /// 全部记录（跨所有分片）
    pub fn all_records(&self) -> Vec<&Record> {
        let mut out: Vec<&Record> = Vec::new();
        for records in self.shards.values() {
            out.extend(records.iter());
        }
        out
    }

    fn add(&mut self, source: &str, record: Record) -> bool {
        if record.embedding.len() != self.dim {
            return false;
        }
        let src = normalize_source(source);
        let records = self.shards.entry(src).or_default();
        // 同 id 覆盖
        if let Some(slot) = records.iter_mut().find(|r| r.id == record.id) {
            *slot = record;
            return true;
        }
        records.push(record);
        true
    }

    /// 删除记录，返回 (是否有删除, 受影响的分片 sources)
    fn delete(&mut self, id: &str) -> (bool, Vec<String>) {
        let mut affected: Vec<String> = Vec::new();
        for (src, records) in self.shards.iter_mut() {
            let before = records.len();
            records.retain(|r| r.id != id);
            if records.len() != before {
                affected.push(src.clone());
            }
        }
        (affected.len() > 0, affected)
    }

    pub fn search(&self, query: &[f32], top_k: usize, metric: Metric) -> Vec<SearchHit> {
        let mut hits: Vec<SearchHit> = self
            .all_records()
            .into_iter()
            .map(|r| SearchHit {
                id: r.id.clone(),
                content: r.content.clone(),
                metadata: r.metadata.clone(),
                score: r.score_against(query, metric),
            })
            .collect();
        // Cosine / Dot 与得分都按降序；Euclidean 也换算成了升序得分，统一降序取前 N
        hits.sort_by(|a, b| b.score.partial_cmp(&a.score).unwrap_or(std::cmp::Ordering::Equal));
        hits.truncate(top_k.max(1));
        hits
    }

    /// 已入库的源文件名列表（去重、排序，供增量导入跳过）
    pub fn sources(&self) -> Vec<String> {
        let mut names: Vec<String> = self.shards.keys().cloned().collect();
        names.sort();
        names
    }
}

/// 一个引擎实例（持有所有集合）
pub struct KbEngine {
    pub data_dir: PathBuf,
    pub collections: HashMap<String, Collection>,
}

impl KbEngine {
    pub fn new(data_dir: &str) -> Self {
        let mut engine = KbEngine {
            data_dir: PathBuf::from(data_dir),
            collections: HashMap::new(),
        };
        if !engine.data_dir.exists() {
            let _ = std::fs::create_dir_all(&engine.data_dir);
        }
        engine.load_all();
        engine
    }

    /// 将集合名规整为合法文件名前缀（保留中文等 Unicode，仅替换 Windows 非法字符）
    fn safe_collection_prefix(&self, name: &str) -> String {
        let mut safe: String = name
            .trim()
            .chars()
            .map(|c| {
                if c.is_control() || matches!(c, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*') {
                    '_'
                } else {
                    c
                }
            })
            .collect();
        // Windows 不允许文件名以空格或句点结尾
        while safe.ends_with([' ', '.']) {
            safe.pop();
        }
        if safe.is_empty() {
            safe = "collection".to_string();
        }
        safe
    }

    /// 分片文件名：`{集合名}.{source 的短 base64url}.json`（无 = 填充）
    fn shard_file(&self, name: &str, source: &str) -> PathBuf {
        let safe = self.safe_collection_prefix(name);
        let ident = shard_identifier(source);
        self.data_dir.join(format!("{safe}.{ident}.json"))
    }

    fn load_all(&mut self) {
        if let Ok(entries) = std::fs::read_dir(&self.data_dir) {
            for entry in entries.flatten() {
                let path = entry.path();
                if path.extension().and_then(|e| e.to_str()) != Some("json") {
                    continue;
                }
                let Ok(text) = std::fs::read_to_string(&path) else {
                    continue;
                };
                let Ok(shard) = serde_json::from_str::<ShardFile>(&text) else {
                    continue;
                };
                let fallback = path
                    .file_stem()
                    .and_then(|s| s.to_str())
                    .unwrap_or("")
                    .to_string();
                self.merge_shard(&shard, &fallback);
            }
        }
    }

    fn merge_shard(&mut self, shard: &ShardFile, fallback_source: &str) {
        let key = if !shard.name.trim().is_empty() {
            shard.name.clone()
        } else {
            fallback_source.to_string()
        };
        if key.is_empty() {
            return;
        }
        let col = self
            .collections
            .entry(key.clone())
            .or_insert_with(|| Collection::new(&key, shard.dim.max(1), shard.metric));
        col.merge_shard(shard, fallback_source);
    }

    /// 仅供测试/内部使用的可变访问器（常规流程走 search/read_all 等方法）
    #[allow(dead_code)]
    pub fn get_or_none(&mut self, name: &str) -> Option<&mut Collection> {
        self.collections.get_mut(name)
    }

    pub fn has(&self, name: &str) -> bool {
        self.collections.contains_key(name)
    }

    pub fn create_collection(&mut self, name: &str, dim: usize, metric: Metric) -> bool {
        if self.collections.contains_key(name) {
            return false;
        }
        let col = Collection::new(name, dim, metric);
        self.collections.insert(name.to_string(), col);
        true
    }

    pub fn drop_collection(&mut self, name: &str) -> bool {
        let col = match self.collections.remove(name) {
            Some(c) => c,
            None => return false,
        };
        // 删除该集合名下的全部相关 json 文件（依据内容反解析，避免误删）
        if let Ok(entries) = std::fs::read_dir(&self.data_dir) {
            for entry in entries.flatten() {
                let path = entry.path();
                if path.extension().and_then(|e| e.to_str()) != Some("json") {
                    continue;
                }
                if let Ok(text) = std::fs::read_to_string(&path) {
                    if let Ok(shard) = serde_json::from_str::<ShardFile>(&text) {
                        if shard.name == name {
                            let _ = std::fs::remove_file(&path);
                        }
                    }
                }
            }
        }
        // 兜底：删除通过当前规则生成的分片文件名
        for src in col.sources() {
            let _ = std::fs::remove_file(self.shard_file(name, &src));
        }
        true
    }

    pub fn set_metric(&mut self, name: &str, metric: Metric) -> bool {
        if !self.has(name) {
            return false;
        }
        if let Some(col) = self.collections.get_mut(name) {
            col.metric = metric;
        }
        self.save_collection(name);
        true
    }

    pub fn list_collections(&self) -> Vec<String> {
        let mut names: Vec<String> = self.collections.keys().cloned().collect();
        names.sort();
        names
    }

    /// 集合已入库的源文件列表（供增量导入判断）
    pub fn list_source_files(&self, col_name: &str) -> Vec<String> {
        match self.collections.get(col_name) {
            Some(col) => col.sources(),
            None => Vec::new(),
        }
    }

    pub fn add(&mut self, col_name: &str, source: &str, record: Record) -> bool {
        let src = normalize_source(source);
        let ok = match self.collections.get_mut(col_name) {
            Some(col) => col.add(&src, record),
            None => return false,
        };
        if ok {
            // 仅重写受影响的分片
            let records = self
                .collections
                .get(col_name)
                .and_then(|c| c.shards.get(&src))
                .cloned()
                .unwrap_or_default();
            self.write_shard(col_name, &src, &records);
        }
        ok
    }

    pub fn delete(&mut self, col_name: &str, id: &str) -> bool {
        let sources = match self.collections.get_mut(col_name) {
            Some(col) => col.delete(id),
            None => (false, Vec::new()),
        };
        let (ok, sources) = sources;
        if ok {
            for src in sources {
                let records = self
                    .collections
                    .get(col_name)
                    .and_then(|c| c.shards.get(&src))
                    .cloned()
                    .unwrap_or_default();
                self.write_shard(col_name, &src, &records);
            }
        }
        ok
    }

    pub fn count(&self, col_name: &str) -> i64 {
        match self.collections.get(col_name) {
            Some(col) => col.all_records().len() as i64,
            None => -1,
        }
    }

    pub fn search(&self, col_name: &str, query: &[f32], top_k: usize) -> Option<Vec<SearchHit>> {
        let col = self.collections.get(col_name)?;
        Some(col.search(query, top_k, col.metric))
    }

    pub fn read_all(&self, col_name: &str) -> Option<Vec<SearchHit>> {
        let col = self.collections.get(col_name)?;
        let hits = col
            .all_records()
            .into_iter()
            .map(|r| SearchHit {
                id: r.id.clone(),
                content: r.content.clone(),
                metadata: r.metadata.clone(),
                score: 0.0,
            })
            .collect();
        Some(hits)
    }

    /// 写一个分片文件（空分片则删除文件）
    fn write_shard(&self, col_name: &str, source: &str, records: &[Record]) {
        if records.is_empty() {
            let _ = std::fs::remove_file(self.shard_file(col_name, source));
            return;
        }
        let safe = self.safe_collection_prefix(col_name);
        let ident = shard_identifier(source);
        // 规整后的源内容：分片文件内非空 source
        let shard = ShardFile {
            name: col_name.to_string(),
            source: source.to_string(),
            dim: self.collections.get(col_name).map(|c| c.dim).unwrap_or(1),
            metric: self
                .collections
                .get(col_name)
                .map(|c| c.metric)
                .unwrap_or(Metric::Cosine),
            records: records.to_vec(),
        };
        if let Ok(text) = serde_json::to_string_pretty(&shard) {
            let _ = std::fs::write(
                self.data_dir.join(format!("{safe}.{ident}.json")),
                text,
            );
        }
    }

    /// 整个集合持久化（遍历全部分片写文件，用于创建后初始写入等）
    fn save_collection(&self, name: &str) {
        if let Some(col) = self.collections.get(name) {
            for (src, records) in &col.shards {
                self.write_shard(name, src, records);
            }
        }
    }
}

// ==================== 短 base64url（无填充） ====================

const B64URL_CHARS: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

/// 标准 base64（无 padding），仅用于生成分片文件名的短标识。
fn base64url(data: &[u8]) -> String {
    let mut out = String::with_capacity((data.len() + 2) / 3 * 4);
    for chunk in data.chunks(3) {
        let b0 = chunk[0];
        let b1 = *chunk.get(1).unwrap_or(&0);
        let b2 = *chunk.get(2).unwrap_or(&0);
        let n = ((b0 as u32) << 16) | ((b1 as u32) << 8) | (b2 as u32);
        out.push(B64URL_CHARS[((n >> 18) & 63) as usize] as char);
        out.push(B64URL_CHARS[((n >> 12) & 63) as usize] as char);
        out.push(if chunk.len() > 1 { B64URL_CHARS[((n >> 6) & 63) as usize] as char } else { '=' });
        out.push(if chunk.len() > 2 { B64URL_CHARS[(n & 63) as usize] as char } else { '=' });
    }
    out.trim_end_matches('=').to_string()
}

/// 保证文件名长度受控的短标识：完整 base64url，若过长则前缀 + FNV-1a 哈希后缀
fn shard_identifier(source: &str) -> String {
    let b64 = base64url(source.as_bytes());
    const MAX_LEN: usize = 96;
    if b64.len() <= MAX_LEN {
        return b64;
    }
    let hash = fnv1a(source.as_bytes());
    format!("{}.{:016x}", &b64[..80], hash)
}

/// FNV-1a 64 位哈希（无依赖）
fn fnv1a(data: &[u8]) -> u64 {
    let mut hash: u64 = 0xcbf29ce484222325;
    for &b in data {
        hash ^= b as u64;
        hash = hash.wrapping_mul(0x100000001b3);
    }
    hash
}

// ==================== 距离算法 ====================

fn dot(a: &[f32], b: &[f32]) -> f32 {
    let n = a.len().min(b.len());
    let mut sum = 0.0f32;
    for i in 0..n {
        sum += a[i] * b[i];
    }
    sum
}

pub fn cosine_similarity(a: &[f32], b: &[f32]) -> f32 {
    let n = a.len().min(b.len());
    let mut dot = 0.0f32;
    let mut na = 0.0f32;
    let mut nb = 0.0f32;
    for i in 0..n {
        dot += a[i] * b[i];
        na += a[i] * a[i];
        nb += b[i] * b[i];
    }
    if na <= f32::EPSILON || nb <= f32::EPSILON {
        return 0.0;
    }
    dot / (na.sqrt() * nb.sqrt())
}

pub fn euclidean_distance(a: &[f32], b: &[f32]) -> f32 {
    let n = a.len().min(b.len());
    let mut sum = 0.0f32;
    for i in 0..n {
        let d = a[i] - b[i];
        sum += d * d;
    }
    sum.sqrt()
}
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

/// 集合
#[derive(Serialize, Deserialize)]
pub struct Collection {
    pub name: String,
    pub dim: usize,
    pub metric: Metric,
    pub records: Vec<Record>,
}

impl Collection {
    fn new(name: &str, dim: usize, metric: Metric) -> Self {
        Collection {
            name: name.to_string(),
            dim,
            metric,
            records: Vec::new(),
        }
    }

    fn add(&mut self, record: Record) -> bool {
        if record.embedding.len() != self.dim {
            return false;
        }
        // 同 id 覆盖
        if let Some(slot) = self.records.iter_mut().find(|r| r.id == record.id) {
            *slot = record;
            return true;
        }
        self.records.push(record);
        true
    }

    fn delete(&mut self, id: &str) -> bool {
        let before = self.records.len();
        self.records.retain(|r| r.id != id);
        before != self.records.len()
    }

    pub fn search(&self, query: &[f32], top_k: usize, metric: Metric) -> Vec<SearchHit> {
        let mut hits: Vec<SearchHit> = self
            .records
            .iter()
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

    fn collection_file(&self, name: &str) -> PathBuf {
        let mut safe: String = name
            .trim()
            .chars()
            .map(|c| {
                if c.is_ascii_alphanumeric() || c == '_' || c == '-' {
                    c
                } else {
                    '_'
                }
            })
            .collect();
        if safe.is_empty() {
            safe = "collection".to_string();
        }
        self.data_dir.join(format!("{safe}.json"))
    }

    fn load_all(&mut self) {
        if let Ok(entries) = std::fs::read_dir(&self.data_dir) {
            for entry in entries.flatten() {
                let path = entry.path();
                if path.extension().and_then(|e| e.to_str()) == Some("json") {
                    if let Some(name) = path.file_stem().and_then(|s| s.to_str()) {
                        if let Ok(text) = std::fs::read_to_string(&path) {
                            if let Ok(col) = serde_json::from_str::<Collection>(&text) {
                                self.collections.insert(name.to_string(), col);
                            }
                        }
                    }
                }
            }
        }
    }

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
        if self.collections.remove(name).is_none() {
            return false;
        }
        let _ = std::fs::remove_file(self.collection_file(name));
        true
    }

    pub fn set_metric(&mut self, name: &str, metric: Metric) -> bool {
        match self.get_or_none(name) {
            Some(col) => {
                col.metric = metric;
                self.save(name);
                true
            }
            None => false,
        }
    }

    pub fn list_collections(&self) -> Vec<String> {
        let mut names: Vec<String> = self.collections.keys().cloned().collect();
        names.sort();
        names
    }

    pub fn add(&mut self, col_name: &str, record: Record) -> bool {
        match self.get_or_none(col_name) {
            Some(col) => {
                let ok = col.add(record);
                if ok {
                    self.save(col_name);
                }
                ok
            }
            None => false,
        }
    }

    pub fn delete(&mut self, col_name: &str, id: &str) -> bool {
        match self.get_or_none(col_name) {
            Some(col) => {
                let ok = col.delete(id);
                if ok {
                    self.save(col_name);
                }
                ok
            }
            None => false,
        }
    }

    pub fn count(&self, col_name: &str) -> i64 {
        match self.collections.get(col_name) {
            Some(col) => col.records.len() as i64,
            None => -1,
        }
    }

    pub fn save(&self, name: &str) {
        if let Some(col) = self.collections.get(name) {
            if let Ok(text) = serde_json::to_string_pretty(col) {
                let _ = std::fs::write(self.collection_file(name), text);
            }
        }
    }
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
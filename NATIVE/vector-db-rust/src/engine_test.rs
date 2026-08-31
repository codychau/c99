use crate::engine::{KbEngine, Metric, Record};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};

static COUNTER: AtomicU32 = AtomicU32::new(0);

/// 简易临时目录（清理通过 drop 删除）
struct TempDir(PathBuf);

impl TempDir {
    fn new() -> TempDir {
        let n = COUNTER.fetch_add(1, Ordering::SeqCst);
        let path = std::env::temp_dir().join(format!("kb_rust_test_{}_{}", std::process::id(), n));
        let _ = std::fs::remove_dir_all(&path);
        let _ = std::fs::create_dir_all(&path);
        TempDir(path)
    }

    fn path(&self) -> &PathBuf {
        &self.0
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn demo_record(id: &str, dim: usize, values: Vec<f32>) -> Record {
    let mut meta = serde_json::Map::new();
    meta.insert("source".to_string(), serde_json::json!("test"));
    Record {
        id: id.to_string(),
        content: format!("content-{id}"),
        metadata: serde_json::Value::Object(meta),
        embedding: {
            // 对齐到 dim
            let mut v = values;
            v.resize(dim, 0.0);
            v
        },
        created_at: 0.to_string(),
        score: None,
    }
}

fn temp_engine() -> (TempDir, KbEngine) {
    let dir = TempDir::new();
    let engine = KbEngine::new(dir.path().to_str().unwrap());
    (dir, engine)
}

#[test]
fn create_and_drop_collection() {
    let (_dir, mut engine) = temp_engine();
    assert!(engine.create_collection("docs", 4, Metric::Cosine));
    // 重名失败
    assert!(!engine.create_collection("docs", 4, Metric::Cosine));
    assert!(engine.has("docs"));
    assert_eq!(engine.list_collections(), vec!["docs".to_string()]);
    assert!(engine.drop_collection("docs"));
    assert!(!engine.has("docs"));
}

#[test]
fn add_and_upsert_and_delete() {
    let (_dir, mut engine) = temp_engine();
    engine.create_collection("c", 4, Metric::Cosine);
    assert!(engine.add("c", demo_record("1", 4, vec![1.0, 0.0, 0.0, 0.0])));
    assert_eq!(engine.count("c"), 1);
    // 相同 id 覆盖
    assert!(engine.add("c", demo_record("1", 4, vec![0.0, 1.0, 0.0, 0.0])));
    assert_eq!(engine.count("c"), 1);
    // 维度不匹配失败
    assert!(!engine.add("c", demo_record("2", 3, vec![1.0, 1.0, 1.0])));
    assert!(engine.delete("c", "1"));
    assert_eq!(engine.count("c"), 0);
    assert!(!engine.delete("c", "nope"));
}

#[test]
fn search_cosine_ranks() {
    let (_dir, mut engine) = temp_engine();
    engine.create_collection("c", 4, Metric::Cosine);
    engine.add("c", demo_record("a", 4, vec![1.0, 0.0, 0.0, 0.0]));
    engine.add("c", demo_record("b", 4, vec![0.0, 1.0, 0.0, 0.0]));
    engine.add("c", demo_record("c0", 4, vec![0.9, 0.1, 0.0, 0.0]));

    let col = engine.get_or_none("c").unwrap();
    let hits = col.search(&[1.0, 0.0, 0.0, 0.0], 3, Metric::Cosine);
    assert_eq!(hits.len(), 3);
    // 与查询 [1,0,0,0] 余弦最接近的应是 a 与 c0
    assert_eq!(hits[0].id, "a");
    assert!(hits[0].score > hits[2].score);
}

#[test]
fn set_metric_changes_ordering() {
    let (_dir, mut engine) = temp_engine();
    engine.create_collection("c", 3, Metric::Cosine);
    engine.add("c", demo_record("near", 3, vec![2.0, 0.0, 0.0]));
    engine.add("c", demo_record("far", 3, vec![100.0, 0.0, 0.0]));
    // 欧氏距离：query=[0,0,0] 时 near 更近 -> 得分更高
    assert!(engine.set_metric("c", Metric::Euclidean));
    let col = engine.get_or_none("c").unwrap();
    let hits = col.search(&[0.0, 0.0, 0.0], 2, Metric::Euclidean);
    assert_eq!(hits[0].id, "near");
}

#[test]
fn persistence_roundtrip() {
    let dir = TempDir::new();
    {
        let mut engine = KbEngine::new(dir.path().to_str().unwrap());
        engine.create_collection("c", 2, Metric::Cosine);
        engine.add("c", demo_record("1", 2, vec![1.0, 0.0]));
    }
    {
        let engine = KbEngine::new(dir.path().to_str().unwrap());
        assert!(engine.has("c"));
        let col = engine.collections.get("c").unwrap();
        assert_eq!(col.records.len(), 1);
        assert_eq!(col.records[0].id, "1");
    }
}
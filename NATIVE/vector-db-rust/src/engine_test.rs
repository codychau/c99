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
    assert!(engine.add("c", "a.txt", demo_record("1", 4, vec![1.0, 0.0, 0.0, 0.0])));
    assert_eq!(engine.count("c"), 1);
    // 相同 id 覆盖
    assert!(engine.add("c", "a.txt", demo_record("1", 4, vec![0.0, 1.0, 0.0, 0.0])));
    assert_eq!(engine.count("c"), 1);
    // 维度不匹配失败
    assert!(!engine.add("c", "a.txt", demo_record("2", 3, vec![1.0, 1.0, 1.0])));
    assert!(engine.delete("c", "1"));
    assert_eq!(engine.count("c"), 0);
    assert!(!engine.delete("c", "nope"));
}

#[test]
fn search_cosine_ranks() {
    let (_dir, mut engine) = temp_engine();
    engine.create_collection("c", 4, Metric::Cosine);
    engine.add("c", "a.txt", demo_record("a", 4, vec![1.0, 0.0, 0.0, 0.0]));
    engine.add("c", "b.txt", demo_record("b", 4, vec![0.0, 1.0, 0.0, 0.0]));
    engine.add("c", "c.txt", demo_record("c0", 4, vec![0.9, 0.1, 0.0, 0.0]));

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
    engine.add("c", "a.txt", demo_record("near", 3, vec![2.0, 0.0, 0.0]));
    engine.add("c", "b.txt", demo_record("far", 3, vec![100.0, 0.0, 0.0]));
    // 欧氏距离：query=[0,0,0] 时 near 更近 -> 得分更高
    assert!(engine.set_metric("c", Metric::Euclidean));
    let col = engine.get_or_none("c").unwrap();
    let hits = col.search(&[0.0, 0.0, 0.0], 2, Metric::Euclidean);
    assert_eq!(hits[0].id, "near");
}

#[test]
fn empty_source_uses_legacy_key() {
    let (_dir, mut engine) = temp_engine();
    engine.create_collection("c", 2, Metric::Cosine);
    assert!(engine.add("c", "", demo_record("1", 2, vec![1.0, 0.0])));
    assert_eq!(engine.list_source_files("c"), vec!["__legacy__".to_string()]);
}

#[test]
fn sharded_persistence_roundtrip() {
    let dir = TempDir::new();
    {
        let mut engine = KbEngine::new(dir.path().to_str().unwrap());
        engine.create_collection("工作库", 2, Metric::Cosine);
        engine.add("工作库", "报告.pdf", demo_record("1", 2, vec![1.0, 0.0]));
        engine.add("工作库", "笔记.md", demo_record("2", 2, vec![0.0, 1.0]));
        // 每个源文件对应一个分片文件，文件名 = 集合名.短base64(源文件名).json
        let files: Vec<String> = std::fs::read_dir(dir.path())
            .unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        assert_eq!(files.len(), 2, "应生成两个分片文件，实际: {:?}", files);
        assert!(files.iter().any(|f| f.starts_with("工作库.")), "期望包含 工作库.*.json，实际: {:?}", files);
    }
    {
        let engine = KbEngine::new(dir.path().to_str().unwrap());
        assert!(engine.has("工作库"));
        let col = engine.collections.get("工作库").unwrap();
        assert_eq!(col.all_records().len(), 2);
        let sources = engine.list_source_files("工作库");
        assert_eq!(sources, vec!["报告.pdf".to_string(), "笔记.md".to_string()]);
    }
}

#[test]
fn add_only_rewrites_affected_shard() {
    let dir = TempDir::new();
    {
        let mut engine = KbEngine::new(dir.path().to_str().unwrap());
        engine.create_collection("c", 2, Metric::Cosine);
        engine.add("c", "a.txt", demo_record("1", 2, vec![1.0, 0.0]));
        engine.add("c", "a.txt", demo_record("2", 2, vec![1.0, 0.0]));
        // 新加 b.txt 只应新增 b.txt 分片，不影响 a.txt 分片内容
        engine.add("c", "b.txt", demo_record("3", 2, vec![0.0, 1.0]));
        assert_eq!(engine.count("c"), 3);
    }
    {
        let engine = KbEngine::new(dir.path().to_str().unwrap());
        let col = engine.collections.get("c").unwrap();
        assert_eq!(col.all_records().len(), 3);
        assert_eq!(engine.list_source_files("c"), vec!["a.txt".to_string(), "b.txt".to_string()]);
    }
}

#[test]
fn delete_only_affects_affected_shard() {
    let dir = TempDir::new();
    {
        let mut engine = KbEngine::new(dir.path().to_str().unwrap());
        engine.create_collection("c", 2, Metric::Cosine);
        engine.add("c", "a.txt", demo_record("1", 2, vec![1.0, 0.0]));
        engine.add("c", "a.txt", demo_record("2", 2, vec![1.0, 0.0]));
        engine.add("c", "b.txt", demo_record("3", 2, vec![0.0, 1.0]));
        assert!(engine.delete("c", "1"));
        assert_eq!(engine.count("c"), 2);
    }
    {
        let engine = KbEngine::new(dir.path().to_str().unwrap());
        let col = engine.collections.get("c").unwrap();
        assert_eq!(col.all_records().len(), 2);
        let ids: Vec<String> = col.all_records().into_iter().map(|r| r.id.clone()).collect();
        assert!(!ids.contains(&"1".to_string()));
    }
}

#[test]
fn chinese_collection_survives_roundtrip() {
    let dir = TempDir::new();
    {
        let mut engine = KbEngine::new(dir.path().to_str().unwrap());
        assert!(engine.create_collection("工作库", 2, Metric::Cosine));
        assert!(engine.has("工作库"));
        // 传中文源文件名
        assert!(engine.add("工作库", "需求文档.txt", demo_record("1", 2, vec![1.0, 0.0])));
        // 文件名应保留中文集合名，而非全部下划线
        let files: Vec<String> = std::fs::read_dir(dir.path())
            .unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        assert!(files.iter().any(|f| f.starts_with("工作库.")), "期望包含 工作库.*.json，实际: {:?}", files);
        assert!(!files.contains(&"工作库.json".to_string()));
    }
    {
        let engine = KbEngine::new(dir.path().to_str().unwrap());
        assert!(engine.has("工作库"), "重启后应能按中文名找到集合");
        assert_eq!(engine.count("工作库"), 1);
        assert_eq!(engine.list_source_files("工作库"), vec!["需求文档.txt".to_string()]);
        assert_eq!(engine.list_collections(), vec!["工作库".to_string()]);
    }
}

#[test]
fn legacy_underscore_file_loads_by_inner_name() {
    // 模拟历史版本：文件名为 `___.json`，但 JSON 内 name 字段为 "工作库"，
    // 修复后 load 时应以 JSON name 作为集合 key。
    let dir = TempDir::new();
    let inner = r#"{
      "name": "工作库",
      "dim": 2,
      "metric": "Cosine",
      "records": [
        {"id":"1","content":"content-1","metadata":{"source":"test"},"embedding":[1.0,0.0]}
      ]
    }"#;
    std::fs::write(dir.path().join("___.json"), inner).unwrap();

    let engine = KbEngine::new(dir.path().to_str().unwrap());
    assert!(engine.has("工作库"));
    assert_eq!(engine.count("工作库"), 1);
    assert_eq!(engine.list_collections(), vec!["工作库".to_string()]);
}
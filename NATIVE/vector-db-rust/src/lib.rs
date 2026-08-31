#![allow(non_snake_case)] // DLL 导出函数采用与 C# 端一致的 KB_* 命名

mod engine;

#[cfg(test)]
mod engine_test;

use engine::{KbEngine, Metric, Record};
use std::ffi::{c_char, c_int, c_void, CStr};
use std::panic::{catch_unwind, AssertUnwindSafe};

/// C ABI 导出层：与 C# 端 P/Invoke 对齐的 KB_* 接口。
///
/// C# 对应签名（Services/BuiltInVectorDbService.cs）：
///   IntPtr KB_Open(string dataDir)
///   void   KB_Close(IntPtr ctx)
///   int    KB_CreateCollection(IntPtr ctx, string name, int dim)
///   int    KB_DropCollection(IntPtr ctx, string name)
///   int    KB_ListCollections(IntPtr ctx, byte[] outBuf, int bufLen)
///   int    KB_Add(IntPtr ctx, string name, string id, float[] vec, int dim,
///                 string content, string metadataJson)
///   int    KB_Delete(IntPtr ctx, string name, string id)
///   int    KB_Search(IntPtr ctx, string name, float[] queryVec, int dim,
///                    int topK, byte[] outBuf, int bufLen)
///   long   KB_Count(IntPtr ctx, string name)
///   int    KB_ReadAll(IntPtr ctx, string name, byte[] outBuf, int bufLen)
///   int    KB_SetMetric(IntPtr ctx, string name, string metric)  // 额外的距离算法切换
///
/// 约定：所有返回 int 的函数，0 表示成功，非 0 表示失败。
/// KB_ListCollections / KB_Search / KB_ReadAll 将 JSON 写入 outBuf（UTF-8），返回值：
///   0  成功
///   >0 表示缓冲区不足，返回所需长度（调用方据此扩容重试）

// ==================== 帮助函数 ====================

unsafe fn to_string(ptr: *const c_char) -> String {
    if ptr.is_null() {
        return String::new();
    }
    CStr::from_ptr(ptr).to_string_lossy().into_owned()
}

/// 将 &str 以 UTF-8 字节形式写入缓冲区（带 NUL 结尾）。
/// 返回 0 成功，正数表示所需缓冲区长度。
unsafe fn write_json_into(buf: *mut c_char, buf_len: c_int, json: &str) -> c_int {
    if buf.is_null() || buf_len <= 0 {
        return json.len() as c_int + 1;
    }
    let bytes = json.as_bytes();
    let required = bytes.len() + 1; // +NUL
    if required > buf_len as usize {
        return required as c_int;
    }
    let dst = std::slice::from_raw_parts_mut(buf as *mut u8, bytes.len());
    dst.copy_from_slice(bytes);
    *buf.add(bytes.len()) = 0;
    0
}

/// 从 FFI 传入的 Vec<float> 指针恢复长度自校验。
unsafe fn vec_from_ptr(ptr: *const f32, dim: c_int) -> Vec<f32> {
    if ptr.is_null() || dim <= 0 {
        return Vec::new();
    }
    std::slice::from_raw_parts(ptr, dim as usize).to_vec()
}

// ==================== 生命周期 ====================

/// 打开（创建）引擎实例。返回指针，失败返回 NULL。
#[no_mangle]
pub extern "C" fn KB_Open(data_dir: *const c_char) -> *mut c_void {
    catch_unwind(AssertUnwindSafe(|| {
        let dir = unsafe { to_string(data_dir) };
        let engine = KbEngine::new(if dir.is_empty() {
            env!("CARGO_MANIFEST_DIR")
        } else {
            dir.as_str()
        });
        Box::into_raw(Box::new(engine)) as *mut c_void
    }))
    .unwrap_or(std::ptr::null_mut())
}

/// 关闭引擎实例并释放内存。
#[no_mangle]
pub extern "C" fn KB_Close(ctx: *mut c_void) {
    if ctx.is_null() {
        return;
    }
    unsafe {
        drop(Box::from_raw(ctx as *mut KbEngine));
    }
}

// ==================== 集合管理 ====================

/// 创建集合。返回 0 成功，非 0 失败（重名/维度非法）。
#[no_mangle]
pub extern "C" fn KB_CreateCollection(ctx: *mut c_void, name: *const c_char, dim: c_int) -> c_int {
    catch_unwind(AssertUnwindSafe(|| {
        let engine = unsafe { &mut *(ctx as *mut KbEngine) };
        let name = unsafe { to_string(name) };
        if name.is_empty() || dim <= 0 {
            return 1;
        }
        if engine.create_collection(&name, dim as usize, Metric::Cosine) {
            0
        } else {
            1
        }
    }))
    .unwrap_or(1)
}

/// 删除集合。返回 0 成功。
#[no_mangle]
pub extern "C" fn KB_DropCollection(ctx: *mut c_void, name: *const c_char) -> c_int {
    catch_unwind(AssertUnwindSafe(|| {
        let engine = unsafe { &mut *(ctx as *mut KbEngine) };
        let name = unsafe { to_string(name) };
        if engine.drop_collection(&name) {
            0
        } else {
            1
        }
    }))
    .unwrap_or(1)
}

/// 列出所有集合，JSON: ["a","b"]
#[no_mangle]
pub extern "C" fn KB_ListCollections(
    ctx: *mut c_void,
    out_buf: *mut c_char,
    buf_len: c_int,
) -> c_int {
    catch_unwind(AssertUnwindSafe(|| {
        let engine = unsafe { &*(ctx as *mut KbEngine) };
        let names = engine.list_collections();
        let json = serde_json::to_string(&names).unwrap_or_else(|_| "[]".to_string());
        unsafe { write_json_into(out_buf, buf_len, &json) }
    }))
    .unwrap_or(1)
}

/// 切换集合的距离算法。metric: "cosine" | "euclidean" | "dot"
#[no_mangle]
pub extern "C" fn KB_SetMetric(
    ctx: *mut c_void,
    name: *const c_char,
    metric: *const c_char,
) -> c_int {
    catch_unwind(AssertUnwindSafe(|| {
        let engine = unsafe { &mut *(ctx as *mut KbEngine) };
        let name = unsafe { to_string(name) };
        let metric_str = unsafe { to_string(metric) };
        match Metric::parse(&metric_str) {
            Some(m) => {
                if engine.set_metric(&name, m) {
                    0
                } else {
                    1
                }
            }
            None => 2, // 未知算法名
        }
    }))
    .unwrap_or(1)
}

// ==================== 数据操作 ====================

/// 插入一条记录（同 id 覆盖更新）。返回 0 成功。
#[no_mangle]
pub extern "C" fn KB_Add(
    ctx: *mut c_void,
    name: *const c_char,
    id: *const c_char,
    vec: *const f32,
    dim: c_int,
    content: *const c_char,
    metadata_json: *const c_char,
) -> c_int {
    catch_unwind(AssertUnwindSafe(|| {
        let engine = unsafe { &mut *(ctx as *mut KbEngine) };
        let name = unsafe { to_string(name) };
        let id = unsafe { to_string(id) };
        if !engine.has(&name) || id.is_empty() {
            return 1;
        }
        let embedding = unsafe { vec_from_ptr(vec, dim) };
        let content = unsafe { to_string(content) };
        let metadata: serde_json::Value =
            serde_json::from_str(&unsafe { to_string(metadata_json) })
                .unwrap_or(serde_json::Value::Object(Default::default()));
        let record = Record {
            id,
            content,
            metadata,
            embedding,
            created_at: now_iso(),
            score: None,
        };
        if engine.add(&name, record) {
            0
        } else {
            1
        }
    }))
    .unwrap_or(1)
}

/// 删除记录。返回 0 成功。
#[no_mangle]
pub extern "C" fn KB_Delete(ctx: *mut c_void, name: *const c_char, id: *const c_char) -> c_int {
    catch_unwind(AssertUnwindSafe(|| {
        let engine = unsafe { &mut *(ctx as *mut KbEngine) };
        let name = unsafe { to_string(name) };
        let id = unsafe { to_string(id) };
        if engine.delete(&name, &id) {
            0
        } else {
            1
        }
    }))
    .unwrap_or(1)
}

/// 向量召回，JSON: [{"id":"..","content":"..","metadata":{},"score":0.9}]
#[no_mangle]
pub extern "C" fn KB_Search(
    ctx: *mut c_void,
    name: *const c_char,
    query_vec: *const f32,
    dim: c_int,
    top_k: c_int,
    out_buf: *mut c_char,
    buf_len: c_int,
) -> c_int {
    catch_unwind(AssertUnwindSafe(|| {
        let engine = unsafe { &mut *(ctx as *mut KbEngine) };
        let name = unsafe { to_string(name) };
        let query = unsafe { vec_from_ptr(query_vec, dim) };
        let col = match engine.get_or_none(&name) {
            Some(c) => c,
            None => return 1,
        };
        let hits = col.search(&query, top_k as usize, col.metric);
        let json = serde_json::to_string(&hits).unwrap_or_else(|_| "[]".to_string());
        unsafe { write_json_into(out_buf, buf_len, &json) }
    }))
    .unwrap_or(1)
}

/// 集合内记录数。失败返回 -1。
/// 返回 i64（C 的 long long），与 C# 端 P/Invoke `long`（8 字节）对齐。
#[no_mangle]
pub extern "C" fn KB_Count(ctx: *mut c_void, name: *const c_char) -> i64 {
    catch_unwind(AssertUnwindSafe(|| {
        let engine = unsafe { &*(ctx as *mut KbEngine) };
        let name = unsafe { to_string(name) };
        engine.count(&name)
    }))
    .unwrap_or(-1)
}

/// 读取集合内全部记录，JSON 与 KB_Search 相同（score=0）。
#[no_mangle]
pub extern "C" fn KB_ReadAll(
    ctx: *mut c_void,
    name: *const c_char,
    out_buf: *mut c_char,
    buf_len: c_int,
) -> c_int {
    catch_unwind(AssertUnwindSafe(|| {
        let engine = unsafe { &*(ctx as *mut KbEngine) };
        let name = unsafe { to_string(name) };
        let col = match engine.collections.get(&name) {
            Some(c) => c,
            None => return 1,
        };
        let hits: Vec<engine::SearchHit> = col
            .records
            .iter()
            .map(|r| engine::SearchHit {
                id: r.id.clone(),
                content: r.content.clone(),
                metadata: r.metadata.clone(),
                score: 0.0,
            })
            .collect();
        let json = serde_json::to_string(&hits).unwrap_or_else(|_| "[]".to_string());
        unsafe { write_json_into(out_buf, buf_len, &json) }
    }))
    .unwrap_or(1)
}

fn now_iso() -> String {
    // 简单时间戳（无第三方时间库依赖）
    use std::time::{SystemTime, UNIX_EPOCH};
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    secs.to_string()
}
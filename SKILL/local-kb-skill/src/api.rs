use std::fmt;
use std::time::Duration;

use serde::{Deserialize, Serialize};

/// 客户端层错误。
#[derive(Debug, Clone)]
pub enum ApiError {
    /// 网络 / 请求层错误（连接失败、超时等）。
    Request(String),
    /// 服务端返回非 2xx，附带响应体便于排查。
    Http { status: u16, body: String },
    /// 响应 JSON 解析失败。
    Json(String),
}

impl fmt::Display for ApiError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ApiError::Request(m) => write!(f, "网络/请求错误: {m}"),
            ApiError::Http { status, body } => {
                let body = if body.chars().count() > 400 {
                    let short: String = body.chars().take(400).collect();
                    format!("{short}...(截断)")
                } else {
                    body.clone()
                };
                write!(f, "服务端返回 HTTP {status}: {body}")
            }
            ApiError::Json(m) => write!(f, "响应解析失败: {m}"),
        }
    }
}

impl std::error::Error for ApiError {}

/// POST /api/kb/query 的请求体。
#[derive(Debug, Serialize)]
pub struct KbQuery {
    pub question: String,
    #[serde(rename = "top_k")]
    pub top_k: u32,
    pub collection: String,
}

/// POST /api/kb/query 的成功响应体。
#[derive(Debug, Deserialize)]
pub struct KbQueryResult {
    pub answer: String,
}

/// 访问 9527 HTTP 服务的客户端。
pub struct Api {
    base: String,
    agent: ureq::Agent,
}

impl Api {
    pub fn new(base: impl Into<String>, timeout_secs: u64) -> Self {
        let agent = ureq::AgentBuilder::new()
            .timeout(Duration::from_secs(timeout_secs))
            .timeout_connect(Duration::from_secs(5))
            .build();
        Self {
            base: base.into().trim_end_matches('/').to_string(),
            agent,
        }
    }

    fn get_text(&self, path: &str) -> Result<String, ApiError> {
        let url = format!("{}{}", self.base, path);
        match self.agent.get(&url).call() {
            Ok(resp) => resp.into_string().map_err(|e| ApiError::Request(e.to_string())),
            Err(ureq::Error::Status(code, resp)) => {
                let body = resp.into_string().unwrap_or_default();
                Err(ApiError::Http { status: code, body })
            }
            Err(e) => Err(ApiError::Request(e.to_string())),
        }
    }

    fn post_json<T: serde::de::DeserializeOwned>(&self, path: &str, json: &str) -> Result<T, ApiError> {
        let url = format!("{}{}", self.base, path);
        match self
            .agent
            .post(&url)
            .set("Content-Type", "application/json")
            .send_string(json)
        {
            Ok(resp) => {
                let text = resp.into_string().map_err(|e| ApiError::Request(e.to_string()))?;
                serde_json::from_str(&text).map_err(|e| ApiError::Json(format!("{e}; body={text}")))
            }
            Err(ureq::Error::Status(code, resp)) => {
                let body = resp.into_string().unwrap_or_default();
                Err(ApiError::Http { status: code, body })
            }
            Err(e) => Err(ApiError::Request(e.to_string())),
        }
    }

    /// GET /api/health —— 返回原始文本（JSON），失败返回错误。
    pub fn health(&self) -> Result<String, ApiError> {
        self.get_text("/api/health")
    }

    /// POST /api/kb/query —— 向本地知识库提问，返回检索+AI 生成的回答。
    pub fn kb_query(&self, question: &str, top_k: u32, collection: &str) -> Result<KbQueryResult, ApiError> {
        let req = KbQuery {
            question: question.trim().to_string(),
            top_k,
            collection: collection.trim().to_string(),
        };
        let json = serde_json::to_string(&req).map_err(|e| ApiError::Json(e.to_string()))?;
        self.post_json("/api/kb/query", &json)
    }
}

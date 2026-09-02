use std::env;
use std::process::ExitCode;

mod api;

const DEFAULT_BASE_URL: &str = "http://127.0.0.1:9527";
const DEFAULT_TIMEOUT_SECS: u64 = 120;

fn print_usage() {
    eprintln!(
        r#"local-kb-skill - 本地知识库检索客户端（端口 9527 的 /api/kb/query）

用法:
  local-kb-skill [选项] <问题...>
  local-kb-skill --health [选项]

位置参数:
  <问题...>                提问内容，多个词将用空格拼接（建议整体用引号包裹）

选项:
  -u, --url <地址>         服务地址，默认 http://127.0.0.1:9527
  -c, --collection <名称>  知识库集合名，默认空（由服务端自动选择）
  -k, --top-k <数字>       召回条数，默认 8，最小 1
  -t, --timeout <秒>       单次请求超时（秒），默认 120（检索+AI 生成可能较慢）
      --health             只做健康检查：GET /api/health
  -h, --help               显示帮助

退出码:
  0  成功
  1  用法错误
  2  服务端返回错误（HTTP 4xx/5xx，或响应解析失败）
  3  网络/连接失败等运行时错误
"#
    );
}

fn err_usage(msg: &str) -> ExitCode {
    eprintln!("错误: {msg}\n");
    print_usage();
    ExitCode::FAILURE
}

fn main() -> ExitCode {
    let argv: Vec<String> = env::args().collect();

    let mut base = String::from(DEFAULT_BASE_URL);
    let mut collection = String::new();
    let mut top_k: u32 = 8;
    let mut timeout_secs = DEFAULT_TIMEOUT_SECS;
    let mut health = false;
    let mut question_parts: Vec<String> = Vec::new();

    let mut i = 1;
    while i < argv.len() {
        let arg = argv[i].as_str();
        match arg {
            "-h" | "--help" => {
                print_usage();
                return ExitCode::SUCCESS;
            }
            "-u" | "--url" => {
                i += 1;
                match argv.get(i) {
                    Some(v) => base = v.clone(),
                    None => return err_usage("--url 缺少值"),
                }
            }
            "-c" | "--collection" => {
                i += 1;
                match argv.get(i) {
                    Some(v) => collection = v.clone(),
                    None => return err_usage("--collection 缺少值"),
                }
            }
            "-k" | "--top-k" => {
                i += 1;
                match argv.get(i).and_then(|s| s.parse::<u32>().ok()) {
                    Some(v) if v >= 1 => top_k = v,
                    _ => return err_usage("--top-k 需要 >=1 的整数"),
                }
            }
            "-t" | "--timeout" => {
                i += 1;
                match argv.get(i).and_then(|s| s.parse::<u64>().ok()) {
                    Some(v) if v >= 1 => timeout_secs = v,
                    _ => return err_usage("--timeout 需要 >=1 的秒数"),
                }
            }
            "--health" => health = true,
            _ => {
                if arg.starts_with('-') && arg != "-" {
                    return err_usage(&format!("未知参数: {arg}"));
                }
                question_parts.push(argv[i].clone());
            }
        }
        i += 1;
    }

    let api = api::Api::new(base, timeout_secs);

    if health {
        return match api.health() {
            Ok(text) => {
                println!("{}", text);
                ExitCode::SUCCESS
            }
            Err(e) => {
                eprintln!("[local-kb-skill] 健康检查失败: {e}");
                ExitCode::from(3)
            }
        };
    }

    if question_parts.is_empty() {
        eprintln!("错误: 缺少问题内容\n");
        print_usage();
        return ExitCode::FAILURE;
    }

    let question = question_parts.join(" ");
    if question.trim().is_empty() {
        return err_usage("问题内容不能为空");
    }

    match api.kb_query(&question, top_k, &collection) {
        Ok(result) => {
            println!("{}", result.answer);
            ExitCode::SUCCESS
        }
        Err(api::ApiError::Http { status, body }) => {
            eprintln!("[local-kb-skill] HTTP {status}: {body}");
            ExitCode::from(2)
        }
        Err(e) => {
            eprintln!("[local-kb-skill] 查询失败: {e}");
            ExitCode::from(3)
        }
    }
}

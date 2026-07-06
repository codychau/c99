# 🚀 科迪 C99 — 一站式 AI 推理引擎启动器

> **C99**（中文名：**科迪C99**）是一款基于 WinUI 3 的 Windows 桌面应用，旨在统一管理和启动各类大语言模型（LLM）推理引擎。  
> 告别繁琐的命令行配置，点点鼠标就能跑起 llama.cpp、vLLM、LM Studio、Ollama！

---

## ✨ 功能亮点

| 功能 | 说明 |
|------|------|
| 🧠 **多引擎支持** | 内置 llama.cpp、vLLM、LM Studio、Ollama 四大推理引擎配置 |
| 📂 **统一模型目录** | 指定一个模型根目录，自动扫描子目录与 GGUF 模型文件 |
| ⚙️ **可视化参数调节** | GPU 层数、上下文长度、采样参数等全部滑块 + 数字输入 |
| 🎯 **预设方案** | 「推荐」「默认」「暴力」三档一键切换，省时省心 |
| 🖼️ **多模态检测** | 自动识别 mmproj 视觉投影文件，智能提示启用多模态 |
| 🔍 **环境检测** | 一键检测 llama.cpp 编译信息、GPU 设备列表、CUDA 环境 |
| 🛒 **AI 杂货铺** | 9 宫格快捷入口，集成搜索与分类浏览功能 |
| 💾 **参数自动保存** | 防抖自动保存机制，配置永不丢失 |
| 🏭 **AI 梦工厂** | 智能邮箱助手：HTTP API 接收邮件 → AI 生成工作报告 → 多格式输出 |

---

## 🧩 支持的引擎

| 引擎 | 特性 |
|------|------|
| **llama.cpp** | C++ 高性能推理，支持 GPU 加速、Flash Attention、推测解码、分布式拆分 |
| **vLLM** | 高吞吐量推理引擎，支持 PagedAttention、连续批处理 |
| **LM Studio** | 图形化界面引擎，开箱即用，适合新手 |
| **Ollama** | 极简部署，一行命令下载并运行模型 |

---

## 🖥️ 界面预览

- **导航栏**：可收起/展开的侧边导航，包含首页、AI梦工厂、AI杂货铺、AI底座启动集合、设置、关于
- **AI梦工厂**：HTTP 服务面板 + AI 模型配置 + 逻辑管道可视化编辑器 + 输出动作设置 + 报告日志
- **AI底座启动集合**：核心功能区，统一模型目录选择 + 引擎参数配置 + 运行日志输出
- **AI杂货铺**：9 宫格快捷入口与搜索功能

---

## 🏭 AI 梦工厂

**AI 梦工厂**是一个内置的智能邮箱助手，通过 HTTP API 接收邮件报告，调用本地 AI 模型生成工作报告，并支持丰富的输出动作。

### HTTP API 服务

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/report` | `POST` | 接收邮件 JSON 报告，触发完整处理流水线 |
| `/api/health` | `GET` | 健康检查，返回 `{"status":"ok","model":"..."}` |
| `/api/config` | `GET/POST` | 读取/更新梦工厂配置 |
| `/report/latest` | `GET` | HTML 网页报告查看器（带历史侧边栏） |

服务默认监听 `http://localhost:9527/`，支持 CORS 跨域，可配置开机自启。

### 处理流水线

```
接收邮件报告 → 前置逻辑 → AI 生成报告 → 后置逻辑 → 执行输出动作
```

- **前置/后置逻辑管道**：可在 AI 调用前后插入自定义处理流程
- **上下文变量模板**：所有文本参数支持 `{variable_name}` 语法，在管道中跨阶段传递

### 10 种逻辑动作

| 动作 | 说明 |
|------|------|
| `set_variable` | 创建或覆盖上下文变量 |
| `replace_text` | 对上下文变量执行字符串替换 |
| `regex_replace` | 对上下文变量执行正则替换 |
| `prepend_text` | 在变量开头插入文本 |
| `append_text` | 在变量末尾追加文本 |
| `http_request` | 发起 HTTP 请求，可选存储响应体到变量 |
| `condition_skip` | 条件满足时**跳过剩余动作**（支持 8 种判断符：contains、equals、is_empty 等） |
| `log` | 输出日志消息 |
| `popup_notify` | 弹出桌面通知（可配置自动消失时间） |
| `popup_confirm` | 弹出确认对话框，取消时**中止管道** |

### AI 模型配置

| 来源 | 说明 |
|------|------|
| **内置预设** | llama.cpp (`:8080`) / Ollama (`:11434`) / vLLM (`:8000`)，可自选 .gguf 模型文件 |
| **自定义外部模型** | 任意 OpenAI 兼容 API，支持自定义地址、API Key、模型名称 |

### 6 种输出动作

| 输出 | 说明 |
|------|------|
| `web_report` | 生成 HTML 网页报告 + Windows Toast 通知 |
| `markdown` | 保存为 `.md` 文件 |
| `word` | 使用 OpenXML SDK 生成 `.docx` 文档 |
| `excel` | 使用 ClosedXML 生成 `.xlsx` 表格 |
| `siyuan` | 上传到思源笔记（通过 REST API） |
| `none` | 仅在应用内显示通知 |

---

## 🛠️ 开发环境

- **框架**：WinUI 3（Windows App SDK 2.0.1）
- **运行时**：.NET 8.0（Windows 10.0.19041+）
- **语言**：C# / XAML
- **IDE**：Visual Studio 2022（17.14+）

### 快速开始

```bash
# 克隆仓库
git clone https://github.com/codychau/c99.git

# 使用 Visual Studio 打开解决方案
cd c99
start C99.sln
```

然后在 Visual Studio 中选择 `x64 | Debug` 配置，按 `F5` 运行即可。

---

## 📦 构建发布

```bash
# 生成 MSIX 安装包
dotnet publish -c Release -p:Platform=x64
```

生成的安装包位于 `AppPackages` 目录下。

---

## 📄 许可证

本项目基于 [MIT License](./LICENSE) 🆓，欢迎自由使用、修改和分发。

---

## 👤 关于作者

**科迪C99** — 由 **Cody** 精心打造 ❤️  
让 AI 推理触手可及，释放每一张显卡的算力！

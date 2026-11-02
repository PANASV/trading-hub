[← Volver al índice](README.md)

# 在策略面板中使用 Codex（ChatGPT）

「回测 → 代码」右侧的 AI 面板可以在 **Claude Code** 和 **Codex CLI**（OpenAI）之间切换。两者都使用你本机安装的命令行工具和你自己的订阅，Trading Hub 不保存任何 API key。

## 安装与登录 Codex

```bash
npm i -g @openai/codex        # 或 brew install codex
codex login                    # 选 “Sign in with ChatGPT”，使用你的 ChatGPT 订阅；也可以用 API key
```

Trading Hub 会在以下位置查找 `codex`：`/opt/homebrew/bin`、`/usr/local/bin`、`~/.local/bin`、`~/.npm-global/bin`、`~/.bun/bin`、`~/.volta/bin`、nvm 的各个 Node 版本、`PATH`，以及 Codex 桌面版自带的副本。也可以用环境变量 `TRADINGHUB_CODEX_CLI` 指定路径。

## 切换助手和模型

点击面板顶部的 **调节图标**（在「实验室」旁边）：

- **助手**：Claude Code 或 Codex CLI。切换后会开始新的对话（一个助手的会话不能交给另一个继续）。
- **模型**：Claude 可以选 Opus 5.5（默认）、Sonnet 5.5、Haiku 5.5；Codex 默认使用你的 Codex 设置里的模型，也可以从列表中选，或在「其他模型」里填写准确的模型 id（OpenAI 的模型名称变化频繁）。
- **推理强度**：默认 / 低 / 中 / 高 / 很高。
- **登录**：打开终端运行 `claude /login` 或 `codex login`。

这些设置保存在 `settings.json`（`AiEngine`、`ClaudeModel`、`ClaudeEffort`、`CodexModel`、`CodexEffort`）。历史对话会记住当时用的是哪个助手，恢复对话时会自动切回去。

## Codex 的运行方式

Trading Hub 运行 `codex exec --json`：

- 工作目录是策略文件夹，沙箱为 `workspace-write`：只能写策略文件夹和指标文件夹，**不联网**，也不会弹出审批。
- 打开「实验室」时，Trading Hub 以 MCP 服务器（`TradingHub --mcp`）的形式提供同样的工具：编译、回测、优化、前进分析、蒙特卡洛、图表截图、组合分析和报告。单个工具调用最长 1 小时。
- 实盘仓位只会生成「提议」，需要你在「实盘 → 提议」中逐条批准。
- 继续对话时使用 `codex exec resume <会话 id>`。

实盘风控代理（「实盘 → 组合」）目前仍然只使用 Claude Code。

## TradingView（官方 MCP）

在助手菜单里勾选 **使用 TradingView（官方 MCP）** 后，Claude 和 Codex 可以使用你的 TradingView 账户（需要 Essential 及以上套餐）查询：

- K 线（1 分钟到月线，每次最多 5,000 根）
- 报价、筛选器、技术指标
- 新闻、经济日历、基本面数据

研究和对照用 TradingView 的数据，回测仍然用 Trading Hub 的数据。TradingView 每个用户每分钟大约 100 次调用。

**第一次使用要先连接。** 在菜单里点「连接 TradingView」，会打开终端：

- Codex：自动运行 `codex mcp add tradingview --url https://mcp.tradingview.com/mcp` 和 `codex mcp login tradingview`，在浏览器里用 TradingView 账户授权。
- Claude Code：自动运行 `claude mcp add --transport http --scope user tradingview https://mcp.tradingview.com/mcp`，然后打开 `claude`。在里面输入 `/mcp`，选 `tradingview` → Authenticate，授权后输入 `/exit` 退出。

助手被要求不能擅自新建、修改或删除 TradingView 的警报和自选列表。

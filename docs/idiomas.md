# 界面语言（Español / English / 简体中文）

在「Ajustes / Settings / 设置」→ General → **Language** 中选择：

- **Automático**：跟随系统语言。系统是西班牙语就用西语，是中文就用中文，其他情况一律用英文。
- **Español / English / 简体中文**：固定使用该语言。

切换后需要重新打开 Trading Hub 才生效，设置旁边有「立即重启」按钮。数字和日期格式会随语言变化：西语 `1.234,5`，英文和中文 `1,234.5`。

## 工作原理

- 源码中的界面文字仍然写西班牙语，并用 `L.T("…")` 包起来；带变量的文字用 `L.F("… {0} …", 值)`。
- 翻译字典放在 `src/TradingHub.Core/Localization/en.json` 和 `zh.json`，键就是西班牙语原文（类似 gettext）。
- 找不到翻译时，中文先回退到英文，英文再回退到西班牙语，所以界面上不会出现空白。
- 指标名称、参数名称、选项名称这类来自 `[Display]`、`[DisplayName]`、`[Description]`、`[Category]` 的文字，在显示时统一翻译。
- 中文模式下，图表里用 Skia 绘制的文字会自动换成带汉字的系统字体（Mac 上是 PingFang，Windows 上是微软雅黑）。
- **不翻译的内容**：发给 Claude 的指令（Claude 会用你的语言回答）、策略和指标代码模板、内部键名，以及保存在设置里的值。

## 自己修改或补充翻译

不需要重新编译。在数据文件夹（设置 → 数据 → 打开）里新建 `Languages/zh.json` 或 `Languages/en.json`，格式如下：

```json
{ "Comprar MKT": "买入（市价）" }
```

这里的条目会覆盖 App 自带的翻译。

## 同步原作者更新之后

原作者新增的界面文字是西班牙语，没有被包起来。用 `tools/TradingHub.I18n` 工具补上：

```bash
dotnet run --project tools/TradingHub.I18n -- wrap .      # 把新增的界面文字包成 L.T / L.F（已处理过的不会重复处理）
dotnet run --project tools/TradingHub.I18n -- compare .   # 列出被拿来做比较的界面文字（这些要人工检查）
dotnet run --project tools/TradingHub.I18n -- missing .   # 生成 i18n-missing-en.json / i18n-missing-zh.json
```

然后把 `i18n-missing-*.json` 里的条目翻译好，加进 `src/TradingHub.Core/Localization/` 下的字典，再重新编译。

`compare` 列出的是被拿去做比较（`==`、`case` 等）的文字。如果同一段文字在别处被翻译了，比较就会失效，需要把比较的一边也改成 `L.T(...)`，或者改用不受语言影响的键来比较。

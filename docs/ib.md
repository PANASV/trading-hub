[← Volver al índice](README.md)

# Interactive Brokers（TWS / IB Gateway）实时行情

Trading Hub 通过 TWS API 连接 TWS 或 IB Gateway，显示股指期货（MNQ、MES、M2K、MYM、NQ、ES、RTY、YM）的历史和实时行情。

## 设置

1. 在 IB Gateway 中：Configure → Settings → API → Settings，勾选 **Enable ActiveX and Socket Clients**，记下端口。
2. 在 Trading Hub「连接 → Interactive Brokers」中填写主机（127.0.0.1）、端口和 Client ID，然后保存。
   - 端口：4001 Gateway 实盘、4002 Gateway 模拟、7496 TWS 实盘、7497 TWS 模拟。
   - Client ID 不能和同一个 Gateway 上的其他程序（比如你的历史数据下载脚本）相同。
3. 账户需要订阅 **CME 实时行情**。DOM 显示 10 档需要 CME 深度行情；没有深度行情时只显示买一和卖一。

## 数据来源

- **历史**：先读磁盘上的数据（「数据 → 导入 IB Gateway 文件夹」导入的 1 分钟 K 线，以及之前实时录下的逐笔数据），缺口用 IB 的 1 分钟 K 线补齐。补齐时每天一个请求，并按换月规则选用当时的主力合约（例如 2026-11 用 MNQ 202612，12 月 14 日起换到 202703）。
- **实时**：逐笔成交（tick-by-tick AllLast）加逐笔买一卖一（BidAsk）。IB 不提供主动买卖方向，所以用成交价和当时的买一卖一来推断：成交在卖一价算主动买，成交在买一价算主动卖，两者之间按价格涨跌判断。
- 实时逐笔会录到 `Ticks/ib/<品种>/`，下次打开会直接读取。之后再导入同一天的 K 线，会用 K 线覆盖当天的数据。
- 收盘或周末打开时，图表停在最后一根 K 线。
- IB Gateway 没打开时，图表只显示磁盘上的数据，并在状态栏提示连接失败；Gateway 打开后会自动重连。

## 限制

- 暂时不能通过 IB 下单。
- IB 账户的逐笔订阅数量有上限：每个图表品种占用 2 个，超过时会提示。

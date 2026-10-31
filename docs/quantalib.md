# QuanTAlib 集成

本分支把 [QuanTAlib](https://github.com/mihakralj/QuanTAlib)（NuGet `QuanTAlib` 0.8.12，Apache 2.0）编译进 Trading Hub。

> 注意：NuGet 上的 `QuanTAlib 1.0.0` 是 2024 年的旧版（API 不同），当前版本是 0.8.x。升级时请看 NuGet 上的发布日期，不要只看版本号。

## 图表指标（指标库 → 分类「QuanTAlib」）

| 指标 | 说明 |
|---|---|
| QuanTAlib · Media móvil | 单条均线，13 种可选：SMA、EMA、WMA、Hull、ALMA、Jurik、KAMA、T3、ZLEMA、DEMA、TEMA、VIDYA、Ehlers SuperSmoother |
| QuanTAlib · Medias 9/60/200 | 三线组合，默认 EMA9（黄）/ EMA60（紫）/ WMA200（蓝），每条线的类型和周期都可以改 |
| QuanTAlib · Camarilla (sesión) | 上一交易日的 Camarilla：H1–H6、L1–L6（H2/L2、PP 默认隐藏）。默认按 CME 纽约时间 18:00 换日 |

- 实时行情下，最后一根 K 线的每个 tick 都用 QuanTAlib 的 `isNew = false` 修正，不会产生漂移。
- 预热期（`IsHot` 之前）默认不画线，可在设置里打开「Dibujar durante el calentamiento」。
- **Camarilla 没有使用 QuanTAlib 的 Pivotcam**：它的系数是 1.0833/12、1.1666/12、1.25/12、1.5/12（R4 = 0.125 × 振幅），与 TradingView 不一致。这里用的是 TradingView 标准公式：H1..H4 = C ± 振幅 × 1.1/12、1.1/6、1.1/4、1.1/2；H5 = H/L × C，H6 = H5 + 1.168 × (H5 − H4)，L5、L6 关于收盘价对称。
- **换日时间**：Trading Hub 的图表默认按 UTC 00:00 切日（为加密货币设计），对 MNQ 来说是错的。所以 Camarilla 有自己的「Día de trading」设置：默认「CME（纽约 18:00）」，与 TradingView 期货日线一致，夏令时自动处理；也可以选「图表的时段」或自定义时区和小时。

## 在策略里使用（回测 → 代码）

脚本里 `Q` 是 `QuanTAlib` 的别名（自动提供）。不能直接写 `using QuanTAlib;`，因为 `Ema`、`Sma` 会和 Trading Hub 自带的同名指标冲突。

```csharp
public class JmaCamarilla : Strategy
{
    private QtSeries _jma = null!;
    private QtSeries _kama = null!;
    private CamarillaSeries _cam = null!;

    protected override void OnInit()
    {
        _jma  = Qt(new Q.Jma(20, phase: 0));               // 任意单值 QuanTAlib 指标
        _kama = Qt(new Q.Kama(10), PriceSource.Typical);   // 指定价格源
        _cam  = Camarilla("America/New_York", 18);         // CME 交易日的 H1–H6 / L1–L6
    }

    protected override void OnBar(Bar bar)
    {
        if (!_jma.IsReady || !_cam.IsReady) return;
        if (bar.Close > _cam.H3 && CrossAbove(_jma, _kama)) EnterLong();
    }
}
```

- `Qt(...)` 返回的序列和内置指标用法一样：`[0]` 是最后一根已收盘 K 线，`[1]` 是前一根，`IsReady` 对应 QuanTAlib 的 `IsHot`，回测预热也会自动计入。
- `Camarilla()` 不带参数时按回测设置里的时区和时段起点切日；写 `Camarilla("America/New_York", 18)` 可以固定为 CME 交易日。
- `Qt(...)` 适用于单值输出的指标（均线、振荡器、滤波器等）。多输出指标（如布林带、通道）可以读 `.Indicator` 上的其它属性。
- 如果你的脚本自己也定义了名为 `Q` 的别名，会和自动别名冲突，请改用别的名字。

## 改动的文件

- `src/TradingHub.Indicators.Native/QuanTAlib/QtIndicators.cs`：图表指标与 Camarilla 公式
- `src/TradingHub.Strategies/QuanTAlibSeries.cs`、`Strategy.cs`：`Qt()`、`Camarilla()`
- `src/TradingHub.Scripting/ScriptCompiler.cs`：有 QuanTAlib 时为脚本加入 `Q` 别名
- 两个 `.csproj`：`QuanTAlib` 0.8.12
- `tests/TradingHub.Tests/QuanTAlibTests.cs`：7 个测试（与 QuanTAlib 逐根比对、实时 tick 不漂移、Camarilla 公式、UTC 与 CME 18:00 换日、策略与脚本别名）

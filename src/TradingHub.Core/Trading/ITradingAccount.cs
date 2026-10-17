using TradingHub.Market;

namespace TradingHub.Trading;

/// <summary>
/// Una cuenta donde se puede operar un instrumento: la simulada (paper) o una real (Binance...).
/// El DOM, el gráfico y el ticket operan contra esta interfaz sin saber cuál es.
/// </summary>
public interface ITradingAccount
{
    string Name { get; }

    /// <summary>True si las órdenes van al mercado con dinero real.</summary>
    bool IsReal { get; }

    Instrument Instrument { get; }
    Position Position { get; }
    IEnumerable<Order> ActiveOrders { get; }

    /// <summary>Último error del broker (orden rechazada, etc.).</summary>
    string? LastError { get; }

    event Action? Changed;

    Order Market(OrderDirections dir, decimal qty, string comment = "");
    Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "");
    Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "");
    void Cancel(Order order);
    void CancelAt(decimal price);
    void CancelAll();
    void Modify(Order order, decimal newPrice);
    void Flatten();
    void Reverse();

    /// <summary>Último precio operado del instrumento (0 si todavía no hay datos).</summary>
    decimal LastPrice => 0;

    /// <summary>Orden de salida (stop loss / objetivo): solo puede reducir la posición, nunca darla vuelta.</summary>
    Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "")
    {
        var o = type == OrderTypes.Stop ? Stop(dir, qty, price, comment) : Limit(dir, qty, price, comment);
        o.ReduceOnly = true;
        return o;
    }

    /// <summary>
    /// Cambia precio y cantidad de una orden. Devuelve la orden vigente: la misma, o una nueva si el
    /// broker no permite modificar (cancela y vuelve a enviar).
    /// </summary>
    Order Replace(Order order, decimal newPrice, decimal newQuantity)
    {
        if (newQuantity == order.Unfilled)
        {
            Modify(order, newPrice);
            return order;
        }

        Cancel(order);
        var n = order.Type == OrderTypes.Stop ? Stop(order.Direction, newQuantity, newPrice, order.Comment) : Limit(order.Direction, newQuantity, newPrice, order.Comment);
        n.ReduceOnly = order.ReduceOnly;
        return n;
    }
}

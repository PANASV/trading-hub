namespace TradingHub.Data;

/// <summary>Datos de un nivel de precio dentro de una vela (una fila del footprint).</summary>
public struct LevelData
{
    /// <summary>Volumen vendido agresivamente (pegó contra el bid).</summary>
    public decimal Bid;

    /// <summary>Volumen comprado agresivamente (pegó contra el ask).</summary>
    public decimal Ask;

    /// <summary>Volumen sin lado conocido.</summary>
    public decimal Between;

    /// <summary>Cantidad de operaciones.</summary>
    public int Trades;

    /// <summary>Hora (UTC, ticks) de la última operación en el nivel.</summary>
    public long LastTimeTicks;

    public readonly decimal Volume => Bid + Ask + Between;
    public readonly decimal Delta => Ask - Bid;
    public readonly bool IsEmpty => Trades == 0;
}

/// <summary>
/// Niveles de precio de una vela, indexados por fila (precio en ticks / paso de fila).
/// Es un arreglo que crece hacia ambos lados: acceso O(1) e iteración contigua (rápida para dibujar).
/// </summary>
public sealed class PriceLevels
{
    private LevelData[] _items = Array.Empty<LevelData>();
    private long _base;          // fila de _items[0]
    private long _minRow = long.MaxValue;
    private long _maxRow = long.MinValue;

    public bool IsEmpty => _minRow > _maxRow;
    public long MinRow => _minRow;
    public long MaxRow => _maxRow;

    /// <summary>Cantidad de filas entre la mínima y la máxima (incluye filas sin operaciones).</summary>
    public int Span => IsEmpty ? 0 : (int)(_maxRow - _minRow + 1);

    public ref LevelData GetOrAdd(long row)
    {
        if (_items.Length == 0)
        {
            _items = new LevelData[16];
            _base = row - 8;
        }
        else if (row < _base || row >= _base + _items.Length)
        {
            Grow(row);
        }

        if (row < _minRow) _minRow = row;
        if (row > _maxRow) _maxRow = row;
        return ref _items[row - _base];
    }

    private void Grow(long row)
    {
        long newMin = Math.Min(_base, row);
        long newMax = Math.Max(_base + _items.Length - 1, row);
        long needed = newMax - newMin + 1;
        long size = Math.Max(needed + needed / 2, _items.Length * 2L);
        if (size > int.MaxValue / 64) size = needed; // límite defensivo
        var arr = new LevelData[size];
        // margen hacia el lado que creció
        long pad = size - needed;
        long newBase = row < _base ? newMin - pad : newMin;
        Array.Copy(_items, 0, arr, _base - newBase, _items.Length);
        _items = arr;
        _base = newBase;
    }

    public bool TryGet(long row, out LevelData data)
    {
        long i = row - _base;
        if (i < 0 || i >= _items.Length || row < _minRow || row > _maxRow)
        {
            data = default;
            return false;
        }

        data = _items[i];
        return !data.IsEmpty;
    }

    public LevelData Get(long row)
    {
        TryGet(row, out var d);
        return d;
    }

    /// <summary>Recorre las filas con operaciones, de menor a mayor precio.</summary>
    public IEnumerable<(long Row, LevelData Data)> Enumerate()
    {
        if (IsEmpty) yield break;
        for (long r = _minRow; r <= _maxRow; r++)
        {
            var d = _items[r - _base];
            if (!d.IsEmpty) yield return (r, d);
        }
    }

    /// <summary>Acceso directo sin chequeo para el render (r entre MinRow y MaxRow).</summary>
    public ref readonly LevelData UnsafeAt(long row) => ref _items[row - _base];

    public void Clear()
    {
        _items = Array.Empty<LevelData>();
        _minRow = long.MaxValue;
        _maxRow = long.MinValue;
    }
}

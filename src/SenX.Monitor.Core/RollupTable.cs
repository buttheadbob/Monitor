using System;
using System.Runtime.InteropServices;

namespace SenX.Monitor.Core;

/// <summary>A total for one thing, such as a grid, a block type or an owner. Times are in 100 ns units.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RollupRow
{
    /// <summary>Table key, must stay first. Zero means empty.</summary>
    public long Key;
    public long MainUnits;
    public long WorkerUnits;
    public long Calls;

    /// <summary>The heaviest entity in this row, used to name it later. Zero when the row has no entity.</summary>
    public long Representative;
    public long RepresentativeUnits;
    public uint MaxUnits;
    private readonly uint _padding;

    public readonly long TotalUnits => MainUnits + WorkerUnits;
}

/// <summary>
/// Sums rows by key in native memory, so a measuring run makes no managed garbage however many
/// entities it saw. Written by one thread at a time, and read only once writing has finished.
/// </summary>
public sealed unsafe class RollupTable
{
    /// <summary>Stands in for a zero key, such as an entity with no owner, because zero marks an empty slot.</summary>
    public const long NoKey = -1L;

    private readonly OpenTable<RollupRow> _table;

    public RollupTable(int capacity)
    {
        _table = OpenTable<RollupRow>.Allocate(capacity);
    }

    /// <summary>Rows in the table.</summary>
    public int Count { get; private set; }

    /// <summary>The most rows it can hold.</summary>
    public int Capacity => _table.Capacity;

    /// <summary>Time that found no row. Shown as lost coverage, so a full table can't quietly hide anything.</summary>
    public long LostUnits { get; private set; }

    public void Add(long key, long mainUnits, long workerUnits, long calls, uint maxUnits, long representative)
    {
        RollupRow* row = _table.Claim(key == 0L ? NoKey : key, out bool created);
        if (row == null)
        {
            LostUnits += mainUnits + workerUnits;
            return;
        }

        if (created)
            Count++;

        row->MainUnits += mainUnits;
        row->WorkerUnits += workerUnits;
        row->Calls += calls;

        long units = mainUnits + workerUnits;
        if (units > row->RepresentativeUnits || row->Representative == 0L)
        {
            row->RepresentativeUnits = units;
            row->Representative = representative;
        }

        if (maxUnits > row->MaxUnits)
            row->MaxUnits = maxUnits;
    }

    /// <summary>The largest rows by total time, or by <paramref name="score"/> if given, largest first.</summary>
    public RollupRow[] Top(int count, Func<RollupRow, double>? score = null)
    {
        var top = new RollupRow[Math.Min(count, Count)];
        if (top.Length == 0)
            return top;

        var scores = new double[top.Length];
        int filled = 0;
        int capacity = _table.Capacity;

        for (int i = 0; i < capacity; i++)
        {
            RollupRow* row = _table.At(i);
            if (row->Key == 0L)
                continue;

            double value = score != null ? score(*row) : row->TotalUnits;
            if (filled == top.Length && value <= scores[filled - 1])
                continue;

            int at = filled < top.Length ? filled++ : filled - 1;
            while (at > 0 && scores[at - 1] < value)
            {
                top[at] = top[at - 1];
                scores[at] = scores[at - 1];
                at--;
            }

            top[at] = *row;
            scores[at] = value;
        }

        return top;
    }

    /// <summary>Copies the largest rows by total time, largest first, into caller-owned memory. Allocates nothing.</summary>
    public int CopyTop(RollupRow* rows, int capacity)
    {
        int filled = 0;
        int slots = _table.Capacity;

        for (int i = 0; i < slots; i++)
        {
            RollupRow* row = _table.At(i);
            if (row->Key == 0L)
                continue;

            long total = row->TotalUnits;
            if (filled == capacity && total <= rows[filled - 1].TotalUnits)
                continue;

            int at = filled < capacity ? filled++ : filled - 1;
            while (at > 0 && rows[at - 1].TotalUnits < total)
            {
                rows[at] = rows[at - 1];
                at--;
            }

            rows[at] = *row;
        }

        return filled;
    }

    /// <summary>Finds one row by key.</summary>
    public bool TryFind(long key, out RollupRow row)
    {
        RollupRow* found = _table.Find(key == 0L ? NoKey : key);
        row = found != null ? *found : default;
        return found != null;
    }

    /// <summary>Calls <paramref name="visitor"/> for every row, in no particular order.</summary>
    public void Visit(Action<RollupRow> visitor)
    {
        int capacity = _table.Capacity;

        for (int i = 0; i < capacity; i++)
        {
            RollupRow* row = _table.At(i);
            if (row->Key != 0L)
                visitor(*row);
        }
    }

    internal void Clear()
    {
        _table.Clear();
        Count = 0;
        LostUnits = 0L;
    }
}

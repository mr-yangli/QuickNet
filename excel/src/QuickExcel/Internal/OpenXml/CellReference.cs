using System.Runtime.CompilerServices;

namespace QuickExcel.Internal.OpenXml;

/// <summary>
/// Converts between zero-based (row, column) indices and Excel A1-style references.
/// Hot path: called for every cell, so kept allocation-free via span parsing.
/// </summary>
internal static class CellReference
{
    /// <summary>Converts a zero-based column index to its A1 column letters (0 -&gt; "A", 26 -&gt; "AA").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetColumnName(int columnIndex)
    {
        // Max column XFD = index 16383 => 3 letters. Reserve enough.
        Span<char> buffer = stackalloc char[8];
        int idx = buffer.Length;
        int n = columnIndex;
        while (n >= 0)
        {
            int rem = n % 26;
            buffer[--idx] = (char)('A' + rem);
            n = n / 26 - 1;
        }
        return new string(buffer.Slice(idx));
    }

    /// <summary>Writes a full A1 reference (e.g. "AB12") into <paramref name="destination"/>, returning the chars written.</summary>
    public static int Format(Span<char> destination, int rowIndex, int columnIndex)
    {
        // column letters
        int pos = 0;
        Span<char> colBuf = stackalloc char[4];
        int colIdx = colBuf.Length;
        int n = columnIndex;
        while (n >= 0)
        {
            int rem = n % 26;
            colBuf[--colIdx] = (char)('A' + rem);
            n = n / 26 - 1;
        }
        for (int i = colIdx; i < colBuf.Length; i++)
            destination[pos++] = colBuf[i];

        // row number is 1-based
        TryFormatRow(destination.Slice(pos), rowIndex + 1, out int written);
        pos += written;
        return pos;
    }

    private static bool TryFormatRow(Span<char> dest, int value, out int charsWritten)
    {
        Span<char> tmp = stackalloc char[10];
        int idx = tmp.Length;
        bool neg = value < 0;
        uint v = (uint)(neg ? -(long)value : value);
        do
        {
            tmp[--idx] = (char)('0' + (int)(v % 10));
            v /= 10;
        } while (v > 0);
        if (neg) tmp[--idx] = '-';
        int len = tmp.Length - idx;
        if (dest.Length < len)
        {
            charsWritten = 0;
            return false;
        }
        tmp.Slice(idx, len).CopyTo(dest);
        charsWritten = len;
        return true;
    }

    /// <summary>Parses an A1 reference into zero-based (row, column). Only supports the column+row form.</summary>
    public static (int row, int column) Parse(ReadOnlySpan<char> reference)
    {
        int column = 0;
        int i = 0;
        while (i < reference.Length && char.IsLetter(reference[i]))
        {
            char c = char.ToUpperInvariant(reference[i]);
            column = column * 26 + (c - 'A' + 1);
            i++;
        }
        column -= 1; // to zero based

        int row = 0;
        while (i < reference.Length && char.IsDigit(reference[i]))
        {
            row = row * 10 + (reference[i] - '0');
            i++;
        }
        row -= 1; // to zero based

        return (row, column);
    }

    /// <summary>Parses just the column index from an A1 reference (used for fast header mapping).</summary>
    public static int ParseColumn(ReadOnlySpan<char> reference)
    {
        int column = 0;
        foreach (char c in reference)
        {
            if (!char.IsLetter(c)) break;
            column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return column - 1;
    }
}

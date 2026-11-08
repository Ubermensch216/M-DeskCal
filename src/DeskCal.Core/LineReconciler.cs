namespace DeskCal.Core;

public enum LineOpKind
{
    /// <summary>Old line survives unchanged at <see cref="LineOp.NewIndex"/> (its id, done flag and colours are kept).</summary>
    Keep,
    /// <summary>Old line's text is replaced by the new line at the same slot.</summary>
    Update,
    /// <summary>A new line with no old counterpart.</summary>
    Add,
    /// <summary>An old line that no longer exists.</summary>
    Delete,
}

public readonly record struct LineOp(LineOpKind Kind, int OldIndex, int NewIndex);

/// <summary>
/// Maps an edited multi-line text back onto the existing items of a day so that untouched lines keep
/// their identity (history, completion, colours) even when lines are inserted or removed above them.
/// Lines are matched by longest common subsequence; lines between two anchors are paired by position
/// (treated as edits), and any surplus becomes an add or a delete.
/// </summary>
public static class LineReconciler
{
    public static IReadOnlyList<LineOp> Reconcile(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        int n = oldLines.Count, m = newLines.Count;

        // lcs[i, j] = length of the LCS of oldLines[i..] and newLines[j..]
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = oldLines[i] == newLines[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<LineOp>(Math.Max(n, m));
        int oi = 0, nj = 0;
        while (oi < n || nj < m)
        {
            // Find the next matched pair at or after (oi, nj), or the end of both lists.
            int mi = n, mj = m;
            {
                int i = oi, j = nj;
                while (i < n && j < m)
                {
                    if (oldLines[i] == newLines[j] && lcs[i, j] == lcs[i + 1, j + 1] + 1) { mi = i; mj = j; break; }
                    if (lcs[i + 1, j] >= lcs[i, j + 1]) i++; else j++;
                }
            }

            // The gap before the match: pair by position, then add/delete the surplus.
            int gapOld = mi - oi, gapNew = mj - nj;
            int paired = Math.Min(gapOld, gapNew);
            for (int k = 0; k < paired; k++) ops.Add(new LineOp(LineOpKind.Update, oi + k, nj + k));
            for (int k = paired; k < gapNew; k++) ops.Add(new LineOp(LineOpKind.Add, -1, nj + k));
            for (int k = paired; k < gapOld; k++) ops.Add(new LineOp(LineOpKind.Delete, oi + k, -1));

            if (mi < n && mj < m) ops.Add(new LineOp(LineOpKind.Keep, mi, mj));
            oi = mi + 1;
            nj = mj + 1;
        }
        return ops;
    }
}

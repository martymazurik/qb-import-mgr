namespace QbImportMgr.Core.Domain;

public enum TemplateLineKind { Percent, Fixed }

/// <summary>Value: basis points (Percent, 2500 = 25%) or cents (Fixed), in the direction of the transaction.</summary>
public sealed record TemplateLine(long CategoryId, long? QbNameId, string? Class, TemplateLineKind Kind, long Value, string? Memo);

public sealed class SplitTemplate
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public List<TemplateLine> Lines { get; set; } = [];
}

public static class SplitTemplateMath
{
    /// <summary>
    /// Fixed lines take their amount, percent lines their share (rounded to the cent), and the last
    /// line takes whatever remains so the splits always balance.
    /// </summary>
    public static IReadOnlyList<TxnSplit> Apply(IReadOnlyList<TemplateLine> lines, long txnSignedCents)
    {
        if (lines.Count == 0) throw new ArgumentException("Template has no lines.", nameof(lines));
        long total = Math.Abs(txnSignedCents);
        var entered = new long[lines.Count];
        for (int i = 0; i < lines.Count - 1; i++)
        {
            var l = lines[i];
            entered[i] = l.Kind == TemplateLineKind.Fixed
                ? l.Value
                : (long)Math.Round(total * l.Value / 10000m, MidpointRounding.AwayFromZero);
        }
        entered[^1] = total - entered.Take(lines.Count - 1).Sum();

        return lines.Select((l, i) => new TxnSplit
        {
            Seq = i + 1,
            CategoryId = l.CategoryId,
            QbNameId = l.QbNameId,
            Class = l.Class,
            AmountCents = SplitMath.ToStoredCents(entered[i], txnSignedCents),
            Memo = l.Memo,
        }).ToList();
    }

    /// <summary>A template from a transaction's splits, as percentages (basis points) or fixed amounts.</summary>
    public static IReadOnlyList<TemplateLine> FromSplits(IReadOnlyList<TxnSplit> splits, long txnSignedCents, TemplateLineKind kind)
    {
        long total = Math.Abs(txnSignedCents);
        return splits.Select(x =>
        {
            long entered = SplitMath.ToEnteredCents(x.AmountCents, txnSignedCents);
            long value = kind == TemplateLineKind.Fixed || total == 0
                ? entered
                : (long)Math.Round(entered * 10000m / total, MidpointRounding.AwayFromZero);
            return new TemplateLine(x.CategoryId, x.QbNameId, x.Class, total == 0 ? TemplateLineKind.Fixed : kind, value, x.Memo);
        }).ToList();
    }
}

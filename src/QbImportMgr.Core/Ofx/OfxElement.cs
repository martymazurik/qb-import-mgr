namespace QbImportMgr.Core.Ofx;

public sealed class OfxElement(string name, OfxElement? parent)
{
    public string Name { get; } = name;
    public OfxElement? Parent { get; } = parent;
    public string? Value { get; set; }
    public List<OfxElement> Children { get; } = [];

    public OfxElement? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

    /// <summary>Direct child value, falling back to the nearest descendant (tolerates flattened SGML).</summary>
    public string? Get(string name) =>
        Child(name)?.Value ?? Descendants(name).FirstOrDefault()?.Value;

    public IEnumerable<OfxElement> Descendants(string name)
    {
        foreach (var c in Children)
        {
            if (c.Name == name) yield return c;
            foreach (var d in c.Descendants(name)) yield return d;
        }
    }

    public override string ToString() => Value is null ? $"<{Name}> ({Children.Count})" : $"<{Name}>{Value}";
}

/// <summary>Builds an element tree from tokens, closing SGML leaf elements that have no end tag.</summary>
public static class OfxTreeBuilder
{
    /// <summary>Aggregates (elements with children) used by bank and credit-card statements.</summary>
    internal static readonly HashSet<string> KnownAggregates =
    [
        "OFX", "SIGNONMSGSRSV1", "SONRS", "STATUS", "FI",
        "BANKMSGSRSV1", "STMTTRNRS", "STMTRS", "BANKACCTFROM", "BANKACCTTO", "BANKTRANLIST",
        "STMTTRN", "LEDGERBAL", "AVAILBAL", "BALLIST", "BAL", "PAYEE", "CURRENCY", "ORIGCURRENCY",
        "CREDITCARDMSGSRSV1", "CCSTMTTRNRS", "CCSTMTRS", "CCACCTFROM", "CCACCTTO",
        "CCSTMTENDTRNRS", "CCSTMTENDRS", "CCCLOSING", "STMTENDTRNRS", "STMTENDRS", "CLOSING",
        "INVSTMTMSGSRSV1", "INVSTMTTRNRS", "INVSTMTRS", "INVACCTFROM", "INVTRANLIST", "INVPOSLIST",
        "INVBAL", "SIGNUPMSGSRSV1", "MKTGINFO", "IMAGEDATA",
    ];

    public static OfxElement Build(IEnumerable<OfxToken> tokens, OfxFormat format)
    {
        var root = new OfxElement("#root", null);
        var stack = new Stack<OfxElement>();
        stack.Push(root);

        foreach (var t in tokens)
        {
            switch (t.Kind)
            {
                case OfxTokenKind.StartTag:
                    CloseOpenLeaf(stack, format);
                    var el = new OfxElement(t.Value, stack.Peek());
                    stack.Peek().Children.Add(el);
                    stack.Push(el);
                    break;

                case OfxTokenKind.Text:
                    var top = stack.Peek();
                    if (top != root && top.Children.Count == 0)
                        top.Value = top.Value is null ? t.Value : top.Value + " " + t.Value;
                    break;

                case OfxTokenKind.EndTag:
                    if (stack.Any(e => e.Name == t.Value))
                    {
                        while (stack.Count > 1)
                        {
                            var popped = stack.Pop();
                            if (popped.Name == t.Value) break;
                        }
                    }
                    break; // stray end tag: ignore
            }
        }

        return root;
    }

    private static void CloseOpenLeaf(Stack<OfxElement> stack, OfxFormat format)
    {
        var top = stack.Peek();
        if (top.Parent is null || top.Children.Count > 0) return;
        if (top.Value is not null)
        {
            stack.Pop(); // leaf with text and no end tag (SGML), or mixed content we don't care about
            return;
        }
        // Empty SGML leaf such as "<MEMO>" directly followed by the next tag.
        if (format == OfxFormat.Sgml && !KnownAggregates.Contains(top.Name))
            stack.Pop();
    }
}

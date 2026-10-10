using System.Text;

namespace ConnectionManager.Core.Steam;

/// One node of Valve's KeyValues text format: a string value, or a block of named children.
public sealed class VdfNode
{
    private readonly List<KeyValuePair<string, VdfNode>> _children = [];

    private VdfNode(string? value) => Value = value;

    public string? Value { get; }
    public IReadOnlyList<KeyValuePair<string, VdfNode>> Children => _children;

    /// The first child with this key (Steam's keys are case-insensitive), or null.
    public VdfNode? this[string key] =>
        _children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    internal static VdfNode Leaf(string value) => new(value);
    internal static VdfNode Block() => new(null);
    internal void Add(string key, VdfNode child) => _children.Add(new(key, child));
}

/// Parses the KeyValues text files Steam writes, such as libraryfolders.vdf.
public static class Vdf
{
    private const int MaxDepth = 32; // Steam's files nest 3–4 levels; anything deeper is damaged or hostile

    private readonly record struct Token(string Text, bool Quoted)
    {
        public bool Is(char brace) => !Quoted && Text.Length == 1 && Text[0] == brace;
    }

    public static VdfNode Parse(string text)
    {
        var root = VdfNode.Block();
        var pos = 0;
        ParseBlock(text, ref pos, root, depth: 0);
        return root;
    }

    private static void ParseBlock(string text, ref int pos, VdfNode block, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("Blocks are nested too deeply");
        var topLevel = depth == 0;
        while (NextToken(text, ref pos) is { } key)
        {
            if (key.Is('}'))
            {
                if (topLevel) throw new FormatException("Unexpected '}'");
                return;
            }
            if (key.Is('{')) throw new FormatException("A block needs a key before '{'");

            var value = NextToken(text, ref pos) ?? throw new FormatException($"Key '{key.Text}' has no value");
            if (value.Is('{'))
            {
                var child = VdfNode.Block();
                ParseBlock(text, ref pos, child, depth + 1);
                block.Add(key.Text, child);
            }
            else if (value.Is('}')) throw new FormatException($"Key '{key.Text}' has no value");
            else block.Add(key.Text, VdfNode.Leaf(value.Text));
        }
        if (!topLevel) throw new FormatException("A block is missing its closing '}'");
    }

    private static Token? NextToken(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsWhiteSpace(c))
            {
                pos++;
                continue;
            }
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            if (c is '{' or '}')
            {
                pos++;
                return new Token(c.ToString(), false);
            }
            return c == '"' ? ReadQuoted(text, ref pos) : ReadBare(text, ref pos);
        }
        return null;
    }

    private static Token ReadQuoted(string text, ref int pos)
    {
        var sb = new StringBuilder();
        pos++; // opening quote
        while (pos < text.Length)
        {
            var c = text[pos++];
            if (c == '"') return new Token(sb.ToString(), true);
            if (c == '\\' && pos < text.Length)
            {
                var escaped = text[pos++];
                sb.Append(escaped switch { 'n' => '\n', 't' => '\t', _ => escaped }); // \\ and \" keep the second character
                continue;
            }
            sb.Append(c);
        }
        throw new FormatException("A quoted string is missing its closing quote");
    }

    private static Token ReadBare(string text, ref int pos)
    {
        var start = pos;
        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"')) pos++;
        return new Token(text[start..pos], false);
    }
}

// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Globalization;
using System.Text;

namespace Yaat.Client.Automation.Selectors;

/// <summary>
/// Parses the automation host's CSS-like selector syntax.
/// <code>
/// selectorList := path ( "," path )*
/// path         := compound ( combinator compound )*
/// combinator   := "&gt;&gt;" | "&gt;"          // descendant or direct child; whitespace = descendant
/// compound     := ( type | "*" )? ( "#" id )? filter*   // whitespace before "#", "[" or ":" starts a new compound
/// type         := IDENT                      // the element's type or a base type, exactly (case-insensitive)
/// id           := DIGITS (node id, at most one) | IDENT (Name)
/// filter       := "[" attr "]" | ":" pseudoClass
/// attr         := propPath op value          // propPath starting "dc." reads the DataContext
/// propPath     := IDENT ( "." IDENT )*
/// op           := "=" | "*=" | "^=" | "$="
/// value        := QUOTED_STRING | RAW        // RAW runs to whitespace or "]"; quote a value holding either
/// pseudoClass  := "visible" | "hidden" | "enabled" | "disabled" | "focused" | "checked"
///               | "has-text(" value ")" | "role(" value ")" | "nth(" DIGITS ")"   // :nth only on a path's last compound
/// </code>
/// Examples: <c>Button</c>, <c>*</c>, <c>#42</c>, <c>Button#Save</c>, <c>Button[Text="Sign in"]</c>, <c>Slider[Value=1.5]</c>,
/// <c>Button:has-text("Sign in"):enabled</c>, <c>ListBoxItem[dc.Callsign=AAL123]</c>,
/// <c>ListBox &gt;&gt; ListBoxItem:nth(2)</c>, <c>#Form #Save</c>, <c>Button, MenuItem</c>.
/// </summary>
public static class SelectorParser
{
    public static ParsedSelector Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new SelectorParseException("selector is empty", 0);
        }

        var state = new ParserState(Tokenize(input));
        List<SelectorPath> alternatives = [ParsePath(state)];
        while (state.Match(TokenKind.Comma))
        {
            alternatives.Add(ParsePath(state));
        }

        if (!state.IsAtEnd)
        {
            throw new SelectorParseException($"unexpected token '{state.Current.Text}'", state.Current.Position);
        }

        return new ParsedSelector(alternatives);
    }

    private static SelectorPath ParsePath(ParserState state)
    {
        List<SelectorStep> steps = [new SelectorStep(Combinator.Self, ParseCompound(state))];
        while (NextCombinator(state) is { } combinator)
        {
            if (state.NthPosition is int nthPosition)
            {
                throw new SelectorParseException("':nth' may only be on the last compound of a path", nthPosition);
            }

            steps.Add(new SelectorStep(combinator, ParseCompound(state)));
        }

        return new SelectorPath(steps);
    }

    private static Combinator? NextCombinator(ParserState state)
    {
        if (state.Match(TokenKind.DescendantOp))
        {
            return Combinator.Descendant;
        }

        if (state.Match(TokenKind.ChildOp))
        {
            return Combinator.Child;
        }

        return state.HasImplicitDescendant() ? Combinator.Descendant : null;
    }

    private static CompoundSelector ParseCompound(ParserState state)
    {
        int compoundStart = state.Index;
        state.NthPosition = null;
        bool universal = state.Match(TokenKind.Star);
        string? typeName = null;
        if (!universal && (state.Current.Kind == TokenKind.Ident))
        {
            typeName = state.Consume(TokenKind.Ident).Text;
        }

        List<SelectorFilter> filters = [];
        int? nodeId = ParseFilters(state, compoundStart, filters);
        if (!universal && (typeName is null) && (nodeId is null) && (filters.Count == 0))
        {
            throw new SelectorParseException("compound selector cannot be empty", state.Current.Position);
        }

        return new CompoundSelector(typeName, nodeId, filters);
    }

    /// <summary>
    /// Reads the <c>#id</c>, <c>[attr]</c> and <c>:pseudo</c> parts of a compound; returns the node id, if any. Stops at
    /// whitespace once the compound has begun, so <c>A #b</c> leaves the descendant combinator to the caller.
    /// </summary>
    private static int? ParseFilters(ParserState state, int compoundStart, List<SelectorFilter> filters)
    {
        int? nodeId = null;
        while (!(state.Current.PrecededByWhitespace && (state.Index > compoundStart)))
        {
            if (state.Match(TokenKind.Hash))
            {
                nodeId = ParseHash(state, filters, nodeId);
            }
            else if (state.Match(TokenKind.LBracket))
            {
                filters.Add(ParseAttributeFilter(state));
                state.Consume(TokenKind.RBracket);
            }
            else if (state.Match(TokenKind.Colon))
            {
                filters.Add(ParsePseudoFilter(state));
            }
            else
            {
                return nodeId;
            }
        }

        return nodeId;
    }

    /// <summary><c>#42</c> sets the node id (a compound has at most one); <c>#Name</c> adds a <c>Name</c> filter. Returns the node id.</summary>
    private static int? ParseHash(ParserState state, List<SelectorFilter> filters, int? nodeId)
    {
        Token token = state.Current;
        if (token.Kind == TokenKind.Number)
        {
            if (nodeId is not null)
            {
                throw new SelectorParseException("a compound selector can have only one node id", token.Position);
            }

            state.Advance();
            if (!int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            {
                throw new SelectorParseException($"node id '{token.Text}' is out of range", token.Position);
            }

            return parsed;
        }

        if (token.Kind == TokenKind.Ident)
        {
            state.Advance();
            filters.Add(new AttributeFilter("Name", AttrOp.Equal, token.Text, false));
            return nodeId;
        }

        throw new SelectorParseException("expected nodeId or name after '#'", token.Position);
    }

    private static AttributeFilter ParseAttributeFilter(ParserState state)
    {
        List<string> pathParts = [ExpectIdent(state, "expected attribute name")];
        while (state.Match(TokenKind.Dot))
        {
            pathParts.Add(ExpectIdent(state, "expected identifier after '.'"));
        }

        bool isDataContext = pathParts[0].Equals("dc", StringComparison.Ordinal);
        string path = string.Join('.', isDataContext ? pathParts.Skip(1) : pathParts);
        AttrOp op = ParseOperator(state);
        Token value = state.Current;
        if (!IsValueToken(value.Kind))
        {
            throw new SelectorParseException("expected attribute value", value.Position);
        }

        state.Advance();
        return new AttributeFilter(path, op, value.Text, isDataContext);
    }

    private static AttrOp ParseOperator(ParserState state)
    {
        AttrOp? op = state.Current.Kind switch
        {
            TokenKind.OpEq => AttrOp.Equal,
            TokenKind.OpContains => AttrOp.Contains,
            TokenKind.OpStartsWith => AttrOp.StartsWith,
            TokenKind.OpEndsWith => AttrOp.EndsWith,
            _ => null,
        };
        if (op is null)
        {
            throw new SelectorParseException("expected operator (=, *=, ^=, $=)", state.Current.Position);
        }

        state.Advance();
        return op.Value;
    }

    private static PseudoFilter ParsePseudoFilter(ParserState state)
    {
        int namePosition = state.Current.Position;
        string name = ExpectIdent(state, "expected pseudo-class name");
        while (state.Match(TokenKind.Minus))
        {
            name += "-" + ExpectIdent(state, "expected identifier after '-'");
        }

        if (!PseudoClassNames.All.Contains(name))
        {
            throw new SelectorParseException(
                $"unknown pseudo-class ':{name}' (known: {string.Join(", ", PseudoClassNames.All.Order(StringComparer.Ordinal))})",
                namePosition
            );
        }

        string? argument = ParsePseudoArgument(state, name, namePosition);
        if (name == PseudoClassNames.Nth)
        {
            state.NthPosition = namePosition;
        }

        return new PseudoFilter(name, argument);
    }

    private static string? ParsePseudoArgument(ParserState state, string name, int namePosition)
    {
        string? argument = null;
        if (state.Match(TokenKind.LParen))
        {
            if ((name == PseudoClassNames.Nth) && (state.Current.Kind != TokenKind.RParen))
            {
                return ParseNthArgument(state);
            }

            if (IsValueToken(state.Current.Kind))
            {
                argument = state.Current.Text;
                state.Advance();
            }

            state.Consume(TokenKind.RParen);
        }

        if ((argument is null) && PseudoClassNames.NeedsArgument(name))
        {
            throw new SelectorParseException($"':{name}' needs an argument, e.g. :{name}(...)", namePosition);
        }

        return argument;
    }

    /// <summary>The <c>N</c> of <c>:nth(N)</c>: a whole number of 0 or more, then the closing parenthesis.</summary>
    private static string ParseNthArgument(ParserState state)
    {
        Token argument = state.Current;
        bool isWholeNumber =
            (argument.Kind == TokenKind.Number)
            && int.TryParse(argument.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && (state.Peek(1).Kind == TokenKind.RParen);
        if (!isWholeNumber)
        {
            throw new SelectorParseException("':nth' needs a whole number of 0 or more, e.g. :nth(0)", argument.Position);
        }

        state.Advance();
        state.Consume(TokenKind.RParen);
        return argument.Text;
    }

    private static string ExpectIdent(ParserState state, string message)
    {
        if (state.Current.Kind != TokenKind.Ident)
        {
            throw new SelectorParseException(message, state.Current.Position);
        }

        return state.Consume(TokenKind.Ident).Text;
    }

    private static bool IsValueToken(TokenKind kind) => kind is TokenKind.QuotedString or TokenKind.RawValue or TokenKind.Ident or TokenKind.Number;

    // ---------- tokenizer ----------

    private enum TokenKind
    {
        Ident,
        Number,
        QuotedString,
        RawValue,
        Hash,
        Dot,
        LBracket,
        RBracket,
        LParen,
        RParen,
        Colon,
        Comma,
        Star,
        Minus,
        DescendantOp,
        ChildOp,
        OpEq,
        OpContains,
        OpStartsWith,
        OpEndsWith,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position, bool PrecededByWhitespace);

    /// <summary>A token read from the source: its kind, its text and how many characters it consumed.</summary>
    private readonly record struct Lexeme(TokenKind Kind, string Text, int Length);

    private static List<Token> Tokenize(string source)
    {
        List<Token> tokens = [];
        int index = 0;
        bool precededByWhitespace = false;
        bool expectValue = false;
        while (index < source.Length)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                precededByWhitespace = true;
                index++;
                continue;
            }

            // After an attribute operator, an unquoted value runs to whitespace or ']' whatever its characters.
            Lexeme lexeme =
                expectValue && (source[index] is not ('\'' or '"' or ']'))
                    ? ReadRun(source, index, TokenKind.RawValue, c => !char.IsWhiteSpace(c) && (c != ']'))
                    : ReadLexeme(source, index);
            tokens.Add(new Token(lexeme.Kind, lexeme.Text, index, precededByWhitespace));
            index += lexeme.Length;
            precededByWhitespace = false;
            expectValue = lexeme.Kind is TokenKind.OpEq or TokenKind.OpContains or TokenKind.OpStartsWith or TokenKind.OpEndsWith;
        }

        tokens.Add(new Token(TokenKind.End, "<end>", source.Length, precededByWhitespace));
        return tokens;
    }

    private static Lexeme ReadLexeme(string source, int start)
    {
        if (TwoCharOperator(source, start) is { } twoChar)
        {
            return new Lexeme(twoChar, source.Substring(start, 2), 2);
        }

        char c = source[start];
        if (SingleCharKind(c) is { } single)
        {
            return new Lexeme(single, c.ToString(), 1);
        }

        if (c is '\'' or '"')
        {
            return ReadQuoted(source, start);
        }

        if (char.IsDigit(c))
        {
            return ReadRun(source, start, TokenKind.Number, char.IsDigit);
        }

        if (char.IsLetter(c) || (c == '_'))
        {
            return ReadRun(source, start, TokenKind.Ident, ch => char.IsLetterOrDigit(ch) || (ch == '_'));
        }

        throw new SelectorParseException($"unexpected character '{c}'", start);
    }

    private static TokenKind? TwoCharOperator(string source, int start)
    {
        if (start + 1 >= source.Length)
        {
            return null;
        }

        return source.AsSpan(start, 2) switch
        {
            "*=" => TokenKind.OpContains,
            "^=" => TokenKind.OpStartsWith,
            "$=" => TokenKind.OpEndsWith,
            ">>" => TokenKind.DescendantOp,
            _ => null,
        };
    }

    private static TokenKind? SingleCharKind(char c) =>
        c switch
        {
            '#' => TokenKind.Hash,
            '.' => TokenKind.Dot,
            '[' => TokenKind.LBracket,
            ']' => TokenKind.RBracket,
            '(' => TokenKind.LParen,
            ')' => TokenKind.RParen,
            ':' => TokenKind.Colon,
            ',' => TokenKind.Comma,
            '-' => TokenKind.Minus,
            '*' => TokenKind.Star,
            '=' => TokenKind.OpEq,
            '>' => TokenKind.ChildOp,
            _ => null,
        };

    /// <summary>How a parse error names an expected token: the character itself where it is one.</summary>
    private static string Describe(TokenKind kind) =>
        kind switch
        {
            TokenKind.RBracket => "']'",
            TokenKind.RParen => "')'",
            TokenKind.Ident => "a name",
            _ => kind.ToString(),
        };

    /// <summary>A quoted string, single or double quotes, with backslash escaping the next character.</summary>
    private static Lexeme ReadQuoted(string source, int start)
    {
        char quote = source[start];
        var text = new StringBuilder();
        int index = start + 1;
        while ((index < source.Length) && (source[index] != quote))
        {
            bool escaped = (source[index] == '\\') && (index + 1 < source.Length);
            text.Append(escaped ? source[index + 1] : source[index]);
            index += escaped ? 2 : 1;
        }

        if (index >= source.Length)
        {
            throw new SelectorParseException("unterminated string literal", start);
        }

        return new Lexeme(TokenKind.QuotedString, text.ToString(), index + 1 - start);
    }

    private static Lexeme ReadRun(string source, int start, TokenKind kind, Func<char, bool> belongs)
    {
        int end = start;
        while ((end < source.Length) && belongs(source[end]))
        {
            end++;
        }

        return new Lexeme(kind, source[start..end], end - start);
    }

    private sealed class ParserState(List<Token> tokens)
    {
        public int Index { get; private set; }

        /// <summary>Where the current compound's <c>:nth</c> is, so a following combinator can reject it.</summary>
        public int? NthPosition { get; set; }

        public Token Current => tokens[Index];

        public bool IsAtEnd => Current.Kind == TokenKind.End;

        public Token Peek(int offset) => tokens[Math.Min(Index + offset, tokens.Count - 1)];

        public bool Match(TokenKind kind)
        {
            if (Current.Kind != kind)
            {
                return false;
            }

            Advance();
            return true;
        }

        public Token Consume(TokenKind kind)
        {
            if (Current.Kind != kind)
            {
                throw new SelectorParseException($"expected {Describe(kind)}, got '{Current.Text}'", Current.Position);
            }

            Token token = Current;
            Advance();
            return token;
        }

        public void Advance() => Index++;

        /// <summary>
        /// Whitespace between two compound selectors implies a descendant combinator, unless the next token closes a
        /// structure or ends the path (comma, closing bracket or parenthesis, end).
        /// </summary>
        public bool HasImplicitDescendant() =>
            Current.PrecededByWhitespace && Current.Kind is not (TokenKind.Comma or TokenKind.RBracket or TokenKind.RParen or TokenKind.End);
    }
}

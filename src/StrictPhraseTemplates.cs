using System;
using System.Collections.Generic;

namespace ClaudeZhInput;

/// <summary>
/// A finite language for complete programming instructions. Slots are opaque
/// single-line backtick literals or explicitly typed ASCII numbers. Unknown
/// text, actors, conditions, scopes and extra sentences must fall through.
/// Call only after personal/exact phrase lookup; this class has no model or I/O.
/// </summary>
public static class StrictPhraseTemplates
{
    private const int MaximumSourceLength = 5000;

    private enum TokenKind { Text, Literal, SignedDecimal, UnsignedInteger, NonNegativeDecimal }
    private readonly record struct Token(TokenKind Kind, string Text = "");
    private sealed record Template(Token[] Tokens, Func<IReadOnlyList<string>, string> Render);
    private enum MatchState { NoMatch, Prefix, Complete }

    private static readonly Template[] Templates = CreateTemplates();

    /// <summary>
    /// Translates only an entire supported sentence. Optional 请 is preserved
    /// as Please; only spaces, ideographic spaces, and one final 。 or . are
    /// accepted outside the grammar. Failure always returns an empty result.
    /// Literals, signs, numeric digits and decimal precision are never changed.
    /// </summary>
    public static bool TryTranslate(string source, out string english)
    {
        english = "";
        if (!TryStart(source, out var start, out var polite)) return false;
        foreach (var template in Templates)
        {
            if (Match(source, start, template, false, out var values) != MatchState.Complete) continue;
            var body = template.Render(values!);
            english = polite ? "Please " + char.ToLowerInvariant(body[0]) + body[1..] : body;
            return true;
        }
        return false;
    }

    /// <summary>
    /// A conservative prefix check over exactly the same finite token language.
    /// True only means model preparation may be delayed while typing; it does
    /// not authorize a translation or claim the next input will still match.
    /// Empty, multiline and unsupported prefixes return false. A complete
    /// sentence is also a valid prefix. Call again after every input change.
    /// </summary>
    public static bool CouldComplete(string prefix)
    {
        if (!TryStart(prefix, out var start, out _)) return false;
        foreach (var template in Templates)
            if (Match(prefix, start, template, true, out _) != MatchState.NoMatch) return true;
        return false;
    }

    private static bool TryStart(string source, out int start, out bool polite)
    {
        start = 0;
        polite = false;
        if (string.IsNullOrEmpty(source) || source.Length > MaximumSourceLength) return false;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (char.IsControl(c) || c is '\u2028' or '\u2029') return false;
            if (char.IsHighSurrogate(c))
            {
                if (++i >= source.Length || !char.IsLowSurrogate(source[i])) return false;
            }
            else if (char.IsLowSurrogate(c)) return false;
        }
        SkipSpaces(source, ref start);
        if (start == source.Length) return false;
        if (source[start] == '请')
        {
            polite = true;
            start++;
            SkipSpaces(source, ref start);
        }
        return true;
    }

    private static MatchState Match(string source, int start, Template template, bool prefixMode,
        out List<string>? values)
    {
        values = null;
        var position = start;
        for (var tokenIndex = 0; tokenIndex < template.Tokens.Length; tokenIndex++)
        {
            var token = template.Tokens[tokenIndex];
            SkipSpaces(source, ref position);
            if (position == source.Length)
                return prefixMode ? PossiblePrefix(source, template, tokenIndex, 0) : MatchState.NoMatch;
            var slotStart = position;
            if (token.Kind == TokenKind.Text)
            {
                var remaining = source.AsSpan(position);
                if (remaining.StartsWith(token.Text.AsSpan(), StringComparison.Ordinal))
                    position += token.Text.Length;
                else if (prefixMode && token.Text.AsSpan().StartsWith(remaining, StringComparison.Ordinal))
                    return PossiblePrefix(source, template, tokenIndex + 1, token.Text.Length - remaining.Length);
                else return MatchState.NoMatch;
                continue;
            }
            if (token.Kind == TokenKind.Literal)
            {
                if (source[position] != '`') return MatchState.NoMatch;
                var closing = source.IndexOf('`', position + 1);
                if (closing < 0)
                    return prefixMode ? PossiblePrefix(source, template, tokenIndex + 1,
                        source.Length == position + 1 ? 2 : 1) : MatchState.NoMatch;
                if (closing == position + 1) return MatchState.NoMatch;
                position = closing + 1;
            }
            else
            {
                var number = ReadNumber(source, ref position, token.Kind, prefixMode,
                    tokenIndex == template.Tokens.Length - 1);
                if (number == MatchState.Prefix) return PossiblePrefix(source, template, tokenIndex + 1, 1);
                if (number != MatchState.Complete) return number;
            }
            if (!prefixMode) (values ??= []).Add(source[slotStart..position]);
        }
        SkipSpaces(source, ref position);
        if (position < source.Length && source[position] is '。' or '.')
        {
            position++;
            SkipSpaces(source, ref position);
        }
        return position == source.Length ? MatchState.Complete : MatchState.NoMatch;
    }

    private static MatchState PossiblePrefix(string source, Template template, int nextToken, int needed)
    {
        for (var i = nextToken; i < template.Tokens.Length; i++)
            needed += template.Tokens[i].Kind switch
            {
                TokenKind.Text => template.Tokens[i].Text.Length,
                TokenKind.Literal => 3, // Opening tick, at least one data character, closing tick.
                _ => 1 // Every number type admits the single ASCII digit 0.
            };
        return needed <= MaximumSourceLength - source.Length ? MatchState.Prefix : MatchState.NoMatch;
    }

    private static MatchState ReadNumber(string source, ref int position, TokenKind kind, bool prefixMode, bool finalToken)
    {
        if (kind == TokenKind.SignedDecimal && source[position] is '+' or '-') position++;
        if (position == source.Length) return prefixMode ? MatchState.Prefix : MatchState.NoMatch;
        if (!IsDigit(source[position])) return MatchState.NoMatch;
        if (source[position++] == '0')
        {
            if (position < source.Length && IsDigit(source[position])) return MatchState.NoMatch;
        }
        else while (position < source.Length && IsDigit(source[position])) position++;

        if (kind != TokenKind.UnsignedInteger && position < source.Length && source[position] == '.')
        {
            if (position + 1 == source.Length && prefixMode && !finalToken) return MatchState.Prefix;
            // A final dot may instead be the one permitted sentence stop.
            if (position + 1 < source.Length && IsDigit(source[position + 1]))
            {
                position += 2;
                while (position < source.Length && IsDigit(source[position])) position++;
            }
        }
        return MatchState.Complete;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static void SkipSpaces(string source, ref int position)
    {
        while (position < source.Length && source[position] is ' ' or '\u3000') position++;
    }

    private static Template[] CreateTemplates()
    {
        var templates = new List<Template>();
        var literal = new Token(TokenKind.Literal);
        var number = new Token(TokenKind.SignedDecimal);
        var count = new Token(TokenKind.UnsignedInteger);
        var duration = new Token(TokenKind.NonNegativeDecimal);
        static Token Text(string value) => new(TokenKind.Text, value);
        void Add(Func<IReadOnlyList<string>, string> render, params Token[] tokens) =>
            templates.Add(new(tokens, render));

        // Every noun and action is closed vocabulary. There is no free text slot.
        var nouns = new (string Zh, string En)[]
        {
            ("代码", "code"), ("文件", "file"), ("目录", "directory"), ("路径", "path"),
            ("变量", "variable"), ("函数", "function"), ("参数", "parameter"), ("配置项", "configuration entry")
        };
        var actions = new (string Zh, string En)[]
        {
            ("检查", "Check"), ("修改", "Modify"), ("保留", "Keep"),
            ("不要修改", "Do not modify"), ("不要删除", "Do not delete")
        };
        foreach (var action in actions)
        {
            Add(v => $"{action.En} {v[0]}.", Text(action.Zh), literal);
            foreach (var noun in nouns)
                Add(v => $"{action.En} the {noun.En} {v[0]}.", Text(action.Zh), Text(noun.Zh), literal);
        }

        foreach (var lead in new[] { "把", "将" })
        {
            Add(v => $"Replace {v[0]} with {v[1]}.", Text(lead), literal, Text("替换为"), literal);
            Add(v => $"Replace the code {v[0]} with {v[1]}.", Text(lead), Text("代码"), literal, Text("替换为"), literal);
            foreach (var noun in nouns)
            {
                if (noun.Zh is "代码" or "路径") continue;
                Add(v => $"Rename the {noun.En} {v[0]} to {v[1]}.",
                    Text(lead), Text(noun.Zh), literal, Text("重命名为"), literal);
            }
            foreach (var noun in nouns)
            {
                if (noun.Zh is not ("变量" or "参数" or "配置项")) continue;
                foreach (var verb in new[] { "设为", "设置为" })
                {
                    Add(v => $"Set the {noun.En} {v[0]} to {v[1]}.", Text(lead), Text(noun.Zh), literal, Text(verb), number);
                    Add(v => $"Set the {noun.En} {v[0]} to {v[1]}.", Text(lead), Text(noun.Zh), literal, Text(verb), literal);
                }
            }
            foreach (var setting in new[] { (Zh: "重试次数", En: "retry count"), (Zh: "最大重试次数", En: "maximum retry count") })
            {
                foreach (var verb in new[] { "设为", "设置为" })
                {
                    Add(v => $"Set the {setting.En} to {v[0]}.", Text(lead), Text(setting.Zh), Text(verb), count);
                    Add(v => $"Set the {setting.En} to {v[0]}.", Text(lead), Text(setting.Zh), Text(verb), count, Text("次"));
                }
            }
            foreach (var verb in new[] { "设为", "设置为" })
                foreach (var unit in new[] { (Zh: "毫秒", En: "ms"), (Zh: "秒", En: "s") })
                    Add(v => $"Set the timeout to {v[0]} {unit.En}.", Text(lead), Text("超时时间"), Text(verb), duration, Text(unit.Zh));

            Add(v => $"Replace {v[1]} with {v[2]} in the file {v[0]}.",
                Text("在"), Text("文件"), literal, Text("中"), Text(lead), literal, Text("替换为"), literal);
            Add(v => $"Replace the code {v[1]} with {v[2]} in the file {v[0]}.",
                Text("在"), Text("文件"), literal, Text("中"), Text(lead), Text("代码"), literal, Text("替换为"), literal);
            foreach (var noun in nouns)
            {
                if (noun.Zh is not ("变量" or "函数" or "参数" or "配置项")) continue;
                Add(v => $"Rename the {noun.En} {v[1]} to {v[2]} in the file {v[0]}.",
                    Text("在"), Text("文件"), literal, Text("中"), Text(lead), Text(noun.Zh), literal, Text("重命名为"), literal);
            }
        }
        Add(v => $"Check {v[1]} in the file {v[0]}.", Text("在"), Text("文件"), literal, Text("中"), Text("检查"), literal);
        Add(v => $"Check the code {v[1]} in the file {v[0]}.", Text("在"), Text("文件"), literal, Text("中"), Text("检查"), Text("代码"), literal);
        return templates.ToArray();
    }
}

using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public static class CadProtectedTokenPolicy
{
    private const string PlaceholderKind = "Placeholder";
    private const string CadFormatControlKind = "CadFormatControl";

    public static IReadOnlyList<CadProtectedToken> Extract(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        var tokens = new List<CadProtectedToken>();
        ExtractRange(sourceText, 0, sourceText.Length, tokens);
        return tokens;
    }

    private static void ExtractRange(string sourceText, int start, int end, List<CadProtectedToken> tokens)
    {
        for (var index = start; index < end;)
        {
            if (TryReadLineBreak(sourceText, index, end, out var tokenLength) ||
                TryReadPercentControl(sourceText, index, end, out tokenLength))
            {
                AddToken(sourceText, index, tokenLength, CadFormatControlKind, tokens);
                index += tokenLength;
                continue;
            }

            if (sourceText[index] == '\\')
            {
                if (index + 1 < end && sourceText[index + 1] == '{')
                {
                    tokenLength = ReadEscapedBraceSpan(sourceText, index, end);
                    AddToken(sourceText, index, tokenLength, CadFormatControlKind, tokens);
                    index += tokenLength;
                    continue;
                }

                if (index + 1 < end && sourceText[index + 1] == '}')
                {
                    AddToken(sourceText, index, 2, CadFormatControlKind, tokens);
                    index += 2;
                    continue;
                }

                if (TryReadMTextControl(sourceText, index, end, out tokenLength))
                {
                    AddToken(sourceText, index, tokenLength, CadFormatControlKind, tokens);
                    index += tokenLength;
                    continue;
                }
            }

            if (sourceText[index] == '{')
            {
                var group = ReadBraceGroup(sourceText, index, end);
                if (!group.IsComplete)
                {
                    AddToken(sourceText, index, end - index, ClassifyBraceSpan(sourceText, index, end), tokens);
                    return;
                }

                if (group.IsNested || group.HasEscapedBrace ||
                    !IsSafeFlatFormattingGroup(sourceText, index + 1, group.ClosingBraceIndex))
                {
                    tokenLength = group.ClosingBraceIndex - index + 1;
                    AddToken(sourceText, index, tokenLength, ClassifyBraceSpan(sourceText, index, group.ClosingBraceIndex + 1), tokens);
                    index += tokenLength;
                    continue;
                }

                AddToken(sourceText, index, 1, CadFormatControlKind, tokens);
                ExtractRange(sourceText, index + 1, group.ClosingBraceIndex, tokens);
                AddToken(sourceText, group.ClosingBraceIndex, 1, CadFormatControlKind, tokens);
                index = group.ClosingBraceIndex + 1;
                continue;
            }

            if (sourceText[index] == '}')
            {
                AddToken(sourceText, index, 1, CadFormatControlKind, tokens);
                index++;
                continue;
            }

            index++;
        }
    }

    private static BraceGroup ReadBraceGroup(string sourceText, int openingBraceIndex, int end)
    {
        var depth = 1;
        var nested = false;
        var escapedBrace = false;
        for (var index = openingBraceIndex + 1; index < end; index++)
        {
            if (sourceText[index] == '\\' && index + 1 < end)
            {
                if (sourceText[index + 1] is '{' or '}')
                    escapedBrace = true;
                index++;
                continue;
            }

            if (sourceText[index] == '{')
            {
                nested = true;
                depth++;
            }
            else if (sourceText[index] == '}' && --depth == 0)
            {
                return new BraceGroup(index, true, nested, escapedBrace);
            }
        }

        return new BraceGroup(-1, false, nested, escapedBrace);
    }

    private static bool IsSafeFlatFormattingGroup(string sourceText, int start, int end)
    {
        if (!TryReadRecognizedMTextControl(sourceText, start, end, out _))
            return false;

        for (var index = start; index < end;)
        {
            if (sourceText[index] != '\\')
            {
                index++;
                continue;
            }

            if (index + 1 >= end || sourceText[index + 1] is '{' or '}' ||
                !TryReadRecognizedMTextControl(sourceText, index, end, out var tokenLength))
                return false;
            index += tokenLength;
        }

        return true;
    }

    private static int ReadEscapedBraceSpan(string sourceText, int start, int end)
    {
        var depth = 1;
        for (var index = start + 2; index < end;)
        {
            if (index + 1 < end && sourceText[index] == '\\' && sourceText[index + 1] == '{')
            {
                depth++;
                index += 2;
                continue;
            }

            if (index + 1 < end && sourceText[index] == '\\' && sourceText[index + 1] == '}')
            {
                depth--;
                index += 2;
                if (depth == 0)
                    return index - start;
                continue;
            }

            index++;
        }

        return end - start;
    }

    private static bool TryReadLineBreak(string sourceText, int index, int end, out int tokenLength)
    {
        tokenLength = 0;
        if (sourceText[index] == '\r')
        {
            tokenLength = index + 1 < end && sourceText[index + 1] == '\n' ? 2 : 1;
            return true;
        }

        if (sourceText[index] != '\n')
            return false;
        tokenLength = 1;
        return true;
    }

    private static bool TryReadPercentControl(string sourceText, int index, int end, out int tokenLength)
    {
        tokenLength = 0;
        if (index + 2 >= end || sourceText[index] != '%' || sourceText[index + 1] != '%' || !IsAsciiLetter(sourceText[index + 2]))
            return false;
        tokenLength = 3;
        return true;
    }

    private static bool TryReadMTextControl(string sourceText, int index, int end, out int tokenLength)
    {
        tokenLength = 0;
        if (index + 1 >= end || sourceText[index] != '\\')
            return false;

        var command = sourceText[index + 1];
        if (command is '\\' or '~')
        {
            tokenLength = 2;
            return true;
        }

        if (command is 'P' or 'L' or 'l' or 'O' or 'o' or 'K' or 'k')
        {
            tokenLength = 2;
            return true;
        }

        if (command == 'U' && index + 6 < end && sourceText[index + 2] == '+' &&
            IsHex(sourceText[index + 3]) && IsHex(sourceText[index + 4]) &&
            IsHex(sourceText[index + 5]) && IsHex(sourceText[index + 6]))
        {
            tokenLength = 7;
            return true;
        }

        if (!IsAsciiLetter(command))
            return false;

        for (var cursor = index + 2; cursor < end; cursor++)
        {
            if (sourceText[cursor] == ';')
            {
                tokenLength = cursor - index + 1;
                return true;
            }

            if (sourceText[cursor] is '\\' or '{' or '}' or '\r' or '\n')
                break;
        }

        return false;
    }

    private static bool TryReadRecognizedMTextControl(string sourceText, int index, int end, out int tokenLength)
    {
        if (!TryReadMTextControl(sourceText, index, end, out tokenLength))
            return false;

        var command = sourceText[index + 1];
        if (command is '\\' or '~' or 'P' or 'L' or 'l' or 'O' or 'o' or 'K' or 'k')
            return true;
        if (command == 'U')
            return tokenLength == 7 && sourceText[index + 2] == '+';
        return sourceText[index + tokenLength - 1] == ';' &&
               command is 'A' or 'a' or 'C' or 'c' or 'F' or 'f' or 'H' or 'h' or
                   'Q' or 'q' or 'S' or 's' or 'T' or 't' or 'W' or 'w' or 'p';
    }

    private static string ClassifyBraceSpan(string sourceText, int start, int end) =>
        TryReadMTextControl(sourceText, start + 1, end, out _) ? CadFormatControlKind : PlaceholderKind;

    private static void AddToken(string sourceText, int start, int length, string kind, List<CadProtectedToken> tokens) =>
        tokens.Add(new CadProtectedToken
        {
            Token = sourceText.Substring(start, length),
            Kind = kind,
            Ordinal = tokens.Count
        });

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsHex(char value) => value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private readonly record struct BraceGroup(int ClosingBraceIndex, bool IsComplete, bool IsNested, bool HasEscapedBrace);
}

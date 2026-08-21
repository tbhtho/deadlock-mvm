using System.Text;

namespace DeadlockMVM.Core.Vdf;

/// <summary>
/// Minimal parser for Valve's VDF (KeyValues) text format, sufficient for
/// <c>libraryfolders.vdf</c> and <c>appmanifest_*.acf</c> files.
/// </summary>
public static class VdfParser
{
    /// <summary>
    /// Parses VDF text into a root object. The returned object contains the
    /// top-level key/value pairs; for files with a single named root (e.g.
    /// <c>"AppState" { ... }</c>) that name becomes a key in the result.
    /// </summary>
    public static VdfObject Parse(string text)
    {
        var tokenizer = new Tokenizer(text.TrimStart('\uFEFF'));
        var root = new VdfObject();

        tokenizer.SkipTrivia();

        if (tokenizer.Peek == '{')
        {
            tokenizer.ReadChar('{');
            ParseEntries(tokenizer, root);
            tokenizer.ReadChar('}');
            return root;
        }

        while (!tokenizer.End)
        {
            tokenizer.SkipTrivia();
            if (tokenizer.End)
                break;

            var key = tokenizer.ReadString();
            tokenizer.SkipTrivia();
            root.Add(key, ReadValue(tokenizer));
        }

        return root;
    }

    private static void ParseEntries(Tokenizer tokenizer, VdfObject target)
    {
        while (true)
        {
            tokenizer.SkipTrivia();
            if (tokenizer.End || tokenizer.Peek == '}')
                break;

            var key = tokenizer.ReadString();
            tokenizer.SkipTrivia();
            target.Add(key, ReadValue(tokenizer));
        }
    }

    private static VdfValue ReadValue(Tokenizer tokenizer)
    {
        tokenizer.SkipTrivia();

        if (tokenizer.Peek == '{')
        {
            tokenizer.ReadChar('{');
            var obj = new VdfObject();
            ParseEntries(tokenizer, obj);
            tokenizer.ReadChar('}');
            return VdfValue.FromObject(obj);
        }

        return VdfValue.FromString(tokenizer.ReadString());
    }

    private sealed class Tokenizer
    {
        private readonly string _text;
        private int _position;

        public Tokenizer(string text) => _text = text;

        public bool End => _position >= _text.Length;

        public char Peek => _text[_position];

        public void SkipTrivia()
        {
            while (_position < _text.Length)
            {
                var c = _text[_position];

                if (char.IsWhiteSpace(c))
                {
                    _position++;
                    continue;
                }

                if (c == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')
                {
                    var newline = _text.IndexOf('\n', _position);
                    _position = newline < 0 ? _text.Length : newline + 1;
                    continue;
                }

                if (c == '/' && _position + 1 < _text.Length && _text[_position + 1] == '*')
                {
                    var end = _text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                    _position = end < 0 ? _text.Length : end + 2;
                    continue;
                }

                break;
            }
        }

        public char ReadChar(char expected)
        {
            SkipTrivia();
            if (End || _text[_position] != expected)
                throw new FormatException($"Expected '{expected}' at position {_position}.");

            return _text[_position++];
        }

        public string ReadString()
        {
            SkipTrivia();
            if (End || _text[_position] != '"')
                throw new FormatException($"Expected '\"' at position {_position}.");

            _position++;

            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                var c = _text[_position++];

                if (c == '"')
                    return builder.ToString();

                if (c == '\\' && _position < _text.Length)
                {
                    var escaped = _text[_position];
                    if (escaped == '"' || escaped == '\\')
                    {
                        builder.Append(escaped);
                        _position++;
                        continue;
                    }
                }

                builder.Append(c);
            }

            throw new FormatException("Unterminated string.");
        }
    }
}

using System;
using System.Runtime.CompilerServices;
using System.Globalization;
using HotChocolate.Language.Properties;

namespace HotChocolate.Language
{
    public ref partial struct Utf8GraphQLReader
    {
        private int _nextNewLines;
        private ReadOnlySpan<byte> _graphQLData;
        private ReadOnlySpan<byte> _value;
        private FloatFormat? _floatFormat;
        private int _length;
        private int _position;
        private TokenKind _kind;
        private int _start;
        private int _end;
        private int _line;
        private int _lineStart;
        private int _column;

        public Utf8GraphQLReader(ReadOnlySpan<byte> graphQLData)
        {
            if (graphQLData.Length == 0)
            {
                throw new ArgumentException("The graphQLData is empty.", nameof(graphQLData));
            }

            _kind = TokenKind.StartOfFile;
            _start = 0;
            _end = 0;
            _lineStart = 0;
            _line = 1;
            _column = 1;
            _graphQLData = graphQLData;
            _length = graphQLData.Length;
            _nextNewLines = 0;
            _position = 0;
            _value = null;
            _floatFormat = null;
        }

        public ReadOnlySpan<byte> GraphQLData => _graphQLData;

        /// <summary>
        /// Gets the kind of <see cref="SyntaxToken" />.
        /// </summary>
        public TokenKind Kind => _kind;

        /// <summary>
        /// Gets the character offset at which this node begins.
        /// </summary>
        public int Start => _start;

        /// <summary>
        /// Gets the character offset at which this node ends.
        /// </summary>
        public int End => _end;

        /// <summary>
        /// The current position of the lexer pointer.
        /// </summary>
        public int Position => _position;

        /// <summary>
        /// Gets the 1-indexed line number on which this
        /// <see cref="SyntaxToken" /> appears.
        /// </summary>
        public int Line => _line;

        /// <summary>
        /// The source index of where the current line starts.
        /// </summary>
        public int LineStart => _lineStart;

        /// <summary>
        /// Gets the 1-indexed column number at which this
        /// <see cref="SyntaxToken" /> begins.
        /// </summary>
        public int Column => _column;

        /// <summary>
        /// For non-punctuation tokens, represents the interpreted
        /// value of the token.
        /// </summary>
        public ReadOnlySpan<byte> Value => _value;

        public FloatFormat? FloatFormat => _floatFormat;

        public bool Read()
        {
            _floatFormat = null;

            if (_position == 0)
            {
                SkipBoml();
            }

            SkipWhitespaces();
            UpdateColumn();

            if (IsEndOfStream())
            {
                _start = _position;
                _end = _position;
                _kind = TokenKind.EndOfFile;
                _value = null;
                return false;
            }

            byte code = _graphQLData[_position];

            if (_isPunctuator[code])
            {
                ReadPunctuatorToken(code);
                return true;
            }

            if (_isLetterOrUnderscore[code])
            {
                ReadNameToken();
                return true;
            }

            if (_isDigitOrMinus[code])
            {
                ReadNumberToken(code);
                return true;
            }

            if (code == Hash)
            {
                ReadCommentToken();
                return true;
            }

            if (code == Quote)
            {
                if (_length > _position + 2
                    && _graphQLData[_position + 1] == Quote
                    && _graphQLData[_position + 2] == Quote)
                {
                    _position += 2;
                    ReadBlockStringToken();
                }
                else
                {
                    ReadStringValueToken();
                }
                return true;
            }

            throw new SyntaxException(this,
                $"Unexpected character `{(char)code}` ({code}).");
        }

        /// <summary>
        /// Reads name tokens as specified in
        /// http://facebook.github.io/graphql/October2016/#Name
        /// [_A-Za-z][_0-9A-Za-z]
        /// from the current lexer state.
        /// </summary>
        /// <param name="state">The lexer state.</param>
        /// <param name="previous">The previous-token.</param>
        /// <returns>
        /// Returns the name token read from the current lexer state.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadNameToken()
        {
            var start = _position;
            var position = _position;

            while (++position < _length
                && _isLetterOrDigitOrUnderscore[_graphQLData[position]])
            {
            }

            _kind = TokenKind.Name;
            _start = start;
            _end = position;
            _value = _graphQLData.Slice(start, position - start);
            _position = position;
        }

        /// <summary>
        /// Reads punctuator tokens as specified in
        /// http://facebook.github.io/graphql/October2016/#sec-Punctuators
        /// one of ! $ ( ) ... : = @ [ ] { | }
        /// additionally the reader will tokenize ampersands.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadPunctuatorToken(byte code)
        {
            _start = _position;
            _end = ++_position;
            _value = null;

            if (code == Dot)
            {
                if (_graphQLData[_position] == Dot
                    && _graphQLData[_position + 1] == Dot)
                {
                    _position += 2;
                    _end = _position;
                    _kind = TokenKind.Spread;
                }
                else
                {
                    _position--;
                    throw new SyntaxException(this,
                        string.Format(CultureInfo.InvariantCulture,
                            LangResources.Reader_InvalidToken,
                            TokenKind.Spread));
                }
            }
            else
            {
                _kind = _punctuatorKind[code];
            }
        }

        /// <summary>
        /// Reads int tokens as specified in
        /// http://facebook.github.io/graphql/October2016/#IntValue
        /// or a float tokens as specified in
        /// http://facebook.github.io/graphql/October2016/#FloatValue
        /// from the current lexer state.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadNumberToken(byte firstCode)
        {
            int start = _position;
            byte code = firstCode;
            var isFloat = false;

            if (code == Minus)
            {
                code = _graphQLData[++_position];
            }

            if (code == Zero && !IsEndOfStream(_position + 1))
            {
                code = _graphQLData[++_position];
                if (_isDigit[code])
                {
                    throw new SyntaxException(this,
                        "Invalid number, unexpected digit after 0: " +
                        $"`{(char)code}` ({code}).");
                }
            }
            else
            {
                code = ReadDigits(code);
            }

            if (code == Dot)
            {
                isFloat = true;
                _floatFormat = Language.FloatFormat.FixedPoint;
                code = _graphQLData[++_position];
                code = ReadDigits(code);
            }

            if ((code | 0x20) == E)
            {
                isFloat = true;
                _floatFormat = Language.FloatFormat.Exponential;
                code = _graphQLData[++_position];
                if (code == Plus
                    || code == Minus)
                {
                    code = _graphQLData[++_position];
                }
                ReadDigits(code);
            }

            _kind = isFloat ? TokenKind.Float : TokenKind.Integer;
            _start = start;
            _end = _position;
            _value = _graphQLData.Slice(start, _position - start);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadDigits(byte firstCode)
        {
            if (!_isDigit[firstCode])
            {
                throw new SyntaxException(this,
                    "Invalid number, expected digit but got: " +
                    $"`{(char)firstCode}` ({firstCode}).");
            }

            byte code = firstCode;

            while (true)
            {
                if (++_position >= _length)
                {
                    code = Space;
                    break;
                }

                code = _graphQLData[_position];
                if (!_isDigit[code])
                {
                    break;
                }
            }

            return code;
        }

        /// <summary>
        /// Reads comment tokens as specified in
        /// http://facebook.github.io/graphql/October2016/#sec-Comments
        /// #[\u0009\u0020-\uFFFF]*
        /// from the current lexer state.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadCommentToken()
        {
            var start = _position;
            var trimStart = _position + 1;
            var trim = true;
            var run = true;

            while (run && ++_position < _length)
            {
                var code = _graphQLData[_position];

                if (code == Hash || code == Space || code == Tab)
                {
                    if (trim)
                    {
                        trimStart = _position;
                    }
                }
                else if (_isControlCharacter[code])
                {
                    run = false;
                }
                else
                {
                    trim = false;
                }
            }

            _kind = TokenKind.Comment;
            _start = start;
            _end = _position;
            _value = _graphQLData.Slice(trimStart, _position - trimStart);
        }

        /// <summary>
        /// Reads string tokens as specified in
        /// http://facebook.github.io/graphql/October2016/#StringValue
        /// "([^"\\\u000A\u000D]|(\\(u[0-9a-fA-F]{4}|["\\/bfnrt])))*"
        /// from the current lexer state.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadStringValueToken()
        {
            var start = _position;

            while (++_position < _length)
            {
                byte code = _graphQLData[_position];

                if (code == NewLine || code == Return)
                {
                    return;
                }
                else if (code == Quote)
                {
                    _kind = TokenKind.String;
                    _start = start;
                    _end = _position;
                    _value = _graphQLData.Slice(start + 1, _position - start - 1);
                    _position++;
                    return;
                }
                else if (code == Backslash)
                {
                    code = _graphQLData[++_position];
                    if (!_isEscapeCharacter[code])
                    {
                        throw new SyntaxException(this,
                            $"Invalid character escape sequence: \\{code}.");
                    }
                }
                else if (_isControlCharacter[code])
                {
                    throw new SyntaxException(this,
                        $"Invalid character within String: {code}.");
                }
            }

            throw new SyntaxException(this, "Unterminated string.");
        }

        /// <summary>
        /// Reads block string tokens as specified in
        /// http://facebook.github.io/graphql/draft/#BlockStringCharacter
        /// from the current lexer state.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadBlockStringToken()
        {
            var start = _position - 2;
            _nextNewLines = 0;

            while (++_position < _length)
            {
                byte code = _graphQLData[_position];

                if (code == NewLine)
                {
                    _nextNewLines++;
                }
                else if (code == Return)
                {
                    int next = _position + 1;
                    if (next < _length
                        && _graphQLData[next] == NewLine)
                    {
                        _position = next;
                    }
                    _nextNewLines++;
                }
                else if (code == Quote)
                {
                    if (_graphQLData[_position + 1] == GraphQLConstants.Quote
                        && _graphQLData[_position + 2] == GraphQLConstants.Quote)
                    {
                        _kind = TokenKind.BlockString;
                        _start = start;
                        _end = _position + 2;
                        int valueStart = _start + 3;
                        int valueEnd = _end - 3;
                        int valueLength = valueEnd - valueStart;
                        _value = _graphQLData.Slice(valueStart, valueLength);
                        _position = _end + 1;
                        return;
                    }
                }
                else if (code == Backslash)
                {
                    if (_graphQLData[_position + 1] == GraphQLConstants.Quote
                        && _graphQLData[_position + 2] == GraphQLConstants.Quote
                        && _graphQLData[_position + 3] == GraphQLConstants.Quote)
                    {
                        _position += 3;
                    }
                }
                else if (_isControlCharacterNoNewLine[code])
                {
                    throw new SyntaxException(this,
                        $"Invalid character within String: {code}.");
                }
            }

            throw new SyntaxException(this, "Unterminated string.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SkipWhitespaces()
        {
            if (_nextNewLines > 0)
            {
                SetNewLine(_nextNewLines);
                _nextNewLines = 0;
            }

            while (!IsEndOfStream())
            {
                byte code = _graphQLData[_position];

                switch (code)
                {
                    case NewLine:
                        ++_position;
                        SetNewLine();
                        break;

                    case Return:
                        if (++_position < _length
                           && _graphQLData[_position] == NewLine)
                        {
                            ++_position;
                        }
                        SetNewLine();
                        break;

                    case Tab:
                    case Space:
                    case Comma:
                        ++_position;
                        break;

                    default:
                        return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SkipBoml()
        {
            byte code = _graphQLData[_position];

            if (code == 239)
            {
                if (_graphQLData[_position + 1] == 187
                    && _graphQLData[_position + 2] == 191)
                {
                    _position += 3;
                }
            }

            if (code == 254)
            {
                if (_graphQLData[_position + 1] == 255)
                {
                    _position += 2;
                }
            }
        }

        /// <summary>
        /// Sets the state to a new line.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetNewLine()
        {
            _line++;
            _lineStart = _position;
            UpdateColumn();
        }

        /// <summary>
        /// Sets the state to a new line.
        /// </summary>
        /// <param name="lines">
        /// The number of lines to skip.
        /// </param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetNewLine(int lines)
        {
            if (lines < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(lines),
                    "Must be greater or equal to 1.");
            }

            _line += lines;
            _lineStart = _position;
            UpdateColumn();
        }

        /// <summary>
        /// Updates the column index.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateColumn()
        {
            _column = 1 + _position - _lineStart;
        }

        /// <summary>
        /// Checks if the lexer source pointer has reached
        /// the end of the GraphQL source text.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEndOfStream()
        {
            return _position >= _length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsEndOfStream(int position)
        {
            return position >= _length;
        }
    }
}

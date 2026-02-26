using Antlr4.Runtime;
using static MainParser;
using System.Text;

/// <summary>
/// All lexer methods that used in grammar
/// should start with Upper Case Char similar to Lexer rules.
/// </summary>
public abstract class MainLexerBase : Lexer
{
    protected readonly ICharStream _input;
    private IToken _lastToken = null;
    private IToken _lastVisibleToken = null;

    private bool _isBOS = true;
    private int _currentDepth = 0;
    private bool _hotstringIsLiteral = true;
    private char _stringQuote = '\0';
    private StringBuilder _stringBuilder;
    private CommonToken _stringToken;

    internal MainLexerBase(ICharStream input) : base(input)
    {
        _input = input;
    }

    internal MainLexerBase(ICharStream input, TextWriter output, TextWriter errorOutput) : this(input)
    {
        RemoveErrorListeners(); // Remove default error listeners
        AddErrorListener(new MainLexerErrorListener());
    }

    /// <summary>
    /// Return the next token from the character stream and records this last
    /// token in case it resides on the default channel. This recorded token
    /// is used to determine when the lexer could possibly match a regex
    /// literal.
    /// 
    /// </summary>
    /// <returns>
    /// The next token from the character stream.
    /// </returns>
    /// 
    public override IToken NextToken()
    {
        // Get the next token.
        IToken next = base.NextToken();

        if ((next.Type == TokenConstants.EOF && this.CurrentMode == MainLexer.STRING_MODE)
            || (next.Channel == MainLexer.ERROR && this.CurrentMode == MainLexer.STRING_MODE))
        {
            EndStringMode();
            throw new Keysharp.Core.ParseException("Unterminated string literal", next);
        }

        // Record last visible token
        if (next.Channel == DefaultTokenChannel)
            _lastVisibleToken = next;

        // Keep track of newlines
        if (next.Type == EOL || next.Type == DirectiveNewline)
            _isBOS = true;
        else if (next.Type != WS)
            _isBOS = false;

        // Record last token (whether visible or not)
        _lastToken = next;

        return next;
    }

    public override IToken Emit()
    {
        var token = base.Emit();
        var type = token.Type;

        if (type == MainLexer.HotstringNewline)
        {
            if (_lastToken.Type == MainLexer.StringLiteral)
                ApplyHotstringOptions(_lastToken.Text);
            (token as CommonToken).Type = MainLexer.EOL;
        }

        if (type == MainLexer.StringLiteral)
            _stringToken = token as CommonToken;

        return token;
    }

    protected int _processHotstringOptions(string input) {
        int res = -1;
        int strLength = input.Length;
        char[] charArray = input.ToCharArray();
        for (int i = 0; i < strLength; i++) { 
            var c = charArray[i];
            if (c == ':' || c == ';' || c == '/')
                break;
            if (c == 'x' || c == 'X') {
                res = (i == (strLength - 1)) ? 1 : (charArray[i + 1] == '0' ? 0 : 1);
            }
        }
        return res;
    }

    // Determine whether the X option is in effect or not
    protected bool IsHotstringLiteral() {
        int intermediate = _processHotstringOptions(base.Text.Substring(1));
        return intermediate == -1 ? _hotstringIsLiteral : intermediate == 0;
    }

	protected void ApplyHotstringOptions(string text)
	{
        if (_lastVisibleToken == null)
            return;

        if (_lastVisibleToken.Type == MainLexer.EndChars || _lastVisibleToken.Type == MainLexer.NoMouse)
            return;
		int intermediate = _processHotstringOptions(text);
		if (intermediate != -1)
			_hotstringIsLiteral = intermediate == 0;
	}

    protected void ProcessOpenBrace()
    {
        //_currentDepth++;
    }
    protected void ProcessCloseBrace()
    {
        //_currentDepth--;
    }

    // Hide EOL and WS if the previous visible token was a line continuation-allowing operator such as :=
    protected void ProcessEOL() {
        if (_currentDepth != 0)
        {
            this.Type = WS;
            return;
        }
        if (_lastVisibleToken == null) return;
        if (_lastVisibleToken.Type != MainLexer.OpenBrace && lineContinuationOperators.Contains(_lastVisibleToken.Type))
            this.Channel = Hidden;
    }
    protected void ProcessWS() {
        if (_lastVisibleToken == null) return;
        if (lineContinuationOperators.Contains(_lastVisibleToken.Type))
            this.Channel = Hidden;
    }

    private HashSet<int> unaryOperators = new HashSet<int> {
        MainLexer.BitNot,
        MainLexer.Not,
        MainLexer.PlusPlus,
        MainLexer.MinusMinus
    };

    public static HashSet<int> flowKeywords = new HashSet<int> {
        MainLexer.Try
    };

    public static HashSet<int> lineContinuationOperators = new HashSet<int> {
        MainLexer.OpenBracket,
        MainLexer.OpenBrace,
        MainLexer.OpenParen,
        MainLexer.DerefStart,
        MainLexer.Comma,
        MainLexer.Assign,
        MainLexer.QuestionMark,
        MainLexer.QuestionMarkDot,
        //MainLexer.Colon, // This may be after a label or switch-case clause, so we can't trim EOL and WS after it
        MainLexer.Plus,
        MainLexer.Minus,
        MainLexer.Divide,
        MainLexer.IntegerDivide,
        MainLexer.Multiply,
        MainLexer.NullCoalesce,
        MainLexer.RightShiftArithmetic,
        MainLexer.LeftShiftArithmetic,
        MainLexer.RightShiftLogical,
        MainLexer.LessThan,
        MainLexer.MoreThan,
        MainLexer.LessThanEquals,
        MainLexer.GreaterThanEquals,
        MainLexer.Equals_,
        MainLexer.NotEquals,
        MainLexer.IdentityEquals,
        MainLexer.IdentityNotEquals,
        MainLexer.RegExMatch,
        MainLexer.BitAnd,
        MainLexer.BitXOr,
        MainLexer.BitOr,
        MainLexer.And,
        MainLexer.Or,
        MainLexer.MultiplyAssign,
        MainLexer.DivideAssign,
        MainLexer.ModulusAssign,
        MainLexer.PlusAssign,
        MainLexer.MinusAssign,
        MainLexer.LeftShiftArithmeticAssign,
        MainLexer.RightShiftArithmeticAssign,
        MainLexer.RightShiftLogicalAssign,
        MainLexer.IntegerDivideAssign,
        MainLexer.ConcatenateAssign,
        MainLexer.BitAndAssign,
        MainLexer.BitXorAssign,
        MainLexer.BitOrAssign,
        MainLexer.PowerAssign,
        MainLexer.NullishCoalescingAssign,
        MainLexer.Arrow,
    };

	// Keep in sync with identifier rule in MainParser.g4
	public static bool IsIdentifierToken(int tokenType) => tokenType switch
	{
		MainLexer.Identifier => true,
		MainLexer.Default => true,
		MainLexer.This => true,
		MainLexer.Class => true,
		MainLexer.Enum => true,
		MainLexer.Extends => true,
		MainLexer.Super => true,
		MainLexer.Base => true,
		MainLexer.From => true,
		MainLexer.Get => true,
		MainLexer.Set => true,
		MainLexer.As => true,
		MainLexer.Do => true,
		MainLexer.NullLiteral => true,
		MainLexer.Parse => true,
		MainLexer.Reg => true,
		MainLexer.Read => true,
		MainLexer.Files => true,
		MainLexer.Throw => true,
		MainLexer.Yield => true,
		MainLexer.Async => true,
		MainLexer.Await => true,
		MainLexer.Import => true,
		MainLexer.Export => true,
		MainLexer.Delete => true,
		_ => false
	};

	protected void ProcessOpenBracket()
    {
        _currentDepth++;
    }
    protected void ProcessCloseBracket()
    {
        _currentDepth--;
    }
    protected void ProcessOpenParen()
    {
        _currentDepth++;
    }
    protected void ProcessCloseParen()
    {
        _currentDepth--;
    }

    protected void ProcessHotstringOpenBrace()
    {
        this.Type = MainLexer.OpenBrace;
        ProcessOpenBrace();
    }

    protected void BeginStringMode(char quote = '\0')
    {
        _stringQuote = quote;
        _stringBuilder = new StringBuilder();
    }

    protected bool IsCurrentStringQuote()
    {
        return _input.LA(-1) == _stringQuote;
    }

    protected void AppendInitialStringChunk()
    {
		// Start rule already stops before a whitespace-preceded ';' comment.
		// Just append everything after the opening quote; continuation-mode
		// trivia/comment handlers trim trailing whitespace if continuation follows.
		_stringBuilder.Append(this.Text);

		if (_stringToken == null)
            this.Type = MainLexer.StringLiteral;
        else
			this.Skip();
	}

    protected void ProcessStringTrivia()
    {
        TrimTrailingHorizontalWhitespace(_stringBuilder);
    }

    protected void MaybeEndStringMode()
    {
        char c = (char)_input.LA(-1);
        if (c == _stringQuote)
            EndStringMode();
        else if (c != '"' && c != '\'')
        {
            if (_stringQuote != '\0')
            {
				var token = Emit() as CommonToken;
				throw new ParseException($"Unterminated string literal (start: {_stringToken.Line}:{_stringToken.Column}, expected end: {token.Line}:{token.Column})", token);
            }
			Rewind();
			EndStringMode();
		}
        else
            _stringBuilder.Append(this.Text);
    }

    protected void EndStringMode()
    {
        _stringToken?.Text = _stringBuilder.ToString();
        _stringQuote = '\0';
        _stringBuilder = null;
        _stringToken = null;
        PopMode();
    }

    private uint _derefDepth = 0;
    protected void ProcessDeref() {
        if (_derefDepth == 0) {
            _derefDepth++;
            _currentDepth++;
            this.Type = DerefStart;
        } else {
            _derefDepth--;
            _currentDepth--;
            this.Type = DerefEnd;
        }
    }

    protected void ProcessContinuationSection()
    {
        var originalSegment = this.Text;
        string singleLine;

        // We are processing a string literal, so accumulate the string
        if (_stringBuilder != null)
        {
			singleLine = Keysharp.Scripting.Parser.MultilineString(originalSegment, 0, this.SourceName);
			if (NeedsSpaceBeforeContinuationSection())
				_stringBuilder.Append(' ');
			_stringBuilder.Append(singleLine);
			if (_stringToken == null)
				this.Type = MainLexer.StringLiteral;
            else
			    this.Skip();
			return;
		}
        
        singleLine = Keysharp.Scripting.Parser.ParseContinuationSection(originalSegment, 0, this.SourceName);
        if (NeedsSpaceBeforeContinuationSection())
            singleLine = " " + singleLine;

        if (!RewriteMatchedInputSegment(singleLine))
            throw new ParseException("Continuation section rewrite requires RewritableInputStream.", this.TokenStartLine, this.Text, this.SourceName);

        Rewind();
        this.Type = WS;
        this.Channel = Hidden;
    }

    protected void Rewind()
    {
		_input.Seek(this.TokenStartCharIndex);
		this.Line = this.TokenStartLine;
		this.Column = this.TokenStartColumn;
	}

    private bool NeedsSpaceBeforeContinuationSection()
    {
        char last;
        if (_stringBuilder != null)
        {
            if (_stringQuote == '\'' || _stringQuote == '"' || _stringBuilder.Length == 0)
                return false;
            last = _stringBuilder[^1]; 
            return char.IsLetterOrDigit(last) || last == '_';
		}
        if (_lastVisibleToken == null || string.IsNullOrEmpty(_lastVisibleToken.Text))
            return false;

        last = _lastVisibleToken.Text[_lastVisibleToken.Text.Length - 1];
        return char.IsLetterOrDigit(last) || last == '_';
    }

    private bool RewriteMatchedInputSegment(string replacement)
    {
        if (_input is not IRewritableCharStream rewritableInput)
            return false;

        rewritableInput.ReplaceRange(this.TokenStartCharIndex, _input.Index, replacement ?? string.Empty);
        return true;
    }

    private static void TrimTrailingHorizontalWhitespace(StringBuilder builder)
    {
        if (builder == null || builder.Length == 0)
            return;

        int i = builder.Length - 1;

        while (i >= 0 && IsHorizontalWhitespace(builder[i]))
            i--;

        builder.Length = i + 1;
    }

    private static bool IsHorizontalWhitespace(char ch)
    {
        return ch == ' ' || ch == '\t' || ch == '\u000B' || ch == '\u000C' || ch == '\u00A0';
    }

    protected bool IsBOS()
    {
        return _isBOS;
    }
    
    protected bool IsValidRemap() {
        int i = 0;
        if (_input.LA(-1) == '{' && _input.LA(-2) != '`')
            return false;
        while (true) {
            i++;
            var nextToken = _input.LA(i);
            switch (nextToken) {
                case Eof:
                case '\n':
                case '\r':
                    return true;
                case ' ':
                case '\u2028':
                case '\u2029':
                    continue;
                case ';':
                    if (i == 1)
                        return false;
                    return true;
                case '/':
                    if (_input.LA(i+1) == '*') {
                        return true;
                    }
                    return false;
            }
            return false;
        }
    }

    // In some cases decimals can be omitted the leading 0, for example .3 instead of 0.3.  
    protected bool IsValidDotDecimal() {
        if (_lastToken == null || _lastVisibleToken == null || _lastToken.Channel != DefaultTokenChannel)
            return true;
        if (lineContinuationOperators.Contains(_lastVisibleToken.Type))
            return true;
        return false;
    }

    protected bool IsCommentPossible() {
        int start = this.TokenStartCharIndex;
        if (start == 0)
            return true;
        // AntlrInputStream.LA calls ValueAt which just returns the char from the char[]
        int prevChar = _input.LA(-2);
        if (prevChar <= 0)
            return true;
        return char.IsWhiteSpace((char)prevChar);
    }

    public override void Reset()
    {
        _lastToken = null;
        _lastVisibleToken = null;
        _currentDepth = 0;
        _stringQuote = '\0';
        _stringBuilder = null;
        _stringToken = null;
        base.Reset();
    }
}

public class MainLexerErrorListener : IAntlrErrorListener<int>
{
    public void SyntaxError(
        TextWriter output, 
        IRecognizer recognizer,
        int offendingSymbol, 
        int line,
        int charPositionInLine, 
        string msg,
        RecognitionException e)
    {
        string sourceName = recognizer.InputStream.SourceName;
        throw new ParseException($"Lexing error at line {line}, column {charPositionInLine}, source {sourceName}: {msg}");
    }
}

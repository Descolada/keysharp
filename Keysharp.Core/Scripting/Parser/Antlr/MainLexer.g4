/*
 * The MIT License (MIT)
 *
 * This file is based on the ANTLR4 grammar for Javascript (https://github.com/antlr/grammars-v4/tree/master/javascript/javascript),
 * but very heavily modified by Descolada (modified for Keysharp use). 
 * 
 * List of authors and contributors for the Javascript grammar:
 * Copyright (c) 2014 by Bart Kiers (original author) and Alexandre Vitorelli (contributor -> ported to CSharp)
 * Copyright (c) 2017-2020 by Ivan Kochurkin (Positive Technologies):
    added ECMAScript 6 support, cleared and transformed to the universal grammar.
 * Copyright (c) 2018 by Juan Alvarez (contributor -> ported to Go)
 * Copyright (c) 2019 by Student Main (contributor -> ES2020)
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 */

lexer grammar MainLexer;

channels {
    ERROR,
    DIRECTIVE,
    WHITESPACE
}

options {
    superClass = MainLexerBase;
    caseInsensitive = true;
    accessLevel = internal;
}

tokens {
    DerefStart,
    DerefEnd,
    ObjectLiteralStart,
    ObjectLiteralEnd,
    ConcatDot,
    Maybe
}

SingleLineBlockComment  : '/*' NonEOLCharacter*? '*/' -> skip;
MultiLineComment  : '/*' .*? ('*/' | EOF) -> type(EOL);
SingleLineComment : SingleLineCommentAtom -> skip;

// First try consuming a hotstring 

HotstringLiteralTrigger
    : HotstringTriggerPart {this.IsBOS() && this.IsHotstringLiteral()}? -> pushMode(HOTSTRING_MODE), type(HotstringTrigger)
    ;
// These two are separated because I couldn't figure out how to conditionally pushMode(HOTSTRING_MODE)
HotstringTrigger
    : HotstringTriggerPart {this.IsBOS() && !this.IsHotstringLiteral()}?
    ;

// If no hotstring is matchable, try matching a remap
RemapKey:
    HotkeyModifier* HotkeyCharacter (WhiteSpace+ '&' WhiteSpace+ HotkeyCharacter)? '::' HotkeyModifierKey* HotkeyCharacter {this.IsBOS() && this.IsValidRemap()}?;

// Otherwise just match a hotkey trigger
HotkeyTrigger:
    HotkeyModifier* HotkeyCharacter (WhiteSpace+ '&' WhiteSpace+ HotkeyCharacter)? (WhiteSpace+ 'up')? '::' {this.IsBOS()}?;

OpenBracket                : '[' {this.ProcessOpenBracket();};
CloseBracket               : ']' {this.ProcessCloseBracket();};
OpenParen                  : '(' {this.ProcessOpenParen();};
CloseParen                 : ')' {this.ProcessCloseParen();};
OpenBrace                  : '{'; // {this.ProcessOpenBrace();};
CloseBrace                 : '}'; // {this.ProcessCloseBrace();};
Comma                      : ',';
Assign                     : ':=';
QuestionMark               : '?';
QuestionMarkDot            : '?.';
Colon                      : ':';
DoubleColon                : '::';
Ellipsis                   : '...';
Dot                        : '.';
PlusPlus                   : '++';
MinusMinus                 : '--';
Plus                       : '+';
Minus                      : '-';
BitNot                     : '~';
Not                        : '!';
Multiply                   : '*';
Divide                     : '/';
IntegerDivide              : '//';
Modulus                    : '%' {ProcessDeref();};
Power                      : '**';
NullCoalesce               : '??';
Hashtag                    : '#' -> mode(PREPROCESSOR_DIRECTIVE_MODE);
RightShiftArithmetic       : '>>';
LeftShiftArithmetic        : '<<';
RightShiftLogical          : '>>>';
LessThan                   : '<';
MoreThan                   : '>';
LessThanEquals             : '<=';
GreaterThanEquals          : '>=';
Equals_                    : '=';
NotEquals                  : '!=';
IdentityEquals             : '==';
IdentityNotEquals          : '!==';
RegExMatch                 : '~=';
BitAnd                     : '&';
BitXOr                     : '^';
BitOr                      : '|';
And                        : '&&';
Or                         : '||';
MultiplyAssign             : '*=';
DivideAssign               : '/=';
ModulusAssign              : '%=';
PlusAssign                 : '+=';
MinusAssign                : '-=';
LeftShiftArithmeticAssign  : '<<=';
RightShiftArithmeticAssign : '>>=';
RightShiftLogicalAssign    : '>>>=';
IntegerDivideAssign        : '//=';
ConcatenateAssign          : '.=';
BitAndAssign               : '&=';
BitXorAssign               : '^=';
BitOrAssign                : '|=';
PowerAssign                : '**=';
NullishCoalescingAssign    : '??=';
Arrow                      : '=>';

/// Null Literals
NullLiteral: 'null';
Unset: 'unset';

/// Boolean Literals
True: 'true';
False: 'false';

/// Numeric Literals

DecimalLiteral:
    DecimalIntegerLiteral '.' [0-9] [0-9_]* ExponentPart?
    | '.' [0-9] [0-9_]* ExponentPart? {this.IsValidDotDecimal()}?
    | DecimalIntegerLiteral ExponentPart?
;
HexIntegerLiteral    : '0x' [0-9a-f] HexDigit*;
OctalIntegerLiteral  : '0' [0-7]+;
OctalIntegerLiteral2 : '0o' [0-7] [_0-7]*;
BinaryIntegerLiteral : '0b' [01] [_01]*;
BigHexIntegerLiteral     : '0x' [0-9a-f] HexDigit* 'n';
BigOctalIntegerLiteral   : '0o' [0-7] [_0-7]* 'n';
BigBinaryIntegerLiteral  : '0b' [01] [_01]* 'n';
BigDecimalIntegerLiteral : DecimalIntegerLiteral 'n';

/// Keywords
Break      : 'break';
Do         : 'do';
Instanceof : 'instanceof';
Switch     : 'switch';
Case       : 'case';
Default    : 'default';
Else       : 'else';
Catch      : 'catch';
Finally    : 'finally';
Return     : 'return';
Continue   : 'continue';
For        : 'for';
While      : 'while';
// For Loop keywords allow optional trailing commas because AHK allows it
Parse      : 'parse';
Reg        : 'reg';
Read       : 'read';
Files      : 'files';
Loop       : 'loop';
Until      : 'until';
This       : 'this';
If         : 'if';
Throw      : 'throw';
Delete     : 'delete';
In         : 'in';
Try        : 'try';
Yield      : 'yield';
Is         : 'is';
Contains   : 'contains';
VerbalAnd  : 'and';
VerbalNot  : 'not';
VerbalOr   : 'or';
Goto       : 'goto';
Get        : 'get';
Set        : 'set';

/// Reserved Words
Class   : 'class';
Enum    : 'enum';
Extends : 'extends';
Super   : 'super';
Base    : 'base';
Export  : 'export';
Import  : 'import';
From    : 'from';
As      : 'as';

Async : 'async';
Await : 'await';

Static : 'static';
Global : 'global';
Local  : 'local';

/// Identifier Names and Identifiers
Identifier: IdentifierStart IdentifierPart*;

ContinuationSection: SingleContinuationSection {this.ProcessContinuationSection();};

StringLiteral
    : ('"' | '\'') {this.BeginStringMode((char)_input.LA(-1));} -> pushMode(STRING_MODE);

EOL: LineBreak {this.ProcessEOL();};
WS: WhiteSpace {this.ProcessWS();};

UnexpectedCharacter : . -> channel(ERROR);

mode STRING_MODE;
StringLiteralPart
    : (';'? StringLiteralCharacter+) {this.AppendInitialStringChunk();} (SingleLineCommentAtom {this.ProcessStringTrivia();})?;
StringModeTerminator
    : ('"' | '\'' | LineBreak) {this.MaybeEndStringMode();} -> skip;
StringModeTrivia
    : LineBreak Trivia {this.ProcessStringTrivia();} -> skip;
StringModeContinuationSection
    : SingleContinuationSection {this.ProcessContinuationSection();};

mode HOTSTRING_MODE;
HotstringOpenBrace
    : '{' {this.ProcessHotstringOpenBrace();} -> type(OpenBrace), popMode;
HotstringExpansion: . {this.BeginStringMode(); this.Rewind();} -> skip, popMode, pushMode(STRING_MODE);

mode PREPROCESSOR_DIRECTIVE_MODE;
PreprocessorDirectiveWS: WhiteSpace              -> type(WS), channel(HIDDEN);
Digits                 : [0-9]+                  -> channel(DIRECTIVE);
DirectiveTrue          : 'true'                  -> channel(DIRECTIVE), type(True);
DirectiveFalse         : 'false'                 -> channel(DIRECTIVE), type(False);
// Positional Directives
HotIf                  : 'hotif'                 -> mode(DEFAULT_MODE);
InputLevel             : 'inputlevel'            -> mode(DEFAULT_MODE);
SuspendExempt          : 'suspendexempt'         -> mode(DEFAULT_MODE);
UseHook                : 'usehook'               -> mode(DEFAULT_MODE);
Hotstring              : 'hotstring'             -> mode(DEFAULT_MODE), pushMode(HOTSTRING_OPTIONS);
Module                 : 'module'                -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
// General directives
Define                 : 'define'                -> channel(DIRECTIVE);
Undef                  : 'undef'                 -> channel(DIRECTIVE);
DirectiveIf            : 'if'                    -> channel(DIRECTIVE), type(If);
ElIf                   : 'elif'                  -> channel(DIRECTIVE);
DirectiveElse          : 'else'                  -> channel(DIRECTIVE), type(Else);
EndIf                  : 'endif'                 -> channel(DIRECTIVE);
DirectiveLine          : 'line'                  -> channel(DIRECTIVE);
Error                  : 'error'                 -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
Warning                : 'warning'               -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
Region                 : 'region'                -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
EndRegion              : 'endregion'             -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
Pragma                 : 'pragma'                -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
Nullable               : 'nullable'              -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
Include                : 'include'               -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
IncludeAgain           : 'includeagain'          -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
DllLoad                : 'dllload'               -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
Requires               : 'requires'              -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
SingleInstance         : 'singleinstance'        -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
Persistent             : 'persistent'            -> channel(DIRECTIVE);
Warn                   : 'warn'                  -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
HookMutexName          : 'hookmutexname'         -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
NoDynamicVars          : 'nodynamicvars'         -> channel(DIRECTIVE);
ErrorStdOut            : 'errorstdout'           -> channel(DIRECTIVE);
ClipboardTimeout       : 'clipboardtimeout'      -> channel(DIRECTIVE);
HotIfTimeout           : 'hotiftimeout'          -> channel(DIRECTIVE);
MaxThreads             : 'maxthreads'            -> channel(DIRECTIVE);
MaxThreadsBuffer       : 'maxthreadsbuffer'      -> channel(DIRECTIVE);
MaxThreadsPerHotkey    : 'maxthreadsperhotkey'   -> channel(DIRECTIVE);
WinActivateForce       : 'winactivateforce'      -> channel(DIRECTIVE);
NoMainWindow           : 'nomainwindow'          -> channel(DIRECTIVE);
NoTrayIcon             : 'notrayicon'            -> channel(DIRECTIVE);
Assembly               : 'assembly' ('title'
                                    | 'description'
                                    | 'configuration'
                                    | 'company'
                                    | 'product'
                                    | 'copyright'
                                    | 'trademark'
                                    | 'version') -> channel(DIRECTIVE), pushMode(DIRECTIVE_TEXT);
DirectiveDefault       : 'default'               -> channel(DIRECTIVE), type(Default);
DirectiveHidden        : 'hidden'                -> channel(DIRECTIVE);
DirectiveOpenParen     : '('                     -> channel(DIRECTIVE), type(OpenParen);
DirectiveCloseParen    : ')'                     -> channel(DIRECTIVE), type(CloseParen);
DirectiveNot           : '!'                     -> channel(DIRECTIVE), type(Not);
DirectiveEquals        : '=='                    -> channel(DIRECTIVE), type(IdentityEquals);
DirectiveNotEquals     : '!='                    -> channel(DIRECTIVE), type(NotEquals);
DirectiveAnd           : '&&'                    -> channel(DIRECTIVE), type(And);
DirectiveOr            : '||'                    -> channel(DIRECTIVE), type(Or);
ConditionalSymbol: Identifier -> channel(DIRECTIVE);
DirectiveSingleLineComment: SingleLineCommentAtom -> skip;
DirectiveNewline: LineBreak WhiteSpace? -> channel(DIRECTIVE), mode(DEFAULT_MODE);
UnexpectedDirectiveCharacter : . -> channel(ERROR);

mode DIRECTIVE_TEXT;
DirectiveTextWhitespace : WhiteSpace -> skip;
DirectiveQuotedStringLiteral
    : ('"' | '\'') {this.BeginStringMode((char)_input.LA(-1));} -> channel(DIRECTIVE), type(StringLiteral), pushMode(STRING_MODE);
DirectiveUnquotedStringLiteral
    : NonWSEOLCharacter {this.BeginStringMode(); this.AppendInitialStringChunk();} -> channel(DIRECTIVE), type(StringLiteral), pushMode(STRING_MODE);
UnexpectedTextDirectiveCharacter : . {this.Rewind();} -> skip, popMode;

mode HOTSTRING_OPTIONS;
HotstringWhitespace : WhiteSpace    -> type(WS), channel(HIDDEN);
HotstringNewline    : LineBreak     -> popMode;
NoMouse : 'NoMouse'                 -> mode(DEFAULT_MODE);
EndChars : 'EndChars';
HotstringOptions: . {this.BeginStringMode(); this.Rewind();} -> skip, pushMode(STRING_MODE);

// Fragment rules

fragment HotstringTriggerPart:
    Colon Options? Colon Trigger DoubleColon;

fragment HotstringOptionCharacter
    : [*?bcortsxz] ' '* '0'?
    | 'c1' 
    | 'si' | 'sp' | 'se' 
    | 'p' [0-9]+ 
    | 'k' ' '* ('-' ' '*)? [0-9]+
    ;
fragment Options:
    HotstringOptionCharacter+;
fragment Trigger:
    (':'? NonColonStringCharacter | ';' {!this.IsCommentPossible()}?)+;

fragment WSCharacter: [\t\u000B\u000C\u0020\u00A0];

fragment NonWSCharacter: ~[\t\u000B\u000C\u0020\u00A0];

fragment WhiteSpace: WSCharacter+;

fragment DQStringCharacter
    : ~["`\t\n\r\u2028\u2029\f ] ';'
    | ~[";`\r\n\u2028\u2029]
    | '`' EscapeSequence ';'?
    ;

fragment SQStringCharacter
    : ~['`\t\n\r\u2028\u2029\f ] ';'
    | ~[';`\r\n\u2028\u2029]
    | '`' EscapeSequence ';'?
    ;

fragment NonColonStringCharacter: ~[;:`\r\n\u2028\u2029] | '`' EscapeSequence;

fragment StringLiteralCharacter
    : ~[`'"\t\n\r\u2028\u2029\f ] ';'         // Match semicolon only if not preceded by whitespace
    | ~[;'"`\r\n\u2028\u2029]                 // Match any character except semicolon, newline, or carriage return
    | '`' EscapeSequence ';'?       // Match escape sequences starting with backtick
    | ('\'' | '"') {!this.IsCurrentStringQuote()}?
    ;

fragment RawStringCharacter
    : ~[`\t\n\r\u2028\u2029\f ] ';'         // Match semicolon only if not preceded by whitespace
    | ~[;`\r\n\u2028\u2029]                 // Match any character except semicolon, newline, or carriage return
    | '`' EscapeSequence ';'?       // Match escape sequences starting with backtick
    ;

fragment AnyCharacter: .;

fragment RawString: RawStringCharacter+;

fragment SingleLineCommentAtom: ';' {this.IsCommentPossible()}? NonEOLCharacter*;

fragment EscapeSequence:
    CharacterEscapeSequence
    | '0' // no digit ahead! TODO
    | HexEscapeSequence
    | UnicodeEscapeSequence
    | ExtendedUnicodeEscapeSequence
;

fragment CharacterEscapeSequence: SingleEscapeCharacter | NonEscapeCharacter;

fragment HexEscapeSequence: 'x' HexDigit HexDigit;

fragment UnicodeEscapeSequence:
    'u' HexDigit HexDigit HexDigit HexDigit
    | 'u' '{' HexDigit HexDigit+ '}'
;

fragment ExtendedUnicodeEscapeSequence: 'u' '{' HexDigit+ '}';

fragment SingleEscapeCharacter: [`;:'"bfnrtvsa];

fragment NonEscapeCharacter: ~[`;:'"bfnrtvsa0-9xu\r\n\u2028\u2029];

fragment EscapeCharacter: SingleEscapeCharacter | [0-9] | [xu];

fragment EOLCharacter: [\r\n\u2028\u2029];

fragment NonEOLCharacter: ~[\r\n\u2028\u2029];

fragment NonWSEOLCharacter: ~[\t\u000B\u000C\u0020\u00A0\r\n\u2028\u2029];

fragment LineBreak: EOLCharacter+;

fragment HexDigit: [_0-9a-f];

fragment DecimalIntegerLiteral: '0' | [1-9] [0-9_]*;

fragment IntegerLiteral: Minus? DecimalIntegerLiteral;

fragment ExponentPart: 'e' [+-]? [0-9_]+;

fragment IdentifierPart: IdentifierStart | [\p{Mn}] | [\p{Nd}] | [\p{Pc}] |  [\p{Cf}];

fragment IdentifierStart: [\p{L}] | [$_] | '\\' UnicodeEscapeSequence;

fragment SingleContinuationSection: LineBreak WhiteSpace? ContinuationSectionBody;

fragment ContinuationSectionBody: '(' WhiteSpace? (ContinuationSectionOption (WhiteSpace ContinuationSectionOption)*)? SingleLineCommentAtom? EOLCharacter .*? LineBreak WhiteSpace? ')' WhiteSpace?;

fragment Trivia: (WhiteSpace | SingleLineCommentAtom | '/*' .*? '*/')+;

fragment ContinuationSectionOption: 'join' ~[\r\n\u2028\u2029)]* | 'ltrim' '0'? | 'rtrim' '0'? | 'c' | 'com' | 'comment' | 'comments' | '`';

fragment HotkeyModifierKey: [#!^+<>];

fragment HotkeyModifier: HotkeyModifierKey | [*~$];

fragment HotkeyCharacter 
    : 'NumpadEnter'
    | 'Delete'
    | 'Del'
    | 'Insert'
    | 'Ins'
    | 'Clear'
    | 'Up'
    | 'Down'
    | 'Left'
    | 'Right'
    | 'Home'
    | 'End'
    | 'PgUp'
    | 'PgDn'
    | 'Numpad0'
    | 'Numpad1'
    | 'Numpad2'
    | 'Numpad3'
    | 'Numpad4'
    | 'Numpad5'
    | 'Numpad6'
    | 'Numpad7'
    | 'Numpad8'
    | 'Numpad9'
    | 'NumpadMult'
    | 'NumpadDiv'
    | 'NumpadAdd'
    | 'NumpadSub'
    | 'NumpadDot'
    | 'Numlock'
    | 'ScrollLock'
    | 'CapsLock'
    | 'Escape'
    | 'Esc'
    | 'Tab'
    | 'Space'
    | 'Backspace'
    | 'BS'
    | 'Enter'
    | 'NumpadDel'
    | 'NumpadIns'
    | 'NumpadClear'
    | 'NumpadUp'
    | 'NumpadDown'
    | 'NumpadLeft'
    | 'NumpadRight'
    | 'NumpadHome'
    | 'NumpadEnd'
    | 'NumpadPgUp'
    | 'NumpadPgDn'
    | 'PrintScreen'
    | 'CtrlBreak'
    | 'Pause'
    | 'Help'
    | 'Sleep'
    | 'AppsKey'
    | 'LControl'
    | 'RControl'
    | 'LCtrl'
    | 'RCtrl'
    | 'LShift'
    | 'RShift'
    | 'LAlt'
    | 'RAlt'
    | 'LWin'
    | 'RWin'
    | 'Control'
    | 'Ctrl'
    | 'Alt'
    | 'Shift'
    | 'F1'
    | 'F2'
    | 'F3'
    | 'F4'
    | 'F5'
    | 'F6'
    | 'F7'
    | 'F8'
    | 'F9'
    | 'F10'
    | 'F11'
    | 'F12'
    | 'F13'
    | 'F14'
    | 'F15'
    | 'F16'
    | 'F17'
    | 'F18'
    | 'F19'
    | 'F20'
    | 'F21'
    | 'F22'
    | 'F23'
    | 'F24'
    | 'LButton'
    | 'RButton'
    | 'MButton'
    | 'XButton1'
    | 'XButton2'
    | 'WheelDown'
    | 'WheelUp'
    | 'WheelLeft'
    | 'WheelRight'
    | 'Browser_Back'
    | 'Browser_Forward'
    | 'Browser_Refresh'
    | 'Browser_Stop'
    | 'Browser_Search'
    | 'Browser_Favorites'
    | 'Browser_Home'
    | 'Volume_Mute'
    | 'Volume_Down'
    | 'Volume_Up'
    | 'Media_Next'
    | 'Media_Prev'
    | 'Media_Stop'
    | 'Media_Play_Pause'
    | 'Launch_Mail'
    | 'Launch_Media'
    | 'Launch_App1'
    | 'Launch_App2'
    | 'AltTab'
    | 'ShiftAltTab'
    | ~[`\r\n\u2028\u2029 ]   // Match any character except semicolon, newline, carriage return, or whitespace
    | '`' EscapeSequence?       // Match escape sequences starting with backtick (or just backtick)
    ;

fragment HotkeyCombinatorCharacter: '&';

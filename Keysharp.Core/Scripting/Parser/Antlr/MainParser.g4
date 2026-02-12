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

parser grammar MainParser;

options {
    tokenVocab = MainLexer;
    superClass = MainParserBase;
    accessLevel = internal;
}

program
    : sourceElements EOF
    | EOF
    ;

sourceElements
    : (sourceElement eos
    | WS
    | EOL)+
    ;

sourceElement
    : classDeclaration
    | '#' positionalDirective
    | remap
    | hotstring
    | hotkey
    | importStatement
    | exportStatement
    | statement
    ;

// Non-positional directives are handled elsewhere, mainly in PreReader.cs
positionalDirective
    : HotIf singleExpression?                      # HotIfDirective
    | Hotstring 
        ( HotstringOptions 
        | NoMouse 
        | EndChars HotstringOptions )              # HotstringDirective
    | InputLevel numericLiteral?                   # InputLevelDirective
    | UseHook (numericLiteral | boolean)?          # UseHookDirective
    | SuspendExempt (numericLiteral | boolean)?    # SuspendExemptDirective
    ;

remap
    : RemapKey
    ;

hotstring
    : HotstringTrigger (EOL HotstringTrigger)* WS* (hotstringExpansion | EOL? functionDeclaration | EOL? statement)
    ;

hotstringExpansion
    : HotstringSingleLineExpansion
    | HotstringMultiLineExpansion
    ;

hotkey
    : HotkeyTrigger (EOL HotkeyTrigger)* s* (functionDeclaration | statement)
    ;

statement
    : variableStatement
    | ifStatement
    | iterationStatement
    | continueStatement
    | breakStatement
    | returnStatement
    | yieldStatement
    | labelledStatement
    | gotoStatement
    | switchStatement
    | throwStatement
    | tryStatement
    | awaitStatement
    | deleteStatement
    | blockStatement
    | functionDeclaration
    | {this.isFunctionCallStatement()}? functionStatement
    | {!this.next(OpenBrace) && !this.nextIsStatementKeyword()}? expressionStatement
    ;

blockStatement
    : block
    ;

block
    : '{' s* statementList? '}'
    ;

// Only to be used inside of a block, cannot meet EOF
statementList
    : (sourceElement EOL)+
    ;

variableStatement
    : (Global | Local | Static) (WS* variableDeclarationList)?
    ;

awaitStatement
    : Await WS* singleExpression
    ;
    
deleteStatement
    : Delete WS* singleExpression
    ;

importStatement
    : Import WS* importClause
    | (Export WS*)? Import WS* importModule (WS* importList)?
    ;

importClause
    : importWildcardFrom
    | importNamedFrom
    ;

importModule
    : moduleName (WS* As WS* identifierName)?
    ;

importWildcardFrom
    : Multiply WS* From WS* moduleName
    ;

importNamedFrom
    : importList s* From WS* moduleName
    ;

importList
    : '{' s* importSpecifierList? s* '}'
    ;

importSpecifierList
    : importSpecifier (WS* ',' importSpecifier)* (WS* ',')?
    ;

importSpecifier
    : identifierName (s* As s* identifierName)?
    ;

moduleName
    : identifierName
    | StringLiteral
    ;

exportStatement
    : Export WS* exportClause
    ;

exportClause
    : Default WS* declaration
    | exportNamed
    ;

exportNamed
    : declaration
    | variableDeclarationList
    ;

declaration
    : classDeclaration
    | functionDeclaration
    ;

variableDeclarationList
    : variableDeclaration (WS* ',' variableDeclaration)*
    ;

variableDeclaration
    : assignable (assignmentOperator singleExpression | op = ('++' | '--'))?
    ;

functionStatement
    : primaryExpression (WS+ arguments)?
    ;

// isFunctionCallStatement does some manual parsing to determine whether this is a functionStatement or
// expressionStatement. A functionStatement can begin with an identifier, property/index access, dereference.
// This could actually be omitted because ANTLR is smart enough to figure out which is which, but it can lead to
// some serious performance issues because of long lookaheads.
expressionStatement
    : expressionSequence
    ;

// For maximum performance there should be two separate statement rules, one with possible
// dangling `else` and one without. That would require duplicating all flow rules though, so
// currently it's not done.
ifStatement
    : If s* singleExpression WS* flowBlock elseProduction
    ;

flowBlock
    : EOL+ statement
    | block // OTB block, other one is handled with statement
    ;

untilProduction
    : EOL Until s* singleExpression 
    | {!this.second(Until)}?
    ;

elseProduction
    : EOL Else s* statement 
    | {!this.second(Else)}? // This can be used to reduce the ambiguity in SLL mode, but has a negative effect on performance
    ;

iterationStatement
    : Loop type = (Files | Read | Reg | Parse) WS* singleExpression (WS* ',' singleExpression?)* WS* flowBlock untilProduction elseProduction  # SpecializedLoopStatement
    | {this.isValidLoopExpression()}? Loop WS* (singleExpression WS*)? flowBlock untilProduction elseProduction      # LoopStatement
    | While WS* singleExpression WS* flowBlock untilProduction elseProduction       # WhileStatement
    | For WS* forInParameters WS* flowBlock untilProduction elseProduction          # ForInStatement
    ;

forInParameters
    : assignable? (WS* ',' assignable?)* WS* In WS* singleExpression
    | '(' assignable? (WS* ',' assignable?)* (WS | EOL)* In (WS | EOL)* singleExpression ')'
    ;

continueStatement
    : Continue WS* (propertyName | '(' propertyName ')')?
    ;

breakStatement
    : Break WS* ('(' propertyName ')' | propertyName)?
    ;

returnStatement
    : Return WS* singleExpression?
    ;

yieldStatement
    : Yield WS* singleExpression?
    ;

switchStatement
    : Switch WS* singleExpression? (WS* ',' literal)? s* caseBlock
    ;

caseBlock
    : '{' s* caseClause* '}'
    ;

caseClause
    : (Case WS* expressionSequence | Default) WS* ':' s* statementList?
    ;

labelledStatement
    : {this.isValidLabel()}? identifier ':'
    ;

gotoStatement
    : Goto WS* propertyName
    | Goto '(' WS* singleExpression WS* ')'
    ;

throwStatement
    : Throw WS* singleExpression?
    ;

tryStatement
    : Try s* statement catchProduction* elseProduction finallyProduction
    ;

catchProduction
    : EOL Catch WS* (catchAssignable WS*)? flowBlock
    ;

catchAssignable
    : catchClasses (WS* As)? (WS* identifier)?
    | '(' catchClasses (WS* As)? (WS* identifier)? ')'
    | (WS* As) (WS* identifier)
    | '(' (WS* As) (WS* identifier) ')'
    ;

catchClasses
    : identifier (WS* ',' identifier)*
    ;

finallyProduction
    : EOL Finally s* statement 
    | {!this.second(Finally)}?
    ;

functionDeclaration
    : functionHead functionBody
    ;

classDeclaration
    : Class WS* identifier (WS+ Extends WS+ classExtensionName)? s* classTail
    ;

classExtensionName
    : identifier ('.' identifier)*
    ;

classTail
    : '{' (classElement EOL | EOL)* '}'
    ;

classElement
    : functionDeclaration                                         # ClassMethodDeclaration
    | (Static WS*)? propertyDefinition                            # ClassPropertyDeclaration
    | (Static WS*)? fieldDefinition (WS* ',' fieldDefinition)*    # ClassFieldDeclaration
    | classDeclaration                                            # NestedClassDeclaration
    ;

propertyDefinition
    : classPropertyName '=>' singleExpression
    | classPropertyName s* '{' (propertyGetterDefinition EOL | propertySetterDefinition EOL | EOL)+ '}'
    ;

classPropertyName
    : propertyName
    | propertyName '[' formalParameterList? ']'
    ;

propertyGetterDefinition
    : Get functionBody
    ;

propertySetterDefinition
    : Set functionBody
    ;

fieldDefinition
    : (propertyName ('.' propertyName)*) ':=' singleExpression
    ;

formalParameterList
    : (formalParameterArg WS* ',')* lastFormalParameterArg
    ;

formalParameterArg
    : BitAnd? identifier (':=' singleExpression | QuestionMark)? // expression instead of singleExpression because it's always enclosed in parenthesis and thus function expressions can be unambiguously parsed
    ;

lastFormalParameterArg
    : formalParameterArg
    | identifier? Multiply
    ;

arrayLiteral
    : '[' arguments? ']'
    ;

// Allow [expr1:expr2, expr3:expr4] to create a Map
mapLiteral
    : '[' mapElementList ']'
    ;

mapElementList
    : (WS* ',')* mapElement (WS* ',' mapElement?)*
    ;

mapElement
    : key = singleExpression ':' value = singleExpression
    ;

propertyAssignment
    : memberIdentifier (WS | EOL)* ':' (WS | EOL)* singleExpression                           # PropertyExpressionAssignment
    // These might be implemented at some point in the future
    //| functionHeadPrefix? '*'? propertyName '(' formalParameterList? ')' functionBody # FunctionProperty
    //| getter '(' ')' functionBody                                        # PropertyGetter
    //| setter '(' formalParameterArg ')' functionBody                     # PropertySetter
    //| Ellipsis? singleExpression                                         # PropertyShorthand
    ;

propertyName
    : identifier
    | reservedWord
    | numericLiteral identifier?
    ;

dereference
    : DerefStart singleExpression DerefEnd
    ;

arguments
    : argument (WS* ',' argument?)*
    | (WS* ',' argument?)+
    ;

argument
    : singleExpression (Multiply | QuestionMark)?
    ;

expressionSequence
    : singleExpression (WS* ',' singleExpression)*
    ;

memberIndexArguments
    : '[' arguments? ']'
    ;

singleExpression
    : left = singleExpression op = ('++' | '--')                                               # PostIncrementDecrementExpression
    | op = ('--' | '++') right = singleExpression                                              # PreIncrementDecrementExpression
    | <assoc = right> left = singleExpression op = '**' right = singleExpression               # PowerExpression
    | op = ('-' | '+' | '!' | '~') right = singleExpression                                    # UnaryExpression
    | left = singleExpression (op = ('*' | '/' | '//') (WS | EOL)*) right = singleExpression   # MultiplicativeExpression
    | left = singleExpression ((WS | EOL)* op = ('+' | '-')) right = singleExpression          # AdditiveExpression
    | left = singleExpression op = ('<<' | '>>' | '>>>') right = singleExpression              # BitShiftExpression
    | left = singleExpression ((WS | EOL)* op = '&' (WS | EOL)*) right = singleExpression      # BitAndExpression
    | left = singleExpression op = '^' right = singleExpression                                # BitXOrExpression
    | left = singleExpression op = '|' right = singleExpression                                # BitOrExpression
    | left = singleExpression (ConcatDot | {this.wsConcatAllowed()}? WS+) right = singleExpression                        # ConcatenateExpression
    | left = singleExpression op = '~=' right = singleExpression                               # RegExMatchExpression
    | left = singleExpression op = ('<' | '>' | '<=' | '>=') right = singleExpression          # RelationalExpression
    | left = singleExpression op = ('=' | '!=' | '==' | '!==') right = singleExpression        # EqualityExpression
    | left = singleExpression ((WS | EOL)* op = (Instanceof | Is | In | Contains) (WS | EOL)*) right = primaryExpression  # ContainExpression
    | op = VerbalNot WS* right = singleExpression                                    # VerbalNotExpression
    | left = singleExpression (op = '&&' | op = VerbalAnd) right = singleExpression  # LogicalAndExpression
    | left = singleExpression (op = '||' | op = VerbalOr) right = singleExpression   # LogicalOrExpression
    | <assoc = right> left = singleExpression op = '??' right = singleExpression                               # CoalesceExpression
    | <assoc = right> ternCond = singleExpression '?' ternTrue = singleExpression (WS | EOL)* ':' (WS | EOL)* ternFalse = singleExpression # TernaryExpression
    | <assoc = right> left = primaryExpression op = assignmentOperator right = singleExpression          # AssignmentExpression
    | {!this.isStatementStart()}? fatArrowExpressionHead '=>' singleExpression  # FatArrowExpression // Not sure why, but this needs to be lower than coalesce expression
    | {this.isFuncExprAllowed()}? functionExpressionHead (WS | EOL)* block      # FunctionExpression
    | primaryExpression                                                         # SingleExpressionDummy
    ;

primaryExpression
    : primaryExpression accessSuffix                                       # AccessExpression
    | BitAnd primaryExpression                                             # VarRefExpression
    | identifier                                                           # IdentifierExpression
    | dynamicIdentifier                                                    # DynamicIdentifierExpression
    | literal                                                              # LiteralExpression
    | arrayLiteral                                                         # ArrayLiteralExpression
    | mapLiteral                                                           # MapLiteralExpression
    | objectLiteral                                                        # ObjectLiteralExpression
    | '(' expressionSequence ')'                                           # ParenthesizedExpression
    ;

accessSuffix
    : modifier = ('.' | '?.') memberIdentifier memberIndexArguments?
    | (modifier = '?.')? (memberIndexArguments | '(' arguments? ')')
    | modifier = '?'
    ;

memberIdentifier
    : propertyName
    | dynamicIdentifier
    ;

// A combination of identifiers and derefs, such as `a%b%`
dynamicIdentifier
    : propertyName dereference (propertyName | dereference)*
    | dereference (propertyName | dereference)*
    ;

initializer
    : ':=' singleExpression
    ;

assignable
    : identifier
    ;

objectLiteral
    : '{' s* (propertyAssignment (WS* ',' propertyAssignment?)* s*)? '}'
    ;

functionHead
    : functionHeadPrefix? identifierName '(' formalParameterList? ')'
    ;

functionHeadPrefix
    : ((Async | Static) WS*)+
    ;

functionExpressionHead
    : identifierName? '(' formalParameterList? ')'
    ;

fatArrowExpressionHead
    : identifierName
    | functionExpressionHead
    ;

functionBody
    : '=>' singleExpression
    | (WS | EOL)* block
    ;

assignmentOperator
    : (':='
    | '%='
    | '+='
    | '-='
    | '*='
    | '/='
    | '//='
    | '.='
    | '|='
    | '&='
    | '^='
    | '>>='
    | '<<='
    | '>>>='
    | '**='
    | '??=')
    ;

literal
    : boolean
    | numericLiteral
    | bigintLiteral
    | (NullLiteral
    | Unset
    | StringLiteral)
    ;

boolean : 
    (True
    | False);

numericLiteral
    : (DecimalLiteral
    | HexIntegerLiteral
    | OctalIntegerLiteral
    | OctalIntegerLiteral2
    | BinaryIntegerLiteral)
    ;

bigintLiteral
    : (BigDecimalIntegerLiteral
    | BigHexIntegerLiteral
    | BigOctalIntegerLiteral
    | BigBinaryIntegerLiteral)
    ;

getter
    : Get propertyName
    ;

setter
    : Set propertyName
    ;

identifierName
    : identifier
    | reservedWord
    ;

identifier
    : (Identifier
    | Default
    | This
//    | Class // AHK v2.1 actually allows assignment like `class := 1`, but it seems unwise
    | Enum
    | Extends
    | Super
    | Base
    | From
    | Get
    | Set
    | As
    | Class
    | Do
    | NullLiteral
    | Parse
    | Reg
    | Read
    | Files
    | Throw
    | Yield  
    | Async
    | Await
    | Import
    | Export
    | Delete)
    ;

// None of these can be used as a variable name
reservedWord
    : keyword
    | Unset
    | boolean
    ;

keyword
    : (Local
    | Global
    | Static
    | If
    | Else
    | Loop
    | For
    | While
    | Until
    | Break
    | Continue
    | Goto
    | Return
    | Switch
    | Case
    | Try
    | Catch
    | Finally
    | Throw
    | As
    | VerbalAnd
    | VerbalOr
    | VerbalNot
    | Contains
    | In
    | Is
    | Super
    | Unset
    | Instanceof)
    ;

s
    : WS
    | EOL
    ;

eos
    : EOF
    | EOL
    ;

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MiniJavaCompiler.LexicalAnalysis;
using MiniJavaCompiler.Support.TokenTypes;
using MiniJavaCompiler.AbstractSyntaxTree;

namespace MiniJavaCompiler
{
    namespace SyntaxAnalysis
    {
        public class SyntaxError : Exception
        {
            public SyntaxError(string message)
                : base(message) { }
        }

        public class LexicalErrorEncountered : Exception
        {
            public LexicalErrorEncountered() { }
        }

        public class BackEndError : Exception
        {
            public List<String> ErrorMsgs
            {
                get;
                private set;
            }

            public BackEndError(List<String> messages)
            {
                ErrorMsgs = messages;
            }
        }

        public class Parser
        {
            private Scanner scanner;
            private Stack<Token> inputBuffer;
            public List<String> errorMessages
            {
                get;
                set;
            }
            Token InputToken
            {
                get;
                set;
            }

            // "Pushes" an already consumed token back into the input.
            private void buffer(Token token)
            {
                inputBuffer.Push(InputToken);
                InputToken = token;
            }

            private void reportError(string errorMsg)
            {
                errorMessages.Add(errorMsg);
            }

            public Parser(Scanner scanner)
            {
                this.scanner = scanner;
                this.inputBuffer = new Stack<Token>();
                errorMessages = new List<String>();
                InputToken = scanner.NextToken();
            }

            public Program Parse()
            {
                return Program();
            }

            public Program Program()
            {
                var main = MainClass();
                var declarations = ClassDeclarationList();
                try
                {
                    Match<EOF>();
                    ReportErrors();
                    return new Program(main, declarations);
                }
                catch (SyntaxError e)
                {
                    errorMessages.Add(e.Message);
                    throw new BackEndError(errorMessages);
                }
            }

            private void ReportErrors()
            {
                if (errorMessages.Count > 0)
                    throw new BackEndError(errorMessages);
                return;
            }

            public MainClassDeclaration MainClass()
            {
                try
                {
                    Token startToken = Match<KeywordToken>("class");
                    Identifier classIdent = Match<Identifier>();
                    Match<LeftCurlyBrace>();
                    Match<KeywordToken>("public");
                    Match<KeywordToken>("static");
                    Match<MiniJavaType>("void");
                    Match<KeywordToken>("main");

                    Match<LeftParenthesis>();
                    Match<RightParenthesis>();

                    Match<LeftCurlyBrace>();
                    List<Statement> main_statements = StatementList();
                    Match<RightCurlyBrace>();

                    Match<RightCurlyBrace>();
                    return new MainClassDeclaration(classIdent.Value,
                        main_statements, startToken.Row, startToken.Col);
                }
                catch (SyntaxError e)
                {
                    reportError(e.Message);
                    RecoverFromClassMatching();
                    return null;
                }
                catch (LexicalErrorEncountered)
                {
                    RecoverFromClassMatching();
                    return null;
                }
            }

            private void RecoverFromClassMatching()
            {
                while (!(MatchWithoutConsuming<EOF>() || MatchWithoutConsuming<KeywordToken>("class")))
                    Consume<Token>();
            }

            public Statement Statement()
            {
                try
                {
                    if (InputToken is MiniJavaType)
                        return VariableDeclaration();
                    else if (InputToken is KeywordToken)
                        return MakeKeywordStatement();
                    else if (InputToken is LeftCurlyBrace)
                        return MakeBlockStatement();
                    else // Can be an assignment, a method invocation or a variable
                        // declaration for a user defined type.
                        return MakeExpressionStatementOrVariableDeclaration();
                }
                catch (SyntaxError e)
                {
                    reportError(e.Message);
                    return RecoverFromStatementMatching();
                }
                catch (LexicalErrorEncountered)
                {
                    return RecoverFromStatementMatching();
                }
            }

            private Statement RecoverFromStatementMatching()
            {
                while (!MatchWithoutConsuming<EOF>())
                {
                    var token = Consume<Token>();
                    if (token is EndLine)
                        break;
                }
                return null;
            }

            // This is a workaround method that is needed because the language is not LL(1).
            // Some buffering is done because several tokens must be peeked at to decide
            // which kind of statement should be parsed.
            private Statement MakeExpressionStatementOrVariableDeclaration()
            {
                Expression expression;
                if (InputToken is Identifier)
                { 
                    var ident = Consume<Identifier>();
                    if (MatchWithoutConsuming<LeftBracket>())
                    {
                        var lBracket = Consume<LeftBracket>();
                        if (MatchWithoutConsuming<RightBracket>())
                        { // The statement is a local array variable declaration.
                            Consume<RightBracket>();
                            return FinishParsingLocalVariableDeclaration(ident, true);
                        }
                        else
                        {   // Brackets are used to index into an array, beginning an expression.
                            // Buffer the tokens that were already consumed so the expression parser
                            // can match them again.
                            buffer(lBracket);
                            buffer(ident);
                            expression = Expression();
                        }
                    }
                    else if (MatchWithoutConsuming<Identifier>())
                        return FinishParsingLocalVariableDeclaration(ident, false);
                    else
                    { // The consumed identifier token is a reference to a variable
                      // and begins an expression.
                        buffer(ident);
                        expression = Expression();
                    }
                }
                else
                    expression = Expression();

                return CompleteStatement(expression);
            }

            private Statement CompleteStatement(Expression expression)
            {
                if (InputToken is AssignmentToken)
                    return MakeAssignmentStatement(expression);
                else
                    return MakeMethodInvocationStatement(expression);
            }

            private Statement MakeMethodInvocationStatement(Expression expression)
            {
                Match<EndLine>();
                if (expression is MethodInvocation)
                    return (MethodInvocation)expression;
                else
                    throw new SyntaxError("Expression of type " + expression.GetType().Name +
                        " cannot form a statement on its own.");
            }

            private Statement MakeAssignmentStatement(Expression lhs)
            {
                var assignment = Match<AssignmentToken>();
                Expression rhs = Expression();
                Match<EndLine>();
                return new AssignmentStatement(lhs, rhs,
                    assignment.Row, assignment.Col);
            }

            private Statement MakeBlockStatement()
            {
                Token blockStart = Match<LeftCurlyBrace>();
                var statements = StatementList();
                Match<RightCurlyBrace>();
                return new BlockStatement(statements, blockStart.Row, blockStart.Col);
            }

            private Statement MakeKeywordStatement()
            {
                KeywordToken token = (KeywordToken)InputToken;
                switch (token.Value)
                {
                    case "assert":
                        return MakeAssertStatement();
                    case "if":
                        return MakeIfStatement();
                    case "while":
                        return MakeWhileStatement();
                    case "System":
                        return MakePrintStatement();
                    case "return":
                        return MakeReturnStatement();
                    default:
                        throw new SyntaxError("Invalid keyword " + token.Value + " starting a statement.");
                }
            }

            private Statement MakeReturnStatement()
            {
                var returnToken = Consume<KeywordToken>();
                var expression = Expression();
                Match<EndLine>();
                return new ReturnStatement(expression,
                    returnToken.Row, returnToken.Col);
            }

            private Statement MakePrintStatement()
            {
                var systemToken = Consume<KeywordToken>();
                Match<MethodInvocationToken>();
                Match<KeywordToken>("out");
                Match<MethodInvocationToken>();
                Match<KeywordToken>("println");
                Match<LeftParenthesis>();
                var integerExpression = Expression();
                Match<RightParenthesis>();
                Match<EndLine>();
                return new PrintStatement(integerExpression,
                    systemToken.Row, systemToken.Col);
            }

            private Statement MakeWhileStatement()
            {
                var whileToken = Consume<KeywordToken>();
                Match<LeftParenthesis>();
                var booleanExpr = Expression();
                Match<RightParenthesis>();
                var whileBody = Statement();
                return new WhileStatement(booleanExpr, whileBody,
                    whileToken.Row, whileToken.Col);
            }

            private Statement MakeIfStatement()
            {
                var ifToken = Consume<KeywordToken>();
                Match<LeftParenthesis>();
                Expression booleanExpr = Expression();
                Match<RightParenthesis>();
                var thenBranch = Statement();
                var elseBranch = OptionalElseBranch();
                return new IfStatement(booleanExpr, thenBranch, elseBranch,
                    ifToken.Row, ifToken.Col);
            }

            private Statement MakeAssertStatement()
            {
                var assertToken = Consume<KeywordToken>();
                Match<LeftParenthesis>();
                Expression expr = Expression();
                Match<RightParenthesis>();
                Match<EndLine>(); // not in the original CFG, probably a bug?
                return new AssertStatement(expr, assertToken.Row, assertToken.Col);
            }

            private Statement FinishParsingLocalVariableDeclaration(Identifier variableTypeName, bool isArray)
            {
                var variableName = Match<Identifier>();
                Match<EndLine>();
                return new VariableDeclaration(variableName.Value, variableTypeName.Value, isArray,
                    variableTypeName.Row, variableTypeName.Col);
            }

            public Statement OptionalElseBranch()
            {
                if (InputToken is KeywordToken &&
                    ((KeywordToken)InputToken).Value == "else")
                {
                    Match<KeywordToken>("else");
                    return Statement();
                }
                else
                    return null;
            }

            public Expression Expression()
            {
                var expressionParser = new ExpressionParser(this);
                return expressionParser.parse();
            }

            public ClassDeclaration ClassDeclaration()
            {
                try
                {
                    Token startToken = Match<KeywordToken>("class");
                    Identifier classIdent = Match<Identifier>();
                    string inheritedClass = OptionalInheritance();
                    Match<LeftCurlyBrace>();
                    List<Declaration> declarations = DeclarationList();
                    Match<RightCurlyBrace>();
                    return new ClassDeclaration(classIdent.Value, inheritedClass,
                        declarations, startToken.Row, startToken.Col);
                }
                catch (SyntaxError e)
                {
                    reportError(e.Message);
                    RecoverFromClassMatching();
                    return null;
                }
                catch (LexicalErrorEncountered)
                {
                    RecoverFromClassMatching();
                    return null;
                }
            }

            public string OptionalInheritance()
            {
                if (!(InputToken is LeftCurlyBrace))
                {
                    Match<KeywordToken>("extends");
                    return Match<Identifier>().Value;
                }
                return null;
            }

            public Declaration Declaration()
            {
                try
                {
                    if (InputToken is MiniJavaType || InputToken is Identifier)
                    {
                        return VariableDeclaration();
                    }
                    else if (InputToken is KeywordToken)
                    {
                        return MethodDeclaration();
                    }
                    else
                        throw new SyntaxError("Invalid token of type " + InputToken.GetType().Name +
                            " starting a declaration.");
                }
                catch (SyntaxError e)
                {
                    reportError(e.Message);
                    return RecoverFromDeclarationMatching();
                }
                catch (LexicalErrorEncountered)
                {
                    return RecoverFromDeclarationMatching();
                }
            }

            private Declaration RecoverFromDeclarationMatching()
            {
                while (!MatchWithoutConsuming<EOF>())
                {
                    var token = Consume<Token>();
                    if (token is LeftCurlyBrace)
                        break;
                }
                return null;
            }

            public VariableDeclaration VariableDeclaration()
            {
                var variableDecl = VariableOrFormalParameterDeclaration();
                Match<EndLine>();
                return variableDecl;
            }

            private VariableDeclaration VariableOrFormalParameterDeclaration()
            {
                try
                {
                    var typeInfo = Type();
                    var type = (StringToken)typeInfo.Item1;
                    var variableIdent = Match<Identifier>();
                    return new VariableDeclaration(variableIdent.Value, type.Value,
                        typeInfo.Item2, type.Row, type.Col);
                }
                catch (SyntaxError e)
                {
                    reportError(e.Message);
                    return RecoverFromVariableDeclarationMatching();
                }
                catch (LexicalErrorEncountered)
                {
                    return RecoverFromVariableDeclarationMatching();
                }
            }

            private VariableDeclaration RecoverFromVariableDeclarationMatching()
            { // could be parameterised on follow set
                while (!(MatchWithoutConsuming<EOF>()
                    || MatchWithoutConsuming<EndLine>()
                    || MatchWithoutConsuming<ParameterSeparator>()
                    || MatchWithoutConsuming<RightParenthesis>()))
                    Consume<Token>();
                return null;
            }

            public MethodDeclaration MethodDeclaration()
            {
                Token startToken = Match<KeywordToken>("public");
                var typeInfo = Type();
                var type = (StringToken)typeInfo.Item1;
                Identifier methodName = Match<Identifier>();
                Match<LeftParenthesis>();
                List<VariableDeclaration> parameters = FormalParameters();
                Match<RightParenthesis>();
                Match<LeftCurlyBrace>();
                List<Statement> methodBody = StatementList();
                Match<RightCurlyBrace>();
                return new MethodDeclaration(methodName.Value, type.Value,
                    typeInfo.Item2, parameters, methodBody, startToken.Row,
                    startToken.Col);
            }

            // Returns a 2-tuple with the matched type token as the first element and
            // a bool value indicating whether the type is an array or not as the
            // second element.
            public Tuple<TypeToken, bool> Type()
            {
                var type = Match<TypeToken>();
                if (InputToken is LeftBracket)
                {
                    Match<LeftBracket>();
                    Match<RightBracket>();
                    return new Tuple<TypeToken, bool>(type, true);
                }
                return new Tuple<TypeToken, bool>(type, false);
            }

            // An internal parser that solves operator precedences in expressions.
            private class ExpressionParser
            {
                Parser Parent
                {
                    get;
                    set;
                }

                public ExpressionParser(Parser parent)
                {
                    Parent = parent;
                }

                public Expression parse()
                {
                    try
                    {
                        return ParseExpression();
                    }
                    catch (SyntaxError e)
                    {
                        Parent.reportError(e.Message);
                        return RecoverFromExpressionParsing();
                    }
                    catch (LexicalErrorEncountered)
                    {
                        return RecoverFromExpressionParsing();
                    }
                }

                private Expression RecoverFromExpressionParsing()
                {
                    while (!(Parent.MatchWithoutConsuming<EOF>()
                        || Parent.MatchWithoutConsuming<RightParenthesis>()
                        || Parent.MatchWithoutConsuming<EndLine>()))
                        Parent.Consume<Token>();
                    return null;
                }

                private Expression ParseExpression()
                {
                    var firstOp = OrOperand();
                    Func<bool> orMatcher =
                        () => Parent.MatchWithoutConsuming<BinaryOperatorToken>("||");
                    return BinaryOpTail<LogicalOp>(firstOp, orMatcher, OrOperand);
                }

                private Expression OrOperand()
                {
                    var firstOp = AndOperand();
                    Func<bool> andMatcher =
                        () => Parent.MatchWithoutConsuming<BinaryOperatorToken>("&&");
                    return BinaryOpTail<LogicalOp>(firstOp, andMatcher, AndOperand);
                }

                private Expression AndOperand()
                {
                    var firstOp = EqOperand();
                    Func<bool> eqMatcher =
                        () => Parent.MatchWithoutConsuming<BinaryOperatorToken>("==");
                    return BinaryOpTail<LogicalOp>(firstOp, eqMatcher, EqOperand);
                }

                private Expression EqOperand()
                {
                    var firstOp = NotEqOperand();
                    Func<bool> neqMatcher =
                        () => Parent.MatchWithoutConsuming<BinaryOperatorToken>("<") ||
                              Parent.MatchWithoutConsuming<BinaryOperatorToken>(">");
                    return BinaryOpTail<LogicalOp>(firstOp, neqMatcher, NotEqOperand);
                }

                private Expression NotEqOperand()
                {
                    var firstOp = AddOperand();
                    Func<bool> addMatcher =
                        () => Parent.MatchWithoutConsuming<BinaryOperatorToken>("+") ||
                              Parent.MatchWithoutConsuming<BinaryOperatorToken>("-");
                    return BinaryOpTail<ArithmeticOp>(firstOp, addMatcher, AddOperand);
                }

                private Expression AddOperand()
                {
                    var firstOp = MultOperand();
                    Func<bool> multMatcher =
                        () => Parent.MatchWithoutConsuming<BinaryOperatorToken>("*") ||
                              Parent.MatchWithoutConsuming<BinaryOperatorToken>("/") ||
                              Parent.MatchWithoutConsuming<BinaryOperatorToken>("%");
                    return BinaryOpTail<ArithmeticOp>(firstOp, multMatcher, MultOperand);
                }

                private Expression MultOperand()
                {
                    if (Parent.MatchWithoutConsuming<UnaryNotToken>())
                    {
                        var token = Parent.Consume<UnaryNotToken>();
                        var term = Term();
                        return new UnaryNot(term, token.Row, token.Col);
                    }
                    else
                        return Term();
                }

                private Expression BinaryOpTail<OperatorType>(Expression lhs,
                    Func<bool> matchOperator, Func<Expression> operandParser)
                    where OperatorType : BinaryOperator
                {
                    if (matchOperator())
                    {
                        var opToken = Parent.Consume<BinaryOperatorToken>();
                        var rhs = operandParser();
                        var operatorExp = (Expression)System.Activator.CreateInstance(
                            typeof(OperatorType), new Object[] { opToken.Value, lhs, rhs, opToken.Row, opToken.Col });
                        return BinaryOpTail<OperatorType>(operatorExp, matchOperator,
                            operandParser);
                    }
                    else
                        return lhs;
                }

                public Expression Term()
                {
                    try
                    {
                        if (Parent.InputToken is KeywordToken)
                            return MakeKeywordExpression();
                        else if (Parent.InputToken is Identifier)
                            return MakeVariableReferenceExpression();
                        else if (Parent.InputToken is IntegerLiteralToken)
                            return MakeIntegerLiteralExpression();
                        else if (Parent.InputToken is LeftParenthesis)
                            return MakeParenthesisedExpression();
                        else
                            throw new SyntaxError("Invalid start token of type " +
                                Parent.InputToken.GetType().Name + " for a term in an expression.");
                    }
                    catch (SyntaxError e)
                    {
                        Parent.reportError(e.Message);
                        return RecoverFromTermMatching();
                    }
                    catch (LexicalErrorEncountered)
                    {
                        return RecoverFromTermMatching();
                    }
                }

                private Expression RecoverFromTermMatching()
                { // could be parameterised on follow set
                    while (!(Parent.MatchWithoutConsuming<EOF>()
                        || Parent.MatchWithoutConsuming<RightParenthesis>()
                        || Parent.MatchWithoutConsuming<EndLine>()
                        || Parent.MatchWithoutConsuming<BinaryOperatorToken>()))
                        Parent.Consume<Token>();
                    return null;
                }

                private Expression MakeParenthesisedExpression()
                {
                    Parent.Consume<LeftParenthesis>();
                    Expression parenthesisedExpression = Parent.Expression();
                    Parent.Match<RightParenthesis>();
                    return OptionalTermTail(parenthesisedExpression);
                }

                private Expression MakeIntegerLiteralExpression()
                {
                    var token = Parent.Match<IntegerLiteralToken>();
                    return OptionalTermTail(new IntegerLiteral(token.Value,
                        token.Row, token.Col));
                }

                private Expression MakeVariableReferenceExpression()
                {
                    var identifier = Parent.Match<Identifier>();
                    return OptionalTermTail(new VariableReference(
                        identifier.Value, identifier.Row, identifier.Col));
                }

                private Expression MakeKeywordExpression()
                {
                    var token = (KeywordToken)Parent.InputToken;
                    switch (token.Value)
                    {
                        case "new":
                            return MakeInstanceCreationExpression();
                        case "this":
                            return MakeThisExpression();
                        case "true":
                            return MakeBooleanLiteral(true);
                        case "false":
                            return MakeBooleanLiteral(false);
                        default:
                            throw new SyntaxError("Invalid start token " + token.Value +
                                " for expression.");
                    }
                }

                private Expression MakeBooleanLiteral(bool value)
                {
                    var boolToken = Parent.Consume<KeywordToken>();
                    return OptionalTermTail(new BooleanLiteral(value,
                        boolToken.Row, boolToken.Col));
                }

                private Expression MakeThisExpression()
                {
                    var thisToken = Parent.Consume<KeywordToken>();
                    return OptionalTermTail(new ThisExpression(thisToken.Row,
                        thisToken.Col));
                }

                private Expression MakeInstanceCreationExpression()
                {
                    var newToken = Parent.Consume<KeywordToken>();
                    var typeInfo = NewType();
                    var type = (StringToken)typeInfo.Item1;
                    return OptionalTermTail(new InstanceCreation(type.Value,
                        newToken.Row, newToken.Col, typeInfo.Item2));
                }

                public Expression OptionalTermTail(Expression lhs)
                {
                    if (Parent.MatchWithoutConsuming<LeftBracket>())
                        return MakeArrayIndexingExpression(lhs);
                    else if (Parent.MatchWithoutConsuming<MethodInvocationToken>())
                        return MakeMethodInvocationExpression(lhs);
                    else
                        return lhs;
                }

                private Expression MakeMethodInvocationExpression(Expression methodOwner)
                {
                    Parent.Consume<MethodInvocationToken>();
                    if (Parent.InputToken is KeywordToken)
                        return MakeLengthMethodInvocation(methodOwner);
                    else
                        return MakeUserDefinedMethodInvocation(methodOwner);
                }

                private Expression MakeUserDefinedMethodInvocation(Expression methodOwner)
                {
                    var methodName = Parent.Match<Identifier>();
                    Parent.Match<LeftParenthesis>();
                    var parameters = Parent.ExpressionList();
                    Parent.Match<RightParenthesis>();
                    return OptionalTermTail(new MethodInvocation(methodOwner,
                        methodName.Value, parameters, methodName.Row, methodName.Col));
                }

                private Expression MakeLengthMethodInvocation(Expression methodOwner)
                {
                    var methodName = Parent.Match<KeywordToken>("length");
                    var parameters = new List<Expression>();
                    return OptionalTermTail(new MethodInvocation(methodOwner, methodName.Value,
                        parameters, methodName.Row, methodName.Col));
                }

                private Expression MakeArrayIndexingExpression(Expression lhs)
                {
                    var startToken = Parent.Match<LeftBracket>();
                    var indexExpression = Parent.Expression();
                    Parent.Match<RightBracket>();
                    return OptionalTermTail(new ArrayIndexExpression(lhs, indexExpression,
                        startToken.Row, startToken.Col));
                }

                public Tuple<TypeToken, Expression> NewType()
                {
                    var type = Parent.Match<TypeToken>();
                    if (type is MiniJavaType || !(Parent.InputToken is LeftParenthesis))
                    { // must be an array
                        Parent.Match<LeftBracket>();
                        var arraySize = Parent.Expression();
                        Parent.Match<RightBracket>();
                        return new Tuple<TypeToken, Expression>(type, arraySize);
                    }
                    else
                    {
                        Parent.Match<LeftParenthesis>();
                        Parent.Match<RightParenthesis>();
                        return new Tuple<TypeToken, Expression>(type, null);
                    }
                }
            }

            // Matcher functions.

            // Checks that the input token is of the expected type and matches the
            // expected value. If the input token matches, it is returned and
            // cast to the expected type. Otherwise an error is reported.
            private ExpectedType Match<ExpectedType>(string expectedValue = null)
                where ExpectedType : Token
            {
                if (MatchWithoutConsuming<ExpectedType>(expectedValue))
                    return Consume<ExpectedType>();
                else
                { // The token is consumed even when it does not match expectations.
                    var token = Consume<Token>();
                    if (token is ErrorToken)
                        throw new LexicalErrorEncountered();
                    else if (expectedValue == null)
                        throw new SyntaxError("Expected type " + typeof(ExpectedType).Name +
                            " but got " + token.GetType().Name + ".");
                    else
                        throw new SyntaxError("Expected value \"" + expectedValue + "\" but got " +
                            ((StringToken)token).Value + ".");
                }
            }

            // Consumes a token from input and returns it after casting to the
            // given type.
            //
            // This method should only be called when the input token's type
            // has already been verified. (Unless consuming tokens as type Token
            // for e.g. recovery purposes.)
            private TokenType Consume<TokenType>() where TokenType : Token
            {
                dynamic temp = GetTokenOrReportError<TokenType>();
                InputToken = inputBuffer.Count > 0 ? inputBuffer.Pop() : scanner.NextToken();
                return temp;
            }

            private dynamic GetTokenOrReportError<TokenType>() where TokenType : Token
            {
                if (InputToken is ErrorToken)
                { // Lexical errors are reported here, so no errors are left unreported
                  // when consuming tokens because of recovery.
                    var temp = (ErrorToken)InputToken;
                    reportError(temp.Message);
                    return temp;
                }
                else
                    return (TokenType)InputToken;
            }

            // Checks whether the input token matches the expected type and value or not.
            // Either returns a boolean value or reports an error if input token is an
            // error token.
            private bool MatchWithoutConsuming<ExpectedType>(string expectedValue = null)
                where ExpectedType : Token
            {
                if (InputToken is ExpectedType)
                {
                    if (expectedValue == null || ((StringToken)InputToken).Value == expectedValue)
                        return true;
                    else
                        return false;
                }
                else
                    return false;
            }

            // List parsers.

            private List<ClassDeclaration> ClassDeclarationList()
            {
                return NodeList<ClassDeclaration, EOF>(ClassDeclaration);
            }

            private List<Declaration> DeclarationList()
            {
                return NodeList<Declaration, RightCurlyBrace>(Declaration);
            }

            private List<Statement> StatementList()
            {
                return NodeList<Statement, RightCurlyBrace>(Statement);
            }

            private List<NodeType> NodeList<NodeType, FollowToken>(Func<NodeType> ParseNode)
                where NodeType : SyntaxTreeNode
                where FollowToken : Token
            {
                var nodeList = new List<NodeType>();
                if (!(InputToken is FollowToken))
                {
                    nodeList.Add(ParseNode());
                    nodeList.AddRange(NodeList<NodeType, FollowToken>(ParseNode));
                }
                return nodeList;
            }

            private List<VariableDeclaration> FormalParameters(bool isListTail = false)
            {
                return CommaSeparatedList<VariableDeclaration, RightParenthesis>(
                    VariableOrFormalParameterDeclaration);
            }

            private List<Expression> ExpressionList(bool isListTail = false)
            {
                return CommaSeparatedList<Expression, RightParenthesis>(Expression);
            }

            private List<NodeType> CommaSeparatedList<NodeType, FollowToken>
                (Func<NodeType> ParseNode, bool isListTail = false)
                where NodeType : SyntaxTreeNode
                where FollowToken : Token
            {
                var list = new List<NodeType>();
                if (!(InputToken is FollowToken))
                {
                    if (isListTail) Match<ParameterSeparator>();
                    list.Add(ParseNode());
                    list.AddRange(CommaSeparatedList<NodeType, FollowToken>(
                        ParseNode, true));
                }
                return list;
            }
        }
    }
}

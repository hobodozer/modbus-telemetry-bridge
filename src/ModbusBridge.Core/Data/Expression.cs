using System.Globalization;

namespace ModbusBridge.Core.Data;

/// <summary>
/// A tiny arithmetic expression compiler, so values that are a function of other tags are
/// configuration rather than code. Deliberately small: numbers, tag names, the usual operators,
/// and a handful of functions. No assignment, no side effects, nothing that can loop.
///
/// Parsed once and evaluated repeatedly - parsing per sample would dominate the cost.
/// </summary>
public sealed class Expression
{
    private readonly Node _root;

    /// <summary>Every tag name the expression reads. Used to decide the result's quality.</summary>
    public IReadOnlyList<string> References { get; }

    public string Source { get; }

    private Expression(Node root, IReadOnlyList<string> references, string source)
    {
        _root = root;
        References = references;
        Source = source;
    }

    /// <summary>Evaluates against a tag lookup. A missing tag reads as 0.</summary>
    public double Evaluate(Func<string, double> lookup) => _root.Evaluate(lookup);

    public static Expression Parse(string text)
    {
        var tokens = Tokenise(text);
        var parser = new Parser(tokens);
        var root = parser.ParseExpression(0);
        parser.ExpectEnd();
        return new Expression(root, parser.References, text);
    }

    public static bool TryParse(string text, out Expression? expression, out string? error)
    {
        try
        {
            expression = Parse(text);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            expression = null;
            error = ex.Message;
            return false;
        }
    }

    // ---- tokens ---------------------------------------------------------------------------

    private enum TokenKind { Number, Name, Operator, LeftParen, RightParen, Comma, End }

    private readonly record struct Token(TokenKind Kind, string Text, double Value);

    private static List<Token> Tokenise(string text)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                // Exponent form, so 1e-3 is usable for a scaling factor.
                if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
                {
                    i++;
                    if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
                    while (i < text.Length && char.IsDigit(text[i])) i++;
                }
                var slice = text[start..i];
                if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    throw new FormatException($"'{slice}' is not a number.");
                tokens.Add(new Token(TokenKind.Number, slice, value));
                continue;
            }

            // Tag names carry dots and underscores, e.g. plc1.di.estop.
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Name, text[start..i], 0));
                continue;
            }

            if (c == '(') { tokens.Add(new Token(TokenKind.LeftParen, "(", 0)); i++; continue; }
            if (c == ')') { tokens.Add(new Token(TokenKind.RightParen, ")", 0)); i++; continue; }
            if (c == ',') { tokens.Add(new Token(TokenKind.Comma, ",", 0)); i++; continue; }

            // Two-character operators first, so <= is not read as < then =.
            if (i + 1 < text.Length)
            {
                var pair = text.Substring(i, 2);
                if (pair is "<=" or ">=" or "==" or "!=" or "&&" or "||")
                {
                    tokens.Add(new Token(TokenKind.Operator, pair, 0));
                    i += 2;
                    continue;
                }
            }

            if ("+-*/%<>!".Contains(c))
            {
                tokens.Add(new Token(TokenKind.Operator, c.ToString(), 0));
                i++;
                continue;
            }

            throw new FormatException($"Unexpected character '{c}' at position {i}.");
        }

        tokens.Add(new Token(TokenKind.End, "", 0));
        return tokens;
    }

    // ---- nodes ----------------------------------------------------------------------------

    private abstract class Node
    {
        public abstract double Evaluate(Func<string, double> lookup);
    }

    private sealed class ConstantNode(double value) : Node
    {
        public override double Evaluate(Func<string, double> lookup) => value;
    }

    private sealed class TagNode(string name) : Node
    {
        public override double Evaluate(Func<string, double> lookup) => lookup(name);
    }

    private sealed class UnaryNode(string op, Node operand) : Node
    {
        public override double Evaluate(Func<string, double> lookup)
        {
            var value = operand.Evaluate(lookup);
            return op switch
            {
                "-" => -value,
                "!" => value == 0 ? 1 : 0,
                _ => value
            };
        }
    }

    private sealed class BinaryNode(string op, Node left, Node right) : Node
    {
        public override double Evaluate(Func<string, double> lookup)
        {
            var a = left.Evaluate(lookup);

            // Short-circuit, so "capacity > 0 && level / capacity > 0.5" cannot divide by zero.
            if (op == "&&") return a == 0 ? 0 : (right.Evaluate(lookup) != 0 ? 1 : 0);
            if (op == "||") return a != 0 ? 1 : (right.Evaluate(lookup) != 0 ? 1 : 0);

            var b = right.Evaluate(lookup);
            return op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                // Division by zero yields 0 rather than infinity: an HMI showing Inf is worse
                // than one showing nothing, and this is usually a not-yet-populated tag.
                "/" => b == 0 ? 0 : a / b,
                "%" => b == 0 ? 0 : a % b,
                "<" => a < b ? 1 : 0,
                "<=" => a <= b ? 1 : 0,
                ">" => a > b ? 1 : 0,
                ">=" => a >= b ? 1 : 0,
                "==" => a == b ? 1 : 0,
                "!=" => a != b ? 1 : 0,
                _ => throw new FormatException($"Unknown operator '{op}'.")
            };
        }
    }

    private sealed class CallNode(string name, List<Node> args) : Node
    {
        public override double Evaluate(Func<string, double> lookup)
        {
            Span<double> v = stackalloc double[args.Count];
            for (var i = 0; i < args.Count; i++) v[i] = args[i].Evaluate(lookup);

            return name switch
            {
                "abs" => Math.Abs(v[0]),
                "min" => Math.Min(v[0], v[1]),
                "max" => Math.Max(v[0], v[1]),
                "clamp" => Math.Clamp(v[0], v[1], v[2]),
                "round" => args.Count > 1 ? Math.Round(v[0], (int)v[1]) : Math.Round(v[0]),
                "floor" => Math.Floor(v[0]),
                "ceil" => Math.Ceiling(v[0]),
                "sqrt" => v[0] < 0 ? 0 : Math.Sqrt(v[0]),
                "if" => v[0] != 0 ? v[1] : v[2],
                _ => throw new FormatException($"Unknown function '{name}'.")
            };
        }
    }

    private static readonly Dictionary<string, int> Arity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["abs"] = 1, ["min"] = 2, ["max"] = 2, ["clamp"] = 3, ["round"] = -1,
        ["floor"] = 1, ["ceil"] = 1, ["sqrt"] = 1, ["if"] = 3
    };

    // ---- parser ---------------------------------------------------------------------------

    private sealed class Parser(List<Token> tokens)
    {
        private int _index;
        private readonly List<string> _references = new();

        public IReadOnlyList<string> References => _references;

        private Token Current => tokens[_index];

        private static int Precedence(string op) => op switch
        {
            "||" => 1,
            "&&" => 2,
            "==" or "!=" or "<" or "<=" or ">" or ">=" => 3,
            "+" or "-" => 4,
            "*" or "/" or "%" => 5,
            _ => -1
        };

        public Node ParseExpression(int minPrecedence)
        {
            var left = ParseUnary();

            while (Current.Kind == TokenKind.Operator)
            {
                var precedence = Precedence(Current.Text);
                if (precedence < 0 || precedence < minPrecedence) break;

                var op = Current.Text;
                _index++;
                // All operators here are left-associative.
                var right = ParseExpression(precedence + 1);
                left = new BinaryNode(op, left, right);
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (Current.Kind == TokenKind.Operator && Current.Text is "-" or "+" or "!")
            {
                var op = Current.Text;
                _index++;
                var operand = ParseUnary();
                return op == "+" ? operand : new UnaryNode(op, operand);
            }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var token = Current;

            switch (token.Kind)
            {
                case TokenKind.Number:
                    _index++;
                    return new ConstantNode(token.Value);

                case TokenKind.LeftParen:
                {
                    _index++;
                    var inner = ParseExpression(0);
                    Expect(TokenKind.RightParen, ")");
                    return inner;
                }

                case TokenKind.Name:
                {
                    _index++;
                    if (Current.Kind != TokenKind.LeftParen)
                    {
                        if (!_references.Contains(token.Text)) _references.Add(token.Text);
                        return new TagNode(token.Text);
                    }

                    if (!Arity.TryGetValue(token.Text, out var arity))
                        throw new FormatException($"Unknown function '{token.Text}'.");

                    _index++;   // past '('
                    var args = new List<Node>();
                    if (Current.Kind != TokenKind.RightParen)
                    {
                        args.Add(ParseExpression(0));
                        while (Current.Kind == TokenKind.Comma)
                        {
                            _index++;
                            args.Add(ParseExpression(0));
                        }
                    }
                    Expect(TokenKind.RightParen, ")");

                    if (arity >= 0 && args.Count != arity)
                        throw new FormatException($"'{token.Text}' takes {arity} argument(s), got {args.Count}.");
                    if (arity < 0 && args.Count is < 1 or > 2)
                        throw new FormatException($"'{token.Text}' takes 1 or 2 arguments, got {args.Count}.");

                    return new CallNode(token.Text.ToLowerInvariant(), args);
                }

                default:
                    throw new FormatException($"Unexpected '{token.Text}' in expression.");
            }
        }

        private void Expect(TokenKind kind, string what)
        {
            if (Current.Kind != kind) throw new FormatException($"Expected '{what}'.");
            _index++;
        }

        public void ExpectEnd()
        {
            if (Current.Kind != TokenKind.End)
                throw new FormatException($"Unexpected '{Current.Text}' after the end of the expression.");
        }
    }
}

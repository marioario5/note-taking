using System.Globalization;

namespace NoteTaker.Core.Expressions;

/// <summary>
/// Small recursive-descent parser for single-variable expressions, compiled once into a
/// delegate so plotting thousands of points stays cheap.
/// </summary>
public static class ExpressionEvaluator
{
    public static Func<double, double> Compile(string expression)
    {
        var parser = new Parser(expression);
        var function = parser.ParseExpression();
        parser.ExpectEnd();
        return function;
    }

    private sealed class Parser(string text)
    {
        private int _position;

        public Func<double, double> ParseExpression()
        {
            var left = ParseTerm();

            while (true)
            {
                SkipWhitespace();
                if (Match('+'))
                {
                    var right = ParseTerm();
                    var previous = left;
                    left = x => previous(x) + right(x);
                }
                else if (Match('-'))
                {
                    var right = ParseTerm();
                    var previous = left;
                    left = x => previous(x) - right(x);
                }
                else
                {
                    return left;
                }
            }
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_position < text.Length)
            {
                throw new FormatException($"Unexpected '{text[_position]}' at position {_position}.");
            }
        }

        private Func<double, double> ParseTerm()
        {
            var left = ParseUnary();

            while (true)
            {
                SkipWhitespace();
                if (Match('*'))
                {
                    var right = ParseUnary();
                    var previous = left;
                    left = x => previous(x) * right(x);
                }
                else if (Match('/'))
                {
                    var right = ParseUnary();
                    var previous = left;
                    left = x => previous(x) / right(x);
                }
                else
                {
                    return left;
                }
            }
        }

        private Func<double, double> ParseUnary()
        {
            SkipWhitespace();

            if (Match('-'))
            {
                // Negation binds looser than exponentiation, so -x^2 is -(x^2).
                var operand = ParseUnary();
                return x => -operand(x);
            }

            if (Match('+'))
            {
                return ParseUnary();
            }

            return ParsePower();
        }

        private Func<double, double> ParsePower()
        {
            var baseValue = ParsePrimary();
            SkipWhitespace();

            if (!Match('^'))
            {
                return baseValue;
            }

            // Right associative, and the exponent may itself be signed: 2^-3, 2^3^2.
            var exponent = ParseUnary();
            return x => Math.Pow(baseValue(x), exponent(x));
        }

        private Func<double, double> ParsePrimary()
        {
            SkipWhitespace();

            if (_position >= text.Length)
            {
                throw new FormatException("The expression ended unexpectedly.");
            }

            if (Match('('))
            {
                var inner = ParseExpression();
                SkipWhitespace();
                if (!Match(')'))
                {
                    throw new FormatException("Missing closing parenthesis.");
                }

                return inner;
            }

            var current = text[_position];

            if (char.IsDigit(current) || current == '.')
            {
                var start = _position;
                while (_position < text.Length && (char.IsDigit(text[_position]) || text[_position] == '.'))
                {
                    _position++;
                }

                var value = double.Parse(text[start.._position], CultureInfo.InvariantCulture);
                return _ => value;
            }

            if (char.IsLetter(current))
            {
                var start = _position;
                while (_position < text.Length && char.IsLetterOrDigit(text[_position]))
                {
                    _position++;
                }

                var name = text[start.._position].ToLowerInvariant();
                SkipWhitespace();

                if (Match('('))
                {
                    var argument = ParseExpression();
                    SkipWhitespace();
                    if (!Match(')'))
                    {
                        throw new FormatException($"Missing closing parenthesis after {name}.");
                    }

                    return name switch
                    {
                        "sin" => x => Math.Sin(argument(x)),
                        "cos" => x => Math.Cos(argument(x)),
                        "tan" => x => Math.Tan(argument(x)),
                        "asin" => x => Math.Asin(argument(x)),
                        "acos" => x => Math.Acos(argument(x)),
                        "atan" => x => Math.Atan(argument(x)),
                        "sinh" => x => Math.Sinh(argument(x)),
                        "cosh" => x => Math.Cosh(argument(x)),
                        "tanh" => x => Math.Tanh(argument(x)),
                        "exp" => x => Math.Exp(argument(x)),
                        "ln" or "log" => x => Math.Log(argument(x)),
                        "log10" => x => Math.Log10(argument(x)),
                        "sqrt" => x => Math.Sqrt(argument(x)),
                        "abs" => x => Math.Abs(argument(x)),
                        "floor" => x => Math.Floor(argument(x)),
                        "ceil" => x => Math.Ceiling(argument(x)),
                        _ => throw new FormatException($"Unknown function '{name}'."),
                    };
                }

                return name switch
                {
                    "x" => x => x,
                    "pi" => _ => Math.PI,
                    "e" => _ => Math.E,
                    _ => throw new FormatException($"Unknown symbol '{name}'."),
                };
            }

            throw new FormatException($"Unexpected '{current}' at position {_position}.");
        }

        private bool Match(char expected)
        {
            if (_position < text.Length && text[_position] == expected)
            {
                _position++;
                return true;
            }

            return false;
        }

        private void SkipWhitespace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }
    }
}


using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DinoLino.Utilities
{
    /// <summary>Supplies the numbers a formula reads while one row is evaluated.</summary>
    public interface IFormulaContext
    {
        /// <summary>The row's number in one column, or null when that cell holds none.</summary>
        double? Value(string column);

        /// <summary>Every number in one column, taken from all specimens in the table.</summary>
        IReadOnlyList<double> ColumnValues(string column);

        /// <summary>Every number in one column belonging to the row's own specimen.</summary>
        IReadOnlyList<double> SpecimenValues(string column);
    }

    /// <summary>A formula that has been checked and is ready to evaluate.</summary>
    public sealed class Formula
    {
        private readonly Func<IFormulaContext, double?> _evaluate;

        internal Formula(string text, Func<IFormulaContext, double?> evaluate, HashSet<string> references)
        {
            Text = text;
            _evaluate = evaluate;
            References = references;
        }

        /// <summary>The expression as it was typed, without a leading "=".</summary>
        public string Text { get; }

        /// <summary>Every column name the expression reads.</summary>
        public IReadOnlyCollection<string> References { get; }

        /// <summary>
        /// The result for one row, or null when a cell the formula needs is blank.
        /// </summary>
        public double? Evaluate(IFormulaContext context)
        {
            try
            {
                return _evaluate(context);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Raised while a formula is being read, and shown to the user as-is.</summary>
    public sealed class FormulaException : Exception
    {
        public FormulaException(string message) : base(message) { }
    }

    /// <summary>
    /// Turns formula text into a <see cref="Formula"/>, or says why it cannot.
    ///
    /// The grammar is the arithmetic one a spreadsheet user expects: numbers, column
    /// names, the operators + - * / ^ and the comparisons = &lt;&gt; &lt; &lt;= &gt; &gt;=, grouped
    /// with parentheses and passed through functions. A column name stands for that
    /// column's value on the row being calculated; the COL and SPEC functions reach
    /// across rows instead.
    ///
    /// A blank cell is not zero. Any arithmetic touching a blank gives a blank
    /// result, so a specimen that was never measured leaves an empty cell rather than
    /// a misleading number. The list functions (SUM, AVERAGE, MIN, MAX, COUNT,
    /// MEDIAN, PRODUCT) skip blank arguments the way a spreadsheet skips empty cells,
    /// and IFBLANK supplies a stand-in value where one is wanted.
    /// </summary>
    public static class FormulaCompiler
    {
        /// <summary>Longest formula accepted.</summary>
        public const int MaxLength = 400;

        // Guards against a pathological expression: 32 arguments is far past what a
        // table of this shape needs.
        private const int MaxArguments = 32;

        /// <summary>The function names, grouped for display in the formula dialog.</summary>
        public static string FunctionHelp =>
            "Row functions: SUM, PRODUCT, AVERAGE, MIN, MAX, COUNT, MEDIAN, ABS, SQRT, LN, LOG, "
            + "LOG10, EXP, POWER, MOD, ROUND, INT, SIGN, PI, SIN, COS, TAN, ASIN, ACOS, ATAN, "
            + "ATAN2, DEGREES, RADIANS, IF, IFBLANK, ISBLANK\n"
            + "Whole-column functions, over every specimen: COLSUM, COLMEAN, COLMIN, COLMAX, "
            + "COLCOUNT, COLMEDIAN, COLSD\n"
            + "Specimen functions, over the row's own specimen: SPECSUM, SPECMEAN, SPECMIN, "
            + "SPECMAX, SPECCOUNT, SPECMEDIAN, SPECSD";

        /// <summary>True when the name belongs to a function, so no column may take it.</summary>
        public static bool IsFunctionName(string name) =>
            !string.IsNullOrEmpty(name)
            && (Functions.ContainsKey(name)
                || Aggregates.ContainsKey(name)
                || SpecialForms.Contains(name));

        /// <summary>
        /// Reads formula text against the columns it is allowed to use. On failure the
        /// message explains the first problem found, in the words of the formula.
        /// </summary>
        public static bool TryCompile(
            string text, IEnumerable<string> knownColumns, out Formula formula, out string error)
        {
            formula = null;
            error = "";

            string expression = (text ?? "").Trim();
            if (expression.StartsWith("=")) expression = expression.Substring(1).Trim();

            if (expression.Length == 0)
            {
                error = "Enter a formula.";
                return false;
            }

            if (expression.Length > MaxLength)
            {
                error = "A formula may be at most " + MaxLength + " characters long.";
                return false;
            }

            var columns = new HashSet<string>(
                knownColumns ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var parser = new Parser(Tokenize(expression), columns, references);
                var root = parser.ParseAll();

                formula = new Formula(expression, root, references);
                return true;
            }
            catch (FormulaException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // =====================
        // Tokens
        // =====================

        private enum TokenKind
        {
            Number,
            Name,
            Symbol,
            End
        }

        private struct Token
        {
            public TokenKind Kind;
            public string Text;
            public double Number;
        }

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                {
                    int start = i;
                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;

                    // An exponent counts only when digits follow it, so "2e" reads as
                    // the number 2 beside a column named e.
                    if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
                    {
                        int mantissaEnd = i;
                        int scan = i + 1;

                        if (scan < text.Length && (text[scan] == '+' || text[scan] == '-')) scan++;

                        if (scan < text.Length && char.IsDigit(text[scan]))
                        {
                            while (scan < text.Length && char.IsDigit(text[scan])) scan++;
                            i = scan;
                        }
                        else
                        {
                            i = mantissaEnd;
                        }
                    }

                    string number = text.Substring(start, i - start);
                    double value;

                    if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        throw new FormulaException("\"" + number + "\" is not a number.");

                    tokens.Add(new Token { Kind = TokenKind.Number, Text = number, Number = value });
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;

                    tokens.Add(new Token { Kind = TokenKind.Name, Text = text.Substring(start, i - start) });
                    continue;
                }

                if (c == '<' || c == '>')
                {
                    string symbol = c.ToString();

                    if (i + 1 < text.Length && (text[i + 1] == '=' || (c == '<' && text[i + 1] == '>')))
                    {
                        symbol = text.Substring(i, 2);
                        i++;
                    }

                    i++;
                    tokens.Add(new Token { Kind = TokenKind.Symbol, Text = symbol });
                    continue;
                }

                if ("+-*/^(),=".IndexOf(c) >= 0)
                {
                    tokens.Add(new Token { Kind = TokenKind.Symbol, Text = c.ToString() });
                    i++;
                    continue;
                }

                throw new FormulaException("\"" + c + "\" cannot be used in a formula.");
            }

            tokens.Add(new Token { Kind = TokenKind.End, Text = "" });
            return tokens;
        }

        // =====================
        // Parser
        // =====================

        // Each rule returns the expression it parsed as something callable, so the
        // parse tree is the chain of closures rather than a class per node.
        private sealed class Parser
        {
            private readonly List<Token> _tokens;
            private readonly HashSet<string> _columns;
            private readonly HashSet<string> _references;
            private int _index;

            public Parser(List<Token> tokens, HashSet<string> columns, HashSet<string> references)
            {
                _tokens = tokens;
                _columns = columns;
                _references = references;
            }

            public Func<IFormulaContext, double?> ParseAll()
            {
                var expression = ParseComparison();

                if (Current.Kind != TokenKind.End)
                    throw new FormulaException("\"" + Current.Text + "\" is unexpected here.");

                return expression;
            }

            private Token Current => _tokens[_index];

            private bool TakeSymbol(string symbol)
            {
                if (Current.Kind != TokenKind.Symbol || Current.Text != symbol) return false;

                _index++;
                return true;
            }

            private void Expect(string symbol)
            {
                if (!TakeSymbol(symbol))
                    throw new FormulaException("Expected \"" + symbol + "\".");
            }

            private Func<IFormulaContext, double?> ParseComparison()
            {
                var left = ParseAdditive();

                while (Current.Kind == TokenKind.Symbol && IsComparison(Current.Text))
                {
                    string symbol = Current.Text;
                    _index++;

                    var right = ParseAdditive();
                    left = Binary(left, right, (a, b) => Compare(symbol, a, b));
                }

                return left;
            }

            private static bool IsComparison(string symbol) =>
                symbol == "=" || symbol == "<>" || symbol == "<"
                || symbol == "<=" || symbol == ">" || symbol == ">=";

            private static double Compare(string symbol, double a, double b)
            {
                bool result;

                switch (symbol)
                {
                    case "=": result = a == b; break;
                    case "<>": result = a != b; break;
                    case "<": result = a < b; break;
                    case "<=": result = a <= b; break;
                    case ">": result = a > b; break;
                    default: result = a >= b; break;
                }

                return result ? 1 : 0;
            }

            private Func<IFormulaContext, double?> ParseAdditive()
            {
                var left = ParseMultiplicative();

                while (Current.Kind == TokenKind.Symbol && (Current.Text == "+" || Current.Text == "-"))
                {
                    bool add = Current.Text == "+";
                    _index++;

                    var right = ParseMultiplicative();
                    left = add
                        ? Binary(left, right, (a, b) => a + b)
                        : Binary(left, right, (a, b) => a - b);
                }

                return left;
            }

            private Func<IFormulaContext, double?> ParseMultiplicative()
            {
                var left = ParseUnary();

                while (Current.Kind == TokenKind.Symbol && (Current.Text == "*" || Current.Text == "/"))
                {
                    bool multiply = Current.Text == "*";
                    _index++;

                    var right = ParseUnary();
                    left = multiply
                        ? Binary(left, right, (a, b) => a * b)
                        : Binary(left, right, (a, b) => a / b);
                }

                return left;
            }

            // A minus sign in front of a power applies to the whole power, so -x^2 is
            // the negative of x squared.
            private Func<IFormulaContext, double?> ParseUnary()
            {
                if (TakeSymbol("-"))
                {
                    var operand = ParseUnary();
                    return ctx =>
                    {
                        var value = operand(ctx);
                        return value.HasValue ? -value.Value : (double?)null;
                    };
                }

                if (TakeSymbol("+")) return ParseUnary();

                return ParsePower();
            }

            private Func<IFormulaContext, double?> ParsePower()
            {
                var left = ParsePrimary();

                // Right-associative, and the exponent may carry its own sign.
                if (TakeSymbol("^"))
                {
                    var right = ParseUnary();
                    return Binary(left, right, Math.Pow);
                }

                return left;
            }

            private Func<IFormulaContext, double?> ParsePrimary()
            {
                var token = Current;

                if (token.Kind == TokenKind.Number)
                {
                    _index++;
                    double value = token.Number;
                    return ctx => value;
                }

                if (token.Kind == TokenKind.Symbol && token.Text == "(")
                {
                    _index++;
                    var inner = ParseComparison();
                    Expect(")");
                    return inner;
                }

                if (token.Kind == TokenKind.Name)
                {
                    _index++;

                    if (Current.Kind == TokenKind.Symbol && Current.Text == "(")
                        return ParseCall(token.Text);

                    return ParseColumn(token.Text);
                }

                throw new FormulaException(token.Kind == TokenKind.End
                    ? "The formula ends too early."
                    : "\"" + token.Text + "\" is unexpected here.");
            }

            private Func<IFormulaContext, double?> ParseColumn(string name)
            {
                if (IsFunctionName(name))
                    throw new FormulaException(name.ToUpperInvariant() + " is a function, so it needs brackets after it.");

                if (!_columns.Contains(name))
                    throw new FormulaException("This table has no column named \"" + name + "\".");

                _references.Add(name);

                string column = name;
                return ctx => ctx.Value(column);
            }

            private Func<IFormulaContext, double?> ParseCall(string name)
            {
                Expect("(");

                if (SpecialForms.Contains(name)) return ParseSpecialForm(name);

                AggregateSpec aggregate;
                if (Aggregates.TryGetValue(name, out aggregate)) return ParseAggregate(name, aggregate);

                FunctionSpec function;
                if (!Functions.TryGetValue(name, out function))
                    throw new FormulaException("\"" + name + "\" is not a function a formula can use.");

                var arguments = ParseArguments();

                if (arguments.Count < function.MinArgs || arguments.Count > function.MaxArgs)
                    throw new FormulaException(ArityMessage(name, function));

                var apply = function.Apply;
                bool skipBlanks = function.SkipBlanks;
                bool allowEmpty = function.AllowEmpty;

                return ctx =>
                {
                    var values = new List<double>(arguments.Count);

                    foreach (var argument in arguments)
                    {
                        var value = argument(ctx);

                        if (!value.HasValue)
                        {
                            if (skipBlanks) continue;
                            return null;
                        }

                        values.Add(value.Value);
                    }

                    if (skipBlanks && values.Count == 0 && !allowEmpty) return null;

                    return apply(values.ToArray());
                };
            }

            // IF, IFBLANK and ISBLANK decide what to do with a blank themselves, so
            // their arguments are held back rather than evaluated in advance.
            private Func<IFormulaContext, double?> ParseSpecialForm(string name)
            {
                var arguments = ParseArguments();
                string upper = name.ToUpperInvariant();

                if (upper == "IF")
                {
                    if (arguments.Count != 3)
                        throw new FormulaException("IF takes three arguments: IF(test, value if true, value if false).");

                    var test = arguments[0];
                    var whenTrue = arguments[1];
                    var whenFalse = arguments[2];

                    return ctx =>
                    {
                        var condition = test(ctx);
                        if (!condition.HasValue) return null;

                        return condition.Value != 0 ? whenTrue(ctx) : whenFalse(ctx);
                    };
                }

                if (upper == "IFBLANK")
                {
                    if (arguments.Count != 2)
                        throw new FormulaException("IFBLANK takes two arguments: IFBLANK(value, value to use when blank).");

                    var value = arguments[0];
                    var fallback = arguments[1];

                    return ctx => value(ctx) ?? fallback(ctx);
                }

                if (arguments.Count != 1)
                    throw new FormulaException("ISBLANK takes one argument: ISBLANK(value).");

                var tested = arguments[0];
                return ctx => tested(ctx).HasValue ? 0d : 1d;
            }

            // A COL or SPEC function reads a column rather than a value, so its one
            // argument is a bare column name.
            private Func<IFormulaContext, double?> ParseAggregate(string name, AggregateSpec spec)
            {
                string upper = name.ToUpperInvariant();

                if (Current.Kind != TokenKind.Name)
                    throw new FormulaException(
                        upper + " takes one column name, as in " + upper + "(outline_area).");

                string column = Current.Text;
                _index++;
                Expect(")");

                if (!_columns.Contains(column))
                    throw new FormulaException("This table has no column named \"" + column + "\".");

                _references.Add(column);

                bool wholeTable = spec.WholeTable;
                var reduce = spec.Reduce;

                return ctx =>
                {
                    var values = wholeTable ? ctx.ColumnValues(column) : ctx.SpecimenValues(column);
                    return values == null || values.Count == 0 ? (double?)null : reduce(values);
                };
            }

            private List<Func<IFormulaContext, double?>> ParseArguments()
            {
                var arguments = new List<Func<IFormulaContext, double?>>();

                if (TakeSymbol(")")) return arguments;

                while (true)
                {
                    if (arguments.Count == MaxArguments)
                        throw new FormulaException("A function takes at most " + MaxArguments + " arguments.");

                    arguments.Add(ParseComparison());

                    if (TakeSymbol(",")) continue;

                    Expect(")");
                    return arguments;
                }
            }

            private static Func<IFormulaContext, double?> Binary(
                Func<IFormulaContext, double?> left,
                Func<IFormulaContext, double?> right,
                Func<double, double, double> combine)
            {
                return ctx =>
                {
                    var a = left(ctx);
                    if (!a.HasValue) return null;

                    var b = right(ctx);
                    if (!b.HasValue) return null;

                    return combine(a.Value, b.Value);
                };
            }

            private static string ArityMessage(string name, FunctionSpec function)
            {
                string upper = name.ToUpperInvariant();

                if (function.MinArgs == function.MaxArgs)
                {
                    return function.MinArgs == 0
                        ? upper + " takes no arguments."
                        : upper + " takes " + function.MinArgs + " argument(s).";
                }

                return function.MaxArgs >= MaxArguments
                    ? upper + " takes at least " + function.MinArgs + " argument(s)."
                    : upper + " takes between " + function.MinArgs + " and " + function.MaxArgs + " arguments.";
            }
        }

        // =====================
        // Functions
        // =====================

        private sealed class FunctionSpec
        {
            public int MinArgs;
            public int MaxArgs;

            // Blank arguments are dropped instead of blanking the whole result, the
            // way a spreadsheet's list functions ignore empty cells.
            public bool SkipBlanks;

            // Whether a call whose arguments are all blank still produces a number.
            public bool AllowEmpty;

            public Func<double[], double?> Apply;
        }

        private sealed class AggregateSpec
        {
            public bool WholeTable;
            public Func<IReadOnlyList<double>, double?> Reduce;
        }

        private static readonly HashSet<string> SpecialForms =
            new HashSet<string>(new[] { "IF", "IFBLANK", "ISBLANK" }, StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, FunctionSpec> Functions = BuildFunctions();

        private static readonly Dictionary<string, AggregateSpec> Aggregates = BuildAggregates();

        private static Dictionary<string, FunctionSpec> BuildFunctions()
        {
            var functions = new Dictionary<string, FunctionSpec>(StringComparer.OrdinalIgnoreCase);

            Action<string, int, int, Func<double[], double?>> strict = (name, min, max, apply) =>
                functions[name] = new FunctionSpec { MinArgs = min, MaxArgs = max, Apply = apply };

            Action<string, Func<double[], double?>> list = (name, apply) =>
                functions[name] = new FunctionSpec
                {
                    MinArgs = 1,
                    MaxArgs = MaxArguments,
                    SkipBlanks = true,
                    Apply = apply
                };

            list("SUM", v => v.Sum());
            list("PRODUCT", v => v.Aggregate(1d, (product, value) => product * value));
            list("AVERAGE", v => v.Average());
            list("MEAN", v => v.Average());
            list("MIN", v => v.Min());
            list("MAX", v => v.Max());
            list("MEDIAN", v => Median(v));

            functions["COUNT"] = new FunctionSpec
            {
                MinArgs = 1,
                MaxArgs = MaxArguments,
                SkipBlanks = true,
                AllowEmpty = true,
                Apply = v => (double)v.Length
            };

            strict("ABS", 1, 1, v => Math.Abs(v[0]));
            strict("SQRT", 1, 1, v => Math.Sqrt(v[0]));
            strict("LN", 1, 1, v => Math.Log(v[0]));
            strict("LOG10", 1, 1, v => Math.Log10(v[0]));
            strict("LOG", 1, 2, v => v.Length == 1 ? Math.Log10(v[0]) : Math.Log(v[0], v[1]));
            strict("EXP", 1, 1, v => Math.Exp(v[0]));
            strict("POWER", 2, 2, v => Math.Pow(v[0], v[1]));
            strict("MOD", 2, 2, v => v[1] == 0 ? (double?)null : v[0] - v[1] * Math.Floor(v[0] / v[1]));
            strict("ROUND", 1, 2, v => Math.Round(
                v[0], v.Length == 1 ? 0 : Limit((int)v[1], 0, 15), MidpointRounding.AwayFromZero));
            strict("INT", 1, 1, v => Math.Floor(v[0]));
            strict("SIGN", 1, 1, v => (double)Math.Sign(v[0]));
            strict("PI", 0, 0, v => Math.PI);
            strict("DEGREES", 1, 1, v => v[0] * 180d / Math.PI);
            strict("RADIANS", 1, 1, v => v[0] * Math.PI / 180d);
            strict("SIN", 1, 1, v => Math.Sin(v[0]));
            strict("COS", 1, 1, v => Math.Cos(v[0]));
            strict("TAN", 1, 1, v => Math.Tan(v[0]));
            strict("ASIN", 1, 1, v => Math.Asin(v[0]));
            strict("ACOS", 1, 1, v => Math.Acos(v[0]));
            strict("ATAN", 1, 1, v => Math.Atan(v[0]));
            strict("ATAN2", 2, 2, v => Math.Atan2(v[1], v[0]));

            return functions;
        }

        private static Dictionary<string, AggregateSpec> BuildAggregates()
        {
            var aggregates = new Dictionary<string, AggregateSpec>(StringComparer.OrdinalIgnoreCase);

            Action<string, bool> add = (prefix, wholeTable) =>
            {
                aggregates[prefix + "SUM"] = Reduce(wholeTable, v => v.Sum());
                aggregates[prefix + "MEAN"] = Reduce(wholeTable, v => v.Average());
                aggregates[prefix + "AVERAGE"] = Reduce(wholeTable, v => v.Average());
                aggregates[prefix + "MIN"] = Reduce(wholeTable, v => v.Min());
                aggregates[prefix + "MAX"] = Reduce(wholeTable, v => v.Max());
                aggregates[prefix + "COUNT"] = Reduce(wholeTable, v => (double)v.Count);
                aggregates[prefix + "MEDIAN"] = Reduce(wholeTable, v => Median(v));
                aggregates[prefix + "SD"] = Reduce(wholeTable, StandardDeviation);
                aggregates[prefix + "STDEV"] = Reduce(wholeTable, StandardDeviation);
            };

            add("COL", true);
            add("SPEC", false);

            return aggregates;
        }

        private static AggregateSpec Reduce(bool wholeTable, Func<IReadOnlyList<double>, double?> reduce) =>
            new AggregateSpec { WholeTable = wholeTable, Reduce = reduce };

        private static double Median(IReadOnlyList<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int middle = sorted.Count / 2;

            return sorted.Count % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2d;
        }

        // Sample standard deviation, which needs two measurements to mean anything.
        private static double? StandardDeviation(IReadOnlyList<double> values)
        {
            if (values.Count < 2) return null;

            double mean = values.Average();
            double sum = 0;

            foreach (var value in values)
            {
                double deviation = value - mean;
                sum += deviation * deviation;
            }

            return Math.Sqrt(sum / (values.Count - 1));
        }

        private static int Limit(int value, int low, int high)
        {
            if (value < low) return low;
            return value > high ? high : value;
        }
    }
}

using System.Globalization;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// One variable that may appear in a points <see cref="PointsRuleKind.Formula"/>. The token is the exact
/// text written in the formula; the description is shown in the editor's variable palette.
/// </summary>
public sealed record PointsVariable(string Token, string DescriptionKey);

/// <summary>
/// The set of values a points formula is evaluated against for a single participant. All members are
/// optional inputs to <see cref="PointsFormula.Evaluate"/>; an unknown/zero value is treated as 0.
/// </summary>
public sealed record PointsFormulaContext
{
    /// <summary>T_у — the participant's result time, in seconds.</summary>
    public double ParticipantTime { get; init; }

    /// <summary>T_л — the leader's (winner's) result time, in seconds.</summary>
    public double LeaderTime { get; init; }

    /// <summary>N — the number of runners in the group.</summary>
    public double GroupSize { get; init; }

    /// <summary>Місце — the participant's place (1 = winner).</summary>
    public double Place { get; init; }

    /// <summary>Бали — the participant's scored points (rogaine score).</summary>
    public double Score { get; init; }

    /// <summary>Бали_л — the leader's scored points.</summary>
    public double LeaderScore { get; init; }
}

/// <summary>
/// Parses and evaluates a points <see cref="PointsRuleKind.Formula"/> — a small arithmetic expression
/// over the allowed <see cref="Variables"/> with <c>+ - * /</c>, parentheses, decimal literals and the
/// helper functions <c>min</c>, <c>max</c>, <c>round</c>. Self-contained (a recursive-descent parser, no
/// external dependency) so BusinessLogic stays free of third-party libraries.
///
/// Use <see cref="TryValidate"/> for live editor feedback and <see cref="Evaluate"/> to compute a value.
/// </summary>
public static class PointsFormula
{
    /// <summary>The variables a formula may reference (token + description key for the palette).</summary>
    public static readonly IReadOnlyList<PointsVariable> Variables =
    [
        new("T_у", "Points.Var.ParticipantTime"),
        new("T_л", "Points.Var.LeaderTime"),
        new("N", "Points.Var.GroupSize"),
        new("Місце", "Points.Var.Place"),
        new("Бали", "Points.Var.Score"),
        new("Бали_л", "Points.Var.LeaderScore"),
    ];

    /// <summary>
    /// Checks that <paramref name="formula"/> parses; on failure returns false with an error message.
    /// A blank formula is treated as invalid (there is nothing to compute).
    /// </summary>
    public static bool TryValidate(string? formula, out string error)
    {
        if (string.IsNullOrWhiteSpace(formula))
        {
            error = "empty";
            return false;
        }

        try
        {
            // Parse against a zero context — only structure is checked here, not the numeric result.
            _ = Evaluate(formula, new PointsFormulaContext());
            error = string.Empty;
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Evaluates <paramref name="formula"/> against <paramref name="context"/>. Division by zero yields 0
    /// (so a missing leader time does not blow up). Throws <see cref="FormatException"/> on a malformed
    /// expression or unknown token.
    /// </summary>
    public static double Evaluate(string formula, PointsFormulaContext context)
    {
        var parser = new Parser(formula ?? string.Empty, context);
        var value = parser.ParseExpression();
        parser.ExpectEnd();
        return value;
    }

    // Recursive-descent parser/evaluator. Grammar (lowest to highest precedence):
    //   expression := term (('+' | '-') term)*
    //   term       := factor (('*' | '/') factor)*
    //   factor     := ('+' | '-') factor | primary
    //   primary    := number | variable | function '(' args ')' | '(' expression ')'
    private sealed class Parser
    {
        private readonly string _text;
        private readonly PointsFormulaContext _ctx;
        private int _pos;

        public Parser(string text, PointsFormulaContext ctx)
        {
            _text = text;
            _ctx = ctx;
        }

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (Match('+')) value += ParseTerm();
                else if (Match('-')) value -= ParseTerm();
                else return value;
            }
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_pos != _text.Length)
                throw new FormatException($"Unexpected '{_text[_pos]}' at position {_pos}.");
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipWhitespace();
                if (Match('*'))
                {
                    value *= ParseFactor();
                }
                else if (Match('/'))
                {
                    var divisor = ParseFactor();
                    value = divisor == 0 ? 0 : value / divisor;
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParseFactor()
        {
            SkipWhitespace();
            if (Match('+')) return ParseFactor();
            if (Match('-')) return -ParseFactor();
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
                throw new FormatException("Unexpected end of formula.");

            var c = _text[_pos];
            if (c == '(')
            {
                _pos++;
                var value = ParseExpression();
                SkipWhitespace();
                if (!Match(')'))
                    throw new FormatException("Missing ')'.");
                return value;
            }

            if (char.IsDigit(c) || c == '.' || c == ',')
                return ParseNumber();

            if (IsIdentifierStart(c))
                return ParseIdentifier();

            throw new FormatException($"Unexpected '{c}' at position {_pos}.");
        }

        private double ParseNumber()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.' || _text[_pos] == ','))
                _pos++;

            var token = _text[start.._pos].Replace(',', '.');
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"Bad number '{token}'.");
            return value;
        }

        private double ParseIdentifier()
        {
            var start = _pos;
            while (_pos < _text.Length && IsIdentifierPart(_text[_pos]))
                _pos++;
            var name = _text[start.._pos];

            // Function call?
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '(')
                return ParseFunction(name);

            return name switch
            {
                "T_у" => _ctx.ParticipantTime,
                "T_л" => _ctx.LeaderTime,
                "N" => _ctx.GroupSize,
                "Місце" => _ctx.Place,
                "Бали" => _ctx.Score,
                "Бали_л" => _ctx.LeaderScore,
                _ => throw new FormatException($"Unknown variable '{name}'."),
            };
        }

        private double ParseFunction(string name)
        {
            Match('(');
            var args = new List<double> { ParseExpression() };
            SkipWhitespace();
            while (Match(';') || Match(','))
                args.Add(ParseExpression());
            SkipWhitespace();
            if (!Match(')'))
                throw new FormatException($"Missing ')' after {name}(.");

            return name switch
            {
                "min" when args.Count >= 1 => args.Min(),
                "max" when args.Count >= 1 => args.Max(),
                "round" when args.Count == 1 => Math.Round(args[0], MidpointRounding.AwayFromZero),
                "round" when args.Count == 2 => Math.Round(args[0], (int)args[1], MidpointRounding.AwayFromZero),
                _ => throw new FormatException($"Unknown function '{name}' with {args.Count} arg(s)."),
            };
        }

        private bool Match(char c)
        {
            if (_pos < _text.Length && _text[_pos] == c)
            {
                _pos++;
                return true;
            }
            return false;
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                _pos++;
        }

        // Variable names include Cyrillic letters and underscores (T_у, Місце, Бали_л).
        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}

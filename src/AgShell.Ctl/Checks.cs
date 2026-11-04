using System.CommandLine;
using System.CommandLine.Parsing;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    /// <summary>
    /// agterm's validate(): checks refused before anything is sent, in agterm's order, the first failing one being the usage
    /// error.
    /// </summary>
    internal static class Checks
    {
        public static void Check(this Command command, params Func<CommandResult, string?>[] checks)
        {
            command.Validators.Add(parsed =>
            {
                // A value the parser could not read is already its error, which reading it here would throw.
                try
                {
                    foreach (Func<CommandResult, string?> check in checks)
                    {
                        if (check(parsed) is string error)
                        {
                            parsed.AddError(error);
                            return;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                }
            });
        }

        /// <summary>agterm's validatePaneArgument: the read-back names and their role and position aliases.</summary>
        public static Func<CommandResult, string?> Pane(Option<string?> pane)
        {
            return parsed => parsed.GetValue(pane) is string value && !IsPane(value) ? "--pane must be left, right, or scratch" : null;
        }

        public static bool IsPane(string value)
        {
            return value is "left" or "top" or "primary" or "right" or "bottom" or "split" or "scratch";
        }

        /// <summary>agterm's isValidColorHex: six hex digits, the leading # optional.</summary>
        public static bool IsHexColor(string color)
        {
            string hex = color.StartsWith('#') ? color[1..] : color;
            return hex.Length == 6 && hex.All(Uri.IsHexDigit);
        }

        /// <summary>agterm's Background.validate, for the values a form carries.</summary>
        public static string? Background(string? fit = null, string? position = null, double? opacity = null, string? color = null, string? text = null, string? path = null)
        {
            if (fit is not null && !ControlBackground.Fits.Contains(fit))
            {
                return "fit must be one of: " + string.Join(", ", ControlBackground.Fits);
            }

            if (position is not null && !ControlBackground.Positions.Contains(position))
            {
                return "position must be one of: " + string.Join(", ", ControlBackground.Positions);
            }

            if (opacity is double value && !(double.IsFinite(value) && value >= 0 && value <= 1))
            {
                return "opacity must be between 0.0 and 1.0";
            }

            if (color is not null && !IsHexColor(color))
            {
                return "color must be a #rrggbb hex value";
            }

            if (text is not null && (text.Length == 0 || new System.Globalization.StringInfo(text).LengthInTextElements > ControlBackground.MaxTextLength))
            {
                return "text must be 1–" + ControlBackground.MaxTextLength + " characters";
            }

            if (path is not null && path.Any(c => c < ' '))
            {
                return "image path must not contain control characters";
            }

            return null;
        }
    }
}

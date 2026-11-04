using AgShell.Protocol;

namespace AgShell.Core
{
    public sealed partial class ControlDispatcher
    {
        /// <summary>
        /// agterm's session.background: image (PNG or JPEG), text (up to 256 characters), color, or clear. Without --pane
        /// it is the session's, with one that pane's own; a right pane needs a split and a scratch pane an open scratch.
        /// </summary>
        private ControlResponse SetBackground(ControlRequest request)
        {
            // agterm's order: fit, position, opacity, the mode's own checks, the pane, then the session; the image file is
            // looked for once the session is known, as a remote row's path is its machine's.
            ControlArgs args = request.Args ?? new ControlArgs();
            if (args.Fit is string fit && !ControlBackground.Fits.Contains(fit))
            {
                return Fail("invalid fit: " + fit + " (" + string.Join("|", ControlBackground.Fits) + ")");
            }

            if (args.Position is string position && !ControlBackground.Positions.Contains(position))
            {
                return Fail("invalid position: " + position);
            }

            if (args.Opacity is double opacity && !(opacity >= 0 && opacity <= 1))
            {
                return Fail("invalid opacity: " + opacity.ToString(System.Globalization.CultureInfo.InvariantCulture) + " (0.0-1.0)");
            }

            string value = args.Text ?? string.Empty;
            string image = args.Path ?? string.Empty;
            ControlBackground? background;
            switch (args.Mode)
            {
                case "clear" or null:
                    background = null;
                    break;
                case "image":
                    // agterm's image path arrives as path; text carries only the text form.
                    if (image.Length == 0)
                    {
                        return Fail("session.background image requires a path");
                    }

                    if (HasControl(image))
                    {
                        return Fail("image path must not contain control characters");
                    }

                    if (Path.GetExtension(image).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg"))
                    {
                        return Fail("unsupported image (PNG or JPEG only): " + image);
                    }

                    background = new ControlBackground { Kind = "image", Opacity = args.Opacity, Fit = args.Fit, Position = args.Position, Repeats = args.Repeat == true ? true : null };
                    break;
                case "text":
                    if (value.Length == 0)
                    {
                        return Fail("session.background text requires text");
                    }

                    if (new System.Globalization.StringInfo(value).LengthInTextElements > ControlBackground.MaxTextLength)
                    {
                        return Fail("session.background text too long (max " + ControlBackground.MaxTextLength + " characters)");
                    }

                    if (args.Color is string textColor && !IsHexColor(textColor))
                    {
                        return Fail("invalid color: " + textColor + " (#rrggbb)");
                    }

                    background = new ControlBackground { Kind = "text", Text = value, ColorHex = args.Color, Opacity = args.Opacity, Fit = args.Fit, Position = args.Position };
                    break;
                case "color":
                    if (string.IsNullOrEmpty(args.Color))
                    {
                        return Fail("session.background color requires a color");
                    }

                    if (!IsHexColor(args.Color))
                    {
                        return Fail("invalid color: " + args.Color + " (#rrggbb)");
                    }

                    background = new ControlBackground { Kind = "color", ColorHex = args.Color };
                    break;
                default:
                    return Fail("invalid background mode: " + args.Mode + " (image|text|color|clear)");
            }

            PaneRole? pane = null;
            if (args.Pane is string name)
            {
                if (!Panes.TryParse(name, out PaneRole parsed))
                {
                    return Fail("--pane must be left, right, or scratch");
                }

                pane = parsed;
            }

            Session session = ResolveSession(request.Target);
            if (background is { Kind: "image" })
            {
                string? path = host.LocalPath(session.Id, image);
                if (path is null || !File.Exists(path))
                {
                    return Fail("no such image file: " + image);
                }

                background = background with { ImagePath = path };
            }

            // agterm: a pane that is not there would hand its background to the next one; a hidden scratch still is.
            if (pane == PaneRole.Split && !session.HasSplit)
            {
                return Fail("session has no split pane");
            }

            if (pane == PaneRole.Scratch && !session.ScratchShown && !host.IsRunning(session.Id, PaneRole.Scratch))
            {
                return Fail("session has no scratch terminal");
            }

            model.SetBackground(session.Id, pane, background);
            return Ok(new ControlResult { Id = session.Id.Wire() });
        }
    }
}

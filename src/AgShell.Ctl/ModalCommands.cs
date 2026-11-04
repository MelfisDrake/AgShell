using System.CommandLine;
using System.Text.Json;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    /// <summary>
    /// agterm's pick and ask. Both open, then poll the result (fast at first, then every half second) and print it as bare
    /// JSON. Ctrl+C cancels the dialog, so an abandoned caller never leaves it on screen.
    /// </summary>
    internal static partial class Commands
    {
        private const int CancelledExitCode = 2;
        private const int EscapedExitCode = 3;

        /// <summary>agterm's pick: open is the default subcommand, so `pick open` works as well as `pick`.</summary>
        private static Command Pick()
        {
            Command pick = PickOpen("pick", "Pick one of the lines (or a JSON array of {id,label,subtitle?}) read from stdin.");
            CtlCommand.AddDefault(pick, PickOpen("open", "Open the picker on the lines (or JSON items) read from stdin and wait for the choice."));
            pick.Subcommands.Add(ModalResult("pick", "Print a picker's result as JSON: pending (exit 1), picked or custom (0), cancelled (2)."));
            pick.Subcommands.Add(ModalCancel("pick", "Cancel a pending picker."));
            return pick;
        }

        private static Command PickOpen(string name, string description)
        {
            Option<string?> prompt = new Option<string?>("--prompt") { Description = "Placeholder text of the query field." };
            Option<string?> query = new Option<string?>("--query") { Description = "Initial query; the picker opens already filtered." };
            Option<string?> select = new Option<string?>("--select") { Description = "The id of the item highlighted on open." };
            Option<bool> allowCustom = new Option<bool>("--allow-custom") { Description = "Accept a query that matches no item; with no items it is a plain text prompt." };
            Option<bool> follow = new Option<bool>("--follow") { Description = "Bring the window to the front." };
            Option<bool> noBlock = new Option<bool>("--no-block") { Description = "Print the picker id and return without waiting." };
            Command pick = new CtlCommand(name, description) { prompt, query, allowCustom, select, follow, noBlock };
            pick.SetAction(result =>
            {
                // As agterm's, stdin is read to its end even from a terminal (Ctrl+Z, Enter), so --allow-custom alone is a prompt.
                using MemoryStream input = new MemoryStream();
                Console.OpenStandardInput().CopyTo(input);
                List<ControlPickItem>? items;
                try
                {
                    items = PickItems(input.ToArray());
                }
                catch (JsonException ex)
                {
                    return Failure("invalid JSON items: " + ex.Message);
                }

                if (items is null)
                {
                    return Usage(result, "stdin must be UTF-8 text or a JSON item array");
                }

                ControlArgs args = new ControlArgs
                {
                    Items = items,
                    Prompt = result.GetValue(prompt),
                    Query = result.GetValue(query),
                    Selection = result.GetValue(select),
                    AllowCustom = Flag(result, allowCustom),
                };
                return OpenModal(result, "pick", null, args, result.GetValue(noBlock), result.GetValue(follow));
            });
            return pick;
        }

        /// <summary>
        /// agterm's parseItems: input whose first byte past spaces, tabs and line breaks is [ is a JSON array of items;
        /// otherwise every line (split at any line break) that is not blank is an item whose id is its label. Null when the
        /// text is not UTF-8.
        /// </summary>
        internal static List<ControlPickItem>? PickItems(byte[] input)
        {
            if (input.FirstOrDefault(b => b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')) == (byte)'[')
            {
                return ControlJson.DeserializePickItems(new System.Text.UTF8Encoding(false).GetString(input)) ?? new List<ControlPickItem>();
            }

            string text;
            try
            {
                text = new System.Text.UTF8Encoding(false, true).GetString(input);
            }
            catch (System.Text.DecoderFallbackException)
            {
                return null;
            }

            return text.Split(new[] { "\r\n", "\n", "\r", "\u0085", "\u2028", "\u2029" }, StringSplitOptions.None)
                .Where(line => !line.All(c => c == '\t' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpaceSeparator))
                .Select(line => new ControlPickItem { Id = line, Label = line })
                .ToList();
        }

        /// <summary>
        /// agterm's ask: open is the default, so asking is `ask "Title"`; a title that is literally open, result or cancel
        /// needs `ask open TITLE`.
        /// </summary>
        private static Command Ask()
        {
            Command ask = AskOpen("ask", "Ask a question with buttons, a text field or a choice list, and print the answer as JSON. Use ask open TITLE when the title is 'open', 'result' or 'cancel'.");
            CtlCommand.AddDefault(ask, AskOpen("open", "Ask a question and wait for its answer."));
            ask.Subcommands.Add(ModalResult("ask", "Print a question's result as JSON: pending (exit 1), answered (0), cancelled (2), escaped (3)."));
            ask.Subcommands.Add(ModalCancel("ask", "Cancel a pending question."));
            return ask;
        }

        private static Command AskOpen(string name, string description)
        {
            Argument<string> title = new Argument<string>("title") { Description = "The question." };
            Option<string[]> buttons = new Option<string[]>("--button") { Description = "A button, ID=LABEL or just a label; one to six, in order." };
            Option<string?> message = new Option<string?>("--message") { Description = "Text under the title." };
            Option<string[]> hotkeys = new Option<string[]>("--hotkey") { Description = "ID=LETTER: a letter key presses that button." };
            Option<string?> defaultButton = new Option<string?>("--default") { Description = "The button highlighted on open." };
            Option<string?> destructive = new Option<string?>("--destructive") { Description = "The button marked as destructive; never the default." };
            Option<string?> cancel = new Option<string?>("--cancel") { Description = "The button that ends the question like Esc." };
            Option<string?> style = new Option<string?>("--style") { Description = "terminal (default: over the session, in its font) or gui (a panel over the window)." };
            Option<string?> align = new Option<string?>("--align") { Description = "Where the buttons sit: left, center or right (default)." };
            // agterm takes the width as text, so -5 and abc are its own error and not the parser's.
            Option<string?> width = new Option<string?>("--width") { Description = "A fixed width, 10 to 100 percent of the area." };
            Option<string?> input = new Option<string?>("--input") { Description = "Add a text field: line or multiline." };
            Option<string?> value = new Option<string?>("--value") { Description = "The text field's prefill." };
            Option<string?> placeholder = new Option<string?>("--placeholder") { Description = "The text field's hint." };
            Option<string[]> choices = new Option<string[]>("--choice") { Description = "A list item, ID=LABEL; a radio list, or checkboxes with --multiple." };
            Option<string[]> choiceSubtitles = new Option<string[]>("--choice-subtitle") { Description = "ID=TEXT: a dimmed line under that item." };
            Option<bool> multiple = new Option<bool>("--multiple") { Description = "Checkboxes instead of radio buttons." };
            Option<string[]> checkedIds = new Option<string[]>("--checked") { Description = "An item checked on open (one for a radio list)." };
            Option<string?> other = new Option<string?>("--other") { Description = "Add a free-form line after the items, with this hint." };
            Option<string?> target = new Option<string?>("--target") { Description = "Session id, unique prefix, or 'active'; terminal style defaults to the selected one." };
            Option<string?> pane = new Option<string?>("--pane") { Description = "Anchor to the session's left or right pane. GUI style requires --target." };
            Option<string?> paneId = new Option<string?>("--pane-id") { Description = "Stable pane token ($AGSHELL_PANE_ID); overrides --pane. GUI style requires --target." };
            Option<bool> follow = new Option<bool>("--follow") { Description = "Bring the window to the front." };
            Option<bool> noBlock = new Option<bool>("--no-block") { Description = "Print the question's id and return without waiting." };
            Command ask = new CtlCommand(name, description)
            {
                title, buttons, message, hotkeys, defaultButton, destructive, cancel, style, align, width, input, value, placeholder,
                choices, choiceSubtitles, multiple, checkedIds, other, target, pane, paneId, follow, noBlock,
            };
            ask.SetAction(result =>
            {
                string[] buttonTokens = result.GetValue(buttons) ?? Array.Empty<string>();
                string[] choiceTokens = result.GetValue(choices) ?? Array.Empty<string>();
                string[] checkedTokens = result.GetValue(checkedIds) ?? Array.Empty<string>();
                string[] hotkeyTokens = result.GetValue(hotkeys) ?? Array.Empty<string>();
                string[] subtitleTokens = result.GetValue(choiceSubtitles) ?? Array.Empty<string>();
                // agterm's checks before anything is sent, in its order: the title, the pane, style and align, the width, then
                // buttons (a field or a list brings ok and cancel), hotkeys and subtitles.
                string styleName = result.GetValue(style) ?? "terminal";
                if (result.GetValue(title)!.Trim().Length == 0)
                {
                    return Usage(result, "ask.open requires a title");
                }

                if (styleName == "gui" && (result.GetValue(pane) ?? result.GetValue(paneId)) is not null && result.GetValue(target) is null)
                {
                    return Usage(result, "--pane requires a session");
                }

                if (result.GetValue(pane) is string paneName && paneName is not ("left" or "right" or "top" or "bottom" or "primary" or "split"))
                {
                    return Usage(result, "--pane must be left or right");
                }

                if (styleName is not ("terminal" or "gui"))
                {
                    return Usage(result, "unknown style");
                }

                if (result.GetValue(align) is string alignName && alignName is not ("left" or "center" or "right"))
                {
                    return Usage(result, "unknown align");
                }

                int? widthPercent = null;
                if (result.GetValue(width) is string widthText)
                {
                    if (!int.TryParse(widthText, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int parsed) || parsed is < 10 or > 100)
                    {
                        return Usage(result, "width must be 10 to 100");
                    }

                    widthPercent = parsed;
                }

                if (buttonTokens.Length == 0)
                {
                    if (result.GetValue(input) is null && choiceTokens.Length == 0)
                    {
                        return Usage(result, "ask requires at least one --button");
                    }

                    if (hotkeyTokens.Length > 0)
                    {
                        return Usage(result, "--hotkey requires --button");
                    }
                }

                Dictionary<string, string> keys = new Dictionary<string, string>();
                foreach (string token in hotkeyTokens)
                {
                    if (!token.Contains('='))
                    {
                        return Usage(result, "--hotkey requires ID=LETTER");
                    }

                    (string keyId, string letter) = Pair(token);
                    if (!buttonTokens.Select(Pair).Any(button => button.Id == keyId))
                    {
                        return Usage(result, "unknown hotkey button: " + keyId);
                    }

                    if (!keys.TryAdd(keyId, letter))
                    {
                        return Usage(result, "hotkey already assigned to button: " + keyId);
                    }
                }

                if (choiceTokens.Length == 0)
                {
                    if (subtitleTokens.Length > 0)
                    {
                        return Usage(result, "--choice-subtitle requires --choice");
                    }

                    if (result.GetValue(other) is not null)
                    {
                        return Usage(result, "--other requires --choice");
                    }
                }

                Dictionary<string, string> subtitles = new Dictionary<string, string>();
                foreach (string token in subtitleTokens)
                {
                    if (!token.Contains('='))
                    {
                        return Usage(result, "--choice-subtitle requires ID=TEXT");
                    }

                    (string choiceId, string text) = Pair(token);
                    if (!choiceTokens.Select(Pair).Any(choice => choice.Id == choiceId))
                    {
                        return Usage(result, "unknown subtitle choice: " + choiceId);
                    }

                    if (!subtitles.TryAdd(choiceId, text))
                    {
                        return Usage(result, "subtitle already set for choice: " + choiceId);
                    }
                }

                ControlArgs args = new ControlArgs
                {
                    Title = result.GetValue(title),
                    Message = result.GetValue(message),
                    Buttons = buttonTokens.Length == 0 ? null : buttonTokens.Select(Pair).Select(pair => new ControlAskButton { Id = pair.Id, Label = pair.Text, Hotkey = keys.GetValueOrDefault(pair.Id) }).ToList(),
                    DefaultButton = result.GetValue(defaultButton),
                    DestructiveButton = result.GetValue(destructive),
                    CancelButton = result.GetValue(cancel),
                    Style = result.GetValue(style),
                    Align = result.GetValue(align),
                    Width = widthPercent,
                    Input = result.GetValue(input),
                    Value = result.GetValue(value),
                    Placeholder = result.GetValue(placeholder),
                    Choices = choiceTokens.Length == 0 ? null : choiceTokens.Select(Pair).Select(pair => new ControlAskChoice { Id = pair.Id, Label = pair.Text, Subtitle = subtitles.GetValueOrDefault(pair.Id) }).ToList(),
                    Multiple = Flag(result, multiple),
                    Checked = checkedTokens.Length == 0 ? null : checkedTokens.ToList(),
                    Other = result.GetValue(other),
                    Pane = result.GetValue(pane),
                    PaneId = result.GetValue(paneId),
                };

                return OpenModal(result, "ask", result.GetValue(target), args, result.GetValue(noBlock), result.GetValue(follow));
            });
            return ask;
        }

        /// <summary>ID=TEXT splits at the first equals sign; a token without one is both the id and the text.</summary>
        private static (string Id, string Text) Pair(string token)
        {
            int equals = token.IndexOf('=');
            return equals < 0 ? (token, token) : (token[..equals], token[(equals + 1)..]);
        }

        private static Command ModalResult(string family, string description)
        {
            Argument<string> id = new Argument<string>("id") { Description = "The id the open printed." };
            Command command = new CtlCommand("result", description) { id };
            command.SetAction(result => PollOnce(result, family, result.GetValue(id)!));
            return command;
        }

        private static Command ModalCancel(string family, string description)
        {
            Argument<string> id = new Argument<string>("id") { Description = "The id the open printed." };
            Command command = new CtlCommand("cancel", description) { id };
            command.SetAction(result => Send(result, family + ".cancel", result.GetValue(id), null));
            return command;
        }

        private static int OpenModal(ParseResult result, string family, string? target, ControlArgs args, bool noBlock, bool follow)
        {
            ControlRequest open = new ControlRequest { Cmd = family + ".open", Target = target, Args = args with { Follow = follow ? true : null } };
            if (Request(result, open) is not ControlReply opened)
            {
                return 1;
            }

            if (!opened.Response.Ok)
            {
                // As agterm's, --json prints the refusal's own line.
                if (result.GetValue(Json))
                {
                    Console.WriteLine(opened.Line);
                    return 1;
                }

                return Error(opened.Response.Error ?? "unknown error");
            }

            if (opened.Response.Result?.Id is not string id)
            {
                return Error(family + ".open result missing id");
            }

            if (noBlock)
            {
                Console.WriteLine("{\"id\":\"" + id + "\"}");
                return 0;
            }

            ConsoleCancelEventHandler onCancel = (sender, e) =>
            {
                e.Cancel = true;
                Request(result, new ControlRequest { Cmd = family + ".cancel", Target = id });
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                // agterm's poll: the first read at once, then 100 ms waits for the first second and 500 ms after.
                for (int pending = 1; ; pending++)
                {
                    int? code = PollOnce(result, family, id, quietWhilePending: true);
                    if (code is ReadFailed or MissingResult)
                    {
                        // agterm abandons a dialog it can no longer read, so it does not stay on screen for nobody.
                        try
                        {
                            ControlClient.SendAsync(ControlEndpoint.ResolvePipe(result.GetValue(Pipe)), new ControlRequest { Cmd = family + ".cancel", Target = id }, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                        }
                        catch (ControlClientException)
                        {
                        }

                        return 1;
                    }

                    if (code is int done)
                    {
                        return done;
                    }

                    Thread.Sleep(PollDelay(pending));
                }
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        private static int PollOnce(ParseResult result, string family, string id)
        {
            int? code = PollOnce(result, family, id, quietWhilePending: false);
            return code is null or ReadFailed or MissingResult ? 1 : code.Value;
        }

        /// <summary>agterm's pickPollDelay: human choices take a while, so fast only for the first second.</summary>
        internal static TimeSpan PollDelay(int pendingPolls)
        {
            return TimeSpan.FromMilliseconds(pendingPolls <= 10 ? 100 : 500);
        }

        /// <summary>The result could not be read at all: the app did not answer.</summary>
        private const int ReadFailed = -1;

        /// <summary>The app answered ok without the dialog's result.</summary>
        private const int MissingResult = -2;

        /// <summary>Prints the result and returns its exit code; null while pending when asked to stay quiet.</summary>
        private static int? PollOnce(ParseResult result, string family, string id, bool quietWhilePending)
        {
            if (Request(result, new ControlRequest { Cmd = family + ".result", Target = id }) is not ControlReply reply)
            {
                return ReadFailed;
            }

            // As agterm's, --json prints the refusal's own line on stdout.
            if (!reply.Response.Ok)
            {
                if (result.GetValue(Json))
                {
                    Console.WriteLine(reply.Line);
                    return 1;
                }

                return Error(reply.Response.Error ?? "unknown error");
            }

            (string? state, string line) = family == "pick"
                ? (reply.Response.Result?.Pick?.Result, reply.Response.Result?.Pick is ControlPickResult pick ? ControlJson.Serialize(pick) : string.Empty)
                : (reply.Response.Result?.Ask?.Result, reply.Response.Result?.Ask is ControlAskResult ask ? ControlJson.Serialize(ask) : string.Empty);
            if (state is null)
            {
                Error(family + ".result missing result");
                return MissingResult;
            }

            if (state == "pending" && quietWhilePending)
            {
                return null;
            }

            Console.WriteLine(line);
            return state switch
            {
                "picked" or "custom" or "answered" => 0,
                "cancelled" => CancelledExitCode,
                "escaped" => EscapedExitCode,
                _ => 1,
            };
        }
    }
}

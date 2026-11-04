using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>agterm's pick and ask: the arguments are checked here, before the model's modal state changes.</summary>
    public sealed partial class ControlDispatcher
    {
        private const int MaxPickItems = 1000;
        private const int MaxAskButtons = 6;
        private const int MaxAskChoices = 50;
        private const int MaxAskTextBytes = 64 * 1024;

        private static readonly ControlAskButton[] FormButtons =
        {
            new ControlAskButton { Id = "ok", Label = "OK" },
            new ControlAskButton { Id = "cancel", Label = "Cancel" },
        };

        private ControlResponse OpenPick(ControlRequest request)
        {
            ControlArgs args = request.Args ?? new ControlArgs();
            if (args.Items is not List<ControlPickItem> items)
            {
                return Fail("pick.open requires items");
            }

            bool allowCustom = args.AllowCustom == true;
            if (items.Count == 0 && !allowCustom)
            {
                return Fail("pick.open requires at least one item");
            }

            if (items.Count > MaxPickItems)
            {
                return Fail("too many items (max " + MaxPickItems + ")");
            }

            if (items.Any(item => string.IsNullOrEmpty(item.Label)))
            {
                return Fail("pick item label must not be empty");
            }

            if (items.Select(item => item.Id).Distinct().Count() != items.Count)
            {
                return Fail("pick item ids must be unique");
            }

            if (items.Any(item => HasControl(item.Label) || (item.Subtitle is string subtitle && HasControl(subtitle))))
            {
                return Fail("item text must not contain control characters");
            }

            if (items.Select(PickItemStatusError).FirstOrDefault(error => error is not null) is string statusError)
            {
                return Fail(statusError);
            }

            if (args.Selection is string selection && items.All(item => item.Id != selection))
            {
                return Fail("pick select must name an item id");
            }

            PickRequest pick = new PickRequest
            {
                Id = Ids.New(),
                Items = items,
                Prompt = args.Prompt,
                Query = args.Query,
                Selection = args.Selection,
                AllowCustom = allowCustom,
            };
            return model.OpenPick(pick) is string error ? Fail(error) : Ok(new ControlResult { Id = pick.Id });
        }

        private ControlResponse PickResult(ControlRequest request)
        {
            if (request.Target is not string id)
            {
                return Fail("pick.result requires a pick id");
            }

            return model.PickResult(id) is ControlPickResult result ? Ok(new ControlResult { Pick = result }) : Fail("unknown pick: " + id);
        }

        private ControlResponse CancelPick(ControlRequest request)
        {
            if (request.Target is not string id)
            {
                return Fail("pick.cancel requires a pick id");
            }

            return model.CancelPick(id) ? Ok(new ControlResult()) : Fail("unknown pick: " + id);
        }

        private static string? PickItemStatusError(ControlPickItem item)
        {
            if (item.Status is string status && !AgentStatuses.TryParse(status, out _))
            {
                return "invalid pick item status (idle|active|completed|blocked)";
            }

            if (item.StatusColor is string color && Themes.Color(color) is null)
            {
                return "invalid pick item color (expected #rrggbb)";
            }

            return item.StatusShape is string shape && !AgentStatuses.Shapes.Contains(shape)
                ? "invalid pick item shape (" + string.Join("|", AgentStatuses.Shapes) + ")"
                : null;
        }

        private ControlResponse OpenAsk(ControlRequest request)
        {
            ControlArgs args = request.Args ?? new ControlArgs();
            if (string.IsNullOrWhiteSpace(args.Title))
            {
                return Fail("ask.open requires a title");
            }

            if (AskFormError(args) is string formError)
            {
                return Fail(formError);
            }

            bool form = args.Input is not null || args.Choices is not null;
            bool generated = args.Buttons is null && form;
            List<ControlAskButton>? buttons = args.Buttons ?? (generated ? FormButtons.ToList() : null);
            if (buttons is null)
            {
                return Fail("ask.open requires buttons");
            }

            string? defaultButton = args.DefaultButton ?? (generated ? "ok" : null);
            string? cancelButton = args.CancelButton ?? (generated ? "cancel" : null);
            if (buttons.Count == 0)
            {
                return Fail("ask.open requires at least one button");
            }

            if (buttons.Count > MaxAskButtons)
            {
                return Fail("too many buttons (max " + MaxAskButtons + ")");
            }

            if (buttons.Any(button => string.IsNullOrEmpty(button.Label)))
            {
                return Fail("ask button label must not be empty");
            }

            HashSet<string> ids = new HashSet<string>();
            if (!buttons.All(button => ids.Add(button.Id)))
            {
                return Fail("ask button ids must be unique");
            }

            if (HasControl(args.Title) || HasControl(args.Message ?? string.Empty) || buttons.Any(button => HasControl(button.Label)))
            {
                return Fail("ask text must not contain control characters");
            }

            foreach ((string role, string? id) in new[] { ("default", defaultButton), ("destructive", args.DestructiveButton), ("cancel", cancelButton) })
            {
                if (id is not null && !ids.Contains(id))
                {
                    return Fail("unknown " + role + " button: " + id);
                }
            }

            if (args.DestructiveButton is not null && defaultButton == args.DestructiveButton)
            {
                return Fail("default button must not be destructive");
            }

            if (args.Input is not null && buttons.Any(button => button.Hotkey is not null))
            {
                return Fail("ask input does not take hotkeys");
            }

            string style = args.Style ?? "terminal";
            if (style is not ("terminal" or "gui"))
            {
                return Fail("unknown style");
            }

            string align = args.Align ?? "right";
            if (align is not ("left" or "center" or "right"))
            {
                return Fail("unknown align");
            }

            if (args.Width is int width && (width < 10 || width > 100))
            {
                return Fail("width must be 10 to 100");
            }

            HashSet<string> hotkeys = new HashSet<string>();
            foreach (ControlAskButton button in buttons.Where(button => button.Hotkey is not null))
            {
                if (button.Hotkey!.Length != 1 || !char.IsAsciiLetter(button.Hotkey[0]))
                {
                    return Fail("ask button hotkey must be one ASCII letter");
                }

                if (!hotkeys.Add(button.Hotkey.ToLowerInvariant()))
                {
                    return Fail("ask button hotkeys must be unique");
                }
            }

            if (style == "gui" && (args.Pane is not null || args.PaneId is not null) && request.Target is null)
            {
                return Fail("--pane requires a session");
            }

            PaneRole? pane = null;
            if (args.Pane is string paneName)
            {
                if (!Panes.TryParse(paneName, out PaneRole parsed) || parsed == PaneRole.Scratch)
                {
                    return Fail("--pane must be left or right");
                }

                pane = parsed;
            }

            Session? session = style == "terminal" || request.Target is not null ? ResolveSession(request.Target) : null;
            // agterm's --pane-id: the pane's stable token wins over --pane.
            if (session is not null && args.PaneId is { Length: > 0 } && PaneById(session, args, alwaysFallBack: false) is PaneRole byId)
            {
                if (byId == PaneRole.Scratch)
                {
                    return Fail("ask pane must be left or right");
                }

                pane = byId;
            }

            // agterm hands a question to the session's presenter before anything about this window counts: nothing is drawn
            // here, so a GUI question's session need not be selected.
            // agterm: a question with a text field, a choice list or a cancel role needs a presenter that draws forms, and one with
            // a free-form row one that draws that too; otherwise it stays here.
            bool needsForm = args.Input is not null || args.Choices is not null || cancelButton is not null;
            bool handOver = session is not null && model.CanHandAskOver(session.Id, pane, role => host.PaneLead(session.Id, role))
                && (!needsForm || model.PresenterSupports(session.Id, ControlPresentationKinds.AskForm))
                && (args.Other is null || model.PresenterSupports(session.Id, ControlPresentationKinds.AskOther))
                && FitsOneFrame(args, buttons);
            // agterm's resolvePanePlacement: with a presenter the pane need only exist, checked before anything decides whether
            // the question goes there (one that then stays here is still refused for a missing split); one drawn here needs it
            // on screen, and a GUI one its session in view, not under a zoom or the dashboard.
            if (session is not null && model.HasPresenter(session.Id) && pane == PaneRole.Split && !session.HasSplit)
            {
                return Fail("session has no split");
            }

            if (session is not null && !handOver)
            {
                if (style == "gui" && (session.Id != model.SelectedSessionId || model.Zoomed || model.Dashboard is not null))
                {
                    return Fail("session not visible");
                }

                if (pane is PaneRole anchor && !session.RendersPane(anchor))
                {
                    return Fail("pane not visible");
                }
            }

            AskRequest ask = new AskRequest
            {
                Id = Ids.New(),
                Title = args.Title,
                Message = args.Message,
                Buttons = buttons.Select(button => button with { Hotkey = button.Hotkey?.ToLowerInvariant() }).ToList(),
                DefaultButton = defaultButton,
                DestructiveButton = args.DestructiveButton,
                CancelButton = cancelButton,
                Style = style,
                Align = align,
                Width = args.Width,
                Input = args.Input,
                InputValue = args.Input is null ? null : args.Value ?? string.Empty,
                Placeholder = args.Placeholder,
                Choices = args.Choices,
                Multiple = args.Multiple == true,
                Checked = args.Checked,
                Other = args.Other,
                SessionId = session?.Id,
                Pane = pane,
            };
            if ((handOver ? model.OpenHandedAsk(ask) : model.OpenAsk(ask)) is string error)
            {
                return Fail(error);
            }

            // A question handed to the presenter is drawn there: nothing here is raised for it.
            if (args.Follow == true && !handOver)
            {
                host.Raise();
            }

            return Ok(new ControlResult { Id = ask.Id, Pane = pane is PaneRole placed ? Panes.Name(placed) : null });
        }

        /// <summary>
        /// A question is handed over only when it, and the largest answer it allows, fit one stream frame (256 KB); a bigger
        /// one stays here. agterm checks only a question with a form, so a bigger plain one is handed over, fails to encode
        /// and waits unseen; this checks every question.
        /// </summary>
        private static bool FitsOneFrame(ControlArgs args, List<ControlAskButton> buttons)
        {
            const int frameLimit = 256 * 1024;
            int question = System.Text.Encoding.UTF8.GetByteCount(ControlJson.Serialize(new ControlRequest { Cmd = "ask.open", Args = args with { Buttons = buttons } }));
            if (question > frameLimit)
            {
                return false;
            }

            // agterm's worst answer: the field or the free-form line full of quotes, each escaped to two bytes, and with a
            // free-form line on a checkbox list every id selected as well.
            if (args.Input is null && args.Other is null)
            {
                return true;
            }

            int selected = args.Other is not null && args.Multiple == true ? (args.Choices ?? new List<ControlAskChoice>()).Sum(choice => System.Text.Encoding.UTF8.GetByteCount(choice.Id) * 2 + 3) : 0;
            int button = buttons.Max(item => System.Text.Encoding.UTF8.GetByteCount(item.Id)) * 2;
            return MaxAskTextBytes * 2 + selected + button + 512 <= frameLimit;
        }

        /// <summary>The text field and the choice list: one or the other, each with only its own arguments.</summary>
        private static string? AskFormError(ControlArgs args)
        {
            if (args.Input is not null && args.Choices is not null)
            {
                return "input and choices are exclusive";
            }

            if (args.Input is null)
            {
                if (args.Value is not null)
                {
                    return "value requires input";
                }

                if (args.Placeholder is not null)
                {
                    return "placeholder requires input";
                }
            }

            if (args.Choices is null)
            {
                if (args.Checked is not null)
                {
                    return "checked requires choices";
                }

                if (args.Multiple is not null)
                {
                    return "multiple requires choices";
                }

                if (args.Other is not null)
                {
                    return "other requires choices";
                }
            }

            if (args.Input is string input)
            {
                if (input is not ("line" or "multiline"))
                {
                    return "unknown input";
                }

                string value = args.Value ?? string.Empty;
                if (System.Text.Encoding.UTF8.GetByteCount(value) > MaxAskTextBytes)
                {
                    return "ask text too long (max " + MaxAskTextBytes + " bytes)";
                }

                bool multiline = input == "multiline";
                if (HasControl(args.Placeholder ?? string.Empty) || value.Any(c => (c < ' ' || c == '\u007f') && !(multiline && c is '\n' or '\t')))
                {
                    return "ask text must not contain control characters";
                }
            }

            if (args.Choices is List<ControlAskChoice> choices)
            {
                if (choices.Count < 1 || choices.Count > MaxAskChoices)
                {
                    return "ask choices must be 1 to " + MaxAskChoices;
                }

                if (choices.Any(choice => string.IsNullOrEmpty(choice.Label)))
                {
                    return "ask choice label must not be empty";
                }

                HashSet<string> ids = new HashSet<string>();
                if (!choices.All(choice => ids.Add(choice.Id)))
                {
                    return "ask choice ids must be unique";
                }

                if (choices.Any(choice => HasControl(choice.Label) || (choice.Subtitle ?? string.Empty).Any(c => (c < ' ' || c == '\u007f') && c != '\n')))
                {
                    return "ask text must not contain control characters";
                }

                List<string> checkedIds = args.Checked ?? new List<string>();
                if (checkedIds.Distinct().Count() != checkedIds.Count)
                {
                    return "ask checked ids must be unique";
                }

                if (checkedIds.FirstOrDefault(id => !ids.Contains(id)) is string unknown)
                {
                    return "unknown checked choice: " + unknown;
                }

                if (args.Multiple != true && checkedIds.Count > 1)
                {
                    return "radio choices accept one checked id";
                }

                if (args.Other is string other)
                {
                    if (string.IsNullOrWhiteSpace(other))
                    {
                        return "ask other placeholder must not be blank";
                    }

                    if (HasControl(other))
                    {
                        return "ask text must not contain control characters";
                    }
                }
            }

            return null;
        }

        private ControlResponse AskResult(ControlRequest request)
        {
            if (request.Target is not string id)
            {
                return Fail("ask.result requires an ask id");
            }

            return model.AskResult(id) is ControlAskResult result ? Ok(new ControlResult { Ask = result }) : Fail("unknown ask: " + id);
        }

        private ControlResponse CancelAsk(ControlRequest request)
        {
            if (request.Target is not string id)
            {
                return Fail("ask.cancel requires an ask id");
            }

            return model.CancelAsk(id) ? Ok(new ControlResult()) : Fail("unknown ask: " + id);
        }

        private static bool HasControl(string text)
        {
            return text.Any(c => c < ' ' || c == '\u007f');
        }
    }
}

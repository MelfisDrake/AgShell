using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AgShell.Protocol
{
    /// <summary>
    /// agterm's PresentationFrame on the wire: kind, gen and rev, and the body under the key of its kind (hello, snapshot,
    /// status, context, layout, hud, notify, sound, ask, overlay, page). In memory the frame stays one flat record; a frame
    /// missing kind, gen or rev, or a body its kind needs, is malformed, as agterm's codec refuses it.
    /// </summary>
    public sealed class PresentationFrameConverter : JsonConverter<ControlPresentationFrame>
    {
        public override ControlPresentationFrame Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("frame is not an object");
            }

            string kind = Required(root, "kind").GetString() ?? throw new JsonException("missing kind");
            int gen = Required(root, "gen").GetInt32();
            int rev = Required(root, "rev").GetInt32();
            ControlPresentationFrame frame = new ControlPresentationFrame { Kind = kind, Gen = gen, Rev = rev };
            switch (kind)
            {
                case ControlPresentationKinds.Hello:
                    JsonElement hello = Required(root, "hello");
                    return frame with
                    {
                        Version = Required(hello, "version").GetInt32(),
                        Kinds = Required(hello, "kinds").Deserialize(Info<List<string>>(options)) ?? throw new JsonException("missing kinds"),
                        PresentationMode = Required(hello, "mode").GetString() is string mode && mode is "mirror" or "presenter" ? mode : throw new JsonException("hello.mode is neither mirror nor presenter"),
                    };
                case ControlPresentationKinds.Snapshot:
                    return frame with { Snapshot = Body<ControlPresentation>(root, "snapshot", options) };
                case ControlPresentationKinds.Status:
                    return frame with { Status = Optional<ControlPresentationStatus>(root, "status", options) };
                case ControlPresentationKinds.Context:
                    return frame with { Context = root.TryGetProperty("context", out JsonElement context) && context.ValueKind != JsonValueKind.Null ? context.GetString() : null };
                case ControlPresentationKinds.Layout:
                    return frame with { Layout = root.TryGetProperty("layout", out JsonElement layout) ? PresentationLayoutConverter.Read(layout) : ControlPresentationLayout.Invalid };
                case ControlPresentationKinds.Hud:
                    return frame with { Hud = Optional<ControlPresentationHud>(root, "hud", options) };
                case ControlPresentationKinds.Notify:
                    JsonElement notify = Required(root, "notify");
                    return frame with { Title = Required(notify, "title").GetString(), Body = Required(notify, "body").GetString() };
                case ControlPresentationKinds.Sound:
                    JsonElement sound = Required(root, "sound");
                    return frame with { Title = sound.TryGetProperty("name", out JsonElement name) && name.ValueKind != JsonValueKind.Null ? name.GetString() : null };
                case ControlPresentationKinds.AskRequest:
                    return frame with { Ask = Body<ControlPresentationAsk>(root, "ask", options) };
                case ControlPresentationKinds.AskResolve:
                    JsonElement answer = Required(root, "ask");
                    return Reference(frame, answer, "id") with
                    {
                        Button = String(answer, "button"),
                        Text = String(answer, "text"),
                        Selected = answer.TryGetProperty("selected", out JsonElement selected) && selected.ValueKind != JsonValueKind.Null ? selected.Deserialize(Info<List<string>>(options)) : null,
                    };
                case ControlPresentationKinds.AskRejected:
                case ControlPresentationKinds.AskDismiss:
                    JsonElement ask = Required(root, "ask");
                    return Reference(frame, ask, "id") with { Text = String(ask, "text") };
                case ControlPresentationKinds.OverlayRequest:
                case ControlPresentationKinds.OverlayResize:
                    return frame with { Overlay = Body<ControlPresentationOverlay>(root, "overlay", options) };
                case ControlPresentationKinds.OverlayRejected:
                case ControlPresentationKinds.OverlayClose:
                case ControlPresentationKinds.OverlayClosed:
                    return Reference(frame, Required(root, "overlay"), "job");
                case ControlPresentationKinds.PageRequest:
                    return frame with { Page = Body<ControlPresentationPage>(root, "page", options) };
                case ControlPresentationKinds.PageRejected:
                case ControlPresentationKinds.PageClose:
                case ControlPresentationKinds.PageClosed:
                    JsonElement change = Required(root, "page");
                    return Reference(frame, change, "page") with { Reason = String(change, "reason") };
                case ControlPresentationKinds.PageCommandKind:
                    JsonElement command = Required(root, "page");
                    return Reference(frame, command, "page") with { PageCommand = command.Deserialize(Info<ControlPresentationPageCommand>(options)) ?? throw new JsonException("missing page") };
                case ControlPresentationKinds.PageState:
                    JsonElement state = Required(root, "page");
                    return Reference(frame, state, "page") with { PageState = state.Deserialize(Info<ControlPresentationPageState>(options)) ?? throw new JsonException("missing page") };
                default:
                    // ping, ack, the presenter role and a kind of a newer side: no body; agterm's unknown is ignored.
                    return frame;
            }
        }

        public override void Write(Utf8JsonWriter writer, ControlPresentationFrame frame, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", frame.Kind);
            writer.WriteNumber("gen", frame.Gen);
            writer.WriteNumber("rev", frame.Rev);
            switch (frame.Kind)
            {
                case ControlPresentationKinds.Hello:
                    writer.WriteStartObject("hello");
                    writer.WriteNumber("version", frame.Version ?? 0);
                    writer.WritePropertyName("kinds");
                    JsonSerializer.Serialize(writer, frame.Kinds ?? new List<string>(), Info<List<string>>(options));
                    writer.WriteString("mode", frame.PresentationMode ?? "mirror");
                    writer.WriteEndObject();
                    break;
                case ControlPresentationKinds.Snapshot:
                    WriteBody(writer, "snapshot", frame.Snapshot, options);
                    break;
                case ControlPresentationKinds.Status:
                    WriteBody(writer, "status", frame.Status, options);
                    break;
                case ControlPresentationKinds.Context:
                    if (frame.Context is not null)
                    {
                        writer.WriteString("context", frame.Context);
                    }

                    break;
                case ControlPresentationKinds.Layout:
                    WriteBody(writer, "layout", frame.Layout, options);
                    break;
                case ControlPresentationKinds.Hud:
                    WriteBody(writer, "hud", frame.Hud, options);
                    break;
                case ControlPresentationKinds.Notify:
                    writer.WriteStartObject("notify");
                    writer.WriteString("title", frame.Title ?? string.Empty);
                    writer.WriteString("body", frame.Body ?? string.Empty);
                    writer.WriteEndObject();
                    break;
                case ControlPresentationKinds.Sound:
                    writer.WriteStartObject("sound");
                    if (frame.Title is not null)
                    {
                        writer.WriteString("name", frame.Title);
                    }

                    writer.WriteEndObject();
                    break;
                case ControlPresentationKinds.AskRequest:
                    WriteBody(writer, "ask", frame.Ask, options);
                    break;
                case ControlPresentationKinds.AskResolve:
                case ControlPresentationKinds.AskRejected:
                case ControlPresentationKinds.AskDismiss:
                    writer.WriteStartObject("ask");
                    WriteReference(writer, frame, "id");
                    WriteString(writer, "button", frame.Button);
                    WriteString(writer, "text", frame.Text);
                    if (frame.Selected is not null)
                    {
                        writer.WritePropertyName("selected");
                        JsonSerializer.Serialize(writer, frame.Selected, Info<List<string>>(options));
                    }

                    writer.WriteEndObject();
                    break;
                case ControlPresentationKinds.OverlayRequest:
                case ControlPresentationKinds.OverlayResize:
                    WriteBody(writer, "overlay", frame.Overlay, options);
                    break;
                case ControlPresentationKinds.OverlayRejected:
                case ControlPresentationKinds.OverlayClose:
                case ControlPresentationKinds.OverlayClosed:
                    writer.WriteStartObject("overlay");
                    WriteReference(writer, frame, "job");
                    writer.WriteEndObject();
                    break;
                case ControlPresentationKinds.PageRequest:
                    WriteBody(writer, "page", frame.Page, options);
                    break;
                case ControlPresentationKinds.PageRejected:
                case ControlPresentationKinds.PageClose:
                case ControlPresentationKinds.PageClosed:
                    writer.WriteStartObject("page");
                    WriteReference(writer, frame, "page");
                    WriteString(writer, "reason", frame.Reason);
                    writer.WriteEndObject();
                    break;
                case ControlPresentationKinds.PageCommandKind:
                    WriteMerged(writer, frame, JsonSerializer.SerializeToElement(frame.PageCommand!, Info<ControlPresentationPageCommand>(options)));
                    break;
                case ControlPresentationKinds.PageState:
                    WriteMerged(writer, frame, JsonSerializer.SerializeToElement(frame.PageState!, Info<ControlPresentationPageState>(options)));
                    break;
            }

            writer.WriteEndObject();
        }

        private static JsonTypeInfo<T> Info<T>(JsonSerializerOptions options)
        {
            return (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        }

        private static JsonElement Required(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value : throw new JsonException("missing " + name);
        }

        private static string? String(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
        }

        private static T Body<T>(JsonElement root, string name, JsonSerializerOptions options)
        {
            return Required(root, name).Deserialize(Info<T>(options)) ?? throw new JsonException("missing " + name);
        }

        /// <summary>A body absent or null is agterm's idle status, no HUD.</summary>
        private static T? Optional<T>(JsonElement root, string name, JsonSerializerOptions options) where T : class
        {
            return root.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.Deserialize(Info<T>(options)) : null;
        }

        /// <summary>The handed-over thing a change names, by agterm's key for it, and the presenter generation it went under.</summary>
        private static ControlPresentationFrame Reference(ControlPresentationFrame frame, JsonElement body, string key)
        {
            return frame with
            {
                Id = Required(body, key).GetString(),
                Owner = body.TryGetProperty("owner", out JsonElement owner) && owner.ValueKind == JsonValueKind.Number ? owner.GetInt32() : null,
            };
        }

        private static void WriteReference(Utf8JsonWriter writer, ControlPresentationFrame frame, string key)
        {
            writer.WriteString(key, frame.Id ?? string.Empty);
            if (frame.Owner is int owner)
            {
                writer.WriteNumber("owner", owner);
            }
        }

        private static void WriteString(Utf8JsonWriter writer, string name, string? value)
        {
            if (value is not null)
            {
                writer.WriteString(name, value);
            }
        }

        private static void WriteBody<T>(Utf8JsonWriter writer, string name, T? value, JsonSerializerOptions options) where T : class
        {
            if (value is not null)
            {
                writer.WritePropertyName(name);
                JsonSerializer.Serialize(writer, value, Info<T>(options));
            }
        }

        /// <summary>agterm's page command and page state: the page's id beside the fields, in one object.</summary>
        private static void WriteMerged(Utf8JsonWriter writer, ControlPresentationFrame frame, JsonElement fields)
        {
            writer.WriteStartObject("page");
            WriteReference(writer, frame, "page");
            foreach (JsonProperty property in fields.EnumerateObject())
            {
                if (property.Name is not ("page" or "owner"))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// agterm's PresentationLayout decoding: a layout it cannot read becomes the invalid one rather than failing the frame,
    /// so a snapshot keeps the rest of its state and the stream stays up; the viewer then warns and skips it.
    /// </summary>
    public sealed class PresentationLayoutConverter : JsonConverter<ControlPresentationLayout>
    {
        public override ControlPresentationLayout Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            return Read(document.RootElement);
        }

        public static ControlPresentationLayout Read(JsonElement element)
        {
            try
            {
                List<string> panes = element.GetProperty("panes").EnumerateArray().Select(pane => Guid.Parse(pane.GetString()!).Wire()).ToList();
                string primary = Guid.Parse(element.GetProperty("primary").GetString()!).Wire();
                string? axis = element.TryGetProperty("axis", out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
                return new ControlPresentationLayout { Panes = panes, Primary = primary, Axis = axis, Shown = element.GetProperty("shown").GetBoolean() };
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is KeyNotFoundException || ex is FormatException || ex is ArgumentNullException)
            {
                return ControlPresentationLayout.Invalid;
            }
        }

        public override void Write(Utf8JsonWriter writer, ControlPresentationLayout layout, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteStartArray("panes");
            foreach (string pane in layout.Panes)
            {
                writer.WriteStringValue(pane);
            }

            writer.WriteEndArray();
            writer.WriteString("primary", layout.Primary);
            if (layout.Axis is not null)
            {
                writer.WriteString("axis", layout.Axis);
            }

            writer.WriteBoolean("shown", layout.Shown);
            writer.WriteEndObject();
        }
    }

    /// <summary>agterm's PresentationPane: a main or split pane travels as its stable identity, a uuid, the scratch by name.</summary>
    public sealed class PresentationPaneConverter : JsonConverter<string>
    {
        public const string Scratch = "scratch";

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? text = reader.GetString();
            return text == Scratch ? text : Guid.TryParse(text, out Guid id) ? id.Wire() : throw new JsonException("pane is neither scratch nor a uuid");
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }
}

# Backend JSON field editors

The existing `MesHttp.JsonTemplate`, `MesHttp.ResponseMappings`, and listener subscription/reply/input-mapping properties remain the saved configuration contract. No new runtime schema or network call is introduced by these editors. Runtime rendering and mapping remain owned by `MesJsonCodec`.

## Web configuration

- **Template field editor (updates configuration)** displays an expandable table of JSON paths, native value types, and values. Select **Edit** to change an existing field, **Delete** to remove it, or enter a new path/type/value and select **Add field**. An add action cannot silently replace an existing field. Deleting an array element closes the index gap.
- New field paths accept dot names and array indices, such as `data.count` or `items[0].ok`. Missing object/array containers are created where the path is unambiguous. Sparse array insertion is rejected. Literal property names containing punctuation are displayed as escaped JSON Pointer paths; editing such a row preserves the literal name.
- Select the `string` type to enter text or a placeholder such as `${mapped.serialNo}` without surrounding quotes. Other types require matching JSON values, for example `false`, `12.5`, `{ "ok": true }`, or `[1, 2]`. The `null` type supplies JSON null. The existing full JSON text area remains editable.
- **Local path mapping preview** shows the original sample and mapped output. Selecting a sample field adds a persisted source/target mapping; the suggested target is editable. Whole object/array selections retain their JSON types. Source paths accept the runtime's dot/index syntax and JSON Pointer syntax; target paths use dot names and array indices.
- **Local JSON preview** displays input context fields and rendered output fields. A whole-string placeholder preserves the selected JSON type; a placeholder embedded in surrounding text becomes a string. SignalR subscription/reply outputs must be arrays of arguments.

## Listener preview

The sample message feeds both input mappings and reply rendering. `input` contains the original sample; `mapped` contains the mapping output. With no mappings, the original value is used unchanged, including an explicit JSON null. Invalid sample JSON or mapping errors are visible and prevent a successful reply preview.

Subscription templates use only `taskId` and `lastEventId`. Normal and ignored/baseline-only replies use `input`, `mapped`, `fields`, and `vars`; `fields` is null in listener replies. Actual flow variables depend on execution.

SSE disables subscription/reply template editing and preview while retaining the saved templates for a later protocol switch. HTTP request templates and response mappings use the same reusable field editors.

## WPF configuration

The HTTP configuration exposes separate offline request-template and response-mapping preview buttons. Listener configuration exposes an input/mapping/subscription/reply preview window. Each window provides an editable local sample, read-only path/type/value tables, and formatted output JSON. It calls `MesJsonCodec.Render` and `MesJsonCodec.Map` directly and validates SignalR argument-array outputs. WPF template and mapping configuration remains editable in the existing JSON text boxes.

## Sample and save boundaries

Preview samples are temporary UI state. They are not serialized to production configuration, and WPF samples are discarded when the preview window closes. Previews do not connect to a hub/backend or send device commands. Editing a sample alone never marks template or mapping configuration changed; adding/editing/deleting a template field or adding/editing/deleting a mapping does. Existing project save/apply behavior remains responsible for applying configuration changes.

JSON field tables cap inspection at 10000 fields and 100 levels and report an explicit error above that limit. Web previews use native JavaScript JSON values; WPF and runtime use `System.Text.Json`.

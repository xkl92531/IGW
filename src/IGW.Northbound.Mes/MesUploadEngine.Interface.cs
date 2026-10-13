using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging;

namespace IGW.Northbound.Mes;

public sealed partial class MesUploadEngine
{
    private readonly AsyncLocal<InterfaceContext?> _interfaceContext = new();
    private readonly MesInterfaceInbox? _interfaceInbox;
    private bool IsEventExecution => _interfaceContext.Value is not null;
    private static bool IsInterfaceTask(MesTask task) => string.Equals(task.Trigger.Mode, "InterfaceEvent", StringComparison.OrdinalIgnoreCase);

    private sealed class InterfaceContext(MesInterfaceInbox inbox, MesInboxMessage message, JsonElement input, JsonElement mapped)
    {
        public MesInboxMessage Message { get; } = message;
        public JsonElement Input { get; } = input;
        public JsonElement Mapped { get; } = mapped;
        public string FreezeBody(string step, string body) => inbox.FreezeBody(Message.Sequence, step, body);
    }

    private static void ValidateListener(MesTask task)
    {
        var s = task.Listener;
        if (string.IsNullOrWhiteSpace(task.Id)) throw new ArgumentException("监听任务需要稳定的任务编号。");
        bool ws = s.Protocol == "WebSocket", sse = s.Protocol == "SSE", signalR = s.Protocol == "SignalR";
        if ((!ws && !sse && !signalR) || !Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) ||
            (ws ? uri.Scheme is not ("ws" or "wss") : uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("监听地址与 SignalR/WebSocket/SSE 协议不匹配。");
        if (s.ChangeFields.Count == 0) throw new ArgumentException("监听任务至少需要一个变化字段路径。");
        if (s.ReconnectMs is < 100 or > 60000 || s.IdleTimeoutSec is < 1 or > 3600 ||
            s.MaxMessageBytes is < 64 or > 16777216)
            throw new ArgumentException("监听重连、空闲超时或报文大小配置超出范围。");
        if (!sse && string.IsNullOrWhiteSpace(s.IdField)) throw new ArgumentException("监听必须配置唯一消息编号路径。");
        if (signalR && (string.IsNullOrWhiteSpace(s.ReceiveMethod) ||
            ((s.ResponseTemplate.Length > 0 || s.IgnoreResponseTemplate.Length > 0) && string.IsNullOrWhiteSpace(s.ReplyMethod))))
            throw new ArgumentException("SignalR 需要接收方法名，配置回执模板时还需要回执方法名。");
        string activeSubscription = signalR && string.IsNullOrWhiteSpace(s.SubscribeMethod) ? "" : s.SubscriptionTemplate;
        foreach (var template in new[] { activeSubscription, s.ResponseTemplate, s.IgnoreResponseTemplate }.Where(t => !sse && !string.IsNullOrWhiteSpace(t)))
            using (JsonDocument.Parse(template)) { }
    }

    private async Task InterfaceEventLoopAsync(MesTask task)
    {
        var inbox = _interfaceInbox!;
        var settings = task.Listener;
        string scope = task.Id;
        await using var client = new MesInterfaceClient(_http);
        var st = _stats[task.Id];
        var trigger = task.Flow?.Nodes.FirstOrDefault(n => n.Type == "Trigger");
        string triggerId = trigger?.Id ?? "trigger";
        try
        {
            string signature = JsonSerializer.Serialize(new
            {
                settings.Protocol, settings.Url, settings.ReceiveMethod, settings.EventName, settings.IdField,
                settings.ChangeFields, settings.TriggerOnFirst, settings.InputMappings,
                task.Steps, task.Payload,
                nodes = task.Flow?.Nodes.Select(n => new { n.Id, n.Type, n.Http, n.Payload, n.WriteBack, n.Captures,
                    n.Conditions, n.Branches, n.JudgeConds, n.JudgeField, n.JudgeValue, n.JudgeOp, n.JudgeCombine, n.Cases, n.SwitchTag, n.DelayMs }),
                edges = task.Flow?.Edges,
            });
            inbox.Configure(scope, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))));
        }
        catch (Exception error)
        {
            _lastError = st.LastError = error.Message; Mark(st, triggerId, "fail", error.Message); return;
        }
        var edges = task.Flow?.Edges.ToDictionary(e => (e.From, e.FromPort)) ?? new();
        var nodes = task.Flow?.Nodes.ToDictionary(n => n.Id) ?? new();
        var conditionPrevious = new Dictionary<MesCondition, string>();
        // Rebuild message-field Changed baselines from the last committed immutable message on restart.
        if (inbox.LastCommitted(scope) is { } committed)
        {
            using var previousDocument = JsonDocument.Parse(committed.Json);
            var previousInput = previousDocument.RootElement.Clone();
            _interfaceContext.Value = new InterfaceContext(inbox, committed, previousInput, MesJsonCodec.Map(previousInput, settings.InputMappings));
            var changedConditions = task.Flow is { Nodes.Count: > 0 }
                ? task.Flow.Nodes.Where(n => n.Type == "Condition").SelectMany(n => n.Branches).SelectMany(b => b.Conditions)
                : task.Steps.SelectMany(s => s.Conditions);
            foreach (var condition in changedConditions.Where(c => c.Op == "Changed" && c.Source is "Input" or "Mapped"))
                EvalCond(condition, conditionPrevious);
            _interfaceContext.Value = null;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var receive = Receive();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var pending = inbox.Pending(scope);
                if (pending is null) { await Task.Delay(50, stop.Token); continue; }
                try
                {
                    using var document = JsonDocument.Parse(pending.Json);
                    var input = document.RootElement.Clone();
                    _interfaceContext.Value = new InterfaceContext(inbox, pending, input, MesJsonCodec.Map(input, settings.InputMappings));
                    string baseline = ChangeValue(input, settings.ChangeFields);
                    string? previous = inbox.Baseline(scope);
                    bool changed = previous is null ? settings.TriggerOnFirst : !JsonEquals(previous, baseline);
                    var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _flowValues.Value = new FlowValues(variables);
                    if (changed)
                    {
                        MarkFired(st, triggerId, "接口字段变化 " + pending.Id);
                        bool consumed = true;
                        if (trigger is not null)
                            consumed = (await SafeRunFlow(task, trigger, "out", st, variables, conditionPrevious, edges, nodes)).ConsumeTrigger;
                        else foreach (var step in task.Steps)
                        {
                            if (!step.Conditions.All(condition => EvalCond(condition, conditionPrevious))) { consumed = false; break; }
                            if (!await SafeRun(task, step, st, variables)) { consumed = false; break; }
                        }
                        if (!consumed) { await Task.Delay(settings.ReconnectMs, stop.Token); continue; }
                    }
                    stop.Token.ThrowIfCancellationRequested();
                    string replyTemplate = changed ? settings.ResponseTemplate : settings.IgnoreResponseTemplate;
                    string? ack = settings.Protocol != "SSE" && !string.IsNullOrWhiteSpace(replyTemplate)
                        ? MesJsonCodec.Render(replyTemplate, JsonContext(null, variables)) : null;
                    inbox.Commit(pending.Sequence, baseline, ack);
                    Mark(st, triggerId, "wait", changed ? "消息已处理，等待下一次字段变化" : "字段未变化，已忽略");
                    if (ack is not null)
                    {
                        try { await client.SendAsync(ack, stop.Token); }
                        catch (Exception ex) when (!stop.IsCancellationRequested)
                        { _log.LogWarning("监听处理已提交，回执发送失败；后台可用同一消息编号重试：{error}", ex.Message); }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    _lastError = st.LastError = error.Message;
                    Mark(st, triggerId, "fail", "监听消息保留等待处理：" + error.Message);
                    await Task.Delay(settings.ReconnectMs, stop.Token);
                }
                finally { _interfaceContext.Value = null; _flowValues.Value = null; }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { stop.Cancel(); try { await receive; } catch (OperationCanceledException) { } }

        async Task Receive()
        {
            int failures = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    string subscription = settings.Protocol == "SSE" ||
                        (settings.Protocol == "SignalR" && string.IsNullOrWhiteSpace(settings.SubscribeMethod)) ||
                        string.IsNullOrWhiteSpace(settings.SubscriptionTemplate) ? "" : MesJsonCodec.Render(
                        settings.SubscriptionTemplate, JsonSerializer.SerializeToElement(new { taskId = task.Id, lastEventId = inbox.LastEventId(scope) }));
                    await client.RunAsync(settings, inbox.LastEventId(scope), subscription, async (message, ct) =>
                    {
                        if (settings.Protocol == "SSE" && settings.EventName.Length > 0 && message.Event != settings.EventName) return;
                        using var document = JsonDocument.Parse(message.Data);
                        string id = string.IsNullOrWhiteSpace(settings.IdField)
                            ? message.Id ?? throw new InvalidDataException("SSE 消息缺少 id，请配置编号字段。")
                            : RequiredString(document.RootElement, settings.IdField);
                        MesInboxMessage saved;
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            try { saved = inbox.Append(scope, id, message.Data, message.Id, _cfg.QueueCapacity); break; }
                            catch (MesInboxFullException error)
                            {
                                _lastError = st.LastError = error.Message;
                                Mark(st, triggerId, "wait", error.Message);
                                await Task.Delay(100, ct);
                            }
                        }
                        failures = 0;
                        if (saved.Completed && saved.Ack is not null && settings.Protocol != "SSE")
                            await client.SendAsync(saved.Ack, ct);
                    }, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    _lastError = st.LastError = error.Message;
                    Mark(st, triggerId, "fail", "监听连接等待重连：" + error.Message);
                }
                int delay = (int)Math.Min(60000L, settings.ReconnectMs * (1L << Math.Min(failures++, 6)));
                try { await Task.Delay(delay, stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static string RequiredString(JsonElement root, string path)
    {
        if (!MesJsonCodec.TrySelect(root, path, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("监听字段必须为非空字符串：" + path);
        return value.GetString()!;
    }
    private static string ChangeValue(JsonElement input, IEnumerable<string> paths) => JsonSerializer.Serialize(paths.Select(path =>
        MesJsonCodec.TrySelect(input, path, out var value) ? value : throw new InvalidDataException("变化字段缺失：" + path)));
    private static bool JsonEquals(string a, string b)
    { using var first = JsonDocument.Parse(a); using var second = JsonDocument.Parse(b); return JsonElement.DeepEquals(first.RootElement, second.RootElement); }

    private JsonElement JsonContext(JsonElement? fields, Dictionary<string, string> variables) => JsonSerializer.SerializeToElement(new
    {
        input = _interfaceContext.Value?.Input,
        mapped = _interfaceContext.Value?.Mapped,
        fields, vars = variables,
    });

    private BuiltBody BuildHttpBody(MesPayload payload, MesHttp http, Dictionary<string, string> vars)
    {
        if (payload.ExtraFields?.Count > 0)
        {
            // Always validate sources against generated fields before a template can omit them.
            bool explicitTemplate = MesParameterRouting.HasExplicitTemplate(payload, http);
            var generated = AddExtraFields(payload, BuildBodyResult(payload, "", vars, !explicitTemplate), vars, !explicitTemplate);
            using var values = JsonDocument.Parse(generated.Body);
            if (!string.IsNullOrWhiteSpace(http.JsonTemplate))
                return generated with { Body = MesJsonCodec.Render(http.JsonTemplate, JsonContext(values.RootElement, vars)) };
            if (!string.IsNullOrWhiteSpace(http.BodyTemplate) && !MesPayloadModes.IsDataRanges(payload))
            {
                var legacy = BuildBodyResult(payload, http.BodyTemplate, vars);
                return legacy with { Body = legacy.Body.Contains("${fields", StringComparison.Ordinal)
                    ? MesJsonCodec.Render(legacy.Body, JsonContext(values.RootElement, vars)) : legacy.Body };
            }
            return generated;
        }
        var built = BuildBodyResult(payload, string.IsNullOrWhiteSpace(http.JsonTemplate) ? http.BodyTemplate : "", vars,
            string.IsNullOrWhiteSpace(http.JsonTemplate));
        if (string.IsNullOrWhiteSpace(http.JsonTemplate)) return built;
        using var fields = JsonDocument.Parse(built.Body);
        return built with { Body = MesJsonCodec.Render(http.JsonTemplate, JsonContext(fields.RootElement, vars)) };
    }
    private static string MapHttpResponse(string response, MesHttp settings)
    {
        if (settings.ResponseMappings.Count == 0) return response;
        using var document = JsonDocument.Parse(response);
        return MesJsonCodec.Map(document.RootElement, settings.ResponseMappings).GetRawText();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace C99.Models
{
    public class LogicAction
    {
        public string ActionType { get; set; } = "log";
        public Dictionary<string, string> Params { get; set; } = new();
    }

    public class LogicPipeline
    {
        public bool Enabled { get; set; }
        public List<LogicAction> Actions { get; set; } = new();
    }

    public class LogicPipelineConfig
    {
        public LogicPipeline? PreAILogic { get; set; }
        public LogicPipeline? PostAILogic { get; set; }
    }

    public class LogicEngine
    {
        public Func<string, string, int, Task>? ShowPopupNotifyAsync { get; set; }
        public Func<string, string, Task<bool>>? ShowPopupConfirmAsync { get; set; }
        public Func<string, Task>? LogAsync { get; set; }

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        public async Task<Dictionary<string, string>> ExecuteAsync(LogicPipeline? pipeline, Dictionary<string, string> context)
        {
            if (pipeline == null || !pipeline.Enabled || pipeline.Actions.Count == 0)
                return context;

            foreach (var action in pipeline.Actions)
            {
                bool shouldContinue = await ExecuteActionAsync(action, context);
                if (!shouldContinue) break;
            }

            return context;
        }

        private async Task<bool> ExecuteActionAsync(LogicAction action, Dictionary<string, string> context)
        {
            var p = action.Params;
            string type = action.ActionType;

            switch (type)
            {
                case "set_variable":
                    SetContext(context, GetParam(p, "name"), Resolve(GetParam(p, "value"), context));
                    break;

                case "replace_text":
                {
                    string target = GetParam(p, "target");
                    string find = Resolve(GetParam(p, "find"), context);
                    string replace = Resolve(GetParam(p, "replace"), context);
                    if (context.TryGetValue(target, out var val))
                        context[target] = val.Replace(find, replace);
                    break;
                }

                case "regex_replace":
                {
                    string target = GetParam(p, "target");
                    string pattern = Resolve(GetParam(p, "pattern"), context);
                    string replacement = Resolve(GetParam(p, "replacement"), context);
                    if (context.TryGetValue(target, out var val))
                    {
                        try { context[target] = Regex.Replace(val, pattern, replacement); }
                        catch (Exception ex) { await Log($"正则替换失败: {ex.Message}"); }
                    }
                    break;
                }

                case "prepend_text":
                {
                    string target = GetParam(p, "target");
                    string text = Resolve(GetParam(p, "text"), context);
                    if (context.TryGetValue(target, out var val))
                        context[target] = text + val;
                    else
                        SetContext(context, target, text);
                    break;
                }

                case "append_text":
                {
                    string target = GetParam(p, "target");
                    string text = Resolve(GetParam(p, "text"), context);
                    if (context.TryGetValue(target, out var val))
                        context[target] = val + text;
                    else
                        SetContext(context, target, text);
                    break;
                }

                case "http_request":
                {
                    string url = Resolve(GetParam(p, "url"), context);
                    string method = GetParam(p, "method", "POST");
                    string body = Resolve(GetParam(p, "body"), context);
                    string resultVar = GetParam(p, "result_var");
                    try
                    {
                        var req = new HttpRequestMessage(method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Get : HttpMethod.Post, url);
                        if (!string.IsNullOrEmpty(body) && !method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        var resp = await _http.SendAsync(req);
                        string respBody = await resp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrEmpty(resultVar))
                            SetContext(context, resultVar, respBody);
                        await Log($"HTTP {method} {url} -> {resp.StatusCode}");
                    }
                    catch (Exception ex) { await Log($"HTTP请求失败: {ex.Message}"); }
                    break;
                }

                case "condition_skip":
                {
                    string variable = GetParam(p, "variable");
                    string op = GetParam(p, "operator", "contains");
                    string value = Resolve(GetParam(p, "value"), context);
                    string actual = context.TryGetValue(variable, out var v) ? v : "";
                    bool match = op switch
                    {
                        "contains" => actual.Contains(value),
                        "not_contains" => !actual.Contains(value),
                        "equals" => actual == value,
                        "not_equals" => actual != value,
                        "not_empty" => !string.IsNullOrEmpty(actual),
                        "is_empty" => string.IsNullOrEmpty(actual),
                        "starts_with" => actual.StartsWith(value),
                        "ends_with" => actual.EndsWith(value),
                        _ => false
                    };
                    if (match) return false;
                    break;
                }

                case "log":
                {
                    string msg = Resolve(GetParam(p, "message"), context);
                    await Log(msg);
                    break;
                }

                case "popup_notify":
                {
                    string title = Resolve(GetParam(p, "title"), context);
                    string msg = Resolve(GetParam(p, "message"), context);
                    int autoDismiss = int.TryParse(GetParam(p, "auto_dismiss"), out var d) ? d : 10;
                    if (ShowPopupNotifyAsync != null)
                        await ShowPopupNotifyAsync(title, msg, autoDismiss);
                    break;
                }

                case "popup_confirm":
                {
                    string title = Resolve(GetParam(p, "title"), context);
                    string msg = Resolve(GetParam(p, "message"), context);
                    if (ShowPopupConfirmAsync != null)
                    {
                        bool confirmed = await ShowPopupConfirmAsync(title, msg);
                        if (!confirmed) return false;
                    }
                    break;
                }
            }

            return true;
        }

        private static string GetParam(Dictionary<string, string> p, string key, string defaultVal = "") =>
            p.TryGetValue(key, out var v) ? v : defaultVal;

        private static void SetContext(Dictionary<string, string> ctx, string key, string value)
        {
            if (!string.IsNullOrEmpty(key)) ctx[key] = value;
        }

        public static string Resolve(string template, Dictionary<string, string> context)
        {
            if (string.IsNullOrEmpty(template) || !template.Contains('{')) return template;
            return Regex.Replace(template, @"\{(\w+)\}", m =>
                context.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        }

        private async Task Log(string msg)
        {
            if (LogAsync != null) await LogAsync(msg);
            System.Diagnostics.Debug.WriteLine($"[LogicEngine] {msg}");
        }
    }
}

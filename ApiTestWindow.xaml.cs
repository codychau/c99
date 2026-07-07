using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace C99
{
    public sealed partial class ApiTestWindow : Window
    {
        private readonly string _baseUrl;

        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

        private static readonly (string Label, string Method, string Path, string? BodyExample)[] Endpoints =
        {
            ("GET  /api/health",       "GET",  "/api/health",    null),
            ("POST /api/report",       "POST", "/api/report",    "{\n  \"important\": [\n    {\n      \"from\": \"张三\",\n      \"subject\": \"项目进度汇报\",\n      \"preview\": \"本周完成了核心模块开发...\",\n      \"time\": \"2024-01-15 09:30\"\n    }\n  ],\n  \"others\": \"其他常规通知邮件3封\",\n  \"emails\": \"\",\n  \"account\": \"demo@example.com\"\n}"),
            ("GET  /api/config",       "GET",  "/api/config",    null),
            ("POST /api/config",       "POST", "/api/config",    "{\n  \"port\": 9527,\n  \"autoStart\": true,\n  \"modelSource\": \"BuiltIn\",\n  \"builtInModel\": \"Local llama.cpp\",\n  \"systemPrompt\": \"你是一个专业的工作报告助手...\"\n}"),
            ("GET  /report/latest",    "GET",  "/report/latest", null),
        };

        public ApiTestWindow(int port)
        {
            InitializeComponent();

            _baseUrl = $"http://localhost:{port}";
            Title = "API 测试工具";
            BaseUrlBlock.Text = _baseUrl;

            foreach (var ep in Endpoints)
                EndpointCombo.Items.Add(ep.Label);
            EndpointCombo.SelectedIndex = 0;

            MethodCombo.Items.Add("GET");
            MethodCombo.Items.Add("POST");
            MethodCombo.SelectedIndex = 0;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(700, 600));

            UpdateBodyState();
        }

        private void OnEndpointChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EndpointCombo.SelectedIndex < 0 || EndpointCombo.SelectedIndex >= Endpoints.Length) return;
            var ep = Endpoints[EndpointCombo.SelectedIndex];
            MethodCombo.SelectedIndex = ep.Method == "POST" ? 1 : 0;
            BodyTextBox.Text = ep.BodyExample ?? "";
            UpdateBodyState();
        }

        private void OnMethodChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateBodyState();
        }

        private void UpdateBodyState()
        {
            bool isPost = MethodCombo.SelectedIndex == 1;
            BodyTextBox.IsEnabled = isPost;
            BodyTextBox.Opacity = isPost ? 1.0 : 0.4;
        }

        private async void OnSendClick(object sender, RoutedEventArgs e)
        {
            var path = GetCurrentPath();
            bool isPost = MethodCombo.SelectedIndex == 1;
            var fullUrl = $"{_baseUrl}{path}";

            SendBtn.IsEnabled = false;
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;
            StatusText.Text = "";
            ResponseTextBox.Text = "";

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var request = new HttpRequestMessage(
                    isPost ? HttpMethod.Post : HttpMethod.Get, fullUrl);

                if (isPost && !string.IsNullOrWhiteSpace(BodyTextBox.Text))
                {
                    request.Content = new StringContent(BodyTextBox.Text, Encoding.UTF8, "application/json");
                }

                using var response = await _httpClient.SendAsync(request);
                sw.Stop();
                var responseBody = await response.Content.ReadAsStringAsync();

                StatusText.Text = $"{(int)response.StatusCode} {response.StatusCode} ({sw.Elapsed.TotalSeconds:F2}s)";
                StatusText.Foreground = response.IsSuccessStatusCode
                    ? new SolidColorBrush(Microsoft.UI.Colors.Green)
                    : new SolidColorBrush(Microsoft.UI.Colors.Red);

                if (string.IsNullOrEmpty(responseBody))
                {
                    ResponseTextBox.Text = "(空响应)";
                }
                else
                {
                    try
                    {
                        var obj = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(responseBody);
                        ResponseTextBox.Text = System.Text.Json.JsonSerializer.Serialize(obj,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    }
                    catch
                    {
                        ResponseTextBox.Text = responseBody;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                StatusText.Text = "连接失败";
                StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                ResponseTextBox.Text = $"无法连接到 {fullUrl}\n\n{ex.Message}";
            }
            catch (TaskCanceledException)
            {
                StatusText.Text = "请求超时";
                StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                ResponseTextBox.Text = $"请求超时: {fullUrl}";
            }
            catch (Exception ex)
            {
                StatusText.Text = "错误";
                StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                ResponseTextBox.Text = $"发送请求时出错:\n{ex.Message}";
            }
            finally
            {
                SendBtn.IsEnabled = true;
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        private string GetCurrentPath()
        {
            if (EndpointCombo.SelectedIndex >= 0 && EndpointCombo.SelectedIndex < Endpoints.Length)
            {
                if (EndpointCombo.Text == Endpoints[EndpointCombo.SelectedIndex].Label)
                    return Endpoints[EndpointCombo.SelectedIndex].Path;
            }

            var text = EndpointCombo.Text?.Trim() ?? "";
            if (text.StartsWith("/"))
                return text;

            var spaceIdx = text.IndexOf(' ');
            if (spaceIdx > 0)
            {
                var path = text.Substring(spaceIdx + 1).Trim();
                if (path.StartsWith("/"))
                    return path;
            }

            var methodIdx = text.IndexOf("GET", StringComparison.OrdinalIgnoreCase);
            if (methodIdx >= 0 && text.IndexOf('/', methodIdx) is int idx && idx > 0)
                return text.Substring(idx);

            return "/api/health";
        }
    }
}

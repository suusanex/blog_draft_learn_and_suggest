using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using blog_draft_learn_and_suggest.Services;

namespace blog_draft_learn_and_suggest.Models
{
    public class ChatModel : IAsyncDisposable
    {
        private bool _disposed;
        private bool _isModelLoaded;
        private string? _activeDeploymentName;
        private string? _activeDisplayName;

        private readonly IConfiguration _configuration;
        private readonly HttpClient _httpClient;
        private readonly int _maxTokens;
        private readonly List<ChatMessage> _history = new();
        private readonly RetrievalService _retrieval;
        private readonly ILogger<ChatModel> _logger;

        public event Action<string>? ProgressChanged;
        public event Action<string>? ResultChanged;

        public string? ActiveModelDisplayName => _activeDisplayName;
        public bool IsModelLoaded => _isModelLoaded;

        private enum ProviderKind { OpenAI, FoundryLocal }
        private ProviderKind Provider =>
            string.Equals(_configuration["LLM:Provider"], "FoundryLocal", StringComparison.OrdinalIgnoreCase)
                ? ProviderKind.FoundryLocal
                : ProviderKind.OpenAI;

        public class ModelInfoItem
        {
            public string Id { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public bool IsCached { get; set; }
        }

        public ChatModel(IConfiguration configuration, RetrievalService retrieval, ILogger<ChatModel> logger)
        {
            _configuration = configuration;
            _retrieval = retrieval;
            _logger = logger;

            _httpClient = new HttpClient();
            var timeoutSeconds = _configuration.GetValue<int>("AzureOpenAI:RequestTimeoutSeconds", 0);
            if (timeoutSeconds > 0)
            {
                _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            }

            _maxTokens = _configuration.GetValue<int>("AzureOpenAI:MaxTokens", 2048);

            _logger.LogInformation("ChatModel created. TimeoutSeconds={TimeoutSeconds}, MaxTokens={MaxTokens}, Provider={Provider}", timeoutSeconds, _maxTokens, Provider);
        }

        public Task<List<ModelInfoItem>> GetAvailableModelsAsync()
        {
            var deployment = _configuration["AzureOpenAI:DeploymentName"] ?? string.Empty;
            var display = string.IsNullOrEmpty(deployment) ? "(未設定)" : deployment;
            var list = new List<ModelInfoItem>
            {
                new ModelInfoItem { Id = deployment, DisplayName = display, IsCached = true }
            };
            _logger.LogInformation("Available models loaded. Deployment='{Deployment}', Provider={Provider}", deployment, Provider);
            return Task.FromResult(list);
        }

        public Task LoadOrDownloadModelAsync(string modelId)
        {
            _activeDeploymentName = modelId;
            _activeDisplayName = modelId;
            // Foundry Local でも OpenAI でも、選択が有効になった時点で送信可能とする（FoundryはUI側で待機制御）。
            _isModelLoaded = !string.IsNullOrWhiteSpace(modelId);

            _history.Clear();
            var styleTitle = _configuration["StyleCard:Title"];
            var styleContent = _configuration["StyleCard:Content"];
            var guide = BuildSystemGuide(styleTitle, styleContent);
            if (!string.IsNullOrWhiteSpace(guide))
            {
                _history.Add(new ChatMessage { role = "system", content = guide });
            }
            _logger.LogInformation("Model loaded. ActiveDeployment='{Deployment}', HistorySystemMsgLen={Len}", _activeDeploymentName, guide?.Length ?? 0);
            var prov = Provider;
            var provText = prov == ProviderKind.FoundryLocal ? "Foundry Local" : "Azure OpenAI";
            ProgressChanged?.Invoke($"{provText} deployment ready: {modelId}");
            return Task.CompletedTask;
        }

        public Task RestartFoundryServiceAsync() => Task.CompletedTask;

        public async Task SendAsync(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                ResultChanged?.Invoke("Input is empty.\n");
                return;
            }
            if (!_isModelLoaded || string.IsNullOrWhiteSpace(_activeDeploymentName))
            {
                ResultChanged?.Invoke("No active deployment selected.\n");
                return;
            }

            try
            {
                // Azure AI Searchで類似文書を取得（VectorizableTextQuery使用）
                var ragEnabled = _configuration.GetValue<bool>("AzureAISearch:Enabled", true);
                var contextText = string.Empty;
                if (ragEnabled)
                {
                    try
                    {
                        var results = await _retrieval.SearchAsync(input);
                        if (results.Count > 0)
                        {
                            var textField = _configuration["AzureAISearch:TextFieldName"] ?? "text";
                            var sb = new StringBuilder();
                            sb.AppendLine("## 過去の記事 (RAG)");
                            foreach (var r in results)
                            {
                                if (r.Document.TryGetValue(textField, out var val) && val != null)
                                {
                                    sb.AppendLine("- " + val.ToString());
                                }
                            }
                            contextText = sb.ToString();
                        }
                    }
                    catch (Exception ragEx)
                    {
                        _logger.LogWarning(ragEx, "Azure AI Search retrieval failed.");
                    }
                }

                var composedInput = string.IsNullOrWhiteSpace(contextText)
                    ? input
                    : $"類似する過去の記事と、新規の記事の概要です。類似する過去の記事も参考にして、新規の記事の下書きを生成してください。\n\n{contextText}\n\n---\n\n新規の記事の概要:\n{input}";

                _history.Add(new ChatMessage { role = "user", content = composedInput });

                var endpoint = _configuration["AzureOpenAI:Endpoint"] ?? string.Empty;
                var apiKey = _configuration["AzureOpenAI:ApiKey"] ?? string.Empty;
                var apiVersion = _configuration["AzureOpenAI:ApiVersion"] ?? "2024-06-01";
                if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
                {
                    ResultChanged?.Invoke("AzureOpenAI endpoint/apiKey not configured.\n");
                    _logger.LogError("AzureOpenAI not configured. Endpoint='{Endpoint}', ApiKeySet={ApiKeySet}", endpoint, !string.IsNullOrEmpty(apiKey));
                    return;
                }

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                _httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

                var requestPath = $"/openai/deployments/{_activeDeploymentName}/chat/completions?api-version={apiVersion}";
                var url = new Uri(new Uri(endpoint.TrimEnd('/')), requestPath);

                string payloadJson = BuildChatRequestJson(_history, _maxTokens, _activeDeploymentName);
                _logger.LogDebug("POST {Url}\nPayload={Payload}", url, payloadJson);

                ResultChanged?.Invoke("[Start]\n");

                using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                using var resp = await _httpClient.PostAsync(url, content);
                var respText = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    ResultChanged?.Invoke($"Error: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{respText}\n");
                    _logger.LogError("AOAI error {Status}: {Reason}. Body={Body}", (int)resp.StatusCode, resp.ReasonPhrase, respText);
                    ResultChanged?.Invoke("\n[End]\n");
                    return;
                }

                try
                {
                    var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(respText);
                    var message = completion?.choices?.FirstOrDefault()?.message?.content;
                    if (!string.IsNullOrEmpty(message))
                    {
                        ResultChanged?.Invoke(message);
                        _history.Add(new ChatMessage { role = "assistant", content = message });
                    }
                    else
                    {
                        ResultChanged?.Invoke("(no content)\n");
                        _logger.LogWarning("Completion parsed but no content. RawBody={Body}", respText);
                    }
                }
                catch (JsonException jex)
                {
                    ResultChanged?.Invoke("Failed to parse response JSON. See logs.\n");
                    _logger.LogError(jex, "JSON parse failed. RawBody={Body}", respText);
                }

                ResultChanged?.Invoke("\n[End]\n");
            }
            catch (Exception ex)
            {
                ResultChanged?.Invoke($"Error generating response: {ex.Message}\n");
                _logger.LogError(ex, "SendAsync failed.");
            }
        }

        private static string BuildSystemGuide(string? title, string? styleMarkdown)
        {
            var parts = new List<string>();
            parts.Add("あなたは、特定の記事群を作成したテックブロガーです。文体カード及び過去の記事いくつかをコンテキストに含めるので、それらの特徴と同じように、新たな記事の下書きを作成してください。出力は常にMarkdownで行い、図が必要ならMermaid.jsで示してください。");
            if (!string.IsNullOrWhiteSpace(title)) parts.Add($"# 文体カード: {title}");
            if (!string.IsNullOrWhiteSpace(styleMarkdown)) parts.Add(styleMarkdown);
            return string.Join("\n\n", parts);
        }

        private static string BuildChatRequestJson(List<ChatMessage> messages, int maxTokens, string? deploymentName)
        {
            var useMaxCompletion = NeedsMaxCompletionTokens(deploymentName);
            var req = new ChatCompletionRequest
            {
                messages = messages,
                max_tokens = useMaxCompletion ? null : maxTokens,
                max_completion_tokens = useMaxCompletion ? maxTokens : null,
            };
            return JsonSerializer.Serialize(req, JsonOptions);
        }

        private static bool NeedsMaxCompletionTokens(string? deploymentName)
        {
            if (string.IsNullOrWhiteSpace(deploymentName)) return false;
            var n = deploymentName.ToLowerInvariant();
            return n.StartsWith("gpt-5");
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _httpClient.Dispose();
                _disposed = true;
            }
            return ValueTask.CompletedTask;
        }

        // DTOs
        private class ChatMessage
        {
            public string role { get; set; } = string.Empty;
            public string content { get; set; } = string.Empty;
        }

        private class ChatCompletionRequest
        {
            public List<ChatMessage> messages { get; set; } = new();
            public int? max_tokens { get; set; }
            [JsonPropertyName("max_completion_tokens")] public int? max_completion_tokens { get; set; }
        }

        private class ChatCompletionResponse
        {
            public List<Choice>? choices { get; set; }
        }

        private class Choice
        {
            public ChatMessage? message { get; set; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using blog_draft_learn_and_suggest.Services;

namespace blog_draft_learn_and_suggest.Models
{
    public class ChatModel : IAsyncDisposable
    {
        private bool _disposed;
        private bool _isModelLoaded;
        private string? _activeModelName;
        private string? _activeDisplayName;

        private readonly IConfiguration _configuration;
        private readonly int _maxTokens;
        private readonly List<ChatMessage> _history = new();
        private readonly RetrievalService _retrieval;
        private readonly ILogger<ChatModel> _logger;
        private OpenAIClient? _openAIClient;
        private ChatClient? _chatClient;

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

            var timeoutSeconds = _configuration.GetValue<int>("OpenAI:RequestTimeoutSeconds", 0);
            _maxTokens = _configuration.GetValue<int>("OpenAI:MaxTokens", 2048);

            _logger.LogInformation("ChatModel created. TimeoutSeconds={TimeoutSeconds}, MaxTokens={MaxTokens}, Provider={Provider}", timeoutSeconds, _maxTokens, Provider);
        }

        public Task<List<ModelInfoItem>> GetAvailableModelsAsync()
        {
            var model = _configuration["OpenAI:Model"] ?? "gpt-4o-mini";
            var display = string.IsNullOrEmpty(model) ? "(未設定)" : model;
            var list = new List<ModelInfoItem>
            {
                new ModelInfoItem { Id = model, DisplayName = display, IsCached = true }
            };
            _logger.LogInformation("Available models loaded. Model='{Model}', Provider={Provider}", model, Provider);
            return Task.FromResult(list);
        }

        public Task LoadOrDownloadModelAsync(string modelId)
        {
            _activeModelName = modelId;
            _activeDisplayName = modelId;

            // OpenAI クライアントを初期化
            var apiKey = _configuration["OpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogError("OpenAI ApiKey not configured.");
                _isModelLoaded = false;
                return Task.CompletedTask;
            }

            var options = new OpenAIClientOptions();
            var timeoutSeconds = _configuration.GetValue<int>("OpenAI:RequestTimeoutSeconds", 0);
            if (timeoutSeconds > 0)
            {
                options.NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            }

            _openAIClient = new OpenAIClient(new ApiKeyCredential(apiKey), options);
            _chatClient = _openAIClient.GetChatClient(modelId);

            _isModelLoaded = !string.IsNullOrWhiteSpace(modelId);

            _history.Clear();
            var styleTitle = _configuration["StyleCard:Title"];
            var styleContent = _configuration["StyleCard:Content"];
            var guide = BuildSystemGuide(styleTitle, styleContent);
            if (!string.IsNullOrWhiteSpace(guide))
            {
                _history.Add(ChatMessage.CreateSystemMessage(guide));
            }
            _logger.LogInformation("Model loaded. ActiveModel='{Model}', HistorySystemMsgLen={Len}", _activeModelName, guide?.Length ?? 0);
            var prov = Provider;
            var provText = prov == ProviderKind.FoundryLocal ? "Foundry Local" : "OpenAI";
            ProgressChanged?.Invoke($"{provText} model ready: {modelId}");
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
            if (!_isModelLoaded || string.IsNullOrWhiteSpace(_activeModelName) || _chatClient == null)
            {
                ResultChanged?.Invoke("No active model selected.\n");
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

                _history.Add(ChatMessage.CreateUserMessage(composedInput));

                ResultChanged?.Invoke("[Start]\n");

                var options = new ChatCompletionOptions
                {
                    MaxOutputTokenCount = _maxTokens
                };

                _logger.LogDebug("Sending chat completion request with {MessageCount} messages", _history.Count);

                var completion = await _chatClient.CompleteChatAsync(_history, options);
                var responseMessage = completion.Value.Content.FirstOrDefault()?.Text;

                if (!string.IsNullOrEmpty(responseMessage))
                {
                    ResultChanged?.Invoke(responseMessage);
                    _history.Add(ChatMessage.CreateAssistantMessage(responseMessage));
                }
                else
                {
                    ResultChanged?.Invoke("(no content)\n");
                    _logger.LogWarning("Completion response has no content.");
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

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                // OpenAIClient doesn't implement IDisposable, so no disposal needed
                _disposed = true;
            }
            return ValueTask.CompletedTask;
        }
    }
}

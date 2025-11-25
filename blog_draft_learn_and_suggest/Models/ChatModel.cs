using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using blog_draft_learn_and_suggest.Services;

namespace blog_draft_learn_and_suggest.Models;

/// <summary>
/// Microsoft.Extensions.AI の IChatClient を使用したチャットモデル。
/// ベストプラクティスに従い、以下の機能を提供します:
/// - IChatClient 抽象化によるプロバイダー非依存の実装
/// - ChatClientBuilder パイプラインによるロギング統合
/// - ストリーミングレスポンス対応（List&lt;ChatResponseUpdate&gt;.AddMessages 拡張メソッド使用）
/// - CancellationToken による適切なキャンセル処理
/// - 会話履歴の管理
/// - RAG（Retrieval-Augmented Generation）統合
/// - 強く型付けされた ChatOptions プロパティ（Temperature, MaxOutputTokens など）の活用
/// </summary>
/// <remarks>
/// <para>
/// このクラスは sealed であり、IAsyncDisposable を実装します。
/// IChatClient インターフェースはスレッドセーフであり、複数のリクエストで同時に使用できますが、
/// ChatOptions インスタンスは mutate される可能性があるため、各リクエストで新しいインスタンスを作成します。
/// </para>
/// <para>
/// ベストプラクティス: ChatClientBuilder を使用してミドルウェアパイプラインを構築し、
/// UseLogging() でリクエスト/レスポンスのロギングを有効化します。
/// </para>
/// <para>
/// 重要: 公開メソッドでは ObjectDisposedException チェックを行い、
/// 破棄済みインスタンスへのアクセスを防止します。
/// </para>
/// </remarks>
public sealed class ChatModel : IAsyncDisposable
{
    private bool _disposed;
    private bool _isModelLoaded;
    private string? _activeModelName;
    private string? _activeDisplayName;

    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly int _maxTokens;
    private readonly List<ChatMessage> _history = [];
    private readonly RetrievalService _retrieval;
    private readonly ILogger<ChatModel> _logger;

    /// <summary>
    /// Microsoft.Extensions.AI の IChatClient インターフェース。
    /// プロバイダーに依存しない統一的なチャット API を提供します。
    /// ChatClientBuilder パイプラインでラップされ、ロギング機能が統合されています。
    /// </summary>
    private IChatClient? _chatClient;

    /// <summary>
    /// 基盤となる OpenAI クライアント。
    /// リソース解放のために保持します（ロード時に設定されるため readonly ではありません）。
    /// </summary>
#pragma warning disable IDE0044 // Add readonly modifier - フィールドは LoadOrDownloadModelAsync で設定されます
    private OpenAIClient? _openAIClient;
#pragma warning restore IDE0044

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

    public ChatModel(IConfiguration configuration, RetrievalService retrieval, ILogger<ChatModel> logger, ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        _retrieval = retrieval;
        _logger = logger;
        _loggerFactory = loggerFactory;

        var timeoutSeconds = _configuration.GetValue<int>("OpenAI:RequestTimeoutSeconds", 0);
        _maxTokens = _configuration.GetValue<int>("OpenAI:MaxTokens", 2048);

        _logger.LogInformation(
            "ChatModel created with Microsoft.Extensions.AI. TimeoutSeconds={TimeoutSeconds}, MaxTokens={MaxTokens}, Provider={Provider}",
            timeoutSeconds, _maxTokens, Provider);
    }

    /// <summary>
    /// 利用可能なモデル一覧を取得します。
    /// </summary>
    /// <exception cref="ObjectDisposedException">インスタンスが破棄済みの場合。</exception>
    public Task<List<ModelInfoItem>> GetAvailableModelsAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var model = _configuration["OpenAI:Model"] ?? "gpt-4o-mini";
        var display = string.IsNullOrEmpty(model) ? "(未設定)" : model;
        var list = new List<ModelInfoItem>
        {
            new() { Id = model, DisplayName = display, IsCached = true }
        };
        _logger.LogInformation("Available models loaded. Model='{Model}', Provider={Provider}", model, Provider);
        return Task.FromResult(list);
    }

    /// <summary>
    /// モデルをロードし、IChatClient を初期化します。
    /// Microsoft.Extensions.AI の AsIChatClient() 拡張メソッドを使用して、
    /// OpenAI SDK の ChatClient を IChatClient に変換します。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ベストプラクティス: ChatClientBuilder パイプラインを使用して、
    /// UseLogging() でリクエスト/レスポンスのロギングを統合します。
    /// ロギングレベルが Trace の場合、メッセージの内容がログに記録されます。
    /// </para>
    /// <para>
    /// 注意: Trace レベルのロギングは機密データを含む可能性があるため、
    /// 本番環境では有効にしないでください。
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">インスタンスが破棄済みの場合。</exception>
    public Task LoadOrDownloadModelAsync(string modelId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // ベストプラクティス: 古いクライアントを適切に破棄してからの再初期化
        // IChatClient は IDisposable を実装しているため、新しいクライアントを作成する前に
        // 既存のクライアントを Dispose する必要があります
        if (_chatClient != null)
        {
            _chatClient.Dispose();
            _chatClient = null;
            _logger.LogDebug("Disposed previous IChatClient before reinitializing.");
        }

        _activeModelName = modelId;
        _activeDisplayName = modelId;

        // OpenAI クライアントを初期化し、IChatClient に変換
        var apiKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("OpenAI ApiKey not configured.");
            _isModelLoaded = false;
            return Task.CompletedTask;
        }

        try
        {
            var options = new OpenAIClientOptions();
            var timeoutSeconds = _configuration.GetValue<int>("OpenAI:RequestTimeoutSeconds", 0);
            if (timeoutSeconds > 0)
            {
                options.NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            }

            var baseUrl = _configuration["OpenAI:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                options.Endpoint = new Uri(baseUrl);
            }

            // OpenAI クライアントを作成し、Microsoft.Extensions.AI の IChatClient に変換
            // AsIChatClient() は OpenAI SDK と Microsoft.Extensions.AI.OpenAI パッケージが提供する拡張メソッド
            var credential = new ApiKeyCredential(apiKey);
            _openAIClient = new OpenAIClient(credential, options);

            // ベストプラクティス: ChatClientBuilder パイプラインを使用
            // UseLogging() でリクエスト/レスポンスのロギングを有効化
            // これにより、デバッグやトラブルシューティングが容易になります
            // 他のオプション: UseOpenTelemetry() で OpenTelemetry トレース/メトリクスを追加可能
            // 例: .UseOpenTelemetry(loggerFactory, sourceName: "MyApp")
            var innerClient = _openAIClient.GetChatClient(modelId).AsIChatClient();
            _chatClient = new ChatClientBuilder(innerClient)
                .UseLogging(_loggerFactory)
                .Build();

            _isModelLoaded = !string.IsNullOrWhiteSpace(modelId);

            // 会話履歴を初期化
            InitializeConversationHistory();

            _logger.LogInformation(
                "IChatClient initialized with logging pipeline. ActiveModel='{Model}', Provider={Provider}",
                _activeModelName, Provider);

            var provText = Provider == ProviderKind.FoundryLocal ? "Foundry Local" : "OpenAI";
            ProgressChanged?.Invoke($"{provText} model ready: {modelId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize IChatClient for model '{Model}'", modelId);
            _isModelLoaded = false;
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 会話履歴を初期化し、システムプロンプトを設定します。
    /// </summary>
    private void InitializeConversationHistory()
    {
        _history.Clear();

        var styleTitle = _configuration["StyleCard:Title"];
        var styleContent = _configuration["StyleCard:Content"];
        var systemPrompt = _configuration["StyleCard:SystemPrompt"];
        var guide = BuildSystemGuide(systemPrompt, styleTitle, styleContent);

        if (!string.IsNullOrWhiteSpace(guide))
        {
            // Microsoft.Extensions.AI の ChatMessage を使用
            // ChatRole.System でシステムメッセージを作成
            _history.Add(new ChatMessage(ChatRole.System, guide));
        }

        _logger.LogDebug("Conversation history initialized. SystemMsgLength={Length}", guide?.Length ?? 0);
    }

    /// <summary>
    /// Foundry サービスを再起動します。
    /// </summary>
    /// <exception cref="ObjectDisposedException">インスタンスが破棄済みの場合。</exception>
    public Task RestartFoundryServiceAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.CompletedTask;
    }

    /// <summary>
    /// メッセージを送信し、ストリーミングでレスポンスを受信します。
    /// Microsoft.Extensions.AI の IChatClient.GetStreamingResponseAsync を使用することで、
    /// 応答を逐次的に受け取り、ユーザーに即座にフィードバックを提供できます。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ベストプラクティス: GetStreamingResponseAsync からの更新を収集し、
    /// ChatResponseExtensions.AddMessages ヘルパーを使用して履歴に追加します。
    /// これにより、テキスト以外のコンテンツ（ツール呼び出しなど）も適切に処理されます。
    /// </para>
    /// <para>
    /// CancellationToken を適切に渡すことで、ユーザーがキャンセルした場合に
    /// リクエストを中断できます。
    /// </para>
    /// </remarks>
    /// <param name="input">ユーザーからの入力メッセージ。</param>
    /// <param name="cancellationToken">操作をキャンセルするためのトークン。</param>
    public async Task SendAsync(string input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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
            // Azure AI Search で類似文書を取得（RAG）
            var composedInput = await PrepareInputWithRagAsync(input, cancellationToken).ConfigureAwait(false);

            // ユーザーメッセージを履歴に追加
            // Microsoft.Extensions.AI の ChatMessage と ChatRole を使用
            // ベストプラクティス: キャンセル時に履歴の整合性を保つため、
            // ユーザーメッセージはキャンセル時に削除します
            var userMessage = new ChatMessage(ChatRole.User, composedInput);
            _history.Add(userMessage);

            ResultChanged?.Invoke("[Start]\n");

            // ChatOptions で追加パラメータを設定
            var chatOptions = CreateChatOptions();

            _logger.LogDebug(
                "Sending streaming chat request with {MessageCount} messages",
                _history.Count);

            // Microsoft.Extensions.AI のストリーミング API を使用
            // GetStreamingResponseAsync は IAsyncEnumerable<ChatResponseUpdate> を返します
            // ベストプラクティス: 公式ドキュメントに従い、更新を List に収集し、
            // ChatResponseUpdateExtensions.AddMessages ヘルパーで履歴に追加します。
            // これにより、テキスト以外のコンテンツ（ツール呼び出しなど）も適切に処理されます。
            // 参考: https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in _chatClient.GetStreamingResponseAsync(_history, chatOptions, cancellationToken).ConfigureAwait(false))
            {
                // 各更新チャンクのテキストを処理
                if (!string.IsNullOrEmpty(update.Text))
                {
                    ResultChanged?.Invoke(update.Text);
                }
                updates.Add(update);
            }

            // ベストプラクティス: List<ChatResponseUpdate>.AddMessages 拡張メソッドを使用
            // これにより、ストリーミングレスポンスから適切に ChatMessage が構築され、
            // 履歴に追加されます。テキスト以外のコンテンツ（関数呼び出しなど）も
            // 正しく処理されます。
            if (updates.Count > 0)
            {
                _history.AddMessages(updates);
                _logger.LogDebug("Response received and added to history. UpdateCount={UpdateCount}", updates.Count);
            }
            else
            {
                ResultChanged?.Invoke("(no content)\n");
                _logger.LogWarning("Streaming response has no content.");
            }

            ResultChanged?.Invoke("\n[End]\n");
        }
        catch (OperationCanceledException)
        {
            // ベストプラクティス: キャンセル時は履歴の整合性を保つため、
            // 応答が完了していないユーザーメッセージを削除します。
            // これにより、次回のリクエストで不完全な会話状態が送信されることを防ぎます。
            if (_history.Count > 0 && _history[^1].Role == ChatRole.User)
            {
                _history.RemoveAt(_history.Count - 1);
                _logger.LogDebug("Removed incomplete user message from history due to cancellation.");
            }
            _logger.LogInformation("SendAsync was cancelled.");
            ResultChanged?.Invoke("\n[Cancelled]\n");
        }
        catch (Exception ex)
        {
            // ベストプラクティス: エラー時も履歴の整合性を保つため、
            // 応答が完了していないユーザーメッセージを削除します。
            if (_history.Count > 0 && _history[^1].Role == ChatRole.User)
            {
                _history.RemoveAt(_history.Count - 1);
                _logger.LogDebug("Removed incomplete user message from history due to error.");
            }
            HandleSendError(ex);
        }
    }

    /// <summary>
    /// RAG（Retrieval-Augmented Generation）のための入力準備。
    /// Azure AI Search から関連文書を取得し、プロンプトに追加します。
    /// </summary>
    /// <param name="input">ユーザーからの入力。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>RAG コンテキストを含む入力文字列。</returns>
    private async Task<string> PrepareInputWithRagAsync(string input, CancellationToken cancellationToken = default)
    {
        var ragEnabled = _configuration.GetValue<bool>("AzureAISearch:Enabled", true);
        if (!ragEnabled)
        {
            return input;
        }

        try
        {
            // CancellationToken を渡せない場合は、手前でキャンセル状態をチェック
            cancellationToken.ThrowIfCancellationRequested();

            var results = await _retrieval.SearchAsync(input).ConfigureAwait(false);
            if (results.Count == 0)
            {
                return input;
            }

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

            var contextText = sb.ToString();
            return $"類似する過去の記事と、新規の記事の概要です。類似する過去の記事も参考にして、新規の記事の下書きを生成してください。\n\n{contextText}\n\n---\n\n新規の記事の概要:\n{input}";
        }
        catch (OperationCanceledException)
        {
            throw; // キャンセルは再スロー
        }
        catch (Exception ragEx)
        {
            _logger.LogWarning(ragEx, "Azure AI Search retrieval failed.");
            return input;
        }
    }

    /// <summary>
    /// ChatOptions を作成します。
    /// Microsoft.Extensions.AI の ChatOptions を使用して、モデルへのリクエストパラメータを設定します。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ベストプラクティス: ChatOptions インスタンスは各リクエストで新規作成します。
    /// IChatClient の実装が ChatOptions を mutate する可能性があるため、
    /// 共有インスタンスの使用は concurrent な呼び出しで問題を引き起こす可能性があります。
    /// </para>
    /// <para>
    /// AdditionalProperties は必要な場合のみ初期化します。
    /// これにより、SDKがまだ対応していないパラメータも送信可能です。
    /// </para>
    /// </remarks>
    private ChatOptions CreateChatOptions()
    {
        // ベストプラクティス: 各リクエストで新しい ChatOptions インスタンスを作成
        // IChatClient 実装が options を mutate する可能性があるため
        var options = new ChatOptions
        {
            // MaxOutputTokens で最大トークン数を設定
            MaxOutputTokens = _maxTokens,
            // ModelId を明示的に設定（オプション）
            ModelId = _activeModelName,
            // ベストプラクティス: ChatOptions の強く型付けされたプロパティを使用
            // これらは AI モデルやサービス間で最も一般的なパラメータです
            // 参考: https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai
            Temperature = _configuration.GetValue<float?>("OpenAI:Temperature", null),
            TopP = _configuration.GetValue<float?>("OpenAI:TopP", null),
            FrequencyPenalty = _configuration.GetValue<float?>("OpenAI:FrequencyPenalty", null),
            PresencePenalty = _configuration.GetValue<float?>("OpenAI:PresencePenalty", null)
        };

        // 追加パラメータがあれば AdditionalProperties に設定
        // ベストプラクティス: AdditionalProperties は ChatOptions 初期化時に null なので、
        // 必要な場合のみ初期化する
        var parameters = _configuration.GetSection("OpenAI:Parameters").Get<List<ChatParameter>>();
        if (parameters is { Count: > 0 })
        {
            options.AdditionalProperties = new AdditionalPropertiesDictionary();
            foreach (var p in parameters)
            {
                if (!string.IsNullOrWhiteSpace(p.Key))
                {
                    options.AdditionalProperties[p.Key] = p.Value;
                }
            }
        }

        return options;
    }

    /// <summary>
    /// 送信エラーを処理します。
    /// </summary>
    private void HandleSendError(Exception ex)
    {
        var detailBuilder = new StringBuilder();
        detailBuilder.AppendLine("Chat completion request failed.");
        detailBuilder.AppendLine($"Type: {ex.GetType().Name}");
        detailBuilder.AppendLine($"Message: {ex.Message}");

        // InnerException があれば詳細を追加
        if (ex.InnerException != null)
        {
            detailBuilder.AppendLine($"Inner: {ex.InnerException.Message}");
        }

        var detail = detailBuilder.ToString();
        ResultChanged?.Invoke(detail + "\n");
        _logger.LogError(ex, "SendAsync failed.");
    }

    /// <summary>
    /// システムガイドを構築します。
    /// </summary>
    private static string BuildSystemGuide(string? systemPrompt, string? title, string? styleMarkdown)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) parts.Add(systemPrompt);
        if (!string.IsNullOrWhiteSpace(title)) parts.Add($"# 文体カード: {title}");
        if (!string.IsNullOrWhiteSpace(styleMarkdown)) parts.Add(styleMarkdown);
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// リソースを非同期で解放します。
    /// IChatClient は IDisposable を実装しているため、適切に Dispose します。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ベストプラクティス: sealed クラスでの IAsyncDisposable 実装。
    /// IChatClient.Dispose() は同期メソッドですが、将来の拡張性のために
    /// IAsyncDisposable パターンを使用しています。
    /// </para>
    /// <para>
    /// 重要: IChatClient ドキュメントによると、インスタンスがまだ使用中の間は
    /// dispose してはいけません。複数のリクエストが同時に処理されている場合は
    /// すべて完了してから dispose する必要があります。
    /// </para>
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;

            // IChatClient は IDisposable を実装
            // ChatClientBuilder パイプラインでラップされたクライアントを Dispose
            // これにより、パイプライン内のすべてのクライアントが適切に解放されます
            _chatClient?.Dispose();
            _chatClient = null;

            // OpenAIClient 自体は IDisposable ではないため Dispose 不要
            _openAIClient = null;

            _logger.LogDebug("ChatModel disposed.");
        }

        // GC.SuppressFinalize を呼び出す（sealed クラスの IAsyncDisposable パターン）
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 設定パラメータを表すクラス。
    /// </summary>
    public class ChatParameter
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using blog_draft_learn_and_suggest.Models;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace blog_draft_learn_and_suggest.ViewModels;

public partial class MainViewModel : ObservableRecipient
{
    private readonly ChatModel _model;
    private readonly IConfiguration _configuration;
    public ChatModel Model => _model;

    // モデル情報クラス
    public class ModelItem
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsCached { get; set; }
        public override string ToString() => DisplayName;
    }

    [ObservableProperty] private string? _result;
    [ObservableProperty] private string? _input;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private bool _isEnableSend;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoadSelectedModelCommand))] private bool _isEnableLoadModel;
    [ObservableProperty] private string? _progressStr;

    // UI 表示メッセージ（モードによって切替）
    [ObservableProperty] private string? _instructionText;
    [ObservableProperty] private string? _sendInstructionText;

    // モード切替（Foundry Local or OpenAI）
    [ObservableProperty] private bool _isFoundryLocal;

    // モデル一覧と選択
    [ObservableProperty] private ObservableCollection<ModelItem> _modelList = new();
    [ObservableProperty] private ModelItem? _selectedModel;

    public MainViewModel(IConfiguration configuration, ChatModel model)
    {
        _configuration = configuration;
        _model = model;
        _model.ProgressChanged += msg => ProgressStr = msg;
        _model.ResultChanged += msg => Result += msg;
    }

    [RelayCommand]
    private async Task OnLoadedAsync()
    {
        try
        {
            Result = string.Empty;
            ProgressStr = string.Empty;

            // プロバイダー判定
            var provider = _configuration["LLM:Provider"] ?? "OpenAI";
            IsFoundryLocal = string.Equals(provider, "FoundryLocal", StringComparison.OrdinalIgnoreCase);

            // モデル一覧取得（ロードはしない）
            await LoadModelListAsync();

            if (IsFoundryLocal)
            {
                // 旧来の挙動: モデルロードが終わるまでSend不可
                InstructionText = "まず、使用するモデルを選択して、モデルロードボタンを押してください。";
                SendInstructionText = "チャットの内容を入力したら、Sendボタンを押してください。ただしモデルのロードが終わるまではSendボタンは有効になりません。";
                IsEnableLoadModel = true;
                IsEnableSend = false;
            }
            else
            {
                // OpenAI: すぐ送信可能（モデルロード操作は不要）
                InstructionText = "使用するモデルを選択してください（即時送信できます）。";
                SendInstructionText = "チャットの内容を入力して、Sendボタンで送信できます。";
                IsEnableLoadModel = false;
                // 選択中のデプロイメントを有効化（システムメッセージ初期化含む）
                if (SelectedModel != null)
                {
                    await _model.LoadOrDownloadModelAsync(SelectedModel.Id);
                }
                IsEnableSend = true;
            }
        }
        catch (Exception e)
        {
            Result += e.ToString() + "\n";
            throw;
        }
    }

    // モデル一覧取得
    private async Task LoadModelListAsync()
    {
        ModelList.Clear();
        var models = await _model.GetAvailableModelsAsync();
        foreach (var m in models)
        {
            var display = m.DisplayName; // Azure OpenAIはDL不要
            ModelList.Add(new ModelItem { Id = m.Id, DisplayName = display, IsCached = true });
        }
        // appsettings.jsonのDeploymentNameと一致するモデルを選択
        var configModelId = _configuration["AzureOpenAI:DeploymentName"];
        var match = ModelList.FirstOrDefault(x => x.Id == configModelId);
        SelectedModel = match ?? ModelList.FirstOrDefault();
        // Foundry Local 時のみロードボタンを有効化（OpenAI は OnLoaded で無効化）
        if (IsFoundryLocal) IsEnableLoadModel = true;
    }

    // モデル選択変更時の挙動
    partial void OnSelectedModelChanged(ModelItem? value)
    {
        // OpenAI モードでは選択変更で即アクティブモデルに反映
        if (!IsFoundryLocal && value != null)
        {
            _ = _model.LoadOrDownloadModelAsync(value.Id);
        }
    }

    // モデルロードコマンド（Foundry Local 用）
    [RelayCommand(CanExecute = nameof(IsEnableLoadModel))]
    private async Task LoadSelectedModelAsync()
    {
        if (SelectedModel == null) return;
        Result = string.Empty;
        ProgressStr = string.Empty;
        await _model.LoadOrDownloadModelAsync(SelectedModel.Id);
        IsEnableSend = _model.IsModelLoaded;
    }

    [RelayCommand(CanExecute = nameof(IsEnableSend))]
    private async Task OnSendAsync()
    {
        IsEnableSend = false;
        try
        {
            Result += $"\n{Input}\n";
            var input = Input;
            Input = string.Empty; // 入力欄をクリア
            await _model.SendAsync(input ?? string.Empty);
        }
        finally
        {
            // Foundry Local/OpenAI ともに送信後は再度送信可能
            IsEnableSend = true;
        }
    }
}

# blog_draft_learn_and_suggest
ブログの癖を学習して、それに合わせた下書きを作成・提案するソフト。今のところ個人的な実験用に作っているため、入力など使い方の説明は不親切な状態。

## 要件

1. 次のものを、ソフト外で事前に手動準備しておく前提とする。
    1. 既存のブログ記事から"文体・語彙・段落構成・見出しの癖"を要約した文体カード。appsettings.jsonで与える。
    1. 既存のブログ記事をRAGとして類似検索できるベクトルDB。Azure AI Searchを使用する（Azure OpenAI On Your Dataは使用しない）。
1. GUIから、次に作成したい記事の内容のポイントや概要を、チャットの入力として入力する。
1. 入力を受け取って、手動準備された情報をコンテキストに付加した上で、LLMへ記事の下書き案をMarkdown形式で作成（推論）させる。図を含む場合、図はMarmeid.js形式で記載させる。
1. 結果を、チャットの出力へ出す。

---

## 仕様（Azure OpenAI + Azure AI Search RAG 前提）

- 既存実装の方針を踏襲する。
  - GUIはWinUI 3を用いる（既存の画面構成・操作フローを継続）。
  - LLMとのやり取りはAzure OpenAI Chat Completionsを使用。ストリーミングは将来対応。従来のFoundry LocalやOn Your Dataは使用しない。
  - RAGはAzure AI SearchのVectorizableTextQueryを用いたベクトル類似検索で関連記事を取得し、アプリ側でプロンプトへ合成する。
  - 文体カードはプロンプトの先頭ガイド（system相当）として適用。

### 全体アーキテクチャ
- プレゼンテーション: WinUI 3（デプロイ選択、入力テキスト、進捗・出力表示）。
- アプリ層:
  - RetrievalService: Azure AI SearchでVectorizableTextQueryを利用して近傍ドキュメントを取得。
  - PromptBuilder（ChatModel内実装）: 文体カードとRAGの結果をユーザー要求に合成。
  - ChatModel: Azure OpenAI Chat Completions REST APIを呼び出し、応答をUIへ反映。

### 動作シーケンス
1. 起動後、ユーザーが使用するAzure OpenAIのデプロイメント（モデル）を選択し「モデルロード」。
2. ユーザーが記事のポイント/概要を入力。
3. 送信時、アプリは以下を順に実施：
   - 文体カード（appsettings.json）を読み込み、ガイドラインをsystemとしてセット。
   - Azure AI SearchでVectorizableTextQueryにより近傍ドキュメントをTop-K取得、簡易整形してRAGセクションを作成。
   - 「RAGセクション」「ユーザー要求」を結合したメッセージをuserとして追加。
   - Chat Completionsへ送信（Markdown出力、図はMermaid.js形式）。
4. 応答をUIに表示。

### Azure AI Search（RAG）前提
- インデックスは事前構築済み。フィールド例：
  - text（本文）、vector（ベクトル格納先）、title/url等のメタデータ（任意）
- 検索時はVectorizableTextQueryを使用し、埋め込み生成はAI Search側に委譲する。
- appsettings.jsonでTopK、MinimumScore、TextFieldName、VectorFieldNameを指定。

### 出力仕様
- 出力はMarkdown。図が含まれる場合はMermaid.js記法。
- 構成は要約→前提→本文→結論。

### エラーハンドリング/タイムアウト
- Chat Completions/APIのタイムアウトはAzureOpenAI:RequestTimeoutSecondsに従う。
- 検索の閾値はAzureAISearch:MinimumScoreを利用。

---

## appsettings.json で入力が必要なパラメータ

- AzureOpenAI（必須）
  - AzureOpenAI:Endpoint: https://<your-aoai>.openai.azure.com
  - AzureOpenAI:ApiKey: Azure OpenAIのAPIキー
  - AzureOpenAI:DeploymentName: 使用するChat Completionsのデプロイ名（例: "gpt-4o-mini"等）
  - AzureOpenAI:ApiVersion: 使用するAPIバージョン（例: "2024-06-01"）
  - AzureOpenAI:MaxTokens: 応答の最大トークン数（例: 2048〜4096）
  - AzureOpenAI:RequestTimeoutSeconds: 推論要求のタイムアウト（秒）。0以下は無制限。
- StyleCard（文体カードをMarkdown文字列で与える）
  - StyleCard:Content: 文体カード本文（Markdown）。改行は \n で記述。
  - StyleCard:Title: 任意。文体カード名。
- AzureAISearch（RAG用）
  - AzureAISearch:Enabled: true/false（RAGを使用するか）。
  - AzureAISearch:Endpoint: https://<検索サービス名>.search.windows.net
  - AzureAISearch:ApiKey: 検索サービスキー
  - AzureAISearch:IndexName: インデックス名
  - AzureAISearch:TextFieldName: 本文のフィールド名（例: "text"）。
  - AzureAISearch:VectorFieldName: ベクトルのフィールド名（例: "vector"）。
  - AzureAISearch:TopK: 取得件数（例: 5）。
  - AzureAISearch:MinimumScore: 類似度閾値（0.0〜1.0、任意）。

### appsettings.json サンプル
```json
{
  "AzureOpenAI": {
    "Endpoint": "https://<your-aoai>.openai.azure.com",
    "ApiKey": "<your-aoai-key>",
    "DeploymentName": "gpt-4o-mini",
    "ApiVersion": "2024-06-01",
    "MaxTokens": 2048,
    "RequestTimeoutSeconds": 0
  },
  "StyleCard": {
    "Title": "ブログ文体カード（例）",
    "Content": "## 文体\n- 一人称。\n- 丁寧語は禁止、常体。\n## 見出し\n- H2で大見出し、H3で詳細。\n## 語彙\n- 専門語は最初に定義。\n## 禁止\n- 絵文字\n- 過度な比喩\n"
  },
  "AzureAISearch": {
    "Enabled": true,
    "Endpoint": "https://<your-search>.search.windows.net",
    "ApiKey": "<your-key>",
    "IndexName": "blog-index",
    "TextFieldName": "text",
    "VectorFieldName": "vector",
    "TopK": 5,
    "MinimumScore": 0.0
  }
}
```

注意: APIキーなどの秘匿情報は、環境変数やユーザーシークレットで上書きし、リポジトリへはコミットしないこと。


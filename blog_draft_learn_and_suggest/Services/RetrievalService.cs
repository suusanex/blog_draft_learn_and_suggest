using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;

namespace blog_draft_learn_and_suggest.Services
{
    public class RetrievalService
    {
        private readonly IConfiguration _config;
        public RetrievalService(IConfiguration config)
        {
            _config = config;
        }

        public async Task<IReadOnlyList<SearchResult<SearchDocument>>> SearchAsync(string query)
        {
            var endpoint = _config["AzureAISearch:Endpoint"];
            var key = _config["AzureAISearch:ApiKey"];
            var indexName = _config["AzureAISearch:IndexName"];
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(indexName))
            {
                return Array.Empty<SearchResult<SearchDocument>>();
            }

            var client = new SearchClient(new Uri(endpoint), indexName, new AzureKeyCredential(key));

            // VectorizableTextQuery を使って埋め込み生成をAI Search側に任せる
            var vectorField = _config["AzureAISearch:VectorFieldName"] ?? "vector";
            var textField = _config["AzureAISearch:TextFieldName"] ?? "text";
            var topK = _config.GetValue<int>("AzureAISearch:TopK", 5);
            var minScore = _config.GetValue<double>("AzureAISearch:MinimumScore", 0.0);

            var vectorQuery = new VectorizableTextQuery(query)
            {
                KNearestNeighborsCount = topK,
                Fields = { vectorField }
            };

            var options = new SearchOptions
            {
                Size = topK,
                QueryType = SearchQueryType.Simple,
                VectorSearch = new VectorSearchOptions
                {
                    Queries = { vectorQuery }
                }
            };
            options.Select.Add(textField);

            // テキスト クエリは空文字列。ベクトル類似で取得。
            var resp = await client.SearchAsync<SearchDocument>(string.Empty, options);
            var list = resp.Value.GetResults().ToList();

            if (minScore > 0)
            {
                list = list.Where(r => r.Score.HasValue && r.Score.Value >= minScore).ToList();
            }

            return list;
        }
    }
}

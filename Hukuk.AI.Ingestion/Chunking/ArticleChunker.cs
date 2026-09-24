using Hukuk.AI.Data.Entities;
using Hukuk.AI.Ingestion.Parsing;
using Microsoft.ML.Tokenizers;

namespace Hukuk.AI.Ingestion.Chunking;

public class ArticleChunker(Tokenizer tokenizer) : IChunker
{
    public string Name => "A";

    public IEnumerable<Chunk> Split(string law, IReadOnlyList<ParsedArticle> articles) =>
        articles.Select(a => new Chunk(Name, law, [new ArticleRef(a.ArticleType, a.ArticleNo)], a.Content, tokenizer.CountTokens(a.Content)));
}

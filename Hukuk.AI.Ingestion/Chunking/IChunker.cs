using Hukuk.AI.Ingestion.Parsing;

namespace Hukuk.AI.Ingestion.Chunking;

public interface IChunker
{
    string Name { get; }
    IEnumerable<Chunk> Split(string law, IReadOnlyList<ParsedArticle> articles);
}
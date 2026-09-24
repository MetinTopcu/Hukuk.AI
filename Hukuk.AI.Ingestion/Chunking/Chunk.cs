using Hukuk.AI.Data.Entities;

namespace Hukuk.AI.Ingestion.Chunking;

public record Chunk(
    string Strategy,
    string Law,
    ArticleRef[] Articles,
    string Text,
    int TokenCount
    );

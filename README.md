# Hukuk.AI

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)
![Semantic Kernel](https://img.shields.io/badge/Semantic%20Kernel-Azure%20OpenAI-0078D4)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-pgvector-4169E1?logo=postgresql&logoColor=white)
![Redis](https://img.shields.io/badge/Redis-8-DC382D?logo=redis&logoColor=white)

**Turkish legal question answering with article citations, built on RAG and measured at every step.**

Hukuk.AI answers questions about the Turkish Labour Law (4857) and the Turkish Code of Obligations (6098), and cites the articles each answer rests on. It also reads an uploaded contract, reports its risky clauses with the law article behind each one, and answers questions about the document.

> A portfolio project. Its answers are not legal advice.

## Highlights

- **Every design choice is measured.** A dedicated evaluation project compares chunking strategies, search methods, token budgets and prompts on a hand-built question set, so decisions come from numbers.
- **Fast first token.** SSE streaming, a two-level embedding cache and request coalescing cut time to first token from 5–7 s to about 1.8 s.
- **A semantic cache that is safe for law.** Two questions about neighbouring articles can score .946 similar, so cache candidates must also pass a number guard and an LLM verifier before an answer is reused.
- **Direct article lookup.** References such as "TBK 344" or "İş K. m. 17" are parsed and fetched by metadata first; vector search fills the rest.
- **Contract analysis.** Uploads (PDF, DOCX, image) are queued on a Redis Stream, read with Azure Document Intelligence, and kept in Redis for two hours only. A risk is reported only when it has a valid article citation.
- **Declines out-of-scope questions** instead of summarising unrelated articles.

## Results

Single runs on the project's own evaluation sets.

| Measure | Result |
|---|---|
| Retrieval, vector search at a 2000-token budget | Recall .940, MRR .851 |
| Retrieval, BM25 at the same budget | Recall .687, MRR .571 |
| Live pipeline at a 4000-token budget | Recall .993, answer score .918 (LLM judge) |
| Time to first token | 5–7 s → about 1.8 s |
| Semantic answer cache | 78% of paraphrases served from cache, 0 trap questions leaked |
| Contract risk report | 11 of 11 planted risks found and cited, 1 false alarm |
| Contract question answering | .883 – .967 answer score |

Hybrid search (vector + BM25) never beat plain vector search on this data, so the live pipeline uses vector search with direct article lookup.

## Architecture

```mermaid
flowchart LR
    Client --> API["ASP.NET Core API<br/>JSON + SSE"]
    API --> Cache[("Redis<br/>embedding + semantic answer cache")]
    API --> Retrieval["Retrieval<br/>article lookup + vector search"]
    Retrieval --> PG[("PostgreSQL + pgvector<br/>HNSW")]
    API --> LLM["Azure OpenAI<br/>via Semantic Kernel"]
    Upload["Contract upload"] --> Stream[("Redis Stream")]
    Stream --> Worker["Background worker<br/>OCR · chunk · embed · risk report"]
    Worker --> Cache
```

| Project | Role |
|---|---|
| `Hukuk.AI` | Web API: questions, document upload, SSE streaming, background worker |
| `Hukuk.AI.Retrieval` | Query routing, vector search, answer generation, semantic cache |
| `Hukuk.AI.Documents` | Contract reading, chunking, risk report, document Q&A |
| `Hukuk.AI.Ingestion` | Law text parser, four chunking strategies, embedding |
| `Hukuk.AI.Data` | EF Core model and migrations for pgvector |
| `Hukuk.AI.Evaluation` | Retrieval, answer, cache, scale and contract evaluations |

## API

| Method | Route | Does |
|---|---|---|
| `POST` | `/api/questions` | Answer a legal question with article citations |
| `POST` | `/api/questions/stream` | The same answer streamed over SSE |
| `POST` | `/api/documents` | Upload a contract for analysis |
| `GET` | `/api/documents/{id}/report` | Risk report with cited articles |
| `POST` | `/api/documents/{id}/questions` | Ask about the uploaded document (also `/stream`) |
| `GET` | `/api/documents/{id}/events` | Processing status over SSE |

## Run locally

Requires the .NET 9 SDK, Docker, and an Azure OpenAI resource with a chat and an embedding deployment.

```bash
cd Hukuk.AI
cp .env.example .env            # set POSTGRES_PASSWORD and REDIS_PASSWORD
docker compose up -d            # PostgreSQL + pgvector on 5433, Redis on 6380
```

Keys and connection strings live in .NET user secrets, never in the repository. Set `ConnectionStrings:DefaultConnection`, `ConnectionStrings:Redis`, `AI:AzureOpenAIEndpoint`, `AI:AzureOpenAIKey`, `AI:ChatDeploymentName` and `AI:EmbeddingDeploymentName`.

```bash
dotnet ef database update --project Hukuk.AI.Data
dotnet run --project Hukuk.AI.Ingestion -- embed   # parse, chunk and embed the law texts
dotnet run --project Hukuk.AI                      # http://localhost:5073
```

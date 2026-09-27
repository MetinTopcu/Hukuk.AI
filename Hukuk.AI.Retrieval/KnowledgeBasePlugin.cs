using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace Hukuk.AI.Retrieval;

// Bilgi tabanı araması SK fonksiyonu olarak. Şu an sabit pipeline (LegalAnswerService) doğrudan çağırıyor;
// ileride agent modunda (auto function calling) LLM'e araç olarak da verilebilir, Description'lar bunun için.
public class KnowledgeBasePlugin(KnowledgeSearch search)
{
    public const string PluginName = "KnowledgeBase";
    public const string SearchFunction = "search_knowledge_base";

    [KernelFunction(SearchFunction)]
    [Description("Türk mevzuatı bilgi tabanında (Türk Borçlar Kanunu kira hükümleri, İş Kanunu) soruyla ilgili kanun maddelerini arar.")]
    public Task<List<KnowledgeSource>> SearchAsync(
        [Description("Kullanıcının sorusu, olduğu gibi")] string question,
        CancellationToken cancellationToken) =>
        search.SearchAsync(question, cancellationToken);
}

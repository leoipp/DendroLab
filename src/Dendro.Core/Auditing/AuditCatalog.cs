namespace Dendro.Core.Auditing;

public sealed record AuditFeature(string Id, string Name, string Module, string Criticality, string Status, string Notes);

/// <summary>Repository-derived initial inventory. Presence in this catalog is not evidence of correct behavior.</summary>
public static class AuditCatalog
{
    public static IReadOnlyList<AuditFeature> InitialFeatures { get; } =
    [
        new("project-storage", "Projetos DLB e histórico", "Projeto e persistência", "Crítica", "Não testado", "Integridade, migração, recuperação e concorrência precisam de auditoria dedicada."),
        new("tabular-import", "Importação e exportação de tabelas", "Dados", "Alta", "Não testado", "Excel, XLS legado, DLB, filtros, junções e exportações."),
        new("inventory-ifc-ifpc", "Processamento de inventário IFC/IFPC", "Inventário", "Crítica", "Não testado", "Requer comparação independente com casos de referência e unidades explícitas."),
        new("volume-models", "Volumetria e modelos de taper", "Mensuração florestal", "Alta", "Não testado", "Métodos e domínios de aplicação devem ser avaliados separadamente."),
        new("growth-models", "Crescimento, produção e índice de sítio", "Mensuração florestal", "Alta", "Não testado", "Extrapolação e plausibilidade biológica não foram validadas nesta etapa."),
        new("statistics-sampling", "Estatística e amostragem", "Estatística", "Alta", "Não testado", "Compatibilidade do estimador com o delineamento deve ser verificada."),
        new("gis-vector", "GIS vetorial e amostragem espacial", "GIS", "Alta", "Não testado", "CRS, geometria, área e operações espaciais requerem casos independentes."),
        new("remote-sensing", "NOAA, Sentinel-2 e séries temporais", "Sensoriamento remoto", "Média", "Não testado", "Serviços externos e consistência espacial/temporal não foram consultados."),
        new("user-interface", "Interface WPF, navegação e acessibilidade", "UI/UX", "Média", "Não testado", "O catálogo não substitui inspeção manual nem testes de automação de interface."),
        new("external-services", "Atualizações e integrações externas", "Integrações", "Média", "Não testado", "Disponibilidade, rede e credenciais dependem do ambiente de execução.")
    ];
}

public static class AuditRunner
{
    /// <summary>Records only that the local catalog was inspected; it runs no product tests.</summary>
    public static AuditRun RecordCatalogRecognition(AuditLedger ledger, string applicationVersion)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        var now = DateTimeOffset.UtcNow;
        var run = new AuditRun(
            Guid.NewGuid().ToString("N"),
            "Reconhecimento do catálogo local",
            applicationVersion,
            null,
            $"Windows; .NET {Environment.Version}",
            now,
            now,
            "Concluído — catálogo reconhecido; funcionalidades não testadas",
            $"{AuditCatalog.InitialFeatures.Count} itens de funcionalidade exibidos. Nenhum fluxo ou cálculo foi executado.",
            "Leitura do catálogo estático compilado no aplicativo; sem acesso a projetos ou dados do usuário.");
        ledger.AddRun(run);
        return run;
    }
}

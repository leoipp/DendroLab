using Dendro.Core.Auditing;

namespace Dendro.Core.Tests;

public sealed class AuditLedgerTests
{
    [Fact]
    public void RecommendationDecisionIsPersistedWithAnAuditEvent()
    {
        string folder = Path.Combine(Path.GetTempPath(), "DendroAuditTests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "audit.db");
        try
        {
            using (var ledger = new AuditLedger(path))
            {
                var created = DateTimeOffset.UtcNow;
                ledger.AddRecommendation(new AuditRecommendation(
                    "rec-1", null, "Revisar validação", "Evidência concreta", "Correção", "Médio",
                    AuditStates.AwaitingApproval, "Validar entradas", "Caso reproduzível", null,
                    "Critério de aceitação", created, created));

                var decided = ledger.SetRecommendationState("rec-1", AuditStates.Rejected, "Administrador", "Adiado para revisão");

                Assert.Equal(AuditStates.Rejected, decided.Status);
                Assert.Equal(AuditStates.Rejected, Assert.Single(ledger.GetRecommendations()).Status);
                var events = ledger.GetEvents();
                Assert.Equal(2, events.Count);
                var decisionEvent = Assert.Single(events, e => e.NewState == AuditStates.Rejected);
                Assert.Equal(AuditStates.AwaitingApproval, decisionEvent.PreviousState);
                Assert.Equal(AuditStates.Rejected, decisionEvent.NewState);
                Assert.Equal("Administrador", decisionEvent.Actor);
            }

            using var reopened = new AuditLedger(path);
            Assert.Equal(AuditStates.Rejected, Assert.Single(reopened.GetRecommendations()).Status);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void RejectsInvalidStateAndConfidence()
    {
        string path = Path.Combine(Path.GetTempPath(), "DendroAuditTests", Guid.NewGuid().ToString("N"), "audit.db");
        using var ledger = new AuditLedger(path);
        Assert.Throws<ArgumentException>(() => ledger.AddRecommendation(new AuditRecommendation(
            "rec-1", null, "x", "x", "Correção", "Baixo", "Executado automaticamente", "x", "x", null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.AddFinding(new AuditFinding(
            "finding-1", "run-1", "Engenharia", "Core", "Defeito", "x", "Baixo", AuditStates.Identified,
            "x", null, null, 1.5, DateTimeOffset.UtcNow)));
    }
}

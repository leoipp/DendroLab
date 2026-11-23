using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dendro.Core.Auditing;

namespace Dendro.Desktop;

internal sealed class AuditCenterView : ToolFormView
{
    private readonly DataGrid features = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        MinHeight = 240,
    };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) };
    private readonly TextBlock recentRun = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };

    public AuditCenterView()
    {
        AddColumn("Funcionalidade", nameof(AuditFeature.Name), 2);
        AddColumn("Módulo", nameof(AuditFeature.Module), 1.4);
        AddColumn("Criticidade", nameof(AuditFeature.Criticality), 0.8);
        AddColumn("Estado", nameof(AuditFeature.Status), 1);
        AddColumn("Escopo e limites", nameof(AuditFeature.Notes), 2.3);

        var content = new StackPanel { Margin = new Thickness(16, 8, 16, 14) };
        content.Children.Add(new TextBlock
        {
            Text = "Central de auditoria",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(48, 74, 96)),
            Margin = new Thickness(0, 4, 0, 4),
        });
        content.Children.Add(new TextBlock
        {
            Text = "Inventário inicial do repositório e registro local de evidências. Nenhum agente de IA ou execução recorrente está configurado.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(104, 122, 137)),
            Margin = new Thickness(0, 0, 0, 4),
        });
        content.Children.Add(summary);
        content.Children.Add(recentRun);
        content.Children.Add(features);
        SetForm(content);
        SetPrimaryAction(Ui.Button("Registrar reconhecimento", RecordRecognition));
        RefreshData();
    }

    private void AddColumn(string title, string property, double weight)
    {
        features.Columns.Add(new DataGridTextColumn
        {
            Header = title,
            Binding = new System.Windows.Data.Binding(property),
            Width = new DataGridLength(weight, DataGridLengthUnitType.Star),
        });
    }

    private void RefreshData()
    {
        features.ItemsSource = AuditCatalog.InitialFeatures;
        using var ledger = new AuditLedger(AuditLedger.DefaultPath);
        var runs = ledger.GetRuns(1);
        recentRun.Text = runs.Count == 0
            ? "Último reconhecimento: ainda não registrado."
            : $"Último reconhecimento: {runs[0].StartedAt.ToLocalTime():g} — {runs[0].Summary}";
        summary.Text = $"{AuditCatalog.InitialFeatures.Count} áreas catalogadas. Estado atual: não testado; abrir esta central não executa os módulos.";
    }

    private void RecordRecognition()
    {
        try
        {
            string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "desconhecida";
            using var ledger = new AuditLedger(AuditLedger.DefaultPath);
            AuditRunner.RecordCatalogRecognition(ledger, version);
            RefreshData();
            Ui.Alert("Central de auditoria", "Reconhecimento do catálogo registrado. Nenhuma funcionalidade do aplicativo foi executada ou validada.", Ui.MessageTone.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Ui.Error(ex);
        }
    }
}

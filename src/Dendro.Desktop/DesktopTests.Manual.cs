using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Dendro.Desktop;

internal static partial class DesktopTests
{
    private static async Task AuditManual(MainWindow owner, string root)
    {
        var manual = new ManualWindow { Owner = owner, ShowActivated = false };
        manual.Show();
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(ManualWindow.Topics.Count >= 40, "manual inclui ferramentas e configurações");
        Check(ManualWindow.Topics.All(topic => topic.Content.Length > 300), "assuntos possuem conteúdo explicativo");
        foreach (string title in new[]
        {
            "Processador", "Revisão de inventário", "Auditoria pós-processamento", "Juntar tabelas", "Parear medições", "Gerar coluna", "Estratificar",
            "Limpeza integrada", "Ativo biológico", "Gráficos", "Ajuste de regressão", "Aplicar regressões", "Ajustar rede neural", "Ajustar Random Forest",
            "Ajustar Gradient Boosting", "Ajustar SVR", "Fronteira de eficiência", "Comparar curvas de produção", "ICA e IMA", "Testes estatísticos",
            "Validação de modelos", "Isolation Forest", "K-means", "Amostragem"
        }) Check(manual.SelectTopic(title) && manual.CurrentContent.Contains(title), "manual navega para " + title);
        manual.Search("configuracoes");
        Check(manual.SelectTopic("Configurações: identificação") && manual.CurrentContent.Contains("MISTO"), "busca ignora acentos e exibe configuração técnica");
        manual.Search("PESO_HA");
        Check(manual.SelectTopic("GRTS: desenho e parâmetros") && manual.CurrentContent.Contains("PI_EXATA"), "busca encontra termos no conteúdo técnico");
        manual.UpdateLayout();
        var visual = (FrameworkElement)manual.Content;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(root, "manual-grts.png"))) encoder.Save(stream);
        manual.Search("");
        foreach (var (title, formula) in new[]
        {
            ("Processador", "G_ha ="), ("Ajuste de regressão", "JᵀJ"), ("Testes estatísticos", "SQ_entre"),
            ("Validação de modelos", "RMSE ="), ("K-means", "Silhouette"), ("Isolation Forest", "c(n)"),
            ("Configurações: estatística do inventário", "n_válidos"), ("Configurações: altura e modelos", "Ridge:"),
            ("GRTS: desenho e parâmetros", "Σπᵢ = n")
        }) Check(manual.SelectTopic(title) && manual.CurrentContent.Contains(formula), "manual contém cálculo detalhado de " + title);
        manual.Search("Welch–Satterthwaite");
        Check(manual.SelectTopic("Testes estatísticos") && manual.CurrentContent.Contains("ν ="), "busca inclui detalhamento matemático");
        manual.Search("MAPE"); manual.SelectTopic("Validação de modelos");
        var formulas = LogicalControls<RichTextBox>(manual).Single();
        Check(formulas.Document.Blocks.OfType<Paragraph>().Any(paragraph => paragraph.BorderThickness.Left == 3), "equações são exibidas em blocos próprios selecionáveis");
        formulas.ScrollToEnd(); manual.UpdateLayout();
        bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(root, "manual-calculos.png"))) encoder.Save(stream);
        manual.Search("termo-inexistente-qa-12345");
        Check(manual.VisibleTopicCount == 0 && manual.CurrentContent.Contains("Nenhum assunto"), "manual trata busca sem resultados");
        manual.Search("");
        Check(manual.VisibleTopicCount == ManualWindow.Topics.Count, "limpar busca recupera todos os assuntos");
        manual.Close();
    }
}

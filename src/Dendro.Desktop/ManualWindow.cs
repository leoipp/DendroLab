using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Dendro.Desktop;

internal sealed record ManualTopic(string Group, string Title, string Content);

internal sealed class ManualWindow : Window
{
    private readonly TextBox search = Ui.Input();
    private readonly TreeView navigation = new() { BorderThickness = new Thickness(0), Margin = new Thickness(6) };
    private readonly RichTextBox reader = new()
    {
        IsReadOnly = true, IsReadOnlyCaretVisible = false, BorderThickness = new Thickness(0),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = Brushes.White, Padding = new Thickness(24, 12, 24, 16)
    };
    internal static IReadOnlyList<ManualTopic> Topics { get; } = LoadTopics();

    internal ManualWindow()
    {
        Title = "Manual do DendroLab";
        Width = 1180; Height = 820; MinWidth = 760; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        var root = new DockPanel { Background = Brushes.White }; Content = root;
        var header = new StackPanel { Margin = new Thickness(20, 12, 20, 10) };
        header.Children.Add(Ui.Text("Manual do DendroLab", 23));
        header.Children.Add(Ui.Text("Ferramentas, cálculos, configurações e exemplos de uso. Pesquise um termo ou escolha um assunto na árvore.", 12));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 210 });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(columns);
        var left = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(246, 249, 252)) };
        var searchHost = new StackPanel { Margin = new Thickness(12) };
        searchHost.Children.Add(Ui.Text("Pesquisar no manual", 12)); searchHost.Children.Add(search);
        DockPanel.SetDock(searchHost, Dock.Top); left.Children.Add(searchHost); left.Children.Add(navigation);
        columns.Children.Add(left);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(220, 228, 236)) };
        Grid.SetColumn(splitter, 1); columns.Children.Add(splitter);
        Grid.SetColumn(reader, 2); columns.Children.Add(reader);
        System.Windows.Automation.AutomationProperties.SetAutomationId(search, "ManualSearch");
        System.Windows.Automation.AutomationProperties.SetAutomationId(navigation, "ManualTopics");
        System.Windows.Automation.AutomationProperties.SetAutomationId(reader, "ManualContent");
        search.TextChanged += (_, _) => RebuildTree();
        navigation.SelectedItemChanged += (_, _) =>
        {
            if (navigation.SelectedItem is TreeViewItem { Tag: ManualTopic topic }) ShowTopic(topic);
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); args.Handled = true; }
            else if (args.Key == Key.Escape) Close();
        };
        RebuildTree();
    }

    internal void Search(string term) => search.Text = term;
    internal string CurrentContent => new TextRange(reader.Document.ContentStart, reader.Document.ContentEnd).Text;
    internal int VisibleTopicCount => navigation.Items.OfType<TreeViewItem>().Sum(group => group.Items.Count);

    internal bool SelectTopic(string title)
    {
        foreach (var item in navigation.Items.OfType<TreeViewItem>().SelectMany(group => group.Items.OfType<TreeViewItem>()))
            if (item.Tag is ManualTopic topic && topic.Title == title) { item.IsSelected = true; item.BringIntoView(); return true; }
        return false;
    }

    private void RebuildTree()
    {
        string query = Normalize(search.Text);
        navigation.Items.Clear();
        TreeViewItem? first = null;
        foreach (var group in Topics.Where(topic => Normalize(topic.Group + " " + topic.Title + " " + topic.Content).Contains(query)).GroupBy(topic => topic.Group))
        {
            var root = new TreeViewItem { Header = group.Key, IsExpanded = true, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 3) };
            foreach (var topic in group)
            {
                var item = new TreeViewItem { Header = topic.Title, Tag = topic, FontWeight = FontWeights.Normal, Margin = new Thickness(0, 2, 0, 2) };
                root.Items.Add(item); first ??= item;
            }
            navigation.Items.Add(root);
        }
        if (first != null) first.IsSelected = true;
        else reader.Document = Document("Nenhum assunto encontrado", "Tente outro termo ou limpe a pesquisa. A busca considera o título e todo o conteúdo de cada assunto.");
    }

    private void ShowTopic(ManualTopic topic)
    {
        reader.Document = Document(topic.Title, topic.Content, topic.Group);
        reader.ScrollToHome();
    }

    private static FlowDocument Document(string title, string body, string group = "")
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 14, PagePadding = new Thickness(0), ColumnWidth = 10000,
            Foreground = new SolidColorBrush(Color.FromRgb(38, 61, 82)), LineHeight = 23 };
        if (group.Length > 0) doc.Blocks.Add(new Paragraph(new Run(group)) { FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 6) });
        doc.Blocks.Add(new Paragraph(new Run(title)) { FontSize = 27, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        foreach (string line in body.Split('\n'))
        {
            string text = line.Trim();
            if (text.Length == 0) continue;
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 11) };
            if (text.StartsWith("### ")) { text = text[4..]; paragraph.FontSize = 18; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 18, 0, 8); }
            else if (text.StartsWith("> "))
            {
                text = text[2..]; paragraph.FontFamily = new FontFamily("Consolas"); paragraph.FontSize = 13;
                paragraph.Background = new SolidColorBrush(Color.FromRgb(239, 245, 250));
                paragraph.BorderBrush = new SolidColorBrush(Color.FromRgb(71, 124, 167));
                paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                paragraph.Padding = new Thickness(12, 8, 12, 8);
                paragraph.Margin = new Thickness(0, 4, 0, 12);
            }
            else if (text.StartsWith("- ")) { text = "•  " + text[2..]; paragraph.Margin = new Thickness(12, 0, 0, 8); }
            foreach (string fragment in Regex.Split(text, @"(\*\*.*?\*\*|`[^`]+`)") )
            {
                if (fragment.StartsWith("**") && fragment.EndsWith("**") && fragment.Length >= 4) paragraph.Inlines.Add(new Bold(new Run(fragment[2..^2])));
                else if (fragment.StartsWith('`') && fragment.EndsWith('`') && fragment.Length >= 2) paragraph.Inlines.Add(new Run(fragment[1..^1]) { FontFamily = new FontFamily("Consolas"), Background = new SolidColorBrush(Color.FromRgb(240, 244, 247)) });
                else paragraph.Inlines.Add(new Run(fragment));
            }
            doc.Blocks.Add(paragraph);
        }
        return doc;
    }

    private static string Normalize(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();

    private static IReadOnlyList<ManualTopic> LoadTopics()
    {
        var topics = ReadTopics("Dendro.Desktop.Manual.DendroLab.md");
        var calculations = ReadTopics("Dendro.Desktop.Manual.Calculos.md").ToDictionary(topic => topic.Title, StringComparer.Ordinal);
        if (calculations.Keys.Except(topics.Select(topic => topic.Title), StringComparer.Ordinal).Any())
            throw new InvalidOperationException("Há uma seção de cálculos sem ferramenta correspondente no manual.");
        return topics.Select(topic => calculations.TryGetValue(topic.Title, out var calculation)
            ? topic with { Content = topic.Content + "\n\n" + calculation.Content } : topic).ToArray();
    }

    private static IReadOnlyList<ManualTopic> ReadTopics(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("O conteúdo do manual não foi incluído no aplicativo.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var topics = new List<ManualTopic>();
        string group = "", title = "";
        var body = new StringBuilder();
        void Commit() { if (title.Length > 0) topics.Add(new ManualTopic(group, title, body.ToString().Trim())); body.Clear(); }
        foreach (string line in reader.ReadToEnd().Split('\n'))
        {
            if (line.StartsWith("# ")) { Commit(); title = ""; group = line[2..].Trim(); }
            else if (line.StartsWith("## ")) { Commit(); title = line[3..].Trim(); }
            else body.AppendLine(line.TrimEnd());
        }
        Commit();
        return topics;
    }
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Configuration;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Statistics;
using Dendro.Core.Processing;

namespace Dendro.Desktop;

internal sealed record AnalysisReference(JsonObject Entry);
internal sealed record AnalysisPlotReference(JsonObject Entry);
internal sealed record AnalysisTableReference(JsonObject Entry);

internal static class AnalysisDocuments
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["test"] = "Análise",
        ["h0"] = "Hipótese nula (H0)",
        ["alpha"] = "Significância (α)",
        ["statistic"] = "Estatística calculada",
        ["p_value"] = "Valor-p",
        ["df1"] = "Graus de liberdade 1",
        ["df2"] = "Graus de liberdade 2",
        ["decision"] = "Decisão",
        ["assumptions"] = "Pressupostos",
        ["notes"] = "Observações",
        ["difference"] = "Diferença média A − B",
        ["ci_low"] = "Limite inferior do IC da diferença",
        ["ci_high"] = "Limite superior do IC da diferença",
        ["critical_low"] = "Valor crítico inferior",
        ["critical_high"] = "Valor crítico superior",
        ["n"] = "Observações válidas",
        ["n_a"] = "N da amostra A",
        ["n_b"] = "N da amostra B",
        ["n_pairs"] = "Pares válidos",
        ["mean_a"] = "Média A",
        ["mean_b"] = "Média B",
        ["variance_a"] = "Variância A",
        ["variance_b"] = "Variância B",
        ["sample_a"] = "Identificação da amostra A",
        ["sample_b"] = "Identificação da amostra B",
        ["groups"] = "Grupos",
        ["model"] = "Modelo",
        ["formula"] = "Fórmula",
        ["sse_common"] = "SQ resíduos — equação comum",
        ["sse_separate"] = "SQ resíduos — equações separadas",
        ["omitted"] = "Linhas / pares incompletos omitidos",
        ["omitted_a"] = "Valores omitidos A",
        ["omitted_b"] = "Valores omitidos B",
        ["method"] = "Método",
        ["age_min"] = "Idade mínima",
        ["age_max"] = "Idade máxima",
        ["age_unit"] = "Unidade das idades",
        ["points"] = "Pontos da fronteira",
        ["skipped_bins"] = "Faixas vazias ou insuficientes",
    };

    private static Dendro.Core.Spreadsheets.DataTable SummaryTable(JsonObject summary)
    {
        var table = new Dendro.Core.Spreadsheets.DataTable(["Item", "Resultado"]);
        foreach (
            var pair in summary.Where(p => p.Value is not JsonArray && p.Value is not JsonObject)
        )
            table.Add(
                Labels.GetValueOrDefault(pair.Key, pair.Key),
                pair.Value?.Display() ?? "Indefinido"
            );
        foreach (var curve in summary["curves"] as JsonArray ?? [])
        {
            table.Add("Curva", curve.S("curve"));
            table.Add("Diagnóstico", curve.S("status"));
            table.Add("Idade do maior IMA (anos)", curve.N("maximum_ima_age"));
            table.Add("Maior IMA encontrado no intervalo", curve.N("maximum_ima"));
            foreach (var crossing in curve!["crossings"] as JsonArray ?? [])
                table.Add(
                    crossing.S("kind"),
                    $"{crossing.N("age"):G7} anos · incremento {crossing.N("increment"):G7}"
                );
        }
        return table;
    }

    internal static void Show(
        MainWindow owner,
        ProjectSession session,
        AnalysisReport report,
        JsonObject definition,
        JsonObject? entry = null,
        bool openSeparatePlot = true
    )
    {
        var summaryTable = SummaryTable(report.Summary);
        if (entry == null)
        {
            entry = new()
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["title"] = report.Summary.S("test"),
                ["created"] = DateTimeOffset.Now.ToString("O"),
                ["path"] = definition.S("path"),
                ["definition"] = definition.DeepClone(),
                ["summary"] = report.Summary.DeepClone(),
            };
            SaveSnapshot(entry, report);
            session.Meta.Arr("analyses").Add(entry);
            session.Touch();
        }
        var tabs = new TabControl();
        bool separatePlot = definition.S("kind") is "increments" or "isolation_forest";
        DockPanel? standalonePlot = null;
        summaryTable.Add("Executado em", entry.S("created"));
        if (definition.S("path").Length > 0)
            summaryTable.Add("Fonte", definition.S("path"));
        var resultPanel = new DockPanel();
        var explanation = new StackPanel { Margin = new Thickness(12, 10, 12, 6) };
        explanation.Children.Add(Ui.Text(report.Summary.S("test"), 18));
        foreach (var key in new[] { "h0", "decision", "assumptions", "notes", "method" })
            if (!string.IsNullOrWhiteSpace(report.Summary.S(key)))
            {
                if (key is "assumptions" or "notes" or "method") Ui.AddHelp(explanation, report.Summary.S(key));
                else explanation.Children.Add(Ui.Text(report.Summary.S(key)));
            }
        DockPanel.SetDock(explanation, Dock.Top);
        resultPanel.Children.Add(explanation);
        resultPanel.Children.Add(Ui.Grid(summaryTable.ToJson()));
        tabs.Items.Add(new TabItem { Header = "Resultado", Content = resultPanel });
        if (report.Plot != null)
        {
            var chartBody = PlotBody(report.Plot);
            if (separatePlot) standalonePlot = chartBody;
            else tabs.Items.Add(new TabItem { Header = "Gráfico", Content = chartBody });
            if (!separatePlot && definition.S("kind") == "frontier")
                tabs.SelectedIndex = 1;
        }
        if (report.Details.Count > 1)
            tabs.Items.Add(
                new TabItem
                {
                    Header = definition.S("kind") == "inventory_audit" ? "Parcelas" : "Dados e ajustes",
                    Content = Ui.Grid(report.Details.ToJson()),
                }
            );
        var captured = entry;
        var export = Ui.Button(
            "Exportar relatório XLSX",
            () =>
            {
                var path = Ui.SaveFile("Relatório Excel|*.xlsx", "Analise.xlsx");
                if (path == null)
                    return;
                ProjectStore.AtomicWrite(
                    path,
                    temp =>
                        WorkbookWriter.Write(
                            temp,
                            [new("Resultado", summaryTable), new("Dados", report.Details)]
                        ),
                    true
                );
            }
        );
        var rerun = Ui.AsyncButton(
            "Reexecutar análise",
            () =>
                owner.Execute(async () =>
                {
                    if (definition.S("kind") == "inventory_audit")
                    {
                        await owner.OpenInventoryAudit(definition.S("path"),definition.S("sheet"));
                        return;
                    }
                    var result = await Compute(session, definition);
                    var current = session
                        .Meta.Arr("analyses")
                        .OfType<JsonObject>()
                        .FirstOrDefault(a => a.S("id") == captured.S("id"));
                    if (current == null)
                    {
                        current = captured.DeepClone().AsObject();
                        session.Meta.Arr("analyses").Add(current);
                    }
                    current["summary"] = result.Summary.DeepClone();
                    current["created"] = DateTimeOffset.Now.ToString("O");
                    SaveSnapshot(current, result);
                    session.Touch();
                    Show(owner, session, result, definition, current);
                })
        );
        var body = Ui.WithToolbar(tabs, export, rerun);
        if(definition.S("kind")=="isolation_forest")
            ((WrapPanel)body.Children[0]).Children.Add(Ui.AsyncButton("Gerar gráfico somente com normais",()=>owner.Execute(()=>CreateNormalChart(owner,session,captured))));
        if (definition.S("kind") == "inventory_audit" && definition.S("evidence_path").Length > 0)
        {
            var toolbar = (WrapPanel)body.Children[0];
            foreach (var evidence in new[] { ("Abrir evolução", "AUDITORIA - EVOLUCAO"), ("Evidências por árvore", "AUDITORIA - ARVORES"), ("Critérios e configuração", "AUDITORIA - INFORMACOES") })
                toolbar.Children.Add(Ui.AsyncButton(evidence.Item1, () => owner.Execute(() => owner.OpenTable(definition.S("evidence_path"), evidence.Item2))));
        }
        if (report.Plot != null)
        {
            if (separatePlot)
                ((WrapPanel)body.Children[0]).Children.Add(Ui.AsyncButton("Abrir gráfico", () => OpenPlot(owner, session, captured)));
            var svg = Ui.Button(
                "Exportar gráfico SVG",
                () =>
                {
                    var path = Ui.SaveFile("Gráfico SVG|*.svg", "Analise.svg");
                    if (path != null)
                        ChartExport.Svg(path, report.Plot);
                }
            );
            ((WrapPanel)body.Children[0]).Children.Add(svg);
        }
        owner.Document("analysis:" + entry.S("id"), entry.S("title"), body);
        if (standalonePlot != null && openSeparatePlot)
            owner.Document("analysis-plot:" + entry.S("id"), "Gráfico · " + report.Plot!.Chart.S("title"), AnalysisPlotBody(owner,session,entry,standalonePlot));
    }

    private static void SaveSnapshot(JsonObject entry, AnalysisReport report)
    {
        if (report.Plot == null) return;
        entry["plot_snapshot"] = JsonSerializer.SerializeToNode(report.Plot);
        entry["details_snapshot"] = report.Details.ToJson();
        ProjectSnapshots.CompactEntry(entry);
    }

    private static async Task<AnalysisReport> SavedReport(ProjectSession session, JsonObject entry)
    {
        var snapshot = ProjectSnapshots.Read(entry);
        ChartResult? plot = snapshot["plot_snapshot"] is { } saved
            ? JsonSerializer.Deserialize<ChartResult>(saved.ToJsonString()) : null;
        var details = new Dendro.Core.Spreadsheets.DataTable(["Item", "Resultado"]);
        if (snapshot["details_snapshot"] is JsonObject data)
        {
            details = new(data["columns"]!.AsArray().Select(c => c.Display()));
            foreach (var row in data["rows"]!.AsArray())
                details.Add(row!.AsArray().Select(v => v == null ? null : v is JsonValue n && n.TryGetValue<double>(out double number) ? (object)number : v.Display()).ToArray());
        }
        if (plot == null && entry["definition"].S("kind") is "increments" or "growth_comparison")
        {
            // Legacy curve analyses can rebuild their graph from saved coefficients, without opening source data.
            var definition = entry.Obj("definition").DeepClone().AsObject();
            definition["path"] = "";
            var reconstructed = await Compute(session, definition);
            plot = reconstructed.Plot;
            if (entry["definition"].S("kind") == "increments") details = reconstructed.Details;
            var restored = new AnalysisReport(entry.Obj("summary").DeepClone().AsObject(), details, plot);
            SaveSnapshot(entry, restored); session.Touch();
        }
        return new(entry.Obj("summary").DeepClone().AsObject(), details, plot);
    }

    internal static async Task OpenPlot(MainWindow owner, ProjectSession session, JsonObject entry)
    {
        var report = await SavedReport(session, entry);
        if (report.Plot == null) throw new InvalidOperationException("A análise não contém um gráfico salvo.");
        owner.Document("analysis-plot:" + entry.S("id"), "Gráfico · " + report.Plot.Chart.S("title"), AnalysisPlotBody(owner,session,entry,PlotBody(report.Plot)));
    }
    private static UIElement AnalysisPlotBody(MainWindow owner,ProjectSession session,JsonObject entry,UIElement body)=>entry["definition"].S("kind")=="isolation_forest"
        ? Ui.WithToolbar(body,Ui.AsyncButton("Gerar gráfico somente com normais",()=>owner.Execute(()=>CreateNormalChart(owner,session,entry)))):body;

    internal static async Task CreateNormalChart(MainWindow owner,ProjectSession session,JsonObject entry)
    {
        var report=await SavedReport(session,entry);var data=report.Details;
        int classification=data.Require("IF_Classificacao");
        int[] normal=Enumerable.Range(1,data.Count-1).Where(r=>data.Text(r,classification)=="Normal").ToArray();
        if(normal.Length==0)throw new InvalidOperationException("Não há observações normais nesta análise.");
        var definition=entry.Obj("definition");string ax=definition.S("x"),ay=definition.S("y");
        string rowColumn="IF_Linha_Origem";while(data.Column(rowColumn)>=0)rowColumn+="_";
        string stage=Path.Combine(session.ProjectWorkFolder(),"IF_Normais_"+Guid.NewGuid().ToString("N")[..8]+".dlb");
        var cancellation=owner.OperationCancellation;
        try
        {
            await Task.Run(()=>{
                using var writer=new Dendro.Core.Tabular.CompactBankWriter(stage);string id=writer.AddTable(data.Rows[0].Select(v=>v?.ToString()??"").Concat(new[]{rowColumn}).ToArray(),"Normais");
                foreach(int row in normal){cancellation.ThrowIfCancellationRequested();writer.Add(id,data.Rows[row].Concat(new object?[]{(double)(row+1)}).ToArray());}writer.Complete();
            });
            string path=await session.CommitBankFile(stage);session.AddFile(path);
            string Axis(string symbol)=>symbol=="$score"?"IF_Score":symbol=="$row"?rowColumn:symbol;
            var chart=new JsonObject { ["path"]=path,["sheet"]="Normais",["header_row"]=1,["x"]=Axis(ax),["y"]=Axis(ay),["title"]="Isolation Forest · somente normais",["point_alpha"]=.55,["parent_analysis_id"]=entry.S("id") };
            await owner.OpenChart(path,false,chart,false);
            owner.Log($"Gráfico derivado: {normal.Length:N0} observações normais. Classificação e análise originais preservadas.");
        }
        finally {if(File.Exists(stage))File.Delete(stage);}
    }

    internal static async Task OpenResultTable(MainWindow owner, ProjectSession session, JsonObject entry)
    {
        var report = await SavedReport(session, entry);
        if (report.Details.Count <= 1) throw new InvalidOperationException("A análise não contém uma tabela de resultados salva.");
        var export = Ui.AsyncButton("Exportar tabela XLSX ou DLB…", () => owner.Execute(async () => {
            string? path = Ui.SaveFile("Excel|*.xlsx|Banco DendroLab|*.dlb", "IsolationForest.xlsx");
            if (path == null) return;
            bool compact = Path.GetExtension(path).Equals(".dlb", StringComparison.OrdinalIgnoreCase);
            if (!compact && !Dendro.Core.Tabular.TableTools.FitsExcel(report.Details.Count - 1, report.Details.Width))
                throw new InvalidOperationException("A tabela excede o limite do Excel. Selecione o formato DLB.");
            await Task.Run(() => ProjectStore.AtomicWrite(path, temporary => {
                if (compact)
                {
                    using var writer = new Dendro.Core.Tabular.CompactBankWriter(temporary);
                    string id = writer.AddTable(Enumerable.Range(0, report.Details.Width).Select(c => report.Details.Text(0, c)).ToArray(), "Isolation Forest - Resultados");
                    foreach (var row in report.Details.Rows.Skip(1)) writer.Add(id, row.ToArray());
                    writer.Complete();
                }
                else WorkbookWriter.Write(temporary, [new("Resultados", report.Details)]);
            }, true));
            owner.Log("Tabela de resultados exportada: " + path);
        }));
        owner.Document("analysis-table:" + entry.S("id"), "Tabela · " + entry.S("title"), Ui.WithToolbar(Ui.Grid(report.Details.ToJson()), export));
    }


    internal static DockPanel PlotBody(ChartResult plot)
    {
        var chartBody = new DockPanel();
        var legend = new WrapPanel { Margin = new Thickness(10) };
        foreach (var pair in plot.Chart["group_colors"] as JsonObject ?? new JsonObject())
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 15, 5) };
            item.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 6, 0),
                Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(pair.Value!.GetValue<string>())! });
            item.Children.Add(Ui.Text(pair.Key, 11)); legend.Children.Add(item);
        }
        var config = plot.Chart.DeepClone().AsObject();
        var scatter = new ScatterPlot { Data = RegressionEngine.ToJson(plot), Config = config };
        var fits = plot.Chart["fits"] as JsonArray ?? [];
        int[] Visible() => FitDisplay.Visible(config);
        foreach (var (fit, index) in fits.Select((fit, index) => (fit!, index)))
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            item.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = 22, Y1 = 8, Y2 = 8, Width = 25, Height = 16,
                Stroke = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(FitDisplay.Color(fit))!,
                StrokeThickness = 2,
                StrokeDashArray = fit.B("line_dashed") ? new System.Windows.Media.DoubleCollection([3d, 2d]) : null,
            });
            item.Children.Add(Ui.Text(fit.S("group") + " · " + fit.S("model"), 11));
            var toggle = new CheckBox { Content = item, IsChecked = Visible().Contains(index), Margin = new Thickness(0, 0, 14, 3),
                VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Mostrar ou ocultar esta curva no gráfico" };
            System.Windows.Automation.AutomationProperties.SetName(toggle, "Mostrar curva " + fit.S("group") + " · " + fit.S("model"));
            void RefreshVisibility()
            {
                int[] visible = Visible().Where(i => i != index).ToArray();
                if (toggle.IsChecked == true) visible = [.. visible, index];
                config["selected_fits"] = new JsonArray(visible.Order().Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
                scatter.Config = config;
            }
            toggle.Checked += (_, _) => RefreshVisibility();
            toggle.Unchecked += (_, _) => RefreshVisibility();
            legend.Children.Add(toggle);
        }
        DockPanel.SetDock(legend, Dock.Top);
        chartBody.Children.Add(legend);
        chartBody.Children.Add(scatter);
        return chartBody;
    }
    internal static Task<AnalysisReport> Compute(ProjectSession session, JsonObject definition)
    {
        var copy = definition.DeepClone().AsObject();
        string path = copy.S("path");
        if (path.Length > 0)
            path = session.Resolve(path);
        var anomalyModel = copy.S("kind") == "isolation_forest"
            ? session.Meta.Arr("anomaly_models").OfType<JsonObject>().FirstOrDefault(m => m.S("id") == copy.S("model_id"))?["model"]?.DeepClone()
            : null;
        return Task.Run(() =>
            copy.S("kind") == "inventory_audit"
                ? InventoryAudit.Presentation(InventoryAudit.Run(Dendro.Core.Tabular.TableStore.Read(new(path,copy.S("sheet"))),
                    copy["configuration"]!.AsObject(), copy.S("source_path").Length == 0 ? null : new(session.Resolve(copy.S("source_path")),copy.S("source_sheet"),(int)copy.N("source_header",1)),
                    JsonSerializer.Deserialize<InventoryAuditOptions>(copy["options"]!.ToJsonString())!))
                : copy.S("kind") == "isolation_forest"
                ? IsolationForest.Report(
                    Dendro.Core.Tabular.TableStore.Read(new(path, copy.S("sheet"), (int)copy.N("header_row", 1))),
                    anomalyModel == null ? throw new InvalidDataException("Modelo de anomalias não encontrado no projeto.") : JsonSerializer.Deserialize<IsolationForestModel>(anomalyModel.ToJsonString())!,
                    copy.S("x"), copy.S("y"))
                : copy.S("kind") == "frontier"
                ? FrontierAnalysis.Run(
                    Dendro.Core.Tabular.TableStore.Read(new(path, copy.S("sheet"), (int)copy.N("header_row", 1))),
                    copy["chart"]!.AsObject(),
                    JsonSerializer.Deserialize<FrontierOptions>(copy["options"]!.ToJsonString())!)
                : copy.S("kind") == "growth_comparison"
                ? RunGrowthComparison(copy, path)
                : copy.S("kind") == "increments"
                ? GrowthDiagnostics.Report(
                    JsonSerializer.Deserialize<GrowthCurve[]>(copy["curves"]!.ToJsonString())!,
                    copy.N("min_age"),
                    copy.N("max_age"),
                    copy.B("annual")
                )
                : StatisticalAnalysis.Run(
                    new(path, copy.S("sheet"), (int)copy.N("header_row", 1)),
                    JsonSerializer.Deserialize<StatisticalRequest>(copy["request"]!.ToJsonString())!
                )
        );
    }

    private static AnalysisReport RunGrowthComparison(JsonObject definition, string path)
    {
        var baseline = JsonSerializer.Deserialize<GrowthCurve>(definition["baseline"]!.ToJsonString())!;
        var candidates = definition["candidates"] is JsonArray list
            ? list.Select(item => JsonSerializer.Deserialize<GrowthCurve>(item!.ToJsonString())!).ToArray()
            : [JsonSerializer.Deserialize<GrowthCurve>(definition["updated"]!.ToJsonString())!];
        return GrowthComparison.Run(baseline, candidates,
            JsonSerializer.Deserialize<GrowthComparisonOptions>(definition["options"]!.ToJsonString())!,
            path.Length == 0 ? null : Dendro.Core.Tabular.TableStore.Read(new(path, definition.S("sheet"), (int)definition.N("header_row", 1))));
    }

    internal static async Task OpenSaved(MainWindow owner, ProjectSession session, JsonObject entry)
    {
        var definition = entry.Obj("definition");
        Show(
            owner,
            session,
            await SavedReport(session, entry),
            definition,
            entry,
            false
        );
        owner.Log(
            "Resultado salvo da análise. Reexecute para recalcular a partir dos dados atuais."
        );
    }
}

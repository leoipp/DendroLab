using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Text.RegularExpressions;

namespace Dendro.Desktop;

/* Edits a detached JSON subtree without dropping unknown project parameters. */
internal sealed class StructuredEditor : ScrollViewer
{
    private readonly JsonNode value;
    private readonly string fieldName;
    private readonly bool growthInputs;
    private readonly Func<Task<(string Path, string Sheet)?>>? spreadsheetPicker;
    private readonly Dictionary<string, TextBox> fields = new();
    private readonly List<Action> commit = new();
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["ajuste_altura"] = "Altura e RNA",
        ["arquivo_entrada"] = "Campos do inventário",
        ["unidades"] = "Unidades do arquivo de entrada",
        ["declinacoes"] = "Declinações vazias",
        ["comprimento_vazio_zero"] = "Considerar declinação do comprimento vazia como zero",
        ["largura_vazio_zero"] = "Considerar declinação da largura vazia como zero",
        ["cadastro"] = "Cadastro florestal",
        ["fallback_idade"] = "Fallback de idade para chaves ausentes",
        ["processador"] = "Processador",
        ["modo"] = "Tipo de processamento",
        ["inventory_type"] = "Tipo de inventário",
        ["factor_scope"] = "Aplicar fatores por",
        ["n_dominantes_parcela"] = "Árvores dominantes por parcela (N)",
        ["calcular_is"] = "Calcular índice de sítio (IS) no processamento",
        ["remover_duplicadas"] = "Remover árvores duplicadas (manter a primeira ocorrência)",
        ["talhao"] = "Talhão",
        ["dap"] = "DAP",
        ["data_medicao"] = "Data de medição",
        ["data_plantio"] = "Data de plantio",
        ["area_efetiva"] = "Área efetiva",
        ["comprimento_parcela"] = "Comprimento da parcela",
        ["largura_parcela"] = "Largura da parcela",
        ["declinacao_comprimento"] = "Declinação no comprimento",
        ["declinacao_largura"] = "Declinação na largura",
        ["colunas"] = "Mapeamento das colunas",
        ["worksheet"] = "Aba da planilha",
        ["coluna_codigo"] = "Coluna do código (vazio = chave do inventário)",
        ["padrao_ano"] = "Padrão do código com grupo nomeado ano",
        ["limite_ano_2000"] = "Último ano curto de 2000 (0–99)",
        ["dia_plantio"] = "Dia presumido de plantio",
        ["mes_plantio"] = "Mês presumido de plantio",
        ["estatisticas"] = "Estatísticas",
        ["comparativo_ifc"] = "Comparação IFC",
        ["saida"] = "Relatório de saída",
        ["features"] = "Variáveis da RNA",
        ["estatisticas_parcela"] = "Estatísticas por parcela",
        ["variaveis_customizadas"] = "Parâmetros avançados",
        ["h_mode"] = "Modo de altura",
        ["modelo_geral"] = "Modelo geral (ZIP)",
        ["caminho"] = "Caminho",
        ["debug"] = "Debug",
        ["nome"] = "Nome",
        ["coluna"] = "Coluna",
        ["funcao"] = "Função",
        ["formula"] = "Fórmula",
        ["filtro"] = "Filtro",
        ["simbolo"] = "Símbolo",
        ["origem"] = "Origem",
        ["unidade"] = "Unidade",
        ["fator"] = "Fator",
        ["agregacao"] = "Agregação",
        ["entradas"] = "Entradas esperadas",
        ["ativo"] = "Ativo",
        ["por_lote"] = "Por lote",
        ["limiares"] = "Limiares (%)",
        ["descricao"] = "Descrições das faixas",
        ["sheets"] = "Abas do relatório",
        ["ajustado"] = "AJUSTADO",
        ["h_ajuste"] = "Ajuste de altura",
        ["resumo_byplot"] = "Resumo por parcela",
        ["resumo_bytalhao"] = "Resumo por talhão",
        ["resumo_bylote"] = "Resumo por lote",
        ["resultado_arvore"] = "Resultado por árvore",
        ["estatisticas_inv"] = "Estatísticas do inventário",
        ["graficos"] = "Gráficos",
        ["modelos_aplicados"] = "Auditoria dos modelos",
        ["informacoes"] = "Informações do processamento",
        ["calculos"] = "Cálculos (debug)",
    };

    internal static string Label(string key) =>
        Labels.TryGetValue(key, out var text)
            ? text
            : char.ToUpper(key[0]) + key[1..].Replace('_', ' ');

    private static JsonNode? NewRecord(string key) =>
        key switch
        {
            "estatisticas_parcela" => new JsonObject
            {
                ["nome"] = "",
                ["coluna"] = "",
                ["funcao"] = "mean",
                ["formula"] = "",
                ["filtro"] = "",
            },
            "variaveis_customizadas" => new JsonObject
            {
                ["nome"] = "",
                ["tipo"] = "formula",
                ["formula"] = "",
                ["col_data1"] = "",
                ["col_data2"] = "",
            },
            "entradas" => new JsonObject
            {
                ["simbolo"] = "x1",
                ["origem"] = "entrada",
                ["coluna"] = "",
                ["unidade"] = "cm",
                ["fator"] = 1,
                ["agregacao"] = "media",
            },
            _ => null,
        };

    public StructuredEditor(
        JsonNode value,
        string fieldName = "",
        bool growthInputs = true,
        Func<Task<(string Path, string Sheet)?>>? spreadsheetPicker = null
    )
    {
        this.value = value;
        this.fieldName = fieldName;
        this.growthInputs = growthInputs;
        this.spreadsheetPicker = spreadsheetPicker;
        var form = Ui.Form();
        if (fieldName == "fallback_idade") Ui.AddHelp(form, "Somente para chaves ausentes no cadastro. Ex.: GNVIVI00518P10-003 → 01/06/2010; P89 → 01/06/1989. Com corte 30: 00–30 usam 2000; 31–99 usam 1900. Não inventa área efetiva nem outros atributos ausentes.");
        if (value is JsonObject obj)
        {
            if (fieldName == "processador")
            {
                if (!obj.ContainsKey("inventory_type")) obj["inventory_type"] = "";
                if (!obj.ContainsKey("factor_scope")) obj["factor_scope"] = "talhao";
                if (!obj.ContainsKey("calcular_is")) obj["calcular_is"] = false;
            }
            if (fieldName == "colunas")
            {
                var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
                foreach (var entry in obj.ToArray())
                {
                    var cell = new StackPanel { Margin = new Thickness(0, 0, 18, 10) };
                    var input = Ui.Input(entry.Value.Display());
                    System.Windows.Automation.AutomationProperties.SetAutomationId(input, "colunas." + entry.Key);
                    Ui.Field(cell, Label(entry.Key), input);
                    grid.Children.Add(cell);
                    string key = entry.Key;
                    commit.Add(() => obj[key] = input.Text);
                }
                form.Children.Add(grid);
            }
            else BuildObject(form, obj);
        }
        else if (value is JsonArray a)
            BuildArray(form, a);
        else
            form.Children.Add(Ui.Text("Valor indisponível."));
        Content = form;
    }

    private void BuildObject(Panel form, JsonObject obj)
    {
        if (fieldName == "variaveis_customizadas")
        {
            BuildCustomVariable(form, obj);
            return;
        }
        if (fieldName == "estatisticas_parcela")
        {
            BuildPlotStatistic(form, obj);
            return;
        }
        foreach (var entry in obj.ToArray())
        {
            string key = entry.Key;
            if(fieldName=="processador"&&key=="colunas_direto")continue;
            var v = entry.Value;
            if (fieldName == "ajuste_altura" && key == "features" && v is JsonArray selectedFeatures)
            {
                BuildFeatureSelector(form, obj, selectedFeatures);
                continue;
            }
            if (fieldName == "ajuste_altura" && key == "modelo_geral" && v is JsonObject generalModel)
            {
                var section = InlineSection(Label(key));
                var editor = new StructuredEditor(generalModel.DeepClone(), "modelo_geral", growthInputs);
                section.Children.Add(editor);
                form.Children.Add(section);
                commit.Add(() => obj[key] = editor.Read());
                continue;
            }
            if (fieldName == "comparativo_ifc" && key == "ativo")
            {
                obj[key] = true;
                continue;
            }
            (string Code, string Title)[]? options = (fieldName, key) switch
            {
                ("ajuste_altura", "h_mode") => [("KEEP_FILE", "Usar alturas do arquivo"), ("MODEL_GENERAL", "Estimar alturas com a RNA geral"), ("FINETUNE_MODEL", "Ajustar a RNA geral aos dados do inventário")],
                ("processador", "modo") => [("completo", "Inventário com revisão e aprovação"), ("resumo_direto", "Resumo direto por nível")],
                ("processador", "inventory_type") => [("", "Selecionar tipo de inventário"), ("IFC", "IFC"), ("IFPC", "IFPC"), ("MISTO", "Misto (IFC e IFPC por linha)")],
                ("processador", "factor_scope") => [("talhao", "Talhão"), ("lote", "Lote")],
                _ => null
            };
            if (options != null)
            {
                var choices = options;
                var combo = Ui.Combo(choices.Select(o => o.Title).ToArray());
                combo.IsEditable = false;
                combo.SelectedIndex = Array.FindIndex(choices, o => o.Code == v.Display());
                System.Windows.Automation.AutomationProperties.SetAutomationId(combo, fieldName + "." + key);
                Ui.Field(form, Label(key), combo);
                if (fieldName == "processador" && key == "modo")
                    Ui.AddHelp(form, "Inventário com revisão e aprovação: guarda o original sem fator e permite corrigir, aprovar e exportar. Resumo direto: gera as tabelas dos níveis selecionados.");
                if (fieldName == "processador" && key == "factor_scope")
                    Ui.AddHelp(form, "Define se cada fator de correção será informado por talhão ou por lote na revisão do inventário.");
                if (fieldName == "processador" && key == "remover_duplicadas")
                    Ui.AddHelp(form, "Quando ativado, mantém a primeira ocorrência para cada chave de árvore definida em Identificação. Quando desativado, as linhas repetidas permanecem e entram nas somas do processamento.");
                commit.Add(() =>
                {
                    if (combo.SelectedIndex < 0) throw new InvalidOperationException($"Selecione uma opção válida em {Label(key)}.");
                    obj[key] = choices[combo.SelectedIndex].Code;
                });
                continue;
            }
            if (v is JsonObject || v is JsonArray)
            {
                if (
                    v is JsonObject
                    && (
                        fieldName == "comparativo_ifc" && key is "limiares" or "descricao"
                        || fieldName == "saida" && key == "sheets"
                        || fieldName == "cadastro" && key is "fallback_idade" or "colunas"
                        || fieldName == "arquivo_entrada" && key is "colunas" or "unidades" or "declinacoes"
                    )
                )
                {
                    var editor = new StructuredEditor(v.DeepClone(), key, growthInputs)
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    };
                    ((Panel)editor.Content).Margin = new Thickness(0);
                    var section = new StackPanel();
                    var heading = Ui.Text(Label(key), 14);
                    heading.FontWeight = FontWeights.SemiBold;
                    section.Children.Add(heading);
                    if (fieldName == "arquivo_entrada" && key == "unidades")
                        Ui.AddHelp(section, "O processamento normaliza os valores para DAP em mm e H em dm antes dos cálculos. A rotina de aplicação/preenchimento de altura permanece igual.");
                    if (fieldName == "arquivo_entrada" && key == "declinacoes") Ui.AddHelp(section, "As opções atuam somente em células vazias ou com espaços. Textos inválidos continuam sendo apresentados como pendências. Comprimento, largura, DAP e altura não são substituídos por zero.");
                    section.Children.Add(editor);
                    form.Children.Add(
                        new Border
                        {
                            Background = System.Windows.Media.Brushes.White,
                            BorderBrush = new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(221, 230, 239)
                            ),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(8),
                            Padding = new Thickness(14),
                            Margin = new Thickness(0, 8, 0, 8),
                            Child = section,
                        }
                    );
                    commit.Add(() => obj[key] = editor.Read());
                    continue;
                }
                var button = Ui.Button(
                    v is JsonArray a
                        ? $"Editar {Label(key)} ({a.Count})…"
                        : $"Editar {Label(key)}…",
                    () =>
                    {
                        var editor = new StructuredEditor(obj[key]!.DeepClone(), key, growthInputs);
                        var window = Ui.Dialog(
                            Label(key),
                            Ui.WithToolbar(editor, Ui.Button("Aplicar", () => { }))
                        );
                        var apply = ((WrapPanel)((DockPanel)window.Content).Children[0])
                            .Children.OfType<Button>()
                            .First();
                        apply.Click += (_, _) =>
                        {
                            try
                            {
                                obj[key] = editor.Read();
                                window.Close();
                            }
                            catch (Exception ex)
                            {
                                Ui.Error(ex);
                            }
                        };
                        window.ShowDialog();
                    }
                );
                form.Children.Add(button);
                continue;
            }
            if (v is JsonValue boolean && boolean.TryGetValue<bool>(out bool b))
            {
                var check = new CheckBox { Content = Label(key), IsChecked = b };
                System.Windows.Automation.AutomationProperties.SetAutomationId(
                    check,
                    fieldName + "." + key
                );
                form.Children.Add(check);
                if (fieldName == "processador" && key == "calcular_is")
                    Ui.AddHelp(form, "Ao processar, usa o CURVA_COD da parcela para localizar a equação vigente no catálogo Índice de sítio. O Y da equação determina se usa DAPd ou Hd; talhão, lote e projeto recebem a média ponderada pela área das parcelas.");
                commit.Add(() => obj[key] = check.IsChecked == true);
                continue;
            }
            if (fieldName == "unidades" && key is "dap" or "altura")
            {
                var choices = key == "dap" ? new[] { "mm", "cm" } : new[] { "dm", "cm", "m" };
                var combo = Ui.Combo(choices);
                combo.SelectedItem = v?.Display();
                System.Windows.Automation.AutomationProperties.SetAutomationId(
                    combo,
                    $"unidades.{key}"
                );
                Ui.Field(form, key == "dap" ? "Unidade de DAP" : "Unidade de H", combo);
                commit.Add(() => obj[key] = combo.SelectedItem?.ToString() ?? choices[0]);
                continue;
            }
            var field = Ui.Input(v.Display());
            System.Windows.Automation.AutomationProperties.SetAutomationId(
                field,
                fieldName + "." + key
            );
            fields[key] = field;
            Ui.Field(form, Label(key), field);
            if (fieldName == "processador" && key == "n_dominantes_parcela")
                Ui.AddHelp(form, "Define N árvores usadas no cálculo em cada parcela. DAPd é a média dos maiores DAP; Hd é a média das alturas dessas mesmas árvores. Talhão, lote e projeto ponderam os resultados das parcelas pela área.");
            if (key == "caminho")
            {
                if (fieldName == "cadastro" && spreadsheetPicker != null)
                {
                    form.Children.Add(
                        Ui.AsyncButton(
                            "Selecionar cadastro, worksheet e cabeçalho…",
                            async () =>
                            {
                                var selected = await spreadsheetPicker();
                                if (selected is not { } file)
                                    return;
                                field.Text = file.Path;
                                if (fields.TryGetValue("worksheet", out var worksheet))
                                    worksheet.Text = file.Sheet;
                            }
                        )
                    );
                }
                else
                    form.Children.Add(
                        Ui.Button(
                            "Selecionar arquivo…",
                            () =>
                            {
                                var path = Ui.PickFile(
                                    "Modelos e dados|*.zip;*.h5;*.keras;*.xlsx;*.xls|Todos|*.*"
                                );
                                if (path != null)
                                    field.Text = path;
                            }
                        )
                    );
            }
            bool integer = v is JsonValue integerValue && integerValue.TryGetValue<int>(out _);
            bool numeric = v is JsonValue numberValue && numberValue.TryGetValue<double>(out _);
            commit.Add(() =>
            {
                if (integer)
                {
                    double parsed = Ui.Number(field.Text);
                    if (parsed != Math.Truncate(parsed) || parsed < int.MinValue || parsed > int.MaxValue)
                        throw new InvalidOperationException($"{Label(key)} deve ser um número inteiro.");
                    obj[key] = JsonValue.Create((int)parsed);
                }
                else
                    obj[key] = numeric
                        ? JsonValue.Create(Ui.Number(field.Text))
                        : JsonValue.Create(field.Text);
            });
        }
    }

    private static StackPanel InlineSection(string title)
    {
        var section = new StackPanel();
        var heading = Ui.Text(title, 14);
        heading.FontWeight = FontWeights.SemiBold;
        section.Children.Add(heading);
        return section;
    }

    private void BuildFeatureSelector(Panel form, JsonObject parent, JsonArray selectedFeatures)
    {
        var section = InlineSection("Variáveis utilizadas pela RNA");
        Ui.AddHelp(section, "Escolha uma variável sugerida ou digite o nome de uma coluna ou variável derivada. A variável precisa existir após o processamento.");
        var features = selectedFeatures.Select(n => n?.Display() ?? "").Where(s => s.Length > 0).ToList();
        var suggestions = features
            .Concat((parent["estatisticas_parcela"] as JsonArray ?? []).OfType<JsonObject>().Select(s => s.S("nome")))
            .Concat((parent["variaveis_customizadas"] as JsonArray ?? []).OfType<JsonObject>().Select(s => s.S("nome")))
            .Concat(["DAP", "H", "Talhão", "Parcela", "Lote", "Ano_Medicao", "Idade_anos"])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var list = new ListBox { ItemsSource = features.ToArray(), MinHeight = 86, MaxHeight = 150 };
        var input = Ui.Combo(suggestions);
        input.IsEditable = true;
        input.IsTextSearchEnabled = true;
        input.ToolTip = "Selecione uma sugestão ou digite o nome de uma variável.";
        var add = Ui.Button("Adicionar variável", () =>
        {
            string name = input.Text.Trim();
            if (name.Length == 0 || features.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
            features.Add(name);
            list.ItemsSource = features.ToArray();
            list.SelectedIndex = features.Count - 1;
            input.Text = "";
        });
        input.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            e.Handled = true;
        };
        var remove = Ui.Button("Remover selecionada", () =>
        {
            if (list.SelectedIndex < 0) return;
            int index = list.SelectedIndex;
            features.RemoveAt(index);
            list.ItemsSource = features.ToArray();
            if (features.Count > 0) list.SelectedIndex = Math.Min(index, features.Count - 1);
        });
        var row = new DockPanel();
        add.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        row.Children.Add(input);
        section.Children.Add(row);
        section.Children.Add(list);
        section.Children.Add(remove);
        form.Children.Add(section);
        commit.Add(() => parent["features"] = new JsonArray(features.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()));
    }

    private void BuildCustomVariable(Panel form, JsonObject obj)
    {
        foreach (string key in new[] { "nome", "tipo", "formula", "col_data1", "col_data2" })
            if (!obj.ContainsKey(key)) obj[key] = key == "tipo" ? "formula" : "";
        var name = Ui.Input(obj.S("nome"));
        Ui.Field(form, "Nome da variável", name);
        var type = Ui.Combo(["Fórmula", "Diferença entre datas (anos)", "Diferença entre datas (meses)", "Diferença entre datas (dias)"]);
        type.SelectedIndex = obj.S("tipo") switch { "data_diff_anos" => 1, "data_diff_meses" => 2, "data_diff_dias" => 3, _ => 0 };
        Ui.Field(form, "Tipo de variável", type);
        var formula = Ui.Input(obj.S("formula"));
        var col1 = Ui.Input(obj.S("col_data1"));
        var col2 = Ui.Input(obj.S("col_data2"));
        var formulaField = Ui.Field(form, "Fórmula", formula);
        var date1Field = Ui.Field(form, "Data inicial · coluna do inventário", col1);
        var date2Field = Ui.Field(form, "Data final · coluna do cadastro", col2);
        Ui.AddHelp(form, "Na opção DataDiff, informe as duas colunas de data. Em Fórmula, use uma expressão aceita pelo processador, como DAP / Média de DAP.");
        void UpdateTypeFields()
        {
            bool isFormula = type.SelectedIndex == 0;
            formulaField.Visibility = isFormula ? Visibility.Visible : Visibility.Collapsed;
            date1Field.Visibility = isFormula ? Visibility.Collapsed : Visibility.Visible;
            date2Field.Visibility = isFormula ? Visibility.Collapsed : Visibility.Visible;
        }
        type.SelectionChanged += (_, _) => UpdateTypeFields();
        UpdateTypeFields();
        commit.Add(() =>
        {
            if (type.SelectedIndex < 0) throw new InvalidOperationException("Selecione o tipo da variável.");
            obj["nome"] = name.Text.Trim();
            obj["tipo"] = type.SelectedIndex switch { 1 => "data_diff_anos", 2 => "data_diff_meses", 3 => "data_diff_dias", _ => "formula" };
            obj["formula"] = type.SelectedIndex == 0 ? formula.Text.Trim() : "";
            obj["col_data1"] = type.SelectedIndex == 0 ? "" : col1.Text.Trim();
            obj["col_data2"] = type.SelectedIndex == 0 ? "" : col2.Text.Trim();
        });
    }

    private void BuildPlotStatistic(Panel form, JsonObject obj)
    {
        foreach (string key in new[] { "nome", "coluna", "funcao", "formula", "filtro" })
            if (!obj.ContainsKey(key)) obj[key] = key == "funcao" ? "mean" : "";
        var name = Ui.Input(obj.S("nome"));
        Ui.Field(form, "Nome da variável", name);
        var calculation = Ui.Combo(["Estatística por parcela", "Fórmula"]);
        calculation.SelectedIndex = obj.S("formula").Length > 0 ? 1 : 0;
        Ui.Field(form, "Cálculo", calculation);
        var column = Ui.Combo(["DAP", "H", "AS", "G", "Volume", "Idade_anos"]);
        column.IsEditable = true;
        column.Text = obj.S("coluna");
        Ui.Field(form, "Coluna de entrada", column);
        var functions = new (string Code, string Label)[]
        {
            ("mean", "Média"), ("sum", "Soma"), ("min", "Mínimo"), ("max", "Máximo"),
            ("count", "Contagem"), ("median", "Mediana"), ("std", "Desvio padrão"),
            ("gini", "Gini"), ("first", "Primeiro valor"), ("last", "Último valor"),
        };
        var function = Ui.Combo(functions.Select(f => f.Label));
        int functionIndex = Array.FindIndex(functions, f => f.Code == obj.S("funcao", "mean"));
        function.SelectedIndex = functionIndex < 0 ? 0 : functionIndex;
        Ui.Field(form, "Função estatística", function);
        var op = Ui.Combo(["Sem filtro", ">", ">=", "<", "<=", "=", "≠"]);
        var threshold = Ui.Input();
        var match = Regex.Match(obj.S("filtro"), @"^\s*(>=|<=|==|!=|>|<)\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)\s*$");
        op.SelectedItem = match.Success ? match.Groups[1].Value switch { "==" => "=", "!=" => "≠", var s => s } : "Sem filtro";
        threshold.Text = match.Success ? match.Groups[2].Value : "";
        var filterControls = new DockPanel();
        op.Width = 110;
        DockPanel.SetDock(op, Dock.Left);
        filterControls.Children.Add(op);
        filterControls.Children.Add(threshold);
        var filterField = Ui.Field(form, "Filtro dos valores de entrada", filterControls);
        var formulaInput = Ui.Input(obj.S("formula"));
        var formulaField = Ui.Field(form, "Fórmula", formulaInput);
        Ui.AddHelp(form, "Use o filtro para incluir somente valores que atendam à condição, por exemplo DAP > 0. Selecione Sem filtro para considerar todos. Fórmulas usam o mesmo avaliador do processador.");
        void UpdateMode()
        {
            bool useFormula = calculation.SelectedIndex == 1;
            column.Visibility = useFormula ? Visibility.Collapsed : Visibility.Visible;
            function.Visibility = useFormula ? Visibility.Collapsed : Visibility.Visible;
            filterField.Visibility = useFormula ? Visibility.Collapsed : Visibility.Visible;
            formulaField.Visibility = useFormula ? Visibility.Visible : Visibility.Collapsed;
        }
        calculation.SelectionChanged += (_, _) => UpdateMode();
        UpdateMode();
        commit.Add(() =>
        {
            bool useFormula = calculation.SelectedIndex == 1;
            obj["nome"] = name.Text.Trim();
            obj["formula"] = useFormula ? formulaInput.Text.Trim() : "";
            obj["coluna"] = useFormula ? "" : column.Text.Trim();
            obj["funcao"] = useFormula ? "mean" : functions[function.SelectedIndex < 0 ? 0 : function.SelectedIndex].Code;
            string selectedOperator = op.SelectedItem?.ToString() ?? "Sem filtro";
            if (!useFormula && selectedOperator != "Sem filtro")
            {
                double limit = Ui.Number(threshold.Text);
                if (!double.IsFinite(limit)) throw new InvalidOperationException("Informe um limite numérico para o filtro.");
                string invariantLimit = limit.ToString("R", CultureInfo.InvariantCulture);
                obj["filtro"] = (selectedOperator switch { "=" => "==", "≠" => "!=", var s => s }) + " " + invariantLimit;
            }
            else obj["filtro"] = "";
        });
    }

    private void BuildArray(Panel form, JsonArray a)
    {
        var list = new ListBox { MinHeight = 180, MaxHeight = 420 };
        void Refresh()
        {
            list.Items.Clear();
            for (int i = 0; i < a.Count; i++)
            {
                var v = a[i];
                list.Items.Add(
                    new ListBoxItem
                    {
                        Content = v is JsonObject o
                            ? string.Join(
                                " · ",
                                o.Take(5).Select(k => Label(k.Key) + ": " + k.Value.Display())
                            )
                            : v.Display(),
                        Tag = i,
                        Padding = new Thickness(8),
                    }
                );
            }
        }
        Refresh();
        form.Children.Add(list);
        void Edit(bool add)
        {
            int index = add ? -1 : list.SelectedIndex;
            if (!add && index < 0)
                return;
            JsonNode? original = index >= 0 ? a[index] : a.FirstOrDefault() ?? NewRecord(fieldName);
            JsonNode template =
                original is JsonObject
                    ? original.DeepClone()
                    : JsonValue.Create(original?.Display() ?? "")!;
            if (template is JsonObject record && NewRecord(fieldName) is JsonObject schema)
            {
                foreach (var entry in schema)
                    if (!record.ContainsKey(entry.Key))
                        record[entry.Key] = entry.Value?.DeepClone();
                if (fieldName == "entradas" && !growthInputs)
                    record.Remove("agregacao");
            }
            if (add && template is JsonObject o)
                foreach (var k in o.ToArray())
                    if (k.Value is JsonValue j && j.TryGetValue<string>(out _))
                        o[k.Key] = "";
            if (template is JsonObject)
            {
                var editor = new StructuredEditor(template, fieldName, growthInputs);
                var apply = Ui.Button("Aplicar", () => { });
                var window = Ui.Dialog(
                    add ? "Adicionar item" : "Editar item",
                    Ui.WithToolbar(editor, apply)
                );
                apply.Click += (_, _) =>
                {
                    try
                    {
                        var edited = editor.Read();
                        if (index < 0)
                            a.Add(edited);
                        else
                            a[index] = edited;
                        Refresh();
                        window.Close();
                    }
                    catch (Exception ex)
                    {
                        Ui.Error(ex);
                    }
                };
                window.ShowDialog();
            }
            else
            {
                var input = Ui.Input(template.Display());
                var apply = Ui.Button("Aplicar", () => { });
                var window = Ui.Dialog("Valor", Ui.WithToolbar(input, apply), 500, 250);
                apply.Click += (_, _) =>
                {
                    JsonNode scalar =
                        template is JsonValue number && number.TryGetValue<double>(out _)
                            ? JsonValue.Create(Ui.Number(input.Text))!
                            : JsonValue.Create(input.Text)!;
                    if (index < 0)
                        a.Add(scalar);
                    else
                        a[index] = scalar;
                    Refresh();
                    window.Close();
                };
                window.ShowDialog();
            }
        }
        form.Children.Add(Ui.Button("Adicionar", () => Edit(true)));
        form.Children.Add(Ui.Button("Editar selecionado", () => Edit(false)));
        form.Children.Add(
            Ui.Button(
                "Remover selecionado",
                () =>
                {
                    if (list.SelectedIndex >= 0)
                    {
                        a.RemoveAt(list.SelectedIndex);
                        Refresh();
                    }
                }
            )
        );
        list.MouseDoubleClick += (_, _) => Edit(false);
    }

    public JsonNode Read()
    {
        foreach (var action in commit)
            action();
        return value.DeepClone();
    }
}

using System.Windows;
using System.Windows.Controls;

namespace Dendro.Desktop;

internal sealed class ProjectView : ScrollViewer
{
    public ProjectView(ProjectSession session)
    {
        var form = Ui.Form();
        form.Children.Add(Ui.Text("Projeto florestal", 28));
        Ui.AddHelp(form, "Organize os dados, cadastre os modelos e execute as ferramentas no mesmo projeto.");
        var name = Ui.Input(session.Meta.S("name"));
        Ui.Field(form, "Nome do projeto", name);
        form.Children.Add(
            Ui.Button(
                "Atualizar nome",
                () =>
                {
                    session.Meta["name"] = name.Text.Trim();
                    session.Touch();
                }
            )
        );
        Ui.AddHelp(form, "Use o botão direito em Planilhas para adicionar dados. As ferramentas ficam no painel à direita; tabelas, modelos e gráficos abrem em abas centrais.");
        form.MaxWidth = 900;
        form.HorizontalAlignment = HorizontalAlignment.Left;
        Content = form;
    }
}

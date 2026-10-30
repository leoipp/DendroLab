using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using Dendro.Core.Updates;

namespace Dendro.Desktop;

public partial class MainWindow
{
    private static readonly HttpClient updateClient=new(){Timeout=TimeSpan.FromSeconds(20)};
    private AppRelease? availableUpdate;
    private bool checkingUpdate;
    internal static Version ApplicationVersion=>Assembly.GetExecutingAssembly().GetName().Version??new Version(1,0,0);

    private async Task CheckUpdates(bool manual)
    {
        if(checkingUpdate)return;checkingUpdate=true;
        try
        {
            var release=await GitHubUpdates.Latest(updateClient);
            if(closing)return;
            availableUpdate=release!=null&&GitHubUpdates.Normalize(release.Version)>GitHubUpdates.Normalize(ApplicationVersion)?release:null;
            UpdateButton.Content=availableUpdate==null?"Verificar atualizações":"Atualizar para "+availableUpdate.Version;
            UpdateButton.ToolTip=availableUpdate==null?$"Versão instalada: {ApplicationVersion.ToString(3)}. Consultar GitHub Releases.":
                $"Nova versão {availableUpdate.Version}. Baixa e verifica o instalador antes de atualizar apenas este usuário. O projeto aberto poderá ser salvo.";
            if(manual)Log(availableUpdate==null?"DendroLab está atualizado ou ainda não há uma versão publicada no GitHub.":"Nova versão disponível: "+availableUpdate.Version+". Clique em Atualizar para instalar.");
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {if(manual)Log("Não foi possível verificar atualizações: "+ex.Message);}
        finally{checkingUpdate=false;}
    }

    private async void UpdatesClick(object sender,RoutedEventArgs e)
    {
        if(busy||closing||checkingUpdate)return;
        if(availableUpdate==null){await CheckUpdates(true);return;}
        var release=availableUpdate;string? installer=null;
        await Execute(async()=>
        {
            string folder=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DENDRO","Updates",release.Tag);
            installer=await GitHubUpdates.Download(updateClient,release,folder,OperationCancellation,StartTableOperation("Baixando atualização do DendroLab…"));
        });
        if(installer==null||busy||closing||!await MayReplace())return;
        try
        {
            SaveWorkspace(WorkspacePath);
            var process=Process.Start(new ProcessStartInfo(installer,"/SILENT /SUPPRESSMSGBOXES /NORESTART /DENDROUPDATE=1"){UseShellExecute=true});
            if(process==null)throw new InvalidOperationException("Não foi possível iniciar o instalador.");
            closing=true;suppressDocumentSelection=true;Close();
        }
        catch(Exception ex){Log("Não foi possível instalar a atualização: "+ex.Message);}
    }
}

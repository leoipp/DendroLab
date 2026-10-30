using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dendro.Core.Tabular;

namespace Dendro.Core.Updates;

public sealed record AppRelease(Version Version,string Tag,string Notes,string InstallerName,Uri Installer,Uri Checksums);

public static class GitHubUpdates
{
    public const string Repository="leoipp/DendroLab";
    public static Version Normalize(Version version)=>new(version.Major,version.Minor,Math.Max(0,version.Build),Math.Max(0,version.Revision));

    public static async Task<AppRelease?> Latest(HttpClient client,CancellationToken cancellation=default)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,$"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("DendroLab-Updater/1.0");request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response=await client.SendAsync(request,cancellation);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();
        var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))!;
        if(json.B("draft")||json.B("prerelease"))return null;
        string tag=json.S("tag_name"),text=tag.StartsWith('v')?tag[1..]:tag;
        if(!Regex.IsMatch(text,@"^\d+\.\d+\.\d+(\.\d+)?$")||!Version.TryParse(text,out var version))
            throw new InvalidDataException("A versão publicada deve usar uma tag como v1.0.1.");
        string name=$"DendroLab-{text}-win-x64-Setup.exe";
        var assets=json["assets"] as JsonArray??[];
        Uri Asset(string filename)
        {
            string url=assets.FirstOrDefault(a=>a.S("name")==filename).S("browser_download_url");
            if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="github.com"
                ||!uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/",StringComparison.Ordinal))
                throw new InvalidDataException("A versão publicada precisa conter o instalador Windows x64 e SHA256SUMS.txt nos arquivos do GitHub Release.");
            return uri;
        }
        return new(version,tag,json.S("body"),name,Asset(name),Asset("SHA256SUMS.txt"));
    }

    public static async Task<string> Download(HttpClient client,AppRelease release,string folder,
        CancellationToken cancellation=default,IProgress<TableProgress>? progress=null)
    {
        if(Path.GetFileName(release.InstallerName)!=release.InstallerName||!release.InstallerName.EndsWith("-win-x64-Setup.exe",StringComparison.Ordinal))
            throw new InvalidDataException("Nome de instalador inválido.");
        foreach(var uri in new[]{release.Installer,release.Checksums})
            if(uri.Scheme!="https"||uri.Host!="github.com"||!uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/",StringComparison.Ordinal))
                throw new InvalidDataException("A atualização precisa vir do repositório oficial do DendroLab.");
        using var sums=await client.GetAsync(release.Checksums,HttpCompletionOption.ResponseHeadersRead,cancellation);
        sums.EnsureSuccessStatusCode();
        using var sumStream=await sums.Content.ReadAsStreamAsync(cancellation);
        using var sumReader=new StreamReader(sumStream);
        char[] textBuffer=new char[65537];int count=0,read;
        while(count<textBuffer.Length&&(read=await sumReader.ReadAsync(textBuffer.AsMemory(count),cancellation))>0)count+=read;
        if(count>65536)throw new InvalidDataException("Arquivo de verificação muito grande.");
        var hashes=new string(textBuffer,0,count).Split('\n').Select(line=>line.Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries))
            .Where(parts=>parts.Length==2&&parts[1].TrimStart('*')==release.InstallerName).Select(parts=>parts[0]).ToArray();
        if(hashes.Length!=1||!Regex.IsMatch(hashes[0],"^[0-9a-fA-F]{64}$"))throw new InvalidDataException("SHA256 do instalador ausente ou ambíguo.");
        Directory.CreateDirectory(folder);string destination=Path.Combine(folder,release.InstallerName),part=destination+"."+Guid.NewGuid().ToString("N")+".part";
        const long limit=512L*1024*1024;var updates=new TableProgressReporter(progress);
        try
        {
            using var response=await client.GetAsync(release.Installer,HttpCompletionOption.ResponseHeadersRead,cancellation);response.EnsureSuccessStatusCode();
            long? length=response.Content.Headers.ContentLength;if(length>limit)throw new InvalidDataException("Instalador acima do tamanho permitido.");
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using(var input=await response.Content.ReadAsStreamAsync(cancellation))
            await using(var output=new FileStream(part,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
            {
                byte[] buffer=new byte[81920];long total=0;
                while((read=await input.ReadAsync(buffer,cancellation))>0)
                {
                    total+=read;if(total>limit)throw new InvalidDataException("Instalador acima do tamanho permitido.");
                    await output.WriteAsync(buffer.AsMemory(0,read),cancellation);hash.AppendData(buffer,0,read);
                    updates.Report(length>0?95d*total/length:null,$"Baixando atualização: {total/1048576d:N1} MB",total);
                }
            }
            string actual=Convert.ToHexString(hash.GetHashAndReset());
            if(!actual.Equals(hashes[0],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("A verificação SHA256 falhou. A atualização não será executada.");
            cancellation.ThrowIfCancellationRequested();File.Move(part,destination,true);updates.Report(100,"Atualização baixada e verificada.",force:true);return destination;
        }
        finally{if(File.Exists(part))File.Delete(part);}
    }
}

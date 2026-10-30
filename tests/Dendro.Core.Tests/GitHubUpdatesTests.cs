using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dendro.Core.Updates;

namespace Dendro.Core.Tests;

public sealed class GitHubUpdatesTests
{
    private const string Name="DendroLab-1.0.1-win-x64-Setup.exe";
    private const string Prefix="https://github.com/leoipp/DendroLab/releases/download/v1.0.1/";
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(respond(request));}
    private static string Release(string prefix=Prefix)=>new JsonObject{
        ["tag_name"]="v1.0.1",["body"]="Melhorias",["assets"]=new JsonArray(
            new JsonObject{["name"]=Name,["browser_download_url"]=prefix+Name},
            new JsonObject{["name"]="SHA256SUMS.txt",["browser_download_url"]=prefix+"SHA256SUMS.txt"})}.ToJsonString();

    [Fact]
    public async Task FindsStableVersionAndHandlesEmptyRepository()
    {
        using var client=new HttpClient(new Handler(r=>new(HttpStatusCode.OK){Content=new StringContent(Release())}));
        var release=await GitHubUpdates.Latest(client);Assert.Equal(new Version(1,0,1),release!.Version);
        Assert.True(GitHubUpdates.Normalize(release.Version)>GitHubUpdates.Normalize(new(1,0,0,0)));
        Assert.Equal(GitHubUpdates.Normalize(new(1,0,0)),GitHubUpdates.Normalize(new(1,0,0,0)));
        using var empty=new HttpClient(new Handler(r=>new(HttpStatusCode.NotFound)));Assert.Null(await GitHubUpdates.Latest(empty));
        using var hostile=new HttpClient(new Handler(r=>new(HttpStatusCode.OK){Content=new StringContent(Release("https://example.org/"))}));
        await Assert.ThrowsAsync<InvalidDataException>(()=>GitHubUpdates.Latest(hostile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadsOnlyVerifiedInstallerAndRemovesIncompleteFiles(bool corrupt)
    {
        byte[] data=Encoding.UTF8.GetBytes("Installer test payload");string hash=Convert.ToHexString(SHA256.HashData(data));
        string folder=Path.Combine(Path.GetTempPath(),"Dendro-update-"+Guid.NewGuid().ToString("N"));
        using var client=new HttpClient(new Handler(r=>new(HttpStatusCode.OK){Content=r.RequestUri!.Host=="api.github.com"?new StringContent(Release()):
            r.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt")?new StringContent((corrupt?new string('0',64):hash)+"  "+Name+"\n"):
            new ByteArrayContent(data)}));
        try
        {
            var release=(await GitHubUpdates.Latest(client))!;
            if(corrupt){await Assert.ThrowsAsync<InvalidDataException>(()=>GitHubUpdates.Download(client,release,folder));Assert.Empty(Directory.GetFiles(folder));}
            else{string result=await GitHubUpdates.Download(client,release,folder);Assert.Equal(data,File.ReadAllBytes(result));Assert.Equal(Name,Path.GetFileName(result));}
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
}

# Distribuir e atualizar o DendroLab

## Instalar na máquina de destino

Use `DendroLab-<versão>-win-x64-Setup.exe`. A instalação é **somente para o
usuário atual**, sem solicitar elevação de administrador. O destino padrão é
`%LOCALAPPDATA%\Programs\DendroLab`. O atalho do menu Iniciar e o registro de
desinstalação também pertencem apenas a esse usuário. O atalho na área de
trabalho é opcional.

O runtime .NET Desktop está incluído; não é necessário instalar o .NET
separadamente. É necessário Windows x64 compatível com .NET 10. Projetos e
bancos devem ser guardados em Documentos ou em outra pasta de dados, fora da
pasta do aplicativo. A desinstalação remove os arquivos instalados e os atalhos;
não inclui instruções para apagar projetos nem preferências pessoais.

Como alternativa, extraia **todo** o ZIP e abra `DendroLab/DendroLab.exe`.
Não execute de dentro do ZIP nem copie apenas o EXE: as DLLs e o runtime que
estão junto dele são necessários.

## Gerar os pacotes

Na máquina de desenvolvimento, instale o SDK .NET 10 e o Inno Setup 6.
O Inno Setup só é necessário para gerar o instalador, não para o usuário final.
O script usa `.build` para os temporários e o cache de pacotes, no mesmo disco
do projeto, e restaura as variáveis de ambiente ao terminar.

```powershell
.\scripts\package.ps1 -Version 1.0.0
# Somente ZIP, sem precisar do Inno Setup:
.\scripts\package.ps1 -Version 1.0.0 -Format Zip
# Compilador Inno Setup fora do PATH:
.\scripts\package.ps1 -Version 1.0.0 -InnoCompiler 'C:\Ferramentas\Inno Setup 6\ISCC.exe'
```

Os arquivos ficam em `dist/<versão>/`:

- `DendroLab-<versão>-win-x64.zip`
- `DendroLab-<versão>-win-x64-Setup.exe`
- `SHA256SUMS.txt`

A publicação inclui o runtime, a licença do DendroLab e os avisos das
dependências. A pasta `dist` não é enviada ao Git; os pacotes são anexos de
uma versão no GitHub Releases. O instalador usa Inno Setup, de Jordan Russell
e Martijn Laan. Assinatura digital do instalador não está configurada nesta
rotina; portanto, o Windows pode identificar o editor como desconhecido.

## Publicar uma atualização no GitHub

1. Escolha uma versão maior que a anterior, por exemplo `1.0.1`.
2. Gere e teste os pacotes com `package.ps1 -Version 1.0.1`.
3. No repositório `leoipp/DendroLab`, crie um **Release** com a tag `v1.0.1`.
4. Anexe o instalador, o ZIP e `SHA256SUMS.txt` gerados para essa mesma versão.
5. Publique como versão estável, sem marcar draft ou pre-release.

O número informado em `-Version` é gravado no executável e precisa
corresponder à tag e aos nomes dos anexos. Um commit ou push de código sozinho
não é uma atualização distribuível. Gere os pacotes novamente sempre que
atualizar o código ou o runtime incluído.

## Atualizar dentro do aplicativo

O DendroLab consulta o último Release estável do repositório oficial alguns
segundos após abrir. Em **Projeto**, o botão **Verificar atualizações** muda
para **Atualizar para <versão>** quando encontra uma versão mais recente.
Também é possível consultar manualmente por esse botão.

Ao clicar em **Atualizar para…**, o aplicativo baixa o instalador, confere o
SHA-256 publicado no Release, oferece salvar o projeto se houver alterações
e inicia a atualização para o usuário atual. O app fecha e o instalador o
reabre ao terminar. O botão Cancelar interrompe o download. Uma falha na
verificação impede a execução do instalador.

O instalador reaproveita o diretório da instalação anterior desse usuário.
Se estiver usando o ZIP e ainda não houver uma instalação, o atualizador
instala a nova versão em `%LOCALAPPDATA%\Programs\DendroLab`; a pasta portátil
anterior permanece intacta.

A consulta usa a API pública do GitHub e não armazena tokens. Repositórios
privados não oferecem atualização anônima por este mecanismo. O SHA-256
detecta download incorreto ou divergente do manifesto publicado; ele não
substitui assinatura digital nem protege contra comprometimento da conta que
publica o Release.

Referências: [Inno Setup: instalação sem administrador](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm),
[publicação self-contained do .NET](https://learn.microsoft.com/en-us/dotnet/core/deploying/).

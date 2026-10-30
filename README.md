# DendroLab

Aplicativo desktop para inventário florestal, processamento dendrométrico,
modelagem, auditoria e análise de dados. Desenvolvido em **C# / .NET 10**, com
interface **WPF** para Windows e painéis encaixáveis.

**Software proprietário — todos os direitos reservados a leoipp.** O acesso
ao código não autoriza seu uso, compilação, modificação, redistribuição ou
exploração comercial. Essas atividades exigem autorização prévia e escrita,
conforme a [licença restritiva](LICENSE). Os comandos abaixo são documentação
técnica para o titular e usuários expressamente autorizados.

## Funcionalidades

- **Inventário IFC/IFPC:** processamento completo com histórico de versões,
  revisão, aplicação de fatores e aprovação; resumo direto por árvore,
  parcela, talhão, lote e projeto.
- **Configurações por projeto:** mapeamento de campos, unidades de importação,
  chaves de identificação, altura, cadastro florestal e seleção das colunas
  opcionais de saída. As colunas obrigatórias permanecem protegidas.
- **Modelagem:** modelos volumétricos VTCC/VCCC, ajuste de altura e RNA,
  regressões 1P, comparação de modelos, curvas de crescimento e produção,
  ICA/IMA e fronteiras de eficiência.
- **Gráficos:** dispersão, estratos com legenda, transparência dos pontos,
  intervalos dos eixos e geração em lote com a origem identificada no título.
- **Limpeza integrada:** vincula vários gráficos da mesma tabela. A seleção
  e a remoção de uma linha se propagam aos demais gráficos; permite desfazer,
  exportar a tabela limpa ou salvá-la como nova tabela no projeto.
- **Auditoria e estatística:** auditoria pós-processamento, Isolation Forest,
  gráfico derivado apenas com observações normais e testes estatísticos.
- **Ativo biológico:** vincula uma tabela aos resultados mais recentes por
  chave e **data da medição**, sinalizando conflitos sem multiplicar linhas.
- **Gestão de tabelas:** junção com identificação da fonte, estratificação,
  criação de colunas, filtros, ordenação e remoção de linhas e colunas.

O processamento ocorre no aplicativo .NET. Não é necessário instalar
Microsoft Excel, Python, Java ou bibliotecas HDF5 nativas para executar o app.

## Requisitos

- Windows x64 compatível com WPF e .NET 10.
- Para executar a publicação tradicional `app`: **.NET Desktop Runtime 10 x64**.
  Os pacotes ZIP e instalador de `dist` já incluem esse runtime.
- Para desenvolver: **SDK .NET 10** e PowerShell.
- Acesso ao NuGet para restaurar as dependências na primeira compilação.

Modelos externos, dados florestais reais, projetos pessoais e resultados de
processamento não fazem parte do repositório. A pasta `tests/fixtures` contém
pequenas entradas e referências utilizadas pelos testes automatizados.

## Compilação e execução

Na raiz do repositório:

```powershell
.\build.ps1
```

O aplicativo compilado fica em `.build/managed-desktop/DendroLab.exe`.
Para gerar a pasta de distribuição local:

```powershell
.\build.ps1 -Publish
# Se a compilação já estiver atualizada:
.\build.ps1 -Publish -NoBuild
```

Execute `app/DendroLab.exe` e distribua a pasta `app` completa somente quando
autorizado. Feche o aplicativo antes de atualizar essa pasta. A publicação
confere os arquivos copiados, preserva a versão anterior em `.build` e inclui
a licença do produto e os avisos das dependências.

## Primeiros passos

Para gerar **ZIP com runtime incluído**, **instalador somente para o usuário**
e publicar versões reconhecidas pelo atualizador do aplicativo, consulte
[Distribuição e atualizações](docs/DISTRIBUICAO.md). Com os requisitos de
compilação instalados, use `./scripts/package.ps1 -Version 1.0.0`.

1. Crie e salve um projeto em **Projeto**.
2. Adicione os dados em **Conteúdos**, escolhendo a tabela/aba e o cabeçalho.
3. Revise os mapeamentos, as unidades, as chaves e os modelos em
   **Projeto > Configurações**.
4. Escolha a operação no painel **Ferramentas**. Resultados e análises ficam
   vinculados às suas origens em **Conteúdos**.

Os painéis podem ser arrastados pelo título para encaixar em outras posições
ou agrupar em abas. O alfinete permite ocultação automática; o menu **Exibir**
recupera painéis fechados. **Ctrl+F1** fixa ou recolhe o menu superior.

### Limpeza integrada

1. Abra **Ferramentas > Limpeza integrada**, ou use o menu de uma tabela.
2. Escolha a tabela e marque pelo menos dois gráficos existentes. Também é
   possível adicionar gráficos escolhendo os eixos X e Y na própria ferramenta.
3. Inicie a sessão, selecione pontos por clique ou contorno e confirme em
   **Remover seleção** ou com **Delete**. A mesma linha sai de todos os gráficos.
4. Use **Desfazer remoção** quando necessário e exporte em XLSX, CSV ou DLB,
   ou salve uma nova tabela limpa no banco do projeto.

A limpeza usa uma cópia da tabela atual e mantém a origem e os gráficos
originais intactos. Não importa exclusões ou ajustes anteriores. A sessão é
temporária: exporte ou salve a tabela limpa antes de fechar a janela.

## Projetos e armazenamento

O arquivo `.dendro` guarda configurações e vínculos. O `.dlb` correspondente é
o banco de dados do projeto, com tabelas, resultados e derivados. Mantenha os
dois arquivos juntos ao transportar um projeto. **Salvar como** copia o banco
para o novo projeto.

O formato DLB usa blocos comprimidos, índice de linhas e verificação de
integridade. As tabelas são visualizadas em páginas de 500 linhas. Junções,
exportações e a cópia da limpeza integrada usam leitura sequencial; os pontos
dos gráficos e alguns cálculos ainda são mantidos em memória. O consumo de
memória cresce com o volume de dados e a quantidade de gráficos abertos.

## Testes

Testes do núcleo:

```powershell
dotnet test tests/Dendro.Core.Tests/Dendro.Core.Tests.csproj -c Release
```

Após compilar, execute uma suíte de interface em uma pasta temporária isolada:

```powershell
$qa = Join-Path $env:TEMP ('DendroLab-QA-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $qa | Out-Null
$p = Start-Process '.build/managed-desktop/DendroLab.exe' -ArgumentList @(
    '--self-test-ui-results', ('"' + $qa + '"')
) -WindowStyle Hidden -PassThru -Wait
if ($p.ExitCode -ne 0) { throw 'Falha nos testes de interface.' }
Get-Content (Join-Path $qa 'results-ui.log')
```

O modo `--self-test-workflow` executa a suíte do fluxo de processamento e gera
`workflow-ui.log`. O modo geral `build.ps1 -Test` também inclui um teste legado
do processamento completo; esse cenário ainda precisa ser atualizado para as
exigências atuais de identificação IFC/IFPC e histórico de versões.

Os testes usam dados pequenos, pastas temporárias e relatórios de referência.
Modelos RNA externos de produção não são necessários para a suíte padrão.

## Organização

| Caminho | Conteúdo |
| --- | --- |
| `src/Dendro.Core` | Cálculos, inventário, modelos, armazenamento e exportações |
| `src/Dendro.Desktop` | Interface WPF, ferramentas e estado do projeto |
| `tests/Dendro.Core.Tests` | Testes automatizados do núcleo |
| `tests/Dendro.Benchmarks` | Medições de desempenho |
| `tests/fixtures` | Entradas e referências portáveis dos testes |
| `scripts` | Compilação, publicação e benchmarks |
| `docs` | Documentação técnica e avisos de terceiros |
| `DENDRO.slnx` | Solução .NET |

`app`, `.build`, `.archive`, `projects` e `outputs` são locais e não são
versionados. Dados de usuários e arquivos de credenciais também são ignorados.

## Documentação técnica

- [Banco DLB](docs/BANCO_DLB.md)
- [Processamento versionado](docs/PROCESSAMENTO_VERSIONADO.md)
- [Auditoria e configuração IFC/IFPC](docs/AUDITORIA_IFPC_CONFIG.md)
- [Estatística](docs/ESTATISTICA.md)
- [Migração WPF](docs/MIGRACAO_WPF.md)
- [Paridade do processamento](docs/PARIDADE.md)
- [Dependências e licenças de terceiros](docs/DEPENDENCIAS.md)

## Licença

Copyright © 2026 **leoipp**. Todos os direitos reservados.

O DendroLab é regido pela [licença proprietária restritiva](LICENSE).
Dependências de terceiros mantêm suas próprias licenças e avisos. Permissões
de visualização e fork concedidas pelos Termos de Serviço do GitHub não
constituem autorização adicional de uso ou exploração do DendroLab.
